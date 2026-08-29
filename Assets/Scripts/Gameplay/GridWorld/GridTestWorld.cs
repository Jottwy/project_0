using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Audio;
using BackroomsSurvival.Gameplay.World;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    // Alias inside the namespace body (not the compilation unit) so it shadows the
    // sibling namespace BackroomsSurvival.Gameplay.Player, which otherwise wins the
    // simple name "Player" from the enclosing scope. Binds it to the STP player type.
    using Player = PolymindGames.Player;

    /// <summary>
    /// Test harness for the procedural world (replaces .bytes loading from Fase 3).
    /// Instantiates a ChunkStreamer driven by the local STP <see cref="Player"/>.
    /// It does not auto-spawn one; instead it waits for the player to appear via
    /// <see cref="Player.PlayerCreated"/> (the player spawns late in multiplayer,
    /// gated by GameBootGate IPC-ready), then defers world init until it exists.
    /// Replaced by the real IPC path in Fase 4.
    /// </summary>
    public sealed class GridTestWorld : MonoBehaviour
    {
        [Header("World")]
        public long seed = 42;
        public int layerCount = 4;
        [Tooltip("-1 = all layers; 0..3 = only that layer")]
        public int onlyLayer = -1;

        [Header("WorldGen3 (ADR-106)")]
        [Tooltip("Materiales del mundo de WG3. Sin ellos el mundo se monta SIN pintar y se ve " +
                 "blanco: son Assets/Materials/WorldGen3/Wg3_Floor, _Structure, _Ceiling y _Trim. " +
                 "Sólo se usan cuando el backend arranca con BACKROOMS_WG3=1.")]
        public BackroomsSurvival.WorldGen3.Wg3Materials wg3Materials =
            new BackroomsSurvival.WorldGen3.Wg3Materials();

        [Header("Streaming")]
        [Tooltip("Chunks visible in each direction (1 = 3×3 ring)")]
        public int viewRadius = 1;

        // DEPRECATED (Fase 4.2): LayerConfig drove the removed client-side WorldGenerator
        // and no longer affects anything. NO code reads these four fields any more (the
        // ChunkStreamer.layerConfigs they used to feed is gone); they are kept only so the
        // values already serialized in BackroomsWithSTP.unity and in the two GridTestWorld
        // prefabs are not lost. Use layerVisualConfigs (Fase 5A) instead.
        [Header("Layer Configs (DEPRECATED — no effect; see Layer Visuals)")]
        public LayerConfig layerConfig0;
        public LayerConfig layerConfig1;
        public LayerConfig layerConfig2;
        public LayerConfig layerConfig3;

        [Header("Fase 5A — Layer Visuals (assign Layer0..3; empty entries load from Resources/LayerVisuals)")]
        public LayerVisualConfig[] layerVisualConfigs = new LayerVisualConfig[4];

        [Header("Rooms (test tool — non-authoritative, replaced by backend in Fase 4)")]
        [Tooltip("Enable the deterministic RoomSpawner prototype. Off by default.")]
        public bool enableTestRooms = false;
        [Tooltip("Rooms spawned on a coarse grid: prefab + per-room spawn height. Empty = no-op.")]
        public RoomSpawner.RoomEntry[] rooms;
        [Tooltip("Room-grid cell size in metres (independent of the 50 m chunk grid).")]
        public float roomGridSize = 100f;
        [Range(0f, 1f)]
        [Tooltip("Per-cell room probability (deterministic per cell).")]
        public float roomSpawnChance = 0.3f;

        private ChunkStreamer _streamer;
        private RoomSpawner _roomSpawner;

        // Backrooms atmosphere: global render settings (set in its Awake) plus
        // per-chunk fluorescent lights placed as chunks stream in.
        private BackroomsLighting _lighting;
        // Chunks already processed (carved). Fase 5A: lighting moved to ChunkStreamer.
        private readonly HashSet<Transform> _processedChunks = new HashSet<Transform>();

        // Hand-placed destruction zones (trigger volumes tagged "DestructionZone")
        // carve a clean hole in each chunk as it streams in, so hand-built
        // mini-structures can sit without overlapping the procedural geometry.
        // Snapshotted once on first use — a test-harness assumption: zones are
        // placed in the scene before entering Play.
        private List<Bounds> _destructionZones;

        // Local player transform, captured once the STP player spawns. Drives the
        // streamer, rooms and lighting. Null until OnPlayerCreated fires.
        private Transform _player;

        private void Start()
        {
            // The local STP player spawns LATE in multiplayer (gated by GameBootGate
            // IPC-ready), so a one-shot FindWithTag in Start would miss it and leave
            // the world un-streamed. Subscribe to the static PlayerCreated event and
            // defer world init until the player exists. If it already spawned (e.g. a
            // standalone scene with an immediate boot gate), handle that synchronously
            // here so we never miss the event.
            Player.PlayerCreated += OnPlayerCreated;

            if (GameMode.HasInstance && GameMode.Instance.LocalPlayer != null)
                OnPlayerCreated(GameMode.Instance.LocalPlayer);
        }

        // First player created on this client is the local one; ignore any later
        // invocations (defensive — also covers re-subscription on respawn).
        private void OnPlayerCreated(Player player)
        {
            if (_player != null) return;

            _player = player.transform;
            Player.PlayerCreated -= OnPlayerCreated;
            InitializeWorld(_player);
        }

        private void OnDestroy()
        {
            Player.PlayerCreated -= OnPlayerCreated;
        }

        private void InitializeWorld(Transform player)
        {
            // Fase 5A: a dim golden ambient fills the space so unlit corners read as
            // Backrooms gloom rather than pure black (the per-layer spots only pool the
            // floor).
            //
            // ADR-066: this is now only the BOOT value, for the frames before the streamer
            // resolves the player's layer and zone. From the first Update, the owner is
            // ProceduralWorldGenerator.ApplyAmbienceForZone, which re-applies it from the
            // layer config (LayerVisualConfig.ambientLight defaults to this same colour) or
            // from the zone's override. Changing the constant here alone no longer changes
            // what the player sees.
            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.28f, 0.24f, 0.16f);

            // Fase 5D: Built-in PPv2 post-process. BackroomsPostProcess first (its Awake
            // builds the global volume + profile before anything reads the singleton), then
            // the camera-side PostProcessLayer enabler, then the F4 visual-effects overlay.
            gameObject.AddComponent<BackroomsPostProcess>();
            gameObject.AddComponent<PlayerCameraPostProcessEnabler>();
            gameObject.AddComponent<BackroomsGraphicsSettings>();

            // Fase 5A: BackroomsLighting is handed to the streamer, which lights each
            // chunk with that chunk's layer visual config (the streamer knows the layer).
            _lighting = gameObject.AddComponent<BackroomsLighting>();

            // Optional deterministic room-placement prototype (test tool, off by
            // default). Created BEFORE the ChunkStreamer and force-spawned now so its
            // rooms — and their DestructionZone trigger volumes — exist when we
            // snapshot the zones below; the carver then carves chunks around them.
            // A runtime-added component can't receive inspector prefab refs directly,
            // so the prefabs live on GridTestWorld and are forwarded. Shares the world
            // seed (and view radius) so placements stay reproducible.
            if (enableTestRooms)
            {
                _roomSpawner = gameObject.AddComponent<RoomSpawner>();
                _roomSpawner.seed            = seed;
                _roomSpawner.playerTransform = player;
                _roomSpawner.rooms           = rooms;
                _roomSpawner.roomGridSize    = roomGridSize;
                _roomSpawner.roomSpawnChance = roomSpawnChance;
                _roomSpawner.viewRadius      = viewRadius;
                _roomSpawner.ForceInitialSpawn();
            }

            // ChunkStreamer after the rooms. Its own Start() builds the first chunks
            // next frame; GridTestWorld.Update then lights and carves them.
            var streamerGo = new GameObject("ChunkStreamer");
            streamerGo.transform.SetParent(transform, false);

            _streamer = streamerGo.AddComponent<ChunkStreamer>();
            _streamer.layerCount = onlyLayer >= 0 ? 1 : layerCount;
            _streamer.viewRadius = viewRadius;
            _streamer.playerTransform = player;
            // Fase 5A: per-layer visuals + lighting are driven by the streamer.
            _streamer.layerVisuals = ResolveLayerVisuals();
            _streamer.lighting = _lighting;

            // ADR-106 — y el streamer de WG3 AL LADO, no en vez de. Los dos se auto-apagan con la
            // misma bandera del saludo (`IPCClient.Wg3Enabled`), así que sólo uno trabaja: el de WG2
            // sale de su Update cuando WG3 manda, y el de WG3 no monta nada cuando no.
            //
            // **Los dos y no uno elegido aquí, y el motivo es de TIEMPO**: en este momento el
            // handshake puede no haber llegado todavía, así que preguntar la bandera ahora daría
            // «WG3 apagado» en una sesión que sí lo tiene. Dejando que cada uno se pregunte por
            // frame, el cambio ocurre solo en cuanto el backend contesta.
            var wg3Go = new GameObject("Wg3ChunkStreamer");
            wg3Go.transform.SetParent(transform, false);
            var wg3 = wg3Go.AddComponent<BackroomsSurvival.WorldGen3.Wg3ChunkStreamer>();
            wg3.viewer = player;
            wg3.radius = viewRadius;
            wg3.materials = wg3Materials;
            if (wg3Materials == null || wg3Materials.floor == null)
            {
                Debug.LogWarning(
                    "[WG3] GridTestWorld no tiene materiales de WorldGen3 asignados: si el backend " +
                    "arranca con BACKROOMS_WG3=1, el mundo se montará SIN pintar. Asigna " +
                    "Assets/Materials/WorldGen3/Wg3_Floor, _Structure, _Ceiling y _Trim en el " +
                    "prefab GridTestWorld.");
            }

            // Snapshot destruction zones AFTER rooms spawn, so room-borne zones are
            // included alongside any hand-placed scene zones. CollectZones scans the
            // scene once; chunks (built next frame) are carved against this snapshot.
            _destructionZones = DestructionZoneCarver.CollectZones();
        }

        // Light chunks as they appear. The ChunkStreamer parents every chunk root
        // under its own transform; a chunk that streams out is Destroyed (with its
        // child lights), so we just prune null entries and skip already-lit roots.
        private void Update()
        {
            if (_streamer == null) return;

            _processedChunks.RemoveWhere(t => t == null);

            // RoomSpawner streams new rooms in as the player moves; their carve zones
            // aren't in the Start snapshot. When it reports new rooms (only on a
            // room-grid crossing, not per frame), re-snapshot the zones and re-carve
            // every active chunk — older chunks the new rooms overlap need cutting too.
            if (_roomSpawner != null && _roomSpawner.SpawnedNewRoomsThisFrame)
            {
                _destructionZones = DestructionZoneCarver.CollectZones();
                var chunks = _streamer.transform;
                for (int i = 0; i < chunks.childCount; i++)
                    DestructionZoneCarver.Carve(chunks.GetChild(i).gameObject, _destructionZones);
            }

            // Fallback if zones were never snapshotted (e.g. rooms disabled).
            // CollectZones scans the scene, so we never want it per chunk.
            _destructionZones ??= DestructionZoneCarver.CollectZones();

            var container = _streamer.transform;
            var childCount = container.childCount;
            for (int i = childCount - 1; i >= 0; i--)
            {
                var chunk = container.GetChild(i);
                if (chunk == null) continue;
                if (!_processedChunks.Add(chunk)) continue; // already processed
                // Fase 5A: lighting now happens in ChunkStreamer (it knows the layer).
                // Here we only carve hand-placed destruction zones (test tool, off by default).
                DestructionZoneCarver.Carve(chunk.gameObject, _destructionZones);
            }
        }

        // Fase 5A: resolve the 4 layer visuals — inspector entries win; empty ones load
        // the assets created by "Backrooms ▸ Create Layer Visuals" from Resources/LayerVisuals.
        // A still-null entry → that layer renders unstyled (FloorSlab+Wall, no lighting).
        private LayerVisualConfig[] ResolveLayerVisuals()
        {
            string[] names = { "Layer0_Vestibulo", "Layer1_Industrial", "Layer2_Concreto", "Layer3_Vacio" };
            var result = new LayerVisualConfig[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                LayerVisualConfig c = (layerVisualConfigs != null && i < layerVisualConfigs.Length)
                    ? layerVisualConfigs[i] : null;
                if (c == null) c = Resources.Load<LayerVisualConfig>("LayerVisuals/" + names[i]);
                result[i] = c;
            }
            return result;
        }
    }
}
