using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Gameplay.GridWorld;
using PolymindGames.InventorySystem;
using PolymindGames.WieldableSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// HOST-ONLY procedural loot tied to REAL chunk streaming (Fase-0 "Opción C", client-side
    /// position-ring variant). Replaces the one-shot scatter of StpItemSpawner / the resource-zone
    /// scatter of StpCarryableSpawner: instead of a fixed set of caches placed once around spawn and
    /// retried, loot is generated PER CHUNK as the chunk enters the streaming ring and removed as it
    /// leaves — so loot appears/despawns with the world the player actually walks through, and the
    /// broadcast list stays bounded to the live ring instead of growing forever.
    ///
    /// AUTHORITY / DETERMINISM (see <see cref="ChunkLootRoll"/>):
    /// - What a chunk contains is a pure function of (worldSeed, cx, cz) → reproducible on reload
    ///   without persisting geometry; only the small "already picked up" set is remembered.
    /// - Pickup memory: a slot removed from the backend's mirror (LatestState) while we had it live
    ///   is marked collected, so a reloaded chunk regenerates everything EXCEPT what was taken.
    /// - On UNLOAD, still-live (un-picked) loot is FORGOTTEN (removed from the sent list, shrinking
    ///   the broadcast) and regenerated identically if the chunk returns — indistinguishable from
    ///   persisting it, minus the collected set.
    ///
    /// SEND MODEL — same merge-from-mirror the old spawners use: set_stp_items/set_stp_carryables
    /// FULL-REPLACE the backend list, and that list is MULTI-AUTHOR (player drops via StpDrop,
    /// scene-authored carryables). So a send is: (backend mirror MINUS the ids WE just unloaded) +
    /// (our new placements). We never rebuild the list from our own bookkeeping alone — that would
    /// drop other authors' entries. Pickups are already gone from the mirror (pickup-aware for free).
    ///
    /// SCOPE (approved Fase-0): items + carryables ONLY. World chests (StpChestSpawner) stay
    /// one-shot, untouched. NO protocol change. NO backend / resolve_move / update_ownership /
    /// TP-diagnostic contact: purely the CLIENT ring (position math + walkable raycast) + the
    /// existing host→backend relay.
    ///
    /// SINGLE-LAYER placement: loot is keyed per (cx,cz) COLUMN and placed on whatever floor the
    /// walkable raycast finds at the player's current vertical band — same single-layer behaviour as
    /// the old scatter. Multi-layer loot was explicitly out of the approved scope.
    ///
    /// Governs only while <see cref="Enabled"/> is true; the old spawners early-out on that flag so
    /// exactly one system authors procedural loot at a time (A/B comparison switch).
    ///
    /// ZONE GATE (Pieza 3, zone_kind → loot): CollectLoads resolves each column's zone via
    /// <see cref="ZoneRegistry.TryGetZone"/> before rolling it. Unlike ChunkStreamer's
    /// ZoneReadyOrExpired (which times out after 0.75s so a chunk's FLOOR never leaves a permanent
    /// hole), loot has no such constraint — an un-rolled column is simply invisible, not broken
    /// geometry. So this gate has NO timeout and NO fallback profile: an unknown zone just leaves
    /// the column unsealed, re-evaluated from _desiredColumns every scan (scanInterval) until
    /// ZoneRegistry has an answer. Deliberately not sharing ZoneReadyOrExpired's timeout policy —
    /// see docs/STATE.md Pieza 3 for the reasoning.
    /// </summary>
    public sealed class ChunkLootManager : MonoBehaviour
    {
        /// <summary>Master switch. true → this manager governs procedural loot and the old scatter
        /// spawners stand down. Flip to false to A/B against the old one-shot scatter (Fase-0 step 5;
        /// the old code is left intact, only gated).</summary>
        public const bool Enabled = true;

        private static ChunkLootManager _instance;

        // Pieza 3: per-zone loot profile table, GripPoseSet-style lazy Resources.Load cache (this
        // manager self-bootstraps via RuntimeInitializeOnLoadMethod — no scene/prefab to hold a
        // serialized reference, same reason CorpseSpawner caches GripPoseSet the same way). Null
        // until "Backrooms ▸ Create Zone Loot Table" has been run once; ZONE-BY-ZONE VARIATION IS
        // SILENTLY ABSENT (not an error) until then — every column resolves ZoneLootProfile.Default.
        private static ZoneLootTable _zoneLootTable;
        private static ZoneLootTable LootTable =>
            _zoneLootTable != null ? _zoneLootTable : (_zoneLootTable = Resources.Load<ZoneLootTable>("Loot/ZoneLootTable"));

        [Tooltip("Only run once the host is in this scene (avoids placing loot in the menu).")]
        public string gameplayScene = "STP_Showcase";
        [Tooltip("Seconds after scene load before the first scan (lets the chunks around the host stream in).")]
        [Min(0f)] public float warmupSeconds = 2f;
        [Tooltip("Scan cadence. NOT every frame: BuildDesiredSet allocates/iterates and the placement raycast is the real cost; the ring only changes on a chunk crossing, and this interval also retries chunks whose floor was not rendered yet.")]
        [Min(0.05f)] public float scanInterval = 0.25f;

        // Mirror ChunkStreamer's ring geometry so loot chunks == rendered chunks.
        /// <summary>
        /// Primera id que acuña el BACKEND (`STP_DROP_ID_BASE` en game_loop.rs). El espacio de
        /// ids de STP está particionado: por debajo acuña el Unity del host, por encima el
        /// backend (drops 0x4000_0000, construcciones 0x6000_0000, carryables 0x7000_0000).
        /// Este lado nunca debe cruzarla — el propio backend documenta que su rango existe
        /// "so it never collides with the low, host-Unity-assigned ids".
        /// </summary>
        private const uint BackendIdBase = 0x4000_0000u;

        private const int ViewRadius = 1;
        private const float Side = GridConstants.ChunkCells * GridConstants.CellSize; // 50 m
        // Walkable raycast — identical to StpItemSpawner/StpCarryableSpawner/ProxyGroundingHook
        // (origin kept UNDER the 4 m layer ceiling; short reach to the walkable slab).
        private const float RaycastUpOffset = 1f;
        private const float RaycastDownRange = 3f;

        private float _warmupEnd;
        private bool _warmedUp;
        private float _nextScan;

        // Columns whose loot has been generated+sent (or rolled empty). A column leaves this set on
        // unload so it regenerates on return.
        private readonly HashSet<(int cx, int cz)> _generatedItemChunks = new HashSet<(int, int)>();
        private readonly HashSet<(int cx, int cz)> _generatedCarryChunks = new HashSet<(int, int)>();

        // Session pickup memory (host-side, never persisted). A collected slot is skipped when its
        // chunk regenerates.
        //
        // ITEMS: permanent for the session (no timed respawn) → plain set.
        // CARRYABLES (construction materials): timed respawn (2026-07-07 balance). Each collected
        // slot stores WHEN it was taken; after CarryableRespawnSeconds it EXPIRES from this set,
        // its chunk is un-sealed and its still-live loot forgotten, so the next scan re-rolls the
        // chunk fresh and the expired material reappears — even if the player never left the chunk.
        // (This is why carryables key on time, not just presence: the "confirmed" set the previous
        // slice added is untouched — a respawned material simply gets a NEW id and re-confirms
        // normally; the behavioural change lives entirely in this collected set.)
        private readonly HashSet<(int cx, int cz, int slot)> _collectedItems = new HashSet<(int, int, int)>();
        private readonly Dictionary<(int cx, int cz, int slot), float> _collectedCarry = new Dictionary<(int, int, int), float>();
        // TODO(balance): 30 min. Only construction-material carryables respawn on this timer.
        private const float CarryableRespawnSeconds = 1800f;

        // network id → (chunk, slot) of loot WE placed and that is still live. This is our authority on
        // which mirror ids are ours (the mirror carries no chunk/slot info) — used for pickup-diff,
        // to drop our unloaded ids from the broadcast, and to span maxId so a fresh id never collides
        // with a not-yet-echoed one. Other authors' ids never appear here.
        private struct LiveLoot
        {
            public int cx, cz, slot;
        }
        private readonly Dictionary<uint, LiveLoot> _liveItems = new Dictionary<uint, LiveLoot>();
        private readonly Dictionary<uint, LiveLoot> _liveCarry = new Dictionary<uint, LiveLoot>();

        // Ids we have seen ECHOED back in a snapshot at least once. A live id missing from the mirror
        // only counts as "picked up" if it was previously confirmed — otherwise it is just a placement
        // the backend has not echoed yet (snapshot lag > scanInterval on a hitch) and must NOT be
        // mistaken for a pickup (which would wrongly mark its slot collected and never respawn it).
        private readonly HashSet<uint> _confirmedItems = new HashSet<uint>();
        private readonly HashSet<uint> _confirmedCarry = new HashSet<uint>();

        // Reused scratch (avoid per-scan allocs).
        private readonly HashSet<(int, int, int)> _desiredScratch = new HashSet<(int, int, int)>();
        private readonly HashSet<(int cx, int cz)> _desiredColumns = new HashSet<(int, int)>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!Enabled || _instance != null)
                return;

            var go = new GameObject("[ChunkLootManager]");
            _instance = go.AddComponent<ChunkLootManager>();
            DontDestroyOnLoad(go);
        }

        private void OnEnable() => _warmupEnd = Time.unscaledTime + warmupSeconds;

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + scanInterval;

            var init = NetworkInitializer.Instance;
            if (init == null || init.CurrentRole != NetworkInitializer.Role.Host)
                return; // only the host authors loot (backend ignores set_stp_* from a non-host)

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            var state = ipc.LatestState;
            if (state == null)
                return; // no world snapshot yet → no seed, no authoritative list to merge from

            if (SceneManager.GetActiveScene().name != gameplayScene)
                return;

            if (!_warmedUp)
            {
                if (Time.unscaledTime < _warmupEnd)
                    return; // let chunks around the host stream in first
                _warmedUp = true;
            }

            var cam = Camera.main;
            if (cam == null)
                return; // no local camera yet (host not in a playable scene)

            // STEP 3: fold pickups into the collected set BEFORE any unload, so a slot picked up on
            // the same scan it leaves the ring is remembered as taken (not forgotten-and-regenerated).
            AbsorbItemPickups(state.stpItems);
            AbsorbCarryPickups(state.stpCarryables);

            // STEP 1: current ring → desired columns (reuse ChunkStreamer's public ring; layerCount=1
            // gives the (cx,cz) column ring directly — placement is single-layer).
            int pcx = Mathf.FloorToInt(cam.transform.position.x / Side);
            int pcz = Mathf.FloorToInt(cam.transform.position.z / Side);
            _desiredScratch.Clear();
            ChunkStreamer.BuildDesiredSet(pcx, pcz, ViewRadius, layerCount: 1, _desiredScratch);
            _desiredColumns.Clear();
            foreach (var k in _desiredScratch)
                _desiredColumns.Add((k.Item1, k.Item2));

            float rayY = cam.transform.position.y; // player's vertical band → floor of the walked layer
            _worldSeed = state.worldSeed;
            _now = Time.unscaledTime;

            // STEP 4 (coalesced): reconcile each channel; at most one send per channel per scan.
            ReconcileItems(ipc, state, rayY);
            ReconcileCarryables(ipc, state, rayY);
        }

        // ── Pickup diff ─────────────────────────────────────────────────────────────────────
        // Any live id absent from the backend mirror was picked up → record its (chunk,slot) so a
        // reload does not resurrect it, and drop it from our live map.
        private void AbsorbItemPickups(List<StpItemMsg> mirror)
        {
            if (_liveItems.Count == 0) return;
            _presentIds.Clear();
            foreach (var it in mirror) _presentIds.Add(it.id);
            _idRemovalScratch.Clear();
            foreach (var kv in _liveItems)
            {
                if (_presentIds.Contains(kv.Key))
                    _confirmedItems.Add(kv.Key);                 // echoed → now confirmed live
                else if (_confirmedItems.Contains(kv.Key))
                    _idRemovalScratch.Add(kv.Key);               // was live, now gone → picked up
                // else: not yet echoed (placement lag) → leave; check again next scan
            }
            foreach (var id in _idRemovalScratch)
            {
                var v = _liveItems[id];
                _collectedItems.Add((v.cx, v.cz, v.slot));
                _liveItems.Remove(id);
                _confirmedItems.Remove(id);
            }
        }

        private void AbsorbCarryPickups(List<StpCarryableMsg> mirror)
        {
            if (_liveCarry.Count == 0) return;
            _presentIds.Clear();
            foreach (var c in mirror) _presentIds.Add(c.id);
            _idRemovalScratch.Clear();
            foreach (var kv in _liveCarry)
            {
                if (_presentIds.Contains(kv.Key))
                    _confirmedCarry.Add(kv.Key);
                else if (_confirmedCarry.Contains(kv.Key))
                    _idRemovalScratch.Add(kv.Key);
            }
            foreach (var id in _idRemovalScratch)
            {
                var v = _liveCarry[id];
                _collectedCarry[(v.cx, v.cz, v.slot)] = _now; // stamp WHEN taken → drives the respawn timer
                _liveCarry.Remove(id);
                _confirmedCarry.Remove(id);
            }
        }

        // ── Items channel ─────────────────────────────────────────────────────────────────────
        private void ReconcileItems(IPCClient ipc, WorldStateMsg state, float rayY)
        {
            _removedIds.Clear();
            CollectUnloads(_generatedItemChunks, _liveItems); // appends to _removedIds, prunes generated+live
            foreach (var id in _removedIds) _confirmedItems.Remove(id);
            LiveSlotsOf(_liveItems);
            CollectLoads(_generatedItemChunks, _collectedItems, ChunkLootRoll.RollItems,
                         ChunkLootRoll.RollItemsByStyle, rayY);

            if (_removedIds.Count == 0 && _pendingPlacements.Count == 0)
                return; // nothing changed this scan → no send (coalesced)

            var merged = new List<StpItemSpec>();
            uint maxId = 0;
            // The broadcast is the backend mirror (other authors' drops + our already-echoed loot),
            // minus what we unloaded, plus this scan's fresh placements. Pickups propagate for free
            // (a picked item vanishes from the mirror). We deliberately do NOT re-emit our own
            // not-yet-echoed placements from a private store: without a reliable per-item echo signal
            // (world_revision tracks world.chunks, not net.stp_items) that cannot be told apart from a
            // pickup, so re-emitting would resurrect items the player just took. Residual: on a
            // snapshot hitch a placement from the previous scan can be dropped by a racing full-replace
            // and re-appears on the chunk's next reload (deterministic) — rare and self-healing.
            foreach (var it in state.stpItems)
            {
                if (_removedIds.Contains(it.id)) continue; // our unloaded loot leaves the broadcast
                merged.Add(new StpItemSpec { id = it.id, defId = it.defId, count = it.count, position = it.position, rotation = it.rotation });
                if (it.id > maxId && it.id < BackendIdBase) maxId = it.id;
            }
            // maxId must ALSO span our live ids (echoed or not) so a fresh id below can never collide
            // with — nor overwrite the _liveItems entry of — a not-yet-echoed placement.
            foreach (var id in _liveItems.Keys)
                if (id > maxId && id < BackendIdBase) maxId = id;

            uint nextId = maxId + 1;
            // Los ids del backend quedan FUERA del máximo, arriba. El roster es multi-autor: lleva
            // también los drops que acuña el backend, desde 0x4000_0000 (game_loop.rs, constante
            // STP_DROP_ID_BASE). Tomando el máximo global, una sola pieza soltada por el backend
            // empujaba este contador dentro del rango alto y a partir de ahí Unity acuñaba ids que
            // el backend cree suyos — el comentario del propio backend dice que ese rango existe
            // "so it never collides with the low, host-Unity-assigned ids".
            //
            // Es la mitad cliente del arreglo de siembra de asignadores: sin ella, sembrar el
            // backend desde su propio rango no basta, porque el que invade es este lado.
            if (nextId >= BackendIdBase)
            {
                Debug.LogError($"[ChunkLootManager] espacio de ids del cliente agotado (nextId=0x{nextId:x8} " +
                               $"alcanza la base del backend 0x{BackendIdBase:x8}); no se acuñan mas items este scan.");
                _pendingPlacements.Clear();
                return;
            }
            int placed = 0;
            foreach (var p in _pendingPlacements)
            {
                var def = ItemDefinition.GetWithName(p.entry.Name);
                if (def == null)
                {
                    Debug.LogWarning($"[ChunkLootManager] item '{p.entry.Name}' not in ItemDefinition DB; skipped.");
                    continue;
                }
                uint id = nextId++;
                merged.Add(new StpItemSpec { id = id, defId = def.Id, count = p.entry.Count, position = p.pos, rotation = p.entry.Rotation });
                _liveItems[id] = new LiveLoot { cx = p.cx, cz = p.cz, slot = p.entry.Slot };
                placed++;
            }

            ipc.SendSetStpItems(merged);
            Debug.Log($"[ChunkLootManager] items → {merged.Count} live ({placed} placed, {_removedIds.Count} unloaded this scan).");
        }

        // ── Carryables channel ─────────────────────────────────────────────────────────────────
        private void ReconcileCarryables(IPCClient ipc, WorldStateMsg state, float rayY)
        {
            _removedIds.Clear();
            // ORDER MATTERS: unload chunks that left the ring FIRST (harvests their live ids while the
            // chunk is still in _generatedCarryChunks), THEN expire. Expiry un-seals a chunk by removing
            // it from _generatedCarryChunks; if a chunk both expires AND leaves the ring on the same
            // scan, expiring first would make CollectUnloads (which only scans still-generated chunks)
            // skip it, orphaning its live ids in the broadcast. Unload-then-expire closes that gap
            // (expiry's Remove of an already-unloaded chunk is a harmless no-op).
            CollectUnloads(_generatedCarryChunks, _liveCarry); // appends to _removedIds
            // Timed respawn (materials): expire old collected slots and un-seal their chunks so
            // CollectLoads re-rolls them. Still-live materials are untouched (they keep their ids and
            // stay in the broadcast); CollectLoads skips currently-live slots (LiveSlotsOf) so only the
            // expired (now un-collected, not-live) slots are re-placed.
            ExpireCarryables();
            foreach (var id in _removedIds) _confirmedCarry.Remove(id);
            LiveSlotsOf(_liveCarry);
            CollectLoads(_generatedCarryChunks, _collectedCarry.Keys, ChunkLootRoll.RollCarryables,
                         ChunkLootRoll.RollCarryablesByStyle, rayY);

            if (_removedIds.Count == 0 && _pendingPlacements.Count == 0)
                return;

            var merged = new List<StpCarryableSpec>();
            uint maxId = 0;
            foreach (var c in state.stpCarryables) // mirror minus unloaded (see items channel for why we don't re-emit)
            {
                if (_removedIds.Contains(c.id)) continue;
                merged.Add(new StpCarryableSpec { id = c.id, defId = c.defId, position = c.position, rotation = c.rotation });
                if (c.id > maxId) maxId = c.id;
            }
            foreach (var id in _liveCarry.Keys) // maxId spans our live ids (echoed or not) — no id reuse/overwrite
                if (id > maxId) maxId = id;

            uint nextId = maxId + 1;
            int placed = 0;
            foreach (var p in _pendingPlacements)
            {
                var def = CarryableDefinition.GetWithName(p.entry.Name);
                if (def == null)
                {
                    Debug.LogWarning($"[ChunkLootManager] carryable '{p.entry.Name}' not in CarryableDefinition DB; skipped.");
                    continue;
                }
                uint id = nextId++;
                merged.Add(new StpCarryableSpec { id = id, defId = def.Id, position = p.pos, rotation = p.entry.Rotation });
                _liveCarry[id] = new LiveLoot { cx = p.cx, cz = p.cz, slot = p.entry.Slot };
                placed++;
            }

            ipc.SendSetStpCarryables(merged);
            Debug.Log($"[ChunkLootManager] carryables → {merged.Count} live ({placed} placed, {_removedIds.Count} unloaded this scan).");
        }

        // Columns in `generated` that left the ring: gather OUR live ids there into _removedIds and
        // forget them (they regenerate deterministically on return, minus collected). Appends to
        // _removedIds (the caller clears it once at the top of the scan, so expiry can pre-fill it).
        private void CollectUnloads(HashSet<(int cx, int cz)> generated, Dictionary<uint, LiveLoot> live)
        {
            _unloadScratch.Clear();
            foreach (var col in generated)
                if (!_desiredColumns.Contains(col)) _unloadScratch.Add(col);

            foreach (var col in _unloadScratch)
            {
                foreach (var kv in live)
                    if (kv.Value.cx == col.cx && kv.Value.cz == col.cz) _removedIds.Add(kv.Key);
                generated.Remove(col);
            }
            foreach (var id in _removedIds)
                live.Remove(id);
        }

        // Rebuild _liveSlotsScratch (current (chunk,slot) set) from a live map, so CollectLoads can
        // skip slots that are already placed & present when it re-rolls an un-sealed chunk (else the
        // re-roll would duplicate a still-live material).
        private void LiveSlotsOf(Dictionary<uint, LiveLoot> live)
        {
            _liveSlotsScratch.Clear();
            foreach (var v in live.Values)
                _liveSlotsScratch.Add((v.cx, v.cz, v.slot));
        }

        // Desired columns not yet generated: roll + raycast-place, now walkability-checked (Fix
        // priorizado worldgen Alpha 1 — see TryPlace). A non-empty roll whose floor is not
        // rendered yet stays pending (retried next scan); a slot that lands in a wall/pillar even
        // after retrying nearby is permanently omitted instead. Fills _pendingPlacements.
        private void CollectLoads(
            HashSet<(int cx, int cz)> generated,
            ICollection<(int cx, int cz, int slot)> collected,
            System.Func<long, int, int, ZoneLootProfile, List<ChunkLootRoll.Entry>> roll,
            ChunkLootRoll.StyleRoll styleRoll,
            float rayY)
        {
            _pendingPlacements.Clear();
            // ADR-108 D4 — con WG3 mandando, la puerta es el PAPEL del espacio, no la zona del chunk.
            // `zone_kind` es de WG2: seguir leyéndolo aquí reparte el botín por un mapa que ya no
            // existe. Misma fuente de verdad que el resto del cliente (`Wg3Enabled`, lo dice el
            // backend en el saludo), no una bandera nueva que pueda contradecirla.
            bool wg3 = IPCClient.Instance is { Wg3Enabled: true };
            var streamer = wg3 ? BackroomsSurvival.WorldGen3.Wg3ChunkStreamer.Active : null;
            foreach (var col in _desiredColumns)
            {
                if (generated.Contains(col)) continue;

                if (wg3)
                {
                    // WG3 encendido pero sin streamer montado todavía: nada que preguntar. Se deja
                    // la columna SIN sellar, igual que una zona que aún no ha llegado.
                    if (streamer == null) continue;
                    var here = col;
                    Vector3 centre = Vector3.zero;
                    var byStyle = styleRoll(
                        _worldSeed, col.cx, col.cz,
                        (u, v) =>
                        {
                            centre = new Vector3((here.cx + u) * Side, rayY, (here.cz + v) * Side);
                            if (!streamer.TryGetStyle(centre, out byte st)) return null;
                            return LootTable != null
                                ? LootTable.ProfileForStyle(st)
                                : ChunkLootRoll.DefaultStyleLootProfiles()[
                                      Mathf.Clamp(st, 0, ChunkLootRoll.DefaultStyleLootProfiles().Length - 1)];
                        },
                        out bool spaceKnown);
                    if (!spaceKnown)
                    {
                        // «Ahí no hay espacio» tiene DOS causas y sólo una se arregla esperando. Con
                        // el chunk montado y sin nada en esa vertical a ninguna cota, la respuesta ya
                        // es definitiva: el plan deja vacíos, y dejar la columna sin sellar la haría
                        // re-sortearse a cada barrido para siempre. Si hay espacio pero en otra
                        // planta, se espera: el jugador puede subir.
                        if (streamer.ChunkIsBuilt(centre) && !streamer.AnySpaceInColumn(centre))
                            generated.Add(col);
                        continue;
                    }
                    PlaceRolled(col, byStyle, generated, collected, rayY);
                    continue;
                }

                // Zone gate (Pieza 3, no-timeout variant — see class doc "ZONE GATE"): a column
                // whose zone_kind is not known yet is left UNSEALED (no generated.Add) and retried
                // next scan. No fallback profile — rolling with the wrong zone would permanently
                // seal the column under stale content (see ZoneLootProfile's hard constraint on
                // slot-count stability; a wrong PROFILE is still recoverable in principle, but there
                // is no reason to accept the risk when waiting costs nothing here).
                if (!ZoneRegistry.TryGetZone(col.cx, col.cz, out byte zoneKind))
                    continue;

                var profile = LootTable != null ? LootTable.Profile(zoneKind) : ZoneLootProfile.Default;
                PlaceRolled(col, roll(_worldSeed, col.cx, col.cz, profile), generated, collected, rayY);
            }
        }

        // El tramo común a las dos puertas —la de zona de WG2 y la de papel de WG3—: descontar lo ya
        // cogido y lo que sigue vivo, colocar cada hueco y sellar la columna. Se extrajo tal cual al
        // migrar el reparto a WG3 (ADR-108 D4); ni una regla cambia respecto a lo que hacía dentro
        // del bucle.
        private void PlaceRolled(
            (int cx, int cz) col,
            List<ChunkLootRoll.Entry> entries,
            HashSet<(int cx, int cz)> generated,
            ICollection<(int cx, int cz, int slot)> collected,
            float rayY)
        {
            if (entries.Count == 0)
            {
                generated.Add(col); // deterministically empty under this zone's profile — don't retry
                return;
            }

            ChunkLootRoll.RemoveCollected(entries, col.cx, col.cz, collected);
            // Also skip slots already placed & present (a re-rolled un-sealed chunk keeps its
            // still-live materials — don't duplicate them; only expired/uncollected slots remain).
            entries.RemoveAll(e => _liveSlotsScratch.Contains((col.cx, col.cz, e.Slot)));
            if (entries.Count == 0)
            {
                generated.Add(col); // every slot already taken or still live — sealed, don't retry
                return;
            }

            bool placedAny = false;
            bool anyPermanentlyUnwalkable = false;
            foreach (var e in entries)
            {
                switch (TryPlace(col.cx, col.cz, e, rayY, out Vector3 pos))
                {
                    case PlaceResult.Placed:
                        placedAny = true;
                        _pendingPlacements.Add((col.cx, col.cz, e, pos));
                        break;
                    case PlaceResult.Unwalkable:
                        // Every retry inside the zone's dispersion also landed in a
                        // wall/pillar — this is not "floor not built yet" (that keeps the
                        // column pending forever, see CollectLoads header), it is permanent
                        // for this roll. Omit the slot instead of never sealing the column.
                        anyPermanentlyUnwalkable = true;
                        Debug.LogWarning($"[ChunkLootManager] slot {e.Slot} en columna " +
                                          $"({col.cx},{col.cz}) cayo en muro/pilar tras " +
                                          $"{WalkabilityRetries} reintentos; omitido.");
                        break;
                    case PlaceResult.FloorMissing:
                        break; // retried next scan, unchanged from before this fix
                }
            }

            // Seal once at least one slot reached a TERMINAL outcome (placed, or permanently
            // unwalkable). If every entry is still FloorMissing, the floor isn't rendered yet
            // → leave pending for a later scan (unchanged from before this fix).
            if (placedAny || anyPermanentlyUnwalkable)
                generated.Add(col);
        }

        private enum PlaceResult { FloorMissing, Unwalkable, Placed }

        // Retries a slot that landed in a wall/pillar within the zone's own dispersion radius
        // before giving up on it. Deterministic per (worldSeed, cx, cz, slot, attempt) — not
        // drawn from ChunkLootRoll's roll stream (that stream is fully consumed once per roll
        // and discarded; this only nudges a PHYSICAL position, it never changes what item/count
        // was rolled), so re-running this on the same inputs always finds the same slot.
        private const int WalkabilityRetries = 8;
        private const ulong PlacementRetrySalt = 0xB0_A7_D0_5EUL;

        private PlaceResult TryPlace(int cx, int cz, ChunkLootRoll.Entry e, float rayY, out Vector3 point)
        {
            if (!TryRaycastFloor(cx, cz, e.U, e.V, rayY, out Vector3 raycastPoint))
            {
                point = default;
                return PlaceResult.FloorMissing;
            }

            // ADR-108 D4 — CON WG3 NO HAY MAPA DE PAREDES QUE MIRAR, y el rayo solo no basta: sale de
            // DENTRO del muro, y un rayo que nace dentro de un collider no lo toca (cara trasera), así
            // que aterriza en el suelo de debajo y el objeto queda emparedado. La prueba honesta es
            // preguntar por el SITIO que ocuparía: una esfera del tamaño de un objeto justo encima del
            // punto. Si ahí hay geometría, no cabe, y se reintenta como cualquier otro hueco en muro.
            if (IPCClient.Instance is { Wg3Enabled: true })
            {
                if (!IsClearOfGeometry(raycastPoint))
                {
                    var wrng = new DeterministicRng(ChunkLootRoll.Hash(_worldSeed, cx, cz,
                        PlacementRetrySalt ^ (ulong)(uint)e.Slot));
                    for (int attempt = 0; attempt < WalkabilityRetries; attempt++)
                    {
                        float ru = Clamp01(e.U + (wrng.NextFloat() - 0.5f) * 2f * ZoneSpreadNormalized);
                        float rv = Clamp01(e.V + (wrng.NextFloat() - 0.5f) * 2f * ZoneSpreadNormalized);
                        if (!TryRaycastFloor(cx, cz, ru, rv, rayY, out Vector3 wp)) continue;
                        if (!IsClearOfGeometry(wp)) continue;
                        point = wp;
                        return PlaceResult.Placed;
                    }
                    point = default;
                    return PlaceResult.Unwalkable;
                }
                point = raycastPoint;
                return PlaceResult.Placed;
            }

            // No wall data cached yet (should not happen once the raycast above succeeded —
            // _wallsCache is populated before the chunk's floor is even built, see
            // ChunkStreamer.OnChunkDataReceived — but degrade gracefully instead of assuming):
            // fall back to floor-only placement, same as before this fix.
            if (ChunkStreamer.Instance == null ||
                !ChunkStreamer.Instance.TryGetWalls(cx, cz, LayerOf(rayY), out byte[,] walls))
            {
                point = raycastPoint;
                return PlaceResult.Placed;
            }

            if (ChunkLootRoll.IsWalkable(walls, e.U, e.V))
            {
                point = raycastPoint;
                return PlaceResult.Placed;
            }

            var rng = new DeterministicRng(ChunkLootRoll.Hash(_worldSeed, cx, cz,
                PlacementRetrySalt ^ (ulong)(uint)e.Slot));
            for (int attempt = 0; attempt < WalkabilityRetries; attempt++)
            {
                float ru = Clamp01(e.U + (rng.NextFloat() - 0.5f) * 2f * ZoneSpreadNormalized);
                float rv = Clamp01(e.V + (rng.NextFloat() - 0.5f) * 2f * ZoneSpreadNormalized);
                if (!ChunkLootRoll.IsWalkable(walls, ru, rv)) continue;
                if (!TryRaycastFloor(cx, cz, ru, rv, rayY, out Vector3 retryPoint)) continue;
                point = retryPoint;
                return PlaceResult.Placed;
            }

            point = default;
            return PlaceResult.Unwalkable;
        }

        // Reuses ChunkLootRoll.ZoneSpreadRadius (12 m normalized) for both channels — items use
        // a tighter CacheClusterRadius for their original cluster, so an item retry can wander
        // slightly further than that cluster, but never further than the zone itself; safe, not
        // worth a second constant here.
        private const float ZoneSpreadNormalized = ChunkLootRoll.ZoneSpreadRadius;

        /// <summary>Radio de la esfera que decide si un objeto CABE ahí. Del orden de un bote: más
        /// grande rechazaría rincones perfectamente buenos, más pequeño se cuela en un muro.</summary>
        private const float FitRadius = 0.2f;

        /// <summary>¿Cabe un objeto justo encima de ese punto de suelo? Se sube el centro de la esfera
        /// su propio radio para no chocar contra el suelo que se acaba de encontrar.</summary>
        private static bool IsClearOfGeometry(Vector3 floorPoint) =>
            !Physics.CheckSphere(floorPoint + Vector3.up * (FitRadius + 0.02f), FitRadius,
                                 GridChunkBuilder.GeoMask, QueryTriggerInteraction.Ignore);

        private static bool TryRaycastFloor(int cx, int cz, float u, float v, float rayY, out Vector3 point)
        {
            float wx = cx * Side + u * Side;
            float wz = cz * Side + v * Side;
            var origin = new Vector3(wx, rayY + RaycastUpOffset, wz);
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, RaycastUpOffset + RaycastDownRange,
                    GridChunkBuilder.GeoMask, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                return true;
            }
            point = default;
            return false;
        }

        private static int LayerOf(float rayY) => Mathf.FloorToInt(rayY / GridConstants.LayerHeight);
        private static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);

        // ── Timed material respawn (carryables only) ──────────────────────────────────────────
        // Expire collected-carryable slots older than CarryableRespawnSeconds and UN-SEAL their
        // chunks so CollectLoads re-rolls them this scan. Still-live materials are left untouched
        // (they keep their ids, stay in the broadcast); CollectLoads skips currently-live slots, so
        // only the expired (now un-collected, not-live) slots get re-placed. Un-picked slots are
        // never affected. Atomic by construction: nothing leaves the broadcast until its replacement
        // is placed (fixes the earlier forget-then-replace dropout).
        private void ExpireCarryables()
        {
            if (_collectedCarry.Count == 0) return;

            SelectExpired(_collectedCarry, _now, CarryableRespawnSeconds, _expiredScratch);
            if (_expiredScratch.Count == 0) return;

            foreach (var key in _expiredScratch)
            {
                _collectedCarry.Remove(key);
                _generatedCarryChunks.Remove((key.cx, key.cz)); // un-seal → CollectLoads re-rolls it
            }
        }

        /// <summary>Pure: collect keys whose stamped time is at least <paramref name="ttlSeconds"/>
        /// old relative to <paramref name="now"/>. Extracted for headless testing (no Unity time).</summary>
        public static void SelectExpired(Dictionary<(int cx, int cz, int slot), float> collected,
            float now, float ttlSeconds, List<(int cx, int cz, int slot)> into)
        {
            into.Clear();
            foreach (var kv in collected)
                if (now - kv.Value >= ttlSeconds) into.Add(kv.Key);
        }

        // worldSeed + scan time captured each scan from the snapshot (read by CollectLoads/expiry).
        private long _worldSeed;
        private float _now;

        // Static scratch shared across the two channels (single-threaded Update; sequential, never
        // reentrant). Cleared at each point of use.
        private static readonly HashSet<uint> _presentIds = new HashSet<uint>();
        private static readonly List<uint> _idRemovalScratch = new List<uint>();

        // Per-channel scratch (the two channels run sequentially in one Update; cleared per channel).
        private readonly HashSet<uint> _removedIds = new HashSet<uint>();
        private readonly List<(int cx, int cz)> _unloadScratch = new List<(int, int)>();
        private readonly List<(int cx, int cz, ChunkLootRoll.Entry entry, Vector3 pos)> _pendingPlacements
            = new List<(int, int, ChunkLootRoll.Entry, Vector3)>();
        // Expiry scratch (carryable channel only).
        private readonly List<(int cx, int cz, int slot)> _expiredScratch = new List<(int, int, int)>();
        // Current (chunk,slot) set of OUR live loot for the channel being reconciled — rebuilt per
        // channel per scan by LiveSlotsOf; lets CollectLoads skip slots that are already placed.
        private readonly HashSet<(int cx, int cz, int slot)> _liveSlotsScratch = new HashSet<(int, int, int)>();
    }
}
