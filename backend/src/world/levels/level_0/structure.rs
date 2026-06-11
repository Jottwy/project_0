use crate::utils::ChunkPos;
use crate::world::chunk::ChunkLayer;

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
