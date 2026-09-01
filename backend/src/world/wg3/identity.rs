//! ADR-103 — la identidad de nivel: un campo de MEZCLA de perfiles, no un nivel por sitio.
//!
//! Espejo EXACTO de `Wg3Identity` en C# (D8: el cliente RECALCULA, cero wire). Fase 1: campo XZ
//! puro; la `y` está en la firma desde el primer commit y va clavada a 0 (D7).
//!
//! Un subnivel no es un set de contenido: es un PERFIL DE PERILLAS sobre las constantes que ya
//! deciden el aspecto del mundo (D1). Este módulo solo RESPONDE quién es cada sitio; hoy nadie
//! consume la respuesta — engancharla al plan, al loot, al spawn o a la dificultad es trabajo de
//! sus consumidores, cada uno con su medida (ADR-110 D2: cada perilla con su antes y después).
//! Mientras nadie la lea, el mundo servido es el de ayer byte a byte, que es la guardia de
//! regresión de D5.
//!
//! La semilla es la GLOBAL del compositor, no la de la región: una celda de identidad cubre 6×6
//! regiones y todas tienen que leer lo mismo — el mismo motivo por el que las puertas de junta
//! sortean con la del mundo (ADR-096, `region_settings`).

use super::hash;
use super::world::{self, Wg3RegionCoord};

/// Lado de la celda de identidad, en regiones. MÚLTIPLO EXACTO de la región (D4): una región nunca
/// cae a caballo de dos celdas, así que la frontera de identidad es siempre frontera de región —
/// un sitio donde el mundo ya cambia de mano y donde ya hay contrato.
pub const IDENTITY_CELL_REGIONS: i32 = 6;

/// Lado de la celda de identidad en metros: 6 × 150 = 900. Varios minutos andando dentro de una
/// misma identidad. El número no está defendido por nada más que el orden de magnitud (D4): si al
/// medir se lee como colcha de retales, sube.
pub const IDENTITY_CELL_M: f32 = IDENTITY_CELL_REGIONS as f32 * world::REGION_M;

/// D2 — con esta probabilidad la celda ENCAJA en el ancla más próxima y sale pura. Sin esto un `u`
/// uniforme casi nunca cae clavado en un ancla, el mundo entero sería mezcla y ningún sitio sería
/// «Level 0 de verdad» — papilla gris, el fallo clásico de mezclar perfiles. Propuesta sin medir
/// (0.45): la medida la trae el frente de sensación, no este módulo.
pub const PURITY_CHANCE: f32 = 0.45;

const SALT_IDENTITY_U: u32 = 0x1DE1_7000;
const SALT_IDENTITY_PURITY: u32 = 0x1DE1_7001;

/// Las cuatro anclas de la fase 1 (D5), en el orden de la escalera de `u`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum LevelAnchor {
    /// Level 0 «Threshold» — el mundo de hoy. Todos los factores a 1.0, EXACTAMENTE: es la guardia
    /// de regresión, no pereza.
    Threshold = 0,
    /// 0.1 «Zenith Station» — amplio y alto.
    ZenithStation = 1,
    /// 0.2 «Remodeled Mess» — irregular, roto.
    RemodeledMess = 2,
    /// 0.3 «The Icy Rooms» — apretado y bajo.
    IcyRooms = 3,
}

/// Posición de cada ancla en la escalera de `u` (D2).
const ANCHOR_U: [f32; 4] = [0.0, 0.33, 0.66, 1.0];

const ANCHORS: [LevelAnchor; 4] = [
    LevelAnchor::Threshold,
    LevelAnchor::ZenithStation,
    LevelAnchor::RemodeledMess,
    LevelAnchor::IcyRooms,
];

