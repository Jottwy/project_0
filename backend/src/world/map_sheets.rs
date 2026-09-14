//! ADR-154 — hojas de mapa dibujadas como objetos: el modelo del registro y el almacén del host.
//!
//! Este módulo es SOLO el modelo: la forma del dato, sus topes y el almacén. No sabe de wire ni
//! de ficheros — el guardado vive en `persistence::map_sheets_save` y los mensajes `map_sheet_*`
//! llegan en P1c.
//!
//! Tres invariantes que el resto del sistema da por hechos:
//!
//! 1. **El JSON es el mismo que el de C#, byte a byte** (ADR-154 D5). Los campos se declaran en
//!    el orden del golden común `tools/dev/fixtures/map_sheet_record.golden.json` y todo son
//!    enteros (posiciones en centésimas de celda, grosor en centésimas de píxel), así que
//!    `serde_json::to_string` escribe exactamente lo que escribe `MapSheetRecord.ToJson` en
//!    Unity. `golden_writes_byte_for_byte` es la puerta.
//! 2. **Nada se reordena** (ADR-154 D2). Las capas guardan su `layer_key` y los tramos su
//!    `draw_index`: con ellos el cliente rehace el MISMO trazo a mano. El almacén asigna claves
//!    crecientes y añade al final; nunca ordena capas ni tramos. Las hojas viven en un
//!    `BTreeMap`, así que recorrerlas sale en orden de id (regla 13).
//! 3. **Todo tope se valida antes de tocar** (ADR-154 D2). Un cambio que pasaría un tope se
//!    rechaza entero y la hoja queda como estaba: el tope es la frontera del tamaño del save.

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};

/// Topes por hoja (ADR-154 D2).
pub const MAX_LAYERS: usize = 32;
pub const MAX_RUNS: usize = 1500;
pub const MAX_LINKS: usize = 16;
pub const MAX_MARKS: usize = 64;
pub const MAX_LABEL_CHARS: usize = 64;

/// Topes globales (ADR-154 D2): hojas y bytes del JSON compacto de todas ellas.
pub const MAX_SHEETS: usize = 2000;
pub const MAX_TOTAL_BYTES: usize = 8 * 1024 * 1024;

/// Un chunk en una planta: la unidad de una hoja.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct MapZone {
    pub chunk_x: i32,
    pub chunk_z: i32,
    pub storey: i32,
}

/// Una capa: todo lo dibujado de una vez con una misma herramienta.
///
/// Cada tramo es `[vertical, line, from, to, old, draw_index]` en celdas de MUNDO, como
/// `MapRun` en C#. Array plano para abaratar el JSON (~30 B por tramo).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MapSheetLayer {
    pub layer_key: i32,
    pub pen_argb: u32,
    pub width_cpx: i32,
    pub runs: Vec<[i32; 6]>,
}

/// Flecha de borde: de esta zona se pasa a `to` por `side`, en centésimas de celda locales.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct MapSheetLink {
    pub to: MapZone,
    pub side: u8,
    pub x_cc: i32,
    pub z_cc: i32,
}

/// Marca de tinta («estás aquí»), en centésimas de celda locales.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct MapSheetMark {
    pub kind: u8,
    pub x_cc: i32,
    pub z_cc: i32,
    pub argb: u32,
}

/// La hoja guardada como FOTO de lo dibujado (ADR-154 D2). El orden de los campos es parte del
/// formato: es el del golden común.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MapSheetRecord {
    pub id: u64,
    pub rev: u32,
    pub zone: Option<MapZone>,
    pub seed: u32,
    pub clean: bool,
    pub label: String,
    pub layers: Vec<MapSheetLayer>,
    pub links: Vec<MapSheetLink>,
    pub marks: Vec<MapSheetMark>,
}

/// Una capa nueva tal como llega del cliente: sin `layer_key`, que lo pone el host.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NewLayer {
    pub pen_argb: u32,
    pub width_cpx: i32,
    pub runs: Vec<[i32; 6]>,
}

/// Lo que añade un dibujo (ADR-154 D4, `map_sheet_append`). Solo se añade: nunca se borra.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct MapSheetDelta {
    pub layers: Vec<NewLayer>,
    pub links: Vec<MapSheetLink>,
    pub marks: Vec<MapSheetMark>,
}

