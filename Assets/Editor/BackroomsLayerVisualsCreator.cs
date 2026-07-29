#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Fase 5A — creates the 4 LayerVisualConfig assets in Resources/LayerVisuals with
    /// the agreed per-layer tints/lighting/fog, wiring the bespoke Backrooms textures
    /// from Resources/Textures. Re-runnable (updates existing assets in place). Run via
    /// "Backrooms ▸ Create Layer Visuals", then they auto-load in GridTestWorld
    /// (or assign them to its Layer Visuals array).
    ///
    /// AVISO: re-ejecutar esto RE-SERIALIZA los 4 assets, así que hornea en YAML
    /// los campos que hoy viven del inicializador de campo — `zoneTints` entre
    /// ellos (ninguno de los 4 `.asset` lo tiene escrito; ver
    /// LayerVisualConfig.DefaultZoneTints, marcado como paleta placeholder "no
    /// final"). Horneado, un cambio futuro del inicializador ya
    /// no propagaría a esos assets. Los campos que este creador NO asigna
    /// (props/propDensity/propClusterBias) se conservan intactos en un asset
    /// existente, pero salen a su default en uno recién creado.
    /// </summary>
    public static class BackroomsLayerVisualsCreator
    {
        private const string Folder = "Assets/Resources/LayerVisuals";
        private const string TexDir = "Assets/Resources/Textures/";

        [MenuItem("Backrooms/Create Layer Visuals")]
        public static void CreateAll()
        {
            EnsureFolder("Assets/Resources");
            EnsureFolder(Folder);

            var carpet  = Tex("CarpetBeige");
            var paper   = Tex("WallpaperYellow");
            var ceiling = Tex("CeilingTiles");
            var tile    = Tex("TileWall");
            var tile1   = Tex("TileWall 1");

            Save("Layer0_Vestibulo", c =>
            {
                c.floorTexture = carpet;    c.floorTint   = new Color(0.82f, 0.74f, 0.51f, 1f);
                c.wallTexture  = paper;     c.wallTint    = new Color(0.90f, 0.85f, 0.65f, 1f);
                c.ceilingTexture = ceiling; c.ceilingTint = new Color(0.88f, 0.88f, 0.82f, 1f);
                c.showCeiling = true;
                c.lightDensity = 0.70f; c.brokenLampChance = 0.10f;
                c.lampColor = new Color(1.00f, 0.97f, 0.88f, 1f); c.lampIntensity = 2.8f; c.lampRange = 5f;
                c.fogDensity = 0.035f; c.fogColor = new Color(0.78f, 0.71f, 0.59f, 1f);
                c.ceilingPanelVariety = 0.25f;
                c.ceilingPipes = true; c.ceilingPipeChance = 0.35f;
                // Pieza C — paleta por tile. Desviaciones DISCRETAS del tinte base
                // (la primera entrada ES el tinte base), no tonos nuevos: sobre ellas
                // sigue aplicandose el jitter HSV de ±8%, asi que un rango ancho aqui
                // leeria como parches de color, no como desgaste.
                c.floorTintVariants = new[]
                {
                    new Color(0.82f, 0.74f, 0.51f, 1f),
                    new Color(0.78f, 0.71f, 0.50f, 1f),
                    new Color(0.85f, 0.76f, 0.52f, 1f),
                };
                c.wallTintVariants = new[]
                {
                    new Color(0.90f, 0.85f, 0.65f, 1f),
                    new Color(0.85f, 0.79f, 0.58f, 1f),
                    new Color(0.93f, 0.88f, 0.70f, 1f),
                    new Color(0.86f, 0.85f, 0.62f, 1f), // leve viraje verdoso (humedad)
                };
                c.ceilingTintVariants = new[]
                {
                    new Color(0.88f, 0.88f, 0.82f, 1f),
                    new Color(0.85f, 0.85f, 0.79f, 1f),
                    new Color(0.90f, 0.90f, 0.85f, 1f),
                };
            });

            Save("Layer1_Industrial", c =>
            {
                c.floorTexture = tile;      c.floorTint   = new Color(0.65f, 0.62f, 0.55f, 1f);
                c.wallTexture  = tile1;     c.wallTint    = new Color(0.70f, 0.67f, 0.58f, 1f);
                c.ceilingTexture = ceiling; c.ceilingTint = new Color(0.60f, 0.60f, 0.57f, 1f);
                c.showCeiling = true;
                c.lightDensity = 0.55f; c.brokenLampChance = 0.20f;
                c.lampColor = new Color(0.95f, 0.92f, 0.75f, 1f); c.lampIntensity = 1.6f; c.lampRange = 5.5f;
                c.fogDensity = 0.05f; c.fogColor = new Color(0.60f, 0.58f, 0.52f, 1f);
                c.ceilingPanelVariety = 0.40f;
                c.ceilingPipes = true; c.ceilingPipeChance = 0.80f;
            });

            Save("Layer2_Concreto", c =>
            {
                c.floorTexture = tile1;     c.floorTint   = new Color(0.45f, 0.43f, 0.40f, 1f);
                c.wallTexture  = tile;      c.wallTint    = new Color(0.50f, 0.48f, 0.44f, 1f);
                c.ceilingTexture = ceiling; c.ceilingTint = new Color(0.40f, 0.40f, 0.38f, 1f);
                c.showCeiling = true;
                c.lightDensity = 0.40f; c.brokenLampChance = 0.30f;
                c.lampColor = new Color(0.88f, 0.80f, 0.55f, 1f); c.lampIntensity = 1.3f; c.lampRange = 6f;
                c.fogDensity = 0.07f; c.fogColor = new Color(0.42f, 0.40f, 0.37f, 1f);
                c.ceilingPanelVariety = 0.70f;
                c.ceilingPipes = true; c.ceilingPipeChance = 1.00f;
            });

            Save("Layer3_Vacio", c =>
            {
                c.floorTexture = carpet;    c.floorTint   = new Color(0.25f, 0.23f, 0.20f, 1f);
                c.wallTexture  = paper;     c.wallTint    = new Color(0.30f, 0.27f, 0.22f, 1f);
                c.ceilingTexture = ceiling; c.ceilingTint = new Color(0.20f, 0.20f, 0.18f, 1f);
                c.showCeiling = true;
                c.lightDensity = 0.25f; c.brokenLampChance = 0.45f;
                c.lampColor = new Color(0.80f, 0.68f, 0.40f, 1f); c.lampIntensity = 0.9f; c.lampRange = 6f;
                c.fogDensity = 0.09f; c.fogColor = new Color(0.28f, 0.25f, 0.22f, 1f);
                c.ceilingPanelVariety = 0.95f;
                c.ceilingPipes = false; c.ceilingPipeChance = 0f;
            });

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BackroomsLayerVisualsCreator] 4 LayerVisualConfig assets created/updated in {Folder}");
        }

        private static Texture2D Tex(string name)
        {
            var t = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}{name}.png");
            if (t == null) Debug.LogWarning($"[LayerVisuals] texture not found: {TexDir}{name}.png");
            return t;
        }

        private static void Save(string name, Action<LayerVisualConfig> set)
        {
            string path = $"{Folder}/{name}.asset";
            var cfg = AssetDatabase.LoadAssetAtPath<LayerVisualConfig>(path);
            bool isNew = cfg == null;
            if (isNew) cfg = ScriptableObject.CreateInstance<LayerVisualConfig>();
            set(cfg);
            if (isNew) AssetDatabase.CreateAsset(cfg, path);
            else EditorUtility.SetDirty(cfg);
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }
    }
}
#endif
