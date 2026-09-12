using System.IO;
using System.Text;
using BackroomsSurvival.Net;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-064 enm. 1 — menú <c>Backrooms/Create Craft Assets</c>, crear-si-falta e idempotente,
    /// tres pasos:
    ///
    /// 1. <b>E1.5</b> — en <c>STP_Player.prefab</c> (el jugador que instancia <c>STP_GameMode</c>,
    ///    variante de <c>FPS_Player</c>) sustituye el <c>CraftingManager</c> del vendor por
    ///    <see cref="NetworkedCraftingManager"/> conservando su <c>_craftAudio</c>. Quita el del
    ///    vendor ANTES de añadir el nuestro: dos <c>ICraftingManagerCC</c> bajo el mismo personaje
    ///    lanzan en release. Es un parche en asset vendor (fila 8 de <c>vendor-patches.md</c>).
    /// 2. <b>E1.7</b> — añade a <c>BR_Bandage</c> un <c>CraftingData</c> de 2 Cloth, 1 unidad,
    ///    3 s, nivel 0, sin desmontaje. Si ya tiene uno, no lo toca.
    /// 3. <b>E1.6</b> — reescribe <c>docs/data/crafting-recipes.json</c> con
    ///    <see cref="CraftingRecipesOracle"/> si difiere del disco.
    /// </summary>
    public static class BackroomsCraftAssetsCreator
    {
        public const string PlayerPrefabPath = "Assets/PolymindGames/STP/Prefabs/Core/STP_Player.prefab";
        public const string BandageDefinitionPath = "Assets/Resources/Definitions/Item/BR_Bandage.asset";

        /// <summary>`STP_Cloth` — el id real del asset, el mismo que usa `Rope` (2 Cloth).</summary>
        public const int ClothId = 8505358;
        public const int BandageClothCount = 2;
        public const float BandageCraftSeconds = 3f;

        [MenuItem("Backrooms/Create Craft Assets")]
        public static void CreateIfMissing()
        {
            bool swapped = SwapCraftingManager();
            bool recipe = AddBandageRecipe();
            AssetDatabase.SaveAssets();
            ItemDefinition.ReloadDefinitions_EditorOnly();
            bool oracle = WriteOracle();
            AssetDatabase.Refresh();
            Debug.Log($"[CraftAssets] gestor {(swapped ? "SUSTITUIDO" : "ya era el nuestro")}, " +
                      $"receta de la venda {(recipe ? "AÑADIDA" : "ya existía")}, " +
                      $"oráculo {(oracle ? "REESCRITO" : "sin cambios")}.");
        }

        /// <summary>
        /// Devuelve true si tuvo que tocar el prefab.
        ///
        /// Es un cambio de GUID en el texto del prefab, no un <c>SaveAsPrefabAsset</c>, por dos
        /// razones medidas: (1) <c>STP_Player.prefab</c> arrastra un script perdido del vendor y
        /// Unity se NIEGA a guardar un prefab con «missing script» («This is not allowed»), así que
        /// la vía de <c>LoadPrefabContents</c> imprime «sustituido» y no escribe nada; (2) las dos
        /// clases serializan los mismos campos (<c>_craftAudio</c>), así que el componente del
        /// vendor pasa a ser el nuestro conservando su sonido sin copiar nada. Los GUID se leen
        /// de los <c>.meta</c>, nunca se escriben a mano. Idempotente.
        /// </summary>
        public static bool SwapCraftingManager()
        {
            string vendorGuid = ScriptGuid(typeof(CraftingManager));
            string ourGuid = ScriptGuid(typeof(NetworkedCraftingManager));
            if (vendorGuid == null || ourGuid == null)
                return false;

            string path = Path.Combine(ProjectRoot(), PlayerPrefabPath);
            string text = File.ReadAllText(path, Encoding.UTF8);
            string vendorRef = $"m_Script: {{fileID: 11500000, guid: {vendorGuid}, type: 3}}";
            string ourRef = $"m_Script: {{fileID: 11500000, guid: {ourGuid}, type: 3}}";
            if (!text.Contains(vendorRef))
            {
                if (!text.Contains(ourRef))
                    Debug.LogError($"[CraftAssets] '{PlayerPrefabPath}' no lleva CraftingManager ni el sustituto: " +
                                   "nada que sustituir (¿reimport a medias?).");
                return false;
            }

            File.WriteAllText(path, text.Replace(vendorRef, ourRef), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(PlayerPrefabPath, ImportAssetOptions.ForceUpdate);
            return true;
        }

        private static string ScriptGuid(System.Type type)
        {
            var script = MonoScriptFromType(type);
            if (script == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out string guid, out long _))
            {
                Debug.LogError($"[CraftAssets] Sin MonoScript para {type.Name}: ¿falta el .meta o el fichero?");
                return null;
            }
            return guid;
        }

        private static MonoScript MonoScriptFromType(System.Type type)
        {
            foreach (string guid in AssetDatabase.FindAssets($"{type.Name} t:MonoScript"))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                if (script != null && script.GetClass() == type)
                    return script;
            }
            return null;
        }

        /// <summary>Devuelve true si añadió la receta; false si ya la tenía (no la toca).</summary>
        public static bool AddBandageRecipe()
        {
            var bandage = AssetDatabase.LoadAssetAtPath<ItemDefinition>(BandageDefinitionPath);
            if (bandage == null)
            {
                Debug.LogError($"[CraftAssets] Falta '{BandageDefinitionPath}': la venda nace de bb9e3cc1, no de aquí.");
                return false;
            }
            if (bandage.TryGetDataOfType<CraftingData>(out _))
                return false;

            var so = new SerializedObject(bandage);
            var data = so.FindProperty("_data");
            int index = data.arraySize;
            data.InsertArrayElementAtIndex(index);
            var element = data.GetArrayElementAtIndex(index);
            element.managedReferenceValue = new CraftingData();
            so.ApplyModifiedPropertiesWithoutUndo();
            so.Update();

            element = so.FindProperty("_data").GetArrayElementAtIndex(index);
            var blueprint = element.FindPropertyRelative("_blueprint");
            blueprint.arraySize = 1;
            var requirement = blueprint.GetArrayElementAtIndex(0);
            requirement.FindPropertyRelative("Item").FindPropertyRelative("_value").intValue = ClothId;
            requirement.FindPropertyRelative("Amount").intValue = BandageClothCount;
            element.FindPropertyRelative("_craftAmount").intValue = 1;
            element.FindPropertyRelative("_craftDuration").floatValue = BandageCraftSeconds;
            element.FindPropertyRelative("_craftLevel").intValue = 0;
            element.FindPropertyRelative("_dismantleEfficiency").floatValue = 0f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bandage);
            return true;
        }

        /// <summary>Escribe el oráculo si difiere del disco (UTF-8 sin BOM, LF). True si escribió.</summary>
        public static bool WriteOracle()
        {
            string json = CraftingRecipesOracle.BuildJson();
            string path = Path.Combine(ProjectRoot(), CraftingRecipesOracle.RelativePath);
            if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == json)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return true;
        }

        public static string ProjectRoot() => Path.GetDirectoryName(Application.dataPath);
    }
}