/// Motivo de un rechazo; `reason()` es la cadena de `map_sheet_rejected`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Reject {
    /// El backend no es host: sus hojas no se guardarían (ADR-154 D3).
    NotHost,
    UnknownId,
    /// Un tope por hoja o global, con el detalle para el log.
    Cap(String),
}

impl Reject {
    pub fn reason(&self) -> &'static str {
        match self {
            Reject::NotHost => "not_host",
            Reject::UnknownId => "unknown_id",
            Reject::Cap(_) => "cap",
        }
    }
}

impl MapSheetRecord {
    /// Los topes por hoja y la forma de los tramos. `Err` con el detalle; nunca recorta.
    pub fn validate(&self) -> Result<(), Reject> {
        if self.layers.len() > MAX_LAYERS {
            return Err(Reject::Cap(format!(
                "capas {} > {MAX_LAYERS}",
                self.layers.len()
            )));
        }
        if self.links.len() > MAX_LINKS {
            return Err(Reject::Cap(format!(
                "enlaces {} > {MAX_LINKS}",
                self.links.len()
            )));
        }
        if self.marks.len() > MAX_MARKS {
            return Err(Reject::Cap(format!(
                "marcas {} > {MAX_MARKS}",
                self.marks.len()
            )));
        }
        let label_chars = self.label.chars().count();
        if label_chars > MAX_LABEL_CHARS {
            return Err(Reject::Cap(format!(
                "etiqueta de {label_chars} caracteres > {MAX_LABEL_CHARS}"
            )));
        }

        let mut runs = 0usize;
        let mut previous_key: Option<i32> = None;
        for layer in &self.layers {
            if previous_key.is_some_and(|key| layer.layer_key <= key) {
                return Err(Reject::Cap(format!(
                    "clave de capa {} no creciente",
                    layer.layer_key
                )));
            }
            previous_key = Some(layer.layer_key);
            for run in &layer.runs {
                let [vertical, _line, from, to, old, draw_index] = *run;
                if !(0..=1).contains(&vertical) || !(0..=1).contains(&old) {
                    return Err(Reject::Cap(format!("tramo con banderas no 0/1: {run:?}")));
                }
                if to <= from {
                    return Err(Reject::Cap(format!("tramo vacío {from}..{to}")));
                }
                if draw_index < 0 {
                    return Err(Reject::Cap(format!("draw_index negativo en {run:?}")));
                }
            }
            runs += layer.runs.len();
        }
        if runs > MAX_RUNS {
            return Err(Reject::Cap(format!("tramos {runs} > {MAX_RUNS}")));
        }
        Ok(())
    }

    /// Bytes del JSON compacto: la unidad del tope global.
    pub fn json_len(&self) -> usize {
        serde_json::to_string(self)
            .map(|json| json.len())
            .unwrap_or(usize::MAX)
    }
}

