using System;
using System.Collections.Generic;
using UnityEngine;

// Client-side mirrors of the backend's IPC wire types (backend/src/ipc/mod.rs). One class per
// `serde` struct, decoded straight off the msgpack bytes.
//
// ── The decode contract, once, so the 22 Parse methods below don't each restate it ──
//
//  * Every `Parse(MsgPackReader)` reads its OWN map header, then loops the pairs matching keys
//    with `MsgPackReader.Is(key, "...")`. Order-independent by construction.
//  * `else r.Skip()` on an unrecognized key is MANDATORY, not defensive padding: it is what lets
//    a newer backend add a field without breaking this client (and what makes every
//    `skip_serializing_if` field on the Rust side safe).
//  * A key the wire omits leaves the field at its declared default — the C# initializer IS the
//    fallback, so lists/arrays stay empty rather than null.
//  * Root-tagged messages (WorldState, DeltaUpdate, ChunkData, Event) instead take
//    `Parse(reader, remainingPairs)`: IPCClient.Dispatch has already eaten the map header and the
//    "type" pair, so they must NOT read a header of their own.
//  * Post-fixups (chunk_schema<=0 → 1, cell sizes <=0 → defaults, macro_size <=0 → 1) run AFTER
//    the loop, never inside it — key order must not change the result.
//
// Nothing here allocates an intermediate Dictionary/object[]/box; that redesign is what closed
// docs/STATE.md's "mayor coste real de la ruta de parseo". The one deliberate exception is
// GameEventMsg.data — see IPCParse at the bottom of this file for why it stays a generic tree.

namespace BackroomsSurvival.Net
{


    /// <summary>
    /// ADR-061 — primer frame de cada conexión IPC: la revisión de esquema del backend.
    /// Root-tagged (<c>ServerMessage::Hello</c>, <c>"type":"hello"</c>).
    ///
    /// El default de <see cref="schemaVersion"/> es 0 A PROPÓSITO: un hello sin la clave (o con
    /// ella de otro tipo) cae en mismatch, nunca en un falso match. Es el único sitio de este
    /// archivo donde el default silencioso del contrato de cabecera se usa para FALLAR, no para
    /// degradar — ver <see cref="WireSchema.IsCompatible"/>.
    /// </summary>
    public class HelloMsg
    {
        public uint schemaVersion;

        public static HelloMsg Parse(MsgPackReader r, int remainingPairs)
        {
            var m = new HelloMsg();
            for (int i = 0; i < remainingPairs; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "schema_version")) m.schemaVersion = (uint)r.ReadInt();
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>
    /// ADR-046 — un frame de voz de un peer, ya filtrado por distancia en el host.
    /// Root-tagged (<c>ServerMessage::PeerVoice</c>, <c>"type":"peer_voice"</c>).
    ///
    /// `seq` es lo que permite detectar pérdida y descartar lo que llega tarde: el transporte
    /// es NO FIABLE a propósito (ADR-039 — un paquete de voz retransmitido llega tarde y ya no
    /// sirve, y la cola fiable se vacía ENTERA al superar MAX_RETRIES). `data` es opaco aquí:
    /// ni el backend ni esta clase saben qué códec lo produjo.
    /// </summary>
    public class PeerVoiceMsg
    {
        /// <summary>Instancia compartida para "sin audio" — evita alocar en un camino de 25 Hz.</summary>
        private static readonly byte[] NoAudio = new byte[0];

        public ushort peerId;
        public ushort seq;

        /// <summary>NUNCA null: un frame sin la clave deja el array VACÍO, así que el consumidor
        /// solo trata "este frame no traía audio" y no un caso de nulo aparte.</summary>
        public byte[] data = NoAudio;

