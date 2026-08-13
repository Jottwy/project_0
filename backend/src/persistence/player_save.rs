//! ADR-045 Fase 2: per-player save file, separate from the world save (`save.rs`) and written by
//! whichever backend owns that player (host or joiner — unlike `world_{seed}.json`, which stays
//! host-only). Same robustness contract as the world save: atomic tmp+rename, `.bak` on an
//! unusable file, NEVER hard-fails the caller.

use std::path::{Path, PathBuf};

use log::warn;
use serde::{Deserialize, Serialize};

use crate::persistence::save::{backup_unusable, major_of, PlayerSnapshot};
use crate::player::Player;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PlayerFile {
    pub version: String,
    pub identity_key: String,
    pub last_saved: String,
    pub snapshot: PlayerSnapshot,
}

impl PlayerFile {
    fn from_player(identity_key: String, player: &Player) -> Self {
        Self {
            version: env!("CARGO_PKG_VERSION").to_string(),
            identity_key,
            last_saved: chrono::Utc::now().to_rfc3339(),
            snapshot: PlayerSnapshot::from_player(player),
        }
    }

    /// Serialize to pretty JSON and write ATOMICALLY (`<path>.tmp` then rename), same mechanism
    /// as `SaveFile::save_to`. Creates the parent dir (`players/world_{seed}/`) if needed.
    fn save_to<P: AsRef<Path>>(&mut self, path: P) -> std::io::Result<()> {
        self.last_saved = chrono::Utc::now().to_rfc3339();
        let json = serde_json::to_string_pretty(self).map_err(std::io::Error::other)?;

        let path = path.as_ref();
        if let Some(parent) = path.parent() {
            if !parent.as_os_str().is_empty() {
                std::fs::create_dir_all(parent)?;
            }
        }

        let mut tmp = path.as_os_str().to_owned();
        tmp.push(".tmp");
        let tmp = PathBuf::from(tmp);
        std::fs::write(&tmp, json)?;
        std::fs::rename(&tmp, path)
    }

    fn load_from<P: AsRef<Path>>(path: P) -> std::io::Result<Self> {
        let json = std::fs::read_to_string(path)?;
        serde_json::from_str(&json)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))
    }
}

/// ADR-045 Fase 2: robust load, same contract as `save::load_or_fresh` — absent, unparseable, or
/// a MAJOR-version mismatch all return `None` (caller starts that player fresh); an unusable file
/// is renamed to `<path>.bak` rather than left in place. Never hard-fails.
pub fn load_or_fresh<P: AsRef<Path>>(path: P) -> Option<PlayerFile> {
    let path = path.as_ref();
    if !path.exists() {
        return None;
    }
    match PlayerFile::load_from(path) {
        Ok(file) => {
            let current = env!("CARGO_PKG_VERSION");
            if major_of(&file.version) != major_of(current) {
                warn!(
                    "ADR-045: player file major version {} incompatible with current {}; starting fresh",
                    file.version, current
                );
                backup_unusable(path);
                return None;
            }
            Some(file)
        }
        Err(e) => {
            warn!(
                "ADR-045: could not parse player file at {} ({e}); starting fresh",
                path.display()
            );
            backup_unusable(path);
            None
        }
    }
}

/// ADR-045 Fase 2: build + write the player file. Atomic write via `PlayerFile::save_to`.
pub fn save_player<P: AsRef<Path>>(
    path: P,
    identity_key: &str,
    player: &Player,
) -> std::io::Result<()> {
    let mut file = PlayerFile::from_player(identity_key.to_string(), player);
    file.save_to(path)
}

/// Root directory for player files — same configurable base the world save resolves from
/// (`SAVE_PATH`, a full FILE path; this takes its parent), default `./saves` when unset.
fn player_save_root() -> PathBuf {
    match std::env::var("SAVE_PATH") {
        Ok(path) => PathBuf::from(path)
            .parent()
            .map(Path::to_path_buf)
            .filter(|p| !p.as_os_str().is_empty())
            .unwrap_or_else(|| PathBuf::from(".")),
        Err(_) => PathBuf::from("./saves"),
    }
}

/// `:` is the ONLY character `persistence::sanitize_player_key`'s whitelist allows that is unsafe
/// as a Windows filename component — it is NTFS Alternate Data Stream syntax there (`file:stream`),
/// so a literal `uuid:...` filename would not create the visible file its name promises. The `:`
/// carries meaning in the KEY (the `uuid:`/`name:` namespace prefix ADR-045 requires) but not in
/// the FILENAME, so it is substituted only here, at the filesystem boundary — the semantic key
/// (with its colon intact) is what travels over IPC and lives in `PlayerFile::identity_key`.
fn filesystem_safe_component(key: &str) -> String {
    key.replace(':', "_")
}

