//! ADR-154 D5: el fichero de hojas de mapa del host, aparte del save del mundo y con el mismo
//! contrato de robustez que `player_save.rs` (ADR-045): escritura ATÓMICA (tmp + rename), `.bak`
//! si el fichero no sirve, y nunca tumba al que llama.
//!
//! Va en JSON COMPACTO, no pretty como el mundo: cada hoja es el registro del golden común y el
//! tope global del ADR (8 MB) se cuenta en bytes compactos.
//!
//! El contador también vive en `SaveFile::next_map_sheet_id`. Si este fichero se pierde, el del
//! mundo impide reutilizar ids que ya nombran items en inventarios guardados.

use std::path::{Path, PathBuf};

use log::warn;
use serde::{Deserialize, Serialize};

use crate::persistence::save::{backup_unusable, major_of};
use crate::world::map_sheets::{MapSheetRecord, MapSheetStore};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MapSheetsFile {
    pub version: String,
    pub next_sheet_id: u64,
    pub sheets: Vec<MapSheetRecord>,
}

/// `<carpeta del save>/map_sheets/<nombre del save>`: sigue a `SAVE_PATH` y a la clave del mundo
/// (`world_{seed}.json`), así que dos mundos distintos no comparten hojas salvo que ya compartan save.
pub fn sheets_path(world_save_path: &Path) -> PathBuf {
    let name = world_save_path
        .file_name()
        .map(|n| n.to_os_string())
        .unwrap_or_else(|| "map_sheets.json".into());
    match world_save_path.parent() {
        Some(parent) if !parent.as_os_str().is_empty() => parent.join("map_sheets").join(name),
        _ => Path::new("map_sheets").join(name),
    }
}

/// Escribe el almacén entero, atómico. No toca `dirty`: eso lo decide el que llama al ver `Ok`.
pub fn save_store(path: &Path, store: &MapSheetStore) -> std::io::Result<()> {
    let file = MapSheetsFile {
        version: env!("CARGO_PKG_VERSION").to_string(),
        next_sheet_id: store.next_sheet_id(),
        sheets: store.all(),
    };
    let json = serde_json::to_string(&file).map_err(std::io::Error::other)?;

    if let Some(parent) = path.parent() {
        if !parent.as_os_str().is_empty() {
            std::fs::create_dir_all(parent)?;
        }
    }

    let mut tmp = path.as_os_str().to_owned();
    tmp.push(format!(".{}.tmp", std::process::id()));
    let tmp = PathBuf::from(tmp);
    std::fs::write(&tmp, json)?;
    std::fs::rename(&tmp, path)
}

/// Mismo contrato que `save::load_or_fresh`: ausente, ilegible o de otra versión mayor ⇒ `None`
/// (y lo inservible se aparta a `.bak`).
pub fn load_or_fresh(path: &Path) -> Option<MapSheetsFile> {
    if !path.exists() {
        return None;
    }
    let loaded = std::fs::read_to_string(path).and_then(|json| {
        serde_json::from_str::<MapSheetsFile>(&json)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))
    });
    match loaded {
        Ok(file) => {
            let current = env!("CARGO_PKG_VERSION");
            if major_of(&file.version) != major_of(current) {
                warn!(
                    "ADR-154: fichero de hojas {} de versión mayor {} incompatible con {current}; se empieza sin hojas",
                    path.display(),
                    file.version
                );
                backup_unusable(path);
                return None;
            }
            Some(file)
        }
        Err(e) => {
            warn!(
                "ADR-154: fichero de hojas {} ilegible ({e}); se aparta a .bak y se empieza sin hojas",
                path.display()
            );
            backup_unusable(path);
            None
        }
    }
}

