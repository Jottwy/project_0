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

        // ── Paleta base de Level 0 — LOS TRES MATERIALES DE SUPERFICIE ──────────
        //
        // Toda la geometría de mundo (suelo, pared —incluida SealedWall, que colapsa a
        // Wall en el cliente— techo, pilares, dinteles, variantes de WallVariantSet y la
        // escalera de OFFICE) se pinta con estos tres y solo con estos tres. Son assets
        // en disco, no valores en código, para que el albedo, la smoothness y la textura
        // se puedan mirar y retocar en el Inspector sin recompilar.
        //
        // POR QUÉ DEJARON DE CONSTRUIRSE DESDE LA CAPA: `cfg.{floor,wall,ceiling}Texture`
        // daba un material distinto por capa (Vestíbulo papel pintado, Concreto hormigón,
        // …) y `cfg.*Tint` los bajaba hasta albedo 0.20 en la capa 3. Con eso no había
        // forma de saber si una superficie oscura lo era por el material o por la luz, que
        // es justo lo que este paso tenía que despejar. La identidad por capa vuelve, si
        // vuelve, como pasada de arte encima de esta base — no como una base distinta.
        public const string WallMaterialResource    = "GridMaterials/M_Backrooms_Wall";
        public const string FloorMaterialResource   = "GridMaterials/M_Backrooms_Floor";
        public const string CeilingMaterialResource = "GridMaterials/M_Backrooms_Ceiling";

        /// <summary>
        /// Build the shared materials for <paramref name="cfg"/>. Floor/wall/ceiling son
        /// COPIAS de los tres materiales canon (<see cref="WallMaterialResource"/> y
        /// compañía), idénticas en las cuatro capas; lámpara, tubería y prop siguen
        /// derivándose de la capa. La copia (y no el asset en sí) es obligatoria: esta
        /// clase destruye sus materiales en <see cref="Destroy"/>, y destruir un asset de
        /// disco lo borraría del proyecto en el editor.
        /// </summary>
        public static LayerVisualMaterials Build(LayerVisualConfig cfg)
        {
            var standard = Shader.Find("Universal Render Pipeline/Lit");
            if (standard == null)
                Debug.LogError("[LayerVisualMaterials] 'Universal Render Pipeline/Lit' shader not found — surfaces would render magenta.");
            var wallShader = Shader.Find("Backrooms/GridWallOffset") ?? standard;

            return new LayerVisualMaterials
            {
                floor   = CanonSurface(FloorMaterialResource,   standard,   cfg.floorTexture,   cfg.floorTint,   "GridMaterials/GridFloor"),
                wall    = CanonSurface(WallMaterialResource,    wallShader, cfg.wallTexture,    cfg.wallTint,    "GridMaterials/GridWall"),
                ceiling = CanonSurface(CeilingMaterialResource, standard,   cfg.ceilingTexture, cfg.ceilingTint, "GridMaterials/GridCeiling"),
                lamp    = MakeLamp(standard, cfg.lampColor, cfg.lampEmission),
                pipe    = MakePipe(standard),
                prop    = MakeProp(standard),
            };
        }

        /// <summary>
        /// Una copia del material canon de <paramref name="resource"/>. Si el asset no
        /// está (proyecto a medio importar, Resources podado), cae al camino anterior —
        /// material por capa desde textura y tinte — en vez de dejar la superficie sin
        /// material, que en URP se dibuja magenta. La caída se loguea UNA vez por rol.
        /// </summary>
        private static Material CanonSurface(string resource, Shader fallbackShader,
            Texture fallbackTex, Color fallbackTint, string fallbackMatResource)
        {
            var canon = Resources.Load<Material>(resource);
            if (canon != null)
                return new Material(canon) { name = $"Canon_{canon.name}" };

            if (_missingCanon.Add(resource))
                Debug.LogError($"[LayerVisualMaterials] Material canon '{resource}' no encontrado en Resources — " +
                               "esta superficie vuelve al material por capa y NO comparte la paleta base.");
            return MakeSurface(fallbackShader, fallbackTex, fallbackTint, fallbackMatResource);
        }

        // Un error por rol y por sesión, no uno por capa y por reconstrucción de chunk.
        private static readonly System.Collections.Generic.HashSet<string> _missingCanon =
            new System.Collections.Generic.HashSet<string>();

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

        /// <paramref name="emission"/> es el brillo del DIFUSOR, no la intensidad de la Light.
        /// Enmienda de ADR-066: aquí se pasaba <c>cfg.lampIntensity</c>, o sea la potencia de
        /// la luz, y el panel acababa a 2.8–3.4 × blanco puro — sin forma bajo ningún
        /// tonemapper y desbordando el bloom por todo el techo.
        ///
        /// El <c>.linear</c> de la emisión es el mismo arreglo que en
        /// <c>BackroomsLighting</c>: <c>_EmissionColor</c> es [HDR] en URP/Lit y se consume
        /// como valor LINEAL, mientras que la <c>Light</c> que acompaña al panel recibe el
        /// color en gamma y lo convierte Unity. Sin la conversión, un color de lámpara
        /// saturado sale del difusor más pálido de lo que proyecta.
        private static Material MakeLamp(Shader shader, Color color, float emission)
        {
            var m = new Material(shader) { name = "LayerLampEmissive" };
            if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, color);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.SetColor(EmissionColorId, color.linear * Mathf.Max(0f, emission));
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
