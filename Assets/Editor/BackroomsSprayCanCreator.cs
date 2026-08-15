#if UNITY_EDITOR
using BackroomsSurvival.Gameplay;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-068 S3 — autora el bote de spray: su <c>ItemDefinition</c> y el prefab del wieldable
    /// que lo pone en la mano. Se ejecuta desde "Backrooms ▸ Spray ▸ Crear bote de spray".
    ///
    /// FUERA DE TERRITORIO DEL VENDOR, igual que <see cref="BackroomsItemCreator"/>. La
    /// definición cae en "Assets/Resources/Definitions/Item" y el prefab en "Assets/Prefabs":
    /// `DataDefinition.LoadDefinitionsFromResources` escanea la ruta literal
    /// "Definitions/{Tipo}" y `Resources.LoadAll` MEZCLA todas las carpetas Resources del
    /// proyecto, así que se recogen junto a las del vendor sin editar un solo fichero suyo — y
    /// sin que un reimport del `.unitypackage` se los lleve.
    ///
    /// EL PREFIJO "BR_" ES FUNCIONAL, no decoración: `DataDefinition.Name` es
    /// `name.RemovePrefix()`, que corta por el PRIMER '_'. El asset `BR_Spray Can` resuelve al
    /// nombre "Spray Can", que es la cadena exacta que buscarán las tablas de loot de S4 con
    /// `ItemDefinition.GetWithName`.
    ///
    /// CREAR-SI-FALTA, y hay que COMMITEAR lo generado: `DataDefinition` acuña `_id` con
    /// `Random.Range(int.MinValue, int.MaxValue)`, y ese id es el `def_id` que viaja por el wire
    /// y el que guarda `stp_inventory` en el save. Regenerarlo huérfana todos los saves que lo
    /// referencien.
    ///
    /// EL ENLACE PREFAB↔ITEM, que es lo único no obvio: el prefab del wieldable lleva un
    /// componente del vendor con un campo `_referencedItem`, cuyo `_value` es el `_id` de la
    /// definición. Se reapunta por <see cref="SerializedProperty"/> y no por tipo para no
    /// depender del nombre de una clase del vendor que quizá ni esté en nuestro asmdef.
    ///
    /// ARTE PRESTADO, DECLARADO: icono, pickup y modelo salen de la antorcha. El bote se verá
    /// como una antorcha en la mano y en el suelo hasta que haya arte propio. Es feo, no roto.
    /// </summary>
    public static class BackroomsSprayCanCreator
    {
        private const string DefinitionFolder = "Assets/Resources/Definitions/Item";
        private const string PrefabFolder = "Assets/Prefabs/Wieldables";

        private const string DefinitionPath = DefinitionFolder + "/BR_Spray Can.asset";
        private const string PrefabPath = PrefabFolder + "/BR_Wieldable_SprayCan.prefab";

        // La antorcha es el donante correcto y no una elección cualquiera: es un wieldable que se
        // saca a la mano, se sostiene y no dispara ni golpea, que es exactamente el
        // comportamiento base que necesita un bote.
        private const string DonorDefinitionPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Wooden Torch.asset";
        private const string DonorPrefabPath =
            "Assets/PolymindGames/STP/Prefabs/Wieldables/STP_Wieldable_WoodenTorch.prefab";

        // El NOMBRE no se toca: las pools de loot de ChunkLootRoll y StpChestSpawner lo buscan
        // por esta cadena exacta con `GetWithName`. La marca vive en la descripción, no aquí.
        private const string ItemName = "Spray Can";

        private const string Description =
            "Motor-Red, esmalte industrial. Todavía suena al agitarlo.";

        private const string LongDescription =
            "Doce onzas de secado rápido y alta cobertura, según la lata. Sirve para dejar dicho " +
            "por dónde has pasado: una flecha, un número, una raya en la esquina que mañana te " +
            "diga que ya estuviste aquí.\n\n" +
            "Sirve igual de bien para tachar lo que dejó dicho otro, y eso no lo pone en la lata.";

        private const float Weight = 0.4f;

        /// <summary>
        /// Icono propio. Si el PNG existe se importa como Sprite y se asigna; si no, el bote se
        /// queda con el icono prestado de la antorcha y se avisa. Se separa del resto para que
        /// meter el arte más adelante sea un paso y no un rehacer.
        /// </summary>
        private const string IconPath = "Assets/Art/Items/BR_SprayCan_Icon.png";

        [MenuItem("Backrooms/Spray/Crear bote de spray")]
        public static void CreateIfMissing()
        {
            var donorDef = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DonorDefinitionPath);
            if (donorDef == null)
            {
                Debug.LogError($"[SprayCanCreator] Sin ItemDefinition donante en '{DonorDefinitionPath}'. " +
                               "No se crea nada: sin pickup el item resolvería por nombre y luego no " +
                               "aparecería nunca.");
                return;
            }
            if (donorDef.Pickup == null)
            {
                Debug.LogError($"[SprayCanCreator] El donante '{donorDef.Name}' no tiene _pickup. Nada creado.");
                return;
            }

            var donorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DonorPrefabPath);
            if (donorPrefab == null)
            {
                Debug.LogError($"[SprayCanCreator] Sin prefab donante en '{DonorPrefabPath}'. Nada creado: " +
                               "un item sin wieldable se puede recoger y no se puede usar.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder("Assets/Resources/Definitions");
            BackroomsEditorFolders.EnsureFolder(DefinitionFolder);
            BackroomsEditorFolders.EnsureFolder("Assets/Prefabs");
            BackroomsEditorFolders.EnsureFolder(PrefabFolder);

            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            if (definition != null)
            {
                Debug.Log($"[SprayCanCreator] '{DefinitionPath}' ya existe (id={definition.Id}) — su id no se " +
                          "toca (está en el wire y en los saves); bórralo a mano si quieres uno nuevo.");
                RepairWieldableTag(definition);
                RepairDurabilityProperty(definition);
                RepairText(definition);
                AssignIcon(definition);
            }
            else
            {
                definition = CreateDefinition(donorDef);
                if (definition == null) return;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            {
                Debug.Log($"[SprayCanCreator] '{PrefabPath}' ya existe — intacto.");
            }
            else
            {
                CreateWieldablePrefab(donorPrefab, definition);
            }

            ItemDefinition.ReloadDefinitions_EditorOnly();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.LogWarning("[SprayCanCreator] VERIFICAR A MANO antes de dar esto por bueno: que el bote " +
                             "aparece en el inventario, que al equiparlo sale el prefab BR_Wieldable_SprayCan " +
                             "(y NO la antorcha), y que apuntar a una pared con el botón izquierdo pinta. " +
                             "El registro de wieldables del jugador puede necesitar que el prefab nuevo se " +
                             "dé de alta explícitamente; este script no lo toca.");
        }

        private static ItemDefinition CreateDefinition(ItemDefinition donor)
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            AssetDatabase.CreateAsset(definition, DefinitionPath);

            // Punto de entrada del propio vendor al acuñado de `_id`. Elegir un id a mano se
            // saltaría el bucle de reintento que existe para evitar colisiones.
            definition.Validate_EditorOnly(
                new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));

            var source = new SerializedObject(donor);
            var target = new SerializedObject(definition);
            target.CopyFromSerializedProperty(source.FindProperty("_icon"));

            // ANDAMIO, no destino: este `_pickup` prestado es el de la ANTORCHA, y es lo que hacía
            // que una lata tirada al suelo fuera una antorcha para todos los clientes y para el
            // loot. Se copia solo para que el item exista y se pueda soltar desde el primer
            // momento; quien lo deja bien es "Backrooms/Spray/Crear el bote del suelo"
            // (`BackroomsSprayPickupCreator`), que hay que ejecutar detrás de éste.
            target.CopyFromSerializedProperty(source.FindProperty("_pickup"));

            // LA ETIQUETA NO ES DECORACIÓN: es la que decide si el item se puede EMPUÑAR.
            // `WieldableInventory` resuelve su pistolera con
            // `FindContainer(ItemContainerFilters.WithTag(ItemConstants.WieldableTag))` y
            // `EquipAction` compara la etiqueta del item contra esa misma. Sin ella el bote entra
            // en la mochila, se ve en el inventario y NO se deja equipar — sin un solo error.
            target.CopyFromSerializedProperty(source.FindProperty("_tag"));

            // Y las PROPIEDADES del donante, que traen `Durability`. Ahí es donde vive la carga
            // de CADA bote: el prefab del wieldable es único y compartido, así que guardar los
            // metros restantes en su componente haría que todos los botes del mundo compartieran
            // depósito. Además sale gratis el bloqueo de uso al vaciarse (`WieldableItem`) y la
            // barra de estado del wieldable equipado, la misma que la antorcha.
            target.CopyFromSerializedProperty(source.FindProperty("_properties"));

            target.FindProperty("_description").stringValue = Description;
            target.FindProperty("_longDescription").stringValue = LongDescription;
            target.FindProperty("_weight").floatValue = Weight;
            // Un bote por hueco: la carga vive en el componente SprayCan de la instancia, así que
            // apilarlos mezclaría botes medio gastados en una sola cifra.
            target.FindProperty("_stackSize").intValue = 1;
            target.ApplyModifiedPropertiesWithoutUndo();

            // Por la API del vendor y NO copiando "_parentGroup" en crudo: SetParentGroup_EditorOnly
            // llama además a AddMember_EditorOnly, o sea que registra el item EN la categoría.
            // Copiar la propiedad deja solo la mitad item→categoría y la lista que enumera el
            // generador se queda sin él. Mismo razonamiento que BackroomsItemCreator.
            if (donor.ParentGroup != null)
                definition.SetParentGroup_EditorOnly(donor.ParentGroup);

            EditorUtility.SetDirty(definition);

            if (definition.Name != ItemName)
            {
                Debug.LogError($"[SprayCanCreator] '{DefinitionPath}' resuelve a Name='{definition.Name}', " +
                               $"no '{ItemName}'. Las tablas de loot de S4 lo buscarán por esa cadena exacta.");
                return null;
            }

            Debug.Log($"[SprayCanCreator] Creado '{DefinitionPath}' (id={definition.Id}, Name='{definition.Name}').");
            return definition;
        }

        /// <summary>
        /// Apaga lo que el bote heredó de la antorcha por ser un clon suyo: la llama, las brasas,
        /// las chispas y CUALQUIER `Light`. Un bote de spray ardiendo no es "arte prestado", es un
        /// objeto equivocado — y además la luz entraría en el criterio de `TorchShadowCaster`
        /// (ADR-065), que promueve a sombras la luz más potente del wieldable ACTIVO: el bote se
        /// habría llevado el único shadow map del juego.
        ///
        /// Se DESACTIVAN los nodos en vez de borrarlos: es reversible, y borrar dentro de una
        /// jerarquía que vino de un prefab del vendor es más frágil que apagarla.
        /// </summary>
        [MenuItem("Backrooms/Spray/Apagar el fuego del bote", false, 97)]
        public static void StripFire()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[SprayCanCreator] No hay prefab en '{PrefabPath}'.");
                return;
            }

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                int off = StripFireFrom(root);
                if (off > 0)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                    AssetDatabase.SaveAssets();
                }
                Debug.Log($"[SprayCanCreator] {off} nodo(s)/luz(ces) de fuego apagados en el bote.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static readonly string[] FireNodeHints =
        {
            "fire", "flame", "ember", "spark", "smoke", "candle", "torch",
        };

        private static int StripFireFrom(GameObject root)
        {
            int touched = 0;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root.transform) continue;
                string n = t.name.ToLowerInvariant();
                foreach (var hint in FireNodeHints)
                {
                    if (!n.Contains(hint)) continue;
                    if (t.gameObject.activeSelf)
                    {
                        t.gameObject.SetActive(false);
                        touched++;
                    }
                    break;
                }
            }

            // Y las luces por tipo, no por nombre: `FireLight` cuelga de un prefab ANIDADO
            // (`FPS_VFX_SmallFire`) y su nodo podría no llevar ninguna de las pistas de arriba.
            foreach (var light in root.GetComponentsInChildren<Light>(true))
            {
                if (!light.enabled) continue;
                light.enabled = false;
                touched++;
            }

            return touched;
        }

        /// <summary>
        /// Repara la etiqueta de un asset YA existente. Existe porque la primera versión de este
        /// creador no copiaba `_tag`, y el resultado era un bote que aparecía en el inventario y
        /// no se dejaba equipar, sin ningún error. Solo toca la etiqueta: el `_id` no se roza,
        /// así que reparar es seguro para saves y wire.
        /// </summary>
        private static void RepairWieldableTag(ItemDefinition definition)
        {
            var donor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DonorDefinitionPath);
            if (donor == null) return;

            var target = new SerializedObject(definition);
            var tag = target.FindProperty("_tag");
            var value = tag?.FindPropertyRelative("_value");
            if (value == null) return;

            var donorValue = new SerializedObject(donor).FindProperty("_tag")?.FindPropertyRelative("_value");
            if (donorValue == null || donorValue.intValue == 0) return;
            if (value.intValue == donorValue.intValue) return;

            value.intValue = donorValue.intValue;
            target.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            Debug.LogWarning($"[SprayCanCreator] Reparada la etiqueta de '{DefinitionPath}' " +
                             $"({value.intValue}). Sin ella el bote se ve en el inventario y no se " +
                             "deja equipar.");
        }

        /// <summary>
        /// Pone al día el texto de un asset ya existente. Barato y sin riesgo: la descripción no
        /// la referencia nadie por valor, a diferencia del `_id` o del nombre.
        /// </summary>
        private static void RepairText(ItemDefinition definition)
        {
            var target = new SerializedObject(definition);
            var desc = target.FindProperty("_description");
            var longDesc = target.FindProperty("_longDescription");
            if (desc == null) return;
            if (desc.stringValue == Description &&
                (longDesc == null || longDesc.stringValue == LongDescription)) return;

            desc.stringValue = Description;
            if (longDesc != null) longDesc.stringValue = LongDescription;
            target.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            Debug.Log("[SprayCanCreator] Descripción del bote actualizada.");
        }

        /// <summary>
        /// Asigna el icono propio si el PNG está en <see cref="IconPath"/>, importándolo antes
        /// como Sprite — un `Texture2D` sin ese tipo de importación NO se puede asignar al
        /// `_icon`, que es un `Sprite`, y el fallo sería un icono que sigue siendo el de la
        /// antorcha sin que nada lo explique.
        ///
        /// Si el PNG no está, se avisa y se deja el prestado: es arte pendiente, no un error.
        /// </summary>
        [MenuItem("Backrooms/Spray/Asignar icono del bote", false, 96)]
        public static void AssignIconMenu()
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            if (definition == null)
            {
                Debug.LogError($"[SprayCanCreator] No hay definición en '{DefinitionPath}'.");
                return;
            }
            if (AssignIcon(definition))
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        private static bool AssignIcon(ItemDefinition definition)
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(
                    System.IO.Directory.GetCurrentDirectory(), IconPath)))
            {
                Debug.LogWarning($"[SprayCanCreator] Sin icono propio en '{IconPath}' — el bote " +
                                 "se queda con el prestado de la antorcha. Deja ahí el PNG y " +
                                 "ejecuta 'Backrooms/Spray/Asignar icono del bote'.");
                return false;
            }

            // El importador PRIMERO: sin Sprite no hay nada que asignar.
            var importer = AssetImporter.GetAtPath(IconPath) as TextureImporter;
            if (importer != null)
            {
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);

                bool dirty = importer.textureType != TextureImporterType.Sprite
                             || importer.spriteImportMode != SpriteImportMode.Single
                             || !importer.alphaIsTransparency
                             || settings.spriteMeshType != SpriteMeshType.FullRect;

                if (dirty)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.alphaIsTransparency = true;
                    importer.mipmapEnabled = false;

                    // FULL RECT, y esto NO es un detalle: con el mesh en `Tight` Unity RECORTA el
                    // borde transparente del sprite, así que el relleno que vuelve cuadrado al
                    // PNG desaparece y el hueco cuadrado del inventario vuelve a estirar la lata.
                    // El icono se ve gordo y chato exactamente igual que sin rellenar.
                    importer.ReadTextureSettings(settings);
                    settings.spriteMeshType = SpriteMeshType.FullRect;
                    importer.SetTextureSettings(settings);

                    importer.SaveAndReimport();
                }
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(IconPath);
            if (sprite == null)
            {
                Debug.LogError($"[SprayCanCreator] '{IconPath}' existe pero no da un Sprite. " +
                               "¿Se importó como algo que no es textura?");
                return false;
            }

            var target = new SerializedObject(definition);
            var icon = target.FindProperty("_icon");
            if (icon == null) return false;
            if (icon.objectReferenceValue == sprite) return false;

            icon.objectReferenceValue = sprite;
            target.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            Debug.Log($"[SprayCanCreator] Icono asignado desde '{IconPath}'.");
            return true;
        }

        /// <summary>
        /// Da a un asset YA existente la propiedad `Durability`, que es donde vive la carga de
        /// cada bote. Sin ella el bote se pinta sin gastarse, no se bloquea al vaciarse y no
        /// aparece su barra. Solo toca `_properties`: el `_id` no se roza.
        /// </summary>
        private static void RepairDurabilityProperty(ItemDefinition definition)
        {
            var donor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DonorDefinitionPath);
            if (donor == null) return;

            var target = new SerializedObject(definition);
            var props = target.FindProperty("_properties");
            if (props == null || props.arraySize > 0) return;

            var donorProps = new SerializedObject(donor).FindProperty("_properties");
            if (donorProps == null || donorProps.arraySize == 0) return;

            target.CopyFromSerializedProperty(donorProps);
            target.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            Debug.LogWarning($"[SprayCanCreator] Reparadas las propiedades de '{DefinitionPath}' " +
                             "(Durability). Sin ella el bote pinta sin gastarse y no tiene barra.");
        }

        private static void CreateWieldablePrefab(GameObject donorPrefab, ItemDefinition definition)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(donorPrefab);
            if (instance == null)
            {
                Debug.LogError("[SprayCanCreator] No se pudo instanciar el prefab donante.");
                return;
            }

            try
            {
                // Romper el vínculo con el prefab del vendor: si no, esto sería una VARIANTE y un
                // reimport del .unitypackage arrastraría sus cambios hasta aquí dentro.
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);

                instance.name = "BR_Wieldable_SprayCan";
                if (instance.GetComponentInChildren<SprayCan>(true) == null)
                    instance.AddComponent<SprayCan>();

                StripFireFrom(instance);

                int repointed = RepointReferencedItem(instance, definition.Id);
                if (repointed == 0)
                {
                    Debug.LogError("[SprayCanCreator] No se encontró ningún campo '_referencedItem' en el " +
                                   "prefab clonado. El wieldable seguiría ligado a la ANTORCHA: equipar el " +
                                   "bote sacaría una antorcha. Revísalo antes de usarlo.");
                }

                PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
                Debug.Log($"[SprayCanCreator] Creado '{PrefabPath}' (referencedItem reapuntado en " +
                          $"{repointed} componente(s) al id {definition.Id}).");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Reapunta todos los campos <c>_referencedItem</c> del prefab al id de NUESTRA
        /// definición. Por <see cref="SerializedProperty"/> y no por tipo, para no depender del
        /// nombre de una clase del vendor que puede no estar visible desde este asmdef.
        ///
        /// Devuelve cuántos campos se reapuntaron. Cero significa que el prefab sigue ligado al
        /// item del donante, que es un fallo silencioso caro: el item existe, se equipa y saca
        /// otra cosa.
        /// </summary>
        private static int RepointReferencedItem(GameObject root, int newItemId)
        {
            int count = 0;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                var so = new SerializedObject(component);
                var prop = so.FindProperty("_referencedItem");
                if (prop == null) continue;

                // El campo es un envoltorio con un `_value` entero (el `_id` de la definición).
                var value = prop.FindPropertyRelative("_value") ?? prop;
                if (value.propertyType != SerializedPropertyType.Integer) continue;

                value.intValue = newItemId;
                so.ApplyModifiedPropertiesWithoutUndo();
                count++;
            }
            return count;
        }
    }
}
#endif
