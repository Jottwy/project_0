//! JSON save/load.
//! See ARCHITECTURE_V1.md §10. Phase 5 fleshes out the distributed merge; the
//! file format and basic read/write are implemented here.

use std::path::Path;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SaveConfig {
    pub max_players: u16,
    pub teleport_interval_min: u32,
    pub teleport_interval_max: u32,
    pub entity_scaling: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SaveFile {
    pub version: String,
    pub world_seed: u64,
    pub session_name: String,
    pub created_at: String,
    pub last_saved: String,
    pub play_time_seconds: u64,
    pub config: SaveConfig,
    // Detailed per-player / anchor / stabilizer payloads are added in Phase 5.
}

impl SaveFile {
    pub fn new(session_name: impl Into<String>, world_seed: u64) -> Self {
        let now = chrono::Utc::now().to_rfc3339();
        Self {
            version: env!("CARGO_PKG_VERSION").to_string(),
            world_seed,
            session_name: session_name.into(),
            created_at: now.clone(),
            last_saved: now,
            play_time_seconds: 0,
            config: SaveConfig {
                max_players: 50,
                teleport_interval_min: 120,
                teleport_interval_max: 600,
                entity_scaling: 1.0,
            },
        }
    }

    /// Serialize to pretty JSON and write to disk.
    pub fn save_to<P: AsRef<Path>>(&mut self, path: P) -> std::io::Result<()> {
        self.last_saved = chrono::Utc::now().to_rfc3339();
        let json = serde_json::to_string_pretty(self)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::Other, e))?;
        std::fs::write(path, json)
    }

    /// Load and parse a save file from disk.
    pub fn load_from<P: AsRef<Path>>(path: P) -> std::io::Result<Self> {
        let json = std::fs::read_to_string(path)?;
        serde_json::from_str(&json)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))
    }
}
