using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Fase 5A — the SHARED render materials for one layer, built once from a
    /// <see cref="LayerVisualConfig"/> and reused across every tile of that layer
    /// (sharedMaterial). Per-tile colour variety is applied with a
    /// MaterialPropertyBlock by <see cref="GridChunkBuilder"/>, NOT by instancing a
    /// material per tile (which would leak thousands of materials and break batching).
    ///
    /// Owned by <see cref="ChunkStreamer"/>: built lazily per layer, cached, and
    /// destroyed in the streamer's OnDestroy. Never assigned to an on-disk asset, so
    /// destroying these instances is safe.
    /// </summary>
    public sealed class LayerVisualMaterials
    {
        // URP Lit property names (the project runs URP Forward+ since the BIRP→URP
        // migration). These IDs are reused by GridChunkBuilder (per-tile _BaseColor
        // tint) and BackroomsLighting (lamp _EmissionColor), so they all stay in
        // lockstep.
        public static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        public static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        public static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");

        public Material floor;
        public Material wall;
        public Material ceiling;
        public Material lamp;   // emissive material for the luminaire mesh
        public Material pipe;   // Fase 5B — dark metallic overhead pipes
        public Material prop;   // Fase 5C — shared matte placeholder-prop material

        /// <summary>
        /// Build the shared materials for <paramref name="cfg"/>. URP Lit for
        /// floor/ceiling/lamp; the URP Backrooms/GridWallOffset shader for walls
        /// (preserves the anti-z-fight polygon offset against floor/ceiling seams).
        /// Falls back to the base Grid* textures when a layer texture is unset.
        /// </summary>
        public static LayerVisualMaterials Build(LayerVisualConfig cfg)
        {
            var standard = Shader.Find("Universal Render Pipeline/Lit");
            if (standard == null)
                Debug.LogError("[LayerVisualMaterials] 'Universal Render Pipeline/Lit' shader not found — surfaces would render magenta.");
            var wallShader = Shader.Find("Backrooms/GridWallOffset") ?? standard;

            return new LayerVisualMaterials
            {
                floor   = MakeSurface(standard,  cfg.floorTexture,   cfg.floorTint,   "GridMaterials/GridFloor"),
                wall    = MakeSurface(wallShader, cfg.wallTexture,   cfg.wallTint,    "GridMaterials/GridWall"),
                ceiling = MakeSurface(standard,  cfg.ceilingTexture, cfg.ceilingTint, "GridMaterials/GridCeiling"),
                lamp    = MakeLamp(standard, cfg.lampColor, cfg.lampIntensity),
                pipe    = MakePipe(standard),
                prop    = MakeProp(standard),
            };
        }

        private static Material MakePipe(Shader shader)
        {
            var m = new Material(shader) { name = "LayerPipeMetal" };
            if (m.HasProperty(BaseColorId))  m.SetColor(BaseColorId, new Color(0.25f, 0.25f, 0.27f, 1f));
            if (m.HasProperty(SmoothnessId)) m.SetFloat(SmoothnessId, 0.4f); // metallic read, not mirror
            return m;
        }

        private static Material MakeProp(Shader shader)
        {
            var m = new Material(shader) { name = "LayerPropMatte" };
            if (m.HasProperty(BaseColorId))  m.SetColor(BaseColorId, new Color(0.45f, 0.40f, 0.32f, 1f));
            if (m.HasProperty(SmoothnessId)) m.SetFloat(SmoothnessId, 0.15f); // matte office surfaces
            return m;
        }

        private static Material MakeSurface(Shader shader, Texture tex, Color tint, string fallbackMatResource)
        {
            var m = new Material(shader) { name = $"LayerSurface_{shader.name}" };
            Texture t = tex != null ? tex : BaseTexture(fallbackMatResource);
            if (t != null && m.HasProperty(BaseMapId)) m.SetTexture(BaseMapId, t);
            if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, tint);
            if (m.HasProperty(SmoothnessId)) m.SetFloat(SmoothnessId, 0.08f); // matte office surfaces
            return m;
        }

        private static Material MakeLamp(Shader shader, Color color, float intensity)
        {
            var m = new Material(shader) { name = "LayerLampEmissive" };
            if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, color);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.SetColor(EmissionColorId, color * Mathf.Max(0f, intensity));
            return m;
        }

        // Best-effort fallback: read _BaseMap from the on-disk Grid* material so a
        // layer with no explicit texture still shows the bespoke Backrooms surface.
        private static Texture BaseTexture(string matResource)
        {
            var baseMat = Resources.Load<Material>(matResource);
            return (baseMat != null && baseMat.HasProperty(BaseMapId)) ? baseMat.GetTexture(BaseMapId) : null;
        }

        public void Destroy()
        {
            SafeDestroy(floor);
            SafeDestroy(wall);
            SafeDestroy(ceiling);
            SafeDestroy(lamp);
            SafeDestroy(pipe);
            SafeDestroy(prop);
            floor = wall = ceiling = lamp = pipe = prop = null;
        }

        private static void SafeDestroy(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
