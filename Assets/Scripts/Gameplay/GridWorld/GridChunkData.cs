using System;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Wire format for one chunk of grid cells.
    ///
    /// CONTRACT with the Rust exporter (backend/tools/grid_viewer, mode
    /// `export`) and, from Fase 4 on, with the IPC chunk message:
    /// 400 cells row-major (z * ChunkCells + x), 4 bytes per cell:
    /// cell_type u8, ceiling_height u8, zone_id u16 little-endian. No header.
    /// </summary>
    public static class GridChunkData
    {
        public const int BytesPerCell = 4;
        public const int ChunkByteSize =
            GridConstants.ChunkCells * GridConstants.ChunkCells * BytesPerCell;

        public static GridCell[] FromBytes(byte[] data)
        {
            if (data == null || data.Length != ChunkByteSize)
                throw new ArgumentException(
                    $"grid chunk payload must be exactly {ChunkByteSize} bytes, " +
                    $"got {(data == null ? 0 : data.Length)}");

            var cells = new GridCell[GridConstants.ChunkCells * GridConstants.ChunkCells];
            for (int i = 0; i < cells.Length; i++)
            {
                int o = i * BytesPerCell;
                cells[i] = new GridCell
                {
                    cellType = data[o],
                    ceilingHeight = data[o + 1],
                    zoneId = (ushort)(data[o + 2] | (data[o + 3] << 8)),
                };
            }
            return cells;
        }
    }
}
