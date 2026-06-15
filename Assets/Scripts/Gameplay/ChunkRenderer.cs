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
        public bool enableTemplateProps = false;
        public bool enableCeilingGrid = false;
        public bool useBackendLayout = true;
        public bool enableWorldCollision = false;
        public bool showLayoutDebug = false;
        public bool showCellDebug = false;
        public bool showCollisionDebug = false;
        // Volumetric "Rubik grid" V0 — render backend-authored 3D architecture.
        public bool enableVolumetricGrid = true;
        // Legacy decorative VISFIX inter-layer volumes / validation showcase /
        // debug markers. OFF by default — superseded by the volumetric model.
        public bool enableLegacyInterLayerVolumes = false;
        // Phase 2.9B/C toggles.
        public bool enableChunkMeshBatching = true;
        public bool enableProceduralMaterialTiling = true;
        public bool enableCrossChunkZFightNudge = true;
        public bool showBatchDebug = false;
        public bool enableRuntimeMptraceLogs = false;
        // Realtime point lights are a major URP forward cost when ~49 chunks
        // are loaded; 2/chunk keeps the fixture rhythm at half the light count.
        public int maxLightsPerChunk = 2;
        public string worldCollisionLayerName = "WorldCollision";
        public bool renderOnlyMainVolumetricBand = false;

        [Header("Volumetric Performance")]
        public bool enableVolumetricLights = false;

        [Header("Streaming Performance")]
        // Per-frame budgets that amortize chunk build/destroy bursts. Building
        // a chunk is the expensive op (CreatePrimitive storm + CombineMeshes);
        // spawn (~49 chunks) and boundary crossings (~7-13) queue instead of
        // freezing one frame. Visuals appear progressively, nearest first.
        public int maxChunkBuildsPerFrame = 2;
        public int maxChunkDestroysPerFrame = 4;

        [Header("Debug / Offline")]
        [Tooltip("Sin backend: genera chunks sintéticos con seed fijo alrededor de (0,0,0).")]
        public bool offlineMode;
        public long offlineSeed = 42;
        [Tooltip("Radio en chunks: 3 = grid 7×7 = 49 chunks")]
        public int offlineRadius = 3;

        // Build/rebuild/destroy policy lives in ChunkVisualLifecycle; this
        // renderer only orchestrates snapshots and builds chunk visuals.
        private readonly ChunkVisualLifecycle _lifecycle = new ChunkVisualLifecycle();
        private long _lastProcessedTick = -1;
        private WorldStateMsg _offlineState;

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
        private Material _v30a2DebugBoundaryMat;
        private Material _v30a2DebugSpanMat;
        private Material _v30a2DebugAnchorMat;
        private Material _v30a2DebugDirectionMat;

        private float _nextSnapshotLogTime;
        private long _worldSeed;
        private bool _worldSeedLogged;

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
        private const int VolumeVisAtriumWalls = 1 << 0;
        private const int VolumeVisLowerRoomVisible = 1 << 1;
        private const int VolumeVisShaftWalls = 1 << 2;
        private const int VolumeVisRailings = 1 << 3;
        private const int VolumeVisRimTrims = 1 << 4;
        private const int VolumeVisPillarSpans = 1 << 5;
        private const int VolumeVisCeilingHints = 1 << 6;
        private const int VolumeVisUnderfloorHints = 1 << 7;
        private const int VolumeVisStackedAlignment = 1 << 8;
        private const int VolumeVisDepthCues = 1 << 9;
        private const long V30A2VisfixSeed = 7778;
        // Disabled by default: oversized seed-7778 validation markers were the
        // "cyan strips / yellow pillars / debug props" clutter. Replaced by the
        // backend-authored volumetric grid showcase.
        private const bool V30A2VisfixDebugMarkersEnabled = false;
        private const int V30A2VisfixConnectorX = 1;
        private const int V30A2VisfixConnectorZ = 3;
        private const int V30A2VisfixAtriumZ = 4;
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

        private void Trace(string message)
        {
            if (enableRuntimeMptraceLogs)
                Debug.Log(message);
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
            _v30a2DebugBoundaryMat = MaterialHelper.MakeEmissive(new Color(0.02f, 0.80f, 1.00f), 0.75f);
            _v30a2DebugSpanMat = MaterialHelper.MakeEmissive(new Color(1.00f, 0.65f, 0.05f), 0.85f);
            _v30a2DebugAnchorMat = MaterialHelper.MakeEmissive(new Color(0.15f, 1.00f, 0.30f), 0.85f);
            _v30a2DebugDirectionMat = MaterialHelper.MakeEmissive(new Color(1.00f, 0.10f, 0.70f), 0.80f);

            if (Camera.main != null)
            {
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
                Camera.main.backgroundColor = new Color(0.030f, 0.030f, 0.038f);
            }

            // RenderSettings (ambient / fog / skybox) are configured manually in the
            // Lighting window and must NOT be overridden here at runtime.

            // Decorative VISFIX debug/validation visuals are off by default; the
            // real architecture is the backend-authored volumetric grid.
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_debug_visuals_disabled_by_default legacy_inter_layer_volumes={enableLegacyInterLayerVolumes} visfix_debug_markers={V30A2VisfixDebugMarkersEnabled} volumetric_grid={enableVolumetricGrid}");
            Trace($"MPTRACE step=RUBIK event=unity_legacy_visfix_render_disabled disabled={(!enableLegacyInterLayerVolumes && !V30A2VisfixDebugMarkersEnabled)}");
            Trace($"MPTRACE step=RUBIK event=unity_legacy_interlayer_render_disabled disabled={!enableLegacyInterLayerVolumes}");
            Trace($"MPTRACE step=V30C event=unity_unified_volumetric_renderer_active enabled={enableVolumetricGrid}");
            Trace($"MPTRACE step=V30C event=unity_visfix_disabled_confirmed disabled={(!enableLegacyInterLayerVolumes && !V30A2VisfixDebugMarkersEnabled)}");
            Trace($"MPTRACE step=V30C event=unity_interlayer_legacy_disabled_confirmed disabled={!enableLegacyInterLayerVolumes}");

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
            if (offlineMode)
            {
                _worldSeed = offlineSeed;
                if (_offlineState == null)
                {
                    _offlineState = new WorldStateMsg { tick = 1, worldSeed = offlineSeed };
                    for (int cx = -offlineRadius; cx <= offlineRadius; cx++)
                        for (int cz = -offlineRadius; cz <= offlineRadius; cz++)
                            _offlineState.visibleChunks.Add(new ChunkViewMsg { pos = new[] { cx, cz } });
                }
                if (_offlineState.tick != _lastProcessedTick)
                {
                    _lastProcessedTick = _offlineState.tick;
                    int pcx = Mathf.FloorToInt(transform.position.x / chunkSize);
                    int pcz = Mathf.FloorToInt(transform.position.z / chunkSize);
                    _lifecycle.Reconcile(_offlineState.visibleChunks, pcx, pcz);
                }
                _lifecycle.ProcessQueues(
                    Mathf.Max(1, maxChunkBuildsPerFrame),
                    Mathf.Max(1, maxChunkDestroysPerFrame),
                    BuildChunk);
                return;
            }

            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null)
                return;

            _worldSeed = state.worldSeed;
            if (!_worldSeedLogged && state.worldSeed != 0)
            {
                _worldSeedLogged = true;
                Trace($"MPTRACE step=RUBIK event=unity_world_seed_received world_seed={state.worldSeed} world_revision={state.worldRevision}");
            }

            // Snapshots arrive at 10hz; reconcile (data-level diff + queueing)
            // only when a new one lands. No chunk is built or destroyed here.
            if (state.tick != _lastProcessedTick)
            {
                _lastProcessedTick = state.tick;
                int playerChunkX = Mathf.FloorToInt(transform.position.x / chunkSize);
                int playerChunkZ = Mathf.FloorToInt(transform.position.z / chunkSize);
                _lifecycle.Reconcile(state.visibleChunks, playerChunkX, playerChunkZ);
            }

            // Execute queued build/destroy work under the per-frame budget so
            // spawn/boundary bursts amortize across frames instead of stalling.
            _lifecycle.ProcessQueues(
                Mathf.Max(1, maxChunkBuildsPerFrame),
                Mathf.Max(1, maxChunkDestroysPerFrame),
                BuildChunk);

            if (Time.unscaledTime >= _nextSnapshotLogTime)
            {
                _nextSnapshotLogTime = Time.unscaledTime + 1f;
                if (enableRuntimeMptraceLogs)
                {
                    Debug.Log($"MPTRACE step=STREAMU event=chunk_lifecycle visible={state.visibleChunks.Count} pool={_lifecycle.PoolCount} pendingBuilds={_lifecycle.PendingBuilds} pendingDestroys={_lifecycle.PendingDestroys} built={_lifecycle.Built} rebuilt={_lifecycle.Rebuilt} destroyed={_lifecycle.Destroyed} builtThisFrame={_lifecycle.BuiltThisFrame} rebuiltThisFrame={_lifecycle.RebuiltThisFrame} destroyedThisFrame={_lifecycle.DestroyedThisFrame}");
                }
                _lifecycle.ResetCounters();
            }
        }



        private GameObject BuildChunk(ChunkViewMsg cv)
        {
            float layerY = Mathf.Abs(cv.layerY) > 0.001f ? cv.layerY : cv.layer * LayerHeight;
            var root = new GameObject($"Chunk_{cv.pos[0]}_{cv.layer}_{cv.pos[1]}");
            root.transform.position = new Vector3(cv.pos[0] * chunkSize, layerY, cv.pos[1] * chunkSize);

            // Volumetric "Rubik grid" host chunk: render the backend-authored 3D
            // architecture from cell/face data and skip the normal per-chunk
            // surface pipeline (the grid supplies every floor/ceiling/wall).
            if (enableVolumetricGrid && cv.HasVolumetricGrid)
            {
                Trace($"MPTRACE step=V30C event=unity_legacy_flat_renderer_bypassed_for_volumetric=true chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) source={cv.volumetricGrid.source}");
                BuildVolumetricChunk(root, cv);
                if (cv.state == "anchored")
                    TintChunk(root, new Color(0.6f, 0.8f, 1f, 1f));
                else if (cv.state == "stabilized")
                    TintChunk(root, new Color(0.8f, 1f, 0.8f, 1f));
                return root;
            }

            Trace($"MPTRACE step=V30C event=unity_legacy_flat_renderer_fallback_used chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) has_volumetric={cv.HasVolumetricGrid}");
            Trace($"MPTRACE step=AQ event=unity_chunk_template_applied chunk_id={Key(cv.pos[0], cv.layer, cv.pos[1])} template_id={cv.templateId} coord=({cv.pos[0]},{cv.layer},{cv.pos[1]}) rotation={cv.rotation}");

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
                Trace($"MPTRACE step=V210 event=unity_vertical_chunk_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) profile={fp} flags={cv.verticalFlags} raised={(fp == 2)} sunken={(fp == 1)} ramps={(fp == 3 || fp == 4 || fp == FloorConnectorUp || fp == FloorConnectorDown)} stairs={(fp == 6 || fp == 7)} pits={(fp == 5)} batched={enableChunkMeshBatching}");
            }

            if (cv.layer != 0 || (cv.verticalFlags & (V30AStackedCorridor | V30AAtriumVoidRoom | V30ADeepPrecipicePlaceholder | V30AGiantPillarHall | V30AConnector)) != 0)
            {
                Trace($"MPTRACE step=V30A event=unity_multilayer_chunk_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) kind={V30AKind(cv)} layer_y={layerY:F2} batched={enableChunkMeshBatching}");
            }
            if (HasV30AFlag(cv, V30AConnector))
            {
                int targetLayer = cv.floorProfile == FloorConnectorUp ? cv.layer + 1 : cv.layer - 1;
                string kind = cv.floorProfile == FloorConnectorUp ? "broad_stairwell" : "service_ramp";
                Trace($"MPTRACE step=V30A event=unity_connector_rendered from=({cv.pos[0]},{cv.layer},{cv.pos[1]}) to=({cv.pos[0]},{targetLayer},{cv.pos[1]}) kind={kind}");
            }

            return root;
        }

        // ─────────────────────────────────────────────────────────────
        // Volumetric "Rubik grid" V0 — backend-authored 3D architecture
        // ─────────────────────────────────────────────────────────────
        //
        // Renders real structural surfaces (floors, ceilings, walls, continuous
        // shaft walls, railings around vertical openings, and structural support
        // columns) derived from backend cell/face data. No decorative props, no
        // debug markers — the architecture IS the geometry.
        private void BuildVolumetricChunk(GameObject rootGo, ChunkViewMsg cv)
        {
            var grid = cv.volumetricGrid;
            Transform root = rootGo.transform;
            Vector3 rootPos = root.position;

            var profile = Level0Profile.FromSeedAndPos(_worldSeed, cv.pos[0], cv.pos[1]);
            Material wallMat = WallMaterialFor(cv.templateId, profile);
            Material floorMat = FloorMaterialFor(cv.templateId, profile);
            Material ceilingMat = CeilingMaterialFor(cv.templateId, profile);
            if (enableProceduralMaterialTiling)
            {
                ApplyTex(wallMat, _wallpaperTex, 2.5f);
                ApplyTex(floorMat, _carpetTex, 4f);
                ApplyTex(ceilingMat, _ceilingTex, 4f);
            }

            float cs = grid.cellSizeXZ;
            float lh = grid.layerHeight;
            Vector3 origin = grid.originWorld;
            // Floor sits a hair above the level, ceiling a hair below the next
            // level — so a room's floor and the room-below's ceiling never share
            // a plane (no z-fight, no black flicker faces).
            const float floorT = 0.10f, floorLift = 0.06f, ceilT = 0.10f, ceilDrop = 0.08f;
            // Thicker, readable rim kerb + guard rail around true openings.
            const float railH = 1.18f, railT = 0.20f, rimH = 0.40f, rimT = 0.52f, colW = 1.9f;

            // Phase 3.0C-FIX: normal Level 0 columns render their main band at
            // Backrooms scale (low drop ceiling), not the full 7 m band height.
            // RubikGrid showcase keeps the tall volumetric language.
            // BandHeightSpec (V30E): use backend-supplied room height when available
            // so TallPillarHall chunks render at 6.6 m instead of the 3.3 m fallback.
            bool level0Fix = grid.source != "RUBIKGRID_ADAPTER";
            float mainH = level0Fix
                ? (grid.heightBands.Count > 1
                    ? Mathf.Clamp(grid.heightBands[1].roomHeight, 2.5f, lh)
                    : Mathf.Min(ceilingHeight, lh))
                : lh;

            int floors = 0, ceilings = 0, walls = 0, shaftWalls = 0, railings = 0, rims = 0, pillars = 0;
            int strayBandFaces = 0;

            Trace($"MPTRACE step=V30C event=unity_unified_volumetric_renderer_active chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) source={grid.source} column_id={grid.columnId} column=({grid.columnCoord[0]},{grid.columnCoord[1]})");
            Trace($"MPTRACE step=V30C event=unity_unified_volumetric_layers_received chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) layers={grid.layerBands.Count} vertical_access={grid.verticalAccess.Count}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_unity_cells_received chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) cells={grid.cells.Length} dims=({grid.nx},{grid.ny},{grid.nz}) base_layer={grid.baseLayer} origin=({origin.x:F1},{origin.y:F1},{origin.z:F1})");

            // Coplanar floor/ceiling faces are merged into row runs (one slab
            // per contiguous same-material run) so large rooms/corridors read
            // as continuous surfaces instead of a per-cell checkerboard.
            var slabRuns = new Dictionary<(int y, byte kind, Material mat), List<(int x, int z)>>();

            foreach (var f in grid.faces)
            {
                if (renderOnlyMainVolumetricBand && level0Fix && f.y != 1)
                    continue;
                float cx0 = origin.x + f.x * cs;
                float cy0 = origin.y + f.y * lh;
                float cz0 = origin.z + f.z * cs;
                float ccx = cx0 + cs * 0.5f;
                float ccy = cy0 + lh * 0.5f;
                float ccz = cz0 + cs * 0.5f;

                // Owning cell occupancy drives the architectural material grammar
                // (room vs corridor vs atrium vs shaft vs service vs support).
                byte occ = grid.CellAt(f.x, f.y, f.z);

                if (level0Fix && f.y != 1)
                    strayBandFaces++;

                Vector3 wpos;
                Vector3 scale;
                string name;

                switch (f.kind)
                {
                    case VolumetricGridMsg.FaceFloor:
                    case VolumetricGridMsg.FaceCeiling:
                    {
                        // Defer to the merged-run pass below.
                        if (f.kind == VolumetricGridMsg.FaceFloor) floors++; else ceilings++;
                        Material hmat = VolumetricMaterialFor(occ, f.kind, floorMat, ceilingMat, wallMat);
                        var key = (f.y, f.kind, hmat);
                        if (!slabRuns.TryGetValue(key, out var list))
                            slabRuns[key] = list = new List<(int x, int z)>();
                        list.Add((f.x, f.z));
                        continue;
                    }
                    case VolumetricGridMsg.FaceWall:
                    case VolumetricGridMsg.FaceShaftWall:
                        bool isShaft = f.kind == VolumetricGridMsg.FaceShaftWall;
                        name = isShaft ? "VolShaftWall" : "VolWall";
                        if (isShaft) shaftWalls++; else walls++;
                        VolumetricWallTransform(f.dir, cx0, cy0, cz0, ccx, ccy, ccz, cs, lh, out wpos, out scale);
                        // Backrooms-scale walls in the main band; shaft/atrium
                        // walls keep the full band height (vertical continuity).
                        if (level0Fix && f.y == 1 && !isShaft)
                        {
                            scale.y = mainH;
                            wpos.y = cy0 + mainH * 0.5f;
                        }
                        break;
                    case VolumetricGridMsg.FaceRim:
                        name = "VolRim"; rims++;
                        VolumetricEdgeTransform(f.dir, cx0, cy0, cz0, ccx, ccz, cs,
                            rimH, rimT, cy0 + floorLift + 0.02f, out wpos, out scale);
                        break;
                    case VolumetricGridMsg.FaceRailing:
                        name = "VolRailing"; railings++;
                        VolumetricEdgeTransform(f.dir, cx0, cy0, cz0, ccx, ccz, cs,
                            railH, railT, cy0 + floorLift + rimH, out wpos, out scale);
                        break;
                    case VolumetricGridMsg.FaceSupportColumn:
                        wpos = new Vector3(ccx, ccy, ccz);
                        scale = new Vector3(colW, lh + 0.02f, colW);
                        if (level0Fix && f.y == 1)
                        {
                            // Pillars span floor → visible (low) ceiling only.
                            scale.y = mainH + 0.02f;
                            wpos.y = cy0 + mainH * 0.5f;
                        }
                        name = "VolSupportColumn"; pillars++;
                        break;
                    default:
                        continue;
                }

                Material mat = VolumetricMaterialFor(occ, f.kind, floorMat, ceilingMat, wallMat);
                CreateSlab(root, name, wpos - rootPos, scale, mat);
            }

            // Merged-run pass: one slab per contiguous same-row run.
            int mergedSlabs = 0;
            foreach (var kv in slabRuns)
            {
                var cells = kv.Value;
                cells.Sort((a, b) => a.z != b.z ? a.z - b.z : a.x - b.x);
                int i = 0;
                while (i < cells.Count)
                {
                    int z = cells[i].z;
                    int startX = cells[i].x;
                    int endX = startX;
                    int j = i + 1;
                    while (j < cells.Count && cells[j].z == z && cells[j].x == endX + 1)
                    {
                        endX = cells[j].x;
                        j++;
                    }
                    int runLen = endX - startX + 1;
                    bool isFloor = kv.Key.kind == VolumetricGridMsg.FaceFloor;
                    float bandY0 = origin.y + kv.Key.y * lh;
                    float visH = (level0Fix && kv.Key.y == 1) ? mainH : lh;
                    float y = isFloor ? bandY0 + floorLift : bandY0 + visH - ceilDrop;
                    var wpos = new Vector3(
                        origin.x + startX * cs + runLen * cs * 0.5f,
                        y,
                        origin.z + z * cs + cs * 0.5f);
                    var scale = new Vector3(runLen * cs, isFloor ? floorT : ceilT, cs);
                    CreateSlab(root, isFloor ? "VolFloor" : "VolCeiling", wpos - rootPos, scale, kv.Key.mat);
                    mergedSlabs++;
                    i = j;
                }
            }
            int internalFacesSuppressed = (floors + ceilings) - mergedSlabs;

            // Visual-grammar census from the cell window (rooms/corridors/etc.).
            int roomCells = 0, corridorCells = 0, atriumCells = 0, shaftCells = 0,
                serviceCells = 0, supportCells = 0, sealedCells = 0, falseCells = 0,
                ceilingVoidCells = 0, underfloorCells = 0, transitionCellsTotal = 0,
                anomalyCells = 0, dangerCells = 0, safeCells = 0;
            foreach (byte c in grid.cells)
            {
                switch (c)
                {
                    case VolumetricGridMsg.OccRoom: roomCells++; break;
                    case VolumetricGridMsg.OccCorridor: corridorCells++; break;
                    case VolumetricGridMsg.OccAtriumVoid: atriumCells++; break;
                    case VolumetricGridMsg.OccShaft: shaftCells++; break;
                    case VolumetricGridMsg.OccServiceSpace: serviceCells++; break;
                    case VolumetricGridMsg.OccSupportCore: supportCells++; break;
                    case VolumetricGridMsg.OccSealedRoom: sealedCells++; break;
                    case VolumetricGridMsg.OccFalseSpace: falseCells++; break;
                    case VolumetricGridMsg.OccCeilingVoid: ceilingVoidCells++; break;
                    case VolumetricGridMsg.OccUnderfloorService: underfloorCells++; break;
                    case VolumetricGridMsg.OccTransition: transitionCellsTotal++; break;
                    case VolumetricGridMsg.OccAnomaly: anomalyCells++; break;
                    case VolumetricGridMsg.OccDangerZone: dangerCells++; break;
                    case VolumetricGridMsg.OccSafeNode: safeCells++; break;
                }
            }
            // Transition edges: walkable window-boundary cells with an open
            // outward face (a seam to an adjacent showcase chunk or a doorway).
            int transitionCells = VolumetricTransitionCells(grid);

            int rendered = floors + ceilings + walls + shaftWalls + railings + rims + pillars;
            Trace($"MPTRACE step=V30C event=unity_unified_volumetric_faces_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) rendered={rendered} faces={grid.faces.Count} floors={floors} ceilings={ceilings} walls={walls + shaftWalls} railings={railings} rims={rims} pillars={pillars}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_unity_faces_generated chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) faces={grid.faces.Count} floors={floors} ceilings={ceilings} walls={walls} shaft_walls={shaftWalls} railings={railings} rims={rims} pillars={pillars}");
            Trace($"MPTRACE step=RUBIK event=unity_rubik_grid_faces_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) rendered={rendered} of_faces={grid.faces.Count}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_unity_vertical_openings_generated chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) railed_openings={railings} valid_vertical_openings={grid.validVerticalOpeningCount} vertical_connections={grid.verticalConnectionCount}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_unity_atrium_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) atrium_span={grid.atriumSpan} shaft_walls={shaftWalls}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_unity_structural_pillars_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) pillars={pillars}");

            // ── Phase 3.0B2 visual grammar telemetry ──
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_b2_faces_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) rendered={rendered}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_b2_rooms_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) rooms={roomCells} service={serviceCells} support={supportCells}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_b2_corridors_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) corridors={corridorCells}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_b2_atrium_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) atrium_cells={atriumCells} shaft_cells={shaftCells}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_b2_transitions_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) transition_cells={transitionCells}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_b2_debug_visuals_disabled legacy_visfix={!enableLegacyInterLayerVolumes} legacy_interlayer={!enableLegacyInterLayerVolumes}");

            if (enableVolumetricLights)
                VolumetricLights(rootGo, grid, rootPos, level0Fix, mainH);

            if (enableChunkMeshBatching)
                CombineChunkVisuals(rootGo, cv);

            // ── Phase 3.0C-FIX telemetry ──
            if (level0Fix)
            {
                // Faces outside the main band are only legitimate when explicit
                // vertical access opened the hint bands.
                bool artifactCheckPassed = strayBandFaces == 0 || grid.verticalAccess.Count > 0;
                Trace($"MPTRACE step=V30CFIX event=unity_level0_volumetric_visual_fix_active active=true chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) main_band_height={mainH:F1} source={grid.source}");
                Trace($"MPTRACE step=V30CFIX event=unity_level0_volumetric_faces_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) rendered={rendered} merged_slabs={mergedSlabs}");
                Trace($"MPTRACE step=V30CFIX event=unity_level0_volumetric_internal_faces_suppressed chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) suppressed={internalFacesSuppressed}");
                Trace($"MPTRACE step=V30CFIX event=unity_level0_volumetric_grid_artifact_check_passed chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) passed={artifactCheckPassed} stray_band_faces={strayBandFaces} vertical_access={grid.verticalAccess.Count}");
                if (IsSpawnChunk(cv))
                    Trace($"MPTRACE step=V30CFIX event=unity_level0_volumetric_spawn_visible_ready ready=true chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]})");
            }

            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_unity_showcase_visible_ready chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) total_faces={grid.faces.Count} open_cells={grid.openCellCount} solid_cells={grid.solidCellCount} world_seed={_worldSeed}");
            Trace($"MPTRACE step=RUBIK event=rubik_grid_v0_b2_showcase_visible_ready chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) rooms={roomCells} corridors={corridorCells} atrium={atriumCells} service={serviceCells} support={supportCells} transitions={transitionCells}");
            // Coherence summary: floors+ceilings enclose closed cells; holes only
            // at the backend-declared valid vertical openings.
            Trace($"MPTRACE step=RUBIK event=unity_showcase_surface_coherence_ready chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) floors={floors} ceilings={ceilings} walls={walls + shaftWalls} valid_vertical_openings={grid.validVerticalOpeningCount} legacy_visfix_render_disabled={!enableLegacyInterLayerVolumes} legacy_interlayer_render_disabled={!enableLegacyInterLayerVolumes}");
            Trace($"MPTRACE step=V30C event=unity_unified_volumetric_world_visible_ready chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) source={grid.source} rooms={roomCells} corridors={corridorCells} sealed={sealedCells} false_spaces={falseCells} ceiling_voids={ceilingVoidCells} underfloor={underfloorCells} transitions={transitionCellsTotal} anomalies={anomalyCells} dangers={dangerCells} safe_nodes={safeCells}");
        }

        // Vertical wall slab placed on a cell boundary in the given direction.
        private void VolumetricWallTransform(byte dir, float cx0, float cy0, float cz0,
            float ccx, float ccy, float ccz, float cs, float lh, out Vector3 wpos, out Vector3 scale)
        {
            switch (dir)
            {
                case VolumetricGridMsg.DirNorth: // +Z
                    wpos = new Vector3(ccx, ccy, cz0 + cs);
                    scale = new Vector3(cs, lh, WallThickness);
                    break;
                case VolumetricGridMsg.DirSouth: // -Z
                    wpos = new Vector3(ccx, ccy, cz0);
                    scale = new Vector3(cs, lh, WallThickness);
                    break;
                case VolumetricGridMsg.DirEast: // +X
                    wpos = new Vector3(cx0 + cs, ccy, ccz);
                    scale = new Vector3(WallThickness, lh, cs);
                    break;
                default: // West, -X
                    wpos = new Vector3(cx0, ccy, ccz);
                    scale = new Vector3(WallThickness, lh, cs);
                    break;
            }
        }

        // Warm fluorescent fill so each stacked layer is legible.
        // Level0-fix columns keep the main band low-ceiling, while upper/lower
        // bands use their full layer height for visibility during 3.0D validation.
        private void VolumetricLights(GameObject rootGo, VolumetricGridMsg grid, Vector3 rootPos,
            bool level0Fix, float mainH)
        {
            float cs = grid.cellSizeXZ;
            float lh = grid.layerHeight;
            Vector3 origin = grid.originWorld;
            var warm = new Color(0.78f, 0.72f, 0.52f);
            int yStart = 0;
            int yEnd = grid.ny;
            for (int y = yStart; y < yEnd; y++)
            {
                float layerVisibleHeight = (level0Fix && y == 1) ? mainH : lh;
                float ly = origin.y + y * lh + layerVisibleHeight - 0.55f;
                AddVolumetricLight(rootGo.transform,
                    new Vector3(origin.x + 5f * cs, ly, origin.z + 6.5f * cs) - rootPos, 1.05f, 20f, warm);
                AddVolumetricLight(rootGo.transform,
                    new Vector3(origin.x + 2.5f * cs, ly, origin.z + 3f * cs) - rootPos, 0.9f, 17f, warm);
                AddVolumetricLight(rootGo.transform,
                    new Vector3(origin.x + 7.5f * cs, ly, origin.z + 3f * cs) - rootPos, 0.9f, 17f, warm);
            }
        }

        private void AddVolumetricLight(Transform parent, Vector3 localPos, float intensity, float range, Color color)
        {
            var go = new GameObject("VolLight");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.shadows = LightShadows.None;
        }

        // Architectural material grammar: pick a material from the owning cell's
        // occupancy + the face kind. Service spaces read dark/technical; the
        // enclosed shaft uses a darker lining than the open atrium; support
        // cores are structural pillars. Reuses existing materials only.
        private Material VolumetricMaterialFor(byte occ, byte kind, Material floorMat, Material ceilingMat, Material wallMat)
        {
            bool service = occ == VolumetricGridMsg.OccServiceSpace || occ == VolumetricGridMsg.OccUnderfloorService;
            bool shaft = occ == VolumetricGridMsg.OccShaft;
            bool danger = occ == VolumetricGridMsg.OccDangerZone || occ == VolumetricGridMsg.OccAnomaly;
            bool safe = occ == VolumetricGridMsg.OccSafeNode;
            bool falseSpace = occ == VolumetricGridMsg.OccFalseSpace || occ == VolumetricGridMsg.OccCeilingVoid;
            bool sealedSpace = occ == VolumetricGridMsg.OccSealedRoom;
            switch (kind)
            {
                case VolumetricGridMsg.FaceFloor:
                    return danger ? _redRoomMat : safe ? _manilaMat : (service || shaft) ? _darkWallMat : floorMat;
                case VolumetricGridMsg.FaceCeiling:
                    return danger ? _redRoomMat
                        : shaft ? _darkWallMat
                        : service ? _humidWallMat
                        : falseSpace ? _ceilingSeamMat
                        : sealedSpace ? _humidWallMat
                        : ceilingMat;

                case VolumetricGridMsg.FaceWall:
                    return danger ? _redRoomMat
                        : safe ? _manilaMat
                        : shaft ? _darkWallMat
                        : service ? _humidWallMat
                        : falseSpace ? _ceilingSeamMat
                        : sealedSpace ? _humidWallMat
                        : wallMat;
                case VolumetricGridMsg.FaceShaftWall:
                    // Enclosed service shaft = darker lining; open atrium = lighter.
                    return shaft ? _darkWallMat : _humidWallMat;
                case VolumetricGridMsg.FaceRim:
                    return _baseboardMat;
                case VolumetricGridMsg.FaceRailing:
                    return _trimMat;
                case VolumetricGridMsg.FaceSupportColumn:
                    return _pillarMat;
                default:
                    return wallMat;
            }
        }

        // Count walkable window-boundary cells whose outward face is open — i.e.
        // a clean seam to an adjacent showcase chunk or a perimeter doorway into
        // normal Level 0. (Informational; the boundary is otherwise walled.)
        private int VolumetricTransitionCells(VolumetricGridMsg grid)
        {
            var walls = new HashSet<int>();
            foreach (var f in grid.faces)
                if (f.kind == VolumetricGridMsg.FaceWall || f.kind == VolumetricGridMsg.FaceShaftWall)
                    walls.Add(WallKey(f.x, f.y, f.z, f.dir));

            int count = 0;
            for (int y = 0; y < grid.ny; y++)
                for (int z = 0; z < grid.nz; z++)
                    for (int x = 0; x < grid.nx; x++)
                    {
                        byte occ = grid.CellAt(x, y, z);
                        bool walkable = occ == VolumetricGridMsg.OccRoom
                            || occ == VolumetricGridMsg.OccCorridor
                            || occ == VolumetricGridMsg.OccServiceSpace
                            || occ == VolumetricGridMsg.OccTransition
                            || occ == VolumetricGridMsg.OccDangerZone
                            || occ == VolumetricGridMsg.OccSafeNode;
                        if (!walkable) continue;
                        if (x == 0 && !walls.Contains(WallKey(x, y, z, VolumetricGridMsg.DirWest))) count++;
                        if (x == grid.nx - 1 && !walls.Contains(WallKey(x, y, z, VolumetricGridMsg.DirEast))) count++;
                        if (z == 0 && !walls.Contains(WallKey(x, y, z, VolumetricGridMsg.DirSouth))) count++;
                        if (z == grid.nz - 1 && !walls.Contains(WallKey(x, y, z, VolumetricGridMsg.DirNorth))) count++;
                    }
            return count;
        }

        private static int WallKey(int x, int y, int z, int dir) => ((x * 16 + y) * 16 + z) * 8 + dir;

        // Edge element (rim kerb / guard rail) on the walkable cell's boundary
        // facing a vertical void. `baseY` is the element's bottom; it is centred
        // at baseY + height/2 and runs along the shared boundary.
        private void VolumetricEdgeTransform(byte dir, float cx0, float cy0, float cz0,
            float ccx, float ccz, float cs, float height, float thickness, float baseY,
            out Vector3 wpos, out Vector3 scale)
        {
            float cy = baseY + height * 0.5f;
            switch (dir)
            {
                case VolumetricGridMsg.DirNorth:
                    wpos = new Vector3(ccx, cy, cz0 + cs);
                    scale = new Vector3(cs, height, thickness);
                    break;
                case VolumetricGridMsg.DirSouth:
                    wpos = new Vector3(ccx, cy, cz0);
                    scale = new Vector3(cs, height, thickness);
                    break;
                case VolumetricGridMsg.DirEast:
                    wpos = new Vector3(cx0 + cs, cy, ccz);
                    scale = new Vector3(thickness, height, cs);
                    break;
                default: // West
                    wpos = new Vector3(cx0, cy, ccz);
                    scale = new Vector3(thickness, height, cs);
                    break;
            }
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
            Trace($"MPTRACE step=V26 event=unity_chunk_render_summary chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) backend_layout={backendLayout} walls={walls} doors={doors} arches={arches} lowwalls={lowWalls} halfwalls={halfWalls} pillars={pillars} false_doors={falseDoors} lights={Mathf.Max(1, maxLightsPerChunk)} vertical={vertical} fallback={!backendLayout} spawn_chunk={spawnChunk}");
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
            CreateInterLayerVolumes(parent, cv, floorOffset);

            if (floorProfile != 0 || cv.verticalFlags != 0)
            {
                Trace($"MPTRACE step=V210B event=unity_vertical_geometry_built chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) profile={floorProfile} raised={(floorProfile == 2)} sunken={(floorProfile == 1)} ramps={(floorProfile == 3 || floorProfile == 4 || floorProfile == FloorConnectorUp || floorProfile == FloorConnectorDown)} stairs={(floorProfile == 6 || floorProfile == 7)} pits={(floorProfile == 5)} batched={enableChunkMeshBatching}");
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

            Trace($"MPTRACE step=V30AFIX event=connector_volume_built from=({cv.pos[0]},{cv.layer},{cv.pos[1]}) to_layer={targetLayer} kind={kind} y0={y0:F1} y1={y1:F1} steps={steps}");
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

        private static bool HasInterLayerOpening(ChunkViewMsg cv)
        {
            if (cv.interLayerVolumes == null)
                return false;

            foreach (var volume in cv.interLayerVolumes)
            {
                if (volume == null)
                    continue;
                if (volume.kind == "ATRIUM_STACK" || volume.kind == "SERVICE_SHAFT" || volume.kind == "OVERLOOK_ROOM")
                    return true;
            }
            return false;
        }

        private static bool HasFloorOpening(ChunkViewMsg cv) => HasVerticalOpening(cv) || (cv.layer >= 0 && HasInterLayerOpening(cv));

        private static bool HasCeilingOpening(ChunkViewMsg cv) =>
            HasVerticalOpening(cv) ||
            (cv.layer < 0 && HasInterLayerOpening(cv)) ||
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
            Trace($"MPTRACE step=V30AFIX event=atrium_volume_built chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) opening=({span:F0},{span:F0}) shaft_height={shaftHeight:F1} rails=4 lower_visible={lowerVisible}");
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
                Trace($"MPTRACE step=V30AFIX event=giant_pillars_built chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) count={pillarCount} height={h:F1}");
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
                Trace($"MPTRACE step=V30AFIX event=branch_layer_style_applied chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) branch_type={branchType} layer_y={LayerRootY(cv):F2}");
            }
        }

        private void CreateInterLayerVolumes(Transform parent, ChunkViewMsg cv, float floorOffset)
        {
            // Legacy decorative VISFIX path. Disabled by default — the real 3D
            // architecture now comes from the backend volumetric grid, rendered
            // by BuildVolumetricChunk. Kept behind a flag for debugging only.
            if (!enableLegacyInterLayerVolumes)
                return;
            if (cv.interLayerVolumes == null || cv.interLayerVolumes.Count == 0)
                return;

            int renderersBefore = CountMeshRenderers(parent);
            int rendered = 0;
            int debugMarkers = 0;
            bool visfixShowcase = IsV30A2VisfixShowcaseChunk(cv);
            bool materialValid = _floorMat != null && _ceilingMat != null && _trimMat != null &&
                                 _pillarMat != null && _baseboardMat != null && _darkWallMat != null &&
                                 _humidWallMat != null;
            Trace($"MPTRACE step=V30A2 event=v30a2_visfix_renderer_material_valid chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) material_valid={materialValid} floor={(_floorMat != null)} trim={(_trimMat != null)} pillar={(_pillarMat != null)} wall={(_humidWallMat != null)}");

            if (visfixShowcase)
            {
                int validationObjects = CreateV30A2VisfixShowcaseArchitecture(parent, cv, floorOffset);
                Trace($"MPTRACE step=V30A2 event=v30a2_visfix_renderer_showcase_visible_ready chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) validation_scale_objects={validationObjects} world_seed={_worldSeed}");
            }

            foreach (var volume in cv.interLayerVolumes)
            {
                if (volume == null || string.IsNullOrEmpty(volume.kind))
                    continue;

                VolumeFootprint(volume, out float minX, out float minZ, out float maxX, out float maxZ);
                Vector3 worldPos = parent.TransformPoint(new Vector3((minX + maxX) * 0.5f, floorOffset, (minZ + maxZ) * 0.5f));
                Trace($"MPTRACE step=V30A2 event=v30a2_visfix_renderer_volume_render_start volume_id={volume.volumeId} kind={volume.kind} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) flags={volume.visualFlags}");
                Trace($"MPTRACE step=V30A2 event=v30a2_visfix_renderer_volume_world_position volume_id={volume.volumeId} kind={volume.kind} world=({worldPos.x:F1},{worldPos.y:F1},{worldPos.z:F1}) footprint=({minX:F1},{minZ:F1})..({maxX:F1},{maxZ:F1})");
                Trace($"MPTRACE step=V30A2 event=vertical_volume_kind volume_id={volume.volumeId} kind={volume.kind} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]})");
                Trace($"MPTRACE step=V30A2 event=vertical_volume_layers volume_id={volume.volumeId} layers=[{VolumeLayersLabel(volume)}] chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]})");
                if (!string.IsNullOrEmpty(volume.futureAudioHint))
                    Trace($"MPTRACE step=V30A2 event=future_audio_hint_registered volume_id={volume.volumeId} hint={volume.futureAudioHint} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]})");

                switch (volume.kind)
                {
                    case "ATRIUM_STACK":
                        CreateVolumeAtriumStack(parent, cv, volume, floorOffset);
                        rendered++;
                        break;
                    case "SERVICE_SHAFT":
                        CreateVolumeServiceShaft(parent, cv, volume, floorOffset);
                        rendered++;
                        break;
                    case "STACKED_CORRIDOR_PAIR":
                        CreateVolumeStackedCorridorPair(parent, cv, volume, floorOffset);
                        rendered++;
                        break;
                    case "OVERLOOK_ROOM":
                        CreateVolumeOverlookRoom(parent, cv, volume, floorOffset);
                        rendered++;
                        break;
                    case "GIANT_PILLAR_SPAN":
                        CreateVolumeGiantPillarSpan(parent, cv, volume, floorOffset);
                        rendered++;
                        break;
                    case "CEILING_ACTIVITY_ZONE":
                        CreateVolumeCeilingActivityZone(parent, cv, volume, floorOffset);
                        rendered++;
                        break;
                    case "UNDERFLOOR_SERVICE_ZONE":
                        CreateVolumeUnderfloorServiceZone(parent, cv, volume, floorOffset);
                        rendered++;
                        break;
                }

                if (IsV30A2VisfixDebugEnabled(cv))
                    debugMarkers += CreateV30A2VisfixDebugMarkers(parent, cv, volume, floorOffset, minX, minZ, maxX, maxZ);
            }

            if (rendered > 0)
            {
                int rendererObjectsCreated = CountMeshRenderers(parent) - renderersBefore;
                Trace($"MPTRACE step=V30A2 event=v30a2_visfix_renderer_objects_created chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) volumes_rendered={rendered} renderer_objects_created={rendererObjectsCreated} debug_markers={debugMarkers}");
                Trace($"MPTRACE step=V30A2 event=unity_inter_layer_volumes_rendered chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) count={rendered}");
            }
        }

        private static int CountMeshRenderers(Transform root)
        {
            return root.GetComponentsInChildren<MeshRenderer>(true).Length;
        }

        private bool IsV30A2VisfixDebugEnabled(ChunkViewMsg cv)
        {
            return V30A2VisfixDebugMarkersEnabled &&
                   _worldSeed == V30A2VisfixSeed &&
                   cv.interLayerVolumes != null &&
                   cv.interLayerVolumes.Count > 0;
        }

        private bool IsV30A2VisfixShowcaseChunk(ChunkViewMsg cv)
        {
            return _worldSeed == V30A2VisfixSeed &&
                   cv.pos[0] == V30A2VisfixConnectorX &&
                   (cv.pos[1] == V30A2VisfixConnectorZ || cv.pos[1] == V30A2VisfixAtriumZ) &&
                   (cv.layer == 0 || cv.layer == -1) &&
                   cv.interLayerVolumes != null &&
                   cv.interLayerVolumes.Count > 0;
        }

        private int CreateV30A2VisfixShowcaseArchitecture(Transform parent, ChunkViewMsg cv, float floorOffset)
        {
            int before = CountMeshRenderers(parent);
            bool atrium = cv.pos[1] == V30A2VisfixAtriumZ;
            float minX = atrium ? 6.0f : 10.0f;
            float maxX = atrium ? 44.0f : 40.0f;
            float minZ = atrium ? 6.0f : 3.5f;
            float maxZ = atrium ? 44.0f : 46.5f;
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;
            float shaftCenterY = cv.layer >= 0 ? floorOffset - LayerHeight * 0.5f : floorOffset + LayerHeight * 0.5f;
            float lowerFloorY = cv.layer >= 0 ? floorOffset - LayerHeight + 0.08f : floorOffset + 0.06f;
            float ceilingHintY = cv.layer >= 0 ? ceilingHeight - 0.24f : LayerHeight + ceilingHeight - 0.24f;

            CreateSlab(parent, "V30A2_ShowcaseShaftWall_N", new Vector3(centerX, shaftCenterY, minZ), new Vector3(spanX + 1.6f, LayerHeight, 0.72f), _humidWallMat);
            CreateSlab(parent, "V30A2_ShowcaseShaftWall_S", new Vector3(centerX, shaftCenterY, maxZ), new Vector3(spanX + 1.6f, LayerHeight, 0.72f), _humidWallMat);
            CreateSlab(parent, "V30A2_ShowcaseShaftWall_W", new Vector3(minX, shaftCenterY, centerZ), new Vector3(0.72f, LayerHeight, spanZ + 1.6f), _humidWallMat);
            CreateSlab(parent, "V30A2_ShowcaseShaftWall_E", new Vector3(maxX, shaftCenterY, centerZ), new Vector3(0.72f, LayerHeight, spanZ + 1.6f), _humidWallMat);

            CreateSlab(parent, "V30A2_ShowcaseRim_N", new Vector3(centerX, floorOffset + 0.16f, minZ - 0.54f), new Vector3(spanX + 2.4f, 0.30f, 1.08f), _baseboardMat);
            CreateSlab(parent, "V30A2_ShowcaseRim_S", new Vector3(centerX, floorOffset + 0.16f, maxZ + 0.54f), new Vector3(spanX + 2.4f, 0.30f, 1.08f), _baseboardMat);
            CreateSlab(parent, "V30A2_ShowcaseRim_W", new Vector3(minX - 0.54f, floorOffset + 0.16f, centerZ), new Vector3(1.08f, 0.30f, spanZ + 2.4f), _baseboardMat);
            CreateSlab(parent, "V30A2_ShowcaseRim_E", new Vector3(maxX + 0.54f, floorOffset + 0.16f, centerZ), new Vector3(1.08f, 0.30f, spanZ + 2.4f), _baseboardMat);

            if (cv.layer >= 0)
            {
                CreateSlab(parent, "V30A2_ShowcaseRail_N", new Vector3(centerX, floorOffset + 0.82f, minZ - 0.88f), new Vector3(spanX + 1.6f, 1.38f, 0.34f), _trimMat);
                CreateSlab(parent, "V30A2_ShowcaseRail_S", new Vector3(centerX, floorOffset + 0.82f, maxZ + 0.88f), new Vector3(spanX + 1.6f, 1.38f, 0.34f), _trimMat);
                CreateSlab(parent, "V30A2_ShowcaseRail_W", new Vector3(minX - 0.88f, floorOffset + 0.82f, centerZ), new Vector3(0.34f, 1.38f, spanZ + 1.6f), _trimMat);
                CreateSlab(parent, "V30A2_ShowcaseRail_E", new Vector3(maxX + 0.88f, floorOffset + 0.82f, centerZ), new Vector3(0.34f, 1.38f, spanZ + 1.6f), _trimMat);
            }

            foreach (var p in new[] { new Vector2(0.10f, 0.10f), new Vector2(0.90f, 0.10f), new Vector2(0.10f, 0.90f), new Vector2(0.90f, 0.90f) })
            {
                float x = Mathf.Lerp(minX, maxX, p.x);
                float z = Mathf.Lerp(minZ, maxZ, p.y);
                CreateSlab(parent, "V30A2_ShowcaseLayerSpanPillar", new Vector3(x, shaftCenterY, z), new Vector3(3.2f, LayerHeight + 0.6f, 3.2f), _pillarMat);
                CreateSlab(parent, "V30A2_ShowcasePillarCapUpper", new Vector3(x, floorOffset + 0.18f, z), new Vector3(4.4f, 0.36f, 4.4f), _baseboardMat);
                CreateSlab(parent, "V30A2_ShowcasePillarCapLower", new Vector3(x, lowerFloorY + 0.14f, z), new Vector3(4.2f, 0.28f, 4.2f), _baseboardMat);
            }

            CreateSlab(parent, "V30A2_ShowcaseLowerRoomFloor", new Vector3(centerX, lowerFloorY, centerZ), new Vector3(spanX * 0.82f, 0.10f, spanZ * 0.82f), _darkStainMat);
            CreateSlab(parent, "V30A2_ShowcaseLowerRoomBackWall", new Vector3(centerX, lowerFloorY + wallHeight * 0.48f, maxZ - 0.65f), new Vector3(spanX * 0.74f, wallHeight * 0.96f, 0.48f), _darkWallMat);
            CreateSlab(parent, "V30A2_ShowcaseLowerRoomSideWall", new Vector3(minX + 0.65f, lowerFloorY + wallHeight * 0.42f, centerZ), new Vector3(0.44f, wallHeight * 0.84f, spanZ * 0.62f), _darkWallMat);
            CreateSlab(parent, "V30A2_ShowcaseDepthLightCue", new Vector3(centerX + spanX * 0.18f, lowerFloorY + 0.16f, centerZ - spanZ * 0.20f), new Vector3(spanX * 0.26f, 0.06f, 0.64f), _overlitMat);

            CreateSlab(parent, "V30A2_ShowcaseCeilingActivity_X", new Vector3(centerX, ceilingHintY, centerZ), new Vector3(spanX * 0.88f, 0.20f, 0.42f), _ceilingSeamMat);
            CreateSlab(parent, "V30A2_ShowcaseCeilingActivity_Z", new Vector3(centerX, ceilingHintY - 0.18f, centerZ), new Vector3(0.42f, 0.18f, spanZ * 0.88f), _ceilingSeamMat);
            CreateSlab(parent, "V30A2_ShowcaseUnderfloorServiceTray", new Vector3(centerX, cv.layer >= 0 ? floorOffset - 0.55f : ceilingHeight + 0.34f, centerZ), new Vector3(spanX * 0.68f, 0.22f, 0.42f), _darkWallMat);
            CreateSlab(parent, "V30A2_ShowcaseUnderfloorPipe", new Vector3(centerX - spanX * 0.28f, cv.layer >= 0 ? floorOffset - 0.86f : ceilingHeight + 0.06f, centerZ + spanZ * 0.18f), new Vector3(0.28f, 0.28f, spanZ * 0.56f), _humidWallMat);

            if (!atrium)
            {
                CreateSlab(parent, "V30A2_ShowcaseStackedCorridorCue_L", new Vector3(minX + 1.8f, floorOffset + 0.10f, centerZ), new Vector3(0.56f, 0.20f, spanZ * 0.94f), _baseboardMat);
                CreateSlab(parent, "V30A2_ShowcaseStackedCorridorCue_R", new Vector3(maxX - 1.8f, floorOffset + 0.10f, centerZ), new Vector3(0.56f, 0.20f, spanZ * 0.94f), _baseboardMat);
                CreateSlab(parent, "V30A2_ShowcaseStackedCorridorOverhead", new Vector3(centerX, ceilingHintY - 0.38f, centerZ), new Vector3(spanX * 0.72f, 0.18f, 0.58f), _trimMat);
            }

            return CountMeshRenderers(parent) - before;
        }

        private int CreateV30A2VisfixDebugMarkers(
            Transform parent,
            ChunkViewMsg cv,
            InterLayerVolumeMsg volume,
            float floorOffset,
            float minX,
            float minZ,
            float maxX,
            float maxZ)
        {
            var root = new GameObject($"V30A2_DebugVolume_{volume.volumeId}_{volume.kind}");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Vector3.zero;

            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;
            float y = floorOffset + 0.32f;
            float shaftCenterY = cv.layer >= 0 ? floorOffset - LayerHeight * 0.5f : floorOffset + LayerHeight * 0.5f;
            int count = 0;

            count += CreateMarkerSlab(root.transform, "Footprint_N", new Vector3(centerX, y, minZ), new Vector3(spanX, 0.12f, 0.16f), _v30a2DebugBoundaryMat);
            count += CreateMarkerSlab(root.transform, "Footprint_S", new Vector3(centerX, y, maxZ), new Vector3(spanX, 0.12f, 0.16f), _v30a2DebugBoundaryMat);
            count += CreateMarkerSlab(root.transform, "Footprint_W", new Vector3(minX, y, centerZ), new Vector3(0.16f, 0.12f, spanZ), _v30a2DebugBoundaryMat);
            count += CreateMarkerSlab(root.transform, "Footprint_E", new Vector3(maxX, y, centerZ), new Vector3(0.16f, 0.12f, spanZ), _v30a2DebugBoundaryMat);
            count += CreateMarkerSlab(root.transform, "VerticalSpan_Center", new Vector3(centerX, shaftCenterY, centerZ), new Vector3(0.38f, LayerHeight, 0.38f), _v30a2DebugSpanMat);

            foreach (var p in new[] { new Vector2(minX, minZ), new Vector2(maxX, minZ), new Vector2(minX, maxZ), new Vector2(maxX, maxZ) })
                count += CreateMarkerSlab(root.transform, "VerticalSpan_Corner", new Vector3(p.x, shaftCenterY, p.y), new Vector3(0.24f, LayerHeight, 0.24f), _v30a2DebugSpanMat);

            count += CreateMarkerSlab(root.transform, "AnchorChunk", new Vector3(centerX, floorOffset + 1.80f, centerZ), new Vector3(1.6f, 1.6f, 1.6f), _v30a2DebugAnchorMat);
            count += CreateMarkerSlab(root.transform, "ConnectionHint", new Vector3(centerX, floorOffset + 2.55f, Mathf.Min(chunkSize - 2.0f, centerZ + spanZ * 0.33f)), new Vector3(0.50f, 0.38f, Mathf.Max(3.0f, spanZ * 0.45f)), _v30a2DebugDirectionMat);

            Trace($"MPTRACE step=V30A2 event=v30a2_visfix_renderer_debug_marker_created chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) volume_id={volume.volumeId} kind={volume.kind} marker_count={count}");
            return count;
        }

        private int CreateMarkerSlab(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat != null ? mat : _warningMat;
            Destroy(go.GetComponent<Collider>());
            return 1;
        }

        private void CreateVolumeAtriumStack(Transform parent, ChunkViewMsg cv, InterLayerVolumeMsg volume, float floorOffset)
        {
            VolumeFootprint(volume, out float minX, out float minZ, out float maxX, out float maxZ);
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;
            float shaftHeight = LayerHeight;
            float shaftY = cv.layer >= 0 ? floorOffset - shaftHeight * 0.5f : floorOffset + shaftHeight * 0.5f;

            if (HasVolumeFlag(volume, VolumeVisAtriumWalls))
            {
                CreateSlab(parent, "VolumeAtriumWall_N", new Vector3(centerX, shaftY, minZ), new Vector3(spanX + 0.35f, shaftHeight, 0.30f), _humidWallMat);
                CreateSlab(parent, "VolumeAtriumWall_S", new Vector3(centerX, shaftY, maxZ), new Vector3(spanX + 0.35f, shaftHeight, 0.30f), _humidWallMat);
                CreateSlab(parent, "VolumeAtriumWall_W", new Vector3(minX, shaftY, centerZ), new Vector3(0.30f, shaftHeight, spanZ + 0.35f), _humidWallMat);
                CreateSlab(parent, "VolumeAtriumWall_E", new Vector3(maxX, shaftY, centerZ), new Vector3(0.30f, shaftHeight, spanZ + 0.35f), _humidWallMat);
                CreateSlab(parent, "VolumeAtriumCornerPost_NW", new Vector3(minX, shaftY, minZ), new Vector3(0.55f, shaftHeight, 0.55f), _pillarMat);
                CreateSlab(parent, "VolumeAtriumCornerPost_SE", new Vector3(maxX, shaftY, maxZ), new Vector3(0.55f, shaftHeight, 0.55f), _pillarMat);
            }

            if (HasVolumeFlag(volume, VolumeVisRimTrims))
                CreateVolumeRim(parent, minX, minZ, maxX, maxZ, floorOffset + 0.065f, _baseboardMat);
            if (HasVolumeFlag(volume, VolumeVisRailings) && cv.layer >= 0)
                CreateVolumeRail(parent, minX, minZ, maxX, maxZ, floorOffset + 0.72f, _trimMat);
            if (HasVolumeFlag(volume, VolumeVisLowerRoomVisible) && cv.layer >= 0)
                CreateLowerRoomDepthCue(parent, centerX, centerZ, spanX, spanZ, floorOffset - LayerHeight + 0.08f);
            if (HasVolumeFlag(volume, VolumeVisCeilingHints) && cv.layer < 0)
                CreateVolumeCeilingTrace(parent, centerX, centerZ, spanX, spanZ, ceilingHeight + 0.08f);

            Trace($"MPTRACE step=V30A2 event=shared_opening_built source=unity volume_id={volume.volumeId} kind={volume.kind} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) footprint=({minX:F1},{minZ:F1})..({maxX:F1},{maxZ:F1})");
            if (HasVolumeFlag(volume, VolumeVisLowerRoomVisible))
                Trace($"MPTRACE step=V30A2 event=lower_room_visible_from_above source=unity volume_id={volume.volumeId} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) visible_from_layer={(cv.layer >= 0)}");
        }

        private void CreateVolumeServiceShaft(Transform parent, ChunkViewMsg cv, InterLayerVolumeMsg volume, float floorOffset)
        {
            VolumeFootprint(volume, out float minX, out float minZ, out float maxX, out float maxZ);
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;
            float shaftHeight = LayerHeight;
            float shaftY = cv.layer >= 0 ? floorOffset - shaftHeight * 0.5f : floorOffset + shaftHeight * 0.5f;

            if (HasVolumeFlag(volume, VolumeVisShaftWalls))
            {
                CreateSlab(parent, "VolumeShaftWall_L", new Vector3(minX, shaftY, centerZ), new Vector3(0.34f, shaftHeight, spanZ), _darkWallMat);
                CreateSlab(parent, "VolumeShaftWall_R", new Vector3(maxX, shaftY, centerZ), new Vector3(0.34f, shaftHeight, spanZ), _darkWallMat);
                CreateSlab(parent, "VolumeShaftEndFrame_A", new Vector3(centerX, shaftY, minZ + 0.10f), new Vector3(spanX, shaftHeight, 0.24f), _humidWallMat);
                CreateSlab(parent, "VolumeShaftEndFrame_B", new Vector3(centerX, shaftY, maxZ - 0.10f), new Vector3(spanX, shaftHeight, 0.24f), _humidWallMat);
            }
            if (HasVolumeFlag(volume, VolumeVisRailings))
            {
                CreateSlab(parent, "VolumeShaftRail_L", new Vector3(minX + 0.48f, floorOffset + 0.70f, centerZ), new Vector3(0.22f, 1.22f, spanZ * 0.94f), _trimMat);
                CreateSlab(parent, "VolumeShaftRail_R", new Vector3(maxX - 0.48f, floorOffset + 0.70f, centerZ), new Vector3(0.22f, 1.22f, spanZ * 0.94f), _trimMat);
            }
            if (HasVolumeFlag(volume, VolumeVisRimTrims))
            {
                CreateSlab(parent, "VolumeShaftRim_A", new Vector3(centerX, floorOffset + 0.07f, minZ + 0.22f), new Vector3(spanX, 0.14f, 0.34f), _baseboardMat);
                CreateSlab(parent, "VolumeShaftRim_B", new Vector3(centerX, floorOffset + 0.07f, maxZ - 0.22f), new Vector3(spanX, 0.14f, 0.34f), _baseboardMat);
            }
            if (HasVolumeFlag(volume, VolumeVisDepthCues))
                CreateSlab(parent, "VolumeShaftDepthPlane", new Vector3(centerX, cv.layer >= 0 ? floorOffset - LayerHeight + 0.06f : floorOffset + LayerHeight - 0.06f, centerZ), new Vector3(spanX * 0.78f, 0.06f, spanZ * 0.80f), _darkStainMat);

            Trace($"MPTRACE step=V30A2 event=shared_opening_built source=unity volume_id={volume.volumeId} kind={volume.kind} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]})");
        }

        private void CreateVolumeStackedCorridorPair(Transform parent, ChunkViewMsg cv, InterLayerVolumeMsg volume, float floorOffset)
        {
            VolumeFootprint(volume, out float minX, out float minZ, out float maxX, out float maxZ);
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float spanZ = maxZ - minZ;

            if (HasVolumeFlag(volume, VolumeVisStackedAlignment))
            {
                CreateSlab(parent, "VolumeStackedAlignment_L", new Vector3(minX + 0.35f, floorOffset + 0.055f, centerZ), new Vector3(0.28f, 0.11f, spanZ * 0.92f), _baseboardMat);
                CreateSlab(parent, "VolumeStackedAlignment_R", new Vector3(maxX - 0.35f, floorOffset + 0.055f, centerZ), new Vector3(0.28f, 0.11f, spanZ * 0.92f), _baseboardMat);
                CreateSlab(parent, "VolumeStackedCeilingTrack", new Vector3(centerX, ceilingHeight - 0.45f, centerZ), new Vector3((maxX - minX) * 0.68f, 0.14f, 0.26f), _trimMat);
            }
            if (HasVolumeFlag(volume, VolumeVisCeilingHints))
                CreateVolumeCeilingTrace(parent, centerX, centerZ, maxX - minX, spanZ, ceilingHeight - 0.14f);

            Trace($"MPTRACE step=V30A2 event=stacked_corridor_pair_built source=unity volume_id={volume.volumeId} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]})");
        }

        private void CreateVolumeOverlookRoom(Transform parent, ChunkViewMsg cv, InterLayerVolumeMsg volume, float floorOffset)
        {
            VolumeFootprint(volume, out float minX, out float minZ, out float maxX, out float maxZ);
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;

            if (HasVolumeFlag(volume, VolumeVisLowerRoomVisible) && cv.layer >= 0)
            {
                CreateLowerRoomDepthCue(parent, centerX, centerZ, spanX, spanZ, floorOffset - LayerHeight + 0.10f);
                CreateSlab(parent, "VolumeLowerRoomBackWall", new Vector3(centerX, floorOffset - LayerHeight + wallHeight * 0.45f, maxZ - 0.20f), new Vector3(spanX * 0.82f, wallHeight * 0.9f, 0.20f), _humidWallMat);
            }
            if (HasVolumeFlag(volume, VolumeVisRailings) && cv.layer >= 0)
                CreateVolumeRail(parent, minX, minZ, maxX, maxZ, floorOffset + 0.68f, _trimMat);
            if (HasVolumeFlag(volume, VolumeVisRimTrims))
                CreateVolumeRim(parent, minX, minZ, maxX, maxZ, floorOffset + 0.075f, _baseboardMat);

            Trace($"MPTRACE step=V30A2 event=lower_room_visible_from_above source=unity volume_id={volume.volumeId} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]})");
        }

        private void CreateVolumeGiantPillarSpan(Transform parent, ChunkViewMsg cv, InterLayerVolumeMsg volume, float floorOffset)
        {
            VolumeFootprint(volume, out float minX, out float minZ, out float maxX, out float maxZ);
            float spanHeight = LayerHeight + ceilingHeight;
            float centerY = cv.layer >= 0 ? floorOffset - LayerHeight * 0.5f : floorOffset + spanHeight * 0.5f;
            int count = 0;
            foreach (var p in new[] { new Vector2(0.18f, 0.18f), new Vector2(0.82f, 0.18f), new Vector2(0.18f, 0.82f), new Vector2(0.82f, 0.82f) })
            {
                float x = Mathf.Lerp(minX, maxX, p.x);
                float z = Mathf.Lerp(minZ, maxZ, p.y);
                CreateSlab(parent, "VolumePillarSpan", new Vector3(x, centerY, z), new Vector3(2.1f, spanHeight, 2.1f), _pillarMat);
                CreateSlab(parent, "VolumePillarSpanCap", new Vector3(x, floorOffset + 0.10f, z), new Vector3(3.0f, 0.20f, 3.0f), _baseboardMat);
                count++;
            }
            Trace($"MPTRACE step=V30A2 event=pillar_span_built source=unity volume_id={volume.volumeId} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) count={count}");
        }

        private void CreateVolumeCeilingActivityZone(Transform parent, ChunkViewMsg cv, InterLayerVolumeMsg volume, float floorOffset)
        {
            VolumeFootprint(volume, out float minX, out float minZ, out float maxX, out float maxZ);
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;

            if (HasVolumeFlag(volume, VolumeVisCeilingHints))
            {
                CreateVolumeCeilingTrace(parent, centerX, centerZ, spanX, spanZ, ceilingHeight - 0.20f);
                CreateSlab(parent, "VolumeCeilingActivityPanel_A", new Vector3(centerX - spanX * 0.18f, ceilingHeight - 0.42f, centerZ), new Vector3(spanX * 0.32f, 0.12f, 0.32f), _overlitMat);
                CreateSlab(parent, "VolumeCeilingActivityPanel_B", new Vector3(centerX + spanX * 0.18f, ceilingHeight - 0.58f, centerZ + spanZ * 0.20f), new Vector3(spanX * 0.28f, 0.10f, 0.28f), _darkWallMat);
            }
            Trace($"MPTRACE step=V30A2 event=ceiling_activity_hint_built source=unity volume_id={volume.volumeId} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]})");
        }

        private void CreateVolumeUnderfloorServiceZone(Transform parent, ChunkViewMsg cv, InterLayerVolumeMsg volume, float floorOffset)
        {
            VolumeFootprint(volume, out float minX, out float minZ, out float maxX, out float maxZ);
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;

            if (HasVolumeFlag(volume, VolumeVisUnderfloorHints))
            {
                float y = cv.layer >= 0 ? floorOffset - 0.20f : ceilingHeight + 0.18f;
                CreateSlab(parent, "VolumeUnderfloorGrate_A", new Vector3(centerX - spanX * 0.22f, floorOffset + 0.025f, centerZ), new Vector3(spanX * 0.18f, 0.05f, spanZ * 0.72f), _trimMat);
                CreateSlab(parent, "VolumeUnderfloorGrate_B", new Vector3(centerX + spanX * 0.22f, floorOffset + 0.026f, centerZ), new Vector3(spanX * 0.18f, 0.05f, spanZ * 0.72f), _trimMat);
                CreateSlab(parent, "VolumeUnderfloorCableTray", new Vector3(centerX, y, centerZ), new Vector3(spanX * 0.70f, 0.12f, 0.22f), _darkWallMat);
                CreateSlab(parent, "VolumeUnderfloorPipe", new Vector3(centerX - spanX * 0.18f, y - 0.22f, centerZ + spanZ * 0.24f), new Vector3(0.18f, 0.18f, spanZ * 0.50f), _humidWallMat);
            }
            Trace($"MPTRACE step=V30A2 event=underfloor_service_hint_built source=unity volume_id={volume.volumeId} chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]})");
        }

        private static bool HasVolumeFlag(InterLayerVolumeMsg volume, int flag) => (volume.visualFlags & flag) != 0;

        private static string VolumeLayersLabel(InterLayerVolumeMsg volume)
        {
            if (volume.involvedLayers == null || volume.involvedLayers.Length == 0)
                return "";
            return string.Join(",", volume.involvedLayers);
        }

        private static int ArrayAt(int[] values, int index, int fallback)
        {
            return values != null && index >= 0 && index < values.Length ? values[index] : fallback;
        }

        private static void VolumeFootprint(InterLayerVolumeMsg volume, out float minX, out float minZ, out float maxX, out float maxZ)
        {
            int minCellX = Mathf.Clamp(ArrayAt(volume.footprintCellMin, 0, 3), 0, GridCells - 1);
            int minCellZ = Mathf.Clamp(ArrayAt(volume.footprintCellMin, 1, 3), 0, GridCells - 1);
            int maxCellX = Mathf.Clamp(ArrayAt(volume.footprintCellMax, 0, 7), minCellX + 1, GridCells);
            int maxCellZ = Mathf.Clamp(ArrayAt(volume.footprintCellMax, 1, 7), minCellZ + 1, GridCells);
            minX = minCellX * CellSize;
            minZ = minCellZ * CellSize;
            maxX = maxCellX * CellSize;
            maxZ = maxCellZ * CellSize;
        }

        private void CreateVolumeRim(Transform parent, float minX, float minZ, float maxX, float maxZ, float y, Material mat)
        {
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;
            CreateSlab(parent, "VolumeRim_N", new Vector3(centerX, y, minZ - 0.16f), new Vector3(spanX + 0.60f, 0.14f, 0.32f), mat);
            CreateSlab(parent, "VolumeRim_S", new Vector3(centerX, y, maxZ + 0.16f), new Vector3(spanX + 0.60f, 0.14f, 0.32f), mat);
            CreateSlab(parent, "VolumeRim_W", new Vector3(minX - 0.16f, y, centerZ), new Vector3(0.32f, 0.14f, spanZ + 0.60f), mat);
            CreateSlab(parent, "VolumeRim_E", new Vector3(maxX + 0.16f, y, centerZ), new Vector3(0.32f, 0.14f, spanZ + 0.60f), mat);
        }

        private void CreateVolumeRail(Transform parent, float minX, float minZ, float maxX, float maxZ, float y, Material mat)
        {
            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;
            CreateSlab(parent, "VolumeRail_N", new Vector3(centerX, y, minZ - 0.36f), new Vector3(spanX + 0.30f, 1.12f, 0.22f), mat);
            CreateSlab(parent, "VolumeRail_S", new Vector3(centerX, y, maxZ + 0.36f), new Vector3(spanX + 0.30f, 1.12f, 0.22f), mat);
            CreateSlab(parent, "VolumeRail_W", new Vector3(minX - 0.36f, y, centerZ), new Vector3(0.22f, 1.12f, spanZ + 0.30f), mat);
            CreateSlab(parent, "VolumeRail_E", new Vector3(maxX + 0.36f, y, centerZ), new Vector3(0.22f, 1.12f, spanZ + 0.30f), mat);
        }

        private void CreateLowerRoomDepthCue(Transform parent, float centerX, float centerZ, float spanX, float spanZ, float y)
        {
            CreateSlab(parent, "VolumeLowerRoomFloorVisible", new Vector3(centerX, y, centerZ), new Vector3(spanX * 0.82f, 0.06f, spanZ * 0.82f), _darkStainMat);
            CreateSlab(parent, "VolumeLowerRoomFloorPatch", new Vector3(centerX - spanX * 0.15f, y + 0.07f, centerZ + spanZ * 0.18f), new Vector3(spanX * 0.36f, 0.04f, spanZ * 0.26f), _stainMat);
            CreateSlab(parent, "VolumeLowerRoomLightCue", new Vector3(centerX + spanX * 0.20f, y + 0.10f, centerZ - spanZ * 0.20f), new Vector3(spanX * 0.22f, 0.035f, 0.28f), _overlitMat);
        }

        private void CreateVolumeCeilingTrace(Transform parent, float centerX, float centerZ, float spanX, float spanZ, float y)
        {
            CreateSlab(parent, "VolumeCeilingTrace_X", new Vector3(centerX, y, centerZ), new Vector3(spanX * 0.82f, 0.10f, 0.18f), _ceilingSeamMat);
            CreateSlab(parent, "VolumeCeilingTrace_Z", new Vector3(centerX, y - 0.12f, centerZ), new Vector3(0.18f, 0.10f, spanZ * 0.82f), _ceilingSeamMat);
            CreateSlab(parent, "VolumeCeilingPipe", new Vector3(centerX - spanX * 0.22f, y - 0.28f, centerZ), new Vector3(0.14f, 0.14f, spanZ * 0.62f), _trimMat);
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
            Trace($"MPTRACE step=V27 event=unity_edge_chunk_render_summary chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) template={cv.templateId} backend_layout={cv.HasBackendLayout} has_edges={edgeLayout} cells={cv.cellFlags.Length} v_edges={cv.verticalEdges.Length} h_edges={cv.horizontalEdges.Length} walls={c.walls} doors={c.doors} arches={c.arches} lowwalls={c.lowWalls} halfwalls={c.halfWalls} partitions={c.partitions} false_doors={c.falseDoors} pillars={pillars} fallback={!edgeLayout}");
        }

        private void LogSpawnChunkRendered(ChunkViewMsg cv, bool edgeLayout, EdgeRenderCounts c)
        {
            int pillars = CountCellFlag(cv, CellPillar);
            Trace($"MPTRACE step=V27 event=unity_spawn_chunk_rendered backend_layout={cv.HasBackendLayout} has_edges={edgeLayout} walls={c.walls} doors={c.doors} arches={c.arches} lowwalls={c.lowWalls} halfwalls={c.halfWalls} pillars={pillars} fallback={!edgeLayout}");
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

            Trace($"MPTRACE step=V29 event=chunk_batch_summary chunk=({cv.pos[0]},{cv.layer},{cv.pos[1]}) batching={enableChunkMeshBatching} source_visuals={sourceVisuals} combined_meshes={combinedMeshes} material_buckets={buckets.Count} combined_vertices={verts} combined_indices={indices} generated_meshes_tracked={cleanup.Meshes.Count}");
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
            _lifecycle.DestroyAll();
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
