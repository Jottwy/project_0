#if UNITY_EDITOR
using BackroomsSurvival.Gameplay.Medical;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Autora la venda: su <c>ItemDefinition</c> y el prefab del wieldable con el que se aplica.
    /// Se ejecuta desde "Backrooms ▸ Venda ▸ Crear venda".
    ///
    /// CALCADO de <see cref="BackroomsCrankFlashlightCreator"/>, que es el precedente del proyecto
    /// para un item propio: carpetas fuera del territorio del vendor, prefijo "BR_" funcional
    /// —<c>DataDefinition.Name</c> corta por el PRIMER '_', así que este asset resuelve a
    /// "Bandage"— y la misma regla dura: el <c>_id</c> se acuña UNA vez, viaja por el wire y vive
    /// en los saves, así que lo generado SE COMMITEA y no se regenera.
    ///
    /// DOS DONANTES, Y AHÍ SE APARTA DEL PRECEDENTE. La linterna copió icono, pickup y etiqueta del
    /// mismo item; aquí el icono y el pickup salen de la TELA (que ya parece un trapo enrollado, o
    /// sea el mejor andamio disponible) y la etiqueta de un wieldable de verdad, porque es la
    /// etiqueta la que decide si un item se puede EMPUÑAR: con la de la tela la venda entraría en
    /// la mochila, se vería en el inventario y no se dejaría equipar, sin un solo error.
    ///
    /// LA VENDA SE APILA y la linterna no, y las dos cosas son correctas: la linterna lleva carga y
    /// salud de batería, que son propiedades de INSTANCIA y apilarlas las promediaría; una venda no
    /// tiene estado, así que cinco vendas son cinco vendas.
    ///
    /// ARTE PLACEHOLDER, DECLARADO. El wieldable sale de la brújula —el tool más limpio del vendor,
    /// sin fuego, sin luz y sin disparo— con su modelo APAGADO y un rollo de gasa construido por
    /// <see cref="BandageVisual"/> en la mano derecha. Se ve un rollo blanco, no una brújula. Arte
    /// de verdad es una pasada posterior: se deja un prefab en <c>Resources/BR_Bandage_Arm</c> y
    /// tanto esto como los dos hooks lo usan sin tocar una línea.
    /// </summary>
    public static class BackroomsBandageCreator
    {
        private const string DefinitionFolder = "Assets/Resources/Definitions/Item";
        private const string PrefabFolder = "Assets/Prefabs/Wieldables";

        public const string DefinitionPath = DefinitionFolder + "/BR_Bandage.asset";
        public const string PrefabPath = PrefabFolder + "/BR_Wieldable_Bandage.prefab";

        /// <summary>Donante de icono y prefab de suelo: un trapo es lo más parecido a una venda.</summary>
        private const string ArtDonorPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Cloth.asset";

        /// <summary>Donante de la etiqueta «esto se empuña» y del prefab del wieldable.</summary>
        private const string WieldableDonorDefinitionPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Compass.asset";

        private const string WieldableDonorPrefabPath =
            "Assets/PolymindGames/STP/Prefabs/Wieldables/STP_Wieldable_Compass.prefab";

        /// <summary>La cadena exacta que buscarán las pools de loot con <c>GetWithName</c>.</summary>
        private const string ItemName = "Bandage";

        public const string NodeName = "BR_Wieldable_Bandage";

        /// <summary>Hueso del que cuelga el rollo de gasa en la mano.</summary>
        private const string HandBoneName = "Hand.R";

        /// <summary>Mallas que SÍ se quedan encendidas en el prefab donante: los brazos.</summary>
        private static readonly string[] KeptRenderers = { "LeftArm", "RightArm" };

        private const string Description = "Gasa. Para el brazo que sangra, no para el que duele.";

        private const string LongDescription =
            "Una tira de tela limpia, o lo bastante limpia. Cierra un corte y aguanta hasta el " +
            "siguiente golpe.\n\n" +
            "Ponerla lleva unos segundos en los que no vas a ninguna parte.";

        private const float Weight = 0.1f;
        private const int StackSize = 5;

        [MenuItem("Backrooms/Venda/Crear venda", false, 90)]
        public static void CreateIfMissing()
        {
            var artDonor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(ArtDonorPath);
            var wieldableDonor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(WieldableDonorDefinitionPath);
            if (artDonor == null || wieldableDonor == null)
            {
                Debug.LogError($"[Venda] Falta un donante ('{ArtDonorPath}' o " +
                               $"'{WieldableDonorDefinitionPath}'). Nada creado: sin etiqueta de empuñable " +
                               "la venda entraría en la mochila y no se dejaría equipar, sin error.");
                return;
            }

            var donorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(WieldableDonorPrefabPath);
            if (donorPrefab == null)
            {
                Debug.LogError($"[Venda] Sin prefab donante en '{WieldableDonorPrefabPath}'. Nada creado: " +
                               "un item sin wieldable se puede recoger y no se puede usar.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder("Assets/Resources/Definitions");
            BackroomsEditorFolders.EnsureFolder(DefinitionFolder);
            BackroomsEditorFolders.EnsureFolder("Assets/Prefabs");
            BackroomsEditorFolders.EnsureFolder(PrefabFolder);

            EnsureClothMaterial();

            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            if (definition != null)
            {
                Debug.Log($"[Venda] '{DefinitionPath}' ya existe (id={definition.Id}) — su id no se toca " +
                          "(está en el wire y en los saves); bórralo a mano si quieres uno nuevo.");
            }
            else
            {
                definition = CreateDefinition(artDonor, wieldableDonor);
                if (definition == null) return;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
                Debug.Log($"[Venda] '{PrefabPath}' ya existe — intacto.");
            else
                CreateWieldablePrefab(donorPrefab, definition);

            ItemDefinition.ReloadDefinitions_EditorOnly();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.LogWarning("[Venda] SIGUIENTE PASO OBLIGATORIO: 'Backrooms ▸ Venda ▸ Registrar venda en " +
                             "el jugador'. Sin esa alta, equipar la venda no saca nada en la mano y no hay " +
                             "ningún error que lo diga (WieldableInventory sólo cachea los wieldables ya " +
                             "colgados del jugador).");
        }

        /// <summary>
        /// Crea el material de la gasa COMO ASSET, en Resources, si no está. Es un paso obligatorio
        /// y no una comodidad: un material creado en memoria se pierde al guardar el prefab que lo
        /// referencia, así que sin esto el rollo de la mano saldría rosa de "material perdido" y
        /// parecería un fallo del render y no de este script.
        /// </summary>
        private static void EnsureClothMaterial()
        {
            string path = "Assets/Resources/" + BandageVisual.MaterialResourcePath + ".mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
                return;

            var material = BandageVisual.Build();
            if (material == null)
            {
                Debug.LogError("[Venda] Sin shader de URP: no se pudo crear el material de la gasa.");
                return;
            }

            AssetDatabase.CreateAsset(material, path);
            Debug.Log($"[Venda] Creado '{path}'.");
        }

        private static ItemDefinition CreateDefinition(ItemDefinition artDonor, ItemDefinition wieldableDonor)
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            AssetDatabase.CreateAsset(definition, DefinitionPath);

            definition.Validate_EditorOnly(
                new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));

            var art = new SerializedObject(artDonor);
            var wieldable = new SerializedObject(wieldableDonor);
            var target = new SerializedObject(definition);

            // Icono y pickup de la TELA: andamio declarado, se ve como un trapo en el suelo.
            target.CopyFromSerializedProperty(art.FindProperty("_icon"));
            target.CopyFromSerializedProperty(art.FindProperty("_pickup"));

            // La etiqueta, del wieldable: es la que decide si el item se puede EMPUÑAR.
            target.CopyFromSerializedProperty(wieldable.FindProperty("_tag"));

            target.FindProperty("_description").stringValue = Description;
            target.FindProperty("_longDescription").stringValue = LongDescription;
            target.FindProperty("_weight").floatValue = Weight;
            target.FindProperty("_stackSize").intValue = StackSize;

            target.ApplyModifiedPropertiesWithoutUndo();

            if (wieldableDonor.ParentGroup != null)
                definition.SetParentGroup_EditorOnly(wieldableDonor.ParentGroup);

            EditorUtility.SetDirty(definition);

            if (definition.Name != ItemName)
            {
                Debug.LogError($"[Venda] '{DefinitionPath}' resuelve a Name='{definition.Name}', no " +
                               $"'{ItemName}'. Las pools de loot lo buscarán por esa cadena exacta.");
                return null;
            }

            Debug.Log($"[Venda] Creado '{DefinitionPath}' (id={definition.Id}, Name='{definition.Name}').");
            return definition;
        }

        private static void CreateWieldablePrefab(GameObject donorPrefab, ItemDefinition definition)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(donorPrefab);
            if (instance == null)
            {
                Debug.LogError("[Venda] No se pudo instanciar el prefab donante.");
                return;
            }

            try
            {
                // Romper el vínculo con el prefab del vendor: si no, esto sería una VARIANTE y un
                // reimport del .unitypackage arrastraría sus cambios hasta aquí dentro.
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);

                instance.name = NodeName;

                SwapWieldableComponent(instance);
                HideDonorModel(instance);
                AttachGauzeRoll(instance);

                int repointed = RepointReferencedItem(instance, definition.Id);
                if (repointed == 0)
                {
                    Debug.LogError("[Venda] No se encontró ningún '_referencedItem' en el prefab clonado. " +
                                   "El wieldable seguiría ligado a la BRÚJULA: equipar la venda sacaría una " +
                                   "brújula. Revísalo antes de usarlo.");
                }

                PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
                Debug.Log($"[Venda] Creado '{PrefabPath}' (referencedItem reapuntado en {repointed} " +
                          $"componente(s) al id {definition.Id}).");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Fuera el <c>WieldableTool</c> (no entrega el mantenido y, sobre todo, no es esto), fuera
        /// el <c>WieldableDurabilityDepleter</c> (quema carga que la venda no tiene) y fuera la
        /// aguja giratoria de la brújula. Nuestro componente se añade ANTES de quitarlos:
        /// <c>WieldableItem</c> y los animadores buscan un <c>IWieldable</c> en el mismo objeto, y
        /// dejarlo un instante sin ninguno es pedirle a Unity que se queje.
        /// </summary>
        private static void SwapWieldableComponent(GameObject root)
        {
            if (root.GetComponent<BandageWieldable>() == null)
                root.AddComponent<BandageWieldable>();

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string type = component.GetType().Name;
                if (type != "WieldableTool" && type != "WieldableDurabilityDepleter"
                    && type != "WieldableRotatingElement") continue;

                Object.DestroyImmediate(component, true);
            }
        }

        /// <summary>
        /// Apaga el modelo de la brújula y deja encendidos los brazos. Se apagan y no se BORRAN
        /// porque el esqueleto de los brazos y el del item comparten raíz en estos prefabs: quitar
        /// una malla es barato de deshacer, quitar huesos no.
        /// </summary>
        private static void HideDonorModel(GameObject root)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                bool keep = System.Array.IndexOf(KeptRenderers, renderer.gameObject.name) >= 0;
                renderer.enabled = keep;
            }
        }

        /// <summary>
        /// El rollo de gasa en la mano derecha, con el mismo constructor que pinta la venda puesta:
        /// lo que llevas en la mano y lo que acabas teniendo en el brazo son la misma tela.
        ///
        /// Se construye AQUÍ, en el horneado, y no en runtime: así el prefab guardado ya trae la
        /// malla y el material, y lo que se ve en el editor es lo que se verá en juego.
        /// </summary>
        private static void AttachGauzeRoll(GameObject root)
        {
            var hand = FindDeep(root.transform, HandBoneName);
            if (hand == null)
            {
                Debug.LogWarning($"[Venda] Sin hueso '{HandBoneName}' en el prefab donante: la venda se " +
                                 "empuñará con la mano vacía. Revisa si el vendor renombró los huesos.");
                return;
            }

            var roll = BandageVisual.Attach(hand, radius: 0.028f, length: 0.05f, alongBone: 0.35f);
            if (roll == null)
                return;

            roll.name = "BR_GauzeRoll";
            roll.SetActive(true);
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

        [MenuItem("Backrooms/Venda/Registrar venda en el jugador", false, 94)]
        public static void Register()
            => SprayCanWieldableRegistrar.RegisterWieldablePrefab(PrefabPath, NodeName);

        /// <summary>
        /// Dar vendas a mano, calcado del giver de la linterna: mientras la venda no esté en
        /// ninguna tabla de loot, la única forma de tener una es no tenerla.
        /// </summary>
        [MenuItem("Backrooms/Venda/Dar tres vendas al jugador", false, 95)]
        private static void Give()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[Venda] Solo en Play: el inventario no existe fuera de una partida.");
                return;
            }

            var definition = ItemDefinition.GetWithName(ItemName);
            if (definition == null)
            {
                Debug.LogError($"[Venda] No hay ItemDefinition llamada '{ItemName}'. Ejecuta antes " +
                               "'Backrooms/Venda/Crear venda'.");
                return;
            }

            var motor = LocalPlayerLocator.Find<PolymindGames.MovementSystem.CharacterControllerMotor>();
            var character = motor != null ? motor.GetComponentInParent<ICharacter>() : null;
            if (character?.Inventory == null)
            {
                Debug.LogError("[Venda] No se encuentra al jugador LOCAL con inventario. ¿Estás dentro de " +
                               "una partida, no solo en el menú?");
                return;
            }

            // De una en una: `AddItemsById` en bloque trunca a 1 cuando el StackSize del item es 1,
            // y aunque aquí no lo sea, pedirlas sueltas se comporta igual en los dos casos.
            int added = 0;
            for (int i = 0; i < 3; i++)
            {
                var (ok, _) = character.Inventory.AddItemsById(definition.Id, 1);
                added += ok;
            }

            Debug.Log($"[Venda] {added} venda(s) al inventario.");
        }
    }
}
#endif
