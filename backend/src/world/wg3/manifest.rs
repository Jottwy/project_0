//! ADR-095 — el manifiesto de WorldGen3 visto desde el backend.
//!
//! Es la ÚNICA forma en que la geometría autorada en Unity llega aquí. Lo escribe el horneado
//! (`Wg3ManifestExporter`, lado editor) en `Assets/StreamingAssets/wg3_manifest.json`; a este lado
//! solo se lee.
//!
//! REGLA R1 — aquí no hay mallas, ni triángulos, ni materiales, ni normales: solo huella, bocas y
//! una lista de cajas. Si alguna vez hiciera falta que el backend "entendiera" una pieza más allá
//! de esto, la arquitectura estaría mal.
//!
//! **DIFERENCIA DELIBERADA CON `room_manifest`: aquí NO hay `OnceLock`.** El de las salas autoradas
//! es un global del proceso, y eso costó una sesión entera de números falsos: en cuanto una sonda
//! ponía la variable de entorno, la siguiente medía otro mundo sin saberlo, y el resultado se leyó
//! como un hallazgo. La regla R3 lo prohíbe, así que el manifiesto se pasa por parámetro hasta
//! donde haga falta. Cuesta una firma más y hace que dos pruebas del mismo proceso no puedan
//! mentirse entre ellas.

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

/// Variable de entorno con la ruta ABSOLUTA del manifiesto, declarada por quien lanza el backend.
///
/// No se deriva de `current_exe()`: en el editor el ejecutable vive en `backend/target/release/` y
/// el manifiesto en `Assets/StreamingAssets/`, que no guardan ninguna relación de ruta estable.
pub const WG3_MANIFEST_ENV: &str = "BACKROOMS_WG3_MANIFEST";

/// Versión de FORMATO que este backend sabe leer. Espejo de `Wg3Manifest.FormatVersion` en C#.
///
/// Un manifiesto con otra versión se rechaza entero en vez de leerse a medias: media pieza colocada
/// es peor que ninguna, porque el síntoma no es un error sino un mundo raro.
pub const WG3_MANIFEST_FORMAT: u32 = 1;

/// Una boca. Los nombres son las claves del JSON: los escribe C# en snake_case a propósito.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Wg3Socket {
    /// `0 = N (+Z)`, `1 = E (+X)`, `2 = S (−Z)`, `3 = O (−X)`.
    pub side: u8,
    /// Metros a lo largo del lado, hasta el CENTRO de la boca.
    pub offset: f32,
    pub width: f32,
    /// Discriminante de `Wg3SocketType`. Entero y no cadena porque es un contrato: cambiar el orden
    /// del enum cambia el mundo, y tiene que doler al tocarlo.
    #[serde(rename = "type")]
    pub kind: u8,
    pub floor_y: f32,
    pub ceiling_y: f32,
}

/// Una caja de la chuleta, en coordenadas LOCALES de la pieza sin girar.
// Sin `Eq`: son todo floats. `PartialEq` basta para lo único que se hace con esto, compararlas en
// tests.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct Wg3Box {
    pub cx: f32,
    pub cy: f32,
    pub cz: f32,
    pub sx: f32,
    pub sy: f32,
    pub sz: f32,
    /// Giro propio de la caja alrededor de Y, en grados. Se SUMA al giro de la colocación.
    pub yaw: f32,
    /// Discriminante de `Wg3VolumeKind`. La colisión no lo necesita —todo lo que llega aquí
    /// bloquea— pero sí hace legible un volcado del backend.
    pub kind: u8,
}

/// Una pieza vista por el backend: huella, bocas, chuleta y lo justo para poder SORTEARLA.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Wg3Piece {
    /// Posición en el catálogo. ES lo que viajará por el wire: mandar la cadena costaría bytes por
    /// chunk para identificar algo que las dos partes ya tienen.
    pub index: u16,

    /// Solo para logs y para poder leer el fichero.
    #[serde(default)]
    pub id: String,

    /// La huella ES el bounds: el origen de una pieza es su esquina mínima, así que tamaño y caja
    /// envolvente son el mismo dato y exportar los dos sería invitar a que se contradigan.
    pub size_x: f32,
    pub size_z: f32,
    pub height_meters: f32,

    pub scale: u8,
    pub weight: f32,
    pub min_depth: i32,
    pub dead_end: bool,

    pub sockets: Vec<Wg3Socket>,

    /// La chuleta: solo volúmenes sólidos. La decoración no cruza la frontera de autoridad (R25).
    pub collision: Vec<Wg3Box>,
}

