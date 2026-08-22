//! ADR-083 enmienda 1, punto 8 — el MANIFIESTO del pool de salas autoradas.
//!
//! Es la única forma en que la geometría de una sala hecha a mano en Unity llega al backend. Lo
//! escribe el horneado (`RoomManifestExporter`, lado editor) en
//! `Assets/StreamingAssets/room_manifest.json`; aquí solo se lee.
//!
//! POR QUÉ UN FICHERO Y NO EL WIRE: el pool viaja en el build y no cambia por partida, así que
//! mandarlo por conexión sería pagarlo una vez por cliente. Y por qué no al revés —que el cliente se
//! lo mande al servidor al conectar—: eso haría al cliente autoridad de la geometría de colisión del
//! servidor, y ADR-083 lo rechazó como alternativa (A).
//!
//! SIN MANIFIESTO NO HAY SALAS, y eso es un estado válido: un backend de test, de CI o de un build
//! sin pool horneado tiene que arrancar igual. Es la misma forma que `layer_rules::load_profiles`
//! (JSON con caída a un valor conocido), con la diferencia de que aquí la caída es "esta feature no
//! existe" en vez de un default.

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use super::CELL_SIZE_M;

/// Variable de entorno con la ruta ABSOLUTA del manifiesto, declarada por quien lanza el backend
/// (`NetworkInitializer`, igual que `WORLD_SEED` o `IPC_PORT`).
///
/// No se resuelve desde `current_exe()` a propósito: en el editor el ejecutable vive en
/// `backend/target/release/` y el manifiesto en `Assets/StreamingAssets/`, que no guardan ninguna
/// relación de ruta estable. Config explícita por lanzamiento, nunca heredada — mismo criterio que
/// el resto de la configuración de este proceso.
pub const ROOM_MANIFEST_ENV: &str = "BACKROOMS_ROOM_MANIFEST";

/// Tope de aberturas por sala. Existe para que el plan de emplazamiento quepa en un array fijo y no
/// pida una asignación por chunk: `generate_chunk_layer` corre para la colisión del jugador, la
/// caché del robapieles y el render, y es sitio caliente. Ocho puertas es más de lo que ninguna sala
/// razonable necesita; el exportador corta ahí y avisa.
pub const MAX_DOORWAYS: usize = 8;

/// Una sala del pool, vista por el backend.
///
/// Lleva LO MÍNIMO para reservar y tallar: cuánto ocupa y por dónde se sale. Ni malla, ni cajas de
/// colisión, ni marcadores — el interior de la sala no es autoritativo en servidor en este slice
/// (ADR-083 enmienda 1, punto 9).
// Sin `Eq`: `height_meters` es un float. `PartialEq` basta para lo único que se hace con esto,
// que es compararlo en tests.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ManifestRoom {
    /// Índice en `RoomPool.rooms`. ES lo que viaja por el wire para decirle al cliente qué prefab
    /// instanciar, así que reordenar el pool sin reexportar cambia qué sala sale en cada sitio.
    pub index: u16,

    /// Solo para logs y para poder leer el fichero. El backend no lo interpreta.
    #[serde(default)]
    pub id: String,

    /// Footprint en TILES de 5 m, sin girar.
    pub tiles_x: u8,
    /// Footprint en TILES de 5 m, sin girar.
    pub tiles_z: u8,

    /// Altura autorada de la sala, en metros (ADR-085). De ella sale cuántas capas invade.
    ///
    /// Es `RoomDefinition.heightMeters`, la altura de REFERENCIA, no la local bajo un techo
    /// inclinado: `ceilingTilt` redistribuye —un lado gana lo que el otro pierde— y usar la local
    /// haría que la capa invadida dependiera de dónde mires (ADR-085 punto 6).
    ///
    /// `default` a 0 y no a 4: un manifiesto exportado antes de ADR-085 no lleva el campo, y 0 cae
    /// en la rama "cabe en su propia capa" de `top_layer_for_height`, que es exactamente el
    /// comportamiento que ese manifiesto tenía. Así un manifiesto viejo sigue dando el mundo viejo.
    #[serde(default)]
    pub height_meters: f32,

    /// TODAS las aberturas practicables de la sala, no solo una.
    ///
    /// Una sala con dos puertas y un solo túnel deja la segunda dando contra el margen macizo: se ve
    /// el vano y detrás un bloque cerrado. Se excava un pasillo por CADA una.
    pub doorways: Vec<ManifestDoorway>,
}