/// El almacén del host al arrancar. `world_next_id` es el contador que guardó el save del mundo:
/// entra en el `max` aunque el fichero de hojas falte (ADR-154 D5).
pub fn load_store(path: &Path, world_next_id: u64) -> MapSheetStore {
    let (sheets, saved_next) = match load_or_fresh(path) {
        Some(file) => (file.sheets, file.next_sheet_id),
        None => (Vec::new(), 0),
    };
    let (store, dropped) = MapSheetStore::from_saved(sheets, saved_next, world_next_id);
    if dropped > 0 {
        warn!("ADR-154: {dropped} hojas del fichero descartadas (no validan o pasan los topes)");
    }
    store
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::map_sheets::{MapSheetDelta, NewLayer};

    fn scratch_dir(name: &str) -> PathBuf {
        let mut dir = std::env::temp_dir();
        dir.push(format!("backrooms_adr154_{name}_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        dir
    }

    fn store_with_two_sheets() -> MapSheetStore {
        let mut store = MapSheetStore::new();
        let (a, _) = store.create(None).unwrap();
        store.create(None).unwrap();
        store
            .append(
                a,
                MapSheetDelta {
                    layers: vec![NewLayer {
                        pen_argb: 0xF22A_47A8,
                        width_cpx: 300,
                        runs: vec![[0, -150, 140, 152, 0, 0], [1, 160, -195, -190, 1, 2]],
                    }],
                    ..MapSheetDelta::default()
                },
            )
            .unwrap();
        store
    }

    #[test]
    fn sheets_file_round_trips() {
        let dir = scratch_dir("round_trip");
        let path = sheets_path(&dir.join("world_42.json"));
        let store = store_with_two_sheets();

        save_store(&path, &store).unwrap();
        let loaded = load_store(&path, 0);

        assert_eq!(loaded.all(), store.all());
        assert_eq!(loaded.next_sheet_id(), store.next_sheet_id());
        assert!(!loaded.is_dirty(), "recién cargado no hay nada que guardar");
        let json = std::fs::read_to_string(&path).unwrap();
        assert!(!json.contains('\n'), "JSON compacto");
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn unusable_file_goes_to_bak_and_starts_fresh() {
        let dir = scratch_dir("unusable");
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("map_sheets.json");
        std::fs::write(&path, "{ esto no es un fichero de hojas").unwrap();

        let store = load_store(&path, 7);

        assert!(store.is_empty());
        assert_eq!(
            store.next_sheet_id(),
            7,
            "el contador del mundo sigue mandando"
        );
        assert!(!path.exists(), "lo ilegible se aparta");
        let mut bak = path.as_os_str().to_owned();
        bak.push(".bak");
        assert!(PathBuf::from(bak).exists());
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn ids_survive_losing_the_sheets_file() {
        let dir = scratch_dir("lost_file");
        let path = sheets_path(&dir.join("world_42.json"));
        let store = store_with_two_sheets();
        save_store(&path, &store).unwrap();
        let world_counter = store.next_sheet_id();

        std::fs::remove_file(&path).unwrap();
        let mut reloaded = load_store(&path, world_counter);

        assert!(reloaded.is_empty());
        let (id, _) = reloaded.create(None).unwrap();
        assert_eq!(id, world_counter, "no se reutiliza ningún id ya dado");
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn sheets_path_follows_save_path() {
        assert_eq!(
            sheets_path(Path::new("./saves/world_42.json")),
            Path::new("./saves/map_sheets/world_42.json")
        );
        assert_eq!(
            sheets_path(Path::new("/x/foo.json")),
            Path::new("/x/map_sheets/foo.json")
        );
        assert_eq!(
            sheets_path(Path::new("foo.json")),
            Path::new("map_sheets/foo.json")
        );
    }

    #[test]
    fn world_save_keeps_the_sheet_counter() {
        let dir = scratch_dir("world_counter");
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("world_42.json");
        let mut save = crate::persistence::save::SaveFile::new("s", 42);
        save.next_map_sheet_id = 31;
        save.save_to(&path).unwrap();

        let loaded = crate::persistence::save::SaveFile::load_from(&path).unwrap();
        assert_eq!(loaded.next_map_sheet_id, 31);
        assert_eq!(
            crate::persistence::save::SaveMeta::from_loaded(&loaded).next_map_sheet_id,
            31
        );

        // Un save anterior a ADR-154 (sin la clave) carga con 0.
        let mut old: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        assert!(old
            .as_object_mut()
            .unwrap()
            .remove("next_map_sheet_id")
            .is_some());
        std::fs::write(&path, serde_json::to_string(&old).unwrap()).unwrap();
        assert_eq!(
            crate::persistence::save::SaveFile::load_from(&path)
                .unwrap()
                .next_map_sheet_id,
            0
        );
        let _ = std::fs::remove_dir_all(dir);
    }
}
