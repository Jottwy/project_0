use serde::{Deserialize, Serialize};

/// A dropped item lying in the world (lost if the chunk teleports).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DroppedItem {
    pub id: u32,
    pub item: crate::player::inventory::Item,
    pub quantity: u16,
    pub position: crate::utils::Vec3,
}
