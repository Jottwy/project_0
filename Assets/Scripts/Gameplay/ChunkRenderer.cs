using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Backend-driven Level 0 renderer.
    ///
    /// Rule:
    /// - Backend/generator chooses template_id.
    /// - Renderer only visualizes that template.
    /// - No random client-side fake macro-architecture that can cross walls/layout.
    ///
    /// This keeps host/joiner deterministic and avoids the "buggy black planes/ramp soup"
    /// problem from visual-only random verticality.
    /// </summary>
    internal struct Level0Profile
    {
        public float humidity;
        public float flickerChance;
        public float wallToneShift;
        public float grime;
        public float depthFactor;
        public bool hasWaterStain;
        public bool hasCeilingDrip;
        public bool hasWallStain;
        public int lightPattern;
        public int propVariant;

        public static Level0Profile FromSeedAndPos(long worldSeed, int cx, int cz)
        {
            ulong h = (ulong)worldSeed ^ 0x9E3779B97F4A7C15UL;
            h += ((ulong)cx) * 0xFF51AFD7ED558CCDUL;
            h ^= h >> 33;
            h += ((ulong)cz) * 0xC4CEB9FE1A85EC53UL;
            h ^= h >> 29;
            h *= 0x9E3779B185EBCA87UL;
            h ^= h >> 32;

            float f0 = ((h >> 0) & 0xFFFF) / 65535f;
            float f1 = ((h >> 16) & 0xFFFF) / 65535f;
            float f2 = ((h >> 32) & 0xFFFF) / 65535f;
            float f3 = ((h >> 48) & 0xFFFF) / 65535f;

            float dist = Mathf.Abs(cx) + Mathf.Abs(cz);
            float depthFactor = Mathf.Clamp01(dist / 10f);

            float humidity = Mathf.Clamp01(Mathf.Lerp(0.10f, 0.62f, depthFactor) + f0 * 0.18f);
            float flicker = Mathf.Clamp01(Mathf.Lerp(0.08f, 0.55f, depthFactor) + f1 * 0.12f);
            float grime = Mathf.Clamp01(Mathf.Lerp(0.08f, 0.70f, depthFactor) + f2 * 0.20f);

            return new Level0Profile
            {
                humidity = humidity,
                flickerChance = flicker,
                wallToneShift = (f2 - 0.5f) * 0.08f,
                grime = grime,
                depthFactor = depthFactor,
                hasWaterStain = f3 > 0.42f || humidity > 0.28f,
                hasCeilingDrip = f3 > 0.72f || humidity > 0.48f,
                hasWallStain = f2 > 0.45f || grime > 0.32f,
                lightPattern = (int)(f1 * 4f) % 4,
                propVariant = (int)(f2 * 8f) % 8,
            };
        }
    }

    public sealed class ChunkRenderer : MonoBehaviour
    {
        [Header("Visuals")]
        public float chunkSize = 50f;
        // Phase 2.7B: wall top meets the ceiling panel cleanly (panel center at
        // ceilingHeight, 0.08 thick → underside 3.26). wallHeight == ceilingHeight
        // tucks the wall top just into the panel with no gap.
        public float wallHeight = 3.3f;
        public float ceilingHeight = 3.3f;

        [Header("Level 0 Backend-Driven Visuals")]
        public bool enableBackroomsDressing = true;
        public bool enableTemplateProps = true;
        public bool enableCeilingGrid = true;
        public bool useBackendLayout = true;
        public bool enableWorldCollision = true;
        public bool showLayoutDebug = false;
        public bool showCellDebug = false;
        public bool showCollisionDebug = false;
        // Phase 2.9B/C toggles.
        public bool enableChunkMeshBatching = true;
        public bool enableProceduralMaterialTiling = true;
        public bool enableCrossChunkZFightNudge = true;
        public bool showBatchDebug = false;
        public int maxLightsPerChunk = 8;
        public string worldCollisionLayerName = "WorldCollision";

        private readonly Dictionary<long, GameObject> _pool = new Dictionary<long, GameObject>();

        private Material _floorMat;
        private Material _ceilingMat;
        private Material _workbenchMat;
        private Material _storageMat;
        private Material _safeMat;
        private Material _dangerMat;
        private Material _trimMat;
        private Material _pillarMat;
        private Material _stainMat;
        private Material _darkStainMat;
        private Material _wetMat;
        private Material _blackMoldMat;
        private Material _boxMat;
        private Material _arrowMat;
        private Material _panelMat;
        private Material _baseboardMat;
        private Material _seamMat;
        private Material _ceilingSeamMat;
        private Material _overlitMat;
        private Material _darkWallMat;
        private Material _humidWallMat;
        private Material _redRoomMat;
        private Material _manilaMat;
        private Material _cleaningMat;
        private Material _warningMat;

        private float _nextSnapshotLogTime;
        private long _worldSeed;

        // Procedural tiling textures — generated once, shared by every chunk.
        private static Texture2D _wallpaperTex;
        private static Texture2D _carpetTex;
        private static Texture2D _ceilingTex;
        private const float PerimeterInset = 0.02f; // visual-only cross-chunk z-fight nudge

        private const float CellSize = 5f;
        private const int GridCells = 10;
        private const float WallThickness = 0.16f;
        private const float CorridorWidth = 5.2f;
        private const float DoorOpening = 3.0f;
        private const float FloorPanelSize = CellSize * 2f;
        // Phase 2.7B edge-architecture dimensions.
        private const float LowWallHeight = 1.0f;   // 0.9–1.1m
        private const float HalfWallHeight = 1.6f;  // 1.4–1.8m
        private const float PartitionThickness = 0.12f;
        private const float DoorPostWidth = 1.0f;   // each side post; leaves DoorOpening in a 5m cell
        private const float DoorPostHeight = 2.25f;
        private const float ArchPostHeight = 2.65f;
        private const float PillarWidth = 1.2f;     // 0.9–1.4m
        private const float BrokenWallHeight = 1.35f;
        private const float FalseDoorPanelInset = 0.04f;
        private const float LayerHeight = 7f;
        private const ushort CellWalkable = 1 << 0;
        private const ushort CellWall = 1 << 1;
        private const ushort CellPillar = 1 << 2;
        private const ushort CellBlocked = 1 << 3;
        private const ushort CellRamp = 1 << 5;
        private const ushort CellPit = 1 << 6;
        private const ushort CellShallowFluid = 1 << 7;
        private const ushort CellDoor = 1 << 10;
        private const ushort CellArch = 1 << 11;
        private const ushort CellLowWall = 1 << 12;
        private const ushort CellHalfWall = 1 << 13;
        private const ushort CellThinPartition = 1 << 14;
        private const ushort CellFalseDoor = 1 << 15;
        private const int FloorConnectorUp = 8;
        private const int FloorConnectorDown = 9;
        private const int V30AStackedCorridor = 1 << 8;
        private const int V30ALowerServiceBranch = 1 << 9;
        private const int V30AUpperOfficeBranch = 1 << 10;
        private const int V30AAtriumVoidRoom = 1 << 11;
        private const int V30ADeepPrecipicePlaceholder = 1 << 12;
        private const int V30AGiantPillarHall = 1 << 13;
        private const int V30AConnector = 1 << 14;
        private const int V30ABlockedVerticalShaft = 1 << 15;
        private const int EdgeNorth = 1 << 0;
        private const int EdgeEast = 1 << 1;
        private const int EdgeSouth = 1 << 2;
        private const int EdgeWest = 1 << 3;

        private static long Key(int x, int layer, int z)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                h = (h ^ x) * 1099511628211L;
                h = (h ^ layer) * 1099511628211L;
                h = (h ^ z) * 1099511628211L;
                return h;
            }
        }

        private static bool IsSpawnChunk(ChunkViewMsg cv) => cv.layer == 0 && cv.pos[0] == 0 && cv.pos[1] == 0;
        private static bool HasV30AFlag(ChunkViewMsg cv, int flag) => (cv.verticalFlags & flag) != 0;

        // Small deterministic hash for cheap, seed-stable visual variation.
        private static uint Hash2(int a, int b, long seed)
        {
            unchecked
            {
                uint h = (uint)seed ^ 0x9E3779B9u;
                h = (h ^ (uint)a) * 0x85EBCA77u;
                h ^= h >> 13;
                h = (h ^ (uint)b) * 0xC2B2AE3Du;
                h ^= h >> 16;
                return h;
            }
        }

        private void Start()
        {
            EnsureProceduralTextures();
            _floorMat = Lit(new Color(0.72f, 0.68f, 0.55f));
            _ceilingMat = Lit(new Color(0.88f, 0.86f, 0.80f));
            _workbenchMat = Lit(new Color(0.45f, 0.30f, 0.18f));

            _storageMat = Lit(new Color(0.42f, 0.41f, 0.33f));
            _safeMat = Lit(new Color(0.66f, 0.66f, 0.49f));
            _dangerMat = Lit(new Color(0.30f, 0.21f, 0.17f));
            _trimMat = Lit(new Color(0.28f, 0.25f, 0.18f));
            _pillarMat = Lit(new Color(0.58f, 0.54f, 0.41f));
            // Stains are translucent decals so they read as soft marks on the
            // carpet/wall, not opaque black "debug rectangles". 2.9A: softer
            // (lower alpha) and lifted off pure black.
            _stainMat = Stain(new Color(0.36f, 0.30f, 0.22f), 0.40f);
            _darkStainMat = Stain(new Color(0.20f, 0.18f, 0.15f), 0.40f);
            _wetMat = Stain(new Color(0.30f, 0.29f, 0.26f), 0.34f);
            _blackMoldMat = Stain(new Color(0.16f, 0.16f, 0.14f), 0.42f);
            _boxMat = Lit(new Color(0.38f, 0.31f, 0.21f));
            _arrowMat = Lit(new Color(0.18f, 0.14f, 0.09f));
            _panelMat = Lit(new Color(0.70f, 0.67f, 0.50f));
            _baseboardMat = Lit(new Color(0.38f, 0.33f, 0.22f));
            // 2.9A: low-contrast carpet grout (close to floor tone) + muted
            // near-ceiling drop-tile T-bar, so neither reads as a debug grid.
            _seamMat = Lit(new Color(0.66f, 0.62f, 0.48f));
            _ceilingSeamMat = Lit(new Color(0.80f, 0.78f, 0.70f));
            _overlitMat = Lit(new Color(0.90f, 0.86f, 0.60f));
            _darkWallMat = Lit(new Color(0.36f, 0.32f, 0.22f));
            _humidWallMat = Lit(new Color(0.56f, 0.53f, 0.38f));
            _redRoomMat = Lit(new Color(0.42f, 0.13f, 0.10f));
            _manilaMat = Lit(new Color(0.72f, 0.61f, 0.38f));
            _cleaningMat = Lit(new Color(0.50f, 0.54f, 0.42f));
            _warningMat = Lit(new Color(0.16f, 0.10f, 0.07f));

            if (Camera.main != null)
            {
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
                Camera.main.backgroundColor = new Color(0.030f, 0.030f, 0.038f);
            }

            // A low, slightly warm ambient floor so areas between fluorescent
            // fixtures stay readable without losing the oppressive feel.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.17f, 0.16f, 0.13f);
            RenderSettings.fog = false;
        }

        // Backrooms surfaces are matte drywall / carpet / acoustic tile. The
        // default Lit smoothness gives a plastic "made of cubes" sheen, so flatten
        // it. Keeps the existing MaterialHelper pipeline (URP Lit or Standard).
        private static Material Lit(Color color)
        {
            Material m = MaterialHelper.MakeLit(color);
            if (m == null)
                return m;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.04f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.04f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 0f);
            if (m.HasProperty("_GlossyReflections")) m.SetFloat("_GlossyReflections", 0f);
            return m;
        }

        // Translucent decal material for stains / grime / wet patches.
        private static Material Stain(Color color, float alpha)
        {
            color.a = Mathf.Clamp01(alpha);
            Material m = MaterialHelper.MakeTransparent(color);
            if (m == null)
                return m;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.05f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            return m;
        }

        private void LateUpdate()
        {
            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null)
                return;

            _worldSeed = state.worldSeed;

            if (Time.unscaledTime >= _nextSnapshotLogTime)
            {
                int backendLayoutChunks = 0;
                int fallbackChunks = 0;
                foreach (var cv in state.visibleChunks)
                {
                    if (useBackendLayout && HasBackendLayout(cv)) backendLayoutChunks++;
                    else fallbackChunks++;
                }

                Debug.Log($"MPTRACE step=AB event=unity_apply_world_snapshot revision={state.worldRevision} chunks={state.visibleChunks.Count} entities={state.visibleEntities.Count} items={state.visibleItems.Count} seed={state.worldSeed}");
                Debug.Log($"MPTRACE step=AR event=unity_structure_visual_counts chunks={_pool.Count} items={state.visibleItems.Count} entities={state.visibleEntities.Count}");
                Debug.Log($"MPTRACE step=V26 event=unity_level0_renderer_active backend_layout_chunks={backendLayoutChunks} fallback_chunks={fallbackChunks}");
                _nextSnapshotLogTime = Time.unscaledTime + 1f;
            }

            var alive = new HashSet<long>();

            foreach (var cv in state.visibleChunks)
            {
                long key = Key(cv.pos[0], cv.layer, cv.pos[1]);
                alive.Add(key);

                if (!_pool.ContainsKey(key))
                    _pool[key] = BuildChunk(cv);
            }

            var stale = new List<long>();
            foreach (var kv in _pool)
            {
                if (!alive.Contains(kv.Key))
                {
                    Destroy(kv.Value);
                    stale.Add(kv.Key);
                }
            }

            foreach (long k in stale)
                _pool.Remove(k);
        }

        private GameObject BuildChunk(ChunkViewMsg cv)
        {
            float layerY = Mathf.Abs(cv.layerY) > 0.001f ? cv.layerY : cv.layer * LayerHeight;
            var root = new GameObject($"Chunk_{cv.pos[0]}_{cv.layer}_{cv.pos[1]}");
            root.transform.position = new Vector3(cv.pos[0] * chunkSize, layerY, cv.pos[1] * chunkSize);

            Debug.Log($"MPTRACE step=AQ event=unity_chunk_template_applied chunk_id={Key(cv.pos[0], cv.layer, cv.pos[1])} template_id={cv.templateId} coord=({cv.pos[0]},{cv.layer},{cv.pos[1]}) rotation={cv.rotation}");

            var profile = Level0Profile.FromSeedAndPos(_worldSeed, cv.pos[0], cv.pos[1]);
            Material wallMat = WallMaterialFor(cv.templateId, profile);
            if (enableProceduralMaterialTiling)
                ApplyTex(wallMat, _wallpaperTex, 2.5f);
            bool edgeLayout = useBackendLayout && cv.HasEdgeLayout;
            var edgeCounts = new EdgeRenderCounts();

            CreateModularSurfaces(root.transform, cv, profile);
            CreateCeilingDetails(root.transform, cv.templateId, profile);

            if (edgeLayout)
            {
                // Phase 2.7B: architecture comes from backend cell edges. No
                // center-cell doors/arches, no blocked-cell full walls.
                edgeCounts = CreateEdgeArchitecture(root.transform, cv, wallMat);
                CreateBackendLayoutPillars(root.transform, cv, _pillarMat);
                CreateBackendCellDetails(root.transform, cv);
            }
            else
            {
                CreateTemplateWalls(root.transform, cv, wallMat);
                CreateInteriorLayout(root.transform, cv, profile, wallMat);
            }

            CreateWallGrime(root.transform, cv.templateId, profile);

            if (enableBackroomsDressing)
                CreateBackroomsDressing(root.transform, cv, profile, edgeLayout);

            if (enableTemplateProps && !edgeLayout &&
                !(useBackendLayout && HasBackendLayout(cv) && (cv.templateId == 9 || cv.templateId == 10)))
                CreateTemplateProps(root.transform, cv, profile);

            CreateLighting(root.transform, cv, profile);

            if (enableWorldCollision)
                CreateCollisionProxy(root.transform, cv, profile);

            if (cv.hasWorkbench)
            {
                CreateSlab(root.transform, "Workbench",
                    new Vector3(chunkSize * 0.5f, 0.5f, chunkSize * 0.5f),
                    new Vector3(2f, 1f, 1.2f),
                    _workbenchMat);
            }

            // Batch static visual slabs into per-material combined meshes BEFORE
            // tint, so the (rare) anchored/stabilized tint applies to the combined
            // renderers. Lights, collision proxy and dynamic objects are excluded.
            if (enableChunkMeshBatching)
                CombineChunkVisuals(root, cv);

            if (cv.state == "anchored")
                TintChunk(root, new Color(0.6f, 0.8f, 1f, 1f));
            else if (cv.state == "stabilized")
                TintChunk(root, new Color(0.8f, 1f, 0.8f, 1f));

            LogChunkRenderSummary(cv);
            LogEdgeChunkRenderSummary(cv, edgeLayout, edgeCounts);
            if (IsSpawnChunk(cv))
                LogSpawnChunkRendered(cv, edgeLayout, edgeCounts);

            // Phase 2.10: verticality is rendered from backend metadata only
            // (FloorOffsetFor raises/sinks the whole chunk floor; ramp/stair/pit
            // use cell markers). Log once per vertical chunk build.
            if (cv.floorProfile != 0 || cv.verticalFlags != 0)
            {
                int fp = cv.floorProfile;
                Debug.Log($"MPTRACE step=V210 event=unity_vertical_chunk_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) profile={fp} flags={cv.verticalFlags} raised={(fp == 2)} sunken={(fp == 1)} ramps={(fp == 3 || fp == 4 || fp == FloorConnectorUp || fp == FloorConnectorDown)} stairs={(fp == 6 || fp == 7)} pits={(fp == 5)} batched={enableChunkMeshBatching}");
            }

            if (cv.layer != 0 || (cv.verticalFlags & (V30AStackedCorridor | V30AAtriumVoidRoom | V30ADeepPrecipicePlaceholder | V30AGiantPillarHall | V30AConnector)) != 0)
            {
                Debug.Log($"MPTRACE step=V30A event=unity_multilayer_chunk_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) kind={V30AKind(cv)} layer_y={layerY:F2} batched={enableChunkMeshBatching}");
            }
            if (HasV30AFlag(cv, V30AConnector))
            {
                int targetLayer = cv.floorProfile == FloorConnectorUp ? cv.layer + 1 : cv.layer - 1;
                string kind = cv.floorProfile == FloorConnectorUp ? "broad_stairwell" : "service_ramp";
                Debug.Log($"MPTRACE step=V30A event=unity_connector_rendered from=({cv.pos[0]},{cv.layer},{cv.pos[1]}) to=({cv.pos[0]},{targetLayer},{cv.pos[1]}) kind={kind}");
            }

            return root;
        }

        private static string V30AKind(ChunkViewMsg cv)
        {
            if (HasV30AFlag(cv, V30AConnector)) return cv.floorProfile == FloorConnectorUp ? "broad_stairwell" : "service_ramp";
            if (HasV30AFlag(cv, V30AGiantPillarHall)) return "giant_pillar_hall";
            if (HasV30AFlag(cv, V30ADeepPrecipicePlaceholder)) return "deep_precipice_placeholder";
            if (HasV30AFlag(cv, V30AAtriumVoidRoom)) return "atrium_void_room";
            if (HasV30AFlag(cv, V30ALowerServiceBranch)) return "lower_service_branch";
            if (HasV30AFlag(cv, V30AUpperOfficeBranch)) return "upper_office_branch";
            if (HasV30AFlag(cv, V30AStackedCorridor)) return "stacked_corridor";
            return "layered_chunk";
        }

        private void LogChunkRenderSummary(ChunkViewMsg cv)
        {
            bool backendLayout = useBackendLayout && HasBackendLayout(cv);
            int walls = 0, doors = 0, arches = 0, lowWalls = 0, halfWalls = 0;
            int pillars = 0, falseDoors = 0, vertical = 0;

            if (backendLayout)
            {
                int grid = Mathf.Min(GridCells, cv.layoutGridSize);
                for (int x = 0; x < grid; x++)
                {
                    for (int z = 0; z < grid; z++)
                    {
                        int idx = z * cv.layoutGridSize + x;
                        if (idx < 0 || idx >= cv.layoutCells.Length) continue;
                        ushort f = cv.layoutCells[idx];
                        if ((f & (CellWall | CellBlocked)) != 0 && (f & CellWalkable) == 0) walls++;
                        if ((f & CellDoor) != 0) doors++;
                        if ((f & CellArch) != 0) arches++;
                        if ((f & CellLowWall) != 0) lowWalls++;
                        if ((f & CellHalfWall) != 0) halfWalls++;
                        if ((f & CellPillar) != 0) pillars++;
                        if ((f & CellFalseDoor) != 0) falseDoors++;
                        if ((f & (CellRamp | CellPit)) != 0) vertical++;
                    }
                }
            }

            bool spawnChunk = IsSpawnChunk(cv);
            Debug.Log($"MPTRACE step=V26 event=unity_chunk_render_summary chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) backend_layout={backendLayout} walls={walls} doors={doors} arches={arches} lowwalls={lowWalls} halfwalls={halfWalls} pillars={pillars} false_doors={falseDoors} lights={Mathf.Max(1, maxLightsPerChunk)} vertical={vertical} fallback={!backendLayout} spawn_chunk={spawnChunk}");
        }

        // ─────────────────────────────────────────────────────────────
        // Materials
        // ─────────────────────────────────────────────────────────────

        private Material FloorMaterialFor(int templateId, Level0Profile profile)
        {
            switch (templateId)
            {
                case 4: return _storageMat;
                case 5: return _safeMat;
                case 6: return Lit(new Color(0.28f, 0.25f, 0.16f));
                case 7: return Lit(new Color(0.22f, 0.18f, 0.14f));
                case 9:
                case 10: return Lit(new Color(0.58f, 0.54f, 0.42f));
                case 11: return Lit(new Color(0.64f, 0.59f, 0.41f));
                case 12: return Lit(new Color(0.43f, 0.45f, 0.34f));
                case 13: return Lit(new Color(0.40f, 0.38f, 0.28f));
                case 14: return Lit(new Color(0.18f, 0.16f, 0.11f));
                case 15: return _manilaMat;
                case 16: return _redRoomMat;
                case 17: return Lit(new Color(0.39f, 0.35f, 0.25f));
                default:
                    float h = profile.humidity;
                    float g = profile.grime;
                    return Lit(new Color(
                        Mathf.Clamp01(0.72f - h * 0.16f - g * 0.05f),
                        Mathf.Clamp01(0.68f - h * 0.12f - g * 0.04f),
                        Mathf.Clamp01(0.55f - h * 0.09f - g * 0.03f)
                    ));
            }
        }

        private Material WallMaterialFor(int templateId, Level0Profile profile)
        {
            if (templateId == 6) return Lit(new Color(0.48f, 0.44f, 0.28f));
            if (templateId == 7 || templateId == 14) return _darkWallMat;
            if (templateId == 5) return Lit(new Color(0.78f, 0.76f, 0.58f));
            if (templateId == 9 || templateId == 10) return Lit(new Color(0.66f, 0.62f, 0.46f));
            if (templateId == 11) return Lit(new Color(0.70f, 0.65f, 0.46f));
            if (templateId == 12) return Lit(new Color(0.54f, 0.57f, 0.43f));
            if (templateId == 13) return _humidWallMat;
            if (templateId == 15) return Lit(new Color(0.78f, 0.65f, 0.40f));
            if (templateId == 16) return Lit(new Color(0.34f, 0.10f, 0.08f));
            if (templateId == 17) return Lit(new Color(0.52f, 0.48f, 0.34f));

            // Sickly mono-yellow Backrooms wallpaper: high R/G, low B, gentle
            // per-chunk variation, and only mild darkening so walls never go muddy.
            // 2.9A: a touch more deterministic per-chunk variance so adjacent
            // chunks aren't a perfectly uniform yellow, while staying mono-yellow.
            float shift = profile.wallToneShift;
            float v = (profile.propVariant - 3.5f) * 0.010f; // ±~0.035, seed-stable
            float h = profile.humidity * 0.28f;
            float g = profile.grime * 0.14f;

            return Lit(new Color(
                Mathf.Clamp01(0.86f + shift + v - h * 0.35f - g),
                Mathf.Clamp01(0.80f + shift * 0.5f + v * 0.7f - h * 0.30f - g),
                Mathf.Clamp01(0.52f - v * 0.5f - h * 0.18f - g * 0.6f)
            ));
        }

        private Material CeilingMaterialFor(int templateId, Level0Profile profile)
        {
            if (templateId == 6 || templateId == 7 || templateId == 14)
                return Lit(new Color(0.42f, 0.39f, 0.28f));
            if (templateId == 16)
                return Lit(new Color(0.33f, 0.12f, 0.09f));
            if (templateId == 15)
                return Lit(new Color(0.72f, 0.62f, 0.40f));

            float h = profile.humidity * 0.15f;
            return Lit(new Color(0.88f - h, 0.86f - h, 0.80f - h));
        }

        // ─────────────────────────────────────────────────────────────
        // Dirt / ceiling / wall detail
        // ─────────────────────────────────────────────────────────────

        private void CreateModularSurfaces(Transform parent, ChunkViewMsg cv, Level0Profile profile)
        {
            int templateId = cv.templateId;
            Material floorMat = FloorMaterialFor(templateId, profile);
            Material ceilingMat = CeilingMaterialFor(templateId, profile);
            if (enableProceduralMaterialTiling)
            {
                ApplyTex(floorMat, _carpetTex, 4f);
                ApplyTex(ceilingMat, _ceilingTex, 4f);
            }
            float floorOffset = FloorOffsetFor(cv);
            int floorProfile = cv.floorProfile;
            // Ramp (3/4) and stairs (6/7) need a sloped/stepped floor surface;
            // raised (2)/sunken (1) already shift the whole-chunk floorOffset.
            bool slopedFloor = floorProfile == 3 || floorProfile == 4 || floorProfile == 6 || floorProfile == 7 ||
                                floorProfile == FloorConnectorUp || floorProfile == FloorConnectorDown;
            bool floorOpening = HasFloorOpening(cv);
            bool ceilingOpening = HasCeilingOpening(cv);

            // Solid base planes behind the tile panels so the dark void never
            // shows through the seams as a "black grid". The panels sit slightly
            // proud, leaving a subtle recessed grout line instead of a gap.
            if (floorOpening)
            {
                CreateRingSurface(parent, "FloorBase", floorOffset - 0.085f, 0.10f, floorMat);
            }
            else
            {
                CreateSlab(parent, "FloorBase",
                    new Vector3(chunkSize * 0.5f, floorOffset - 0.085f, chunkSize * 0.5f),
                    new Vector3(chunkSize, 0.10f, chunkSize),
                    floorMat);
            }

            if (ceilingOpening)
            {
                CreateRingSurface(parent, "CeilingBase", ceilingHeight + 0.05f, 0.08f, ceilingMat);
            }
            else
            {
                CreateSlab(parent, "CeilingBase",
                    new Vector3(chunkSize * 0.5f, ceilingHeight + 0.05f, chunkSize * 0.5f),
                    new Vector3(chunkSize, 0.08f, chunkSize),
                    ceilingMat);
            }

            if (floorOpening)
                CreateAtriumVolume(parent, cv, floorOffset);

            for (int x = 0; x < GridCells; x += 2)
            {
                for (int z = 0; z < GridCells; z += 2)
                {
                    float cx = (x + 1f) * CellSize;
                    float cz = (z + 1f) * CellSize;

                    bool floorInOpening = floorOpening && IsOpeningPanel(x, z);
                    bool ceilingInOpening = ceilingOpening && IsOpeningPanel(x, z);
                    if (!slopedFloor && !floorInOpening)
                        CreateSlab(parent, "FloorPanel",
                            new Vector3(cx, floorOffset - 0.045f, cz),
                            new Vector3(FloorPanelSize - 0.05f, 0.09f, FloorPanelSize - 0.05f),
                            floorMat);

                    // 2.9C2: rare deterministic missing/damaged ceiling tile —
                    // a darker recessed panel into the plenum (over the base plane).
                    bool missingTile = (Hash2(x + cv.pos[0] * 31, z + cv.pos[1] * 17, _worldSeed) % 23u) == 0u;
                    if (!ceilingInOpening)
                    {
                        CreateSlab(parent, missingTile ? "CeilingTileMissing" : "CeilingPanel",
                            new Vector3(cx, missingTile ? ceilingHeight + 0.045f : ceilingHeight, cz),
                            new Vector3(FloorPanelSize - 0.08f, 0.08f, FloorPanelSize - 0.08f),
                            missingTile ? _darkStainMat : ceilingMat);
                    }
                }
            }

            if (enableCeilingGrid)
            {
                for (int i = 1; i < GridCells; i++)
                {
                    float p = i * CellSize;

                    // Carpet grout only on the 10m panel boundaries (i = 2,4,6,8),
                    // low-contrast + thin → continuous carpet, not a board grid.
                    // Skipped on vertical chunks where a flat seam would be misplaced.
                    if (i % 2 == 0 && floorProfile == 0)
                    {
                        CreateSlab(parent, "FloorSeam_X",
                            new Vector3(p, 0.014f, chunkSize * 0.5f),
                            new Vector3(0.014f, 0.008f, chunkSize),
                            _seamMat);
                        CreateSlab(parent, "FloorSeam_Z",
                            new Vector3(chunkSize * 0.5f, 0.015f, p),
                            new Vector3(chunkSize, 0.008f, 0.014f),
                            _seamMat);
                    }

                    // Ceiling tile rhythm kept on every cell but muted (near-ceiling
                    // tone) + thin so it reads as a drop ceiling, not a debug grid.
                    CreateSlab(parent, "CeilingGrid_X",
                        new Vector3(p, ceilingHeight - 0.090f, chunkSize * 0.5f),
                        new Vector3(0.022f, 0.014f, chunkSize),
                        _ceilingSeamMat);
                    CreateSlab(parent, "CeilingGrid_Z",
                        new Vector3(chunkSize * 0.5f, ceilingHeight - 0.091f, p),
                        new Vector3(chunkSize, 0.014f, 0.022f),
                        _ceilingSeamMat);
                }
            }

            // Phase 2.10B: build the visible vertical floor surface.
            if (slopedFloor)
                BuildSlopedFloor(parent, floorProfile, floorOffset, floorMat);
            if (floorProfile == FloorConnectorUp || floorProfile == FloorConnectorDown)
                CreateConnectorVolume(parent, cv, floorOffset);
            if (floorProfile == 1 || floorProfile == 2)
                BuildFloorRim(parent, floorOffset);
            CreateV30AMacroVisuals(parent, cv, floorOffset);

            if (floorProfile != 0 || cv.verticalFlags != 0)
            {
                Debug.Log($"MPTRACE step=V210B event=unity_vertical_geometry_built chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) profile={floorProfile} raised={(floorProfile == 2)} sunken={(floorProfile == 1)} ramps={(floorProfile == 3 || floorProfile == 4 || floorProfile == FloorConnectorUp || floorProfile == FloorConnectorDown)} stairs={(floorProfile == 6 || floorProfile == 7)} pits={(floorProfile == 5)} batched={enableChunkMeshBatching}");
            }

            CreateFloorStains(parent, templateId, profile);
        }

        // Broad stepped/sloped floor matching backend floor_player_y:
        //   ramp:  base + (local/CHUNK)*0.5   (10 broad steps, ~0.05m each)
        //   stairs: base + floor(local/10)/5 * 0.6  (5 shallow steps of 0.12m)
        // Built as full-width slabs of floorMat so mesh batching still groups them.
        private void BuildSlopedFloor(Transform parent, int floorProfile, float baseOffset, Material floorMat)
        {
            bool connector = floorProfile == FloorConnectorUp || floorProfile == FloorConnectorDown;
            bool ns = floorProfile == 3 || floorProfile == 6 || connector;   // varies along Z
            bool stairs = floorProfile == 6 || floorProfile == 7;
            int segments = stairs ? 5 : GridCells;
            float segLen = chunkSize / segments;

            for (int s = 0; s < segments; s++)
            {
                float center = (s + 0.5f) * segLen;
                float t = center / chunkSize;
                float surfY = stairs
                    ? baseOffset + (s / 5f) * 0.6f
                    : connector
                        ? baseOffset + (floorProfile == FloorConnectorUp ? t * LayerHeight : -t * LayerHeight)
                        : baseOffset + t * 0.5f;
                Vector3 pos = ns
                    ? new Vector3(chunkSize * 0.5f, surfY - 0.045f, center)
                    : new Vector3(center, surfY - 0.045f, chunkSize * 0.5f);
                Vector3 scale = ns
                    ? new Vector3(chunkSize, 0.09f, segLen)
                    : new Vector3(segLen, 0.09f, chunkSize);
                CreateSlab(parent, connector ? "LayerConnectorStep" : "VerticalFloorStep", pos, scale, floorMat);
            }
        }

        private void CreateConnectorVolume(Transform parent, ChunkViewMsg cv, float baseOffset)
        {
            bool up = cv.floorProfile == FloorConnectorUp;
            int targetLayer = up ? cv.layer + 1 : cv.layer - 1;
            string kind = up ? "broad_stairwell" : "service_ramp";
            float y0 = baseOffset;
            float y1 = baseOffset + (up ? LayerHeight : -LayerHeight);
            const int steps = GridCells;
            float segLen = chunkSize / steps;
            float railInset = CellSize * 1.15f;
            Material railMat = up ? _baseboardMat : _darkWallMat;
            Material riserMat = up ? _trimMat : _humidWallMat;

            CreateSlab(parent, "ConnectorLanding_Start",
                new Vector3(chunkSize * 0.5f, y0 + 0.02f, segLen * 0.45f),
                new Vector3(chunkSize - railInset * 2f, 0.14f, segLen * 0.90f),
                up ? _floorMat : _darkStainMat);
            CreateSlab(parent, "ConnectorLanding_End",
                new Vector3(chunkSize * 0.5f, y1 + 0.02f, chunkSize - segLen * 0.45f),
                new Vector3(chunkSize - railInset * 2f, 0.14f, segLen * 0.90f),
                up ? _floorMat : _darkStainMat);

            for (int s = 0; s < steps; s++)
            {
                float z = (s + 0.5f) * segLen;
                float t = z / chunkSize;
                float y = baseOffset + (up ? t * LayerHeight : -t * LayerHeight);
                float railY = y + 0.65f;
                CreateSlab(parent, "ConnectorRail_L",
                    new Vector3(railInset, railY, z),
                    new Vector3(0.28f, 1.25f, segLen * 0.92f),
                    railMat);
                CreateSlab(parent, "ConnectorRail_R",
                    new Vector3(chunkSize - railInset, railY, z),
                    new Vector3(0.28f, 1.25f, segLen * 0.92f),
                    railMat);

                if (s > 0)
                {
                    float previousT = (s * segLen) / chunkSize;
                    float previousY = baseOffset + (up ? previousT * LayerHeight : -previousT * LayerHeight);
                    float riserY = (previousY + y) * 0.5f;
                    float riserHeight = Mathf.Max(0.16f, Mathf.Abs(y - previousY));
                    CreateSlab(parent, "ConnectorStepRiser",
                        new Vector3(chunkSize * 0.5f, riserY, s * segLen),
                        new Vector3(chunkSize - railInset * 2f, riserHeight, 0.16f),
                        riserMat);
                }
            }

            CreateSlab(parent, "ConnectorOverheadCue",
                new Vector3(chunkSize * 0.5f, Mathf.Max(y0, y1) + 0.55f, chunkSize * 0.5f),
                new Vector3(chunkSize - railInset * 2.2f, 0.12f, 0.34f),
                _ceilingSeamMat);

            Debug.Log($"MPTRACE step=V30AFIX event=connector_volume_built from=({cv.pos[0]},{cv.layer},{cv.pos[1]}) to_layer={targetLayer} kind={kind} y0={y0:F1} y1={y1:F1} steps={steps}");
        }

        // Thin perimeter rim so a raised/sunken room reads as a deliberate
        // platform/pit edge rather than a floating floor.
        private void BuildFloorRim(Transform parent, float floorOffset)
        {
            float y = floorOffset + 0.02f;
            float half = chunkSize * 0.5f;
            const float t = 0.18f, h = 0.10f;
            CreateSlab(parent, "FloorRim_N", new Vector3(half, y, 0.12f), new Vector3(chunkSize, h, t), _trimMat);
            CreateSlab(parent, "FloorRim_S", new Vector3(half, y, chunkSize - 0.12f), new Vector3(chunkSize, h, t), _trimMat);
            CreateSlab(parent, "FloorRim_W", new Vector3(0.12f, y, half), new Vector3(t, h, chunkSize), _trimMat);
            CreateSlab(parent, "FloorRim_E", new Vector3(chunkSize - 0.12f, y, half), new Vector3(t, h, chunkSize), _trimMat);
        }

        private static bool HasVerticalOpening(ChunkViewMsg cv) =>
            HasV30AFlag(cv, V30AAtriumVoidRoom) ||
            HasV30AFlag(cv, V30ADeepPrecipicePlaceholder) ||
            HasV30AFlag(cv, V30ABlockedVerticalShaft);

        private static bool HasFloorOpening(ChunkViewMsg cv) => HasVerticalOpening(cv);

        private static bool HasCeilingOpening(ChunkViewMsg cv) =>
            HasVerticalOpening(cv) ||
            (cv.layer < 0 && HasV30AFlag(cv, V30AGiantPillarHall));

        private static float LayerRootY(ChunkViewMsg cv) =>
            Mathf.Abs(cv.layerY) > 0.001f ? cv.layerY : cv.layer * LayerHeight;

        private static bool IsOpeningPanel(int x, int z) => x >= 2 && x <= 6 && z >= 2 && z <= 6;

        private void CreateRingSurface(Transform parent, string name, float y, float height, Material mat)
        {
            float side = CellSize * 3f;
            float opening = chunkSize - side * 2f;
            float half = chunkSize * 0.5f;
            CreateSlab(parent, name + "_N", new Vector3(half, y, side * 0.5f), new Vector3(chunkSize, height, side), mat);
            CreateSlab(parent, name + "_S", new Vector3(half, y, chunkSize - side * 0.5f), new Vector3(chunkSize, height, side), mat);
            CreateSlab(parent, name + "_W", new Vector3(side * 0.5f, y, half), new Vector3(side, height, opening), mat);
            CreateSlab(parent, name + "_E", new Vector3(chunkSize - side * 0.5f, y, half), new Vector3(side, height, opening), mat);
        }

        private void CreateAtriumVolume(Transform parent, ChunkViewMsg cv, float floorOffset)
        {
            float min = CellSize * 3f;
            float max = CellSize * 7f;
            float center = (min + max) * 0.5f;
            float span = max - min;
            float shaftHeight = LayerHeight;
            float shaftCenterY = cv.layer >= 0 ? floorOffset - shaftHeight * 0.5f : floorOffset + shaftHeight * 0.5f;
            float rimY = floorOffset + 0.04f;
            float railY = floorOffset + 0.58f;
            float depthY = cv.layer >= 0 ? floorOffset - shaftHeight + 0.08f : floorOffset + shaftHeight - 0.08f;
            Material shaftMat = HasV30AFlag(cv, V30ADeepPrecipicePlaceholder) ? _darkWallMat : _humidWallMat;

            CreateSlab(parent, "AtriumShaftWall_N", new Vector3(center, shaftCenterY, min), new Vector3(span + 0.2f, shaftHeight, 0.24f), shaftMat);
            CreateSlab(parent, "AtriumShaftWall_S", new Vector3(center, shaftCenterY, max), new Vector3(span + 0.2f, shaftHeight, 0.24f), shaftMat);
            CreateSlab(parent, "AtriumShaftWall_W", new Vector3(min, shaftCenterY, center), new Vector3(0.24f, shaftHeight, span + 0.2f), shaftMat);
            CreateSlab(parent, "AtriumShaftWall_E", new Vector3(max, shaftCenterY, center), new Vector3(0.24f, shaftHeight, span + 0.2f), shaftMat);

            CreateSlab(parent, "AtriumRim_N", new Vector3(center, rimY, min - 0.18f), new Vector3(span + 0.8f, 0.12f, 0.36f), _baseboardMat);
            CreateSlab(parent, "AtriumRim_S", new Vector3(center, rimY, max + 0.18f), new Vector3(span + 0.8f, 0.12f, 0.36f), _baseboardMat);
            CreateSlab(parent, "AtriumRim_W", new Vector3(min - 0.18f, rimY, center), new Vector3(0.36f, 0.12f, span + 0.8f), _baseboardMat);
            CreateSlab(parent, "AtriumRim_E", new Vector3(max + 0.18f, rimY, center), new Vector3(0.36f, 0.12f, span + 0.8f), _baseboardMat);

            CreateSlab(parent, "AtriumRail_N", new Vector3(center, railY, min - 0.35f), new Vector3(span + 0.5f, 1.16f, 0.24f), _trimMat);
            CreateSlab(parent, "AtriumRail_S", new Vector3(center, railY, max + 0.35f), new Vector3(span + 0.5f, 1.16f, 0.24f), _trimMat);
            CreateSlab(parent, "AtriumRail_W", new Vector3(min - 0.35f, railY, center), new Vector3(0.24f, 1.16f, span + 0.5f), _trimMat);
            CreateSlab(parent, "AtriumRail_E", new Vector3(max + 0.35f, railY, center), new Vector3(0.24f, 1.16f, span + 0.5f), _trimMat);

            CreateSlab(parent, "AtriumDepthGlow", new Vector3(center, depthY, center), new Vector3(span - 1.2f, 0.06f, span - 1.2f), _darkStainMat);
            CreateSlab(parent, "AtriumDepthPatch", new Vector3(center - 2.6f, depthY + 0.08f, center + 2.2f), new Vector3(span * 0.45f, 0.035f, span * 0.32f), _stainMat);

            bool lowerVisible = cv.layer >= 0 && !HasV30AFlag(cv, V30ABlockedVerticalShaft);
            Debug.Log($"MPTRACE step=V30AFIX event=atrium_volume_built chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) opening=({span:F0},{span:F0}) shaft_height={shaftHeight:F1} rails=4 lower_visible={lowerVisible}");
        }

        private void CreateV30AMacroVisuals(Transform parent, ChunkViewMsg cv, float floorOffset)
        {
            if (HasV30AFlag(cv, V30AGiantPillarHall))
            {
                float h = LayerHeight + ceilingHeight;
                int pillarCount = 0;
                foreach (var p in new[] { new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.25f), new Vector2(0.25f, 0.75f), new Vector2(0.75f, 0.75f) })
                {
                    Vector3 basePos = new Vector3(chunkSize * p.x, floorOffset + 0.08f, chunkSize * p.y);
                    Vector3 topPos = new Vector3(chunkSize * p.x, floorOffset + h - 0.08f, chunkSize * p.y);
                    CreateSlab(parent, "GiantPillar",
                        new Vector3(chunkSize * p.x, floorOffset + h * 0.5f, chunkSize * p.y),
                        new Vector3(2.4f, h, 2.4f),
                        _pillarMat);
                    CreateSlab(parent, "GiantPillarBase", basePos, new Vector3(3.6f, 0.28f, 3.6f), _baseboardMat);
                    CreateSlab(parent, "GiantPillarCap", topPos, new Vector3(3.8f, 0.30f, 3.8f), _baseboardMat);
                    pillarCount++;
                }
                Debug.Log($"MPTRACE step=V30AFIX event=giant_pillars_built chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) count={pillarCount} height={h:F1}");
            }

            string branchType = null;
            if (HasV30AFlag(cv, V30ALowerServiceBranch))
            {
                branchType = "lower_service_branch";
                CreateSlab(parent, "LowerServiceFloorGrime", new Vector3(chunkSize * 0.5f, floorOffset + 0.018f, chunkSize * 0.5f), new Vector3(chunkSize * 0.82f, 0.035f, chunkSize * 0.72f), _darkStainMat);
                CreateSlab(parent, "LowerServiceLowCeiling_A", new Vector3(chunkSize * 0.5f, ceilingHeight - 0.34f, chunkSize * 0.28f), new Vector3(chunkSize * 0.78f, 0.20f, 0.34f), _darkWallMat);
                CreateSlab(parent, "LowerServiceLowCeiling_B", new Vector3(chunkSize * 0.5f, ceilingHeight - 0.38f, chunkSize * 0.72f), new Vector3(chunkSize * 0.68f, 0.18f, 0.30f), _darkWallMat);
                CreateSlab(parent, "ServicePipe_A", new Vector3(chunkSize * 0.5f, ceilingHeight - 0.65f, chunkSize * 0.22f), new Vector3(chunkSize * 0.70f, 0.18f, 0.18f), _trimMat);
                CreateSlab(parent, "ServicePipe_B", new Vector3(chunkSize * 0.35f, ceilingHeight - 0.95f, chunkSize * 0.72f), new Vector3(0.18f, 0.18f, chunkSize * 0.55f), _trimMat);
                CreateSlab(parent, "ServicePipe_C", new Vector3(chunkSize * 0.70f, ceilingHeight - 1.15f, chunkSize * 0.55f), new Vector3(0.16f, 0.16f, chunkSize * 0.40f), _humidWallMat);
                CreateSlab(parent, "LowerServicePanel", new Vector3(chunkSize - 0.16f, wallHeight * 0.46f, chunkSize * 0.48f), new Vector3(0.10f, 1.8f, 5.8f), _darkWallMat);
            }

            if (HasV30AFlag(cv, V30AUpperOfficeBranch))
            {
                branchType = "upper_office_branch";
                CreateSlab(parent, "UpperBranchCleanFloor", new Vector3(chunkSize * 0.5f, floorOffset + 0.018f, chunkSize * 0.5f), new Vector3(chunkSize * 0.76f, 0.035f, chunkSize * 0.68f), _manilaMat);
                CreateSlab(parent, "UpperBranchRail", new Vector3(chunkSize * 0.5f, floorOffset + 0.68f, chunkSize - 0.28f), new Vector3(chunkSize * 0.70f, 1.25f, 0.24f), _trimMat);
                CreateSlab(parent, "UpperBranchOverlookTrim", new Vector3(chunkSize * 0.5f, floorOffset + 0.08f, chunkSize - 0.80f), new Vector3(chunkSize * 0.72f, 0.16f, 0.36f), _baseboardMat);
                CreateSlab(parent, "UpperOfficeWindowBand", new Vector3(0.18f, wallHeight * 0.60f, chunkSize * 0.5f), new Vector3(0.10f, 0.85f, chunkSize * 0.55f), _ceilingMat);
            }

            if (HasV30AFlag(cv, V30AStackedCorridor))
            {
                branchType = branchType ?? "stacked_corridor";
                CreateSlab(parent, "StackedCorridorShadowTrim_L", new Vector3(CellSize * 1.05f, floorOffset + 0.06f, chunkSize * 0.5f), new Vector3(0.32f, 0.12f, chunkSize * 0.78f), _baseboardMat);
                CreateSlab(parent, "StackedCorridorShadowTrim_R", new Vector3(chunkSize - CellSize * 1.05f, floorOffset + 0.06f, chunkSize * 0.5f), new Vector3(0.32f, 0.12f, chunkSize * 0.78f), _baseboardMat);
                CreateSlab(parent, "StackedCorridorCableTray", new Vector3(chunkSize * 0.5f, ceilingHeight - 0.52f, chunkSize * 0.5f), new Vector3(chunkSize * 0.62f, 0.16f, 0.28f), _trimMat);
            }

            if (branchType != null)
            {
                Debug.Log($"MPTRACE step=V30AFIX event=branch_layer_style_applied chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) branch_type={branchType} layer_y={LayerRootY(cv):F2}");
            }
        }

        private void CreateFloorStains(Transform parent, int templateId, Level0Profile profile)
        {
            bool forceDirty = templateId == 4 || templateId == 6 || templateId == 7 ||
                              templateId == 9 || templateId == 12 || templateId == 13 ||
                              templateId == 14 || templateId == 16 || templateId == 17;

            if (profile.hasWaterStain || profile.humidity > 0.22f || forceDirty)
            {
            CreateSlab(parent, "LargeWaterStain_A",
                    new Vector3(chunkSize * (0.30f + profile.humidity * 0.35f), 0.024f, chunkSize * 0.35f),
                    new Vector3(8f + profile.humidity * 12f, 0.012f, 6f + profile.humidity * 10f),
                    templateId == 14 ? _darkStainMat : _wetMat);

                CreateSlab(parent, "LargeWaterStain_B",
                    new Vector3(chunkSize * 0.65f, 0.026f, chunkSize * 0.58f),
                    new Vector3(5f + profile.humidity * 8f, 0.012f, 4f + profile.humidity * 7f),
                    _stainMat);
            }

            if (templateId == 7 || templateId == 14)
            {
                // Smaller, soft damp pool instead of a big near-black square plane.
                CreateSlab(parent, "DarkPool",
                    new Vector3(chunkSize * 0.5f, 0.030f, chunkSize * 0.5f),
                    new Vector3(8.5f, 0.012f, 7f),
                    _darkStainMat);
            }

            if (templateId == 16)
            {
                CreateSlab(parent, "RedRoomCarpetBurn",
                    new Vector3(chunkSize * 0.5f, 0.032f, chunkSize * 0.5f),
                    new Vector3(10f, 0.012f, 8f),
                    _redRoomMat);
            }
        }

        private void CreateCeilingDetails(Transform parent, int templateId, Level0Profile profile)
        {
            bool forceDrip = templateId == 6 || templateId == 7 || templateId == 12 ||
                              templateId == 13 || templateId == 14 || profile.hasCeilingDrip;

            if (!forceDrip)
                return;

            CreateSlab(parent, "CeilingDripLarge",
                new Vector3(chunkSize * (0.2f + profile.humidity * 0.6f), ceilingHeight - 0.085f, chunkSize * 0.35f),
                new Vector3(3.5f, 0.018f, 2.5f),
                _stainMat);

            CreateSlab(parent, "CeilingPanelMissingHint",
                new Vector3(chunkSize * 0.72f, ceilingHeight - 0.082f, chunkSize * 0.25f),
                new Vector3(2.2f, 0.018f, 2.2f),
                _darkStainMat);
        }

        private void CreateWallGrime(Transform parent, int templateId, Level0Profile profile)
        {
            bool force = templateId == 4 || templateId == 6 || templateId == 7 ||
                         templateId == 9 || templateId == 12 || templateId == 13 ||
                         templateId == 14 || templateId == 16 || templateId == 17;

            if (!profile.hasWallStain && !force)
                return;

            Material mat = (templateId == 7 || templateId == 14 || templateId == 16) ? _darkStainMat : _stainMat;

            CreateSlab(parent, "WallGrime_N",
                new Vector3(chunkSize * 0.35f, wallHeight * 0.42f, 0.11f),
                new Vector3(8f + profile.grime * 8f, 1.4f + profile.humidity * 1.8f, 0.04f),
                mat);

            CreateSlab(parent, "WallGrime_E",
                new Vector3(chunkSize - 0.11f, wallHeight * 0.35f, chunkSize * 0.65f),
                new Vector3(0.04f, 1.2f + profile.humidity * 2.0f, 7f + profile.grime * 6f),
                mat);
        }

        // ─────────────────────────────────────────────────────────────
        // Lighting
        // ─────────────────────────────────────────────────────────────

        private void CreateLighting(Transform parent, ChunkViewMsg cv, Level0Profile profile)
        {
            int templateId = cv.templateId;
            int cap = Mathf.Clamp(maxLightsPerChunk, 1, 10);
            // Only skip cells when the backend layout tells us they're not floor;
            // fallback chunks light the whole grid as before.
            bool walkAware = cv.HasBackendLayout;
            float lightY = ceilingHeight - 0.20f; // hangs below the ceiling tile, no clip

            // Fixtures snap to cell centres on a sparse rhythm aligned to the
            // ceiling tile grid (≈15m spacing, range 16 → readable overlap).
            // 2.9A: stagger the grid per chunk so lighting never reads as one
            // perfect repeating pattern across the level.
            int stagger = (int)(Hash2(cv.pos[0], cv.pos[1], _worldSeed) & 1u);
            int[] coords = stagger == 0 ? new[] { 1, 4, 7 } : new[] { 2, 5, 8 };
            int placed = 0;
            int idx = 0;
            foreach (int cellX in coords)
            {
                foreach (int cellZ in coords)
                {
                    if (placed >= cap)
                        break;
                    if (walkAware && !IsCellWalkable(cv.GetCell(cellX, cellZ)))
                    {
                        idx++;
                        continue;
                    }
                    float lx = (cellX + 0.5f) * CellSize;
                    float lz = (cellZ + 0.5f) * CellSize;
                    bool affected = IsLightAffected(idx, profile, templateId);
                    // Deterministic irregular dead fixtures (~25%) instead of a
                    // regular checkerboard. Manila/red keep all fixtures lit.
                    uint hc = Hash2(cellX * 7 + cv.pos[0], cellZ * 11 + cv.pos[1], _worldSeed);
                    bool unpoweredFixture = templateId != 15 && templateId != 16 && (hc % 4u == 0u);
                    CreateLight(parent, new Vector3(lx, lightY, lz), profile, affected, templateId, unpoweredFixture);
                    placed++;
                    idx++;
                }
                if (placed >= cap)
                    break;
            }

            // Never leave a walkable chunk pitch black (e.g. all candidate cells blocked).
            if (placed == 0)
            {
                CreateLight(parent, new Vector3(chunkSize * 0.5f, lightY, chunkSize * 0.5f), profile, false, templateId, false);
                placed++;
            }

            bool wantsLongFixture = templateId == 0 || templateId == 3 || templateId == 9 ||
                                    templateId == 10 || templateId == 11 || templateId == 15 ||
                                    templateId == 17;
            if (placed < cap && wantsLongFixture)
            {
                CreateLongFluorescent(parent,
                    new Vector3(chunkSize * 0.5f, ceilingHeight - 0.14f, chunkSize * 0.5f),
                    templateId == 15 ? 0.55f : 0.9f,
                    templateId == 15 ? new Color(1f, 0.72f, 0.34f) : new Color(1f, 0.95f, 0.64f));
            }
        }

        private static bool IsLightAffected(int lightIdx, Level0Profile profile, int templateId)
        {
            if (templateId == 6 || templateId == 7 || templateId == 12 || templateId == 13 ||
                templateId == 14 || templateId == 16 || templateId == 17)
                return true;

            if (templateId == 5 || templateId == 11 || templateId == 15)
                return false;

            switch (profile.lightPattern)
            {
                case 0: return lightIdx == 2 || lightIdx == 4;
                case 1: return lightIdx == 0 || lightIdx == 5;
                case 2: return lightIdx == 1 || lightIdx == 3;
                case 3: return lightIdx == 3 || lightIdx == 5;
                default: return false;
            }
        }

        private void CreateLight(Transform parent, Vector3 pos, Level0Profile profile, bool isAffected, int templateId, bool forceOff)
        {
            var lightObj = new GameObject("CeilingLight");
            lightObj.transform.SetParent(parent, false);
            lightObj.transform.localPosition = pos;

            float flickerRoll = isAffected ? profile.flickerChance : 0f;
            if (templateId == 14) flickerRoll += 0.75f;
            else if (templateId == 7 || templateId == 16) flickerRoll += 0.30f;
            else if (templateId == 6 || templateId == 13) flickerRoll += 0.18f;
            else if (templateId == 5 || templateId == 15) flickerRoll *= 0.20f;

            if (templateId == 14 && flickerRoll < 0.82f)
                flickerRoll = 0.82f;

            bool isOff = forceOff || flickerRoll > 0.24f;
            bool isDim = !isOff && flickerRoll > 0.08f;

            Color fixtureColor;
            float fixtureEmission;

            if (isOff)
            {
                fixtureColor = new Color(0.20f, 0.18f, 0.13f);
                fixtureEmission = 0f;
            }
            else if (isDim)
            {
                fixtureColor = new Color(0.70f, 0.62f, 0.38f);
                fixtureEmission = 0.12f;
            }
            else
            {
                fixtureColor = templateId == 15
                    ? new Color(1f, 0.72f, 0.34f)
                    : templateId == 16
                        ? new Color(0.95f, 0.20f, 0.14f)
                    : new Color(1f, 0.95f, 0.66f);
                // Lower self-emission so the fixture reads as a fluorescent tube,
                // not a blown-out white block.
                fixtureEmission = templateId == 15 ? 0.42f : templateId == 16 ? 0.5f : 0.55f;
            }

            var fixture = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fixture.transform.SetParent(lightObj.transform, false);
            fixture.transform.localScale = new Vector3(1.65f, 0.045f, 0.22f);
            fixture.GetComponent<Renderer>().sharedMaterial =
                fixtureEmission > 0f
                    ? MaterialHelper.MakeEmissive(fixtureColor, fixtureEmission)
                    : Lit(fixtureColor);
            Destroy(fixture.GetComponent<Collider>());

            if (!isOff)
            {
                var light = lightObj.AddComponent<Light>();
                light.type = LightType.Point;
                light.shadows = LightShadows.None;

                if (isDim)
                {
                    light.color = new Color(0.9f, 0.78f, 0.42f);
                    light.intensity = 0.18f;
                    light.range = 6f;
                }
                else
                {
                    light.color = templateId == 15
                        ? new Color(1f, 0.64f, 0.30f)
                        : templateId == 16
                            ? new Color(1f, 0.12f, 0.08f)
                            : new Color(1f, 0.90f, 0.55f);
                    light.intensity = templateId == 15 ? 0.60f : templateId == 16 ? 0.80f : 1.0f;
                    light.range = templateId == 15 ? 12f : templateId == 16 ? 10f : 16f;
                }
            }
        }

        private void CreateLongFluorescent(Transform parent, Vector3 pos, float intensity, Color fixtureColor)
        {
            var lightObj = new GameObject("LongFluorescent");
            lightObj.transform.SetParent(parent, false);
            lightObj.transform.localPosition = pos;

            var fixture = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fixture.transform.SetParent(lightObj.transform, false);
            fixture.transform.localScale = new Vector3(5.8f, 0.045f, 0.24f);
            fixture.GetComponent<Renderer>().sharedMaterial =
                MaterialHelper.MakeEmissive(fixtureColor, Mathf.Max(0.35f, intensity * 0.9f));
            Destroy(fixture.GetComponent<Collider>());

            var light = lightObj.AddComponent<Light>();
            light.type = LightType.Point;
            light.shadows = LightShadows.None;
            light.color = fixtureColor;
            light.intensity = intensity;
            light.range = 16f;
        }

        // ─────────────────────────────────────────────────────────────
        // Template geometry
        // ─────────────────────────────────────────────────────────────

        private void CreateTemplateWalls(Transform parent, ChunkViewMsg cv, Material wallMat)
        {
            var openings = OpeningsFor(cv);
            CreateWallWithOpening(parent, "WallN", 0, openings.north, wallMat);
            CreateWallWithOpening(parent, "WallS", 2, openings.south, wallMat);
            CreateWallWithOpening(parent, "WallW", 3, openings.west, wallMat);
            CreateWallWithOpening(parent, "WallE", 1, openings.east, wallMat);
        }

        private void CreateInteriorLayout(Transform parent, ChunkViewMsg cv, Level0Profile profile, Material wallMat)
        {
            var placedWalls = new HashSet<string>();
            if (useBackendLayout && HasBackendLayout(cv))
            {
                CreateCellLayoutWalls(parent, BackendWalkableMask(cv), wallMat, placedWalls);
                CreateBackendLayoutPillars(parent, cv, wallMat);
                CreateBackendSpecialCells(parent, cv, wallMat);
                return;
            }

            switch (cv.templateId)
            {
                case 1:
                    CreateCellLayoutWalls(parent, CorridorMask(cv.templateId, cv.rotation), wallMat, placedWalls);
                    CreateRhythmPilasters(parent, cv.rotation, wallMat, placedWalls);
                    break;
                case 2:
                case 3:
                case 6:
                case 8:
                    CreateCellLayoutWalls(parent, CorridorMask(cv.templateId, cv.rotation), wallMat, placedWalls);
                    break;
                case 0:
                case 5:
                case 11:
                case 15:
                    CreateRoomModuleDividers(parent, cv.templateId, profile, wallMat, placedWalls);
                    break;
                case 4:
                case 12:
                    CreateServiceRoomDividers(parent, wallMat, placedWalls);
                    break;
                case 7:
                case 13:
                case 14:
                case 16:
                case 17:
                    CreateSparseBrokenDividers(parent, cv.templateId, profile, wallMat, placedWalls);
                    break;
            }
        }

        private bool[,] CorridorMask(int templateId, int rotation)
        {
            var cells = new bool[GridCells, GridCells];

            void Open(int x, int z)
            {
                if (x >= 0 && x < GridCells && z >= 0 && z < GridCells)
                    cells[x, z] = true;
            }

            switch (templateId)
            {
                case 1:
                    for (int z = 0; z < GridCells; z++)
                    {
                        Open(4, z);
                        Open(5, z);
                    }
                    break;
                case 2:
                    for (int z = 0; z < 6; z++)
                    {
                        Open(4, z);
                        Open(5, z);
                    }
                    for (int x = 4; x < GridCells; x++)
                    {
                        Open(x, 4);
                        Open(x, 5);
                    }
                    break;
                case 3:
                    for (int i = 0; i < GridCells; i++)
                    {
                        Open(4, i);
                        Open(5, i);
                        Open(i, 4);
                        Open(i, 5);
                    }
                    break;
                case 6:
                    for (int z = 4; z < GridCells; z++)
                    {
                        Open(4, z);
                        Open(5, z);
                    }
                    Open(3, 4);
                    Open(6, 4);
                    break;
                case 8:
                    for (int z = 0; z < GridCells; z++)
                    {
                        Open(4, z);
                        Open(5, z);
                    }
                    for (int x = 4; x < GridCells; x++)
                    {
                        Open(x, 4);
                        Open(x, 5);
                    }
                    break;
                default:
                    for (int x = 0; x < GridCells; x++)
                        for (int z = 0; z < GridCells; z++)
                            Open(x, z);
                    break;
            }

            int turns = Mathf.RoundToInt(rotation / 90f) % 4;
            for (int i = 0; i < turns; i++)
                cells = RotateCellsClockwise(cells);

            return cells;
        }

        private static bool[,] RotateCellsClockwise(bool[,] source)
        {
            var rotated = new bool[GridCells, GridCells];
            for (int x = 0; x < GridCells; x++)
                for (int z = 0; z < GridCells; z++)
                    rotated[GridCells - 1 - z, x] = source[x, z];
            return rotated;
        }

        private void CreateCellLayoutWalls(Transform parent, bool[,] openCells, Material wallMat, HashSet<string> placedWalls)
        {
            for (int x = 1; x < GridCells; x++)
            {
                for (int z = 0; z < GridCells; z++)
                {
                    if (openCells[x - 1, z] != openCells[x, z])
                    {
                        CreateUniqueWall(parent, placedWalls, "CellWall_X",
                            new Vector3(x * CellSize, wallHeight * 0.5f, z * CellSize + CellSize * 0.5f),
                            new Vector3(WallThickness, wallHeight, CellSize),
                            wallMat);
                    }
                }
            }

            for (int z = 1; z < GridCells; z++)
            {
                for (int x = 0; x < GridCells; x++)
                {
                    if (openCells[x, z - 1] != openCells[x, z])
                    {
                        CreateUniqueWall(parent, placedWalls, "CellWall_Z",
                            new Vector3(x * CellSize + CellSize * 0.5f, wallHeight * 0.5f, z * CellSize),
                            new Vector3(CellSize, wallHeight, WallThickness),
                            wallMat);
                    }
                }
            }
        }

        private void CreateRoomModuleDividers(Transform parent, int templateId, Level0Profile profile, Material wallMat, HashSet<string> placedWalls)
        {
            float offset = profile.propVariant % 2 == 0 ? 0f : CellSize;
            CreatePartialDividerX(parent, placedWalls, 3 * CellSize + offset, 1, 4, wallMat);
            CreatePartialDividerZ(parent, placedWalls, 6 * CellSize, 5, 8, wallMat);

            if (templateId == 11 || templateId == 15)
                CreatePartialDividerX(parent, placedWalls, 7 * CellSize, 5, 8, wallMat);
        }

        private void CreateServiceRoomDividers(Transform parent, Material wallMat, HashSet<string> placedWalls)
        {
            CreatePartialDividerX(parent, placedWalls, 2 * CellSize, 1, 7, wallMat);
            CreatePartialDividerZ(parent, placedWalls, 7 * CellSize, 2, 6, wallMat);
            CreatePartialDividerX(parent, placedWalls, 8 * CellSize, 4, 8, wallMat);
        }

        private void CreateSparseBrokenDividers(Transform parent, int templateId, Level0Profile profile, Material wallMat, HashSet<string> placedWalls)
        {
            if (templateId == 14)
            {
                CreatePartialDividerX(parent, placedWalls, 4 * CellSize, 1, 3, wallMat);
                CreatePartialDividerZ(parent, placedWalls, 6 * CellSize, 6, 8, wallMat);
                return;
            }

            CreatePartialDividerX(parent, placedWalls, 3 * CellSize, 2, 5, wallMat);
            if (profile.propVariant % 3 == 0 || templateId == 16 || templateId == 17)
                CreatePartialDividerZ(parent, placedWalls, 7 * CellSize, 4, 8, wallMat);
        }

        private void CreatePartialDividerX(Transform parent, HashSet<string> placedWalls, float x, int zStartCell, int zEndCell, Material wallMat)
        {
            float length = Mathf.Max(CellSize, (zEndCell - zStartCell) * CellSize);
            CreateUniqueWall(parent, placedWalls, "Divider_X",
                new Vector3(x, wallHeight * 0.5f, zStartCell * CellSize + length * 0.5f),
                new Vector3(WallThickness, wallHeight, length),
                wallMat);
        }

        private void CreatePartialDividerZ(Transform parent, HashSet<string> placedWalls, float z, int xStartCell, int xEndCell, Material wallMat)
        {
            float length = Mathf.Max(CellSize, (xEndCell - xStartCell) * CellSize);
            CreateUniqueWall(parent, placedWalls, "Divider_Z",
                new Vector3(xStartCell * CellSize + length * 0.5f, wallHeight * 0.5f, z),
                new Vector3(length, wallHeight, WallThickness),
                wallMat);
        }

        private void CreateRhythmPilasters(Transform parent, int rotation, Material wallMat, HashSet<string> placedWalls)
        {
            bool eastWest = Mathf.RoundToInt(rotation / 90f) % 2 != 0;
            for (int i = 1; i < GridCells; i += 2)
            {
                float p = i * CellSize + CellSize * 0.5f;
                if (eastWest)
                {
                    CreateUniqueWall(parent, placedWalls, "CorridorPilaster_N", new Vector3(p, wallHeight * 0.5f, 22.1f), new Vector3(0.42f, wallHeight, 0.65f), wallMat);
                    CreateUniqueWall(parent, placedWalls, "CorridorPilaster_S", new Vector3(p, wallHeight * 0.5f, 27.9f), new Vector3(0.42f, wallHeight, 0.65f), wallMat);
                }
                else
                {
                    CreateUniqueWall(parent, placedWalls, "CorridorPilaster_W", new Vector3(22.1f, wallHeight * 0.5f, p), new Vector3(0.65f, wallHeight, 0.42f), wallMat);
                    CreateUniqueWall(parent, placedWalls, "CorridorPilaster_E", new Vector3(27.9f, wallHeight * 0.5f, p), new Vector3(0.65f, wallHeight, 0.42f), wallMat);
                }
            }
        }

        private static void CreateUniqueWall(Transform parent, HashSet<string> placedWalls, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            string key = $"{Mathf.RoundToInt(pos.x * 10f)}:{Mathf.RoundToInt(pos.z * 10f)}:{Mathf.RoundToInt(scale.x * 10f)}:{Mathf.RoundToInt(scale.z * 10f)}";
            if (!placedWalls.Add(key))
                return;

            CreateSlab(parent, name, pos, scale, mat);
        }

        private void CreateCollisionProxy(Transform chunkRoot, ChunkViewMsg cv, Level0Profile profile)
        {
            var proxy = new GameObject("__CollisionProxy");
            proxy.transform.SetParent(chunkRoot, false);
            int layer = ResolveWorldCollisionLayer(chunkRoot.gameObject.layer);
            proxy.layer = layer;

            CreateCollisionBox(proxy.transform, "FloorCollider",
                new Vector3(chunkSize * 0.5f, -0.08f, chunkSize * 0.5f),
                new Vector3(chunkSize, 0.16f, chunkSize),
                layer);

            // Phase 2.7B: collision proxy mirrors the backend edge model exactly,
            // so the client proxy never blocks where the authoritative backend
            // would allow movement (and vice versa).
            if (useBackendLayout && cv.HasEdgeLayout)
            {
                CreateEdgeArchitectureCollision(proxy.transform, cv, layer);
                CreateBackendLayoutPillarCollision(proxy.transform, cv, layer);
                CreateBackendCellCollision(proxy.transform, cv, layer);
                return;
            }

            CreateCollisionTemplateWalls(proxy.transform, cv, layer);
            CreateCollisionInteriorLayout(proxy.transform, cv, profile, layer);

            if (!(useBackendLayout && HasBackendLayout(cv)) && (cv.templateId == 9 || cv.templateId == 10))
                CreateColumnCollision(proxy.transform, cv.templateId == 9, layer);
        }

        private int ResolveWorldCollisionLayer(int fallbackLayer)
        {
            int layer = LayerMask.NameToLayer(worldCollisionLayerName);
            return layer >= 0 ? layer : fallbackLayer;
        }

        private void CreateCollisionTemplateWalls(Transform parent, ChunkViewMsg cv, int layer)
        {
            var openings = OpeningsFor(cv);
            CreateCollisionWallWithOpening(parent, "WallN_Collider", 0, openings.north, layer);
            CreateCollisionWallWithOpening(parent, "WallS_Collider", 2, openings.south, layer);
            CreateCollisionWallWithOpening(parent, "WallW_Collider", 3, openings.west, layer);
            CreateCollisionWallWithOpening(parent, "WallE_Collider", 1, openings.east, layer);
        }

        private void CreateCollisionInteriorLayout(Transform parent, ChunkViewMsg cv, Level0Profile profile, int layer)
        {
            var placedWalls = new HashSet<string>();
            if (useBackendLayout && HasBackendLayout(cv))
            {
                CreateCollisionCellLayoutWalls(parent, BackendWalkableMask(cv), placedWalls, layer);
                CreateBackendLayoutPillarCollision(parent, cv, layer);
                CreateBackendSpecialCellCollision(parent, cv, layer);
                return;
            }

            switch (cv.templateId)
            {
                case 1:
                    CreateCollisionCellLayoutWalls(parent, CorridorMask(cv.templateId, cv.rotation), placedWalls, layer);
                    CreateCollisionRhythmPilasters(parent, cv.rotation, placedWalls, layer);
                    break;
                case 2:
                case 3:
                case 6:
                case 8:
                    CreateCollisionCellLayoutWalls(parent, CorridorMask(cv.templateId, cv.rotation), placedWalls, layer);
                    break;
                case 0:
                case 5:
                case 11:
                case 15:
                    CreateCollisionRoomModuleDividers(parent, cv.templateId, profile, placedWalls, layer);
                    break;
                case 4:
                case 12:
                    CreateCollisionServiceRoomDividers(parent, placedWalls, layer);
                    break;
                case 7:
                case 13:
                case 14:
                case 16:
                case 17:
                    CreateCollisionSparseBrokenDividers(parent, cv.templateId, profile, placedWalls, layer);
                    break;
            }
        }

        private void CreateCollisionCellLayoutWalls(Transform parent, bool[,] openCells, HashSet<string> placedWalls, int layer)
        {
            for (int x = 1; x < GridCells; x++)
            {
                for (int z = 0; z < GridCells; z++)
                {
                    if (openCells[x - 1, z] != openCells[x, z])
                    {
                        CreateUniqueCollisionBox(parent, placedWalls, "CellWall_X_Collider",
                            new Vector3(x * CellSize, wallHeight * 0.5f, z * CellSize + CellSize * 0.5f),
                            new Vector3(WallThickness, wallHeight, CellSize),
                            layer);
                    }
                }
            }

            for (int z = 1; z < GridCells; z++)
            {
                for (int x = 0; x < GridCells; x++)
                {
                    if (openCells[x, z - 1] != openCells[x, z])
                    {
                        CreateUniqueCollisionBox(parent, placedWalls, "CellWall_Z_Collider",
                            new Vector3(x * CellSize + CellSize * 0.5f, wallHeight * 0.5f, z * CellSize),
                            new Vector3(CellSize, wallHeight, WallThickness),
                            layer);
                    }
                }
            }
        }

        private void CreateCollisionRoomModuleDividers(Transform parent, int templateId, Level0Profile profile, HashSet<string> placedWalls, int layer)
        {
            float offset = profile.propVariant % 2 == 0 ? 0f : CellSize;
            CreateCollisionPartialDividerX(parent, placedWalls, 3 * CellSize + offset, 1, 4, layer);
            CreateCollisionPartialDividerZ(parent, placedWalls, 6 * CellSize, 5, 8, layer);

            if (templateId == 11 || templateId == 15)
                CreateCollisionPartialDividerX(parent, placedWalls, 7 * CellSize, 5, 8, layer);
        }

        private void CreateCollisionServiceRoomDividers(Transform parent, HashSet<string> placedWalls, int layer)
        {
            CreateCollisionPartialDividerX(parent, placedWalls, 2 * CellSize, 1, 7, layer);
            CreateCollisionPartialDividerZ(parent, placedWalls, 7 * CellSize, 2, 6, layer);
            CreateCollisionPartialDividerX(parent, placedWalls, 8 * CellSize, 4, 8, layer);
        }

        private void CreateCollisionSparseBrokenDividers(Transform parent, int templateId, Level0Profile profile, HashSet<string> placedWalls, int layer)
        {
            if (templateId == 14)
            {
                CreateCollisionPartialDividerX(parent, placedWalls, 4 * CellSize, 1, 3, layer);
                CreateCollisionPartialDividerZ(parent, placedWalls, 6 * CellSize, 6, 8, layer);
                return;
            }

            CreateCollisionPartialDividerX(parent, placedWalls, 3 * CellSize, 2, 5, layer);
            if (profile.propVariant % 3 == 0 || templateId == 16 || templateId == 17)
                CreateCollisionPartialDividerZ(parent, placedWalls, 7 * CellSize, 4, 8, layer);
        }

        private void CreateCollisionPartialDividerX(Transform parent, HashSet<string> placedWalls, float x, int zStartCell, int zEndCell, int layer)
        {
            float length = Mathf.Max(CellSize, (zEndCell - zStartCell) * CellSize);
            CreateUniqueCollisionBox(parent, placedWalls, "Divider_X_Collider",
                new Vector3(x, wallHeight * 0.5f, zStartCell * CellSize + length * 0.5f),
                new Vector3(WallThickness, wallHeight, length),
                layer);
        }

        private void CreateCollisionPartialDividerZ(Transform parent, HashSet<string> placedWalls, float z, int xStartCell, int xEndCell, int layer)
        {
            float length = Mathf.Max(CellSize, (xEndCell - xStartCell) * CellSize);
            CreateUniqueCollisionBox(parent, placedWalls, "Divider_Z_Collider",
                new Vector3(xStartCell * CellSize + length * 0.5f, wallHeight * 0.5f, z),
                new Vector3(length, wallHeight, WallThickness),
                layer);
        }

        private void CreateCollisionRhythmPilasters(Transform parent, int rotation, HashSet<string> placedWalls, int layer)
        {
            bool eastWest = Mathf.RoundToInt(rotation / 90f) % 2 != 0;
            for (int i = 1; i < GridCells; i += 2)
            {
                float p = i * CellSize + CellSize * 0.5f;
                if (eastWest)
                {
                    CreateUniqueCollisionBox(parent, placedWalls, "CorridorPilaster_N_Collider", new Vector3(p, wallHeight * 0.5f, 22.1f), new Vector3(0.42f, wallHeight, 0.65f), layer);
                    CreateUniqueCollisionBox(parent, placedWalls, "CorridorPilaster_S_Collider", new Vector3(p, wallHeight * 0.5f, 27.9f), new Vector3(0.42f, wallHeight, 0.65f), layer);
                }
                else
                {
                    CreateUniqueCollisionBox(parent, placedWalls, "CorridorPilaster_W_Collider", new Vector3(22.1f, wallHeight * 0.5f, p), new Vector3(0.65f, wallHeight, 0.42f), layer);
                    CreateUniqueCollisionBox(parent, placedWalls, "CorridorPilaster_E_Collider", new Vector3(27.9f, wallHeight * 0.5f, p), new Vector3(0.65f, wallHeight, 0.42f), layer);
                }
            }
        }

        private void CreateColumnCollision(Transform parent, bool large, int layer)
        {
            float[] coords = large
                ? new[] { 0.24f, 0.50f, 0.76f }
                : new[] { 0.32f, 0.68f };

            int idx = 0;
            foreach (float x in coords)
            {
                foreach (float z in coords)
                {
                    bool edge = x < 0.3f || x > 0.7f || z < 0.3f || z > 0.7f;
                    float width = large ? (edge ? 2.8f : 1.8f) : 1.4f;
                    CreateCollisionBox(parent, "ColumnCollider" + idx,
                        new Vector3(chunkSize * x, wallHeight * 0.5f, chunkSize * z),
                        new Vector3(width, wallHeight, width),
                        layer);
                    idx++;
                }
            }
        }

        private void CreateCollisionWallWithOpening(Transform parent, string name, int side, bool hasOpening, int layer)
        {
            float gap = hasOpening ? Mathf.Max(DoorOpening, CorridorWidth + 0.8f) : 0f;
            float sideLen = (chunkSize - gap) * 0.5f;

            if (!hasOpening)
            {
                CreateCollisionFullWall(parent, name, side, chunkSize, layer);
                return;
            }

            if (side == 0 || side == 2)
            {
                float z = side == 0 ? 0f : chunkSize;
                CreateCollisionBox(parent, name + "A",
                    new Vector3(sideLen * 0.5f, wallHeight * 0.5f, z),
                    new Vector3(sideLen, wallHeight, WallThickness),
                    layer);
                CreateCollisionBox(parent, name + "B",
                    new Vector3(chunkSize - sideLen * 0.5f, wallHeight * 0.5f, z),
                    new Vector3(sideLen, wallHeight, WallThickness),
                    layer);
            }
            else
            {
                float x = side == 3 ? 0f : chunkSize;
                CreateCollisionBox(parent, name + "A",
                    new Vector3(x, wallHeight * 0.5f, sideLen * 0.5f),
                    new Vector3(WallThickness, wallHeight, sideLen),
                    layer);
                CreateCollisionBox(parent, name + "B",
                    new Vector3(x, wallHeight * 0.5f, chunkSize - sideLen * 0.5f),
                    new Vector3(WallThickness, wallHeight, sideLen),
                    layer);
            }
        }

        private void CreateCollisionFullWall(Transform parent, string name, int side, float length, int layer)
        {
            if (side == 0 || side == 2)
            {
                float z = side == 0 ? 0f : chunkSize;
                CreateCollisionBox(parent, name,
                    new Vector3(chunkSize * 0.5f, wallHeight * 0.5f, z),
                    new Vector3(length, wallHeight, WallThickness),
                    layer);
            }
            else
            {
                float x = side == 3 ? 0f : chunkSize;
                CreateCollisionBox(parent, name,
                    new Vector3(x, wallHeight * 0.5f, chunkSize * 0.5f),
                    new Vector3(WallThickness, wallHeight, length),
                    layer);
            }
        }

        private static void CreateUniqueCollisionBox(Transform parent, HashSet<string> placedWalls, string name, Vector3 pos, Vector3 scale, int layer)
        {
            string key = $"{Mathf.RoundToInt(pos.x * 10f)}:{Mathf.RoundToInt(pos.z * 10f)}:{Mathf.RoundToInt(scale.x * 10f)}:{Mathf.RoundToInt(scale.z * 10f)}";
            if (!placedWalls.Add(key))
                return;

            CreateCollisionBox(parent, name, pos, scale, layer);
        }

        private static void CreateCollisionBox(Transform parent, string name, Vector3 pos, Vector3 scale, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one;

            var box = go.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = scale;
        }

        private void CreateTemplateProps(Transform parent, ChunkViewMsg cv, Level0Profile profile)
        {
            switch (cv.templateId)
            {
                case 0: CreateOpenRoomProps(parent); break;
                case 1: CreateHallwayProps(parent, cv.rotation); break;
                case 3: CreateIntersectionProps(parent); break;
                case 4: CreateShelves(parent); break;
                case 5: CreateSafeRoomProps(parent); break;
                case 6: CreateDeadEndProps(parent); break;
                case 7: CreateDangerProps(parent); break;
                case 8: CreateTJunctionProps(parent, cv.rotation); break;
                case 9: CreatePillars(parent, true); break;
                case 10: CreateOpenHallProps(parent); break;
                case 11: CreateArchRoomProps(parent); break;
                case 12: CreateCleaningAreaProps(parent); break;
                case 13: CreateHumidZoneProps(parent); break;
                case 14: CreateBlackoutZoneProps(parent); break;
                case 15: CreateManilaRoomProps(parent); break;
                case 16: CreateRedRoomWarningProps(parent); break;
                case 17: CreatePitRoomPlaceholderProps(parent); break;
            }
        }

        private void CreateOpenRoomProps(Transform parent)
        {
            CreateSlab(parent, "OpenRoomLowDivider",
                new Vector3(chunkSize * 0.42f, 0.65f, chunkSize * 0.68f),
                new Vector3(12f, 1.3f, 0.5f),
                _panelMat);
        }

        private void CreateHallwayProps(Transform parent, int rotation)
        {
            bool eastWest = Mathf.RoundToInt(rotation / 90f) % 2 != 0;
            CreateSlab(parent, "HallwayLongFloorWear",
                new Vector3(chunkSize * 0.5f, 0.015f, chunkSize * 0.5f),
                eastWest ? new Vector3(chunkSize * 0.82f, 0.025f, 8f) : new Vector3(8f, 0.025f, chunkSize * 0.82f),
                _stainMat);

            if (eastWest)
            {
                CreateSlab(parent, "HallwayPilasterA",
                    new Vector3(chunkSize * 0.32f, wallHeight * 0.5f, chunkSize * 0.18f),
                    new Vector3(1.4f, wallHeight, 0.70f),
                    _pillarMat);
                CreateSlab(parent, "HallwayPilasterB",
                    new Vector3(chunkSize * 0.68f, wallHeight * 0.5f, chunkSize * 0.82f),
                    new Vector3(1.4f, wallHeight, 0.70f),
                    _pillarMat);
            }
            else
            {
                CreateSlab(parent, "HallwayPilasterA",
                    new Vector3(chunkSize * 0.18f, wallHeight * 0.5f, chunkSize * 0.32f),
                    new Vector3(0.70f, wallHeight, 1.4f),
                    _pillarMat);
                CreateSlab(parent, "HallwayPilasterB",
                    new Vector3(chunkSize * 0.82f, wallHeight * 0.5f, chunkSize * 0.68f),
                    new Vector3(0.70f, wallHeight, 1.4f),
                    _pillarMat);
            }
        }

        private void CreateIntersectionProps(Transform parent)
        {
            CreateSlab(parent, "IntersectionCore",
                new Vector3(chunkSize * 0.5f, ceilingHeight * 0.5f, chunkSize * 0.5f),
                new Vector3(2.4f, ceilingHeight, 2.4f),
                _pillarMat);

            CreateSlab(parent, "IntersectionDirtyCross",
                new Vector3(chunkSize * 0.5f, 0.025f, chunkSize * 0.5f),
                new Vector3(11f, 0.04f, 11f),
                _stainMat);
        }

        private void CreateShelves(Transform parent)
        {
            CreateSlab(parent, "Shelf_Long_A",
                new Vector3(chunkSize * 0.22f, 0.9f, chunkSize * 0.72f),
                new Vector3(8.5f, 1.8f, 0.9f),
                _trimMat);

            CreateSlab(parent, "Shelf_Long_B",
                new Vector3(chunkSize * 0.72f, 0.9f, chunkSize * 0.28f),
                new Vector3(0.9f, 1.8f, 8.5f),
                _trimMat);

            CreateSlab(parent, "Storage_Box_A",
                new Vector3(chunkSize * 0.37f, 0.45f, chunkSize * 0.42f),
                new Vector3(2.4f, 0.9f, 2.1f),
                _boxMat);

            CreateSlab(parent, "Storage_Box_B",
                new Vector3(chunkSize * 0.48f, 0.35f, chunkSize * 0.62f),
                new Vector3(2.0f, 0.7f, 1.7f),
                _boxMat);

            CreateSlab(parent, "Storage_Box_C",
                new Vector3(chunkSize * 0.64f, 0.55f, chunkSize * 0.60f),
                new Vector3(2.8f, 1.1f, 1.8f),
                _boxMat);
        }

        private void CreateSafeRoomProps(Transform parent)
        {
            CreateSlab(parent, "SafeCenterPatch",
                new Vector3(chunkSize * 0.5f, 0.025f, chunkSize * 0.5f),
                new Vector3(9f, 0.04f, 9f),
                _safeMat);

            CreateSlab(parent, "SafeLowBench",
                new Vector3(chunkSize * 0.65f, 0.35f, chunkSize * 0.65f),
                new Vector3(4f, 0.7f, 1.3f),
                _trimMat);
        }

        private void CreateDeadEndProps(Transform parent)
        {
            CreateSlab(parent, "DeadEndBarrier",
                new Vector3(chunkSize * 0.75f, 1.25f, chunkSize * 0.5f),
                new Vector3(0.7f, 2.5f, 10f),
                _trimMat);

            CreateSlab(parent, "DeadEndFloorDirt",
                new Vector3(chunkSize * 0.55f, 0.03f, chunkSize * 0.5f),
                new Vector3(12f, 0.06f, 10f),
                _stainMat);
        }

        private void CreateDangerProps(Transform parent)
        {
            CreateSlab(parent, "DangerMark",
                new Vector3(chunkSize * 0.5f, 0.035f, chunkSize * 0.5f),
                new Vector3(13f, 0.07f, 13f),
                _dangerMat);

            CreateSlab(parent, "DangerWallBlockA",
                new Vector3(chunkSize * 0.30f, 1.2f, chunkSize * 0.65f),
                new Vector3(0.8f, 2.4f, 9f),
                _trimMat);

            CreateSlab(parent, "DangerWallBlockB",
                new Vector3(chunkSize * 0.72f, 1.2f, chunkSize * 0.35f),
                new Vector3(9f, 2.4f, 0.8f),
                _trimMat);
        }

        private void CreateTJunctionProps(Transform parent, int rotation)
        {
            bool eastWest = Mathf.RoundToInt(rotation / 90f) % 2 != 0;
            CreateSlab(parent, "TJunctionMarker",
                eastWest ? new Vector3(chunkSize * 0.68f, 0.025f, chunkSize * 0.5f) : new Vector3(chunkSize * 0.5f, 0.025f, chunkSize * 0.68f),
                eastWest ? new Vector3(2.0f, 0.05f, 10f) : new Vector3(10f, 0.05f, 2.0f),
                _trimMat);

            CreateSlab(parent, "TJunctionVisualStop",
                eastWest ? new Vector3(chunkSize * 0.78f, 1.1f, chunkSize * 0.5f) : new Vector3(chunkSize * 0.5f, 1.1f, chunkSize * 0.78f),
                eastWest ? new Vector3(0.55f, 2.2f, 7f) : new Vector3(7f, 2.2f, 0.55f),
                _pillarMat);
        }

        private void CreatePillars(Transform parent, bool large)
        {
            float pillarH = ceilingHeight;
            Vector3 largeScale = large ? new Vector3(3.0f, pillarH, 3.0f) : new Vector3(2.2f, pillarH, 2.2f);
            Vector3 mediumScale = large ? new Vector3(2.0f, pillarH, 2.0f) : new Vector3(1.5f, pillarH, 1.5f);

            float[] coords = large
                ? new[] { 0.24f, 0.50f, 0.76f }
                : new[] { 0.32f, 0.68f };

            int idx = 0;
            foreach (float x in coords)
            {
                foreach (float z in coords)
                {
                    bool edge = x < 0.3f || x > 0.7f || z < 0.3f || z > 0.7f;
                    CreateSlab(parent, "Pillar" + idx,
                        new Vector3(chunkSize * x, pillarH * 0.5f, chunkSize * z),
                        edge ? largeScale : mediumScale,
                        _pillarMat);
                    idx++;
                }
            }
        }

        private void CreateOpenHallProps(Transform parent)
        {
            CreatePillars(parent, false);
            CreateSlab(parent, "OpenHallLowPartition",
                new Vector3(chunkSize * 0.50f, 0.7f, chunkSize * 0.24f),
                new Vector3(chunkSize * 0.50f, 1.4f, 0.55f),
                _panelMat);
        }

        private void CreateArchRoomProps(Transform parent)
        {
            CreateSlab(parent, "ArchRoomHeader_N",
                new Vector3(chunkSize * 0.5f, wallHeight * 0.78f, 0.16f),
                new Vector3(8.5f, 0.65f, 0.08f),
                _pillarMat);

            CreateSlab(parent, "ArchRoomColumn_NA",
                new Vector3(chunkSize * 0.36f, wallHeight * 0.45f, 0.16f),
                new Vector3(0.7f, 2.5f, 0.08f),
                _pillarMat);

            CreateSlab(parent, "ArchRoomColumn_NB",
                new Vector3(chunkSize * 0.64f, wallHeight * 0.45f, 0.16f),
                new Vector3(0.7f, 2.5f, 0.08f),
                _pillarMat);

            CreateSlab(parent, "ArchRoomQuietPatch",
                new Vector3(chunkSize * 0.5f, 0.025f, chunkSize * 0.5f),
                new Vector3(12f, 0.04f, 7f),
                _manilaMat);
        }

        private void CreateCleaningAreaProps(Transform parent)
        {
            CreateSlab(parent, "CleaningWetPatch",
                new Vector3(chunkSize * 0.52f, 0.018f, chunkSize * 0.55f),
                new Vector3(13f, 0.03f, 8f),
                _wetMat);

            CreateSlab(parent, "CleaningCart",
                new Vector3(chunkSize * 0.34f, 0.55f, chunkSize * 0.38f),
                new Vector3(2.6f, 1.1f, 1.5f),
                _cleaningMat);

            CreateSlab(parent, "CleaningBucket",
                new Vector3(chunkSize * 0.42f, 0.32f, chunkSize * 0.42f),
                new Vector3(1.0f, 0.64f, 1.0f),
                _cleaningMat);

            CreateSlab(parent, "CleaningSupplyStack",
                new Vector3(chunkSize * 0.72f, 0.65f, chunkSize * 0.30f),
                new Vector3(3.2f, 1.3f, 1.8f),
                _boxMat);
        }

        private void CreateHumidZoneProps(Transform parent)
        {
            CreateSlab(parent, "HumidWetCarpet",
                new Vector3(chunkSize * 0.5f, 0.018f, chunkSize * 0.5f),
                new Vector3(chunkSize * 0.72f, 0.03f, chunkSize * 0.62f),
                _wetMat);
        }

        private void CreateBlackoutZoneProps(Transform parent)
        {
            CreateSlab(parent, "BlackoutZoneMoldBand",
                new Vector3(chunkSize * 0.5f, 0.30f, 0.12f),
                new Vector3(chunkSize * 0.75f, 0.55f, 0.06f),
                _blackMoldMat);
        }

        private void CreateManilaRoomProps(Transform parent)
        {
            CreateSlab(parent, "ManilaCenterPatch",
                new Vector3(chunkSize * 0.5f, 0.02f, chunkSize * 0.5f),
                new Vector3(chunkSize * 0.50f, 0.035f, chunkSize * 0.50f),
                _manilaMat);

            CreateSlab(parent, "ManilaLowTable",
                new Vector3(chunkSize * 0.62f, 0.32f, chunkSize * 0.58f),
                new Vector3(4.2f, 0.64f, 2.0f),
                _trimMat);
        }

        private void CreateRedRoomWarningProps(Transform parent)
        {
            CreateSlab(parent, "RedRoomWarningPanel_A",
                new Vector3(chunkSize * 0.28f, wallHeight * 0.5f, 0.13f),
                new Vector3(3.5f, 2.7f, 0.06f),
                _redRoomMat);

            CreateSlab(parent, "RedRoomWarningPanel_B",
                new Vector3(chunkSize * 0.55f, wallHeight * 0.5f, 0.13f),
                new Vector3(3.5f, 2.7f, 0.06f),
                _warningMat);

            CreateArrowMarks(parent);
        }

        private void CreatePitRoomPlaceholderProps(Transform parent)
        {
            CreateSlab(parent, "PitRoomPaintedWarning",
                new Vector3(chunkSize * 0.5f, 0.018f, chunkSize * 0.5f),
                new Vector3(15f, 0.03f, 11f),
                _warningMat);

            CreateSlab(parent, "PitRoomLowRail_N",
                new Vector3(chunkSize * 0.5f, 0.45f, chunkSize * 0.38f),
                new Vector3(14f, 0.9f, 0.45f),
                _trimMat);

            CreateSlab(parent, "PitRoomLowRail_S",
                new Vector3(chunkSize * 0.5f, 0.45f, chunkSize * 0.62f),
                new Vector3(14f, 0.9f, 0.45f),
                _trimMat);
        }

        private void CreateBackroomsDressing(Transform parent, ChunkViewMsg cv, Level0Profile profile, bool edgeLayout)
        {
            CreatePerimeterTrim(parent, cv);

            if (profile.propVariant == 0 || cv.templateId == 8 || cv.templateId == 11 || cv.templateId == 16)
                CreateArrowMarks(parent);

            // Edge layouts get their false doors from real boundary edges; skip the
            // legacy perimeter-attached fake panel to avoid duplicates.
            if (!edgeLayout && (profile.propVariant == 4 || cv.templateId == 11))
                CreateFalseDoorPanel(parent);
        }

        /// <summary>
        /// Continuous baseboard + crown trim around the chunk perimeter (the most
        /// visible room edge). Baseboards split around the centred boundary opening
        /// so they never float across a doorway; crown runs full span under the
        /// ceiling. Merged into a few slabs to stay within the per-chunk budget.
        /// </summary>
        private void CreatePerimeterTrim(Transform parent, ChunkViewMsg cv)
        {
            var op = OpeningsFor(cv);
            float gapA = (GridCells * 0.5f - 1f) * CellSize; // 20
            float gapB = (GridCells * 0.5f + 1f) * CellSize; // 30
            const float baseY = 0.16f, baseH = 0.32f, t = 0.06f;
            float crownY = ceilingHeight - 0.16f;
            const float crownH = 0.14f;
            float half = chunkSize * 0.5f;

            AddPerimeterBaseboard(parent, "Baseboard_N", true, 0.10f, op.north, gapA, gapB, baseY, baseH, t);
            AddPerimeterBaseboard(parent, "Baseboard_S", true, chunkSize - 0.10f, op.south, gapA, gapB, baseY, baseH, t);
            AddPerimeterBaseboard(parent, "Baseboard_W", false, 0.10f, op.west, gapA, gapB, baseY, baseH, t);
            AddPerimeterBaseboard(parent, "Baseboard_E", false, chunkSize - 0.10f, op.east, gapA, gapB, baseY, baseH, t);

            CreateSlab(parent, "Crown_N", new Vector3(half, crownY, 0.10f), new Vector3(chunkSize, crownH, t), _trimMat);
            CreateSlab(parent, "Crown_S", new Vector3(half, crownY, chunkSize - 0.10f), new Vector3(chunkSize, crownH, t), _trimMat);
            CreateSlab(parent, "Crown_W", new Vector3(0.10f, crownY, half), new Vector3(t, crownH, chunkSize), _trimMat);
            CreateSlab(parent, "Crown_E", new Vector3(chunkSize - 0.10f, crownY, half), new Vector3(t, crownH, chunkSize), _trimMat);
        }

        private void AddPerimeterBaseboard(Transform parent, string name, bool alongX, float fixedCoord, bool open,
            float gapA, float gapB, float y, float h, float t)
        {
            if (!open)
            {
                Vector3 pos = alongX ? new Vector3(chunkSize * 0.5f, y, fixedCoord) : new Vector3(fixedCoord, y, chunkSize * 0.5f);
                Vector3 scale = alongX ? new Vector3(chunkSize, h, t) : new Vector3(t, h, chunkSize);
                CreateSlab(parent, name, pos, scale, _baseboardMat);
                return;
            }

            float lenA = gapA;                 // 0 .. gapA
            float lenB = chunkSize - gapB;     // gapB .. chunkSize
            if (alongX)
            {
                CreateSlab(parent, name + "A", new Vector3(gapA * 0.5f, y, fixedCoord), new Vector3(lenA, h, t), _baseboardMat);
                CreateSlab(parent, name + "B", new Vector3(gapB + lenB * 0.5f, y, fixedCoord), new Vector3(lenB, h, t), _baseboardMat);
            }
            else
            {
                CreateSlab(parent, name + "A", new Vector3(fixedCoord, y, gapA * 0.5f), new Vector3(t, h, lenA), _baseboardMat);
                CreateSlab(parent, name + "B", new Vector3(fixedCoord, y, gapB + lenB * 0.5f), new Vector3(t, h, lenB), _baseboardMat);
            }
        }

        private void CreateArrowMarks(Transform parent)
        {
            float y = wallHeight * 0.55f;

            CreateSlab(parent, "WallArrowBody",
                new Vector3(chunkSize * 0.30f, y, 0.125f),
                new Vector3(3.0f, 0.16f, 0.055f),
                _arrowMat);

            CreateSlab(parent, "WallArrowHeadA",
                new Vector3(chunkSize * 0.36f, y + 0.22f, 0.13f),
                new Vector3(1.0f, 0.14f, 0.055f),
                _arrowMat);

            CreateSlab(parent, "WallArrowHeadB",
                new Vector3(chunkSize * 0.36f, y - 0.22f, 0.13f),
                new Vector3(1.0f, 0.14f, 0.055f),
                _arrowMat);
        }

        private void CreateFalseDoorPanel(Transform parent)
        {
            CreateSlab(parent, "FalseDoorPanel",
                new Vector3(0.13f, wallHeight * 0.48f, chunkSize * 0.60f),
                new Vector3(0.055f, 2.2f, 4.2f),
                _panelMat);

            CreateSlab(parent, "FalseDoorHandle",
                new Vector3(0.165f, wallHeight * 0.45f, chunkSize * 0.63f),
                new Vector3(0.06f, 0.18f, 0.18f),
                _trimMat);
        }

        // ─────────────────────────────────────────────────────────────
        // Phase 2.7B — edge-wall architecture (backend edge arrays)
        // ─────────────────────────────────────────────────────────────

        private struct EdgeRenderCounts
        {
            public int walls, doors, arches, lowWalls, halfWalls, partitions, falseDoors, broken;
        }

        // Spawn-core cells / interior edges on chunk (0,0). The backend already
        // clears these (reserve_starter_spawn_area); these guards are a visual
        // safety net so nothing ever crosses the spawn capsule.
        private static bool IsSpawnCoreCell(int x, int z) => x >= 3 && x <= 6 && z >= 3 && z <= 6;
        private static bool IsSpawnCoreVerticalEdge(int bx, int z) => bx >= 4 && bx <= 6 && z >= 3 && z <= 6;
        private static bool IsSpawnCoreHorizontalEdge(int x, int bz) => bz >= 4 && bz <= 6 && x >= 3 && x <= 6;

        /// <summary>
        /// Render walls/doors/arches/low+half walls/partitions/false doors from
        /// the backend's cell-edge arrays. Vertical edges run along Z (local
        /// x = bx*CellSize); horizontal edges run along X (local z = bz*CellSize).
        /// Perimeter edges are included, so chunk-boundary openings match the
        /// backend exactly. Each edge is drawn once — no double walls.
        /// </summary>
        private EdgeRenderCounts CreateEdgeArchitecture(Transform parent, ChunkViewMsg cv, Material wallMat)
        {
            var counts = new EdgeRenderCounts();
            int g = cv.layoutGridSize;
            bool spawnChunk = IsSpawnChunk(cv);

            for (int z = 0; z < g; z++)
            {
                for (int bx = 0; bx <= g; bx++)
                {
                    byte kind = cv.GetVEdge(bx, z);
                    if (EdgeKinds.EdgeIsOpen(kind))
                        continue;
                    if (spawnChunk && IsSpawnCoreVerticalEdge(bx, z) && EdgeKinds.EdgeBlocksMovement(kind))
                        continue;
                    Vector3 center = new Vector3(bx * CellSize, 0f, (z + 0.5f) * CellSize);
                    // Nudge perimeter solid walls inward a hair so two neighbours'
                    // coincident boundary slabs aren't coplanar (kills z-fight).
                    // Collision is NOT nudged (it must match the backend boundary).
                    if (enableCrossChunkZFightNudge && EdgeKinds.EdgeBlocksMovement(kind))
                    {
                        if (bx == 0) center.x += PerimeterInset;
                        else if (bx == g) center.x -= PerimeterInset;
                    }
                    RenderEdge(parent, center, true, kind, wallMat, ref counts);
                }
            }

            for (int bz = 0; bz <= g; bz++)
            {
                for (int x = 0; x < g; x++)
                {
                    byte kind = cv.GetHEdge(x, bz);
                    if (EdgeKinds.EdgeIsOpen(kind))
                        continue;
                    if (spawnChunk && IsSpawnCoreHorizontalEdge(x, bz) && EdgeKinds.EdgeBlocksMovement(kind))
                        continue;
                    Vector3 center = new Vector3((x + 0.5f) * CellSize, 0f, bz * CellSize);
                    if (enableCrossChunkZFightNudge && EdgeKinds.EdgeBlocksMovement(kind))
                    {
                        if (bz == 0) center.z += PerimeterInset;
                        else if (bz == g) center.z -= PerimeterInset;
                    }
                    RenderEdge(parent, center, false, kind, wallMat, ref counts);
                }
            }

            return counts;
        }

        private void RenderEdge(Transform parent, Vector3 center, bool runsAlongZ, byte kind, Material wallMat, ref EdgeRenderCounts counts)
        {
            if (EdgeKinds.EdgeIsDoor(kind))
            {
                CreateEdgeDoorFrame(parent, center, runsAlongZ, false, wallMat);
                counts.doors++;
            }
            else if (EdgeKinds.EdgeIsArch(kind))
            {
                CreateEdgeDoorFrame(parent, center, runsAlongZ, true, wallMat);
                counts.arches++;
            }
            else if (EdgeKinds.EdgeIsLowWall(kind))
            {
                CreateEdgeWallSlab(parent, "EdgeLowWall", center, runsAlongZ, LowWallHeight, WallThickness, _panelMat);
                CreateEdgeCap(parent, center, runsAlongZ, LowWallHeight);
                counts.lowWalls++;
            }
            else if (EdgeKinds.EdgeIsHalfWall(kind))
            {
                CreateEdgeWallSlab(parent, "EdgeHalfWall", center, runsAlongZ, HalfWallHeight, WallThickness, _panelMat);
                CreateEdgeCap(parent, center, runsAlongZ, HalfWallHeight);
                counts.halfWalls++;
            }
            else if (EdgeKinds.EdgeIsPartition(kind))
            {
                // Office cubicle partition: thinner + a slightly different (paler) tint.
                CreateEdgeWallSlab(parent, "EdgePartition", center, runsAlongZ, wallHeight, PartitionThickness, _panelMat);
                counts.partitions++;
            }
            else if (EdgeKinds.EdgeIsFalseDoor(kind))
            {
                CreateEdgeFalseDoor(parent, center, runsAlongZ, wallMat);
                counts.falseDoors++;
            }
            else if (EdgeKinds.EdgeIsBrokenWall(kind))
            {
                // Backend treats broken walls as passable: render a low stub only.
                CreateEdgeWallSlab(parent, "EdgeBrokenWall", center, runsAlongZ, BrokenWallHeight, WallThickness, wallMat);
                counts.broken++;
            }
            else
            {
                CreateEdgeWallSlab(parent, "EdgeWall", center, runsAlongZ, wallHeight, WallThickness, wallMat);
                counts.walls++;
            }
        }

        private void CreateEdgeWallSlab(Transform parent, string name, Vector3 center, bool runsAlongZ, float height, float thickness, Material mat)
        {
            Vector3 scale = runsAlongZ
                ? new Vector3(thickness, height, CellSize)
                : new Vector3(CellSize, height, thickness);
            CreateSlab(parent, name, new Vector3(center.x, height * 0.5f, center.z), scale, mat);
        }

        // A thin slightly-wider cap strip on top of a low/half wall so it reads as
        // an intentional divider rather than a clipped wall.
        private void CreateEdgeCap(Transform parent, Vector3 center, bool runsAlongZ, float height)
        {
            const float capT = WallThickness * 1.7f;
            const float capH = 0.07f;
            Vector3 scale = runsAlongZ
                ? new Vector3(capT, capH, CellSize)
                : new Vector3(CellSize, capH, capT);
            CreateSlab(parent, "EdgeWallCap", new Vector3(center.x, height + capH * 0.5f, center.z), scale, _trimMat);
        }

        private void CreateEdgeDoorFrame(Transform parent, Vector3 center, bool runsAlongZ, bool arch, Material mat)
        {
            float postHeight = arch ? ArchPostHeight : DoorPostHeight;
            float postOffset = (CellSize - DoorPostWidth) * 0.5f;
            Material frameMat = arch ? _trimMat : mat;

            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 postPos = runsAlongZ
                    ? new Vector3(center.x, postHeight * 0.5f, center.z + s * postOffset)
                    : new Vector3(center.x + s * postOffset, postHeight * 0.5f, center.z);
                Vector3 postScale = runsAlongZ
                    ? new Vector3(WallThickness, postHeight, DoorPostWidth)
                    : new Vector3(DoorPostWidth, postHeight, WallThickness);
                CreateSlab(parent, arch ? "EdgeArchPost" : "EdgeDoorPost", postPos, postScale, frameMat);
            }

            // Header fills from post height up to the ceiling so the opening reads
            // as a hole in a wall (no gap above), but the passage itself is clear.
            float headerHeight = Mathf.Max(0.1f, wallHeight - postHeight);
            Vector3 headerScale = runsAlongZ
                ? new Vector3(WallThickness, headerHeight, CellSize)
                : new Vector3(CellSize, headerHeight, WallThickness);
            CreateSlab(parent, arch ? "EdgeArchHeader" : "EdgeDoorHeader",
                new Vector3(center.x, postHeight + headerHeight * 0.5f, center.z),
                headerScale, mat);

            Vector3 threshScale = runsAlongZ
                ? new Vector3(0.18f, 0.05f, DoorOpening)
                : new Vector3(DoorOpening, 0.05f, 0.18f);
            CreateSlab(parent, "EdgeThreshold", new Vector3(center.x, 0.03f, center.z), threshScale, _trimMat);
        }

        private void CreateEdgeFalseDoor(Transform parent, Vector3 center, bool runsAlongZ, Material mat)
        {
            // Full wall behind it (false doors block in the backend).
            CreateEdgeWallSlab(parent, "EdgeFalseDoorWall", center, runsAlongZ, wallHeight, WallThickness, mat);

            // A door-shaped panel flush on one wall face so it reads as a (fake) door.
            float panelH = 2.15f;
            float faceOffset = WallThickness * 0.5f + FalseDoorPanelInset;
            Vector3 panelPos = runsAlongZ
                ? new Vector3(center.x + faceOffset, panelH * 0.5f, center.z)
                : new Vector3(center.x, panelH * 0.5f, center.z + faceOffset);
            Vector3 panelScale = runsAlongZ
                ? new Vector3(0.06f, panelH, DoorOpening - 0.4f)
                : new Vector3(DoorOpening - 0.4f, panelH, 0.06f);
            CreateSlab(parent, "EdgeFalseDoorPanel", panelPos, panelScale, _panelMat);

            // Small handle so it reads as a (sealed) door, not a blank panel.
            const float hOff = 0.62f; // toward one side of the panel
            Vector3 handlePos = runsAlongZ
                ? new Vector3(center.x + faceOffset + 0.05f, panelH * 0.46f, center.z + hOff)
                : new Vector3(center.x + hOff, panelH * 0.46f, center.z + faceOffset + 0.05f);
            CreateSlab(parent, "EdgeFalseDoorHandle", handlePos, new Vector3(0.12f, 0.12f, 0.12f), _trimMat);
        }

        /// <summary>Cell-center detail only: blocked stacks, pits, ramps, fluid film.</summary>
        private void CreateBackendCellDetails(Transform parent, ChunkViewMsg cv)
        {
            int g = cv.layoutGridSize;
            bool spawnChunk = IsSpawnChunk(cv);
            for (int z = 0; z < g; z++)
            {
                for (int x = 0; x < g; x++)
                {
                    ushort flags = cv.GetCell(x, z);
                    if (flags == 0)
                        continue;
                    Vector3 center = new Vector3((x + 0.5f) * CellSize, 0f, (z + 0.5f) * CellSize);

                    if ((flags & CellBlocked) != 0 && (flags & CellWalkable) == 0)
                    {
                        CreateSlab(parent, "BackendBlockedCell",
                            new Vector3(center.x, wallHeight * 0.5f, center.z),
                            new Vector3(CellSize - 0.2f, wallHeight, CellSize - 0.2f),
                            _trimMat);
                    }
                    if ((flags & CellPit) != 0)
                        CreatePitMarker(parent, center);
                    if ((flags & CellRamp) != 0)
                        CreateRampMarker(parent, center, cv.floorProfile);
                    if ((flags & CellShallowFluid) != 0 && !spawnChunk)
                    {
                        CreateSlab(parent, "BackendFluidFilm",
                            new Vector3(center.x, 0.02f, center.z),
                            new Vector3(CellSize - 0.3f, 0.03f, CellSize - 0.3f),
                            _wetMat);
                    }
                }
            }
        }

        private void CreateEdgeArchitectureCollision(Transform parent, ChunkViewMsg cv, int layer)
        {
            int g = cv.layoutGridSize;
            bool spawnChunk = IsSpawnChunk(cv);

            for (int z = 0; z < g; z++)
            {
                for (int bx = 0; bx <= g; bx++)
                {
                    byte kind = cv.GetVEdge(bx, z);
                    if (!EdgeKinds.EdgeBlocksMovement(kind))
                        continue;
                    if (spawnChunk && IsSpawnCoreVerticalEdge(bx, z))
                        continue;
                    AddEdgeCollision(parent, new Vector3(bx * CellSize, 0f, (z + 0.5f) * CellSize), true, kind, layer);
                }
            }

            for (int bz = 0; bz <= g; bz++)
            {
                for (int x = 0; x < g; x++)
                {
                    byte kind = cv.GetHEdge(x, bz);
                    if (!EdgeKinds.EdgeBlocksMovement(kind))
                        continue;
                    if (spawnChunk && IsSpawnCoreHorizontalEdge(x, bz))
                        continue;
                    AddEdgeCollision(parent, new Vector3((x + 0.5f) * CellSize, 0f, bz * CellSize), false, kind, layer);
                }
            }
        }

        private void AddEdgeCollision(Transform parent, Vector3 center, bool runsAlongZ, byte kind, int layer)
        {
            float height = wallHeight;
            float thickness = WallThickness;
            if (EdgeKinds.EdgeIsLowWall(kind))
                height = LowWallHeight;
            else if (EdgeKinds.EdgeIsHalfWall(kind))
                height = HalfWallHeight;
            else if (EdgeKinds.EdgeIsPartition(kind))
                thickness = PartitionThickness;

            Vector3 scale = runsAlongZ
                ? new Vector3(thickness, height, CellSize)
                : new Vector3(CellSize, height, thickness);
            CreateCollisionBox(parent, "EdgeCollider", new Vector3(center.x, height * 0.5f, center.z), scale, layer);
        }

        private void CreateBackendCellCollision(Transform parent, ChunkViewMsg cv, int layer)
        {
            int g = cv.layoutGridSize;
            for (int z = 0; z < g; z++)
            {
                for (int x = 0; x < g; x++)
                {
                    ushort flags = cv.GetCell(x, z);
                    if ((flags & CellBlocked) != 0 && (flags & CellWalkable) == 0)
                    {
                        CreateCollisionBox(parent, "BackendBlockedCellCollider",
                            new Vector3((x + 0.5f) * CellSize, wallHeight * 0.5f, (z + 0.5f) * CellSize),
                            new Vector3(CellSize - 0.2f, wallHeight, CellSize - 0.2f),
                            layer);
                    }
                }
            }
        }

        private static int CountCellFlag(ChunkViewMsg cv, ushort flag)
        {
            int n = 0;
            if (cv.cellFlags == null)
                return 0;
            for (int i = 0; i < cv.cellFlags.Length; i++)
                if ((cv.cellFlags[i] & flag) != 0)
                    n++;
            return n;
        }

        private void LogEdgeChunkRenderSummary(ChunkViewMsg cv, bool edgeLayout, EdgeRenderCounts c)
        {
            int pillars = CountCellFlag(cv, CellPillar);
            Debug.Log($"MPTRACE step=V27 event=unity_edge_chunk_render_summary chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) template={cv.templateId} backend_layout={cv.HasBackendLayout} has_edges={edgeLayout} cells={cv.cellFlags.Length} v_edges={cv.verticalEdges.Length} h_edges={cv.horizontalEdges.Length} walls={c.walls} doors={c.doors} arches={c.arches} lowwalls={c.lowWalls} halfwalls={c.halfWalls} partitions={c.partitions} false_doors={c.falseDoors} pillars={pillars} fallback={!edgeLayout}");
        }

        private void LogSpawnChunkRendered(ChunkViewMsg cv, bool edgeLayout, EdgeRenderCounts c)
        {
            int pillars = CountCellFlag(cv, CellPillar);
            Debug.Log($"MPTRACE step=V27 event=unity_spawn_chunk_rendered backend_layout={cv.HasBackendLayout} has_edges={edgeLayout} walls={c.walls} doors={c.doors} arches={c.arches} lowwalls={c.lowWalls} halfwalls={c.halfWalls} pillars={pillars} fallback={!edgeLayout}");
        }

        // ─────────────────────────────────────────────────────────────
        // Openings
        // ─────────────────────────────────────────────────────────────

        private static bool HasBackendLayout(ChunkViewMsg cv)
        {
            return cv.layoutCells != null &&
                   cv.layoutGridSize > 0 &&
                   cv.layoutCells.Length >= cv.layoutGridSize * cv.layoutGridSize;
        }

        private static bool IsCellWalkable(ushort flags)
        {
            if ((flags & (CellLowWall | CellHalfWall)) != 0)
                return true;

            return (flags & CellWalkable) != 0 &&
                   (flags & (CellWall | CellPillar | CellBlocked | CellPit)) == 0;
        }

        private static bool[,] BackendWalkableMask(ChunkViewMsg cv)
        {
            var cells = new bool[GridCells, GridCells];
            if (!HasBackendLayout(cv))
                return cells;

            int grid = Mathf.Min(GridCells, cv.layoutGridSize);
            for (int x = 0; x < grid; x++)
            {
                for (int z = 0; z < grid; z++)
                {
                    int idx = z * cv.layoutGridSize + x;
                    cells[x, z] = idx >= 0 && idx < cv.layoutCells.Length && IsCellWalkable(cv.layoutCells[idx]);
                }
            }
            return cells;
        }

        private void CreateBackendLayoutPillars(Transform parent, ChunkViewMsg cv, Material mat)
        {
            if (!HasBackendLayout(cv))
                return;

            bool spawnChunk = IsSpawnChunk(cv);
            int grid = Mathf.Min(GridCells, cv.layoutGridSize);
            for (int x = 0; x < grid; x++)
            {
                for (int z = 0; z < grid; z++)
                {
                    int idx = z * cv.layoutGridSize + x;
                    if (idx >= cv.layoutCells.Length || (cv.layoutCells[idx] & CellPillar) == 0)
                        continue;
                    // Never plant a column in the reserved spawn core.
                    if (spawnChunk && IsSpawnCoreCell(x, z))
                        continue;

                    float cx = (x + 0.5f) * CellSize;
                    float cz = (z + 0.5f) * CellSize;
                    // Square office column with base + cap trim (no cartoon capsule).
                    CreateSlab(parent, "BackendPillar",
                        new Vector3(cx, wallHeight * 0.5f, cz),
                        new Vector3(PillarWidth, wallHeight, PillarWidth),
                        mat);
                    float trimW = PillarWidth + 0.18f;
                    CreateSlab(parent, "BackendPillarBase",
                        new Vector3(cx, 0.10f, cz), new Vector3(trimW, 0.20f, trimW), _trimMat);
                    CreateSlab(parent, "BackendPillarCap",
                        new Vector3(cx, wallHeight - 0.12f, cz), new Vector3(trimW, 0.16f, trimW), _trimMat);
                }
            }
        }

        private void CreateBackendSpecialCells(Transform parent, ChunkViewMsg cv, Material wallMat)
        {
            if (!HasBackendLayout(cv))
                return;

            int grid = Mathf.Min(GridCells, cv.layoutGridSize);
            var placed = new HashSet<string>();
            for (int x = 0; x < grid; x++)
            {
                for (int z = 0; z < grid; z++)
                {
                    int idx = z * cv.layoutGridSize + x;
                    if (idx >= cv.layoutCells.Length)
                        continue;

                    ushort flags = cv.layoutCells[idx];
                    Vector3 center = new Vector3((x + 0.5f) * CellSize, 0f, (z + 0.5f) * CellSize);

                    if ((flags & CellLowWall) != 0)
                    {
                        CreateUniqueWall(parent, placed, "BackendLowWall",
                            new Vector3(center.x, 0.55f, center.z),
                            new Vector3(CellSize * 0.82f, 1.1f, 0.30f),
                            _panelMat);
                    }
                    if ((flags & CellHalfWall) != 0)
                    {
                        CreateUniqueWall(parent, placed, "BackendHalfWall",
                            new Vector3(center.x, 0.85f, center.z),
                            new Vector3(0.34f, 1.7f, CellSize * 0.82f),
                            _panelMat);
                    }
                    if ((flags & CellDoor) != 0)
                        CreateDoorFrame(parent, center, false, wallMat);
                    if ((flags & CellArch) != 0)
                        CreateDoorFrame(parent, center, true, wallMat);
                    if ((flags & CellFalseDoor) != 0)
                        CreateFalseDoorCell(parent, center);
                    if ((flags & CellPit) != 0)
                        CreatePitMarker(parent, center);
                    if ((flags & CellRamp) != 0)
                        CreateRampMarker(parent, center, cv.floorProfile);
                }
            }
        }

        private void CreateDoorFrame(Transform parent, Vector3 center, bool arch, Material mat)
        {
            float postHeight = arch ? 2.65f : 2.25f;
            Material frameMat = arch ? _trimMat : mat;
            CreateSlab(parent, arch ? "BackendArchPostA" : "BackendDoorPostA",
                new Vector3(center.x - 1.15f, postHeight * 0.5f, center.z),
                new Vector3(0.22f, postHeight, 0.32f),
                frameMat);
            CreateSlab(parent, arch ? "BackendArchPostB" : "BackendDoorPostB",
                new Vector3(center.x + 1.15f, postHeight * 0.5f, center.z),
                new Vector3(0.22f, postHeight, 0.32f),
                frameMat);
            CreateSlab(parent, arch ? "BackendArchLintel" : "BackendDoorLintel",
                new Vector3(center.x, postHeight + 0.11f, center.z),
                new Vector3(2.55f, 0.22f, 0.34f),
                frameMat);
            CreateSlab(parent, "BackendThreshold",
                new Vector3(center.x, 0.035f, center.z),
                new Vector3(2.7f, 0.045f, 0.38f),
                _trimMat);
        }

        private void CreateFalseDoorCell(Transform parent, Vector3 center)
        {
            CreateSlab(parent, "BackendFalseDoorPanel",
                new Vector3(center.x, wallHeight * 0.45f, center.z - 2.28f),
                new Vector3(2.4f, 2.15f, 0.07f),
                _panelMat);
            CreateSlab(parent, "BackendFalseDoorHandle",
                new Vector3(center.x + 0.72f, wallHeight * 0.43f, center.z - 2.22f),
                new Vector3(0.12f, 0.16f, 0.10f),
                _trimMat);
        }

        private void CreatePitMarker(Transform parent, Vector3 center)
        {
            CreateSlab(parent, "BackendPitWarning",
                new Vector3(center.x, 0.035f, center.z),
                new Vector3(CellSize * 0.72f, 0.045f, CellSize * 0.72f),
                _warningMat);
            CreateSlab(parent, "BackendPitRail",
                new Vector3(center.x, 0.42f, center.z),
                new Vector3(CellSize * 0.86f, 0.18f, 0.16f),
                _trimMat);
        }

        private void CreateRampMarker(Transform parent, Vector3 center, int floorProfile)
        {
            Vector3 scale = (floorProfile == 4 || floorProfile == 7)
                ? new Vector3(CellSize * 0.90f, 0.16f, CellSize * 0.48f)
                : new Vector3(CellSize * 0.48f, 0.16f, CellSize * 0.90f);
            CreateSlab(parent, "BackendRampOrStair",
                new Vector3(center.x, 0.09f, center.z),
                scale,
                _trimMat);
        }

        private void CreateBackendLayoutPillarCollision(Transform parent, ChunkViewMsg cv, int layer)
        {
            if (!HasBackendLayout(cv))
                return;

            bool spawnChunk = IsSpawnChunk(cv);
            int grid = Mathf.Min(GridCells, cv.layoutGridSize);
            for (int x = 0; x < grid; x++)
            {
                for (int z = 0; z < grid; z++)
                {
                    int idx = z * cv.layoutGridSize + x;
                    if (idx >= cv.layoutCells.Length || (cv.layoutCells[idx] & CellPillar) == 0)
                        continue;
                    if (spawnChunk && IsSpawnCoreCell(x, z))
                        continue;

                    CreateCollisionBox(parent, "BackendPillarCollider",
                        new Vector3((x + 0.5f) * CellSize, wallHeight * 0.5f, (z + 0.5f) * CellSize),
                        new Vector3(PillarWidth, wallHeight, PillarWidth),
                        layer);
                }
            }
        }

        private void CreateBackendSpecialCellCollision(Transform parent, ChunkViewMsg cv, int layer)
        {
            if (!HasBackendLayout(cv))
                return;

            int grid = Mathf.Min(GridCells, cv.layoutGridSize);
            for (int x = 0; x < grid; x++)
            {
                for (int z = 0; z < grid; z++)
                {
                    int idx = z * cv.layoutGridSize + x;
                    if (idx >= cv.layoutCells.Length)
                        continue;

                    ushort flags = cv.layoutCells[idx];
                    Vector3 center = new Vector3((x + 0.5f) * CellSize, 0f, (z + 0.5f) * CellSize);
                    if ((flags & CellLowWall) != 0)
                    {
                        CreateCollisionBox(parent, "BackendLowWallCollider",
                            new Vector3(center.x, 0.55f, center.z),
                            new Vector3(CellSize * 0.82f, 1.1f, 0.30f),
                            layer);
                    }
                    else if ((flags & CellHalfWall) != 0)
                    {
                        CreateCollisionBox(parent, "BackendHalfWallCollider",
                            new Vector3(center.x, 0.85f, center.z),
                            new Vector3(0.34f, 1.7f, CellSize * 0.82f),
                            layer);
                    }
                    else if ((flags & (CellThinPartition | CellFalseDoor)) != 0)
                    {
                        CreateCollisionBox(parent, "BackendPartitionCollider",
                            new Vector3(center.x, wallHeight * 0.5f, center.z),
                            new Vector3(CellSize * 0.86f, wallHeight, 0.22f),
                            layer);
                    }
                }
            }
        }

        private static float FloorOffsetFor(ChunkViewMsg cv)
        {
            float baseOffset = cv.floorLevel * 1.5f;
            switch (cv.floorProfile)
            {
                case 1: return baseOffset - 0.25f;
                case 2: return baseOffset + 0.35f;
                default: return baseOffset;
            }
        }

        private (bool north, bool east, bool south, bool west) OpeningsFor(ChunkViewMsg cv)
        {
            if (cv.edgeOpenings != 0)
            {
                return (
                    (cv.edgeOpenings & EdgeNorth) != 0,
                    (cv.edgeOpenings & EdgeEast) != 0,
                    (cv.edgeOpenings & EdgeSouth) != 0,
                    (cv.edgeOpenings & EdgeWest) != 0
                );
            }

            bool n, e, s, w;

            switch (cv.templateId)
            {
                case 1: // hallway_straight: base = N+S open
                    n = true; e = false; s = true; w = false;
                    break;
                case 2: // hallway_corner: base = N+E open
                    n = true; e = true; s = false; w = false;
                    break;
                case 8: // hallway_t: base = N+E+S open
                    n = true; e = true; s = true; w = false;
                    break;
                case 6: // dead_end: base = S open only
                    n = false; e = false; s = true; w = false;
                    break;
                default:
                    // Open rooms/macros stay open; macro layout comes from backend adjacency.
                    n = true; e = true; s = true; w = true;
                    break;
            }

            int turns = Mathf.RoundToInt(cv.rotation / 90f) % 4;
            for (int i = 0; i < turns; i++)
            {
                bool oldN = n;
                n = w;
                w = s;
                s = e;
                e = oldN;
            }

            return (n, e, s, w);
        }

        private void CreateWallWithOpening(Transform parent, string name, int side, bool hasOpening, Material mat)
        {
            float gap = hasOpening ? Mathf.Max(DoorOpening, CorridorWidth + 0.8f) : 0f;
            float sideLen = (chunkSize - gap) * 0.5f;

            if (!hasOpening)
            {
                CreateFullWall(parent, name, side, chunkSize, mat);
                return;
            }

            if (side == 0 || side == 2)
            {
                float z = side == 0 ? 0f : chunkSize;
                CreateSlab(parent, name + "A",
                    new Vector3(sideLen * 0.5f, wallHeight * 0.5f, z),
                    new Vector3(sideLen, wallHeight, WallThickness),
                    mat);

                CreateSlab(parent, name + "B",
                    new Vector3(chunkSize - sideLen * 0.5f, wallHeight * 0.5f, z),
                    new Vector3(sideLen, wallHeight, WallThickness),
                    mat);
            }
            else
            {
                float x = side == 3 ? 0f : chunkSize;
                CreateSlab(parent, name + "A",
                    new Vector3(x, wallHeight * 0.5f, sideLen * 0.5f),
                    new Vector3(WallThickness, wallHeight, sideLen),
                    mat);

                CreateSlab(parent, name + "B",
                    new Vector3(x, wallHeight * 0.5f, chunkSize - sideLen * 0.5f),
                    new Vector3(WallThickness, wallHeight, sideLen),
                    mat);
            }
        }

        private void CreateFullWall(Transform parent, string name, int side, float length, Material mat)
        {
            if (side == 0 || side == 2)
            {
                float z = side == 0 ? 0f : chunkSize;
                CreateSlab(parent, name,
                    new Vector3(chunkSize * 0.5f, wallHeight * 0.5f, z),
                    new Vector3(length, wallHeight, WallThickness),
                    mat);
            }
            else
            {
                float x = side == 3 ? 0f : chunkSize;
                CreateSlab(parent, name,
                    new Vector3(x, wallHeight * 0.5f, chunkSize * 0.5f),
                    new Vector3(WallThickness, wallHeight, length),
                    mat);
            }
        }

        private static void CreateSlab(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            Destroy(go.GetComponent<Collider>());
        }

        // ── Phase 2.9B: chunk-local static mesh batching ──
        // Combine direct-child static slabs by material into one mesh each. Lights
        // (their fixture cubes live under a Light object) and the collision proxy
        // (no MeshRenderer) are excluded automatically; dynamic items/entities are
        // separate renderers, never children of this root.
        private void CombineChunkVisuals(GameObject root, ChunkViewMsg cv)
        {
            var cleanup = root.AddComponent<ChunkRenderCleanup>();
            var buckets = new Dictionary<Material, List<MeshFilter>>();
            int sourceVisuals = 0;

            foreach (Transform child in root.transform)
            {
                if (child.GetComponent<Light>() != null)
                    continue;
                var mf = child.GetComponent<MeshFilter>();
                var mr = child.GetComponent<MeshRenderer>();
                if (mf == null || mr == null || mf.sharedMesh == null || mr.sharedMaterial == null)
                    continue;
                var mat = mr.sharedMaterial;
                if (!buckets.TryGetValue(mat, out var list))
                {
                    list = new List<MeshFilter>();
                    buckets[mat] = list;
                }
                list.Add(mf);
                sourceVisuals++;
            }

            int combinedMeshes = 0;
            long verts = 0, indices = 0;
            var toDestroy = new List<GameObject>();
            foreach (var kv in buckets)
            {
                var list = kv.Value;
                var ci = new CombineInstance[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    ci[i].mesh = list[i].sharedMesh;
                    ci[i].transform = root.transform.worldToLocalMatrix * list[i].transform.localToWorldMatrix;
                    toDestroy.Add(list[i].gameObject);
                }
                var mesh = new Mesh { name = "ChunkBatch", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.CombineMeshes(ci, true, true);
                mesh.RecalculateBounds();
                cleanup.Meshes.Add(mesh);

                var go = new GameObject("Batch");
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = kv.Key;
                combinedMeshes++;
                verts += mesh.vertexCount;
                indices += mesh.GetIndexCount(0);
            }
            for (int i = 0; i < toDestroy.Count; i++)
                Destroy(toDestroy[i]);

            Debug.Log($"MPTRACE step=V29 event=chunk_batch_summary chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) batching={enableChunkMeshBatching} source_visuals={sourceVisuals} combined_meshes={combinedMeshes} material_buckets={buckets.Count} combined_vertices={verts} combined_indices={indices} generated_meshes_tracked={cleanup.Meshes.Count}");
        }

        // ── Phase 2.9C: procedural tiling textures (generated once, shared) ──
        private static void EnsureProceduralTextures()
        {
            if (_wallpaperTex != null && _carpetTex != null && _ceilingTex != null)
                return;
            _wallpaperTex = BuildModulationTex(11, 0.06f, 8, 0.90f);   // faint vertical wallpaper stripe
            _carpetTex = BuildModulationTex(23, 0.14f, 0, 1f);         // fibrous carpet noise
            _ceilingTex = BuildModulationTex(37, 0.05f, 0, 1f, true);  // speckled acoustic tile
        }

        private static uint TexNoise(int x, int y, int salt)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + salt * (int)0x9E3779B9);
                h = (h ^ (h >> 13)) * 1274126177u;
                return h ^ (h >> 16);
            }
        }

        // Near-white grayscale modulation map (texture * material color keeps it
        // subtle, low-contrast, never glossy). Optional vertical stripe + speckle.
        private static Texture2D BuildModulationTex(int salt, float noiseAmp, int stripeEvery, float stripeMul, bool speckle = false)
        {
            const int N = 64;
            var t = new Texture2D(N, N, TextureFormat.RGB24, true)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear
            };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float n = (1f - noiseAmp) + (TexNoise(x, y, salt) & 0xFFFF) / 65535f * noiseAmp;
                    float v = n;
                    if (stripeEvery > 0 && (x % stripeEvery) == 0)
                        v *= stripeMul;
                    if (speckle && (TexNoise(x, y, salt + 7) & 0xFFFF) / 65535f > 0.93f)
                        v *= 0.85f;
                    byte b = (byte)(Mathf.Clamp01(v) * 255f);
                    px[y * N + x] = new Color32(b, b, b, 255);
                }
            }
            t.SetPixels32(px);
            t.Apply(true);
            return t;
        }

        private static void ApplyTex(Material m, Texture2D tex, float tiling)
        {
            if (m == null || tex == null)
                return;
            var scale = new Vector2(tiling, tiling);
            if (m.HasProperty("_BaseMap"))
            {
                m.SetTexture("_BaseMap", tex);
                m.SetTextureScale("_BaseMap", scale);
            }
            if (m.HasProperty("_MainTex"))
                m.SetTexture("_MainTex", tex);
            m.mainTexture = tex;
            m.mainTextureScale = scale;
        }

        private static void TintChunk(GameObject root, Color tint)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                var mat = r.material;
                mat.color = mat.color * tint;
                r.material = mat;
            }
        }

        private void OnDestroy()
        {
            foreach (var go in _pool.Values)
            {
                if (go != null)
                    Destroy(go);
            }

            _pool.Clear();
        }
    }

    /// <summary>
    /// Holds the runtime-combined meshes for a chunk and destroys them when the
    /// chunk GameObject is unloaded, so batching does not leak Mesh instances.
    /// </summary>
    internal sealed class ChunkRenderCleanup : MonoBehaviour
    {
        public readonly List<Mesh> Meshes = new List<Mesh>();

        private void OnDestroy()
        {
            for (int i = 0; i < Meshes.Count; i++)
            {
                if (Meshes[i] != null)
                    Destroy(Meshes[i]);
            }
            Meshes.Clear();
        }
    }
}