/// Una abertura de la sala, vista desde los cuatro giros.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ManifestDoorway {
    /// Lado al que da con giro `q · 90°`, ya en la convención de `RoomPlan::door_side`
    /// (`0 = sur (−z), 1 = norte (+z), 2 = oeste (−x), 3 = este (+x)`).
    ///
    /// La traducción desde los bits `EdgeSouth/North/West/East` del cliente la hace el exportador,
    /// una sola vez. Aquí llega ya en números de backend: si esto se calculara a este lado harían
    /// falta dos implementaciones de la misma regla, que es la deuda que ADR-081 ya dejó anotada.
    pub side_by_quarter: [u8; 4],

    /// Tile de 5 m por el que sale, contado desde la esquina de menor x/z del footprint YA GIRADO y
    /// a lo largo de ese lado.
    ///
    /// **No se calcula aquí, y no es pereza.** Al girar la sala, la puerta se muda a otro tile del
    /// lado; cualquier cuenta local del tipo "el tile de en medio" solo acierta con giro 0 y deja el
    /// pasillo contra la pared en los otros tres. El dato sale de la posición REAL del boquete en el
    /// contorno de la sala, que es geometría que solo conoce el lado de Unity.
    pub tile_by_quarter: [u8; 4],
}

impl ManifestRoom {
    /// Footprint en CELDAS de 2,5 m con el giro `q` aplicado. Un cuarto impar intercambia los ejes.
    pub fn footprint_cells(&self, quarter: u8) -> (usize, usize) {
        let tile_cells = (5.0 / CELL_SIZE_M) as usize; // 2
        let (tx, tz) = if quarter % 2 == 1 {
            (self.tiles_z, self.tiles_x)
        } else {
            (self.tiles_x, self.tiles_z)
        };
        (tx as usize * tile_cells, tz as usize * tile_cells)
    }
}

/// El pool entero tal y como lo exportó el horneado.
// Sin `Eq` por la misma razón que `ManifestRoom`: lleva la altura, que es un float.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RoomManifest {
    /// SHA-256 en hex del JSON de `rooms`, calculado por el exportador. Aquí es una CADENA OPACA:
    /// se compara con la del peer en el handshake y nada más.
    ///
    /// Recalcularlo a este lado obligaría a que C# y Rust coincidieran byte a byte en una forma
    /// canónica del JSON — dos implementaciones de la misma regla, justo lo que este ADR evita en
    /// todo lo demás. Contra la edición a mano no protege el digest: la prohíbe el ADR.
    pub digest: String,

    pub rooms: Vec<ManifestRoom>,
}

/// Ruta del manifiesto declarada en el entorno, si la hay.
pub fn manifest_path_from_env() -> Option<PathBuf> {
    std::env::var_os(ROOM_MANIFEST_ENV)
        .filter(|v| !v.is_empty())
        .map(PathBuf::from)
}

/// El manifiesto de ESTE proceso, cargado una sola vez.
///
/// Es un global de solo lectura, y eso es deliberado: `generate_chunk_layer` lo necesita, y esa
/// función la llaman la colisión del jugador, la caché del robapieles, el render y una docena de
/// tests. Pasarlo como parámetro hasta ahí sería tocar todas esas firmas para transportar una
/// constante de proceso — que es lo que ya es `LAYER_PROFILES`, solo que esta se lee de disco.
///
/// No rompe el determinismo: el fichero no cambia durante la partida, y que dos peers tengan
/// ficheros distintos es exactamente lo que el digest del handshake existe para cazar.
pub fn active_manifest() -> Option<&'static RoomManifest> {
    static ACTIVE: std::sync::OnceLock<Option<RoomManifest>> = std::sync::OnceLock::new();
    ACTIVE
        .get_or_init(|| {
            let path = manifest_path_from_env()?;
            let manifest = load_manifest(&path)?;
            log::info!(
                "room_manifest cargado: {} sala(s), digest {} ({})",
                manifest.rooms.len(),
                manifest.digest,
                path.display()
            );
            Some(manifest)
        })
        .as_ref()
}

