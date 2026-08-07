//! JSON save/load.
//! See ARCHITECTURE_V1.md §10 and ADR-032. Host-only world persistence: the seed is the
//! deterministic anchor (terrain + world_graph regenerate from it), so only the state a player
//! CHANGED over the generated world is persisted — buildings, loot rosters, corpses/chests, and
//! the host player's own snapshot.

use std::path::Path;

use log::warn;
use serde::{Deserialize, Serialize};

use crate::network::protocol::{
    StpBuildingInfo, StpCarryableInfo, StpHarvestableInfo, StpItemInfo,
};
use crate::player::inventory::Inventory;
use crate::player::stats::PlayerStats;
use crate::player::Player;
use crate::utils::Vec3;
use crate::world::corpse::{CorpseData, CorpseStack};
use crate::world::World;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SaveConfig {
    pub max_players: u16,
    pub teleport_interval_min: u32,
    pub teleport_interval_max: u32,
    pub entity_scaling: f32,
}

/// ADR-032: the host player's persisted slice. NOT the full `Player` — transient/cosmetic
/// fields (crouch/pitch/hit_seq/death_loot_reported), the network-derived `owned_chunks`, and the
/// per-session `uuid`/`id` are deliberately omitted; only durable survival/inventory state is kept.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PlayerSnapshot {
    pub stats: PlayerStats,
    pub position: Vec3,
    pub rotation: f32,
    pub inventory: Inventory,
    pub equipment: [i32; 4],
    pub held_item: i32,
    pub respawn_point: Option<Vec3>,
    /// ADR-032 amendment: the REAL STP inventory (client-reported via `report_inventory`,
    /// raw item ids). The `inventory` field above is the legacy disconnected model, kept
    /// intact only for shape stability — this is the one the game actually restores.
    /// `serde(default)` → pre-amendment saves load with an empty list.
    #[serde(default)]
    pub stp_inventory: Vec<CorpseStack>,
}

impl PlayerSnapshot {
    /// `pub(crate)`: also built by `persistence::player_save` (ADR-045 Fase 2) from a `Player`
    /// that has no `World`/STP rosters around it, so it cannot reuse `build_save`.
    pub(crate) fn from_player(p: &Player) -> Self {
        Self {
            stats: p.stats.clone(),
            position: p.position,
            rotation: p.rotation,
            inventory: p.inventory.clone(),
            equipment: p.equipment,
            held_item: p.held_item,
            respawn_point: p.respawn_point,
            stp_inventory: p.stp_inventory.clone(),
        }
    }
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

    // ADR-032 payloads. All `#[serde(default)]` so a metadata-only or older save (missing a
    // field) still parses — additive schema changes within the same major version are tolerated.
    #[serde(default)]
    pub host_player: Option<PlayerSnapshot>,
    #[serde(default)]
    pub corpses: Vec<CorpseData>,
    #[serde(default)]
    pub next_corpse_id: u32,
    #[serde(default)]
    pub stp_items: Vec<StpItemInfo>,
    #[serde(default)]
    pub stp_buildings: Vec<StpBuildingInfo>,
    #[serde(default)]
    pub stp_carryables: Vec<StpCarryableInfo>,
    #[serde(default)]
    pub stp_harvestables: Vec<StpHarvestableInfo>,
    /// P0-2: density multiplier for the phantom population draw, same precedence rule as
    /// `world_seed` — a loaded save wins over the launch-time env. `#[serde(default = "...")]`
    /// (not bare `default`) because f32's zero default would mean "no phantoms ever", not
    /// "unset"; a save written before this field existed must load as 1.0 (no scaling).
    #[serde(default = "default_phantom_density_scale")]
    pub phantom_density_scale: f32,
}

fn default_phantom_density_scale() -> f32 {
    1.0
}

/// ADR-032: metadatos que deben SOBREVIVIR a un ciclo de carga.
///
/// Los dos campos que transporta existen en el schema desde el primer día y ninguno decía la
/// verdad: `SaveFile::new` re-estampa `created_at` en CADA guardado (así que la fecha real de
/// creación del mundo se perdía en el primer autosave) y `play_time_seconds` se escribía como
/// literal `0`. No cambia el schema — cambia quién los escribe.
#[derive(Debug, Clone, Default)]
pub struct SaveMeta {
    /// Fecha de creación del mundo ORIGINAL. `None` = mundo nuevo → se estampa ahora.
    pub created_at: Option<String>,
    /// Total acumulado INCLUIDA la sesión en curso. Lo calcula el llamante, que es el único
    /// que sabe cuánto lleva corriendo (aquí no hay acceso al contador de ticks).
    pub play_time_seconds: u64,
}

