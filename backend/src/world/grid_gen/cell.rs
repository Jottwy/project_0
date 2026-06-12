use serde::{Deserialize, Serialize};

/// Width and depth of one grid cell in world units.
pub const CELL_SIZE_M: f32 = 2.5;

/// Cells per side in one chunk (20 × 20 = 400 cells = 50 m per side).
pub const CHUNK_CELLS: usize = 20;

/// Vertical separation between adjacent macro layers (6 × CELL_SIZE_M).
/// Every ceiling_height value must stay within this bound so no cell
/// overflows into the layer above.
pub const LAYER_HEIGHT_M: f32 = 15.0;

/// Hard ceiling on ceiling_height field (6 units × 2.5 m = 15 m = LAYER_HEIGHT_M).
pub const MAX_CEILING_UNITS: u8 = 6;

/// Cell type discriminant.
///
/// These integer values are a CONTRACT with Unity C#.
/// Changing any value here requires updating the mirror enum in C# AND
/// the `cell_contract_values_are_stable` test IN THE SAME COMMIT.
#[repr(u8)]
#[derive(Clone, Copy, PartialEq, Eq, Debug, Serialize, Deserialize)]
pub enum CellType {
    Wall    = 0,
    Corridor = 1,
    Open    = 2,
    Pillar  = 3,
    Stair   = 4,
    Pit     = 5,
    Void    = 6,
    Anomaly = 7,
}

impl CellType {
    pub fn is_solid(self) -> bool {
        matches!(self, CellType::Wall | CellType::Pillar | CellType::Void)
    }

    pub fn is_walkable(self) -> bool {
        matches!(
            self,
            CellType::Corridor | CellType::Open | CellType::Stair
                | CellType::Pit | CellType::Anomaly
        )
    }
}

/// A single 2.5 m × 2.5 m cell in the world grid.
///
/// `cell_type` is stored as `u8` to match the IPC wire format Unity reads.
/// Use `Cell::kind()` to obtain the type-safe `CellType` variant.
#[derive(Clone, Copy, PartialEq, Eq, Debug, Serialize, Deserialize)]
pub struct Cell {
    /// Wire-format discriminant; mirrors `CellType` repr.
    pub cell_type: u8,
    /// Ceiling height in units of 2.5 m. Range 1..=MAX_CEILING_UNITS.
    /// 0 is reserved for Wall (no walkable space, no ceiling).
    pub ceiling_height: u8,
    /// Piece/room this cell belongs to; 0 = none.
    pub zone_id: u16,
}

impl Cell {
    /// Default fill value for new grids: solid wall, no ceiling, no zone.
    pub const SOLID_WALL: Self = Self {
        cell_type: CellType::Wall as u8,
        ceiling_height: 0,
        zone_id: 0,
    };

    pub fn new(ct: CellType, ceiling: u8, zone: u16) -> Self {
        debug_assert!(
            ceiling <= MAX_CEILING_UNITS,
            "ceiling_height {ceiling} exceeds MAX_CEILING_UNITS ({MAX_CEILING_UNITS})"
        );
        Self {
            cell_type: ct as u8,
            ceiling_height: ceiling,
            zone_id: zone,
        }
    }

    /// Type-safe accessor for `cell_type`.
    pub fn kind(self) -> CellType {
        match self.cell_type {
            0 => CellType::Wall,
            1 => CellType::Corridor,
            2 => CellType::Open,
            3 => CellType::Pillar,
            4 => CellType::Stair,
            5 => CellType::Pit,
            6 => CellType::Void,
            7 => CellType::Anomaly,
            _ => CellType::Wall, // unknown values collapse to Wall (safe/solid)
        }
    }

    pub fn is_solid(self) -> bool {
        self.kind().is_solid()
    }

    pub fn is_walkable(self) -> bool {
        self.kind().is_walkable()
    }
}

/// Convert metres to ceiling_height units (rounded to nearest 2.5 m step).
pub fn height_units(metres: f32) -> u8 {
    (metres / CELL_SIZE_M).round() as u8
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Bloque B — contrato Cell↔Unity.
    ///
    /// Estos valores son un CONTRATO con Unity. Cambiarlos rompe el cliente.
    /// Si un valor cambia aquí, debe cambiar en el mismo commit en el enum C# espejo.
    #[test]
    fn cell_contract_values_are_stable() {
        assert_eq!(CellType::Wall as u8, 0);
        assert_eq!(CellType::Corridor as u8, 1);
        assert_eq!(CellType::Open as u8, 2);
        assert_eq!(CellType::Pillar as u8, 3);
        assert_eq!(CellType::Stair as u8, 4);
        assert_eq!(CellType::Pit as u8, 5);
        assert_eq!(CellType::Void as u8, 6);
        assert_eq!(CellType::Anomaly as u8, 7);

        // height_units helper: metros → unidades de 2.5 m
        assert_eq!(height_units(5.0), 2);
        assert_eq!(height_units(10.0), 4);
        assert_eq!(height_units(15.0), 6);

        // LAYER_HEIGHT_M == MAX_CEILING_UNITS × CELL_SIZE_M
        assert_eq!(
            LAYER_HEIGHT_M,
            MAX_CEILING_UNITS as f32 * CELL_SIZE_M,
            "LAYER_HEIGHT_M debe ser exactamente MAX_CEILING_UNITS pasos de CELL_SIZE_M"
        );

        // Cell::kind() es inverso de CellType as u8
        assert_eq!(Cell::new(CellType::Corridor, 2, 0).kind(), CellType::Corridor);
        assert_eq!(Cell::SOLID_WALL.kind(), CellType::Wall);
    }

    #[test]
    fn cell_walkability_is_consistent() {
        assert!(!CellType::Wall.is_walkable());
        assert!(!CellType::Pillar.is_walkable());
        assert!(!CellType::Void.is_walkable());
        assert!(CellType::Corridor.is_walkable());
        assert!(CellType::Open.is_walkable());
        assert!(CellType::Stair.is_walkable());
        assert!(CellType::Pit.is_walkable());
        assert!(CellType::Anomaly.is_walkable());
    }
}
