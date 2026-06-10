use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Level0Profile {
    pub name: String,
    pub default_risk: u8,
    pub supports_verticality: bool,
}

impl Default for Level0Profile {
    fn default() -> Self {
        Self {
            name: "Level 0 - The Lobby".to_string(),
            default_risk: 1,
            supports_verticality: true,
        }
    }
}