/// Carga el manifiesto. `None` = no hay salas autoradas en este mundo.
///
/// Fichero ausente: silencioso y esperado (test, CI, build sin pool). Fichero presente pero
/// ilegible, mal formado o incoherente: **ruidoso**, y la feature se apaga entera. No se descartan
/// filas sueltas — un manifiesto a medias es un build roto, y prefiero que el digest del handshake
/// lo delate a que dos peers coloquen salas distintas creyendo cada uno que va bien.
pub fn load_manifest(path: &Path) -> Option<RoomManifest> {
    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return None,
        Err(e) => {
            log::error!(
                "room_manifest illegible en {}: {e}. Sin salas autoradas.",
                path.display()
            );
            return None;
        }
    };
    parse_manifest(&text)
}

/// Parsea y valida. Separada de la E/S para poder probarla sin tocar disco.
pub fn parse_manifest(text: &str) -> Option<RoomManifest> {
    let manifest: RoomManifest = match serde_json::from_str(text) {
        Ok(m) => m,
        Err(e) => {
            log::error!("room_manifest mal formado: {e}. Sin salas autoradas.");
            return None;
        }
    };

    if manifest.digest.is_empty() {
        log::error!("room_manifest sin digest. Sin salas autoradas.");
        return None;
    }

    for room in &manifest.rooms {
        if room.tiles_x < 1 || room.tiles_z < 1 {
            log::error!(
                "room_manifest: sala {} ({}) con footprint {}×{}. Sin salas autoradas.",
                room.index,
                room.id,
                room.tiles_x,
                room.tiles_z
            );
            return None;
        }
        // Sin abertura, la sala es una caja sellada: colocarla sería poner un sitio inaccesible en
        // mitad del mundo. El exportador ya las descarta; esto es la red por si el fichero viene de
        // otro sitio.
        if room.doorways.is_empty() {
            log::error!(
                "room_manifest: sala {} ({}) sin ninguna abertura. Sin salas autoradas.",
                room.index,
                room.id
            );
            return None;
        }
        if room.doorways.len() > MAX_DOORWAYS {
            log::error!(
                "room_manifest: sala {} ({}) con {} aberturas, el tope es {MAX_DOORWAYS}. Sin salas autoradas.",
                room.index,
                room.id,
                room.doorways.len()
            );
            return None;
        }

        for door in &room.doorways {
            if door.side_by_quarter.iter().any(|&s| s > 3) {
                log::error!(
                    "room_manifest: sala {} ({}) con lado de puerta fuera de 0..=3. Sin salas autoradas.",
                    room.index,
                    room.id
                );
                return None;
            }
            // El tile de la puerta tiene que caer DENTRO del lado por el que sale. Un valor fuera de
            // rango pondría el túnel a un lado de la sala, o fuera de ella.
            for q in 0..4usize {
                let along_x = matches!(door.side_by_quarter[q], 0 | 1);
                let swapped = q % 2 == 1;
                let span = match (along_x, swapped) {
                    (true, false) | (false, true) => room.tiles_x,
                    _ => room.tiles_z,
                };
                if door.tile_by_quarter[q] >= span {
                    log::error!(
                        "room_manifest: sala {} ({}) con tile de puerta {} fuera del lado de {} tiles (giro {q}). Sin salas autoradas.",
                        room.index,
                        room.id,
                        door.tile_by_quarter[q],
                        span
                    );
                    return None;
                }
            }
        }
    }

    Some(manifest)
}

#[cfg(test)]
mod tests {
    use super::*;

