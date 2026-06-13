using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.Gameplay.World
{
    /// <summary>
    /// Establishes the Backrooms visual atmosphere:
    ///   • black sky (no skybox) and flat, dim yellow ambient — no sun,
    ///   • dense yellow exponential fog that strangles visibility past ~15 m,
    ///   • runtime fluorescent point lights spaced across each chunk ceiling.
    ///
    /// Global render settings are applied once in <see cref="Awake"/>. Per-chunk
    /// lights are spawned via <see cref="PlaceFluorescentLights"/> and parented to
    /// the chunk root, so they are destroyed together with the chunk when it
    /// streams out — no manual cleanup needed.
    /// </summary>
    public sealed class BackroomsLighting : MonoBehaviour
    {
        [Header("Ambient & fog")]
        [SerializeField] private Color ambientColor = new Color(0.45f, 0.40f, 0.20f); // warm dim yellow
        [SerializeField] private Color fogColor     = new Color(0.55f, 0.50f, 0.28f); // dirty yellow
        [SerializeField] private float fogDensity   = 0.018f; // ~invisible at 15 m, fully hidden at 40 m

        [Header("Fluorescent lights")]
        [SerializeField] private float lightIntensity   = 1.8f;
        [SerializeField] private float lightRange        = 12f;
        [SerializeField] private int   lightEveryNTiles = 4;

        // Canonical fluorescent tube tint — fixed (not exposed) per spec.
        private static readonly Color LightColor = new Color(1f, 0.95f, 0.78f);

        private void Awake()
        {
            // 1 — black sky + flat yellow ambient; kill any directional "sun".
            RenderSettings.skybox       = null;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = ambientColor;

            var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (var l in lights)
                if (l.type == LightType.Directional)
                    l.gameObject.SetActive(false);

            // 2 — dense yellow exponential fog.
            RenderSettings.fog        = true;
            RenderSettings.fogMode    = FogMode.Exponential;
            RenderSettings.fogColor   = fogColor;
            RenderSettings.fogDensity = fogDensity;
        }

        /// <summary>
        /// Spawns point lights just below the ceiling of <paramref name="chunkRoot"/>,
        /// one every <see cref="lightEveryNTiles"/> tiles in both axes. Positions are
        /// chunk-local and follow GridChunkBuilder's tile-centre convention
        /// ((tile + 0.5) × tileSize), so a light lands over the middle of its tile.
        /// </summary>
        public void PlaceFluorescentLights(Transform chunkRoot, int tilesX, int tilesZ,
            float tileSize, float ceilingHeight)
        {
            int step = Mathf.Max(1, lightEveryNTiles);
            for (int tz = 0; tz < tilesZ; tz += step)
            {
                for (int tx = 0; tx < tilesX; tx += step)
                {
                    var go = new GameObject("FluorescentLight");
                    go.transform.SetParent(chunkRoot, false);
                    go.transform.localPosition = new Vector3(
                        (tx + 0.5f) * tileSize,
                        ceilingHeight - 0.3f, // just below the ceiling slab
                        (tz + 0.5f) * tileSize);

                    var light = go.AddComponent<Light>();
                    light.type      = LightType.Point;
                    light.color     = LightColor;
                    light.intensity = lightIntensity;
                    light.range     = lightRange;
                    light.shadows   = LightShadows.None;
                }
            }
        }
    }
}
