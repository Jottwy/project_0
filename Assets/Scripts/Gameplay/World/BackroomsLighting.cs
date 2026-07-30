using BackroomsSurvival.Gameplay.GridWorld;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.World
{
    /// <summary>
    /// Fase 5A — per-chunk fluorescent lighting driven by a <see cref="LayerVisualConfig"/>.
    /// For each tile: a luminaire MESH (emissive tube) is placed with probability
    /// <c>lightDensity</c>; of those, <c>brokenLampChance</c> are dark (mesh only, no
    /// Light); the rest cast a point Light, and ~30% of those flicker (10% of which
    /// eventually die). All decisions are deterministic per chunk (System.Random
    /// seeded by chunk coords) so every client/run agrees on which lamps are broken.
    ///
    /// Lights/meshes are parented to the chunk root, so they are destroyed with the
    /// chunk when it streams out — no manual cleanup. RenderSettings are not touched
    /// here (fog is owned by ChunkStreamer per active layer).
    /// </summary>
    public sealed class BackroomsLighting : MonoBehaviour
    {
        // ~30% of working lamps flicker; ~10% of those eventually burn out.
        private const double FlickerChance = 0.30;
        private const double DeathChance = 0.10;

        // Pieza D — salt for the per-chunk lamp-density jitter ("LDNS").
        private const uint LightSaltDensity = 0x4C444E53U;

        // Lazy-init at first use, NOT via a field initializer: a static initializer in a
        // MonoBehaviour runs inside the first AddComponent's constructor context, where
        // creating a MaterialPropertyBlock throws "CreateImpl not allowed from constructor".
        private static MaterialPropertyBlock _lampMpb;

        /// <summary>
        /// Spawn luminaires across <paramref name="chunkRoot"/>'s ceiling. Positions
        /// follow GridChunkBuilder's tile-centre convention ((tile + 0.5) × tileSize).
        /// <paramref name="lampMat"/> is the shared emissive material for this layer.
        /// </summary>
        public void PlaceFluorescentLights(Transform chunkRoot, int tilesX, int tilesZ,
            float tileSize, float ceilingHeight, LayerVisualConfig cfg, Material lampMat,
            int chunkX, int chunkZ, int worldLayer, byte[,] walls)
        {
            if (cfg == null) return;

            // One deterministic RNG per chunk; tiles drawn in scan order.
            var rng = new System.Random(unchecked(chunkX * 73856093 ^ chunkZ * 19349663));
            Color litEmission = cfg.lampColor * Mathf.Max(0f, cfg.lampIntensity);

            // Pieza D — per-chunk density jitter. With a flat cfg.lightDensity every
            // chunk lands within a couple of lamps of the same count, so a corridor is
            // lit identically end to end and the space reads as one uniform box. The
            // band is deliberately asymmetric downward so genuinely DARK stretches
            // exist; the pure hash keeps it deterministic per chunk (all peers agree)
            // without touching the rng stream above. TODO(balance): band tuned by eye.
            float density = Mathf.Clamp01(cfg.lightDensity *
                (0.30f + GridChunkBuilder.Hash01(chunkX, chunkZ, LightSaltDensity) * 0.90f));

            // Lamps hang from the ceiling of a tile the player can be under. A fully
            // enclosed tile has no such space — its luminaire was buried inside solid
            // geometry, wasting a draw call and a realtime Light on something never
            // seen. Same 0x0F test GridChunkBuilder.PlaceProps uses. null ⇒ no filter
            // (caller without geometry to hand, e.g. a fixed test room).
            bool HasCeilingSpace(int tx, int tz)
                => walls == null || (walls[tx, tz] & 0x0F) != 0x0F;

            // Fase 5A (Bug #1): isolate this layer's lamps to this layer's geometry so
            // light never bleeds through the thin slabs into adjacent stacked layers.
            // The luminaire meshes go on the same Unity layer as their chunk; each Light
            // illuminates that geometry layer PLUS everything non-geometric (player,
            // props) — never the other macro-layers. (~GeoMask clears all 4 layer bits,
            // then we re-add only this one.)
            int geoLayer = GridChunkBuilder.GeoLayer(worldLayer);
            int litMask  = ~GridChunkBuilder.GeoMask | (1 << geoLayer);

            for (int tz = 0; tz < tilesZ; tz++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    if (!HasCeilingSpace(tx, tz)) continue;   // solid tile — nothing to light
                    if (rng.NextDouble() >= density) continue; // no luminaire here

                    var localPos = new Vector3(
                        (tx + 0.5f) * tileSize,
                        ceilingHeight - 0.3f,   // just below the ceiling slab
                        (tz + 0.5f) * tileSize);

                    bool broken     = rng.NextDouble() < cfg.brokenLampChance;
                    bool flickering = !broken && rng.NextDouble() < FlickerChance;
                    bool dying      = flickering && rng.NextDouble() < DeathChance;
                    float frequency = flickering ? (float)(0.5 + rng.NextDouble() * 3.5) : 0f;   // 0.5–4 Hz
                    float deathIn   = dying ? (float)(10.0 + rng.NextDouble() * 50.0) : 0f;       // 10–60 s

                    // Luminaire mesh (always): emissive when working, near-dark when broken.
                    var mesh = MakeLuminaire(chunkRoot, localPos, lampMat,
                        broken ? cfg.lampColor * 0.04f : litEmission, out var meshRenderer);
                    mesh.layer = geoLayer; // so only this layer's lamps light the tube

                    if (broken) continue; // dark tube, no light cast (mesh stays)

                    // One centred Point light per panel (4 lights/panel killed perf).
                    //
                    // Pieza D: the range now comes from cfg.lampRange instead of a hardcoded
                    // 5 m that silently ignored the field. HARD CONSTRAINT when retuning it:
                    // horizontal reach at floor level is √(range² − ceilingHeight²), and it
                    // must stay under the 5 m corridor width or a lamp lights the corridor
                    // BEYOND its own wall — the per-layer cullingMask below only stops
                    // bleed between macro-layers, never sideways within one. With a 4 m
                    // ceiling that caps range at ~6.4 m; the authored values are ≤ 6.
                    var lightGo = new GameObject("FluorescentLight");
                    lightGo.transform.SetParent(mesh.transform, false); // centred under the panel
                    var light = lightGo.AddComponent<Light>();
                    light.type        = LightType.Point;
                    light.color       = cfg.lampColor;
                    light.intensity   = cfg.lampIntensity;
                    light.range       = Mathf.Max(0.1f, cfg.lampRange);
                    light.shadows     = LightShadows.None;
                    light.renderMode  = LightRenderMode.Auto;
                    light.cullingMask = litMask; // only this layer's geometry + non-geo

                    if (flickering)
                    {
                        var f = lightGo.AddComponent<LampFlicker>();
                        f.target        = light;
                        f.baseIntensity = cfg.lampIntensity;
                        f.frequency     = frequency;
                        f.mesh          = meshRenderer; // already resolved by MakeLuminaire
                        f.litEmission   = litEmission;
                        if (dying) f.Invoke(nameof(LampFlicker.StartDying), deathIn);
                    }
                }
            }
        }

        /// <paramref name="renderer"/> is handed back so the flicker path does not look the
        /// MeshRenderer up a second time on a component this method already resolved.
        private static GameObject MakeLuminaire(Transform parent, Vector3 localPos,
            Material lampMat, Color emission, out MeshRenderer renderer)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Luminaire";
            if (go.TryGetComponent<Collider>(out var col)) Destroy(col); // decorative — no collision
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(1.65f, 0.08f, 1.65f); // flat square ceiling panel

            var r = go.GetComponent<MeshRenderer>();
            if (lampMat != null) r.sharedMaterial = lampMat;
            _lampMpb ??= new MaterialPropertyBlock();
            _lampMpb.Clear();
            _lampMpb.SetColor(LayerVisualMaterials.EmissionColorId, emission);
            r.SetPropertyBlock(_lampMpb);
            renderer = r;
            return go;
        }
    }

    /// <summary>
    /// Fase 5A — per-lamp flicker. Oscillates the Light intensity at a per-lamp
    /// frequency/phase (not synced between lamps). A lamp told to die fades its
    /// intensity AND its luminaire mesh emission to zero, then removes itself.
    /// </summary>
    public sealed class LampFlicker : MonoBehaviour
    {
        public Light target;
        public float baseIntensity;
        public float minIntensity = 0.3f;   // × base
        public float frequency    = 2f;     // Hz

        // Optional: dim the luminaire mesh emission alongside the light when dying,
        // so a dead tube goes dark instead of glowing with no light cast.
        public MeshRenderer mesh;
        public Color litEmission;

        private float _offset;
        private bool  _dying;
        // Lazy-init at first use (same reason as BackroomsLighting._lampMpb).
        private static MaterialPropertyBlock _emb;

        private void Start() => _offset = Random.Range(0f, 100f); // cosmetic phase, non-deterministic OK

        private void Update()
        {
            if (target == null) { Destroy(this); return; }

            if (_dying)
            {
                target.intensity = Mathf.Max(0f, target.intensity - Time.deltaTime * 0.5f);
                float f = baseIntensity > 0f ? target.intensity / baseIntensity : 0f;
                SetMeshEmission(litEmission * f);
                if (target.intensity <= 0f) { SetMeshEmission(Color.black); Destroy(this); }
                return;
            }

            float t = Mathf.Sin((Time.time + _offset) * frequency * Mathf.PI * 2f);
            target.intensity = Mathf.Lerp(baseIntensity * minIntensity, baseIntensity, (t + 1f) * 0.5f);
        }

        public void StartDying() => _dying = true;

        private void SetMeshEmission(Color e)
        {
            if (mesh == null) return;
            _emb ??= new MaterialPropertyBlock();
            _emb.Clear();
            _emb.SetColor(LayerVisualMaterials.EmissionColorId, e);
            mesh.SetPropertyBlock(_emb);
        }
    }
}
