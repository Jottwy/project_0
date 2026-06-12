use crate::utils::ChunkPos;
use crate::world::chunk::ChunkLayer;
use crate::world::chunk::{
    ZONE_BLACKOUT, ZONE_CLEANING, ZONE_DANGER, ZONE_HUMID, ZONE_MANILA, ZONE_NORMAL,
    ZONE_OPEN_HALL, ZONE_PILLAR_HALL, ZONE_PIT, ZONE_RED, ZONE_SAFE, ZONE_STORAGE,
};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StructureType {
    StarterCluster,
    HallwayChain,
    Intersection,
    StorageRoom,
    SafeRoom,
    DeadEnd,
    DangerRoom,
    HallwayT,
    PillarRoom,
    OpenHall,
    PillarHall,
    HumidZone,
    ArchRoom,
    BlackoutZone,
    RedRoom,
    ManilaRoom,
    CleaningArea,
    PitRoom,
    StackedCorridor,
    LowerServiceBranch,
    UpperOfficeBranch,
    AtriumVoidRoom,
    DeepPrecipicePlaceholder,
    GiantPillarHall,
    PoiLandmark,
    PoiAnomalyCluster,
    PoiDangerPocket,
    PoiSafePocket,
}

impl StructureType {
    pub fn as_str(self) -> &'static str {
        match self {
            StructureType::StarterCluster => "starter_cluster",
            StructureType::HallwayChain => "hallway_chain",
            StructureType::Intersection => "intersection",
            StructureType::StorageRoom => "storage_room",
            StructureType::SafeRoom => "safe_room",
            StructureType::DeadEnd => "dead_end",
            StructureType::DangerRoom => "danger_room",
            StructureType::HallwayT => "hallway_t",
            StructureType::PillarRoom => "pillar_room",
            StructureType::OpenHall => "open_hall",
            StructureType::PillarHall => "pillar_hall",
            StructureType::HumidZone => "humid_zone",
            StructureType::ArchRoom => "arch_room",
            StructureType::BlackoutZone => "blackout_zone",
            StructureType::RedRoom => "red_room",
            StructureType::ManilaRoom => "manila_room",
            StructureType::CleaningArea => "cleaning_area",
            StructureType::PitRoom => "pit_room_placeholder",
            StructureType::StackedCorridor => "stacked_corridor",
            StructureType::LowerServiceBranch => "lower_service_branch",
            StructureType::UpperOfficeBranch => "upper_office_branch",
            StructureType::AtriumVoidRoom => "atrium_void_room",
            StructureType::DeepPrecipicePlaceholder => "deep_precipice_placeholder",
            StructureType::GiantPillarHall => "giant_pillar_hall",
            StructureType::PoiLandmark => "poi_landmark",
            StructureType::PoiAnomalyCluster => "poi_anomaly_cluster",
            StructureType::PoiDangerPocket => "poi_danger_pocket",
            StructureType::PoiSafePocket => "poi_safe_pocket",
        }
    }
}

#[derive(Debug, Clone)]
pub struct StructureV0 {
    pub id: u32,
    pub structure_type: StructureType,
    pub origin: ChunkPos,
    pub origin_layer: ChunkLayer,
    pub size: [u8; 2],
    pub seed: u64,
    pub chunks: Vec<ChunkPos>,
    pub layers: Vec<ChunkLayer>,
    pub tags: Vec<&'static str>,
    pub chunk_overrides: Vec<(u8, u16)>,
}

impl StructureV0 {
    pub fn chunk_layer(&self, index: usize) -> ChunkLayer {
        self.layers.get(index).copied().unwrap_or(0)
    }
}

// ─── MIG-5c: structure helpers (moved from generator.rs) ───

pub(crate) fn structure_bounds(chunks: &[ChunkPos]) -> (i32, i32, i32, i32) {
    let mut min_x = chunks[0].0;
    let mut min_z = chunks[0].1;
    let mut max_x = chunks[0].0;
    let mut max_z = chunks[0].1;
    for &(x, z) in chunks {
        min_x = min_x.min(x);
        min_z = min_z.min(z);
        max_x = max_x.max(x);
        max_z = max_z.max(z);
    }
    (min_x, min_z, max_x, max_z)
}

pub(crate) fn structure_zone_kind(structure_type: StructureType, template_id: u8) -> u8 {
    match structure_type {
        StructureType::StorageRoom => ZONE_STORAGE,
        StructureType::SafeRoom | StructureType::StarterCluster => ZONE_SAFE,
        StructureType::DangerRoom => ZONE_DANGER,
        StructureType::OpenHall => ZONE_OPEN_HALL,
        StructureType::PillarRoom | StructureType::PillarHall => ZONE_PILLAR_HALL,
        StructureType::HumidZone => ZONE_HUMID,
        StructureType::BlackoutZone => ZONE_BLACKOUT,
        StructureType::ManilaRoom => ZONE_MANILA,
        StructureType::CleaningArea => ZONE_CLEANING,
        StructureType::RedRoom => ZONE_RED,
        StructureType::PitRoom => ZONE_PIT,
        StructureType::StackedCorridor => ZONE_NORMAL,
        StructureType::LowerServiceBranch => ZONE_CLEANING,
        StructureType::UpperOfficeBranch => ZONE_MANILA,
        StructureType::AtriumVoidRoom => ZONE_OPEN_HALL,
        StructureType::DeepPrecipicePlaceholder => ZONE_PIT,
        StructureType::GiantPillarHall => ZONE_PILLAR_HALL,
        StructureType::PoiLandmark => ZONE_OPEN_HALL,
        StructureType::PoiAnomalyCluster => ZONE_NORMAL,
        StructureType::PoiDangerPocket => ZONE_DANGER,
        StructureType::PoiSafePocket => ZONE_MANILA,
        _ => crate::world::architecture::layout_grammars::template_zone_kind(template_id),
    }
}
