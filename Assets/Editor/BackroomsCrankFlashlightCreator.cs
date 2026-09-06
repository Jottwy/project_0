#if UNITY_EDITOR
using BackroomsSurvival.Gameplay;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-133 bloque A — autora la linterna de manivela: su propiedad de batería, su
    /// <c>ItemDefinition</c> y el prefab del wieldable. Se ejecuta desde
    /// "Backrooms ▸ Linterna ▸ Crear linterna de manivela".
    ///
    /// CALCADO de <see cref="BackroomsSprayCanCreator"/>, que es el precedente del proyecto para un
    /// item propio: mismo donante (la antorcha, el único wieldable que se sostiene sin disparar ni
    /// golpear), mismas carpetas fuera del territorio del vendor, mismo prefijo "BR_" funcional
    /// —<c>DataDefinition.Name</c> corta por el PRIMER '_', así que este asset resuelve a
    /// "Crank Flashlight", la cadena exacta que buscarán las tablas de loot— y la misma regla dura:
    /// el <c>_id</c> se acuña una vez, viaja por el wire y vive en los saves, así que lo generado
    /// HAY QUE COMMITEARLO y regenerarlo huérfana los saves que lo referencien.
    ///
    /// TRES DIFERENCIAS CON EL BOTE, y las tres importan:
    ///
    /// 1. EL COMPONENTE WIELDABLE SE SUSTITUYE, no se acompaña. El bote se cuelga del
    ///    <c>WieldableTool</c> del vendor porque le basta el pulsado; la manivela necesita la fase
    ///    `Hold`, que <c>FPSWieldablesInput</c> sólo entrega a quien implemente
    ///    <c>IUseInputHandler</c> — el propio componente wieldable. Así que se quita el
    ///    <c>WieldableTool</c> y se pone <see cref="CrankFlashlightWieldable"/>.
    ///
    /// 2. SE QUITA EL <c>WieldableDurabilityDepleter</c> DEL DONANTE, y esto no es limpieza: es el
    ///    componente que quema la antorcha a 1 punto por segundo. Heredado, vaciaría la linterna en
    ///    un segundo dando igual cuánta cuerda le des. El gasto lo lleva nuestro componente, que es
    ///    quien conoce la capacidad real de ESTA batería.
    ///
    /// 3. SE AÑADE UNA LUZ, porque la antorcha no trae ninguna en su prefab (la suya sale de un
    ///    prefab de fuego anidado, ver <c>TorchShadowCaster</c>) y una linterna sin <c>Light</c> no
    ///    alumbra, no la ven los demás (ADR-042 relaya luces, no items) y no entra en la detección
    ///    de ADR-080. La luz nace bajo <c>Hand.R</c> y el aplicador del modelo la recolocará bajo el
    ///    cuerpo de la linterna cuando el arte esté.
    ///
    /// LA CARGA ES `Durability` y se sortea al nacer entre <see cref="FoundChargeMin"/> y
    /// <see cref="FoundChargeMax"/>: una linterna encontrada trae un resto, no un depósito lleno.
    /// La SALUD de la batería es una propiedad propia, también sorteada al nacer y persistida — es
    /// lo que hace que encontrar otra linterna signifique algo.
    ///
    /// Crear-si-falta y reejecutable: lo que ya existe no se toca.
    /// </summary>
    public static class BackroomsCrankFlashlightCreator
    {
        private const string DefinitionFolder = "Assets/Resources/Definitions/Item";
        private const string PropertyFolder = "Assets/Resources/Definitions/ItemProperty";
        private const string PrefabFolder = "Assets/Prefabs/Wieldables";

        public const string DefinitionPath = DefinitionFolder + "/BR_Crank Flashlight.asset";
        public const string PropertyPath = PropertyFolder + "/BR_Battery Health.asset";
        public const string PrefabPath = PrefabFolder + "/BR_Wieldable_CrankFlashlight.prefab";

        private const string DonorDefinitionPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Wooden Torch.asset";
        private const string DonorPrefabPath =
            "Assets/PolymindGames/STP/Prefabs/Wieldables/STP_Wieldable_WoodenTorch.prefab";

        /// <summary>La cadena exacta que buscarán las pools de loot con <c>GetWithName</c>.</summary>
        private const string ItemName = "Crank Flashlight";

        /// <summary>Nombre de la propiedad, tal y como lo busca el componente en runtime.</summary>
        public const string BatteryHealthName = "Battery Health";

        public const string HandBoneName = "Hand.R";
        public const string BeamNodeName = "BR_FlashlightBeam";

        private const string Description =
            "De manivela. No necesita pilas, necesita brazo.";

        private const string LongDescription =
            "Carcasa de plástico duro y una dinamo dentro que cruje. Treinta segundos de brazo te " +
            "dan un minuto de pasillo, más o menos, según lo vieja que esté.\n\n" +
            "Mientras le das cuerda no corres, y la luz hace lo que le da la gana.";

        private const float Weight = 0.6f;

        /// <summary>Carga con la que nace una linterna encontrada. Un resto, no un depósito.</summary>
        private const float FoundChargeMin = 0.10f;
        private const float FoundChargeMax = 0.50f;

        /// <summary>Salud de batería con la que nace. Se queda escrita en el item para siempre.</summary>
        private const float BatteryHealthMin = 0.80f;
        private const float BatteryHealthMax = 1.00f;

        private const string IconPath = "Assets/Art/Items/BR_CrankFlashlight_Icon.png";

        [MenuItem("Backrooms/Linterna/Crear linterna de manivela", false, 90)]
        public static void CreateIfMissing()
        {
            var donorDef = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DonorDefinitionPath);
            if (donorDef == null)
            {
                Debug.LogError($"[CrankFlashlight] Sin ItemDefinition donante en '{DonorDefinitionPath}'. " +
                               "Nada creado: sin pickup el item resolvería por nombre y luego no aparecería.");
                return;
            }

            var donorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DonorPrefabPath);
            if (donorPrefab == null)
            {
                Debug.LogError($"[CrankFlashlight] Sin prefab donante en '{DonorPrefabPath}'. Nada creado: " +
                               "un item sin wieldable se puede recoger y no se puede usar.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder("Assets/Resources/Definitions");
            BackroomsEditorFolders.EnsureFolder(DefinitionFolder);
            BackroomsEditorFolders.EnsureFolder(PropertyFolder);
            BackroomsEditorFolders.EnsureFolder("Assets/Prefabs");
            BackroomsEditorFolders.EnsureFolder(PrefabFolder);

            var battery = EnsureBatteryHealthProperty();
            if (battery == null) return;

            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            if (definition != null)
            {
                Debug.Log($"[CrankFlashlight] '{DefinitionPath}' ya existe (id={definition.Id}) — su id no se " +
                          "toca (está en el wire y en los saves); bórralo a mano si quieres uno nuevo.");
                AssignIcon(definition);
            }
            else
            {
                definition = CreateDefinition(donorDef, battery);
                if (definition == null) return;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
                Debug.Log($"[CrankFlashlight] '{PrefabPath}' ya existe — intacto.");
            else
                CreateWieldablePrefab(donorPrefab, definition);

            ItemDefinition.ReloadDefinitions_EditorOnly();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.LogWarning("[CrankFlashlight] VERIFICAR A MANO: que la linterna aparece en el inventario, " +
                             "que al equiparla sale BR_Wieldable_CrankFlashlight (y NO la antorcha), que el " +
                             "botón derecho enciende y apaga y que mantener el izquierdo da cuerda (la barra " +
                             "sube a saltos y el jugador anda más lento). El registro de wieldables del " +
                             "jugador puede necesitar dar de alta el prefab nuevo; este script no lo toca.");
        }

        /// <summary>
        /// La propiedad donde vive la salud de la batería. Es propia y no una del vendor porque
        /// ninguna de las suyas significa esto, y porque el restaurador de inventario guarda y
        /// devuelve propiedades de instancia por id sin conocerlas: una propiedad nueva persiste
        /// sin tocar el esquema de guardado.
        /// </summary>
        private static ItemPropertyDefinition EnsureBatteryHealthProperty()
        {
            var existing = AssetDatabase.LoadAssetAtPath<ItemPropertyDefinition>(PropertyPath);
            if (existing != null)
                return existing;

            var property = ScriptableObject.CreateInstance<ItemPropertyDefinition>();
            AssetDatabase.CreateAsset(property, PropertyPath);

            // Punto de entrada del vendor al acuñado de `_id`, igual que con el item: elegirlo a
            // mano se saltaría el bucle de reintento que evita colisiones.
            property.Validate_EditorOnly(
                new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));

            var so = new SerializedObject(property);
            var type = so.FindProperty("_propertyType");
            if (type == null)
            {
                Debug.LogError("[CrankFlashlight] La ItemPropertyDefinition no tiene '_propertyType'. " +
                               "¿Cambió el vendor? Nada más creado.");
                return null;
            }

            type.enumValueIndex = (int)ItemPropertyType.Float;
            var description = so.FindProperty("_description");
            if (description != null)
            {
                description.stringValue =
                    "Cuánto rinde la dinamo de ESTA linterna, 0..1. Se sortea al nacer y no cambia: " +
                    "una linterna vieja lo será siempre.\n  Tipo: Wieldable\n  Rango: 0.8-1";
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(property);

            if (property.Name != BatteryHealthName)
            {
                Debug.LogError($"[CrankFlashlight] '{PropertyPath}' resuelve a Name='{property.Name}', no " +
                               $"'{BatteryHealthName}'. El componente la busca por esa cadena exacta.");
                return null;
            }

            Debug.Log($"[CrankFlashlight] Creada la propiedad '{PropertyPath}' (id={property.Id}).");
            return property;
        }

        private static ItemDefinition CreateDefinition(ItemDefinition donor, ItemPropertyDefinition battery)
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            AssetDatabase.CreateAsset(definition, DefinitionPath);

            definition.Validate_EditorOnly(
                new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));

            var source = new SerializedObject(donor);
            var target = new SerializedObject(definition);

            // Icono y pickup PRESTADOS de la antorcha: andamio declarado, igual que en el bote. La
            // linterna se verá como una antorcha en el suelo hasta que haya arte propio.
            target.CopyFromSerializedProperty(source.FindProperty("_icon"));
            target.CopyFromSerializedProperty(source.FindProperty("_pickup"));

            // La etiqueta decide si el item se puede EMPUÑAR: sin ella entra en la mochila, se ve
            // en el inventario y no se deja equipar, sin un solo error.
            target.CopyFromSerializedProperty(source.FindProperty("_tag"));

            target.FindProperty("_description").stringValue = Description;
            target.FindProperty("_longDescription").stringValue = LongDescription;
            target.FindProperty("_weight").floatValue = Weight;
            // Una por hueco: la carga y la batería son de instancia, apilarlas las promediaría.
            target.FindProperty("_stackSize").intValue = 1;

            WriteProperties(target, battery);

            target.ApplyModifiedPropertiesWithoutUndo();

            if (donor.ParentGroup != null)
                definition.SetParentGroup_EditorOnly(donor.ParentGroup);

            EditorUtility.SetDirty(definition);

            if (definition.Name != ItemName)
            {
                Debug.LogError($"[CrankFlashlight] '{DefinitionPath}' resuelve a Name='{definition.Name}', " +
                               $"no '{ItemName}'. Las pools de loot lo buscarán por esa cadena exacta.");
                return null;
            }

            Debug.Log($"[CrankFlashlight] Creado '{DefinitionPath}' (id={definition.Id}, Name='{definition.Name}').");
            return definition;
        }

        /// <summary>
        /// Las dos propiedades de instancia, escritas a mano en vez de copiadas del donante: la
        /// antorcha nace LLENA (`_valueRange` {1,0} sin aleatorio) y una linterna encontrada no.
        /// El sorteo lo hace el vendor al crear el item, así que no hay que escribir código para
        /// que dos linternas del mismo mundo salgan distintas.
        /// </summary>
        private static void WriteProperties(SerializedObject target, ItemPropertyDefinition battery)
        {
            var props = target.FindProperty("_properties");
            if (props == null)
            {
                Debug.LogError("[CrankFlashlight] El ItemDefinition no tiene '_properties'. ¿Cambió el vendor?");
                return;
            }

            props.arraySize = 2;
            WriteProperty(props.GetArrayElementAtIndex(0), ItemConstants.Durability,
                FoundChargeMin, FoundChargeMax);
            WriteProperty(props.GetArrayElementAtIndex(1), battery.Id,
                BatteryHealthMin, BatteryHealthMax);
        }

        private static void WriteProperty(SerializedProperty element, int propertyId, float min, float max)
        {
            element.FindPropertyRelative("_itemPropertyId").intValue = propertyId;
            element.FindPropertyRelative("_useRandomValue").boolValue = true;
            element.FindPropertyRelative("_valueRange").vector2Value = new Vector2(min, max);
        }

        private static void CreateWieldablePrefab(GameObject donorPrefab, ItemDefinition definition)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(donorPrefab);
            if (instance == null)
            {
                Debug.LogError("[CrankFlashlight] No se pudo instanciar el prefab donante.");
                return;
            }

            try
            {
                // Romper el vínculo con el prefab del vendor: si no, esto sería una VARIANTE y un
                // reimport del .unitypackage arrastraría sus cambios hasta aquí dentro.
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);

                instance.name = "BR_Wieldable_CrankFlashlight";

                StripFireFrom(instance);
                SwapWieldableComponent(instance);
                var beam = EnsureBeam(instance);
                PointComponentAtBeam(instance, beam);

                int repointed = RepointReferencedItem(instance, definition.Id);
                if (repointed == 0)
                {
                    Debug.LogError("[CrankFlashlight] No se encontró ningún '_referencedItem' en el prefab " +
                                   "clonado. El wieldable seguiría ligado a la ANTORCHA: equipar la linterna " +
                                   "sacaría una antorcha. Revísalo antes de usarlo.");
                }

                PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
                Debug.Log($"[CrankFlashlight] Creado '{PrefabPath}' (referencedItem reapuntado en " +
                          $"{repointed} componente(s) al id {definition.Id}).");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Fuera el <c>WieldableTool</c> (no entrega el mantenido) y fuera el
        /// <c>WieldableDurabilityDepleter</c> (quema la carga a 1/s, que es la antorcha, no la
        /// linterna). Nuestro componente se añade ANTES de quitarlos: <c>WieldableItem</c> y los
        /// animadores buscan un <c>IWieldable</c> en el mismo objeto, y dejarlo un instante sin
        /// ninguno es pedirle a Unity que se queje.
        /// </summary>
        private static void SwapWieldableComponent(GameObject root)
        {
            if (root.GetComponent<CrankFlashlightWieldable>() == null)
                root.AddComponent<CrankFlashlightWieldable>();

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string type = component.GetType().Name;
                if (type != "WieldableTool" && type != "WieldableDurabilityDepleter") continue;

                Object.DestroyImmediate(component, true);
            }
        }

        /// <summary>
        /// El haz. Cuelga del hueso de la mano derecha porque el modelo aún no está: cuando el
        /// aplicador ponga el cuerpo de la linterna, lo reparentará ahí y la luz apuntará a donde
        /// apunta el objeto. Spot y no point: una linterna hace un cono, y el cono es lo que
        /// distingue esto de la antorcha para el jugador y para ADR-080.
        ///
        /// CREAR-SI-FALTA y no crear-siempre, porque el aplicador la llama también: al rehacer el
        /// modelo destruye el nodo entero, y el haz vive DENTRO de él desde la primera pasada. Sin
        /// esta puerta, una segunda pasada del aplicador dejaba la linterna sin luz — y sin error,
        /// porque un `Light` que no existe no se queja.
        /// </summary>
        internal static Light EnsureBeam(GameObject root)
        {
            var hand = FindDeep(root.transform, HandBoneName) ?? root.transform;

            var existing = FindDeep(root.transform, BeamNodeName);
            if (existing != null)
                return existing.GetComponent<Light>();

            var node = new GameObject(BeamNodeName);
            node.transform.SetParent(hand, false);
            node.transform.localPosition = Vector3.zero;
            node.transform.localRotation = Quaternion.identity;

            var light = node.AddComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = 55f;
            light.innerSpotAngle = 22f;
            light.range = 18f;
            light.intensity = 4f;
            // Blanco de LED barato, con una pizca de frío. Ni la llama de la antorcha ni el blanco
            // clínico de los plafones del mundo: la linterna tiene que leerse como TUYA.
            light.color = new Color(0.93f, 0.95f, 1f);
            light.shadows = LightShadows.None; // TorchShadowCaster (ADR-065) decide quién las tiene.

            return light;
        }

        private static void PointComponentAtBeam(GameObject root, Light beam)
        {
            var flashlight = root.GetComponent<CrankFlashlightWieldable>();
            if (flashlight == null || beam == null) return;

            var so = new SerializedObject(flashlight);
            var field = so.FindProperty("beam");
            if (field == null) return;

            field.objectReferenceValue = beam;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Apaga lo que la linterna hereda de la antorcha por ser un clon suyo: llama, brasas,
        /// chispas y cualquier <c>Light</c> previa. Una linterna ardiendo no es "arte prestado",
        /// es otro objeto — y la llama entraría en el criterio de <c>TorchShadowCaster</c>
        /// robándole el shadow map al haz. Se desactivan los nodos en vez de borrarlos: reversible.
        /// </summary>
        private static void StripFireFrom(GameObject root)
        {
            string[] hints = { "fire", "flame", "ember", "spark", "smoke", "candle", "torch" };

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root.transform) continue;
                string n = t.name.ToLowerInvariant();
                foreach (var hint in hints)
                {
                    if (!n.Contains(hint)) continue;
                    if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                    break;
                }
            }

            foreach (var light in root.GetComponentsInChildren<Light>(true))
            {
                if (light.enabled) light.enabled = false;
            }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }
            return null;
        }

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

        [MenuItem("Backrooms/Linterna/Asignar icono de la linterna", false, 96)]
        public static void AssignIconMenu()
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            if (definition == null)
            {
                Debug.LogError($"[CrankFlashlight] No hay definición en '{DefinitionPath}'.");
                return;
            }
            if (AssignIcon(definition))
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// Asigna el icono propio si el PNG está, importándolo antes como Sprite en FullRect — un
        /// <c>Texture2D</c> sin ese tipo no se puede asignar al <c>_icon</c>, y con el mesh en
        /// `Tight` Unity recorta el borde transparente y el icono vuelve a salir estirado. Las dos
        /// trampas son literales del bote.
        /// </summary>
        private static bool AssignIcon(ItemDefinition definition)
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(
                    System.IO.Directory.GetCurrentDirectory(), IconPath)))
            {
                Debug.LogWarning($"[CrankFlashlight] Sin icono propio en '{IconPath}' — se queda con el " +
                                 "prestado de la antorcha.");
                return false;
            }

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

                    importer.ReadTextureSettings(settings);
                    settings.spriteMeshType = SpriteMeshType.FullRect;
                    importer.SetTextureSettings(settings);

                    importer.SaveAndReimport();
                }
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(IconPath);
            if (sprite == null)
            {
                Debug.LogError($"[CrankFlashlight] '{IconPath}' existe pero no da un Sprite.");
                return false;
            }

            var so = new SerializedObject(definition);
            var icon = so.FindProperty("_icon");
            if (icon == null || icon.objectReferenceValue == sprite) return false;

            icon.objectReferenceValue = sprite;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            Debug.Log($"[CrankFlashlight] Icono asignado desde '{IconPath}'.");
            return true;
        }
    }
}
#endif
