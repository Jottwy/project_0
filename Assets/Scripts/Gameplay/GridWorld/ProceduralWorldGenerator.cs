using System;
using System.Collections.Generic;
using BackroomsSurvival.Gameplay.World;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    // ─────────────────────────────────────────────────────────────────
    // LayerConfig — ScriptableObject, one per layer (0..3).
    // ─────────────────────────────────────────────────────────────────

    [CreateAssetMenu(menuName = "Backrooms/LayerConfig", fileName = "LayerConfig")]
    public sealed class LayerConfig : ScriptableObject
    {
        [Range(0f, 1f)] public float wallDensity      = 0.50f;
        [Range(0f, 1f)] public float openZoneChance  = 0.20f;
        [Range(2, 6)]   public int   openZoneSize    = 3;
        // Chance per chunk of a vertical shaft (a floorless tile that drops
        // through every layer). Must be uniform across layers for the shaft to
        // punch cleanly.
        [Range(0f, 1f)] public float shaftChance     = 0.03f;
        [Range(0f, 1f)] public float pillarChance    = 0.20f;
        // Aperture ratio range: how open the gaps in dividing walls are.
        [Range(0f, 1f)] public float minApertureRatio = 0.30f;
        [Range(0f, 1f)] public float maxApertureRatio = 0.70f;
    }

    // ─────────────────────────────────────────────────────────────────
    // StructureDefinition — JSON schema for Resources/Structures/
    // default_structures.json. Kept as the authoring contract (validated by
    // BackroomsSurvival.EditorTools.StructureValidator). The edge-based
    // generator below does not place structures; the type documents the format.
    // ─────────────────────────────────────────────────────────────────

    [Serializable]
    public sealed class StructureDefinition
    {
        public string     id;
        public float      probability;
        public int        layersTall = 1;
        public string[][] pattern;   // [row][col], row 0 = south
    }

    // ─────────────────────────────────────────────────────────────────
    // ChunkStreamer — manages a 3×3 ring of loaded chunks
    // ─────────────────────────────────────────────────────────────────

    public sealed class ChunkStreamer : MonoBehaviour
    {
        public int       layerCount = 4;
        public int       viewRadius = 1;
        public Transform playerTransform;

        [Header("Fase 5A — per-layer visuals + lighting (set by GridTestWorld)")]
        public LayerVisualConfig[] layerVisuals = new LayerVisualConfig[4];
        public BackroomsLighting lighting;

        [Header("Streaming budget (per frame)")]
        [Tooltip("Chunks built per frame when streaming in. The anti-fall guard can exceed this for the player's own chunk.")]
        public int maxChunkBuildsPerFrame = 1;
        [Tooltip("Chunks destroyed per frame when streaming out.")]
        public int maxChunkDestroysPerFrame = 2;
        [Tooltip("Per-frame log of active / queued / built / destroyed chunk counts.")]
        public bool debugLogging = false;

        private GridPrefabSet _prefabs;
        private readonly Dictionary<(int, int, int), GameObject> _loaded
            = new Dictionary<(int, int, int), GameObject>();

        // Fase 4.1: chunks now stream from the backend (grid_gen) via IPC instead of
        // the local WorldGenerator. _ipc is the bridge; _pending tracks requested-but-
        // not-yet-arrived chunks (the request→reply is async, unlike the old synchronous
        // BuildChunk); _wallsCache holds received bitmasks so a backtracked chunk
        // rebuilds instantly without a round-trip (mirrors the old _cache lifetime,
        // evicted on unload).
        private IPCClient _ipc;
        private readonly HashSet<(int, int, int)> _pending = new HashSet<(int, int, int)>();
        private readonly Dictionary<(int, int, int), byte[,]> _wallsCache
            = new Dictionary<(int, int, int), byte[,]>();

        // ADR-034: rects de sala (RoomType) del mismo mensaje ChunkData. Cacheado en
        // PARALELO a _wallsCache y con SU MISMA vida (escrito en OnChunkDataReceived,
        // desalojado junto a él al descargar el chunk) — así una reconstrucción desde
        // caché nunca queda con el bitmask pero sin su tipo de sala.
        //
        // Clave (cx, cz, LAYER), no (cx, cz): el RoomType se sortea por capa dentro de
        // cada chunk. ZoneRegistry sí ignora la capa (zone_kind se asigna por
        // estructura, es igual en toda la columna) y ese atajo le costó un bug real
        // — ver el fix cf1ab94 de la Pieza 3. Aquí NO aplica: dos capas del mismo
        // chunk tienen zonas distintas, con rects distintos.
        private readonly Dictionary<(int, int, int), RoomZoneMsg[]> _roomZonesCache
            = new Dictionary<(int, int, int), RoomZoneMsg[]>();

        // Fase 4.1 fix: Time.unscaledTime when each key entered _pending. A request
        // whose reply never arrives (lost after the socket accepted it, or a stale
        // connection) is freed after PendingTimeout and re-queued — otherwise
        // ExceptWith(_pending) would keep that chunk out of _buildQueue forever (the
        // permanent-empty-chunk bug).
        private readonly Dictionary<(int, int, int), float> _pendingSince
            = new Dictionary<(int, int, int), float>();
        private const float PendingTimeout = 2f; // seconds; well above localhost RTT, conservative

        // Zone-kind gate (Pieza 2 fix): ChunkData (grid_gen geometry) and WorldState
        // (ChunkView, the only carrier of zone_kind) are independent IPC messages with no
        // ordering guarantee, so a chunk often arrived before ZoneRegistry knew its zone and
        // got baked with a white tint forever. A chunk whose zone is not known yet is held
        // back and retried from _wallsCache next frame — the same "stays queued" semantics a
        // dropped request already uses. Time.unscaledTime when each key first had to wait;
        // after ZoneWaitTimeout it builds anyway (white) so a missing zone can never leave a
        // permanent hole in the world.
        private readonly Dictionary<(int, int, int), float> _zoneWaitSince
            = new Dictionary<(int, int, int), float>();
        private const float ZoneWaitTimeout = 0.75f; // seconds; ~7 world-state ticks at 10hz

        // Fix priorizado worldgen (Alpha 1, chunks blancos): ZoneWaitTimeout above only ever
        // moves WHEN a chunk gives up waiting — it still bakes white forever once it does, and
        // the anti-fall guard chunk bypasses the gate entirely (never waits at all). Neither
        // path has a way back once the zone actually lands. This set tracks every currently
        // LOADED chunk that was built with zoneKnown == false (mirrors the exact check
        // GridChunkBuilder.BuildFromWalls makes internally, GridChunkBuilder.cs:307) so
        // ZoneRegistry.ZoneArrived can trigger a late, full rebuild instead of a permanent hole.
        // Styled-but-unknown chunks only; an unstyled layer (no LayerVisualConfig) never
        // depends on zone_kind, so it is never added here (see BuildChunkFromBitmask).
        private readonly HashSet<(int, int, int)> _builtWhite = new HashSet<(int, int, int)>();

        // Fase 5A: shared per-layer materials (built once, reused across all tiles of
        // the layer; per-tile tint via MaterialPropertyBlock) + the fog layer currently
        // applied to RenderSettings. _matCache is owned here and freed in OnDestroy.
        private readonly Dictionary<int, LayerVisualMaterials> _matCache = new Dictionary<int, LayerVisualMaterials>();
        private int _activeFogLayer = int.MinValue;
        // ADR-066 — zona cuyo ambiente está aplicado ahora mismo. −1 es un valor LEGÍTIMO
        // ("zona aún desconocida", cae al fallback de capa), así que el centinela de "nada
        // aplicado todavía" tiene que ser otro: int.MinValue, igual que _activeFogLayer.
        private int _activeZoneKind = int.MinValue;

        private int _lastCX = int.MinValue;
        private int _lastCZ = int.MinValue;

        // Pending streaming work, drained under per-frame budget in ProcessBudget().
        // HashSets (not FIFO queues) so backtrack rescue can drop an arbitrary key and
        // builds drain nearest-first against the CURRENT player chunk, re-evaluated each
        // frame. _desired / _drainScratch are reused scratch to avoid per-frame allocs.
        private readonly HashSet<(int, int, int)> _buildQueue = new HashSet<(int, int, int)>();
        private readonly HashSet<(int, int, int)> _unloadQueue = new HashSet<(int, int, int)>();
        private readonly HashSet<(int, int, int)> _desired = new HashSet<(int, int, int)>();
        private readonly List<(int, int, int)> _drainScratch = new List<(int, int, int)>();

        // Chunk side in metres (50 m). Same quantity as GridChunkBuilder's
        // `float span = Tiles * Ts;` (GridChunkBuilder.cs:949): 10 tiles × 5 m. Both
        // derive from GridConstants, which mirrors backend/src/world/grid_gen/cell.rs,
        // so neither can drift without the Rust contract drifting first.
        private const float Side = GridConstants.ChunkCells * GridConstants.CellSize;

        /// <summary>Fix priorizado worldgen (Alpha 1): la única instancia viva, para que
        /// ChunkLootManager (u otro consumidor fuera de esta clase) consulte el caché de
        /// paredes sin una referencia de escena. Misma asunción de "solo hay una" que ya
        /// hace el setup de GridTestWorld; mismo patrón singleton que ChunkLootManager
        /// usa para sí mismo (_instance).</summary>
        public static ChunkStreamer Instance { get; private set; }

        private void Start()
        {
            Instance = this;
            // Fix priorizado worldgen (Alpha 1, chunks blancos): subscribe unconditionally,
            // before the early-returns below — OnDestroy's unsubscribe is safe even if this
            // never actually fires (no chunk ever gets built without prefabs/playerTransform).
            ZoneRegistry.ZoneArrived += OnZoneArrived;
            _prefabs = GridPrefabSet.LoadFromResources();
            if (_prefabs.floor == null) return;
            if (playerTransform == null) return;

            // Fase 4.1: subscribe to backend chunk replies before requesting anything.
            // The reply fires on the main thread (IPCClient drains its queue in Update).
            if (IPCClient.TryGetInstance(out _ipc))
                _ipc.AddChunkDataListener(OnChunkDataReceived);
            else
                Debug.LogWarning("[ChunkStreamer] No IPCClient present — no chunks will " +
                                 "stream (Fase 4.1 needs the backend connection running).");

            // Fill the queues for the initial ring and request the player's chunk first.
            // NOTE (Fase 4.1): builds are now ASYNC (request→reply), so unlike the old
            // synchronous path there is no floor guaranteed at frame 0 — the player's
            // chunk arrives a few frames later (sub-ms round-trip on localhost). Brief
            // fall-through at spawn / on very fast crossings is a known caveat, hardened
            // in Fase 4.2.
            UpdateChunks(force: true);
            int cx = Mathf.FloorToInt(playerTransform.position.x / Side);
            int cz = Mathf.FloorToInt(playerTransform.position.z / Side);
            ProcessBudget(cx, cz);
        }

        private void OnDestroy()
        {
            ZoneRegistry.ZoneArrived -= OnZoneArrived;
            if (Instance == this)
                Instance = null;
            if (_ipc != null)
                _ipc.RemoveChunkDataListener(OnChunkDataReceived);
            // Fase 5A: free the shared per-layer materials we instanced.
            foreach (var m in _matCache.Values) m.Destroy();
            _matCache.Clear();
        }

        private void Update()
        {
            if (playerTransform == null) return;
            int cx = Mathf.FloorToInt(playerTransform.position.x / Side);
            int cz = Mathf.FloorToInt(playerTransform.position.z / Side);

            // On a chunk crossing, recompute the desired-set and re-queue work. The set is
            // UNCHANGED from before (same viewRadius / layers / shafts) — only WHEN chunks
            // are built/destroyed changes here, never WHICH.
            if (cx != _lastCX || cz != _lastCZ)
                UpdateChunks();

            // ADR-066: ambient + fog follow the player's layer AND the zone of the chunk they
            // stand in (applied only on change). The zone is POLLED here rather than driven by
            // ZoneRegistry.ZoneArrived: TryGetZone is a dictionary hit, and polling needs no
            // event lifecycle to get the late-chunk case right — the frame the zone lands, the
            // lookup starts answering and the change-guard below does the rest.
            int ambienceZone = StableZone(
                ZoneRegistry.TryGetZone(cx, cz, out byte zk) ? zk : -1);
            ApplyAmbienceForZone(Mathf.Clamp(
                Mathf.FloorToInt(playerTransform.position.y / GridConstants.LayerHeight),
                0, layerCount - 1), ambienceZone);

            // Drain the queues under the per-frame budget every frame.
            ProcessBudget(cx, cz);
        }

        // Recompute the desired ring and reconcile it against loaded chunks + the pending
        // queues. Does NOT build or destroy — that happens in ProcessBudget under budget.
        private void UpdateChunks(bool force = false)
        {
            if (playerTransform == null) return;
            int cx = Mathf.FloorToInt(playerTransform.position.x / Side);
            int cz = Mathf.FloorToInt(playerTransform.position.z / Side);

            if (!force && cx == _lastCX && cz == _lastCZ) return;
            _lastCX = cx; _lastCZ = cz;

            _desired.Clear();
            BuildDesiredSet(cx, cz, viewRadius, layerCount, _desired);
            ReconcileQueues(_desired, _loaded.Keys, _buildQueue, _unloadQueue);
            // Fase 4.1: ReconcileQueues re-adds every desired-not-loaded key, including
            // chunks already requested and awaiting a reply. Drop those so we never
            // re-request an in-flight chunk (the pure scheduler stays untouched).
            _buildQueue.ExceptWith(_pending);
        }

        // Per-frame work under budget: anti-fall guard first, then nearest-first builds
        // and farthest-first destroys, each capped.
        private void ProcessBudget(int cx, int cz)
        {
            // Fase 4.1 fix: free any pending request whose reply never arrived so it can
            // be re-requested (otherwise ExceptWith(_pending) blocks it forever).
            ExpirePendingRequests();

            // Anti-fall: prioritise the chunk under the player, budget or not. Fase 4.1:
            // builds are async (request→reply), so this no longer GUARANTEES floor this
            // frame — it just requests the player's chunk first (or builds instantly if
            // its bitmask is already cached). The reply lands a few frames later.
            int forced = 0;
            int playerLayer = Mathf.Clamp(
                Mathf.FloorToInt(playerTransform.position.y / GridConstants.LayerHeight),
                0, layerCount - 1);
            var guardKey = (cx, cz, playerLayer);
            if (_desired.Contains(guardKey) && !_loaded.ContainsKey(guardKey))
            {
                // Drop from the queue only if fulfilled; a dropped send stays queued so
                // the drain loop / next frame retries it (anti-fall also re-runs each frame).
                // EXEMPT from the zone gate on purpose: the chunk under the player must
                // never be delayed for a cosmetic tint. It builds immediately (white if the
                // zone has not arrived) exactly as it did before the gate existed.
                if (RequestOrBuild(guardKey, bypassZoneGate: true)) _buildQueue.Remove(guardKey);
                forced++;
            }

            int builds = 0;
            if (_buildQueue.Count > 0)
            {
                OrderByDistance(_buildQueue, cx, cz, nearestFirst: true, _drainScratch);
                for (int i = 0; i < _drainScratch.Count && builds < maxChunkBuildsPerFrame; i++)
                {
                    var key = _drainScratch[i];
                    if (!_buildQueue.Contains(key)) continue;
                    if (!_desired.Contains(key) || _loaded.ContainsKey(key)) { _buildQueue.Remove(key); continue; } // stale/built
                    // Dequeue only on success; a dropped send (no connection) stays queued → retried next frame.
                    if (RequestOrBuild(key)) { _buildQueue.Remove(key); builds++; }
                }
            }

            int destroys = 0;
            if (_unloadQueue.Count > 0)
            {
                OrderByDistance(_unloadQueue, cx, cz, nearestFirst: false, _drainScratch);
                for (int i = 0; i < _drainScratch.Count && destroys < maxChunkDestroysPerFrame; i++)
                {
                    var key = _drainScratch[i];
                    if (!_unloadQueue.Remove(key)) continue;
                    if (_desired.Contains(key)) continue;                  // rescued — back in range
                    if (!_loaded.TryGetValue(key, out var go)) continue;
                    Destroy(go);
                    _loaded.Remove(key);
                    _wallsCache.Remove(key);
                    _roomZonesCache.Remove(key); // ADR-034: misma vida que el bitmask
                    _zoneWaitSince.Remove(key);
                    _builtWhite.Remove(key); // fix chunks blancos: sin sentido reconstruir lo descargado
                    destroys++;
                }
            }

            if (debugLogging && (forced + builds + destroys > 0 || _buildQueue.Count + _unloadQueue.Count > 0))
                Debug.Log($"[ChunkStreamer] active={_loaded.Count} buildQ={_buildQueue.Count} " +
                          $"unloadQ={_unloadQueue.Count} built={builds}(+{forced} forced) destroyed={destroys}");
        }

        // Fase 4.1: fulfil one chunk slot from the backend instead of generating locally.
        // Returns true if the slot was handled (built from cache, already in flight, or a
        // fresh request actually went out → now pending) so the caller dequeues it.
        // Returns FALSE if the request was DROPPED (socket not connected): the key is NOT
        // marked pending and stays in _buildQueue, so it is retried next frame instead of
        // being stranded empty forever. The old synchronous local-generation path
        // (WorldGenerator) was removed in Fase 4.2.
        // bypassZoneGate: build as soon as the bitmask is available, without waiting for
        // zone_kind. Reserved for the anti-fall guard chunk (see ProcessBudget).
        private bool RequestOrBuild((int, int, int) key, bool bypassZoneGate = false)
        {
            if (_wallsCache.TryGetValue(key, out var walls))
            {
                // Zone unknown and still inside the grace window → leave queued (same
                // contract as a dropped send) so the retry picks it up from _wallsCache.
                if (!bypassZoneGate && !ZoneReadyOrExpired(key))
                    return false;
                BuildChunkFromBitmask(key, walls);
                return true;
            }
            if (_pending.Contains(key))
                return true; // already in flight — dedup, don't re-send
            if (_ipc != null && _ipc.SendRequestChunk(key.Item1, key.Item2, (byte)key.Item3))
            {
                _pending.Add(key);
                _pendingSince[key] = Time.unscaledTime;
                return true;
            }
            return false; // send dropped → leave queued for retry
        }

        /// <summary>
        /// True when <paramref name="key"/> may be built: either ZoneRegistry knows the
        /// chunk's zone_kind, or it has been waiting longer than ZoneWaitTimeout (build it
        /// unstyled rather than ever leave a hole). False means "not yet — retry next frame".
        /// Mirrors the _pendingSince/ExpirePendingRequests timeout pattern.
        /// </summary>
        private bool ZoneReadyOrExpired((int, int, int) key)
        {
            if (ZoneRegistry.TryGetZone(key.Item1, key.Item2, out _))
            {
                _zoneWaitSince.Remove(key);
                return true;
            }

            float now = Time.unscaledTime;
            if (!_zoneWaitSince.TryGetValue(key, out float since))
            {
                _zoneWaitSince[key] = now; // first deferral — start the grace window
                return false;
            }
            if (now - since < ZoneWaitTimeout)
                return false;

            _zoneWaitSince.Remove(key); // gave up waiting; build white so no hole persists
            return true;
        }

        // Free pending requests older than PendingTimeout (reply lost / never delivered)
        // and re-queue them if still wanted, so RequestOrBuild can re-issue with a fresh
        // timestamp. Reuses _drainScratch (fully consumed here before the budget drains
        // re-clear it via OrderByDistance) to avoid a per-frame allocation.
        private void ExpirePendingRequests()
        {
            if (_pending.Count == 0) return;
            float now = Time.unscaledTime;
            _drainScratch.Clear();
            foreach (var key in _pending)
                if (now - _pendingSince[key] > PendingTimeout)
                    _drainScratch.Add(key);
            for (int i = 0; i < _drainScratch.Count; i++)
            {
                var key = _drainScratch[i];
                _pending.Remove(key);
                _pendingSince.Remove(key);
                if (_desired.Contains(key) && !_loaded.ContainsKey(key))
                    _buildQueue.Add(key);
            }
        }

        // Backend reply (main thread). Cache the bitmask, then build the chunk if it is
        // still wanted and not already built. Stale replies (chunk left the view before it
        // arrived) are cached but not built — a later return visit builds instantly.
        private void OnChunkDataReceived(GridChunkDataMsg data)
        {
            var key = (data.cx, data.cz, (int)data.layer);
            _pending.Remove(key);
            _pendingSince.Remove(key);
            _wallsCache[key] = data.walls;
            _roomZonesCache[key] = data.roomZones; // ADR-034; nunca null, vacío si el wire no lo trae
            if (_desired.Contains(key) && !_loaded.ContainsKey(key))
            {
                // Zone gate: geometry arrived, but zone_kind rides a different IPC message.
                // If it is not known yet, re-queue instead of baking a white tint forever —
                // ProcessBudget retries from _wallsCache (no re-request) until the zone lands
                // or ZoneWaitTimeout expires. The chunk under the player is never held here:
                // the anti-fall guard runs every frame and bypasses the gate.
                if (ZoneReadyOrExpired(key))
                    BuildChunkFromBitmask(key, data.walls);
                else
                    _buildQueue.Add(key);
            }
        }

        /// <summary>
        /// ADR-034 — rects de sala del chunk (cx, cz, layer). Devuelve un array VACÍO
        /// (nunca null) si el chunk no está cargado, no tiene zonas, o el backend es
        /// anterior al ADR: "sin zona conocida" y "zona Open" son estados distintos,
        /// pero ambos se manejan igual desde fuera — ninguna zona cubre el tile.
        /// </summary>
        public RoomZoneMsg[] GetRoomZones(int cx, int cz, int layer) =>
            _roomZonesCache.TryGetValue((cx, cz, layer), out var zones)
                ? zones
                : System.Array.Empty<RoomZoneMsg>();

        /// <summary>Fix priorizado worldgen (Alpha 1, loot dentro de muros) — el bitmask
        /// de paredes cacheado para una columna cargada/vista, EXACTAMENTE el mismo dato
        /// del que <see cref="GridChunkBuilder.BuildFromWalls"/> renderiza (sin segunda
        /// fuente de verdad). false si este (cx,cz,layer) nunca llegó (no pedido aún, o
        /// desalojado al descargar) — el llamador debe degradar con gracia, no asumir que
        /// las paredes siempre se conocen.</summary>
        public bool TryGetWalls(int cx, int cz, int layer, out byte[,] walls) =>
            _wallsCache.TryGetValue((cx, cz, layer), out walls);

        // Instantiate one chunk from a tile-wall bitmask, parenting it under the streamer
        // ONLY once fully built so GridTestWorld's carve sees a complete chunk. Fase 5A:
        // applies the layer's visuals (shared materials + per-tile tint) and lights it.
        private void BuildChunkFromBitmask((int, int, int) key, byte[,] walls)
        {
            var (ccx, ccz, layer) = key;
            var origin = new Vector3(ccx * Side, layer * GridConstants.LayerHeight, ccz * Side);
            var cfg = GetLayerVisual(layer);
            var mats = cfg != null ? GetLayerMaterials(layer) : null;

            // Fix priorizado worldgen (Alpha 1, chunks blancos): mismo check que
            // BuildFromWalls hace internamente para decidir tinte/modelo de zona
            // (GridChunkBuilder.cs:307) — leerlo aquí también nos dice si ESTE build nace
            // "blanco" (zona aún no conocida), para poder reconstruirlo más tarde cuando
            // ZoneRegistry.ZoneArrived avise. Lectura pura sin efectos secundarios — llamar
            // TryGetZone dos veces (aquí y dentro de BuildFromWalls) es inofensivo.
            bool styled = cfg != null && mats != null;
            // ADR-059: el byte de zona se conserva — además de decidir "blanco o no",
            // alimenta los overrides de luz por zona más abajo.
            byte zoneKindByte = 0;
            bool zoneKnownAtBuild = styled && ZoneRegistry.TryGetZone(ccx, ccz, out zoneKindByte);

            // ADR-035: los rects de sala salen del cache, no del mensaje — una
            // reconstrucción desde _wallsCache (chunk revisitado, o reintento tras el gate
            // de zona) tiene que ver las mismas zonas que la construcción original.
            var go = GridChunkBuilder.BuildFromWalls(walls, _prefabs, origin,
                $"Chunk_L{layer}_{ccx}_{ccz}", layer, layerCount, cfg, mats, ccx, ccz,
                GetRoomZones(ccx, ccz, layer));
            go.transform.SetParent(transform, true);
            _loaded[key] = go;

            if (styled && !zoneKnownAtBuild)
                _builtWhite.Add(key);
            else
                _builtWhite.Remove(key); // a rebuild resolved it, or it was never white to begin with

            // Fase 5A: light here (layer + coords known); GridTestWorld no longer lights.
            if (lighting != null && cfg != null && mats != null)
            {
                // Tile count and light height are the BUILDER's geometry, not ours:
                // TilesPerChunk is GridChunkBuilder.Tiles (= ChunkCells / 2), and
                // CellHeight * 2f is the same expression as its WallPrefabHeight
                // (GridChunkBuilder.cs:109, 2f * Ch). The height is left as the literal
                // expression on purpose — aliasing it would move a number for no gain.
                int tiles = GridChunkBuilder.TilesPerChunk;
                // ADR-059: −1 cuando la zona no se conocía al construir — un set de luz
                // específico de zona no casa con eso (solo uno comodín), y la
                // reconstrucción vía OnZoneArrived vuelve a pasar por aquí con la zona.
                lighting.PlaceFluorescentLights(go.transform, tiles, tiles,
                    GridVisualConstants.TileSize, GridVisualConstants.CellHeight * 2f,
                    cfg, mats.lamp, ccx, ccz, layer, walls,
                    zoneKnownAtBuild ? zoneKindByte : -1);
            }
        }

        // Fix priorizado worldgen (Alpha 1, chunks blancos): ZoneRegistry told us (cx,cz) just
        // learned its zone — rebuild every currently-loaded layer of that column that was built
        // white. Column, not single layer: the anti-fall guard bypasses the gate for only the
        // player's OWN layer (ProcessBudget above), so the other 3 layers of the same column can
        // independently be sitting white too, and this is the only recovery path any of them get.
        private void OnZoneArrived(int cx, int cz)
        {
            if (_builtWhite.Count == 0) return; // fast-out; the common case once warmed up
            for (int layer = 0; layer < layerCount; layer++)
            {
                var key = (cx, cz, layer);
                if (_builtWhite.Contains(key))
                    RebuildChunk(key);
            }
        }

        // Full rebuild from the SAME cached bitmask/room-zones the original build used — the
        // zone doesn't just re-tint, it selects a different wall MODEL (ADR-035,
        // GridChunkBuilder.cs:309), so nothing short of a full rebuild renders correctly. Builds
        // the new root BEFORE destroying the old one (both in this same call, same frame): the
        // target chunk can be the one under the player, and a Destroy-then-Build order would
        // leave zero floor collider for however many frames the build takes.
        private void RebuildChunk((int, int, int) key)
        {
            if (!_wallsCache.TryGetValue(key, out var walls))
                return; // unloaded since — nothing to rebuild, _builtWhite already pruned on unload
            if (!_loaded.TryGetValue(key, out var oldGo))
                return; // not currently loaded (shouldn't happen if _wallsCache still has it, but
                         // degrade instead of assuming — the next real load will re-evaluate zoneKnownAtBuild anyway)

            BuildChunkFromBitmask(key, walls); // overwrites _loaded[key] with the NEW root first
            Destroy(oldGo);
        }

        // ── Fase 5A — per-layer visuals ────────────────────────────────────────

        /// <summary>The visual config for <paramref name="layer"/> (clamped), or null.</summary>
        private LayerVisualConfig GetLayerVisual(int layer)
        {
            if (layerVisuals == null || layerVisuals.Length == 0) return null;
            return layerVisuals[Mathf.Clamp(layer, 0, layerVisuals.Length - 1)];
        }

        /// <summary>Shared materials for <paramref name="layer"/>, built once and cached.</summary>
        private LayerVisualMaterials GetLayerMaterials(int layer)
        {
            var cfg = GetLayerVisual(layer);
            if (cfg == null) return null;
            if (!_matCache.TryGetValue(layer, out var m))
            {
                m = LayerVisualMaterials.Build(cfg);
                _matCache[layer] = m;
            }
            return m;
        }

        // Fase 5A: uniform, lighter fog. Was per-layer cfg.fogDensity (0.035–0.09 → too
        // dense); a flat 0.015 lets the space read without milky wash. ADR-066 keeps these
        // as the FALLBACK — a zone that authors nothing looks exactly as it did before the
        // ADR, which is what makes layers 1-3 byte-identical while only layer 0 is authored.
        // cfg.fogDensity/cfg.fogColor stay disconnected on purpose: reconnecting them would
        // bring back the milky look Fase 5A removed.
        private const float BaseFogDensity = 0.015f;
        private static readonly Color BaseFogColor = new Color(0.72f, 0.65f, 0.45f);

        /// <summary>
        /// ADR-066 — apply ambient light + fog for the player's layer AND zone (on change only).
        /// Generalises Fase 5A's ApplyFogForLayer: fog is no longer flat across the world, and
        /// RenderSettings.ambientLight — written exactly once at boot until now — is owned here.
        ///
        /// A zone with no authored <see cref="ZoneAmbienceSet"/> (including −1, "zone not known
        /// yet") falls back to the layer's values, so an unauthored layer renders byte-identical
        /// to before. This is GLOBAL render state resolved once per change, never inside the
        /// per-tile loop: it cannot touch the per-chunk System.Random, so lamp/prop determinism
        /// holds by construction.
        /// </summary>
        // ── Histéresis de zona ──────────────────────────────────────────────────

        /// <summary>
        /// Segundos que una zona nueva tiene que sostenerse antes de que la atmósfera la
        /// adopte. Caminar justo por el límite entre dos chunks de zonas distintas hace que
        /// la lectura alterne frame a frame, y sin esto el reverb persigue las dos salas a la
        /// vez: bombeo audible mientras el jugador está prácticamente quieto. Niebla y
        /// ambiente sufrían lo mismo, solo que un parpadeo de color se nota menos que uno de
        /// cola. Medio segundo cuesta menos que cruzar un umbral andando y mata la oscilación.
        /// </summary>
        private const float ZoneSettleSeconds = 0.5f;

        private int   _committedZone = int.MinValue; // la que gobierna ahora mismo
        private int   _pendingZone   = int.MinValue; // candidata esperando a sostenerse
        private float _zonePendingSince;

        /// <summary>
        /// La zona a aplicar, filtrada por histéresis. Una lectura distinta a la vigente NO
        /// manda hasta que se mantiene <see cref="ZoneSettleSeconds"/>; si vuelve a la
        /// anterior antes, no ha pasado nada. El primer valor entra de inmediato: al aparecer
        /// el mundo no hay nada que estabilizar y esperar medio segundo solo retrasaría la
        /// atmósfera inicial.
        /// </summary>
        private int StableZone(int reading)
        {
            if (_committedZone == int.MinValue) { _committedZone = reading; return reading; }
            if (reading == _committedZone) { _pendingZone = int.MinValue; return _committedZone; }

            if (reading != _pendingZone)
            {
                _pendingZone  = reading;
                _zonePendingSince = Time.unscaledTime;
                return _committedZone;
            }
            if (Time.unscaledTime - _zonePendingSince < ZoneSettleSeconds)
                return _committedZone;

            _committedZone = reading;
            _pendingZone   = int.MinValue;
            return _committedZone;
        }

        private void ApplyAmbienceForZone(int layer, int zoneKind)
        {
            if (layer == _activeFogLayer && zoneKind == _activeZoneKind) return;
            var cfg = GetLayerVisual(layer);
            if (cfg == null) return;
            _activeFogLayer = layer;
            _activeZoneKind = zoneKind;

            var ambient = cfg.ambientLight;
            float density = BaseFogDensity;
            var color = BaseFogColor;

            if (cfg.TryGetZoneAmbienceSet(zoneKind, out var za))
            {
                if (za.overrideAmbientLight) ambient = za.ambientLight;
                if (za.overrideFogDensity)   density = za.fogDensity;
                if (za.overrideFogColor)     color   = za.fogColor;
            }

            RenderSettings.ambientMode  = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = ambient;
            RenderSettings.fog          = true;
            RenderSettings.fogMode      = FogMode.ExponentialSquared;
            RenderSettings.fogDensity   = density;
            RenderSettings.fogColor     = color;

            // El reverb es atmósfera igual que la niebla y el ambiente, así que se resuelve
            // aquí y no en un segundo sondeo de zona: mismo dueño, mismo disparo, un solo
            // punto donde el "dónde estoy" se convierte en "cómo suena y cómo se ve".
            //
            // Se manda el TONO, no un salto: el driver interpola en su propio Update porque
            // una cola que cambia de golpe se oye como un corte, mientras que ambiente y
            // niebla sí pueden aplicarse de una vez.
            Audio.ReverbMixerDriver.SetRoom(cfg.ReverbFor(zoneKind), zoneKind);
        }

        // ── Scheduling logic (pure; unit-tested headless in ChunkStreamSchedulerTests) ──

        /// <summary>Fill <paramref name="into"/> with the desired ring of chunk keys
        /// (cx,cz,layer) around the player's chunk — same set as before, just factored out
        /// so it can be tested without Play.</summary>
        public static void BuildDesiredSet(int cx, int cz, int viewRadius, int layerCount,
            HashSet<(int, int, int)> into)
        {
            for (int dz = -viewRadius; dz <= viewRadius; dz++)
                for (int dx = -viewRadius; dx <= viewRadius; dx++)
                    for (int layer = 0; layer < layerCount; layer++)
                        into.Add((cx + dx, cz + dz, layer));
        }

        /// <summary>Reconcile the desired set against currently-loaded chunks and the
        /// pending queues: enqueue new builds/unloads and rescue backtracked keys (a key
        /// that left then re-entered range is never build-then-destroyed, nor vice versa).</summary>
        public static void ReconcileQueues(
            HashSet<(int, int, int)> desired,
            ICollection<(int, int, int)> loaded,
            HashSet<(int, int, int)> buildQueue,
            HashSet<(int, int, int)> unloadQueue)
        {
            buildQueue.RemoveWhere(k => !desired.Contains(k) || loaded.Contains(k));
            unloadQueue.RemoveWhere(k => desired.Contains(k) || !loaded.Contains(k));
            foreach (var k in desired)
                if (!loaded.Contains(k)) buildQueue.Add(k);
            foreach (var k in loaded)
                if (!desired.Contains(k)) unloadQueue.Add(k);
        }

        /// <summary>Order <paramref name="keys"/> into <paramref name="into"/> by squared
        /// horizontal chunk distance to (cx,cz): nearest-first for builds, farthest-first
        /// for unloads. Pure distance only — no structural-room priority (deferred).</summary>
        public static void OrderByDistance(
            IEnumerable<(int, int, int)> keys, int cx, int cz, bool nearestFirst,
            List<(int, int, int)> into)
        {
            into.Clear();
            into.AddRange(keys);
            // El comparador NO puede ser un lambda aquí: captura cx, cz y nearestFirst, así que
            // Roslyn no lo puede cachear y materializa un display class + un delegate en CADA
            // llamada — y ProcessBudget llama dos veces por frame mientras hay streaming.
            // Instancia reutilizada con los tres parámetros como campos: mismo orden total, cero
            // asignaciones. No es reentrante, pero OrderByDistance nunca se anida.
            _comparer.cx = cx;
            _comparer.cz = cz;
            _comparer.nearestFirst = nearestFirst;
            into.Sort(_comparer);
        }

        private static readonly DistanceComparer _comparer = new DistanceComparer();

        /// <summary>Comparador de <see cref="OrderByDistance"/>, extraído a clase para no asignar
        /// un delegate por llamada. El desempate (capa, x, z) es idéntico al del lambda que
        /// sustituye, y hace el orden TOTAL: sin él, dos claves a la misma distancia quedarían en
        /// un orden que depende del algoritmo de Sort.</summary>
        private sealed class DistanceComparer : IComparer<(int, int, int)>
        {
            public int cx;
            public int cz;
            public bool nearestFirst;

            public int Compare((int, int, int) a, (int, int, int) b)
            {
                int cmp = Dist2(a, cx, cz).CompareTo(Dist2(b, cx, cz));
                if (cmp == 0) cmp = a.Item3.CompareTo(b.Item3);
                if (cmp == 0) cmp = a.Item1.CompareTo(b.Item1);
                if (cmp == 0) cmp = a.Item2.CompareTo(b.Item2);
                return nearestFirst ? cmp : -cmp;
            }
        }

        private static long Dist2((int, int, int) k, int cx, int cz)
        {
            long dx = k.Item1 - cx, dz = k.Item2 - cz;
            return dx * dx + dz * dz;
        }

    }
}