impl SaveMeta {
    /// Continuidad a partir de un save recién cargado.
    pub fn from_loaded(save: &SaveFile) -> Self {
        Self {
            created_at: Some(save.created_at.clone()),
            play_time_seconds: save.play_time_seconds,
        }
    }
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
            host_player: None,
            corpses: Vec::new(),
            next_corpse_id: 1,
            stp_items: Vec::new(),
            stp_buildings: Vec::new(),
            stp_carryables: Vec::new(),
            stp_harvestables: Vec::new(),
            phantom_density_scale: 1.0,
        }
    }

    /// Serialize to pretty JSON and write to disk ATOMICALLY (write to `<path>.tmp`, then rename)
    /// so a crash mid-write never leaves a torn/corrupt save at `path`. Creates the parent dir if
    /// needed.
    pub fn save_to<P: AsRef<Path>>(&mut self, path: P) -> std::io::Result<()> {
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
        let tmp = std::path::PathBuf::from(tmp);
        std::fs::write(&tmp, json)?;
        std::fs::rename(&tmp, path)
    }

    /// Load and parse a save file from disk.
    pub fn load_from<P: AsRef<Path>>(path: P) -> std::io::Result<Self> {
        let json = std::fs::read_to_string(path)?;
        serde_json::from_str(&json)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))
    }
}

/// Major-version component of a semver string ("0.1.0" → "0"). Used to gate compatibility.
/// `pub(crate)`: `persistence::player_save` (ADR-045 Fase 2) reuses this exact gate instead of
/// duplicating it.
pub(crate) fn major_of(version: &str) -> &str {
    version.split('.').next().unwrap_or(version)
}

/// ADR-032 robustness: back up an unusable save so a fresh world can be started without losing the
/// old file. Best-effort — a failed rename is logged, never propagated. `pub(crate)`: reused by
/// `persistence::player_save` (ADR-045 Fase 2) for the same never-hard-fail contract.
pub(crate) fn backup_unusable(path: &Path) {
    let mut bak = path.as_os_str().to_owned();
    bak.push(".bak");
    let bak = std::path::PathBuf::from(bak);
    match std::fs::rename(path, &bak) {
        Ok(()) => warn!("ADR-032: moved unusable save to {}", bak.display()),
        Err(e) => warn!(
            "ADR-032: failed to back up unusable save {}: {e}",
            path.display()
        ),
    }
}

/// ADR-032: robust load. Returns `None` (→ start a fresh world) when the file is ABSENT,
/// UNPARSEABLE, or a MAJOR-version mismatch. NEVER hard-fails the server: a corrupt/incompatible
/// save is renamed to `<path>.bak` and the caller regenerates from the seed. Additive changes
/// within the same major parse fine (every payload field is `#[serde(default)]`).
pub fn load_or_fresh<P: AsRef<Path>>(path: P) -> Option<SaveFile> {
    let path = path.as_ref();
    if !path.exists() {
        return None;
    }
    match SaveFile::load_from(path) {
        Ok(save) => {
            let current = env!("CARGO_PKG_VERSION");
            if major_of(&save.version) != major_of(current) {
                warn!(
                    "ADR-032: save major version {} incompatible with current {}; starting fresh",
                    save.version, current
                );
                backup_unusable(path);
                return None;
            }
            Some(save)
        }
        Err(e) => {
            warn!(
                "ADR-032: could not parse save at {} ({e}); starting fresh",
                path.display()
            );
            backup_unusable(path);
            None
        }
    }
}

/// ADR-032: collect the host-authoritative world + host player into a `SaveFile`. Takes slices for
/// the STP rosters (rather than `&NetworkManager`) so it stays decoupled and unit-testable.
#[allow(clippy::too_many_arguments)]
pub fn build_save(
    session_name: &str,
    world: &World,
    player: &Player,
    meta: &SaveMeta,
    stp_items: &[StpItemInfo],
    stp_buildings: &[StpBuildingInfo],
    stp_carryables: &[StpCarryableInfo],
    stp_harvestables: &[StpHarvestableInfo],
    phantom_density_scale: f32,
) -> SaveFile {
    let mut corpses: Vec<CorpseData> = world.corpses.values().cloned().collect();
    // Stable ordering for a deterministic file (mirrors visible_corpse_views' sort rationale).
    corpses.sort_unstable_by_key(|c| c.id);

    let mut save = SaveFile::new(session_name, world.seed);
    // `SaveFile::new` estampa `created_at = now`, que es lo correcto para un mundo NUEVO y lo
    // incorrecto para uno cargado: se sobrescribe con la fecha original cuando la hay.
    if let Some(created_at) = &meta.created_at {
        save.created_at = created_at.clone();
    }
    save.play_time_seconds = meta.play_time_seconds;
    save.host_player = Some(PlayerSnapshot::from_player(player));
    save.corpses = corpses;
    save.next_corpse_id = world.next_corpse_id();
    save.stp_items = stp_items.to_vec();
    save.stp_buildings = stp_buildings.to_vec();
    save.stp_carryables = stp_carryables.to_vec();
    save.stp_harvestables = stp_harvestables.to_vec();
    save.phantom_density_scale = phantom_density_scale;
    save
}