        public static PeerVoiceMsg Parse(MsgPackReader r, int remainingPairs)
        {
            var m = new PeerVoiceMsg();
            for (int i = 0; i < remainingPairs; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "peer_id")) m.peerId = (ushort)r.ReadInt();
                else if (MsgPackReader.Is(k, "seq")) m.seq = (ushort)r.ReadInt();
                else if (MsgPackReader.Is(k, "data"))
                {
                    var bytes = r.ReadBin();
                    if (bytes.Length > 0) m.data = bytes;
                }
                else r.Skip();
            }
            return m;
        }
    }

    // ───────────────────────── Remote players (pose relay) ─────────────────────────

    public class RemotePlayerMsg
    {
        public int id;
        public string name = "";
        public Vector3 position;
        public float rotation;
        public string animation = "idle";
        // ADR-020: cosmetic crouch state of this remote player (host-relayed).
        public bool crouch;
        // ADR-021: cosmetic camera pitch in degrees (−90..90, quantized to 1° on the wire).
        public int pitch;
        // ADR-022: cosmetic worn clothing item IDs [Head, Torso, Legs, Feet] (0 = empty).
        public int[] equipment = new int[4];
        // ADR-023: cosmetic held item ID (0 = empty hands).
        public int heldItem;
        // ADR-024: cosmetic hit-reaction counter (monotonic, wrapping; 0 = never hit).
        public int hitSeq;
        // ADR-028 post-E3: cosmetic dead flag (server-derived on the peer's own backend) —
        // hide the standing proxy while true (the corpse is the visible body).
        public bool dead;
        // ADR-038: cosmetic "showing its real form" flag — true only while the robapieles (ADR-016)
        // is in SPRINT/STATUE. Backend-derived: it has no counterpart in the outgoing PlayerInput,
        // so nothing this client writes can set it, and for a real peer it is always false.
        public bool revealed;
        // ADR-042: cosmetic "this peer's held wieldable is lit" flag (read by ProxyLightHook).
        public bool lightOn;
        // ADR-042: cosmetic shot counter (monotonic, wrapping; 0 = never fired). The proxy hook
        // plays the gunshot on a DELTA, so a burst that outruns the 10 Hz relay still lands.
        public int fireSeq;
        // ADR-044: cosmetic sustained-state bits — bit 0 = aiming, bit 1 = reloading.
        public int buttons;
        // ADR-044: cosmetic melee-swing counter (monotonic, wrapping; 0 = never swung).
        public int meleeSeq;
        // ADR-048: cosmetic vocalisation counter (monotonic, wrapping; 0 = never vocalised, and it
        // never wraps back onto 0). ProxyVocalHook plays a sound on a DELTA, never on a level —
        // a scream modelled as a flag is a scream lost with the first dropped datagram.
        public int vocalSeq;
        // ADR-048: which voice the last bump was. 0 reveal, 1 search-shriek, 2 noise-grunt,
        // 3 stalking-breath. Meaningless on its own — only read together with vocalSeq.
        public int vocalKind;
        // ADR-049: cosmetic carry state — the CarryableDefinition id on this peer's shoulder
        // (0 = empty hands) and how many units. A LEVEL, unlike the counters above: ProxyCarryHook
        // rebuilds its visuals when either value changes, never on a delta.
        public int carryDef;
        public int carryCount;

        public static RemotePlayerMsg Parse(MsgPackReader reader)
        {
            var r = new RemotePlayerMsg();
            int n = reader.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = reader.ReadKey();
                if (MsgPackReader.Is(k, "id")) r.id = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "name")) r.name = reader.ReadString();
                else if (MsgPackReader.Is(k, "position")) r.position = reader.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) r.rotation = reader.ReadFloat();
                else if (MsgPackReader.Is(k, "animation")) r.animation = reader.ReadStringCached();
                else if (MsgPackReader.Is(k, "crouch")) r.crouch = reader.ReadBool();
                else if (MsgPackReader.Is(k, "pitch")) r.pitch = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "equipment")) reader.ReadIntArrayInto(r.equipment);
                else if (MsgPackReader.Is(k, "held_item")) r.heldItem = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "hit_seq")) r.hitSeq = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "dead")) r.dead = reader.ReadBool();
                else if (MsgPackReader.Is(k, "revealed")) r.revealed = reader.ReadBool();
                else if (MsgPackReader.Is(k, "light_on")) r.lightOn = reader.ReadBool();
                else if (MsgPackReader.Is(k, "fire_seq")) r.fireSeq = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "buttons")) r.buttons = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "melee_seq")) r.meleeSeq = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "vocal_seq")) r.vocalSeq = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "vocal_kind")) r.vocalKind = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "carry_def")) r.carryDef = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "carry_count")) r.carryCount = (int)reader.ReadInt();
                else reader.Skip();
            }
            return r;
        }
    }



    // ───────────────────────── Entities & world items ─────────────────────────

    public class EntityViewMsg
    {
        public uint id;
        public string entityType = "lurker";
        public Vector3 position;
        public float rotation;
        public string state = "idle";
        public float healthPct = 1f;

        public static EntityViewMsg Parse(MsgPackReader r)
        {
            var e = new EntityViewMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) e.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "entity_type")) e.entityType = r.ReadStringCached();
                else if (MsgPackReader.Is(k, "position")) e.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) e.rotation = r.ReadFloat();
                else if (MsgPackReader.Is(k, "state")) e.state = r.ReadStringCached();
                else if (MsgPackReader.Is(k, "health_pct")) e.healthPct = r.ReadFloat();
                else r.Skip();
            }
            return e;
        }
    }

    public class ItemViewMsg
    {
        public uint id;
        public string itemType = "";
        public Vector3 position;
        public int quantity;

        public static ItemViewMsg Parse(MsgPackReader r)
        {
            var i = new ItemViewMsg();
            int n = r.ReadMapHeader();
            for (int idx = 0; idx < n; idx++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) i.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "item_type")) i.itemType = r.ReadStringCached();
                else if (MsgPackReader.Is(k, "position")) i.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "quantity")) i.quantity = (int)r.ReadInt();
                else r.Skip();
            }
            return i;
        }
    }


    /// <summary>
    /// Phase 6.6/6.7 — debug placeholder for one backend virtual vertical node.
    /// World-space AABB; render-as-debug only (no collision, no traversal).
    /// kind: "stair" | "ramp" | "shaft" | "atrium" | "sealed_upper" | "other".
    /// </summary>
    public class VerticalDebugMarkerMsg
    {
        public uint id;
        public string kind = "";
        public Vector3 worldMin;
        public Vector3 worldMax;

        public static VerticalDebugMarkerMsg Parse(MsgPackReader r)
        {
            var m = new VerticalDebugMarkerMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) m.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "kind")) m.kind = r.ReadStringCached();
                else if (MsgPackReader.Is(k, "world_min")) m.worldMin = r.ReadVec3();
                else if (MsgPackReader.Is(k, "world_max")) m.worldMax = r.ReadVec3();
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>ADR-028: outbound loot stack the client reports via IPCClient.SendReportDeathLoot.
    /// itemId = raw STP item id (DataIdReference — may be negative), NEVER a backend enum.</summary>
    public struct CorpseLootStack
    {
        public int itemId;
        public int quantity;
    }

    /// <summary>ADR-045 Fase 3: one item-instance property (durability, ammo, ...). Mirrors STP's
    /// <c>ItemProperty {int Id, double Value}</c> 1:1.</summary>
    public struct ItemPropertyValue
    {
        public int id;
        public double value;
    }

    /// <summary>ADR-045 Fase 3: instance-fidelity companion to <see cref="CorpseLootStack"/> —
    /// carries WHERE the item sits (container/slot) and its properties, for report_inventory and
    /// inventory_restored. Unlike CorpseLootStack (death loot, world chests — slot is meaningless
    /// there), this is used ONLY by the player's own live-inventory report/restore round trip.
    /// itemId = raw STP item id (DataIdReference — may be negative), same convention as
    /// CorpseLootStack.</summary>
    public struct InventoryStackV2
    {
        public int itemId;
        public int quantity;
        public byte container;
        public byte slot;
        public List<ItemPropertyValue> props;
    }

    /// <summary>
    /// ADR-028 — one lootable corpse replicated in world_state.visible_corpses. position is the
    /// server-frozen death position (the loot interaction point); the client ragdoll is cosmetic
    /// and never moves it. equipment/heldItem are the cosmetic snapshot that dresses the ragdoll.
    /// </summary>
    public class CorpseViewMsg
    {
        public uint id;
        public uint ownerId;
        public string ownerName = "";
        public Vector3 position;
        public int[] equipment = new int[4];
        public int heldItem;
        public List<CorpseLootStack> items = new List<CorpseLootStack>();
        /// <summary>ADR-028 amendment: true → host-seeded supply chest (crate visual, no ragdoll).</summary>
        public bool isChest;

        public static CorpseViewMsg Parse(MsgPackReader r)
        {
            var m = new CorpseViewMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) m.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "owner_id")) m.ownerId = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "owner_name")) m.ownerName = r.ReadString();
                else if (MsgPackReader.Is(k, "is_chest")) m.isChest = r.ReadBool();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "equipment")) r.ReadIntArrayInto(m.equipment);
                else if (MsgPackReader.Is(k, "held_item")) m.heldItem = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "items"))
                {
                    int sc = r.ReadArrayHeader();
                    if (sc > 0)
                    {
                        m.items.Capacity = sc;
                        for (int si = 0; si < sc; si++)
                        {
                            var stack = new CorpseLootStack();
                            int sn = r.ReadMapHeader();
                            for (int fi = 0; fi < sn; fi++)
                            {
                                var fk = r.ReadKey();
                                if (MsgPackReader.Is(fk, "item_id")) stack.itemId = (int)r.ReadInt();
                                else if (MsgPackReader.Is(fk, "quantity")) stack.quantity = (int)r.ReadInt();
                                else r.Skip();
                            }
                            m.items.Add(stack);
                        }
                    }
                }
                else r.Skip();
            }
            return m;
        }
    }

    // ──────── Root messages: what IPCClient.Dispatch matches on the "type" tag ────────
    // These take (reader, remainingPairs) — Dispatch already consumed the header and the tag.

    public class WorldStateMsg
    {
        public long tick;
        public long worldSeed;
        public long worldRevision;
        public LocalPlayerMsg localPlayer = new LocalPlayerMsg();
        public List<RemotePlayerMsg> remotePlayers = new List<RemotePlayerMsg>();
        public List<ChunkViewMsg> visibleChunks = new List<ChunkViewMsg>();
        public List<EntityViewMsg> visibleEntities = new List<EntityViewMsg>();
        public List<ItemViewMsg> visibleItems = new List<ItemViewMsg>();
        // Optional on the wire (omitted when empty) — stays an empty list then.
        public List<VerticalDebugMarkerMsg> verticalDebugMarkers = new List<VerticalDebugMarkerMsg>();
        // Phase 1 — host-authoritative STP world items (omitted when empty).
        public List<StpItemMsg> stpItems = new List<StpItemMsg>();
        // Phase B1 — host-authoritative STP building pieces (omitted when empty).
        public List<StpBuildingMsg> stpBuildings = new List<StpBuildingMsg>();
        // Phase B2.5 — host-authoritative STP world carryables (omitted when empty).
        public List<StpCarryableMsg> stpCarryables = new List<StpCarryableMsg>();
        // Phase B2.6 — host-authoritative STP scene harvestables / health (omitted when empty).
        public List<StpHarvestableMsg> stpHarvestables = new List<StpHarvestableMsg>();
        // ADR-028 — lootable corpses near the player (omitted when empty; v7 backend → empty).
        public List<CorpseViewMsg> visibleCorpses = new List<CorpseViewMsg>();

        /// <summary>
        /// Root-tagged message (ServerMessage::WorldState, "type":"world_state") — reads the
        /// REMAINING <paramref name="remainingPairs"/> pairs after IPCClient.Dispatch already
        /// consumed the map header and the "type" pair. The single highest-volume decode in the
        /// whole client: N chunks × up to 320 layout_cells each, at 10 Hz — this is the path the
        /// boxing removal in docs/STATE.md targets.
        /// </summary>
        public static WorldStateMsg Parse(MsgPackReader r, int remainingPairs)
        {
            var ws = new WorldStateMsg();
            for (int i = 0; i < remainingPairs; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "tick")) ws.tick = r.ReadInt();
                else if (MsgPackReader.Is(k, "world_seed")) ws.worldSeed = r.ReadInt();
                else if (MsgPackReader.Is(k, "world_revision")) ws.worldRevision = r.ReadInt();
                else if (MsgPackReader.Is(k, "local_player")) ws.localPlayer = LocalPlayerMsg.Parse(r);
                else if (MsgPackReader.Is(k, "remote_players")) ReadList(r, ws.remotePlayers, RemotePlayerMsg.Parse);
                else if (MsgPackReader.Is(k, "visible_chunks")) ReadList(r, ws.visibleChunks, ChunkViewMsg.Parse);
                else if (MsgPackReader.Is(k, "visible_entities")) ReadList(r, ws.visibleEntities, EntityViewMsg.Parse);
                else if (MsgPackReader.Is(k, "visible_items")) ReadList(r, ws.visibleItems, ItemViewMsg.Parse);
                else if (MsgPackReader.Is(k, "vertical_debug_markers")) ReadList(r, ws.verticalDebugMarkers, VerticalDebugMarkerMsg.Parse);
                else if (MsgPackReader.Is(k, "stp_items")) ReadList(r, ws.stpItems, StpItemMsg.Parse);
                else if (MsgPackReader.Is(k, "stp_buildings")) ReadList(r, ws.stpBuildings, StpBuildingMsg.Parse);
                else if (MsgPackReader.Is(k, "stp_carryables")) ReadList(r, ws.stpCarryables, StpCarryableMsg.Parse);
                else if (MsgPackReader.Is(k, "stp_harvestables")) ReadList(r, ws.stpHarvestables, StpHarvestableMsg.Parse);
                else if (MsgPackReader.Is(k, "visible_corpses")) ReadList(r, ws.visibleCorpses, CorpseViewMsg.Parse);
                else r.Skip();
            }
            return ws;
        }

        /// <summary>Shared array→List helper: sets Capacity once (same reasoning as the
        /// legacy path's comment above) then reads exactly that many elements via
        /// <paramref name="parseOne"/>. A nil array (key present, value nil) reads as empty,
        /// matching <c>IPCParse.Get(d, key) is object[]</c> failing for a null value.</summary>
        private static void ReadList<T>(MsgPackReader r, List<T> dest, Func<MsgPackReader, T> parseOne)
        {
            int n = r.ReadArrayHeader();
            if (n <= 0) return;
            dest.Capacity = n;
            for (int i = 0; i < n; i++) dest.Add(parseOne(r));
        }
    }

    public class GameEventMsg
    {
        public string eventType = "";
        public object data;

        /// <summary>
        /// Root-tagged message (ServerMessage::Event, "type":"event") — reads the REMAINING
        /// <paramref name="remainingPairs"/> pairs. "data" is free-form (serde_json::Value on the
        /// wire) and every consumer (PvpFeedbackController, StpPickupController, CorpseLootSync,
        /// ...) expects the generic object tree via IPCParse.L/F/S/Vec3 — so unlike every other
        /// field on the hot path, this one deliberately still materializes via
        /// <see cref="MsgPackReader.ReadValue"/>. Events are discrete/low-frequency; there is no
        /// boxing pressure to remove here.
        /// </summary>
        public static GameEventMsg Parse(MsgPackReader r, int remainingPairs)
        {
            var e = new GameEventMsg();
            for (int i = 0; i < remainingPairs; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "event_type")) e.eventType = r.ReadString();
                else if (MsgPackReader.Is(k, "data")) e.data = r.ReadValue();
                else r.Skip();
            }
            return e;
        }
    }

    /// <summary>
    /// Accessors over the generic <c>Dictionary&lt;string,object&gt;</c> tree that
    /// <see cref="MsgPackReader.ReadValue"/> produces.
    ///
    /// NOT DEAD CODE, despite appearances. Every message type moved to the streaming
    /// <c>Parse(MsgPackReader)</c> path and the old <c>Parse(object)</c> overloads are gone, so
    /// nothing in THIS file calls these any more — but <see cref="GameEventMsg.data"/> is still
    /// a free-form object tree (<c>serde_json::Value</c> on the wire), and its consumers read it
    /// exclusively through here: PvpFeedbackController, StpPickupController,
    /// StpCarryablePickupController, CorpseLootSync, AuthoritativePoseApplier,
    /// PhantomAttackHandler, InventoryRestorer.
    ///
    /// Events are discrete and low-frequency, so the boxing this path implies is not worth
    /// removing — that was the explicit trade in the decoder sweep, not an oversight.
    /// </summary>
    public static class IPCParse
    {
        public static object Get(Dictionary<string, object> d, string key)
            => (d != null && d.TryGetValue(key, out var v)) ? v : null;

        public static float ToFloat(object v)
        {
            if (v is double dd) return (float)dd;
            if (v is long ll) return ll;
            return 0f;
        }

        public static long ToLong(object v)
        {
            if (v is long ll) return ll;
            if (v is double dd) return (long)dd;
            return 0L;
        }

        public static float F(Dictionary<string, object> d, string key) => ToFloat(Get(d, key));
        public static long L(Dictionary<string, object> d, string key) => ToLong(Get(d, key));
        public static bool B(Dictionary<string, object> d, string key) => Get(d, key) is bool b && b;
        public static string S(Dictionary<string, object> d, string key) => Get(d, key) as string ?? "";

        public static Vector3 Vec3(object v)
        {
            if (v is object[] a && a.Length >= 3)
                return new Vector3(ToFloat(a[0]), ToFloat(a[1]), ToFloat(a[2]));
            return Vector3.zero;
        }

        public static int[] IntArray2(object v)
        {
            if (v is object[] a && a.Length >= 2)
                return new[] { (int)ToLong(a[0]), (int)ToLong(a[1]) };
            return new[] { 0, 0 };
        }

        /// Fills <paramref name="dest"/> from a msgpack array, zeroing any slot the wire did not
        /// carry — the same normalization IntArray + a copy loop performed, minus the throwaway
        /// int[] that allocated once per remote player and once per corpse, every snapshot.
        public static void FillIntArray(object v, int[] dest)
        {
            var a = v as object[];
            int n = a?.Length ?? 0;
            for (int i = 0; i < dest.Length; i++)
                dest[i] = i < n ? (int)ToLong(a[i]) : 0;
        }

        public static int[] IntArray(object v)
        {
            if (v is object[] a)
            {
                var values = new int[a.Length];
                for (int i = 0; i < a.Length; i++)
                    values[i] = (int)ToLong(a[i]);
                return values;
            }
            return new int[0];
        }

        public static string[] StringArray(object v)
        {
            if (v is object[] a)
            {
                var values = new string[a.Length];
                for (int i = 0; i < a.Length; i++)
                    values[i] = a[i] as string ?? "";
                return values;
            }
            return new string[0];
        }

        public static byte[] ByteArray(object v)
        {
            if (v is object[] a)
            {
                var values = new byte[a.Length];
                for (int i = 0; i < a.Length; i++)
                {
                    long value = ToLong(a[i]);
                    if (value < 0) value = 0;
                    if (value > byte.MaxValue) value = byte.MaxValue;
                    values[i] = (byte)value;
                }
                return values;
            }
            return new byte[0];
        }

        public static ushort[] UShortArray(object v)
        {
            if (v is object[] a)
            {
                var values = new ushort[a.Length];
                for (int i = 0; i < a.Length; i++)
                {
                    long value = ToLong(a[i]);
                    if (value < 0) value = 0;
                    if (value > ushort.MaxValue) value = ushort.MaxValue;
                    values[i] = (ushort)value;
                }
                return values;
            }
            return new ushort[0];
        }
    }

    /// <summary>
    /// Mirror of backend edge-kind values (backend/src/world/chunk.rs, Phase 2.7
    /// edge-wall model). Architecture lives on cell edges, not cell centres.
    /// Helper predicates match the backend's edge_blocks_movement / edge_is_full_wall.
    /// </summary>
    public static class EdgeKinds
    {
        public const byte Open = 0;
        public const byte Wall = 1;
        public const byte Door = 2;
        public const byte Arch = 3;
        public const byte LowWall = 4;
        public const byte HalfWall = 5;
        public const byte Partition = 6;
        public const byte FalseDoor = 7;
        public const byte BrokenWall = 8; // backend EDGE_KIND_BROKEN

        public static bool EdgeIsOpen(byte k) => k == Open;

        // Matches backend edge_is_full_wall.
        public static bool EdgeIsFullWall(byte k) => k == Wall || k == Partition || k == FalseDoor;

        // Matches backend edge_blocks_movement.
        public static bool EdgeBlocksMovement(byte k) =>
            k == Wall || k == LowWall || k == HalfWall || k == Partition || k == FalseDoor;

        public static bool EdgeIsDoor(byte k) => k == Door;
        public static bool EdgeIsArch(byte k) => k == Arch;
        public static bool EdgeIsLowWall(byte k) => k == LowWall;
        public static bool EdgeIsHalfWall(byte k) => k == HalfWall;
        public static bool EdgeIsPartition(byte k) => k == Partition;
        public static bool EdgeIsFalseDoor(byte k) => k == FalseDoor;
        public static bool EdgeIsBrokenWall(byte k) => k == BrokenWall;
    }
}