/// ADR-045 Fase 2: `saves/players/world_{seed}/{key}.json` under the same root the world save
/// uses. `key` must already be sanitized (`persistence::sanitize_player_key`) — this function only
/// additionally guards the filesystem boundary (see `filesystem_safe_component`), it does not
/// re-validate the whitelist.
pub fn resolve_player_save_path(world_seed: u64, key: &str) -> PathBuf {
    player_save_root()
        .join("players")
        .join(format!("world_{world_seed}"))
        .join(format!("{}.json", filesystem_safe_component(key)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::utils::Vec3;

    fn scratch_path(name: &str) -> PathBuf {
        let mut p = std::env::temp_dir();
        p.push(format!("backrooms_adr045_player_{name}.json"));
        let _ = std::fs::remove_file(&p);
        let mut bak = p.as_os_str().to_owned();
        bak.push(".bak");
        let _ = std::fs::remove_file(PathBuf::from(bak));
        p
    }

    #[test]
    fn round_trip_save_load_matches_state() {
        let mut player = Player::new(1, "Joiner");
        player.stats.health = 61.0;
        player.position = Vec3::new(3.0, 1.8, -4.0);
        player.rotation = 45.0;
        player.equipment = [9, 0, 0, 7];
        player.held_item = 555;
        player.respawn_point = Some(Vec3::new(1.0, 1.8, 1.0));
        // ADR-069: la cama plantada y sin construir tiene que sobrevivir al ciclo de guardado —
        // "planto la cama, salgo, vuelvo y la termino" es la secuencia normal, no un caso raro.
        player.pending_respawn_point = Some(Vec3::new(2.0, 1.8, 2.0));

        let path = scratch_path("round_trip");
        save_player(&path, "uuid:1234-5678", &player).expect("save should succeed");

        let loaded = load_or_fresh(&path).expect("freshly written player file must load");
        assert_eq!(loaded.identity_key, "uuid:1234-5678");
        assert!((loaded.snapshot.stats.health - 61.0).abs() < 1e-4);
        assert_eq!(loaded.snapshot.position, Vec3::new(3.0, 1.8, -4.0));
        assert_eq!(loaded.snapshot.equipment, [9, 0, 0, 7]);
        assert_eq!(loaded.snapshot.held_item, 555);
        assert_eq!(
            loaded.snapshot.respawn_point,
            Some(Vec3::new(1.0, 1.8, 1.0))
        );
        assert_eq!(
            loaded.snapshot.pending_respawn_point,
            Some(Vec3::new(2.0, 1.8, 2.0))
        );

        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn corrupt_player_file_backs_up_and_returns_none() {
        let path = scratch_path("corrupt");
        std::fs::write(&path, b"{ not valid json ][").unwrap();

        assert!(
            load_or_fresh(&path).is_none(),
            "a corrupt player file must not crash — start that player fresh"
        );
        assert!(!path.exists(), "corrupt file should be renamed to .bak");
        let mut bak = path.as_os_str().to_owned();
        bak.push(".bak");
        let bak = PathBuf::from(bak);
        assert!(bak.exists());
        let _ = std::fs::remove_file(&bak);
    }

    #[test]
    fn incompatible_major_version_backs_up_and_returns_none() {
        let path = scratch_path("major_mismatch");
        let player = Player::new(1, "Joiner");
        let mut file = PlayerFile::from_player("uuid:x".into(), &player);
        file.version = "999.0.0".into();
        file.save_to(&path).unwrap();

        assert!(
            load_or_fresh(&path).is_none(),
            "major-version mismatch -> that player starts fresh"
        );
        assert!(!path.exists());
        let mut bak = path.as_os_str().to_owned();
        bak.push(".bak");
        let _ = std::fs::remove_file(PathBuf::from(bak));
    }

    #[test]
    fn missing_player_file_returns_none_without_side_effects() {
        let path = scratch_path("absent");
        assert!(!path.exists());
        assert!(load_or_fresh(&path).is_none());
        assert!(!path.exists());
    }

    #[test]
    fn resolve_player_save_path_places_file_under_players_world_seed_dir() {
        // A key with the mandatory ":" namespace prefix (ADR-045 point 2) — the case that would
        // silently misbehave on Windows (NTFS Alternate Data Stream syntax) without
        // `filesystem_safe_component`.
        let path = resolve_player_save_path(42, "uuid:abc-123");

        let components: Vec<String> = path
            .iter()
            .map(|c| c.to_string_lossy().to_string())
            .collect();
        let players_idx = components
            .iter()
            .position(|c| c == "players")
            .expect("path must contain a players/ segment");
        assert_eq!(
            components.get(players_idx + 1),
            Some(&"world_42".to_string()),
            "world_{{seed}} must sit directly under players/"
        );
        assert_eq!(
            path.file_name().unwrap().to_string_lossy(),
            "uuid_abc-123.json",
            "the ':' namespace separator must not survive into the filename"
        );
    }
}
