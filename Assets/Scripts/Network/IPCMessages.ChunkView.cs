using System;
using System.Collections.Generic;
using UnityEngine;

// Parte del espejo C# de los tipos de wire del IPC (backend/src/ipc/mod.rs).
// EL CONTRATO DE DECODIFICACION (cabecera de mapa propia, `else r.Skip()` obligatorio,
// defaults por omision, post-fixups fuera del bucle) esta enunciado UNA sola vez, en
// IPCMessages.cs. Leelo alli antes de tocar cualquier Parse de este fichero.

namespace BackroomsSurvival.Net
{
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
        public ushort[] layoutCells = Array.Empty<ushort>();
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
        public ushort[] cellFlags = Array.Empty<ushort>();
        public byte[] verticalEdges = Array.Empty<byte>();
        public byte[] horizontalEdges = Array.Empty<byte>();
        public bool hasBackendLayout;
        public bool hasEdgeLayout;

        public bool HasEdgeLayout => hasEdgeLayout;
        public bool HasBackendLayout => hasBackendLayout;

        // ── Dedup sets for the two log-once diagnostics below ──────────────────
        //
        // Both stay small by construction, so they are not a memory concern: the invalid-layout
        // key is (grid << 20) ^ length with grid effectively always 10, i.e. a handful of
        // distinct values; the volume key only gains an entry per volume-carrying chunk, and
        // volumes exist on showcase chunks only.
        //
        // They DO need the reset below, though. Being `static` they survive an editor domain
        // reload, so without it the first Play session consumes the "once" and every later
        // session in the same editor process is silently deaf to these traces — the failure mode
        // is a diagnostic that stops working exactly when you go looking for it. Same
        // ResetStatics pattern as IPCClient and ZoneRegistry.
        //
        // Touched from the IPC network thread (Dispatch → WorldStateMsg.Parse → here), NOT the
        // main thread, and deliberately unsynchronized: there is exactly one reader thread. If a
        // second one ever parses chunks, these two Add calls need a lock.
        private static readonly HashSet<int> _loggedInvalidLayouts = new HashSet<int>();
        // Tuple key, not an interpolated string: only the Debug.Log below is deduplicated, so the
        // key itself was still being built for every volume-carrying chunk of every snapshot,
        // long after the log had gone quiet.
        private static readonly HashSet<(int, int, int, int)> _loggedVolumeParseChunks
            = new HashSet<(int, int, int, int)>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _loggedInvalidLayouts.Clear();
            _loggedVolumeParseChunks.Clear();
        }

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
                else if (MsgPackReader.Is(k, "state")) c.state = r.ReadStringCached();
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
            cellFlags = Array.Empty<ushort>();
            verticalEdges = Array.Empty<byte>();
            horizontalEdges = Array.Empty<byte>();

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
}
