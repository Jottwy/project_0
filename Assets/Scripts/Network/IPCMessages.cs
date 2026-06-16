using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    public class StatsMsg
    {
        public float health, hunger, thirst, sanity;
        // ADR-009: server-authoritative stamina, interpolated client-side at 5 Hz.
        public float stamina;

        public static StatsMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var s = new StatsMsg();
            if (d == null) return s;
            s.health = IPCParse.F(d, "health");
            s.hunger = IPCParse.F(d, "hunger");
            s.thirst = IPCParse.F(d, "thirst");
            s.sanity = IPCParse.F(d, "sanity");
            s.stamina = IPCParse.F(d, "stamina");
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

        public static LocalPlayerMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var p = new LocalPlayerMsg();
            if (d == null) return p;
            p.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            p.rotation = IPCParse.F(d, "rotation");
            p.stats = StatsMsg.Parse(IPCParse.Get(d, "stats"));
            p.speedModifier = IPCParse.F(d, "speed_modifier");
            p.inventoryChanged = IPCParse.B(d, "inventory_changed");
            p.ackInputSeq = (uint)IPCParse.L(d, "ack_input_seq");
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

        public static MovementDeltaMsg Parse(Dictionary<string, object> d)
        {
            var m = new MovementDeltaMsg();
            if (d == null) return m;
            m.tick = (uint)IPCParse.L(d, "tick");
            m.ackInputSeq = (uint)IPCParse.L(d, "ack_input_seq");
            m.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            m.velocity = IPCParse.Vec3(IPCParse.Get(d, "velocity"));
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

        public static RemotePlayerMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var r = new RemotePlayerMsg();
            if (d == null) return r;
            r.id = (int)IPCParse.L(d, "id");
            r.name = IPCParse.S(d, "name");
            r.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            r.rotation = IPCParse.F(d, "rotation");
            r.animation = IPCParse.S(d, "animation");
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

        public static InterLayerVolumeMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var v = new InterLayerVolumeMsg();
            if (d == null) return v;
            v.volumeId = (uint)IPCParse.L(d, "volume_id");
            v.kind = IPCParse.S(d, "kind");
            v.baseChunk = IPCParse.IntArray2(IPCParse.Get(d, "base_chunk"));
            v.involvedLayers = IPCParse.IntArray(IPCParse.Get(d, "involved_layers"));
            v.footprintCellMin = IPCParse.IntArray2(IPCParse.Get(d, "footprint_cell_min"));
            v.footprintCellMax = IPCParse.IntArray2(IPCParse.Get(d, "footprint_cell_max"));
            v.safetyType = IPCParse.S(d, "safety_type");
            v.futureAudioHint = IPCParse.S(d, "future_audio_hint");
            v.visualFlags = (int)IPCParse.L(d, "visual_flags");
            v.visualHints = IPCParse.StringArray(IPCParse.Get(d, "visual_hints"));
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

        public static VolumetricFaceMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var f = new VolumetricFaceMsg();
            if (d == null) return f;
            var cell = IPCParse.IntArray(IPCParse.Get(d, "cell"));
            if (cell.Length >= 3) { f.x = cell[0]; f.y = cell[1]; f.z = cell[2]; }
            f.dir = (byte)IPCParse.L(d, "dir");
            f.kind = (byte)IPCParse.L(d, "kind");
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

        public static LayerBandMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var b = new LayerBandMsg();
            if (d == null) return b;
            b.bandId = (uint)IPCParse.L(d, "band_id");
            b.layer = (int)IPCParse.L(d, "layer");
            b.profile = IPCParse.S(d, "profile");
            b.profileCode = (int)IPCParse.L(d, "profile_code");
            b.accessible = IPCParse.B(d, "accessible");
            b.dangerProfile = IPCParse.S(d, "danger_profile");
            b.resourceProfile = IPCParse.S(d, "resource_profile");
            b.anomalyProfile = IPCParse.S(d, "anomaly_profile");
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

        public static VerticalAccessNodeMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var n = new VerticalAccessNodeMsg();
            if (d == null) return n;
            n.accessId = (uint)IPCParse.L(d, "access_id");
            n.accessType = IPCParse.S(d, "access_type");
            n.accessTypeCode = (int)IPCParse.L(d, "access_type_code");
            n.fromLayer = (int)IPCParse.L(d, "from_layer");
            n.toLayer = (int)IPCParse.L(d, "to_layer");
            n.footprintCellMin = IPCParse.IntArray2(IPCParse.Get(d, "footprint_cell_min"));
            n.footprintCellMax = IPCParse.IntArray2(IPCParse.Get(d, "footprint_cell_max"));
            n.explicitAccess = IPCParse.B(d, "explicit");
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

        public static BandHeightSpecMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var b = new BandHeightSpecMsg();
            if (d == null) return b;
            b.bandIndex = (int)IPCParse.L(d, "band_index");
            b.layer = (int)IPCParse.L(d, "layer");
            b.roomHeight = IPCParse.F(d, "room_height");
            b.totalHeight = IPCParse.F(d, "total_height");
            b.neighborMaxRoomHeight = IPCParse.F(d, "neighbor_max_room_height");
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

        public static VolumetricGridMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            var g = new VolumetricGridMsg();
            g.active = IPCParse.B(d, "active");
            g.columnId = (ulong)IPCParse.L(d, "column_id");
            g.columnCoord = IPCParse.IntArray2(IPCParse.Get(d, "column_coord"));
            g.source = IPCParse.S(d, "source");
            var dims = IPCParse.IntArray(IPCParse.Get(d, "dims"));
            if (dims.Length >= 3) { g.nx = dims[0]; g.ny = dims[1]; g.nz = dims[2]; }
            g.cellSizeXZ = IPCParse.F(d, "cell_size_xz");
            if (g.cellSizeXZ <= 0f) g.cellSizeXZ = 5f;
            g.layerHeight = IPCParse.F(d, "layer_height");
            if (g.layerHeight <= 0f) g.layerHeight = 7f;
            g.originWorld = IPCParse.Vec3(IPCParse.Get(d, "origin_world"));
            g.baseLayer = (int)IPCParse.L(d, "base_layer");
            g.cells = IPCParse.ByteArray(IPCParse.Get(d, "cells"));
            if (IPCParse.Get(d, "faces") is object[] fs)
                foreach (var item in fs) g.faces.Add(VolumetricFaceMsg.Parse(item));
            g.openCellCount = (int)IPCParse.L(d, "open_cell_count");
            g.solidCellCount = (int)IPCParse.L(d, "solid_cell_count");
            g.verticalConnectionCount = (int)IPCParse.L(d, "vertical_connection_count");
            g.validVerticalOpeningCount = (int)IPCParse.L(d, "valid_vertical_opening_count");
            g.atriumSpan = IPCParse.B(d, "atrium_span");
            if (IPCParse.Get(d, "layer_bands") is object[] bands)
                foreach (var item in bands) g.layerBands.Add(LayerBandMsg.Parse(item));
            if (IPCParse.Get(d, "vertical_access") is object[] access)
                foreach (var item in access) g.verticalAccess.Add(VerticalAccessNodeMsg.Parse(item));
            if (IPCParse.Get(d, "height_bands") is object[] hbands)
                foreach (var item in hbands) g.heightBands.Add(BandHeightSpecMsg.Parse(item));
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
        private static readonly HashSet<string> _loggedVolumeParseChunks = new HashSet<string>();

        public static ChunkViewMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var c = new ChunkViewMsg();
            if (d == null) return c;
            c.chunkSchema = (int)IPCParse.L(d, "chunk_schema");
            if (c.chunkSchema <= 0) c.chunkSchema = 1;
            c.pos = IPCParse.IntArray2(IPCParse.Get(d, "pos"));
            c.layer = (int)IPCParse.L(d, "layer");
            c.layerY = IPCParse.F(d, "layer_y");
            c.templateId = (int)IPCParse.L(d, "template_id");
            c.rotation = (int)IPCParse.L(d, "rotation");
            c.mirrored = IPCParse.B(d, "mirrored");
            c.state = IPCParse.S(d, "state");
            c.hasWorkbench = IPCParse.B(d, "has_workbench");
            c.layoutGridSize = Mathf.Max(1, (int)IPCParse.L(d, "layout_grid_size"));
            c.layoutCellSize = IPCParse.F(d, "layout_cell_size");
            if (c.layoutCellSize <= 0f) c.layoutCellSize = 5f;
            c.layoutCells = IPCParse.UShortArray(IPCParse.Get(d, "layout_cells"));
            c.edgeOpenings = (int)IPCParse.L(d, "edge_openings");
            c.macroId = (uint)IPCParse.L(d, "macro_id");
            c.zoneKind = (int)IPCParse.L(d, "zone_kind");
            c.macroLocal = IPCParse.IntArray2(IPCParse.Get(d, "macro_local"));
            c.macroSize = IPCParse.IntArray2(IPCParse.Get(d, "macro_size"));
            if (c.macroSize[0] <= 0) c.macroSize[0] = 1;
            if (c.macroSize[1] <= 0) c.macroSize[1] = 1;
            c.floorLevel = (int)IPCParse.L(d, "floor_level");
            c.floorProfile = (int)IPCParse.L(d, "floor_profile");
            c.ceilingProfile = (int)IPCParse.L(d, "ceiling_profile");
            c.lightProfile = (int)IPCParse.L(d, "light_profile");
            c.anomalyFlags = (int)IPCParse.L(d, "anomaly_flags");
            c.verticalFlags = (int)IPCParse.L(d, "vertical_flags");
            if (IPCParse.Get(d, "inter_layer_volumes") is object[] volumes)
                foreach (var volume in volumes) c.interLayerVolumes.Add(InterLayerVolumeMsg.Parse(volume));
            if (c.interLayerVolumes.Count > 0)
                LogVolumeParseOnce(c);
            if (IPCParse.Get(d, "volumetric_grid") != null)
                c.volumetricGrid = VolumetricGridMsg.Parse(IPCParse.Get(d, "volumetric_grid"));
            c.SplitPackedLayout();
            return c;
        }

        private static void LogVolumeParseOnce(ChunkViewMsg c)
        {
            string key = $"{c.pos[0]}:{c.layer}:{c.pos[1]}:{c.interLayerVolumes.Count}";
            if (!_loggedVolumeParseChunks.Add(key))
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

        public static EntityViewMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var e = new EntityViewMsg();
            if (d == null) return e;
            e.id = (uint)IPCParse.L(d, "id");
            e.entityType = IPCParse.S(d, "entity_type");
            e.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            e.rotation = IPCParse.F(d, "rotation");
            e.state = IPCParse.S(d, "state");
            e.healthPct = IPCParse.F(d, "health_pct");
            return e;
        }
    }

    public class ItemViewMsg
    {
        public uint id;
        public string itemType = "";
        public Vector3 position;
        public int quantity;

        public static ItemViewMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var i = new ItemViewMsg();
            if (d == null) return i;
            i.id = (uint)IPCParse.L(d, "id");
            i.itemType = IPCParse.S(d, "item_type");
            i.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            i.quantity = (int)IPCParse.L(d, "quantity");
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

        public static StpItemMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var m = new StpItemMsg();
            if (d == null) return m;
            m.id = (uint)IPCParse.L(d, "id");
            m.defId = (int)IPCParse.L(d, "def_id");
            m.count = (int)IPCParse.L(d, "count");
            m.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            m.rotation = IPCParse.F(d, "rotation");
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
        // Phase B2 — host-authoritative construction progress (units of each material accepted).
        public List<StpBuildProgressMsg> added = new List<StpBuildProgressMsg>();

        public static StpBuildingMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var m = new StpBuildingMsg();
            if (d == null) return m;
            m.id = (uint)IPCParse.L(d, "id");
            m.defId = (int)IPCParse.L(d, "def_id");
            m.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            m.rotation = IPCParse.F(d, "rotation");
            if (IPCParse.Get(d, "added") is object[] ad)
                foreach (var item in ad) m.added.Add(StpBuildProgressMsg.Parse(item));
            return m;
        }
    }

    /// <summary>Phase B2 — one (material → accepted count) entry of a piece's progress.</summary>
    public class StpBuildProgressMsg
    {
        public int materialId;
        public int count;

        public static StpBuildProgressMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var p = new StpBuildProgressMsg();
            if (d == null) return p;
            p.materialId = (int)IPCParse.L(d, "material_id");
            p.count = (int)IPCParse.L(d, "count");
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

        public static StpCarryableMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var m = new StpCarryableMsg();
            if (d == null) return m;
            m.id = (uint)IPCParse.L(d, "id");
            m.defId = (int)IPCParse.L(d, "def_id");
            m.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            m.rotation = IPCParse.F(d, "rotation");
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

        public static StpHarvestableMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var m = new StpHarvestableMsg();
            if (d == null) return m;
            m.id = (uint)IPCParse.L(d, "id");
            m.position = IPCParse.Vec3(IPCParse.Get(d, "position"));
            m.remaining = IPCParse.F(d, "remaining");
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

        public static VerticalDebugMarkerMsg Parse(object o)
        {
            var d = o as Dictionary<string, object>;
            var m = new VerticalDebugMarkerMsg();
            if (d == null) return m;
            m.id = (uint)IPCParse.L(d, "id");
            m.kind = IPCParse.S(d, "kind");
            m.worldMin = IPCParse.Vec3(IPCParse.Get(d, "world_min"));
            m.worldMax = IPCParse.Vec3(IPCParse.Get(d, "world_max"));
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

        public static WorldStateMsg Parse(Dictionary<string, object> d)
        {
            var ws = new WorldStateMsg();
            if (d == null) return ws;
            ws.tick = IPCParse.L(d, "tick");
            ws.worldSeed = IPCParse.L(d, "world_seed");
            ws.worldRevision = IPCParse.L(d, "world_revision");
            ws.localPlayer = LocalPlayerMsg.Parse(IPCParse.Get(d, "local_player"));

            if (IPCParse.Get(d, "remote_players") is object[] rp)
                foreach (var item in rp) ws.remotePlayers.Add(RemotePlayerMsg.Parse(item));

            if (IPCParse.Get(d, "visible_chunks") is object[] vc)
                foreach (var item in vc) ws.visibleChunks.Add(ChunkViewMsg.Parse(item));

            if (IPCParse.Get(d, "visible_entities") is object[] ve)
                foreach (var item in ve) ws.visibleEntities.Add(EntityViewMsg.Parse(item));

            if (IPCParse.Get(d, "visible_items") is object[] vi)
                foreach (var item in vi) ws.visibleItems.Add(ItemViewMsg.Parse(item));

            if (IPCParse.Get(d, "vertical_debug_markers") is object[] vm)
                foreach (var item in vm) ws.verticalDebugMarkers.Add(VerticalDebugMarkerMsg.Parse(item));

            if (IPCParse.Get(d, "stp_items") is object[] si)
                foreach (var item in si) ws.stpItems.Add(StpItemMsg.Parse(item));

            if (IPCParse.Get(d, "stp_buildings") is object[] sb)
                foreach (var item in sb) ws.stpBuildings.Add(StpBuildingMsg.Parse(item));

            if (IPCParse.Get(d, "stp_carryables") is object[] sc)
                foreach (var item in sc) ws.stpCarryables.Add(StpCarryableMsg.Parse(item));

            if (IPCParse.Get(d, "stp_harvestables") is object[] sh)
                foreach (var item in sh) ws.stpHarvestables.Add(StpHarvestableMsg.Parse(item));

            return ws;
        }
    }

    public class GameEventMsg
    {
        public string eventType = "";
        public object data;

        public static GameEventMsg Parse(Dictionary<string, object> d)
        {
            var e = new GameEventMsg();
            if (d == null) return e;
            e.eventType = IPCParse.S(d, "event_type");
            e.data = IPCParse.Get(d, "data");
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