/// ADR-032: build + write the save (host-only). Atomic write via `SaveFile::save_to`.
#[allow(clippy::too_many_arguments)]
pub fn save_world<P: AsRef<Path>>(
    path: P,
    session_name: &str,
    world: &World,
    player: &Player,
    meta: &SaveMeta,
    stp_items: &[StpItemInfo],
    stp_buildings: &[StpBuildingInfo],
    stp_carryables: &[StpCarryableInfo],
    stp_harvestables: &[StpHarvestableInfo],
    phantom_density_scale: f32,
) -> std::io::Result<()> {
    let mut save = build_save(
        session_name,
        world,
        player,
        meta,
        stp_items,
        stp_buildings,
        stp_carryables,
        stp_harvestables,
        phantom_density_scale,
    );
    save.save_to(path)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::corpse::CorpseStack;

    fn scratch_path(name: &str) -> std::path::PathBuf {
        let mut p = std::env::temp_dir();
        // Unique-ish per test name; the round-trip test cleans up after itself.
        p.push(format!("backrooms_adr032_{name}.json"));
        let _ = std::fs::remove_file(&p);
        let mut bak = p.as_os_str().to_owned();
        bak.push(".bak");
        let _ = std::fs::remove_file(std::path::PathBuf::from(bak));
        p
    }

    fn seeded_world() -> World {
        let mut world = World::new(42);
        // A real corpse and a world chest, so is_chest round-trips too.
        world.spawn_corpse(
            7,
            "Joel".into(),
            Vec3::new(10.0, 1.8, 20.0),
            [101, 202, 303, 404],
            -55,
            vec![CorpseStack {
                item_id: -12345,
                quantity: 3,
            }],
        );
        world.spawn_chest(
            Vec3::new(5.0, 1.8, 5.0),
            vec![CorpseStack {
                item_id: 9692212,
                quantity: 1,
            }],
        );
        world
    }

    #[test]
    fn round_trip_save_load_matches_state() {
        let world = seeded_world();
        let mut player = Player::new(1, "Host");
        player.stats.health = 73.0;
        player.stats.hunger = 41.0;
        player.position = Vec3::new(12.0, 1.8, -8.0);
        player.rotation = 90.0;
        player.equipment = [1, 2, 3, 4];
        player.held_item = 9692212;
        player.respawn_point = Some(Vec3::new(5.0, 1.8, 5.0));
        player.stp_inventory = vec![
            CorpseStack {
                item_id: -52379,
                quantity: 2,
            },
            CorpseStack {
                item_id: 3621376,
                quantity: 30,
            },
        ];

        let items = vec![StpItemInfo {
            id: 1,
            def_id: -52379,
            count: 2,
            position: [1.0, 0.0, 1.0],
            rotation: 0.0,
        }];
        let buildings = vec![StpBuildingInfo {
            id: 10,
            def_id: 5498433,
            position: [2.0, 0.0, 2.0],
            rotation: 90.0,
            group_id: 0,
            added: vec![],
        }];
        let carryables = vec![StpCarryableInfo {
            id: 20,
            def_id: 111,
            position: [3.0, 0.0, 3.0],
            rotation: 0.0,
        }];
        let harvestables = vec![StpHarvestableInfo {
            id: 30,
            position: [4.0, 0.0, 4.0],
            remaining: 55.5,
        }];

        let path = scratch_path("round_trip");
        save_world(
            &path,
            "test-session",
            &world,
            &player,
            &SaveMeta::default(),
            &items,
            &buildings,
            &carryables,
            &harvestables,
            2.5,
        )
        .expect("save should succeed");

        let loaded = load_or_fresh(&path).expect("freshly written save must load");
        assert_eq!(loaded.world_seed, 42);
        // P0-2: persists alongside world_seed, same precedence rule.
        assert!((loaded.phantom_density_scale - 2.5).abs() < 1e-4);
        assert_eq!(loaded.corpses.len(), 2);
        assert!(loaded.corpses.iter().any(|c| c.is_chest));
        assert!(loaded
            .corpses
            .iter()
            .any(|c| !c.is_chest && c.owner_name == "Joel"));
        assert_eq!(loaded.next_corpse_id, world.next_corpse_id());
        assert_eq!(loaded.stp_items.len(), 1);
        assert_eq!(loaded.stp_buildings[0].def_id, 5498433);
        assert_eq!(loaded.stp_carryables[0].id, 20);
        assert!((loaded.stp_harvestables[0].remaining - 55.5).abs() < 1e-4);

        let hp = loaded.host_player.expect("host player must persist");
        assert!((hp.stats.health - 73.0).abs() < 1e-4);
        assert!((hp.stats.hunger - 41.0).abs() < 1e-4);
        assert_eq!(hp.equipment, [1, 2, 3, 4]);
        assert_eq!(hp.held_item, 9692212);
        assert_eq!(hp.respawn_point, Some(Vec3::new(5.0, 1.8, 5.0)));
        // ADR-032 amendment: the REAL STP inventory round-trips identically.
        assert_eq!(
            hp.stp_inventory,
            vec![
                CorpseStack {
                    item_id: -52379,
                    quantity: 2
                },
                CorpseStack {
                    item_id: 3621376,
                    quantity: 30
                },
            ]
        );

        let _ = std::fs::remove_file(&path);
    }

    // ADR-032 amendment: a save written BEFORE stp_inventory existed (host_player present but
    // no such key) must load with an empty default — never a parse error.
    #[test]
    fn pre_amendment_save_without_stp_inventory_loads_with_empty_default() {
        let path = scratch_path("pre_amendment_host_player");
        let json = r#"{
            "version": "0.1.0",
            "world_seed": 42,
            "session_name": "old",
            "created_at": "2026-01-01T00:00:00Z",
            "last_saved": "2026-01-01T00:00:00Z",
            "play_time_seconds": 0,
            "config": { "max_players": 50, "teleport_interval_min": 120, "teleport_interval_max": 600, "entity_scaling": 1.0 },
            "host_player": {
                "stats": { "health": 80.0, "hunger": 50.0, "thirst": 50.0, "sanity": 50.0, "stamina": 100.0,
                           "speed_modifier": 1.0, "accuracy_modifier": 1.0, "hallucination_intensity": 0.0 },
                "position": { "x": 1.0, "y": 1.8, "z": 2.0 },
                "rotation": 90.0,
                "inventory": { "slots": [] },
                "equipment": [0, 0, 0, 0],
                "held_item": 0,
                "respawn_point": null
            }
        }"#;
        std::fs::write(&path, json).unwrap();

        let loaded = load_or_fresh(&path).expect("pre-amendment save with host_player must parse");
        let hp = loaded.host_player.expect("host_player present");
        assert!(
            hp.stp_inventory.is_empty(),
            "missing stp_inventory must default to empty"
        );
        assert!((hp.stats.health - 80.0).abs() < 1e-4);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn corrupt_save_backs_up_and_returns_none() {
        let path = scratch_path("corrupt");
        std::fs::write(&path, b"{ this is not valid json ][").unwrap();

        assert!(
            load_or_fresh(&path).is_none(),
            "a corrupt save must not crash — start fresh"
        );
        // Original was moved aside, not left in place.
        assert!(!path.exists(), "corrupt save should be renamed to .bak");
        let mut bak = path.as_os_str().to_owned();
        bak.push(".bak");
        let bak = std::path::PathBuf::from(bak);
        assert!(bak.exists(), "corrupt save should be preserved as .bak");
        let _ = std::fs::remove_file(&bak);
    }

    #[test]
    fn incompatible_major_version_backs_up_and_returns_none() {
        let path = scratch_path("major_mismatch");
        let mut save = SaveFile::new("x", 42);
        // Force a major-version bump that will never match the current 0.x crate.
        save.version = "999.0.0".into();
        save.save_to(&path).unwrap();

        assert!(
            load_or_fresh(&path).is_none(),
            "major-version mismatch → fresh world"
        );
        assert!(!path.exists());
        let mut bak = path.as_os_str().to_owned();
        bak.push(".bak");
        let _ = std::fs::remove_file(std::path::PathBuf::from(bak));
    }

    #[test]
    fn back_to_back_saves_are_idempotent_last_write_wins() {
        // ADR-032: the save-on-quit path (immediate save) may fire moments after a timer autosave
        // to the SAME file. Two back-to-back saves must never corrupt it — the second simply wins.
        let world = seeded_world();
        let mut player = Player::new(1, "Host");
        let path = scratch_path("double_save");

        player.stats.health = 50.0;
        save_world(
            &path,
            "s",
            &world,
            &player,
            &SaveMeta::default(),
            &[],
            &[],
            &[],
            &[],
            1.0,
        )
        .expect("first save");

        player.stats.health = 88.0; // state changed between the two saves
        save_world(
            &path,
            "s",
            &world,
            &player,
            &SaveMeta::default(),
            &[],
            &[],
            &[],
            &[],
            1.0,
        )
        .expect("second save");

        let loaded = load_or_fresh(&path).expect("file must still be valid after a double save");
        let hp = loaded.host_player.expect("host player present");
        assert!(
            (hp.stats.health - 88.0).abs() < 1e-4,
            "last write must win, got {}",
            hp.stats.health
        );
        let _ = std::fs::remove_file(&path);
    }

    /// `created_at` se re-estampaba en CADA guardado (`SaveFile::new`), asi que la fecha real de
    /// creacion del mundo moria en el primer autosave, y `play_time_seconds` se escribia literal
    /// `0`. Ambos campos ya existian en el schema; lo que cambia es quien los escribe.
    #[test]
    fn save_meta_carries_creation_date_and_accumulated_play_time() {
        let world = seeded_world();
        let player = Player::new(1, "Host");
        let path = scratch_path("save_meta");

        // Guardado 1: mundo NUEVO. `created_at` se estampa ahora, tiempo jugado 0.
        save_world(
            &path,
            "s",
            &world,
            &player,
            &SaveMeta::default(),
            &[],
            &[],
            &[],
            &[],
            1.0,
        )
        .expect("first save");
        let first = load_or_fresh(&path).expect("first save must load");
        assert!(
            !first.created_at.is_empty(),
            "un mundo nuevo debe estampar su fecha de creacion"
        );
        assert_eq!(first.play_time_seconds, 0);

        // Guardado 2: continuidad desde el cargado + 300 s de sesion.
        let mut meta = SaveMeta::from_loaded(&first);
        meta.play_time_seconds += 300;
        save_world(&path, "s", &world, &player, &meta, &[], &[], &[], &[], 1.0)
            .expect("second save");

        let second = load_or_fresh(&path).expect("second save must load");
        assert_eq!(
            second.created_at, first.created_at,
            "la fecha de creacion debe sobrevivir a un ciclo guardar/cargar"
        );
        assert_eq!(second.play_time_seconds, 300);

        // Guardado 3: el tiempo ACUMULA en vez de reiniciarse cada sesion.
        let mut meta = SaveMeta::from_loaded(&second);
        meta.play_time_seconds += 120;
        save_world(&path, "s", &world, &player, &meta, &[], &[], &[], &[], 1.0)
            .expect("third save");

        let third = load_or_fresh(&path).expect("third save must load");
        assert_eq!(third.play_time_seconds, 420);
        assert_eq!(third.created_at, first.created_at);
        // Contrapartida: `last_saved` NO se congela — es la fecha del guardado, no la de creacion.
        assert_ne!(
            third.last_saved, third.created_at,
            "last_saved debe seguir avanzando aunque created_at se preserve"
        );

        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn missing_save_returns_none_without_side_effects() {
        let path = scratch_path("absent");
        assert!(!path.exists());
        assert!(load_or_fresh(&path).is_none());
        assert!(!path.exists());
    }

    #[test]
    fn additive_field_defaults_when_absent_in_json() {
        // A metadata-only save (pre-ADR-032 shape) must still parse, with empty payloads.
        let path = scratch_path("metadata_only");
        let json = r#"{
            "version": "0.1.0",
            "world_seed": 42,
            "session_name": "old",
            "created_at": "2026-01-01T00:00:00Z",
            "last_saved": "2026-01-01T00:00:00Z",
            "play_time_seconds": 0,
            "config": { "max_players": 50, "teleport_interval_min": 120, "teleport_interval_max": 600, "entity_scaling": 1.0 }
        }"#;
        std::fs::write(&path, json).unwrap();

        let loaded = load_or_fresh(&path).expect("metadata-only save must parse");
        assert!(loaded.host_player.is_none());
        assert!(loaded.corpses.is_empty());
        assert_eq!(loaded.next_corpse_id, 0);
        assert!(loaded.stp_buildings.is_empty());
        let _ = std::fs::remove_file(&path);
    }
}
