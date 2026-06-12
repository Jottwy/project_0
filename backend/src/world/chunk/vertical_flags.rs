pub const V30A_STACKED_CORRIDOR: u16 = 1 << 8;
pub const V30A_LOWER_SERVICE_BRANCH: u16 = 1 << 9;
pub const V30A_UPPER_OFFICE_BRANCH: u16 = 1 << 10;
pub const V30A_ATRIUM_VOID_ROOM: u16 = 1 << 11;
pub const V30A_DEEP_PRECIPICE_PLACEHOLDER: u16 = 1 << 12;
pub const V30A_GIANT_PILLAR_HALL: u16 = 1 << 13;
pub const V30A_CONNECTOR: u16 = 1 << 14;
pub const V30A_BLOCKED_VERTICAL_SHAFT: u16 = 1 << 15;

// Phase 3.0A — layer connectors. These span a full `LAYER_HEIGHT` along +Z so a
// player walks the whole vertical distance between two stacked layers without
// any free fall. UP rises toward the north (z+) edge, DOWN descends toward it.
pub const FLOOR_CONNECTOR_UP: u8 = 8;
pub const FLOOR_CONNECTOR_DOWN: u8 = 9;
