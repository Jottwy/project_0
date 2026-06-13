using UnityEngine;

namespace BackroomsSurvival.Gameplay.World
{
    /// <summary>
    /// Spawns the Backrooms fluorescent point lights across each chunk ceiling.
    ///
    /// Render settings (sky / ambient / fog) are intentionally NOT touched here —
    /// they are configured elsewhere. Per-chunk lights are spawned via
    /// <see cref="PlaceFluorescentLights"/> and parented to the chunk root, so
    /// they are destroyed together with the chunk when it streams out — no manual
    /// cleanup needed.
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
            // RenderSettings (sky / ambient / fog) are no longer driven here; this
            // component only spawns the per-chunk fluorescent point lights via
            // PlaceFluorescentLights. The ambient/fog fields are kept as inspector
            // knobs for whatever does own those settings.
            Debug.Log("[BackroomsLighting] Fluorescent lights only — RenderSettings untouched.");
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