    const GOOD: &str = r#"{
        "digest": "abc123",
        "rooms": [
            { "index": 0, "id": "room_0", "tiles_x": 4, "tiles_z": 4,
              "doorways": [{ "side_by_quarter": [1,3,0,2], "tile_by_quarter": [2,1,1,2] }] }
        ]
    }"#;

    #[test]
    fn parses_a_well_formed_manifest() {
        let m = parse_manifest(GOOD).expect("manifiesto válido");
        assert_eq!(m.digest, "abc123");
        assert_eq!(m.rooms.len(), 1);
        assert_eq!(m.rooms[0].index, 0);
        assert_eq!(m.rooms[0].tiles_x, 4);
        assert_eq!(m.rooms[0].doorways[0].side_by_quarter, [1, 3, 0, 2]);
    }

    /// Un manifiesto vacío es válido: pool sin salas todavía, no error.
    #[test]
    fn empty_room_list_is_valid() {
        let m = parse_manifest(r#"{ "digest": "d", "rooms": [] }"#).expect("válido");
        assert!(m.rooms.is_empty());
    }

    #[test]
    fn rejects_missing_digest() {
        assert!(parse_manifest(r#"{ "digest": "", "rooms": [] }"#).is_none());
    }

    #[test]
    fn rejects_malformed_json() {
        assert!(parse_manifest("{ no es json").is_none());
    }

    /// Una fila mala tumba el manifiesto ENTERO, no solo su fila — ver el doc-comment de
    /// `load_manifest`.
    #[test]
    fn one_bad_row_disables_the_whole_manifest() {
        let bad = r#"{ "digest": "d", "rooms": [
            { "index": 0, "id": "ok",  "tiles_x": 4, "tiles_z": 4, "doorways": [{ "side_by_quarter": [0,1,2,3], "tile_by_quarter": [0,0,0,0] }] },
            { "index": 1, "id": "bad", "tiles_x": 0, "tiles_z": 4, "doorways": [{ "side_by_quarter": [0,1,2,3], "tile_by_quarter": [0,0,0,0] }] }
        ] }"#;
        assert!(parse_manifest(bad).is_none());
    }

    #[test]
    fn rejects_door_side_out_of_range() {
        let bad = r#"{ "digest": "d", "rooms": [
            { "index": 0, "id": "x", "tiles_x": 2, "tiles_z": 2, "doorways": [{ "side_by_quarter": [0,1,2,9], "tile_by_quarter": [0,0,0,0] }] }
        ] }"#;
        assert!(parse_manifest(bad).is_none());
    }

    /// Un fichero que no existe NO es un error: es "este mundo no tiene salas autoradas".
    #[test]
    fn missing_file_is_silent_none() {
        assert!(load_manifest(Path::new("definitely/missing/room_manifest.json")).is_none());
    }

    /// Sonda contra el manifiesto REAL del repo, el que acaba de escribir el horneado. Ignorada
    /// por defecto —depende de un fichero fuera de `backend/`, y CI no tiene por qué tenerlo— pero
    /// es la única comprobación que cubre el formato de verdad en vez de uno escrito a mano aquí:
    ///
    ///     cargo test --manifest-path backend/Cargo.toml real_repo_manifest -- --ignored --nocapture
    #[test]
    #[ignore = "sonda contra el manifiesto real del repo: de una en una, con -- --ignored --nocapture"]
    fn real_repo_manifest_parses() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../Assets/StreamingAssets/room_manifest.json"
        );
        let m = load_manifest(Path::new(path)).expect("el manifiesto del repo tiene que parsear");
        assert!(!m.digest.is_empty());
        for r in &m.rooms {
            println!(
                "{} idx={} {}×{} aberturas={}",
                r.id,
                r.index,
                r.tiles_x,
                r.tiles_z,
                r.doorways.len()
            );
        }
    }

    /// El giro impar intercambia los ejes; el par los deja. Un tile son 2 celdas.
    #[test]
    fn footprint_swaps_axes_on_odd_quarters() {
        let room = ManifestRoom {
            index: 0,
            id: "r".into(),
            tiles_x: 4,
            tiles_z: 2,
            height_meters: 0.0,
            doorways: vec![ManifestDoorway {
                side_by_quarter: [0, 1, 2, 3],
                tile_by_quarter: [0, 0, 0, 0],
            }],
        };
        assert_eq!(room.footprint_cells(0), (8, 4));
        assert_eq!(room.footprint_cells(1), (4, 8));
        assert_eq!(room.footprint_cells(2), (8, 4));
        assert_eq!(room.footprint_cells(3), (4, 8));
    }
}