/// Semilla del trazo a mano de una hoja, fija para su id (ADR-154 D2: la pone el host al crearla).
/// SplitMix64 y los 32 bits altos: cualquier id da una semilla bien repartida y siempre la misma.
pub fn seed_for(id: u64) -> u32 {
    let mut z = id.wrapping_add(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^= z >> 31;
    (z >> 32) as u32
}

/// El almacén del host: `sheet_id → registro`, con el siguiente id y el total de bytes.
#[derive(Debug)]
pub struct MapSheetStore {
    sheets: BTreeMap<u64, MapSheetRecord>,
    sizes: BTreeMap<u64, usize>,
    next_sheet_id: u64,
    total_bytes: usize,
    dirty: bool,
}

impl Default for MapSheetStore {
    fn default() -> Self {
        Self::new()
    }
}

impl MapSheetStore {
    pub fn new() -> Self {
        Self {
            sheets: BTreeMap::new(),
            sizes: BTreeMap::new(),
            next_sheet_id: 1,
            total_bytes: 0,
            dirty: false,
        }
    }

    /// Rehace el almacén de un guardado. Descarta (y cuenta) las hojas que no validan o no caben
    /// en los topes globales. `next_sheet_id = max(fichero, save del mundo, id máximo + 1, 1)`:
    /// si el fichero de hojas se perdió, el contador del mundo evita reutilizar ids que ya
    /// nombran items (ADR-154 D5).
    pub fn from_saved(
        sheets: Vec<MapSheetRecord>,
        saved_next_id: u64,
        world_next_id: u64,
    ) -> (Self, usize) {
        let mut store = Self::new();
        let mut dropped = 0usize;
        let mut max_id = 0u64;
        for sheet in sheets {
            max_id = max_id.max(sheet.id);
            let size = sheet.json_len();
            let fits = store.sheets.len() < MAX_SHEETS
                && store.total_bytes.saturating_add(size) <= MAX_TOTAL_BYTES
                && !store.sheets.contains_key(&sheet.id);
            if sheet.validate().is_err() || !fits {
                dropped += 1;
                continue;
            }
            store.total_bytes += size;
            store.sizes.insert(sheet.id, size);
            store.sheets.insert(sheet.id, sheet);
        }
        store.next_sheet_id = saved_next_id
            .max(world_next_id)
            .max(max_id.saturating_add(1))
            .max(1);
        (store, dropped)
    }

    pub fn len(&self) -> usize {
        self.sheets.len()
    }

    pub fn is_empty(&self) -> bool {
        self.sheets.is_empty()
    }

    pub fn next_sheet_id(&self) -> u64 {
        self.next_sheet_id
    }

    pub fn total_bytes(&self) -> usize {
        self.total_bytes
    }

    /// Hay cambios sin guardar: el autosave solo escribe si es así.
    pub fn is_dirty(&self) -> bool {
        self.dirty
    }

    /// El guardado salió bien.
    pub fn mark_saved(&mut self) {
        self.dirty = false;
    }

    pub fn get(&self, id: u64) -> Option<&MapSheetRecord> {
        self.sheets.get(&id)
    }

    /// Todas las hojas en orden de id, para guardar.
    pub fn all(&self) -> Vec<MapSheetRecord> {
        self.sheets.values().cloned().collect()
    }

    /// Hoja nueva en blanco. Devuelve su id y su semilla. El id solo se consume si se crea.
    pub fn create(&mut self, zone: Option<MapZone>) -> Result<(u64, u32), Reject> {
        if self.sheets.len() >= MAX_SHEETS {
            return Err(Reject::Cap(format!(
                "hojas {} >= {MAX_SHEETS}",
                self.sheets.len()
            )));
        }
        let id = self.next_sheet_id;
        let record = MapSheetRecord {
            id,
            rev: 0,
            zone,
            seed: seed_for(id),
            clean: false,
            label: String::new(),
            layers: Vec::new(),
            links: Vec::new(),
            marks: Vec::new(),
        };
        let size = record.json_len();
        if self.total_bytes.saturating_add(size) > MAX_TOTAL_BYTES {
            return Err(Reject::Cap(format!(
                "bytes {} + {size} > {MAX_TOTAL_BYTES}",
                self.total_bytes
            )));
        }
        let seed = record.seed;
        self.next_sheet_id = id.saturating_add(1);
        self.total_bytes += size;
        self.sizes.insert(id, size);
        self.sheets.insert(id, record);
        self.dirty = true;
        Ok((id, seed))
    }

    /// Añade un dibujo a la hoja `id`. Asigna `layer_key` crecientes a las capas nuevas y sube
    /// `rev`. Si el resultado pasaría un tope, no cambia NADA. Devuelve `rev` y las claves.
    pub fn append(&mut self, id: u64, delta: MapSheetDelta) -> Result<(u32, Vec<i32>), Reject> {
        let current = self.sheets.get(&id).ok_or(Reject::UnknownId)?;
        let mut next = current.clone();
        let mut key = next.layers.last().map_or(0, |layer| layer.layer_key + 1);
        let mut keys = Vec::with_capacity(delta.layers.len());
        for layer in delta.layers {
            next.layers.push(MapSheetLayer {
                layer_key: key,
                pen_argb: layer.pen_argb,
                width_cpx: layer.width_cpx,
                runs: layer.runs,
            });
            keys.push(key);
            key += 1;
        }
        next.links.extend(delta.links);
        next.marks.extend(delta.marks);
        next.rev = next.rev.wrapping_add(1);
        next.validate()?;

        let old_size = self.sizes.get(&id).copied().unwrap_or(0);
        let size = next.json_len();
        let total = self.total_bytes - old_size + size;
        if total > MAX_TOTAL_BYTES {
            return Err(Reject::Cap(format!("bytes {total} > {MAX_TOTAL_BYTES}")));
        }
        let rev = next.rev;
        self.total_bytes = total;
        self.sizes.insert(id, size);
        self.sheets.insert(id, next);
        self.dirty = true;
        Ok((rev, keys))
    }
}

/// `map_sheet_create` (P1c): solo el host guarda hojas (ADR-154 D3).
pub fn handle_create(
    is_host: bool,
    store: &mut MapSheetStore,
    zone: Option<MapZone>,
) -> Result<(u64, u32), Reject> {
    if !is_host {
        return Err(Reject::NotHost);
    }
    store.create(zone)
}

#[cfg(test)]
mod tests {
    use super::*;

    const GOLDEN: &str = concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../tools/dev/fixtures/map_sheet_record.golden.json"
    );

    fn golden() -> String {
        std::fs::read_to_string(GOLDEN)
            .expect("golden común de ADR-154")
            .trim_end_matches(['\r', '\n'])
            .to_string()
    }

    fn zone(chunk_x: i32, chunk_z: i32, storey: i32) -> MapZone {
        MapZone {
            chunk_x,
            chunk_z,
            storey,
        }
    }

    /// El mismo registro que `MapSheetRecordTests.Handmade` en C#, escrito a mano.
    fn handmade() -> MapSheetRecord {
        MapSheetRecord {
            id: 42,
            rev: 3,
            zone: Some(zone(1, -2, 0)),
            seed: 305_419_896,
            clean: false,
            label: "Pasillo \"B\"".to_string(),
            layers: vec![
                MapSheetLayer {
                    layer_key: 0,
                    pen_argb: 0xF22A_47A8,
                    width_cpx: 300,
                    runs: vec![[0, -150, 140, 152, 0, 0], [1, 160, -195, -190, 1, 2]],
                },
                MapSheetLayer {
                    layer_key: 2,
                    pen_argb: 0xFFB0_2020,
                    width_cpx: 250,
                    runs: vec![[1, 101, -160, -151, 0, 0]],
                },
            ],
            links: vec![
                MapSheetLink {
                    to: zone(2, -2, 0),
                    side: 1,
                    x_cc: 9940,
                    z_cc: 4050,
                },
                MapSheetLink {
                    to: zone(1, -2, 1),
                    side: 4,
                    x_cc: 2050,
                    z_cc: 3050,
                },
            ],
            marks: vec![MapSheetMark {
                kind: 1,
                x_cc: 6050,
                z_cc: 7050,
                argb: 0xF22A_47A8,
            }],
        }
    }

    fn layer(runs: Vec<[i32; 6]>) -> NewLayer {
        NewLayer {
            pen_argb: 0xF22A_47A8,
            width_cpx: 300,
            runs,
        }
    }

    /// `n` tramos horizontales de 1..4 celdas, con valores de mundo realistas (miles de celdas).
    fn runs(n: usize) -> Vec<[i32; 6]> {
        (0..n)
            .map(|i| {
                let i = i as i32;
                [
                    i % 2,
                    -4870 + i % 100,
                    1200 + i,
                    1203 + i,
                    (i % 5 == 0) as i32,
                    i,
                ]
            })
            .collect()
    }

    #[test]
    fn golden_writes_byte_for_byte() {
        let json = serde_json::to_string(&handmade()).unwrap();
        assert_eq!(
            json,
            golden(),
            "si cambia el formato, cambian también el golden y MapSheetRecord.cs"
        );
    }

    #[test]
    fn golden_reads_and_rewrites_identically() {
        let golden = golden();
        let record: MapSheetRecord = serde_json::from_str(&golden).unwrap();
        assert_eq!(record, handmade());
        assert_eq!(serde_json::to_string(&record).unwrap(), golden);
        assert!(record.validate().is_ok());
    }

    #[test]
    fn create_assigns_monotonic_ids_never_reused() {
        let mut store = MapSheetStore::new();
        let (a, seed_a) = store.create(Some(zone(0, 0, 0))).unwrap();
        let (b, _) = store.create(None).unwrap();
        assert_eq!((a, b), (1, 2));
        assert_eq!(seed_a, seed_for(1));
        assert_eq!(store.get(b).unwrap().zone, None);
        assert_eq!(store.next_sheet_id(), 3);
    }

    #[test]
    fn append_assigns_layer_keys_in_order_and_keeps_run_order() {
        let mut store = MapSheetStore::new();
        let (id, _) = store.create(Some(zone(0, 0, 0))).unwrap();
        // Tramos a propósito fuera de orden por draw_index: el almacén NO los ordena.
        let unordered = vec![[0, 10, 5, 9, 0, 3], [1, 4, 2, 6, 1, 1]];
        let delta = MapSheetDelta {
            layers: vec![layer(unordered.clone()), layer(runs(2))],
            ..MapSheetDelta::default()
        };
        assert_eq!(store.append(id, delta), Ok((1, vec![0, 1])));

        let delta = MapSheetDelta {
            layers: vec![layer(runs(1))],
            links: vec![MapSheetLink {
                to: zone(1, 0, 0),
                side: 1,
                x_cc: 9940,
                z_cc: 2050,
            }],
            ..MapSheetDelta::default()
        };
        assert_eq!(store.append(id, delta), Ok((2, vec![2])));

        let record = store.get(id).unwrap();
        assert_eq!(record.layers[0].runs, unordered);
        assert_eq!(record.links.len(), 1);
        assert_eq!(record.rev, 2);
    }

    #[test]
    fn append_over_a_cap_is_rejected_and_changes_nothing() {
        let mut store = MapSheetStore::new();
        let (id, _) = store.create(None).unwrap();
        store
            .append(
                id,
                MapSheetDelta {
                    layers: vec![layer(runs(10))],
                    ..MapSheetDelta::default()
                },
            )
            .unwrap();
        let before = store.get(id).unwrap().clone();
        let bytes_before = store.total_bytes();

        let too_many_runs = MapSheetDelta {
            layers: vec![layer(runs(MAX_RUNS))],
            ..MapSheetDelta::default()
        };
        let rejected = store.append(id, too_many_runs).unwrap_err();
        assert_eq!(rejected.reason(), "cap");

        let empty_run = MapSheetDelta {
            layers: vec![layer(vec![[0, 1, 5, 5, 0, 0]])],
            ..MapSheetDelta::default()
        };
        assert_eq!(store.append(id, empty_run).unwrap_err().reason(), "cap");
        assert_eq!(store.get(id).unwrap(), &before, "la hoja queda como estaba");
        assert_eq!(store.total_bytes(), bytes_before);

        assert_eq!(
            store.append(999, MapSheetDelta::default()),
            Err(Reject::UnknownId)
        );

        let mut labelled = before.clone();
        labelled.label = "x".repeat(MAX_LABEL_CHARS + 1);
        assert_eq!(labelled.validate().unwrap_err().reason(), "cap");
        let mut unordered = before;
        unordered.layers.push(MapSheetLayer {
            layer_key: 0,
            pen_argb: 1,
            width_cpx: 100,
            runs: runs(1),
        });
        assert_eq!(unordered.validate().unwrap_err().reason(), "cap");
    }

    #[test]
    fn global_caps_reject_before_writing() {
        let mut store = MapSheetStore::new();
        for _ in 0..MAX_SHEETS {
            store.create(None).unwrap();
        }
        let next = store.next_sheet_id();
        assert_eq!(store.create(None).unwrap_err().reason(), "cap");
        assert_eq!(store.next_sheet_id(), next, "un rechazo no consume id");

        // Bytes: hojas llenas hasta que no cabe la siguiente.
        let mut bytes = MapSheetStore::new();
        let full = || MapSheetDelta {
            layers: vec![layer(runs(MAX_RUNS))],
            ..MapSheetDelta::default()
        };
        let mut rejected = false;
        for _ in 0..1000 {
            let (id, _) = bytes.create(None).unwrap();
            if bytes.append(id, full()).is_err() {
                rejected = true;
                assert_eq!(bytes.get(id).unwrap().layers.len(), 0);
                break;
            }
        }
        assert!(
            rejected,
            "8 MB se tienen que alcanzar antes de 1000 hojas llenas"
        );
        assert!(bytes.total_bytes() <= MAX_TOTAL_BYTES);
    }

    #[test]
    fn create_off_host_is_not_host() {
        let mut store = MapSheetStore::new();
        assert_eq!(handle_create(false, &mut store, None), Err(Reject::NotHost));
        assert!(store.is_empty());
        assert!(!store.is_dirty());
        assert!(handle_create(true, &mut store, None).is_ok());
    }

    #[test]
    fn only_changes_mark_dirty() {
        let mut store = MapSheetStore::new();
        assert!(!store.is_dirty());
        let (id, _) = store.create(None).unwrap();
        assert!(store.is_dirty());
        store.mark_saved();

        let _ = store.get(id);
        let _ = store.all();
        assert!(store
            .append(
                id,
                MapSheetDelta {
                    layers: vec![layer(vec![[0, 0, 3, 3, 0, 0]])],
                    ..MapSheetDelta::default()
                }
            )
            .is_err());
        assert!(!store.is_dirty(), "leer o un rechazo no ensucian");

        store
            .append(
                id,
                MapSheetDelta {
                    layers: vec![layer(runs(1))],
                    ..MapSheetDelta::default()
                },
            )
            .unwrap();
        assert!(store.is_dirty());
    }

    #[test]
    fn from_saved_restores_the_next_id_with_max() {
        let mut sheets = Vec::new();
        let mut source = MapSheetStore::new();
        for _ in 0..3 {
            source.create(None).unwrap();
        }
        sheets.extend(source.all());

        let (store, dropped) = MapSheetStore::from_saved(sheets.clone(), 2, 0);
        assert_eq!((store.len(), dropped), (3, 0));
        assert_eq!(
            store.next_sheet_id(),
            4,
            "id máximo + 1 gana a un contador viejo"
        );
        assert!(!store.is_dirty());

        let (store, _) = MapSheetStore::from_saved(Vec::new(), 0, 9);
        assert_eq!(
            store.next_sheet_id(),
            9,
            "sin fichero de hojas, el contador del mundo evita reutilizar ids"
        );

        let mut broken = sheets;
        broken[1].layers.push(MapSheetLayer {
            layer_key: 0,
            pen_argb: 0,
            width_cpx: 0,
            runs: vec![[0, 0, 4, 1, 0, 0]],
        });
        let (store, dropped) = MapSheetStore::from_saved(broken, 0, 0);
        assert_eq!((store.len(), dropped), (2, 1));
        assert_eq!(
            store.next_sheet_id(),
            4,
            "la descartada sigue sin reutilizarse"
        );
    }

    #[test]
    fn measured_sizes() {
        let mut store = MapSheetStore::new();
        let (id, _) = store.create(Some(zone(-37, 12, 3))).unwrap();
        store
            .append(
                id,
                MapSheetDelta {
                    layers: vec![layer(runs(MAX_RUNS))],
                    ..MapSheetDelta::default()
                },
            )
            .unwrap();
        let full = store.get(id).unwrap().json_len();

        let mut typical = MapSheetStore::new();
        for _ in 0..MAX_SHEETS {
            let (id, _) = typical.create(Some(zone(3, -4, 0))).unwrap();
            typical
                .append(
                    id,
                    MapSheetDelta {
                        layers: vec![layer(runs(60))],
                        ..MapSheetDelta::default()
                    },
                )
                .unwrap();
        }

        println!(
            "ADR-154 medida: hoja llena ({MAX_RUNS} tramos) = {full} B; {MAX_SHEETS} hojas típicas (60 tramos) = {} B",
            typical.total_bytes()
        );
        assert!(
            full < 60_000,
            "una hoja llena {full} B pasa del presupuesto del ADR"
        );
        assert!(typical.total_bytes() <= MAX_TOTAL_BYTES);
    }
}
