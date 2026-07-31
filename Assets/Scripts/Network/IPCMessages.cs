using System;
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    public class StatsMsg
    {
        public float health, hunger, thirst, sanity;
        // ADR-009: server-authoritative stamina, interpolated client-side at 5 Hz.
        public float stamina;

        /// <summary>Reads its own map header — zero intermediate Dictionary/box per field.</summary>
        public static StatsMsg Parse(MsgPackReader r)
        {
            var s = new StatsMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "health")) s.health = r.ReadFloat();
                else if (MsgPackReader.Is(k, "hunger")) s.hunger = r.ReadFloat();
                else if (MsgPackReader.Is(k, "thirst")) s.thirst = r.ReadFloat();
                else if (MsgPackReader.Is(k, "sanity")) s.sanity = r.ReadFloat();
                else if (MsgPackReader.Is(k, "stamina")) s.stamina = r.ReadFloat();
                else r.Skip();
            }
            return s;
        }
    }

    public class LocalPlayerMsg
    {
        public Vector3 position;
        public float rotation;
        public StatsMsg stats = new StatsMsg();
        public float speedModifier = 1f;
        public bool inventoryChanged;
        // ADR-009: echo of the last client input_seq the server has applied.
        public uint ackInputSeq;

        public static LocalPlayerMsg Parse(MsgPackReader r)
        {
            var p = new LocalPlayerMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "position")) p.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) p.rotation = r.ReadFloat();
                else if (MsgPackReader.Is(k, "stats")) p.stats = StatsMsg.Parse(r);
                else if (MsgPackReader.Is(k, "speed_modifier")) p.speedModifier = r.ReadFloat();
                else if (MsgPackReader.Is(k, "inventory_changed")) p.inventoryChanged = r.ReadBool();
                else if (MsgPackReader.Is(k, "ack_input_seq")) p.ackInputSeq = (uint)r.ReadInt();
                else r.Skip();
            }
            return p;
        }
    }

    /// <summary>
    /// ADR-009 §2 DeltaUpdate: the 20 Hz authoritative movement delta consumed by
    /// the MovementReconciler — pose to detect desync, velocity to snap to, and
    /// ackInputSeq to align with the client's input ring buffer.
    /// </summary>
    public class MovementDeltaMsg
    {
        public uint tick;
        public uint ackInputSeq;
        public Vector3 position;
        public Vector3 velocity;

        /// <summary>
        /// Root-tagged message (ServerMessage::DeltaUpdate, "type":"delta_update") —
        /// IPCClient.Dispatch already consumed the map header AND the "type" pair, so this reads
        /// only the REMAINING <paramref name="remainingPairs"/> key/value pairs, not a header of
        /// its own.
        /// </summary>
        public static MovementDeltaMsg Parse(MsgPackReader r, int remainingPairs)
        {
            var m = new MovementDeltaMsg();
            for (int i = 0; i < remainingPairs; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "tick")) m.tick = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "ack_input_seq")) m.ackInputSeq = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "velocity")) m.velocity = r.ReadVec3();
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>
    /// ADR-034 — tipo de zona estampada por la Fase 4 de grid_gen.
    ///
    /// Los valores son un CONTRATO con el enum `RoomType` del backend
    /// (backend/src/world/grid_gen/generator.rs, ver `RoomType::wire_kind`).
    /// Cambiar uno exige cambiar el Rust en el mismo commit.
    /// </summary>
    public enum RoomZoneKind : byte
    {
        Open = 0,
        SealedRoom = 1,
        CorridorSpine = 2,
    }

    /// <summary>
    /// ADR-034 — un rect estampado en la Fase 4, en coordenadas de CELDA (2.5 m)
    /// locales al chunk, con x1/z1 EXCLUSIVOS. Mirror de `grid_gen::RoomZone`.
    ///
    /// Ojo con la escala: el resto del render cliente trabaja en tiles de 5 m
    /// (`GridChunkDataMsg.Tiles` = 10 por lado); estas coordenadas son de celda
    /// (20 por lado). Un tile `tx` cubre las celdas `2*tx` y `2*tx + 1`.
    /// </summary>
    public struct RoomZoneMsg
    {
        public byte x0, z0, x1, z1;

        /// <summary>Discriminante crudo del wire; ver <see cref="Kind"/>.</summary>
        public byte kindByte;

        /// <summary>
        /// Accesor con tipo. Un valor desconocido (backend más nuevo que este
        /// cliente) colapsa a <see cref="RoomZoneKind.Open"/> — el tipo sin
        /// perímetro sellado, o sea el comportamiento histórico previo a
        /// RoomType. Mismo criterio de degradación que <c>GridCell.Kind</c>.
        /// </summary>
        public RoomZoneKind Kind =>
            kindByte <= (byte)RoomZoneKind.CorridorSpine
                ? (RoomZoneKind)kindByte
                : RoomZoneKind.Open;

        /// <summary>True si la celda (cx, cz) del chunk cae dentro del rect.</summary>
        public bool ContainsCell(int cellX, int cellZ) =>
            cellX >= x0 && cellX < x1 && cellZ >= z0 && cellZ < z1;

        public static RoomZoneMsg Parse(MsgPackReader r)
        {
            var z = new RoomZoneMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "x0")) z.x0 = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "z0")) z.z0 = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "x1")) z.x1 = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "z1")) z.z1 = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "kind")) z.kindByte = (byte)r.ReadInt();
                else r.Skip();
            }
            return z;
        }
    }

    /// <summary>
    /// Fase 4.1 — backend grid_gen chunk reply (ServerMessage::ChunkData, tag
    /// "chunk_data"). A 10×10 grid of 5 m tiles, each an edge-wall bitmask in the
    /// BACKEND convention: N=1 (−Z), S=2 (+Z), E=4 (+X), W=8 (−X). walls[x,z].
    /// </summary>
    public class GridChunkDataMsg
    {
        public const int Tiles = 10;
        public const byte WallN = 1; // −Z
        public const byte WallS = 2; // +Z
        public const byte WallE = 4; // +X
        public const byte WallW = 8; // −X

        /// <summary>Instancia compartida para "sin zonas" — evita alocar por chunk.</summary>
        private static readonly RoomZoneMsg[] NoRoomZones = new RoomZoneMsg[0];

        public int cx;
        public int cz;
        public byte layer;
        public byte[,] walls = new byte[Tiles, Tiles];

        /// <summary>
        /// ADR-034 — rects de Fase 4 con su tipo de sala. NUNCA null: un backend
        /// que no manda la clave (versión anterior al ADR, o chunk sin zonas)
        /// deja el array VACÍO, así que el consumidor solo tiene que tratar el
        /// caso "ninguna zona cubre este tile", no un caso de nulo aparte.
        /// </summary>
        public RoomZoneMsg[] roomZones = NoRoomZones;

        /// <summary>
        /// Root-tagged message (ServerMessage::ChunkData, "type":"chunk_data") — reads the
        /// REMAINING <paramref name="remainingPairs"/> pairs after IPCClient.Dispatch already
        /// consumed the map header and the "type" pair.
        /// </summary>
        public static GridChunkDataMsg Parse(MsgPackReader r, int remainingPairs)
        {
            var m = new GridChunkDataMsg();
            for (int i = 0; i < remainingPairs; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "cx")) m.cx = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "cz")) m.cz = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "layer")) m.layer = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "walls"))
                {
                    // Backend [[u8;10];10]. Consume every row/col the wire actually sent (keeps
                    // the cursor in sync for the fields after this one) but only STORE within the
                    // 10×10 contract — same clamp as the old Mathf.Min(rows.Length, Tiles).
                    int rows = r.ReadArrayHeader();
                    if (rows < 0) rows = 0;
                    for (int x = 0; x < rows; x++)
                    {
                        int cols = r.ReadArrayHeader();
                        if (cols < 0) cols = 0;
                        for (int z = 0; z < cols; z++)
                        {
                            byte v = (byte)r.ReadInt();
                            if (x < Tiles && z < Tiles) m.walls[x, z] = v;
                        }
                    }
                }
                else if (MsgPackReader.Is(k, "room_zones"))
                {
                    // ADR-034: additive key, absent entirely on a chunk with no zones (or a
                    // pre-ADR backend) ⇒ m.roomZones stays the shared NoRoomZones default.
                    int zc = r.ReadArrayHeader();
                    if (zc > 0)
                    {
                        m.roomZones = new RoomZoneMsg[zc];
                        for (int zi = 0; zi < zc; zi++)
                            m.roomZones[zi] = RoomZoneMsg.Parse(r);
                    }
                }
                else r.Skip();
            }
            return m;
        }
    }

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
                else if (MsgPackReader.Is(k, "animation")) r.animation = reader.ReadString();
                else if (MsgPackReader.Is(k, "crouch")) r.crouch = reader.ReadBool();
                else if (MsgPackReader.Is(k, "pitch")) r.pitch = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "equipment")) reader.ReadIntArrayInto(r.equipment);
                else if (MsgPackReader.Is(k, "held_item")) r.heldItem = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "hit_seq")) r.hitSeq = (int)reader.ReadInt();
                else if (MsgPackReader.Is(k, "dead")) r.dead = reader.ReadBool();
                else reader.Skip();
            }
            return r;
        }
    }

    public class InterLayerVolumeMsg
    {
        public uint volumeId;
        public string kind = "";
        public int[] baseChunk = new int[2];
        public int[] involvedLayers = new int[0];
        public int[] footprintCellMin = new int[2];
        public int[] footprintCellMax = new int[2];
        public string safetyType = "";
        public string futureAudioHint = "";
        public int visualFlags;
        public string[] visualHints = new string[0];

        public static InterLayerVolumeMsg Parse(MsgPackReader r)
        {
            var v = new InterLayerVolumeMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "volume_id")) v.volumeId = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "kind")) v.kind = r.ReadString();
                else if (MsgPackReader.Is(k, "base_chunk")) v.baseChunk = r.ReadIntArray2();
                else if (MsgPackReader.Is(k, "involved_layers")) v.involvedLayers = r.ReadIntArray();
                else if (MsgPackReader.Is(k, "footprint_cell_min")) v.footprintCellMin = r.ReadIntArray2();
                else if (MsgPackReader.Is(k, "footprint_cell_max")) v.footprintCellMax = r.ReadIntArray2();
                else if (MsgPackReader.Is(k, "safety_type")) v.safetyType = r.ReadString();
                else if (MsgPackReader.Is(k, "future_audio_hint")) v.futureAudioHint = r.ReadString();
                else if (MsgPackReader.Is(k, "visual_flags")) v.visualFlags = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "visual_hints")) v.visualHints = r.ReadStringArray();
                else r.Skip();
            }
            return v;
        }
    }

    /// <summary>
    /// One renderable face of the backend-authored volumetric grid.
    /// dir: 0=N(+Z) 1=S(-Z) 2=E(+X) 3=W(-X) 4=Up(+Y) 5=Down(-Y).
    /// kind: 0=Wall 1=ShaftWall 2=Floor 3=Ceiling 4=Railing 5=SupportColumn.
    /// </summary>
    public class VolumetricFaceMsg
    {
        public int x, y, z;
        public byte dir;
        public byte kind;

        public static VolumetricFaceMsg Parse(MsgPackReader r)
        {
            var f = new VolumetricFaceMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "cell"))
                {
                    var cell = r.ReadIntArray();
                    if (cell.Length >= 3) { f.x = cell[0]; f.y = cell[1]; f.z = cell[2]; }
                }
                else if (MsgPackReader.Is(k, "dir")) f.dir = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "kind")) f.kind = (byte)r.ReadInt();
                else r.Skip();
            }
            return f;
        }
    }

    public class LayerBandMsg
    {
        public uint bandId;
        public int layer;
        public string profile = "";
        public int profileCode;
        public bool accessible;
        public string dangerProfile = "";
        public string resourceProfile = "";
        public string anomalyProfile = "";

        public static LayerBandMsg Parse(MsgPackReader r)
        {
            var b = new LayerBandMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "band_id")) b.bandId = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "layer")) b.layer = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "profile")) b.profile = r.ReadString();
                else if (MsgPackReader.Is(k, "profile_code")) b.profileCode = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "accessible")) b.accessible = r.ReadBool();
                else if (MsgPackReader.Is(k, "danger_profile")) b.dangerProfile = r.ReadString();
                else if (MsgPackReader.Is(k, "resource_profile")) b.resourceProfile = r.ReadString();
                else if (MsgPackReader.Is(k, "anomaly_profile")) b.anomalyProfile = r.ReadString();
                else r.Skip();
            }
            return b;
        }
    }

    public class VerticalAccessNodeMsg
    {
        public uint accessId;
        public string accessType = "";
        public int accessTypeCode;
        public int fromLayer;
        public int toLayer;
        public int[] footprintCellMin = new int[2];
        public int[] footprintCellMax = new int[2];
        public bool explicitAccess;

        public static VerticalAccessNodeMsg Parse(MsgPackReader r)
        {
            var n = new VerticalAccessNodeMsg();
            int fc = r.ReadMapHeader();
            for (int i = 0; i < fc; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "access_id")) n.accessId = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "access_type")) n.accessType = r.ReadString();
                else if (MsgPackReader.Is(k, "access_type_code")) n.accessTypeCode = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "from_layer")) n.fromLayer = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "to_layer")) n.toLayer = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "footprint_cell_min")) n.footprintCellMin = r.ReadIntArray2();
                else if (MsgPackReader.Is(k, "footprint_cell_max")) n.footprintCellMax = r.ReadIntArray2();
                else if (MsgPackReader.Is(k, "explicit")) n.explicitAccess = r.ReadBool();
                else r.Skip();
            }
            return n;
        }
    }

    public class BandHeightSpecMsg
    {
        public int bandIndex;
        public int layer;
        public float roomHeight;
        public float totalHeight;
        public float neighborMaxRoomHeight;

        public static BandHeightSpecMsg Parse(MsgPackReader r)
        {
            var b = new BandHeightSpecMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "band_index")) b.bandIndex = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "layer")) b.layer = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "room_height")) b.roomHeight = r.ReadFloat();
                else if (MsgPackReader.Is(k, "total_height")) b.totalHeight = r.ReadFloat();
                else if (MsgPackReader.Is(k, "neighbor_max_room_height")) b.neighborMaxRoomHeight = r.ReadFloat();
                else r.Skip();
            }
            return b;
        }
    }

    /// <summary>
    /// Backend-authored volumetric "Rubik grid" architecture (Volumetric V0),
    /// attached render-only to the near-spawn host chunk. The renderer derives
    /// floors/ceilings/walls/railings/shaft-walls/support columns from faces;
    /// occupancy codes mirror backend CellOccupancy.
    /// </summary>
    public class VolumetricGridMsg
    {
        public const byte OccSolid = 0;
        public const byte OccRoom = 1;
        public const byte OccCorridor = 2;
        public const byte OccAtriumVoid = 3;
        public const byte OccShaft = 4;
        public const byte OccServiceSpace = 5;
        public const byte OccSupportCore = 6;
        public const byte OccBlocked = 7;
        public const byte OccSealedRoom = 8;
        public const byte OccFalseSpace = 9;
        public const byte OccCeilingVoid = 10;
        public const byte OccUnderfloorService = 11;
        public const byte OccTransition = 12;
        public const byte OccAnomaly = 13;
        public const byte OccDangerZone = 14;
        public const byte OccSafeNode = 15;

        public const byte DirNorth = 0, DirSouth = 1, DirEast = 2, DirWest = 3, DirUp = 4, DirDown = 5;
        public const byte FaceWall = 0, FaceShaftWall = 1, FaceFloor = 2, FaceCeiling = 3, FaceRailing = 4, FaceSupportColumn = 5, FaceRim = 6;

        public bool active;
        public ulong columnId;
        public int[] columnCoord = new int[2];
        public string source = "";
        public int nx, ny, nz;
        public float cellSizeXZ = 5f;
        public float layerHeight = 7f;
        public Vector3 originWorld;
        public int baseLayer;
        public byte[] cells = new byte[0];
        public List<VolumetricFaceMsg> faces = new List<VolumetricFaceMsg>();
        public int openCellCount;
        public int solidCellCount;
        public int verticalConnectionCount;
        public int validVerticalOpeningCount;
        public bool atriumSpan;
        public List<LayerBandMsg> layerBands = new List<LayerBandMsg>();
        public List<VerticalAccessNodeMsg> verticalAccess = new List<VerticalAccessNodeMsg>();
        public List<BandHeightSpecMsg> heightBands = new List<BandHeightSpecMsg>();

        /// <summary>
        /// Unlike every other nested Msg type, this one reads its OWN nil-check (a nil value
        /// here means "no volumetric grid on this chunk" ⇒ returns null) — so ChunkViewMsg.Parse
        /// can call this unconditionally on the "volumetric_grid" key without a separate presence
        /// check first.
        /// </summary>
        public static VolumetricGridMsg Parse(MsgPackReader r)
        {
            int n = r.ReadMapHeader();
            if (n < 0) return null;

            var g = new VolumetricGridMsg();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "active")) g.active = r.ReadBool();
                else if (MsgPackReader.Is(k, "column_id")) g.columnId = (ulong)r.ReadInt();
                else if (MsgPackReader.Is(k, "column_coord")) g.columnCoord = r.ReadIntArray2();
                else if (MsgPackReader.Is(k, "source")) g.source = r.ReadString();
                else if (MsgPackReader.Is(k, "dims"))
                {
                    var dims = r.ReadIntArray();
                    if (dims.Length >= 3) { g.nx = dims[0]; g.ny = dims[1]; g.nz = dims[2]; }
                }
                else if (MsgPackReader.Is(k, "cell_size_xz")) g.cellSizeXZ = r.ReadFloat();
                else if (MsgPackReader.Is(k, "layer_height")) g.layerHeight = r.ReadFloat();
                else if (MsgPackReader.Is(k, "origin_world")) g.originWorld = r.ReadVec3();
                else if (MsgPackReader.Is(k, "base_layer")) g.baseLayer = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "cells")) g.cells = r.ReadByteArrayValues();
                else if (MsgPackReader.Is(k, "faces"))
                {
                    int fc = r.ReadArrayHeader();
                    if (fc > 0)
                    {
                        g.faces.Capacity = fc;
                        for (int fi = 0; fi < fc; fi++) g.faces.Add(VolumetricFaceMsg.Parse(r));
                    }
                }
                else if (MsgPackReader.Is(k, "open_cell_count")) g.openCellCount = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "solid_cell_count")) g.solidCellCount = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "vertical_connection_count")) g.verticalConnectionCount = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "valid_vertical_opening_count")) g.validVerticalOpeningCount = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "atrium_span")) g.atriumSpan = r.ReadBool();
                else if (MsgPackReader.Is(k, "layer_bands"))
                {
                    int bc = r.ReadArrayHeader();
                    if (bc > 0)
                    {
                        g.layerBands.Capacity = bc;
                        for (int bi = 0; bi < bc; bi++) g.layerBands.Add(LayerBandMsg.Parse(r));
                    }
                }
                else if (MsgPackReader.Is(k, "vertical_access"))
                {
                    int ac = r.ReadArrayHeader();
                    if (ac > 0)
                    {
                        g.verticalAccess.Capacity = ac;
                        for (int ai = 0; ai < ac; ai++) g.verticalAccess.Add(VerticalAccessNodeMsg.Parse(r));
                    }
                }
                else if (MsgPackReader.Is(k, "height_bands"))
                {
                    int hc = r.ReadArrayHeader();
                    if (hc > 0)
                    {
                        g.heightBands.Capacity = hc;
                        for (int hi = 0; hi < hc; hi++) g.heightBands.Add(BandHeightSpecMsg.Parse(r));
                    }
                }
                else r.Skip();
            }
            // Same post-fixups as Parse(object) — a zero/negative wire value falls back to the
            // documented default rather than propagating a degenerate scale into the renderer.
            if (g.cellSizeXZ <= 0f) g.cellSizeXZ = 5f;
            if (g.layerHeight <= 0f) g.layerHeight = 7f;
            return g;
        }

        public byte CellAt(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz) return OccSolid;
            int idx = (y * nz + z) * nx + x;
            return (idx >= 0 && idx < cells.Length) ? cells[idx] : OccSolid;
        }
    }

    public class ChunkViewMsg
    {
        public int chunkSchema = 1;
        public int[] pos = new int[2];
        public int layer;
        public float layerY;
        public int templateId;
        public int rotation;
        public bool mirrored;
        public string state = "random";
        public bool hasWorkbench;
        public int layoutGridSize = 10;
        public float layoutCellSize = 5f;
        public ushort[] layoutCells = new ushort[0];
        public int edgeOpenings;
        public uint macroId;
        public int zoneKind;
        public int[] macroLocal = new int[2];
        public int[] macroSize = new int[] { 1, 1 };
        public int floorLevel;
        public int floorProfile;
        public int ceilingProfile;
        public int lightProfile;
        public int anomalyFlags;
        public int verticalFlags;
        public List<InterLayerVolumeMsg> interLayerVolumes = new List<InterLayerVolumeMsg>();

        // Volumetric "Rubik grid" V0 — present only on the near-spawn host chunk.
        public VolumetricGridMsg volumetricGrid;
        public bool HasVolumetricGrid => volumetricGrid != null && volumetricGrid.active;

        // Phase 2.7B — split views of the packed layout_cells array.
        // Packing (gridSize g): [cells (g*g)] [edges_v ((g+1)*g)] [edges_h (g*(g+1))].
        public ushort[] cellFlags = new ushort[0];
        public byte[] verticalEdges = new byte[0];
        public byte[] horizontalEdges = new byte[0];
        public bool hasBackendLayout;
        public bool hasEdgeLayout;

        /// <summary>The full packed array exactly as received from the backend.</summary>
        public ushort[] LayoutCellsRaw => layoutCells;
        public bool HasEdgeLayout => hasEdgeLayout;
        public bool HasBackendLayout => hasBackendLayout;

        private static readonly HashSet<int> _loggedInvalidLayouts = new HashSet<int>();
        // Tuple key, not an interpolated string: only the Debug.Log below is deduplicated, so the
        // key itself was still being built for every volume-carrying chunk of every snapshot,
        // long after the log had gone quiet.
        private static readonly HashSet<(int, int, int, int)> _loggedVolumeParseChunks
            = new HashSet<(int, int, int, int)>();

        /// <summary>Same post-fixups, same SplitPackedLayout() call, same MPTRACE log-once as
        /// before the boxing removal — only the source of the fields changed (token walk instead
        /// of a materialized Dictionary).</summary>
        public static ChunkViewMsg Parse(MsgPackReader r)
        {
            var c = new ChunkViewMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "chunk_schema")) c.chunkSchema = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "pos")) c.pos = r.ReadIntArray2();
                else if (MsgPackReader.Is(k, "layer")) c.layer = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "layer_y")) c.layerY = r.ReadFloat();
                else if (MsgPackReader.Is(k, "template_id")) c.templateId = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "rotation")) c.rotation = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "mirrored")) c.mirrored = r.ReadBool();
                else if (MsgPackReader.Is(k, "state")) c.state = r.ReadString();
                else if (MsgPackReader.Is(k, "has_workbench")) c.hasWorkbench = r.ReadBool();
                else if (MsgPackReader.Is(k, "layout_grid_size")) c.layoutGridSize = Mathf.Max(1, (int)r.ReadInt());
                else if (MsgPackReader.Is(k, "layout_cell_size")) c.layoutCellSize = r.ReadFloat();
                else if (MsgPackReader.Is(k, "layout_cells")) c.layoutCells = r.ReadUShortArrayValues();
                else if (MsgPackReader.Is(k, "edge_openings")) c.edgeOpenings = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "macro_id")) c.macroId = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "zone_kind")) c.zoneKind = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "macro_local")) c.macroLocal = r.ReadIntArray2();
                else if (MsgPackReader.Is(k, "macro_size")) c.macroSize = r.ReadIntArray2();
                else if (MsgPackReader.Is(k, "floor_level")) c.floorLevel = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "floor_profile")) c.floorProfile = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "ceiling_profile")) c.ceilingProfile = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "light_profile")) c.lightProfile = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "anomaly_flags")) c.anomalyFlags = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "vertical_flags")) c.verticalFlags = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "inter_layer_volumes"))
                {
                    int vc = r.ReadArrayHeader();
                    if (vc > 0)
                    {
                        c.interLayerVolumes.Capacity = vc;
                        for (int vi = 0; vi < vc; vi++) c.interLayerVolumes.Add(InterLayerVolumeMsg.Parse(r));
                    }
                }
                else if (MsgPackReader.Is(k, "volumetric_grid")) c.volumetricGrid = VolumetricGridMsg.Parse(r);
                else r.Skip();
            }
            // Same post-fixups as Parse(object).
            if (c.chunkSchema <= 0) c.chunkSchema = 1;
            if (c.layoutCellSize <= 0f) c.layoutCellSize = 5f;
            if (c.macroSize[0] <= 0) c.macroSize[0] = 1;
            if (c.macroSize[1] <= 0) c.macroSize[1] = 1;
            if (c.interLayerVolumes.Count > 0)
                LogVolumeParseOnce(c);
            c.SplitPackedLayout();
            return c;
        }

        private static void LogVolumeParseOnce(ChunkViewMsg c)
        {
            if (!_loggedVolumeParseChunks.Add((c.pos[0], c.layer, c.pos[1], c.interLayerVolumes.Count)))
                return;

            Debug.Log($"MPTRACE step=V30A2 event=v30a2_visfix_unity_volume_count_received chunk=({c.pos[0]},{c.layer},{c.pos[1]}) volume_count={c.interLayerVolumes.Count}");
            Debug.Log($"MPTRACE step=V30A2 event=v30a2_visfix_unity_layer_y_received chunk=({c.pos[0]},{c.layer},{c.pos[1]}) layer_y={c.layerY:F2} chunk_schema={c.chunkSchema}");
            for (int i = 0; i < c.interLayerVolumes.Count; i++)
            {
                var volume = c.interLayerVolumes[i];
                Debug.Log($"MPTRACE step=V30A2 event=v30a2_visfix_unity_volume_kind_received chunk=({c.pos[0]},{c.layer},{c.pos[1]}) index={i} volume_id={volume.volumeId} kind={volume.kind} flags={volume.visualFlags}");
            }
        }

        /// <summary>
        /// Split the packed layout_cells array into cell flags + edge arrays.
        /// Packing (gridSize g): [cells g*g][edges_v (g+1)*g][edges_h g*(g+1)].
        /// Falls back gracefully for cells-only (old) or invalid layouts.
        /// </summary>
        public void SplitPackedLayout()
        {
            hasBackendLayout = false;
            hasEdgeLayout = false;
            cellFlags = new ushort[0];
            verticalEdges = new byte[0];
            horizontalEdges = new byte[0];

            int g = layoutGridSize;
            if (g <= 0 || layoutCells == null)
                return;

            int cellCount = g * g;
            int vEdgeCount = (g + 1) * g;
            int hEdgeCount = g * (g + 1);
            int expected = cellCount + vEdgeCount + hEdgeCount;
            int len = layoutCells.Length;

            if (len >= expected)
            {
                hasBackendLayout = true;
                hasEdgeLayout = true;
                cellFlags = new ushort[cellCount];
                verticalEdges = new byte[vEdgeCount];
                horizontalEdges = new byte[hEdgeCount];
                for (int i = 0; i < cellCount; i++)
                    cellFlags[i] = layoutCells[i];
                for (int i = 0; i < vEdgeCount; i++)
                    verticalEdges[i] = (byte)(layoutCells[cellCount + i] & 0xFF);
                for (int i = 0; i < hEdgeCount; i++)
                    horizontalEdges[i] = (byte)(layoutCells[cellCount + vEdgeCount + i] & 0xFF);
            }
            else if (len >= cellCount)
            {
                // Old layout without an edge tail — cell flags only.
                hasBackendLayout = true;
                hasEdgeLayout = false;
                cellFlags = new ushort[cellCount];
                for (int i = 0; i < cellCount; i++)
                    cellFlags[i] = layoutCells[i];
            }
            else
            {
                // Missing/short → renderer uses the template fallback path.
                if (len > 0)
                    LogInvalidLayoutOnce(g, len);
            }
        }

        private static void LogInvalidLayoutOnce(int grid, int length)
        {
            int key = (grid << 20) ^ length;
            if (_loggedInvalidLayouts.Add(key))
                Debug.LogWarning($"MPTRACE step=V27 event=unity_layout_parse_fallback grid={grid} packed_len={length} reason=too_short");
        }

        /// <summary>Cell flags at (x, z). x,z in 0..gridSize-1.</summary>
        public ushort GetCell(int x, int z)
        {
            if (!hasBackendLayout)
                return 0;
            int g = layoutGridSize;
            if (x < 0 || x >= g || z < 0 || z >= g)
                return 0;
            int idx = z * g + x;
            return (idx >= 0 && idx < cellFlags.Length) ? cellFlags[idx] : (ushort)0;
        }

        /// <summary>Vertical edge kind between cells (x-1,z) and (x,z). x:0..g, z:0..g-1.</summary>
        public byte GetVEdge(int x, int z)
        {
            if (!hasEdgeLayout)
                return 0;
            int g = layoutGridSize;
            if (x < 0 || x > g || z < 0 || z >= g)
                return 0;
            int idx = z * (g + 1) + x;
            return (idx >= 0 && idx < verticalEdges.Length) ? verticalEdges[idx] : (byte)0;
        }

        /// <summary>Horizontal edge kind between cells (x,z-1) and (x,z). x:0..g-1, z:0..g.</summary>
        public byte GetHEdge(int x, int z)
        {
            if (!hasEdgeLayout)
                return 0;
            int g = layoutGridSize;
            if (x < 0 || x >= g || z < 0 || z > g)
                return 0;
            int idx = z * g + x;
            return (idx >= 0 && idx < horizontalEdges.Length) ? horizontalEdges[idx] : (byte)0;
        }
    }

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
                else if (MsgPackReader.Is(k, "entity_type")) e.entityType = r.ReadString();
                else if (MsgPackReader.Is(k, "position")) e.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) e.rotation = r.ReadFloat();
                else if (MsgPackReader.Is(k, "state")) e.state = r.ReadString();
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
                else if (MsgPackReader.Is(k, "item_type")) i.itemType = r.ReadString();
                else if (MsgPackReader.Is(k, "position")) i.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "quantity")) i.quantity = (int)r.ReadInt();
                else r.Skip();
            }
            return i;
        }
    }

    /// <summary>
    /// Phase 1 — one host-authoritative STP world item replicated in world_state.stp_items.
    /// id = network instance id (host-assigned); defId = STP ItemDefinition id (stable).
    /// </summary>
    public class StpItemMsg
    {
        public uint id;
        public int defId;
        public int count;
        public Vector3 position;
        public float rotation;

        public static StpItemMsg Parse(MsgPackReader r)
        {
            var m = new StpItemMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) m.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "def_id")) m.defId = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "count")) m.count = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) m.rotation = r.ReadFloat();
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>Outbound spec the host sends via IPCClient.SendSetStpItems (Phase 1).</summary>
    public struct StpItemSpec
    {
        public uint id;
        public int defId;
        public int count;
        public Vector3 position;
        public float rotation;
    }

    /// <summary>
    /// Phase B1 — one host-authoritative STP building piece replicated in
    /// world_state.stp_buildings. id = network instance id (host-assigned); defId =
    /// STP BuildingPieceDefinition id (stable across instances).
    /// </summary>
    public class StpBuildingMsg
    {
        public uint id;
        public int defId;
        public Vector3 position;
        public float rotation;
        // Phase B3 — host-assigned group identity (0 = standalone). Pieces sharing a groupId
        // are bucketed into one BuildingPieceGroup so sockets cohere across all clients.
        public uint groupId;
        // Phase B2 — host-authoritative construction progress (units of each material accepted).
        public List<StpBuildProgressMsg> added = new List<StpBuildProgressMsg>();

        public static StpBuildingMsg Parse(MsgPackReader r)
        {
            var m = new StpBuildingMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) m.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "def_id")) m.defId = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) m.rotation = r.ReadFloat();
                else if (MsgPackReader.Is(k, "group_id")) m.groupId = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "added"))
                {
                    int ac = r.ReadArrayHeader();
                    if (ac > 0)
                    {
                        m.added.Capacity = ac;
                        for (int ai = 0; ai < ac; ai++) m.added.Add(StpBuildProgressMsg.Parse(r));
                    }
                }
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>Phase B2 — one (material → accepted count) entry of a piece's progress.</summary>
    public class StpBuildProgressMsg
    {
        public int materialId;
        public int count;

        public static StpBuildProgressMsg Parse(MsgPackReader r)
        {
            var p = new StpBuildProgressMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "material_id")) p.materialId = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "count")) p.count = (int)r.ReadInt();
                else r.Skip();
            }
            return p;
        }
    }

    /// <summary>
    /// Phase B2.5 — one host-authoritative STP world carryable replicated in
    /// world_state.stp_carryables. id = network instance id (host-assigned); defId =
    /// STP CarryableDefinition id (stable).
    /// </summary>
    public class StpCarryableMsg
    {
        public uint id;
        public int defId;
        public Vector3 position;
        public float rotation;

        public static StpCarryableMsg Parse(MsgPackReader r)
        {
            var m = new StpCarryableMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) m.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "def_id")) m.defId = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "rotation")) m.rotation = r.ReadFloat();
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>Outbound spec the host sends via IPCClient.SendSetStpCarryables (Phase B2.5).</summary>
    public struct StpCarryableSpec
    {
        public uint id;
        public int defId;
        public Vector3 position;
        public float rotation;
    }

    /// <summary>
    /// Phase B2.6 — one host-authoritative STP scene harvestable (tree/rock) replicated in
    /// world_state.stp_harvestables. id = network instance id; remaining = harvest health
    /// (1.0 full → 0.0 depleted); position lets clients map id → local harvestable.
    /// </summary>
    public class StpHarvestableMsg
    {
        public uint id;
        public Vector3 position;
        public float remaining;

        public static StpHarvestableMsg Parse(MsgPackReader r)
        {
            var m = new StpHarvestableMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) m.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "position")) m.position = r.ReadVec3();
                else if (MsgPackReader.Is(k, "remaining")) m.remaining = r.ReadFloat();
                else r.Skip();
            }
            return m;
        }
    }

    /// <summary>Outbound spec the host sends via IPCClient.SendSetStpHarvestables (Phase B2.6).</summary>
    public struct StpHarvestableSpec
    {
        public uint id;
        public Vector3 position;
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
                else if (MsgPackReader.Is(k, "kind")) m.kind = r.ReadString();
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

        public static int Len(object v) => v is object[] a ? a.Length : 0;

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
