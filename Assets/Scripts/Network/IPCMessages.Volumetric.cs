using System;
using System.Collections.Generic;
using UnityEngine;

// Parte del espejo C# de los tipos de wire del IPC (backend/src/ipc/mod.rs).
// EL CONTRATO DE DECODIFICACION (cabecera de mapa propia, `else r.Skip()` obligatorio,
// defaults por omision, post-fixups fuera del bucle) esta enunciado UNA sola vez, en
// IPCMessages.cs. Leelo alli antes de tocar cualquier Parse de este fichero.

namespace BackroomsSurvival.Net
{
    // ─────────── Volumetric V0 "Rubik grid" (render-only, near-spawn showcase chunk) ───────────
    // Migration scaffolding, not the live renderer — see docs/STATE.md on volumetric_grid.rs.

    public class InterLayerVolumeMsg
    {
        public uint volumeId;
        public string kind = "";
        public int[] baseChunk = new int[2];
        public int[] involvedLayers = Array.Empty<int>();
        public int[] footprintCellMin = new int[2];
        public int[] footprintCellMax = new int[2];
        public string safetyType = "";
        public string futureAudioHint = "";
        public int visualFlags;
        public string[] visualHints = Array.Empty<string>();

        public static InterLayerVolumeMsg Parse(MsgPackReader r)
        {
            var v = new InterLayerVolumeMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "volume_id")) v.volumeId = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "kind")) v.kind = r.ReadStringCached();
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
        public byte[] cells = Array.Empty<byte>();
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
}
