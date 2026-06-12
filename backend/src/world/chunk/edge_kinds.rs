pub const EDGE_NORTH: u8 = 1 << 0;
pub const EDGE_EAST: u8 = 1 << 1;
pub const EDGE_SOUTH: u8 = 1 << 2;
pub const EDGE_WEST: u8 = 1 << 3;

// ─── Edge-wall model (Phase 2.7) ───
//
// Walls, doors, arches and partitions live on the *boundary* between two cells,
// not as whole blocked cells. This removes 5m-thick "wall cells", double walls,
// and doorframes floating in cell centres. Cells themselves stay floor modules
// (walkable, or carrying a centre prop like a pillar/pit).
pub const EDGE_KIND_OPEN: u8 = 0;
pub const EDGE_KIND_WALL: u8 = 1;
pub const EDGE_KIND_DOOR: u8 = 2;
pub const EDGE_KIND_ARCH: u8 = 3;
pub const EDGE_KIND_LOW_WALL: u8 = 4;
pub const EDGE_KIND_HALF_WALL: u8 = 5;
pub const EDGE_KIND_PARTITION: u8 = 6;
pub const EDGE_KIND_FALSE_DOOR: u8 = 7;
pub const EDGE_KIND_BROKEN: u8 = 8;

/// Cell side indices used by `cell_side_edge`.
pub const SIDE_NORTH: u8 = 0;
pub const SIDE_EAST: u8 = 1;
pub const SIDE_SOUTH: u8 = 2;
pub const SIDE_WEST: u8 = 3;
