#if UNITY_EDITOR
using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.ResourceHarvesting;
using PolymindGames.WieldableSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-114 — los siete assets del desmontaje, en UNA pasada de editor, crear-si-falta:
    /// el destornillador (`ItemDefinition` + wieldable), las dos tablas (`BR_Wooden Plank`,
    /// `BR_Metal Beam`), tres `HarvestableResourceDefinition` (escritorio, estantería, silla) y
    /// tres prefabs de mueble con `HarvestableResource` y el collider EN LA RAÍZ.
    /// Se ejecuta desde "Backrooms ▸ Create Dismantle Assets".
    ///
    /// FUERA DE TERRITORIO DEL VENDOR, como el resto de creadores: las definiciones caen en
    /// "Assets/Resources/Definitions/Item" (la ruta literal que escanea
    /// `DataDefinition.LoadDefinitionsFromResources`) y los muebles en
    /// "Assets/Resources/Props/Dismantle", que es exactamente lo que `StpWorldPropSpawner`
    /// pide con `Resources.Load("Props/Dismantle/&lt;Desk|Shelf|Chair&gt;")`.
    ///
    /// EL PREFIJO "BR_" ES FUNCIONAL: `DataDefinition.Name` corta por el primer '_', así que
    /// `BR_Wooden Plank` resuelve a "Wooden Plank", la cadena exacta de
    /// `ChunkDismantleRoll.WoodenPlank` que el sembrador busca con `GetWithName`.
    ///
    /// CREAR-SI-FALTA, y COMMITEAR lo generado: `DataDefinition` acuña `_id` al azar, y ese id es
    /// el `def_id` que viaja por el wire (`stp_drop`) y el que guarda `stp_inventory` en el save.
    /// Regenerarlo huérfana saves y desincroniza la partida.
    ///
    /// LA PUERTA DE HERRAMIENTA (ADR-114 D6) queda escrita en dos números que tienen que casar:
    /// el destornillador lleva un `ResourceHarvestProfile{Plant, HarvestPower}` y cada mueble
    /// exige `_requiredPower` ≤ ese valor. El hacha y el pico no llevan perfil `Plant` — medido
    /// antes de aprobar el ADR — y por eso no pueden desmontar. El servidor nunca sabe qué
    /// herramienta se usó: sólo defiende el ritmo (0,25 por golpe) y el alcance.
    ///
    /// ARTE PRESTADO, DECLARADO: el destornillador se ve como el hacha (modelo, icono y pickup
    /// donados por referencia) hasta que haya arte propio; las tablas se ven como chatarra en el
    /// suelo. Feo, no roto: `StpItemReplicator` decide la identidad por `def_id`, no por prefab.
    /// </summary>
    public static class BackroomsDismantleAssetsCreator
    {
        private const string ItemFolder = "Assets/Resources/Definitions/Item";
        private const string WieldableFolder = "Assets/Prefabs/Wieldables";

        /// <summary>`StpWorldPropSpawner.PropResourcePath` = "Props/Dismantle/" bajo Resources.</summary>
        public const string PropFolder = "Assets/Resources/Props/Dismantle";
        public const string DefinitionFolder = PropFolder + "/Definitions";

        public const string ScrewdriverDefinitionPath = ItemFolder + "/BR_Screwdriver.asset";
        public const string ScrewdriverWieldablePath = WieldableFolder + "/BR_Wieldable_Screwdriver.prefab";
        public const string ScrewdriverWieldableNode = "BR_Wieldable_Screwdriver";
        public const string ScrewdriverName = "Screwdriver";

        /// <summary>Los dos números de la puerta de herramienta (D6). El hacha lleva 0,2 en `Tree`;
        /// esto no compite con él porque el TIPO ya separa: un perfil que no existe no abre nada.</summary>
        public const float ScrewdriverHarvestPower = 0.5f;
        public const float FurnitureRequiredPower = 0.5f;

        /// <summary>Un cuarto por golpe: cuatro golpes justos, que es el mínimo que el servidor
        /// impone con `MAX_HARVEST_FRACTION_PER_HIT` (D8). Más aquí no serviría de nada.</summary>
        public const float ScrewdriverYieldPerHit = 0.25f;

        // Donantes. Chatarra para los materiales (sin acciones, con pickup simple y apilado); el
        // hacha para la herramienta, que es el wieldable de golpear-para-cosechar del vendor.
        private const string MaterialDonorPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Metal Shard.asset";
        private const string ToolDonorDefinitionPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Hunting Axe.asset";
        private const string ToolDonorPrefabPath =
            "Assets/PolymindGames/STP/Prefabs/Wieldables/STP_Wieldable_HuntingAxe.prefab";

        /// <summary>Los dos packs, ya convertidos a URP (docs/reference/asset-packs.md) y con el
        /// collider en la raíz de cada prefab. Se anidan como HIJO visual: el collider que golpea
        /// el destornillador es el de NUESTRA raíz, que es donde vive `HarvestableResource`.</summary>
        private const string OfficePack = "Assets/AK Studio Art/Business Office/Prefabs/Office";
        private const string GroceryPack = "Assets/GroceryStorePropsCollection/Prefabs/URP";

        private readonly struct Material_
        {
            public readonly string Name;
            public readonly string Description;
            public readonly float Weight;

            public Material_(string name, string description, float weight)
            {
                Name = name;
                Description = description;
                Weight = weight;
            }
        }

        // Nombres = `ChunkDismantleRoll.{WoodenPlank, MetalBeam}`, carácter a carácter.
        // TODO(balance): pesos de primera pasada. Sin stacks (FARMING-ROADMAP D1): el tope es el
        // peso, y el sembrador suelta cada unidad como un drop propio por la misma razón.
        private static readonly Material_[] Materials =
        {
            new("Wooden Plank", "Tabla de aglomerado con cantos de chapa. Huele a oficina vieja.", 1.5f),
            new("Metal Beam", "Perfil de acero de una estantería. Pesa lo que promete.", 3.0f),
        };

        public readonly struct Prop
        {
            /// <summary>Nombre del prefab bajo Resources, = `DismantleProp` del sembrador.</summary>
            public readonly string Name;
            /// <summary>Ruta del prefab del pack que hace de visual.</summary>
            public readonly string PackPrefab;
            public readonly string DisplayName;

            public Prop(string name, string packPrefab, string displayName)
            {
                Name = name;
                PackPrefab = packPrefab;
                DisplayName = displayName;
            }
        }

        // La estantería NO es `Shelf 1` del pack de oficina: mide 4,98 × 3,46 m, es pieza de pared
        // de dos tiles y atraviesa el techo de 2,80 m de servicio y almacén, que es justo donde el
        // sorteo la pone. La de almacén del pack de supermercado es una estantería exenta.
        public static readonly Prop[] Props =
        {
            new("Desk", OfficePack + "/Desk 1.prefab", "Escritorio"),
            new("Shelf", GroceryPack + "/SM_WarehouseShelfSingle.prefab", "Estantería"),
            new("Chair", OfficePack + "/Chair 1.prefab", "Silla"),
        };

        public static string PrefabPathFor(string prop) => $"{PropFolder}/{prop}.prefab";
        public static string DefinitionPathFor(string prop) => $"{DefinitionFolder}/BR_{prop}.asset";

        /// <summary>Margen de los `_harvestBounds` sobre la caja del mueble. `CanHarvestAt` ya
        /// tolera 0,5 m; esto es para que el borde de un tablero no quede fuera por redondeo.</summary>
        private const float HarvestBoundsMargin = 0.25f;

        [MenuItem("Backrooms/Create Dismantle Assets")]
        public static void CreateIfMissing()
        {
            var materialDonor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(MaterialDonorPath);
            var toolDonor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(ToolDonorDefinitionPath);
            var toolDonorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ToolDonorPrefabPath);
            if (materialDonor == null || materialDonor.Pickup == null)
            {
                Debug.LogError($"[DismantleAssets] Sin donante de materiales usable en '{MaterialDonorPath}'. Nada creado.");
                return;
            }
            if (toolDonor == null || toolDonor.Pickup == null || toolDonorPrefab == null)
            {
                Debug.LogError("[DismantleAssets] Sin donante de herramienta (definición del hacha + prefab). Nada creado.");
                return;
            }

            var packPrefabs = new Dictionary<string, GameObject>();
            foreach (var prop in Props)
            {
                string path = prop.PackPrefab;
                var pack = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (pack == null)
                {
                    Debug.LogError($"[DismantleAssets] Falta el prefab del pack '{path}'. Nada creado.");
                    return;
                }
                packPrefabs[prop.Name] = pack;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder("Assets/Resources/Definitions");
            BackroomsEditorFolders.EnsureFolder(ItemFolder);
            BackroomsEditorFolders.EnsureFolder("Assets/Prefabs");
            BackroomsEditorFolders.EnsureFolder(WieldableFolder);
            BackroomsEditorFolders.EnsureFolder("Assets/Resources/Props");
            BackroomsEditorFolders.EnsureFolder(PropFolder);
            BackroomsEditorFolders.EnsureFolder(DefinitionFolder);

            int created = 0, kept = 0;

            // B) Materiales.
            foreach (var material in Materials)
            {
                string path = $"{ItemFolder}/BR_{material.Name}.asset";
                if (KeepExistingItem(path))
                {
                    kept++;
                    continue;
                }
                CreateItem(path, material.Name, material.Description, material.Weight, materialDonor,
                    copyTag: false);
                created++;
            }

            // A) Herramienta: definición + wieldable + alta en el jugador.
            ItemDefinition screwdriver;
            if (KeepExistingItem(ScrewdriverDefinitionPath))
            {
                screwdriver = AssetDatabase.LoadAssetAtPath<ItemDefinition>(ScrewdriverDefinitionPath);
                kept++;
            }
            else
            {
                screwdriver = CreateItem(ScrewdriverDefinitionPath, ScrewdriverName,
                    "Destornillador de electricista. Lo único que afloja lo que sujeta este sitio.",
                    0.5f, toolDonor, copyTag: true);
                created++;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(ScrewdriverWieldablePath) != null)
            {
                kept++;
            }
            else if (screwdriver != null)
            {
                CreateScrewdriverWieldable(toolDonorPrefab, screwdriver);
                created++;
            }

            // Sin esto el item existe, se recoge, y equiparlo no saca nada: el jugador monta su
            // diccionario de wieldables a partir de los hijos ya instanciados en su prefab.
            SprayCanWieldableRegistrar.RegisterWieldablePrefab(ScrewdriverWieldablePath, ScrewdriverWieldableNode);

            // C) + D) Definiciones y prefabs de mueble. La definición primero (el prefab la
            // referencia) y su `_prefab` al final (referencia el prefab guardado).
            foreach (var prop in Props)
            {
                string defPath = DefinitionPathFor(prop.Name);
                var definition = AssetDatabase.LoadAssetAtPath<HarvestableResourceDefinition>(defPath);
                if (definition != null)
                {
                    kept++;
                }
                else
                {
                    definition = CreateDefinition(defPath, prop, packPrefabs[prop.Name]);
                    created++;
                }

                string prefabPath = PrefabPathFor(prop.Name);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
                {
                    kept++;
                }
                else
                {
                    CreatePropPrefab(prefabPath, prop, packPrefabs[prop.Name], definition);
                    created++;
                }

                LinkDefinitionToPrefab(definition, prefabPath);
            }

            ItemDefinition.ReloadDefinitions_EditorOnly();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Verificación por el ARTEFACTO, no por el "OK": lo que el sembrador va a preguntar.
            foreach (string name in new[] { "Wooden Plank", "Metal Beam", "Cloth", "Leather", ScrewdriverName })
            {
                var def = ItemDefinition.GetWithName(name);
                if (def == null)
                    Debug.LogError($"[DismantleAssets] '{name}' NO resuelve por nombre: el sembrador lo saltará.");
                else
                    Debug.Log($"[DismantleAssets] '{name}' → id {def.Id}.");
            }
            foreach (var prop in Props)
            {
                var go = Resources.Load<GameObject>("Props/Dismantle/" + prop.Name);
                if (go == null || go.GetComponent<HarvestableResource>() == null || go.GetComponent<Collider>() == null)
                    Debug.LogError($"[DismantleAssets] 'Props/Dismantle/{prop.Name}' no carga con HarvestableResource + collider en la raíz.");
                else
                    Debug.Log($"[DismantleAssets] 'Props/Dismantle/{prop.Name}' OK.");
            }

            Debug.Log($"[DismantleAssets] {created} creados, {kept} ya existían.");
        }

        private static bool KeepExistingItem(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
            if (existing == null)
                return false;
            if (existing.Pickup == null)
            {
                Debug.LogError($"[DismantleAssets] '{path}' existe SIN _pickup: resto de una ejecución interrumpida. " +
                               "Resuelve por nombre y luego no aparece nunca. Bórralo a mano y vuelve a ejecutar.");
            }
            else
            {
                Debug.Log($"[DismantleAssets] '{path}' ya existe (id={existing.Id}) — intacto: el id está en el wire y en los saves.");
            }
            return true;
        }

        private static ItemDefinition CreateItem(string path, string expectedName, string description,
            float weight, ItemDefinition donor, bool copyTag)
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            AssetDatabase.CreateAsset(definition, path);

            // El punto de entrada del vendor al acuñado de `_id`, con su bucle anti-colisión.
            definition.Validate_EditorOnly(
                new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));

            var source = new SerializedObject(donor);
            var target = new SerializedObject(definition);
            target.CopyFromSerializedProperty(source.FindProperty("_icon"));
            target.CopyFromSerializedProperty(source.FindProperty("_pickup"));
            target.CopyFromSerializedProperty(source.FindProperty("_stackPickup"));
            // La etiqueta es la que decide si un item se puede EMPUÑAR (misma lección que el bote de
            // spray). Sólo la herramienta la lleva; una tabla con etiqueta de wieldable sería
            // equipable y sacaría… nada.
            if (copyTag)
                target.CopyFromSerializedProperty(source.FindProperty("_tag"));
            // Y NO se copian `_properties` (durabilidad: fuera de Alpha 1, ADR-114 D10) ni `_data`
            // (el hacha lleva `CraftingData`: el destornillador no se craftea, ADR-064 sigue en propuesta).

            target.FindProperty("_description").stringValue = description;
            target.FindProperty("_weight").floatValue = weight;
            target.FindProperty("_stackSize").intValue = 1; // sin stacks en items propios (FARMING D1)
            target.ApplyModifiedPropertiesWithoutUndo();

            // Por la API del vendor: registra el item EN la categoría, no sólo la referencia.
            if (donor.ParentGroup != null)
                definition.SetParentGroup_EditorOnly(donor.ParentGroup);

            EditorUtility.SetDirty(definition);

            if (definition.Name != expectedName)
            {
                Debug.LogError($"[DismantleAssets] '{path}' resuelve a Name='{definition.Name}', no '{expectedName}'. " +
                               "El sembrador lo busca por esa cadena exacta.");
            }

            Debug.Log($"[DismantleAssets] Creado '{path}' (id={definition.Id}, Name='{definition.Name}').");
            return definition;
        }

        private static void CreateScrewdriverWieldable(GameObject donorPrefab, ItemDefinition definition)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(donorPrefab);
            if (instance == null)
            {
                Debug.LogError("[DismantleAssets] No se pudo instanciar el prefab del hacha.");
                return;
            }

            try
            {
                // Desvinculado del vendor: si no, sería una VARIANTE y un reimport arrastraría cambios.
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
                instance.name = ScrewdriverWieldableNode;

                int repointed = RepointReferencedItem(instance, definition.Id);
                if (repointed == 0)
                {
                    Debug.LogError("[DismantleAssets] Ningún '_referencedItem' en el clon: equipar el destornillador " +
                                   "sacaría el HACHA. Revísalo antes de usarlo.");
                }

                int profiled = 0;
                foreach (var attack in instance.GetComponentsInChildren<MeleeHarvestAttack>(true))
                {
                    var so = new SerializedObject(attack);
                    var profiles = so.FindProperty("_resourceHarvestProfiles");
                    if (profiles == null)
                        continue;
                    profiles.arraySize = 1;
                    var p = profiles.GetArrayElementAtIndex(0);
                    p.FindPropertyRelative("ResourceType").intValue = (int)HarvestableResourceType.Plant;
                    p.FindPropertyRelative("HarvestPower").floatValue = ScrewdriverHarvestPower;
                    p.FindPropertyRelative("YieldPerHit").floatValue = ScrewdriverYieldPerHit;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    profiled++;
                }
                if (profiled == 0)
                {
                    Debug.LogError("[DismantleAssets] El clon del hacha no tiene MeleeHarvestAttack: el destornillador " +
                                   "no podría desmontar nada.");
                }

                PrefabUtility.SaveAsPrefabAsset(instance, ScrewdriverWieldablePath);
                Debug.Log($"[DismantleAssets] Creado '{ScrewdriverWieldablePath}' (referencedItem→{definition.Id} en " +
                          $"{repointed} componente(s); perfil Plant/{ScrewdriverHarvestPower} en {profiled}).");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>Misma técnica que el bote de spray: por SerializedProperty, sin depender del nombre
        /// de la clase del vendor que lleve el campo.</summary>
        private static int RepointReferencedItem(GameObject root, int newItemId)
        {
            int count = 0;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                var so = new SerializedObject(component);
                var prop = so.FindProperty("_referencedItem");
                if (prop == null) continue;
                var value = prop.FindPropertyRelative("_value") ?? prop;
                if (value.propertyType != SerializedPropertyType.Integer) continue;
                value.intValue = newItemId;
                so.ApplyModifiedPropertiesWithoutUndo();
                count++;
            }
            return count;
        }

        /// <summary>Caja local del visual: la unión de sus renderers con el pack en el origen.</summary>
        private static Bounds VisualBounds(GameObject packPrefab)
        {
            var renderers = packPrefab.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(Vector3.up * 0.5f, Vector3.one);
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            // Los bounds de un prefab-asset se calculan con su transform en la identidad, que es
            // exactamente la pose con la que se anida bajo la raíz.
            return b;
        }

        private static HarvestableResourceDefinition CreateDefinition(string path, Prop prop, GameObject packPrefab)
        {
            var definition = ScriptableObject.CreateInstance<HarvestableResourceDefinition>();
            AssetDatabase.CreateAsset(definition, path);

            var bounds = VisualBounds(packPrefab);
            bounds.Expand(HarvestBoundsMargin * 2f);

            var so = new SerializedObject(definition);
            so.FindProperty("_harvestableName").stringValue = prop.DisplayName;
            // ADR-114 D6: el único tipo libre del enum cerrado del vendor.
            so.FindProperty("_resourceType").intValue = (int)HarvestableResourceType.Plant;
            so.FindProperty("_requiredPower").floatValue = FurnitureRequiredPower;
            // D5: el reloj de regeneración es del BACKEND (15 min). Con 0 el vendor no resucita
            // nada por ciclo de día en el cliente, que sería un mueble entero para uno y vacío
            // para los demás.
            so.FindProperty("_respawnDays").intValue = 0;
            so.FindProperty("_harvestBounds").boundsValue = bounds;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);

            Debug.Log($"[DismantleAssets] Creada definición '{path}' (Plant, power {FurnitureRequiredPower}, bounds {bounds.size:F2}).");
            return definition;
        }

        private static void CreatePropPrefab(string path, Prop prop, GameObject packPrefab,
            HarvestableResourceDefinition definition)
        {
            var root = new GameObject(prop.Name);
            try
            {
                // La misma capa que el harvestable del vendor (`STP_Harvestable_Rock`): entra en
                // `SimpleSolidObjectsMask`, que es lo que barre el golpe del destornillador.
                root.layer = LayerConstants.DynamicObject;

                var visual = (GameObject)PrefabUtility.InstantiatePrefab(packPrefab, root.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one;

                // Los colliders del pack se APAGAN (no se borran: es una instancia anidada y borrar
                // dentro de ella es más frágil que sobrescribir un bool). Un golpe que cayera en un
                // collider hijo no encontraría HarvestableResource, porque STP resuelve el componente
                // desde el GameObject del collider, nunca desde el padre.
                foreach (var c in visual.GetComponentsInChildren<Collider>(true))
                    c.enabled = false;

                var bounds = VisualBounds(packPrefab);
                var box = root.AddComponent<BoxCollider>();
                box.center = bounds.center;
                box.size = bounds.size;

                var hr = root.AddComponent<HarvestableResource>();
                var so = new SerializedObject(hr);
                so.FindProperty("_resourceDefinition").objectReferenceValue = definition;
                so.FindProperty("_disableHitboxOnKilled").boolValue = true;
                so.FindProperty("_unharvestedObject").objectReferenceValue = visual;
                // El mismo objeto para el estado PARCIAL, como hace la roca del vendor: si fuera
                // nulo, el primer golpe apagaría el visual y el mueble desaparecería con 0,75 de
                // vida. Entero o nada (ADR-114 D10) significa que se ve hasta el último golpe.
                so.FindProperty("_partiallyHarvestedObject").objectReferenceValue = visual;
                so.FindProperty("_fullyHarvestedObject").objectReferenceValue = null;
                var events = so.FindProperty("_events");
                if (events != null) events.arraySize = 0;
                so.ApplyModifiedPropertiesWithoutUndo();

                // Sin `SaveableObject` a propósito: el estado lo guarda el backend (`stp_harvestables`,
                // ADR-114 D2), y un saveable instanciado en runtime pide GUID en la SaveableDatabase.

                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[DismantleAssets] Creado '{path}' (visual '{prop.PackPrefab}', caja {bounds.size:F2}).");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void LinkDefinitionToPrefab(HarvestableResourceDefinition definition, string prefabPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var hr = prefab != null ? prefab.GetComponent<HarvestableResource>() : null;
            if (hr == null)
            {
                Debug.LogError($"[DismantleAssets] '{prefabPath}' no tiene HarvestableResource en la raíz; la definición queda sin _prefab.");
                return;
            }
            var so = new SerializedObject(definition);
            var p = so.FindProperty("_prefab");
            if (p.objectReferenceValue == hr)
                return;
            p.objectReferenceValue = hr;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
        }
    }
}
#endif