/// D1 — el perfil de perillas. Factores sobre constantes que YA existen; cero geometría nueva,
/// cero piezas nuevas, cero `SpaceRole` nuevo. Los números de D5 son puntos de partida, no
/// resultados: salen a medida cuando el frente los ande, como todos los de ADR-095 en adelante.
///
/// Qué multiplica cada uno, y dónde vive hoy la constante:
///  · `target_area_mul`  — `TARGET_AREA_M2` (`plan.rs`)
///  · `weird_spread_mul` — `WEIRD_SPREAD` (`plan.rs`)
///  · `void_chance_mul`  — `VOID_CHANCE_WEIRD` (`plan.rs`)
///  · `clear_height_mul` — `clear_height_by_role` (`fill.rs`)
///  · `band_width_mul`   — `BAND_WIDTH_CM` (`plan.rs`); D6: SOLO puede subir (≥ 1.0), su suelo
///    sigue siendo 240 — por debajo el ráster conservador no deja pasar (ADR-098)
///  · `max_depth_delta`  — `MAX_DEPTH` (`plan.rs`); se suma, no multiplica; `f32` para que la
///    mezcla sea uniforme, el consumidor redondea
///
/// Fuera del perfil, por D6: los umbrales de `scale_at`, `REGION_STOREYS`, y `GATE_CLEARANCE_CM`
/// (que debe calcularse con el MÁXIMO `band_width_mul` de todos los perfiles — ver
/// [`max_band_width_mul`] — o dos identidades vecinas nacen selladas por su junta).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LevelProfile {
    pub target_area_mul: f32,
    pub weird_spread_mul: f32,
    pub void_chance_mul: f32,
    pub clear_height_mul: f32,
    pub band_width_mul: f32,
    pub max_depth_delta: f32,
}

impl LevelProfile {
    /// El perfil neutro: Level 0. Cualquier consumidor que aplique este perfil tiene que producir
    /// EXACTAMENTE el mundo de hoy (verificación (b) de ADR-103).
    pub const THRESHOLD: LevelProfile = LevelProfile {
        target_area_mul: 1.0,
        weird_spread_mul: 1.0,
        void_chance_mul: 1.0,
        clear_height_mul: 1.0,
        band_width_mul: 1.0,
        max_depth_delta: 0.0,
    };
}

/// Perfil de cada ancla (D5). Indexado por `LevelAnchor as usize`.
const ANCHOR_PROFILES: [LevelProfile; 4] = [
    LevelProfile::THRESHOLD,
    // 0.1 Zenith Station — amplio y alto.
    LevelProfile {
        target_area_mul: 1.9,
        weird_spread_mul: 1.0,
        void_chance_mul: 0.4,
        clear_height_mul: 1.35,
        band_width_mul: 1.25,
        max_depth_delta: 0.0,
    },
    // 0.2 Remodeled Mess — irregular, roto.
    LevelProfile {
        target_area_mul: 0.8,
        weird_spread_mul: 1.8,
        void_chance_mul: 2.2,
        clear_height_mul: 1.0,
        band_width_mul: 1.0,
        max_depth_delta: 2.0,
    },
    // 0.3 The Icy Rooms — apretado y bajo.
    LevelProfile {
        target_area_mul: 0.45,
        weird_spread_mul: 1.0,
        void_chance_mul: 0.3,
        clear_height_mul: 0.8,
        band_width_mul: 1.0,
        max_depth_delta: 0.0,
    },
];

/// D6 — `GATE_CLEARANCE_CM` se calcula con el MÁXIMO `band_width_mul` de todos los perfiles, no
/// con el local: dos regiones vecinas de identidad distinta comparten puertas de junta (ADR-096) y
/// si cada lado despeja según su propia banda, un corte cae encima de una puerta y la región nace
/// sellada por ese lado. Ya pasó: 64 de 256 puertas perdidas (ADR-100 enmienda 1).
pub fn max_band_width_mul() -> f32 {
    let mut max = 0.0f32;
    let mut i = 0;
    while i < ANCHOR_PROFILES.len() {
        if ANCHOR_PROFILES[i].band_width_mul > max {
            max = ANCHOR_PROFILES[i].band_width_mul;
        }
        i += 1;
    }
    max
}

/// D2 — lo que devuelve el campo: DOS perfiles ancla y un peso, no un perfil. Cada perilla
/// efectiva es la media ponderada de las dos ([`LevelMix::profile`]). Una celda pura lleva la
/// misma ancla en los dos lados y peso 0.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct LevelMix {
    pub lower: LevelAnchor,
    pub upper: LevelAnchor,
    /// En `[0,1)`: 0 = todo `lower`, hacia 1 = todo `upper`.
    pub toward_upper: f32,
}

impl LevelMix {
    pub fn is_pure(&self) -> bool {
        self.lower == self.upper
    }

