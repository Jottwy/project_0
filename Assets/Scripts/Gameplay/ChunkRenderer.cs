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
        public float wallHeight = 3.1f;
        public float ceilingHeight = 3.2f;

        [Header("Level 0 Backend-Driven Visuals")]
        public bool enableBackroomsDressing = true;
        public bool enableTemplateProps = true;
        public bool enableCeilingGrid = true;
        public bool useBackendLayout = true;
        public bool enableWorldCollision = true;
        public bool showLayoutDebug = false;
        public bool showCellDebug = false;
        public bool showCollisionDebug = false;
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
        private Material _overlitMat;
        private Material _darkWallMat;
        private Material _humidWallMat;
        private Material _redRoomMat;
        private Material _manilaMat;
        private Material _cleaningMat;
        private Material _warningMat;

        private float _nextSnapshotLogTime;
        private long _worldSeed;

        private const float CellSize = 5f;
        private const int GridCells = 10;
        private const float WallThickness = 0.16f;
        private const float CorridorWidth = 5.2f;
        private const float DoorOpening = 3.0f;
        private const float FloorPanelSize = CellSize * 2f;
        private const ushort CellWalkable = 1 << 0;
        private const ushort CellWall = 1 << 1;
        private const ushort CellPillar = 1 << 2;
        private const ushort CellBlocked = 1 << 3;
        private const ushort CellRamp = 1 << 5;
        private const ushort CellPit = 1 << 6;
        private const ushort CellDoor = 1 << 10;
        private const ushort CellArch = 1 << 11;
        private const ushort CellLowWall = 1 << 12;
        private const ushort CellHalfWall = 1 << 13;
        private const ushort CellThinPartition = 1 << 14;
        private const ushort CellFalseDoor = 1 << 15;
        private const int EdgeNorth = 1 << 0;
        private const int EdgeEast = 1 << 1;
        private const int EdgeSouth = 1 << 2;
        private const int EdgeWest = 1 << 3;

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        private void Start()
        {
            _floorMat = MaterialHelper.MakeLit(new Color(0.72f, 0.68f, 0.55f));
            _ceilingMat = MaterialHelper.MakeLit(new Color(0.88f, 0.86f, 0.80f));
            _workbenchMat = MaterialHelper.MakeLit(new Color(0.45f, 0.30f, 0.18f));

            _storageMat = MaterialHelper.MakeLit(new Color(0.42f, 0.41f, 0.33f));
            _safeMat = MaterialHelper.MakeLit(new Color(0.66f, 0.66f, 0.49f));
            _dangerMat = MaterialHelper.MakeLit(new Color(0.30f, 0.21f, 0.17f));
            _trimMat = MaterialHelper.MakeLit(new Color(0.28f, 0.25f, 0.18f));
            _pillarMat = MaterialHelper.MakeLit(new Color(0.58f, 0.54f, 0.41f));
            _stainMat = MaterialHelper.MakeLit(new Color(0.42f, 0.36f, 0.25f));
            _darkStainMat = MaterialHelper.MakeLit(new Color(0.20f, 0.18f, 0.13f));
            _wetMat = MaterialHelper.MakeLit(new Color(0.30f, 0.29f, 0.22f));
            _blackMoldMat = MaterialHelper.MakeLit(new Color(0.09f, 0.085f, 0.07f));
            _boxMat = MaterialHelper.MakeLit(new Color(0.38f, 0.31f, 0.21f));
            _arrowMat = MaterialHelper.MakeLit(new Color(0.18f, 0.14f, 0.09f));
            _panelMat = MaterialHelper.MakeLit(new Color(0.70f, 0.67f, 0.50f));
            _baseboardMat = MaterialHelper.MakeLit(new Color(0.38f, 0.33f, 0.22f));
            _seamMat = MaterialHelper.MakeLit(new Color(0.58f, 0.54f, 0.40f));
            _overlitMat = MaterialHelper.MakeLit(new Color(0.90f, 0.86f, 0.60f));
            _darkWallMat = MaterialHelper.MakeLit(new Color(0.36f, 0.32f, 0.22f));
            _humidWallMat = MaterialHelper.MakeLit(new Color(0.56f, 0.53f, 0.38f));
            _redRoomMat = MaterialHelper.MakeLit(new Color(0.42f, 0.13f, 0.10f));
            _manilaMat = MaterialHelper.MakeLit(new Color(0.72f, 0.61f, 0.38f));
            _cleaningMat = MaterialHelper.MakeLit(new Color(0.50f, 0.54f, 0.42f));
            _warningMat = MaterialHelper.MakeLit(new Color(0.16f, 0.10f, 0.07f));

            if (Camera.main != null)
            {
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
                Camera.main.backgroundColor = new Color(0.030f, 0.030f, 0.038f);
            }
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
                long key = Key(cv.pos[0], cv.pos[1]);
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
            var root = new GameObject($"Chunk_{cv.pos[0]}_{cv.pos[1]}");
            root.transform.position = new Vector3(cv.pos[0] * chunkSize, 0f, cv.pos[1] * chunkSize);

            Debug.Log($"MPTRACE step=AQ event=unity_chunk_template_applied chunk_id={Key(cv.pos[0], cv.pos[1])} template_id={cv.templateId} coord=({cv.pos[0]},{cv.pos[1]}) rotation={cv.rotation}");

            var profile = Level0Profile.FromSeedAndPos(_worldSeed, cv.pos[0], cv.pos[1]);

            CreateModularSurfaces(root.transform, cv, profile);
            CreateCeilingDetails(root.transform, cv.templateId, profile);
            CreateTemplateWalls(root.transform, cv, WallMaterialFor(cv.templateId, profile));
            CreateInteriorLayout(root.transform, cv, profile, WallMaterialFor(cv.templateId, profile));
            CreateWallGrime(root.transform, cv.templateId, profile);

            if (enableBackroomsDressing)
                CreateBackroomsDressing(root.transform, cv, profile);

            if (enableTemplateProps && !(useBackendLayout && HasBackendLayout(cv) && (cv.templateId == 9 || cv.templateId == 10)))
                CreateTemplateProps(root.transform, cv, profile);

            CreateLighting(root.transform, cv.templateId, profile);

            if (enableWorldCollision)
                CreateCollisionProxy(root.transform, cv, profile);

            if (cv.hasWorkbench)
            {
                CreateSlab(root.transform, "Workbench",
                    new Vector3(chunkSize * 0.5f, 0.5f, chunkSize * 0.5f),
                    new Vector3(2f, 1f, 1.2f),
                    _workbenchMat);
            }

            if (cv.state == "anchored")
                TintChunk(root, new Color(0.6f, 0.8f, 1f, 1f));
            else if (cv.state == "stabilized")
                TintChunk(root, new Color(0.8f, 1f, 0.8f, 1f));

            LogChunkRenderSummary(cv);

            return root;
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

            bool spawnChunk = cv.pos[0] == 0 && cv.pos[1] == 0;
            Debug.Log($"MPTRACE step=V26 event=unity_chunk_render_summary chunk=({cv.pos[0]},{cv.pos[1]}) backend_layout={backendLayout} walls={walls} doors={doors} arches={arches} lowwalls={lowWalls} halfwalls={halfWalls} pillars={pillars} false_doors={falseDoors} lights={Mathf.Max(1, maxLightsPerChunk)} vertical={vertical} fallback={!backendLayout} spawn_chunk={spawnChunk}");
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
                case 6: return MaterialHelper.MakeLit(new Color(0.28f, 0.25f, 0.16f));
                case 7: return MaterialHelper.MakeLit(new Color(0.22f, 0.18f, 0.14f));
                case 9:
                case 10: return MaterialHelper.MakeLit(new Color(0.58f, 0.54f, 0.42f));
                case 11: return MaterialHelper.MakeLit(new Color(0.64f, 0.59f, 0.41f));
                case 12: return MaterialHelper.MakeLit(new Color(0.43f, 0.45f, 0.34f));
                case 13: return MaterialHelper.MakeLit(new Color(0.40f, 0.38f, 0.28f));
                case 14: return MaterialHelper.MakeLit(new Color(0.18f, 0.16f, 0.11f));
                case 15: return _manilaMat;
                case 16: return _redRoomMat;
                case 17: return MaterialHelper.MakeLit(new Color(0.39f, 0.35f, 0.25f));
                default:
                    float h = profile.humidity;
                    float g = profile.grime;
                    return MaterialHelper.MakeLit(new Color(
                        Mathf.Clamp01(0.72f - h * 0.16f - g * 0.05f),
                        Mathf.Clamp01(0.68f - h * 0.12f - g * 0.04f),
                        Mathf.Clamp01(0.55f - h * 0.09f - g * 0.03f)
                    ));
            }
        }

        private Material WallMaterialFor(int templateId, Level0Profile profile)
        {
            if (templateId == 6) return MaterialHelper.MakeLit(new Color(0.48f, 0.44f, 0.28f));
            if (templateId == 7 || templateId == 14) return _darkWallMat;
            if (templateId == 5) return MaterialHelper.MakeLit(new Color(0.78f, 0.76f, 0.58f));
            if (templateId == 9 || templateId == 10) return MaterialHelper.MakeLit(new Color(0.66f, 0.62f, 0.46f));
            if (templateId == 11) return MaterialHelper.MakeLit(new Color(0.70f, 0.65f, 0.46f));
            if (templateId == 12) return MaterialHelper.MakeLit(new Color(0.54f, 0.57f, 0.43f));
            if (templateId == 13) return _humidWallMat;
            if (templateId == 15) return MaterialHelper.MakeLit(new Color(0.78f, 0.65f, 0.40f));
            if (templateId == 16) return MaterialHelper.MakeLit(new Color(0.34f, 0.10f, 0.08f));
            if (templateId == 17) return MaterialHelper.MakeLit(new Color(0.52f, 0.48f, 0.34f));

            float shift = profile.wallToneShift;
            float h = profile.humidity * 0.45f;
            float g = profile.grime * 0.20f;

            return MaterialHelper.MakeLit(new Color(
                Mathf.Clamp01(0.82f + shift - h * 0.55f - g),
                Mathf.Clamp01(0.80f + shift * 0.5f - h * 0.40f - g * 0.8f),
                Mathf.Clamp01(0.72f - h * 0.30f - g * 0.5f)
            ));
        }

        private Material CeilingMaterialFor(int templateId, Level0Profile profile)
        {
            if (templateId == 6 || templateId == 7 || templateId == 14)
                return MaterialHelper.MakeLit(new Color(0.42f, 0.39f, 0.28f));
            if (templateId == 16)
                return MaterialHelper.MakeLit(new Color(0.33f, 0.12f, 0.09f));
            if (templateId == 15)
                return MaterialHelper.MakeLit(new Color(0.72f, 0.62f, 0.40f));

            float h = profile.humidity * 0.15f;
            return MaterialHelper.MakeLit(new Color(0.88f - h, 0.86f - h, 0.80f - h));
        }

        // ─────────────────────────────────────────────────────────────
        // Dirt / ceiling / wall detail
        // ─────────────────────────────────────────────────────────────

        private void CreateModularSurfaces(Transform parent, ChunkViewMsg cv, Level0Profile profile)
        {
            int templateId = cv.templateId;
            Material floorMat = FloorMaterialFor(templateId, profile);
            Material ceilingMat = CeilingMaterialFor(templateId, profile);
            float floorOffset = FloorOffsetFor(cv);

            for (int x = 0; x < GridCells; x += 2)
            {
                for (int z = 0; z < GridCells; z += 2)
                {
                    float cx = (x + 1f) * CellSize;
                    float cz = (z + 1f) * CellSize;

                    CreateSlab(parent, "FloorPanel",
                        new Vector3(cx, floorOffset - 0.045f, cz),
                        new Vector3(FloorPanelSize - 0.05f, 0.09f, FloorPanelSize - 0.05f),
                        floorMat);

                    CreateSlab(parent, "CeilingPanel",
                        new Vector3(cx, ceilingHeight, cz),
                        new Vector3(FloorPanelSize - 0.08f, 0.08f, FloorPanelSize - 0.08f),
                        ceilingMat);
                }
            }

            if (enableCeilingGrid)
            {
                Material gridMat = profile.grime > 0.55f || templateId == 12 || templateId == 13 || templateId == 14 ? _stainMat : _panelMat;
                for (int i = 1; i < GridCells; i++)
                {
                    float p = i * CellSize;
                    CreateSlab(parent, "FloorSeam_X",
                        new Vector3(p, 0.016f, chunkSize * 0.5f),
                        new Vector3(0.018f, 0.010f, chunkSize),
                        _seamMat);
                    CreateSlab(parent, "FloorSeam_Z",
                        new Vector3(chunkSize * 0.5f, 0.017f, p),
                        new Vector3(chunkSize, 0.010f, 0.018f),
                        _seamMat);
                    CreateSlab(parent, "CeilingGrid_X",
                        new Vector3(p, ceilingHeight - 0.100f, chunkSize * 0.5f),
                        new Vector3(0.030f, 0.018f, chunkSize),
                        gridMat);
                    CreateSlab(parent, "CeilingGrid_Z",
                        new Vector3(chunkSize * 0.5f, ceilingHeight - 0.101f, p),
                        new Vector3(chunkSize, 0.018f, 0.030f),
                        gridMat);
                }
            }

            CreateFloorStains(parent, templateId, profile);
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
                CreateSlab(parent, "DarkPool",
                    new Vector3(chunkSize * 0.5f, 0.030f, chunkSize * 0.5f),
                    new Vector3(14f, 0.012f, 14f),
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

        private void CreateLighting(Transform parent, int templateId, Level0Profile profile)
        {
            int placed = 0;
            for (int row = 0; row < 6; row++)
            {
                for (int col = 0; col < 6; col++)
                {
                    if (placed >= Mathf.Max(1, maxLightsPerChunk))
                        break;
                    float lx = 4f + row * (chunkSize - 8f) / 5f;
                    float lz = 4f + col * (chunkSize - 8f) / 5f;
                    int lightIdx = row * 6 + col;
                    bool affected = IsLightAffected(lightIdx, profile, templateId);
                    bool unpoweredFixture = (row + col) % 2 != 0 && templateId != 15 && templateId != 16;
                    CreateLight(parent, new Vector3(lx, ceilingHeight - 0.24f, lz), profile, affected, templateId, unpoweredFixture);
                    placed++;
                }
                if (placed >= Mathf.Max(1, maxLightsPerChunk))
                    break;
            }

            bool wantsLongFixture = templateId == 0 || templateId == 3 || templateId == 9 ||
                                    templateId == 10 || templateId == 11 || templateId == 15 ||
                                    templateId == 17;
            if (placed < Mathf.Max(1, maxLightsPerChunk) && wantsLongFixture)
            {
                CreateLongFluorescent(parent,
                    new Vector3(chunkSize * 0.5f, ceilingHeight - 0.12f, chunkSize * 0.5f),
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
                fixtureEmission = templateId == 15 ? 0.65f : templateId == 16 ? 0.9f : 1.0f;
            }

            var fixture = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fixture.transform.SetParent(lightObj.transform, false);
            fixture.transform.localScale = new Vector3(1.65f, 0.045f, 0.22f);
            fixture.GetComponent<Renderer>().sharedMaterial =
                fixtureEmission > 0f
                    ? MaterialHelper.MakeEmissive(fixtureColor, fixtureEmission)
                    : MaterialHelper.MakeLit(fixtureColor);
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
                    light.intensity = templateId == 15 ? 0.55f : templateId == 16 ? 0.80f : 0.95f;
                    light.range = templateId == 15 ? 11f : templateId == 16 ? 9f : 14f;
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

        private void CreateBackroomsDressing(Transform parent, ChunkViewMsg cv, Level0Profile profile)
        {
            CreateBaseboards(parent);

            if (profile.propVariant == 0 || cv.templateId == 8 || cv.templateId == 11 || cv.templateId == 16)
                CreateArrowMarks(parent);

            if (profile.propVariant == 4 || cv.templateId == 11)
                CreateFalseDoorPanel(parent);
        }

        private void CreateBaseboards(Transform parent)
        {
            CreateSlab(parent, "Baseboard_N",
                new Vector3(chunkSize * 0.5f, 0.32f, 0.13f),
                new Vector3(chunkSize * 0.92f, 0.20f, 0.055f),
                _baseboardMat);

            CreateSlab(parent, "Baseboard_E",
                new Vector3(chunkSize - 0.13f, 0.32f, chunkSize * 0.5f),
                new Vector3(0.055f, 0.20f, chunkSize * 0.92f),
                _baseboardMat);
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

            int grid = Mathf.Min(GridCells, cv.layoutGridSize);
            for (int x = 0; x < grid; x++)
            {
                for (int z = 0; z < grid; z++)
                {
                    int idx = z * cv.layoutGridSize + x;
                    if (idx >= cv.layoutCells.Length || (cv.layoutCells[idx] & CellPillar) == 0)
                        continue;

                    CreateSlab(parent, "BackendPillar",
                        new Vector3((x + 0.5f) * CellSize, wallHeight * 0.5f, (z + 0.5f) * CellSize),
                        new Vector3(1.8f, wallHeight, 1.8f),
                        mat);
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

            int grid = Mathf.Min(GridCells, cv.layoutGridSize);
            for (int x = 0; x < grid; x++)
            {
                for (int z = 0; z < grid; z++)
                {
                    int idx = z * cv.layoutGridSize + x;
                    if (idx >= cv.layoutCells.Length || (cv.layoutCells[idx] & CellPillar) == 0)
                        continue;

                    CreateCollisionBox(parent, "BackendPillarCollider",
                        new Vector3((x + 0.5f) * CellSize, wallHeight * 0.5f, (z + 0.5f) * CellSize),
                        new Vector3(1.8f, wallHeight, 1.8f),
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
}