/// El catálogo entero tal y como lo exportó el horneado.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Wg3Manifest {
    pub version: u32,

    /// SHA-256 en hex del JSON de `pieces`, calculado por el exportador.
    ///
    /// Aquí es una CADENA OPACA: se compara y nada más. Recalcularlo a este lado obligaría a que C#
    /// y Rust coincidieran byte a byte en una forma canónica del JSON, que es la duplicación que
    /// este proyecto ya paga cara en otros sitios.
    pub digest: String,

    pub pieces: Vec<Wg3Piece>,
}

impl Wg3Manifest {
    pub fn piece(&self, index: u16) -> Option<&Wg3Piece> {
        self.pieces.get(index as usize)
    }

    /// Lo que el backend NECESITA que sea cierto antes de colocar nada. Devuelve los motivos; vacío
    /// = utilizable.
    ///
    /// No repite el validador de C#: aquel comprueba el AUTORADO (que una boca quepa en su lado,
    /// que haya hueco caminable). Éste comprueba la INTEGRIDAD DEL FICHERO, que es lo único que
    /// puede haberse roto entre el horneado y este proceso.
    pub fn problems(&self) -> Vec<String> {
        let mut out = Vec::new();
        if self.version != WG3_MANIFEST_FORMAT {
            out.push(format!(
                "versión de formato {} — este backend lee la {}",
                self.version, WG3_MANIFEST_FORMAT
            ));
        }
        if self.digest.len() != 64 || !self.digest.bytes().all(|b| b.is_ascii_hexdigit()) {
            out.push(format!("digest malformado: {:?}", self.digest));
        }
        for (i, p) in self.pieces.iter().enumerate() {
            if p.index as usize != i {
                out.push(format!(
                    "la pieza {} declara índice {} — el índice ES la posición, y es lo que viaja",
                    i, p.index
                ));
            }
            if p.size_x <= 0.0 || p.size_z <= 0.0 {
                out.push(format!("{}: huella no positiva", p.id));
            }
            if p.sockets.is_empty() {
                out.push(format!("{}: sin bocas, no se puede colocar nunca", p.id));
            }
            if p.collision.is_empty() {
                // R6 llevado al backend: una pieza sin chuleta se dibujaría en el cliente y se
                // atravesaría en el servidor. Es el fallo silencioso que este ADR viene a evitar.
                out.push(format!("{}: sin chuleta de colisión", p.id));
            }
        }
        out
    }
}

/// Ruta del manifiesto declarada en el entorno, si la hay.
pub fn manifest_path_from_env() -> Option<PathBuf> {
    std::env::var_os(WG3_MANIFEST_ENV)
        .filter(|v| !v.is_empty())
        .map(PathBuf::from)
}

/// Carga y parsea. `None` con motivo en el log si algo falla.
///
/// SIN MANIFIESTO NO HAY WG3, y ése es un estado válido: un backend de test, de CI o de un build sin
/// catálogo horneado tiene que arrancar igual. Mientras WG3 conviva con WG2 (ADR-095, D3) su
/// ausencia no es un error, es que la feature no está.
pub fn load_manifest(path: &Path) -> Option<Wg3Manifest> {
    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(e) => {
            log::warn!("[wg3] no se pudo leer {}: {e}", path.display());
            return None;
        }
    };
    parse_manifest(&text)
}

pub fn parse_manifest(text: &str) -> Option<Wg3Manifest> {
    // Se le quita el BOM si viene: el exportador escribe UTF-8 sin él, pero un editor de texto puede
    // metérselo por el camino y `serde_json` se atraganta con el primer carácter dando un error que
    // no menciona el BOM por ningún lado.
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);

    let manifest: Wg3Manifest = match serde_json::from_str(text) {
        Ok(m) => m,
        Err(e) => {
            log::warn!("[wg3] manifiesto ilegible: {e}");
            return None;
        }
    };

    let problems = manifest.problems();
    if !problems.is_empty() {
        // Se rechaza ENTERO. Colocar las piezas sanas y callar las rotas es el modo de fallo que
        // ADR-095 nombra: un mundo al que le falta contenido y nadie sabe por qué.
        log::warn!(
            "[wg3] manifiesto rechazado, {} motivos: {}",
            problems.len(),
            problems.join("; ")
        );
        return None;
    }
    Some(manifest)
}