    /// Perilla a perilla, la media ponderada de las dos anclas (D2).
    pub fn profile(&self) -> LevelProfile {
        let a = ANCHOR_PROFILES[self.lower as usize];
        let b = ANCHOR_PROFILES[self.upper as usize];
        let w = self.toward_upper;
        LevelProfile {
            target_area_mul: blend(a.target_area_mul, b.target_area_mul, w),
            weird_spread_mul: blend(a.weird_spread_mul, b.weird_spread_mul, w),
            void_chance_mul: blend(a.void_chance_mul, b.void_chance_mul, w),
            clear_height_mul: blend(a.clear_height_mul, b.clear_height_mul, w),
            band_width_mul: blend(a.band_width_mul, b.band_width_mul, w),
            max_depth_delta: blend(a.max_depth_delta, b.max_depth_delta, w),
        }
    }
}

/// En `f32` y en este orden, para que el espejo C# dé los mismos bits.
fn blend(a: f32, b: f32, w: f32) -> f32 {
    a + (b - a) * w
}

/// La identidad en un punto del mundo. `world_seed` es la semilla GLOBAL del compositor
/// ([`world::composer_seed`]), la misma que sortea las puertas de junta — nunca la de una región.
///
/// D7 — la `y` entra en la firma HOY y se ignora: fase 1 es campo XZ puro. El día que exista el
/// descenso entre subniveles se suelta el parámetro y no se reescribe una sola llamada.
///
/// D3 — la mezcla es por CELDA, nunca por metro: dentro de la celda la mezcla es constante y la
/// frontera es un escalón duro, la misma premisa de `scale.rs`.
pub fn at(world_seed: i32, x: f32, _y: f32, z: f32) -> LevelMix {
    let cx = floor_div(x, IDENTITY_CELL_M);
    let cz = floor_div(z, IDENTITY_CELL_M);
    let u = hash::to_unit(hash::mix(world_seed, cx, cz, SALT_IDENTITY_U as i32));
    let purity = hash::to_unit(hash::mix(world_seed, cx, cz, SALT_IDENTITY_PURITY as i32));

    if purity < PURITY_CHANCE {
        let a = nearest_anchor(u);
        return LevelMix {
            lower: a,
            upper: a,
            toward_upper: 0.0,
        };
    }

    // Escalera de anclas: `u` da a la vez qué dos son vecinas y en qué proporción se mezclan.
    let mut i = 0;
    while i + 2 < ANCHOR_U.len() && u >= ANCHOR_U[i + 1] {
        i += 1;
    }
    LevelMix {
        lower: ANCHORS[i],
        upper: ANCHORS[i + 1],
        toward_upper: (u - ANCHOR_U[i]) / (ANCHOR_U[i + 1] - ANCHOR_U[i]),
    }
}

/// D4 — la identidad de una región se resuelve UNA VEZ, en su centro. Como la celda es múltiplo
/// exacto de la región, cualquier punto de la región daría lo mismo; el centro es la consulta
/// canónica para que nadie tenga que elegir.
pub fn for_region(world_seed: u64, region: Wg3RegionCoord) -> LevelMix {
    let (min_x, min_z, _, _) = region.bounds();
    at(
        world::composer_seed(world_seed),
        min_x + world::REGION_M * 0.5,
        0.0,
        min_z + world::REGION_M * 0.5,
    )
}

/// El ancla más próxima en la escalera. Empates hacia la de índice menor, con `<` estricto, para
/// que el espejo C# no pueda discrepar ni en el empate.
fn nearest_anchor(u: f32) -> LevelAnchor {
    let mut best = 0;
    let mut best_d = (u - ANCHOR_U[0]).abs();
    let mut i = 1;
    while i < ANCHOR_U.len() {
        let d = (u - ANCHOR_U[i]).abs();
        if d < best_d {
            best = i;
            best_d = d;
        }
        i += 1;
    }
    ANCHORS[best]
}

/// División con suelo, copiada de `scale.rs` letra a letra (que a su vez copia la forma de C#):
/// `(int)(v / size)` trunca hacia cero y espejaría el campo en el origen. Copia y no reexport a
/// propósito — cada espejo se lee entero en su fichero, igual que hace el de escala.
fn floor_div(v: f32, size: f32) -> i32 {
    let q = v / size;
    let i = q as i32;
    if q < 0.0 && q != i as f32 {
        i - 1
    } else {
        i
    }
}
