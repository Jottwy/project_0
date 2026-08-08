pub const ZONE_NORMAL: u8 = 0;
pub const ZONE_STORAGE: u8 = 1;
pub const ZONE_SAFE: u8 = 2;
pub const ZONE_DANGER: u8 = 3;
pub const ZONE_OPEN_HALL: u8 = 4;
pub const ZONE_PILLAR_HALL: u8 = 5;
pub const ZONE_HUMID: u8 = 6;
pub const ZONE_BLACKOUT: u8 = 7;
pub const ZONE_MANILA: u8 = 8;
pub const ZONE_CLEANING: u8 = 9;
pub const ZONE_RED: u8 = 10;
pub const ZONE_PIT: u8 = 11;
/// Planta de oficinas: despachos cerrados en retícula 2×2 intra-chunk, sin
/// columnas. Primer `zone_kind` añadido después de los 12 originales — el
/// espacio de valores es abierto (`u8` por el wire), pero TODO consumidor
/// indexado por zona en el cliente debe cubrir 0..=12 o su `Clamp` colapsará
/// OFFICE sobre `ZONE_PIT` en silencio: `LayerVisualConfig.zoneTints`,
/// `ZoneLootTable.profiles` y `ChunkLootRoll.DefaultZoneLootProfiles`.
pub const ZONE_OFFICE: u8 = 12;

pub const FLOOR_FLAT: u8 = 0;
pub const FLOOR_SUNKEN: u8 = 1;
pub const FLOOR_RAISED: u8 = 2;
pub const FLOOR_RAMP_NORTH_SOUTH: u8 = 3;
pub const FLOOR_RAMP_EAST_WEST: u8 = 4;
pub const FLOOR_PIT_PLACEHOLDER: u8 = 5;
pub const FLOOR_STAIRS_NORTH_SOUTH: u8 = 6;
pub const FLOOR_STAIRS_EAST_WEST: u8 = 7;

pub const CEILING_NORMAL: u8 = 0;
pub const CEILING_LOW_SERVICE: u8 = 1;
pub const CEILING_TALL_HALL: u8 = 2;
pub const CEILING_DAMAGED: u8 = 3;

pub const LIGHT_NORMAL: u8 = 0;
pub const LIGHT_DIM: u8 = 1;
pub const LIGHT_BLACKOUT: u8 = 2;
pub const LIGHT_RED: u8 = 3;
pub const LIGHT_WARM: u8 = 4;
