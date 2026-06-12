using System;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Cell type discriminant.
    ///
    /// These integer values are a CONTRACT with the Rust backend
    /// (backend/src/world/grid_gen/cell.rs). Changing any value here requires
    /// updating the Rust enum AND its `cell_contract_values_are_stable` test
    /// IN THE SAME COMMIT.
    /// </summary>
    public enum GridCellType : byte
    {
        Wall = 0,
        Corridor = 1,
        Open = 2,
        Pillar = 3,
        Stair = 4,
        Pit = 5,
        Void = 6,
        Anomaly = 7,
    }

    /// <summary>
    /// A single 2.5 m × 2.5 m cell in the world grid. Mirror of the Rust
    /// `Cell` struct (3 fields, wire format). Rust is the collision
    /// authority; Unity only renders these cells.
    /// </summary>
    [Serializable]
    public struct GridCell
    {
        /// <summary>Wire-format discriminant; mirrors GridCellType.</summary>
        public byte cellType;

        /// <summary>Ceiling height in units of 2.5 m. 0 reserved for Wall.</summary>
        public byte ceilingHeight;

        /// <summary>Piece/room this cell belongs to; 0 = none.</summary>
        public ushort zoneId;

        public GridCell(GridCellType type, byte ceiling, ushort zone)
        {
            cellType = (byte)type;
            ceilingHeight = ceiling;
            zoneId = zone;
        }

        /// <summary>Type-safe accessor; unknown values collapse to Wall (safe/solid).</summary>
        public GridCellType Kind =>
            cellType <= (byte)GridCellType.Anomaly ? (GridCellType)cellType : GridCellType.Wall;

        public bool IsWalkable
        {
            get
            {
                switch (Kind)
                {
                    case GridCellType.Corridor:
                    case GridCellType.Open:
                    case GridCellType.Stair:
                    case GridCellType.Pit:
                    case GridCellType.Anomaly:
                        return true;
                    default:
                        return false;
                }
            }
        }

        public bool IsSolid => !IsWalkable;
    }

    /// <summary>
    /// Grid constants mirrored from backend/src/world/grid_gen/cell.rs.
    /// Same contract rule as GridCellType: values change in lockstep with Rust.
    /// </summary>
    public static class GridConstants
    {
        /// <summary>Width and depth of one grid cell in metres (CELL_SIZE_M).</summary>
        public const float CellSize = 2.5f;

        /// <summary>Cells per side in one chunk (CHUNK_CELLS): 20 × 20 = 50 m per side.</summary>
        public const int ChunkCells = 20;

        /// <summary>Vertical separation between macro layers in metres (LAYER_HEIGHT_M).</summary>
        public const float LayerHeight = 15f;

        /// <summary>Hard ceiling on ceilingHeight (MAX_CEILING_UNITS): 6 × 2.5 m = 15 m.</summary>
        public const byte MaxCeilingUnits = 6;
    }
}
