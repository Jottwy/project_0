#if UNITY_EDITOR
using PolymindGames.ResourceHarvesting;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-145 D5 — un <c>HarvestableResourceDefinition</c> por CLASE de material del atrezo de
    /// oficina (18 kinds → 4 clases reales; "Silla con tela" queda subsumida en Madera-pequeño),
    /// crear-si-falta. Se ejecuta desde "Backrooms ▸ Create Office Harvest Assets".
    ///
    /// A DIFERENCIA de <see cref="BackroomsDismantleAssetsCreator"/> (ADR-114), aquí NO se crea
    /// ningún prefab: <c>Wg3SceneAssembler.AssembleProp</c> añade <c>HarvestableResource</c> +
    /// <c>NetworkHarvestableInstance</c> EN RUNTIME sobre el prop ya instanciado (los prefabs de
    /// `Resources/Wg3Props` son copias de packs de terceros, decenas de variantes por kind — no
    /// hay "un" prefab por clase que autorar). Estas 4 definiciones sólo cargan por
    /// <c>Resources.Load</c> desde ese punto.
    ///
    /// <c>_requiredPower</c> es el MISMO 0,5 de ADR-114 (regla D5: "sin herramienta nueva", el
    /// destornillador ya vale). Los tamaños de <c>_harvestBounds</c> son placeholders por clase, no
    /// por kind — `TODO(balance)`, como toda cifra de loot del proyecto.
    /// </summary>
    public static class BackroomsOfficeHarvestAssetsCreator
    {
        public const string DefinitionFolder = "Assets/Resources/Wg3Props/Definitions";

        public const float RequiredPower = 0.5f;

        private readonly struct ClassSpec
        {
            public readonly string AssetName;
            public readonly string DisplayName;
            public readonly Vector3 BoundsSize;

            public ClassSpec(string assetName, string displayName, Vector3 boundsSize)
            {
                AssetName = assetName;
                DisplayName = displayName;
                BoundsSize = boundsSize;
            }
        }

        // Nombre = `Wg3PropHarvest.MaterialClass`, carácter a carácter — es la cadena que
        // `Wg3SceneAssembler` usa para el `Resources.Load` en runtime.
        private static readonly ClassSpec[] Classes =
        {
            new("WoodSmall", "Objeto pequeño de madera/papel", new Vector3(1.2f, 1.4f, 1.2f)),
            new("MetalSmall", "Objeto pequeño metálico/electrónico", new Vector3(1.0f, 1.0f, 1.0f)),
            new("BigFurniture", "Mueble grande", new Vector3(2.2f, 1.6f, 1.6f)),
            new("MetalContainer", "Contenedor metálico", new Vector3(1.6f, 2.2f, 1.6f)),
        };

        public static string DefinitionPathFor(string className) => $"{DefinitionFolder}/BR_Office{className}.asset";

        [MenuItem("Backrooms/Create Office Harvest Assets")]
        public static void CreateIfMissing()
        {
            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder("Assets/Resources/Wg3Props");
            BackroomsEditorFolders.EnsureFolder(DefinitionFolder);

            int created = 0, kept = 0;
            foreach (var cls in Classes)
            {
                string path = DefinitionPathFor(cls.AssetName);
                if (AssetDatabase.LoadAssetAtPath<HarvestableResourceDefinition>(path) != null)
                {
                    kept++;
                    continue;
                }
                CreateDefinition(path, cls);
                created++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            foreach (var cls in Classes)
            {
                var def = Resources.Load<HarvestableResourceDefinition>($"Wg3Props/Definitions/BR_Office{cls.AssetName}");
                if (def == null)
                    Debug.LogError($"[OfficeHarvestAssets] 'Wg3Props/Definitions/BR_Office{cls.AssetName}' no resuelve por Resources.Load.");
                else
                    Debug.Log($"[OfficeHarvestAssets] '{cls.AssetName}' OK.");
            }

            Debug.Log($"[OfficeHarvestAssets] {created} creados, {kept} ya existían.");
        }

        private static void CreateDefinition(string path, ClassSpec cls)
        {
            var definition = ScriptableObject.CreateInstance<HarvestableResourceDefinition>();
            AssetDatabase.CreateAsset(definition, path);

            var bounds = new Bounds(new Vector3(0f, cls.BoundsSize.y * 0.5f, 0f), cls.BoundsSize);

            var so = new SerializedObject(definition);
            so.FindProperty("_harvestableName").stringValue = cls.DisplayName;
            // ADR-114 D6: el único tipo libre del enum cerrado del vendor — mismo criterio que el
            // desmontable de escritorio/estantería/silla.
            so.FindProperty("_resourceType").intValue = (int)HarvestableResourceType.Plant;
            so.FindProperty("_requiredPower").floatValue = RequiredPower;
            // El reloj de regeneración es del BACKEND (D3/D5, ADR-114 D5 15 min): 0 aquí para que
            // el vendor no resucite nada por ciclo de día en el cliente.
            so.FindProperty("_respawnDays").intValue = 0;
            so.FindProperty("_harvestBounds").boundsValue = bounds;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);

            Debug.Log($"[OfficeHarvestAssets] Creada '{path}' (Plant, power {RequiredPower}, bounds {bounds.size:F2}).");
        }
    }
}
#endif
