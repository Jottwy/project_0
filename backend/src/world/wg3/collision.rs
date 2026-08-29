//! ADR-106 — la fuente de colisión de WG3: un caché de rásteres por chunk y las tres preguntas.
//!
//! # Por qué un caché y no consultar la región directamente
//!
//! Rasterizar un chunk cuesta —son las cajas de todas las piezas, tramos, vanos y macizos que lo
//! tocan— y un movimiento consulta hasta nueve celdas de la cápsula en tres posiciones distintas. Sin
//! caché, cada paso de cada jugador rasterizaría el mismo chunk varias veces.
//!
//! **La forma es la de [`crate::world::collision::SimChunkCache`], que ya resolvió este problema
//! exacto para el robapieles**: se PRECALIENTA el 3 × 3 alrededor de los dos extremos del movimiento
//! antes de resolver, y a partir de ahí el resolve es una lectura pura. Copiar esa forma no es pereza:
//! es que el préstamo mutable del caché de regiones y el inmutable del resolve no pueden coexistir, y
//! ése es justo el problema que `prewarm_for_move` resuelve.
//!
//! # Y sin capa en la clave
//!
//! La clave es [`Wg3ChunkCoord`], no `LayeredChunkPos`. Un ráster de WG3 cubre TODA la altura del
//! chunk (ADR-095 D2), que es exactamente la propiedad que hace posible un atrio de 6,40 m y por la
//! que este ADR existe: con capas de 4 m, subir una planta congelaba.

use std::collections::HashMap;

use super::chunk::{self, Wg3ChunkCoord};
use super::manifest::Wg3Manifest;
use super::raster::Wg3Raster;
use super::world::Wg3WorldCache;
use crate::world::Vec3;

/// Cuántos rásteres se guardan antes de tirar el caché entero.
///
/// **Poda tonta a propósito, igual que la de `Wg3WorldCache`**: al pasar del tope se tira TODO en vez
/// de mantener un LRU. Un LRU es estado con orden que mantener —lo que R3 evita— a cambio de ahorrar
/// algo que ya es barato, y el conjunto de trabajo de un movimiento son nueve chunks.
const MAX_CACHED_RASTERS: usize = 64;

/// Alto del cuerpo del jugador, en metros. Mismo valor que la cota a la que reporta su transform
/// estando de pie sobre un suelo a cero (`collision::PLAYER_BASE_Y`), y por la misma razón: es lo que
/// mide de los pies a la cabeza.
const PLAYER_BODY_M: f32 = 1.8;

/// Cuánto se permite subir sin saltar al buscar el suelo, en metros.
///
/// **Espejo de `plan::MAX_WALK_STEP_CM`, y por eso 0,30 y no 0,27**: la constante del plan es el tope
/// que la geometría no puede pasar; aquí hace falta un pelo más para que un peldaño que mide justo el
/// tope se encuentre pese al redondeo a centímetros del ráster. Sin este margen una escalera existe,
/// se dibuja entera y no se sube — que es exactamente el fallo que ADR-097 enmienda 1 documentó.
const STEP_UP_M: f32 = 0.30;

/// Los rásteres que el movimiento en curso puede leer.
#[derive(Debug, Default)]
pub struct Wg3CollisionCache {
    rasters: HashMap<Wg3ChunkCoord, Wg3Raster>,
    /// ADR-109 D7 — las celdas que ocupa lo que ha CONSTRUIDO un jugador.
    ///
    /// Van aparte del ráster y no dentro: el ráster es función pura de la semilla y se cachea por
    /// chunk, mientras que esto cambia cada vez que alguien pone o quita una pieza. Meterlo dentro
    /// obligaría a rasterizar de nuevo el chunk en cada colocación.
    ///
    /// **Sólo lo llenan los cachés de las CRIATURAS.** El del jugador se queda vacío, igual que en
    /// WG2: chocar con lo construido es cosa de los colliders de Unity, y meterlo aquí sería una
    /// segunda autoridad que puede contradecir a la primera.
    blocked: std::collections::HashSet<(i32, i32)>,
}

impl Wg3CollisionCache {
    pub fn new() -> Self {
        Self::default()
    }

    /// Rasteriza el 3 × 3 alrededor de los dos extremos del movimiento.
    ///
    /// Después de esto el resolve es lectura pura sobre el caché, que es lo que permite que
    /// `MoveSource` sea inmutable.
    pub fn prewarm_for_move(
        &mut self,
        regions: &mut Wg3WorldCache,
        manifest: &Wg3Manifest,
        world_seed: u64,
        from: Vec3,
        desired: Vec3,
    ) {
        if self.rasters.len() > MAX_CACHED_RASTERS {
            self.rasters.clear();
        }
        for end in [from, desired] {
            let c = Wg3ChunkCoord::containing(end.x, end.z);
            for dz in -1..=1 {
                for dx in -1..=1 {
                    let coord = Wg3ChunkCoord {
                        x: c.x + dx,
                        z: c.z + dz,
                    };
                    if self.rasters.contains_key(&coord) {
                        continue;
                    }
                    let region = regions.region_for(manifest, world_seed, coord);
                    // ADR-105 — y con los MACIZOS. Construir el ráster sin ellos es el fallo que ya
                    // se pagó en las sondas: un pretil que para en el servidor y no en el cliente, o
                    // al revés, y ningún error en ninguna parte.
                    let raster = chunk::build_chunk_raster_full(
                        manifest,
                        &region.placements_touching_chunk(manifest, coord),
                        &region.segments_touching_chunk(coord),
                        &region.carves_touching_chunk(coord),
                        &region.solids_touching_chunk(coord),
                        coord,
                    );
                    self.rasters.insert(coord, raster);
                }
            }
        }
    }

    /// ADR-109 D7 — las piezas construidas que la navegación tiene que rodear.
    ///
    /// **Misma aproximación que WG2 y por el mismo motivo:** el servidor conoce la posición, el
    /// `def_id` y la rotación de una pieza, pero NO su tamaño —las huellas viven en las definiciones
    /// de Unity—, así que cualquier cosa exacta pediría un campo de wire y un ADR. WG2 bloqueaba la
    /// celda de 2,5 m en la que caía; aquí se bloquea el mismo cuadrado, que a 0,5 m son 5 × 5
    /// celdas. Es lo correcto para muros y suelos, que es lo que la gente construye para esconderse.
    pub fn set_blocked_from(&mut self, positions: impl Iterator<Item = [f32; 3]>) {
        /// Medio lado del cuadrado que ocupa una pieza: la mitad de la celda de WG2.
        const HALF_M: f32 = 1.25;
        self.blocked.clear();
        for p in positions {
            let (cx0, cz0) = super::nav::cell_of(p[0] - HALF_M, p[2] - HALF_M);
            let (cx1, cz1) = super::nav::cell_of(p[0] + HALF_M, p[2] + HALF_M);
            for cz in cz0..=cz1 {
                for cx in cx0..=cx1 {
                    self.blocked.insert((cx, cz));
                }
            }
        }
    }

    /// ¿Cae ese punto en algo construido? Falso siempre en un caché al que nadie le ha dado piezas,
    /// que es el caso del jugador.
    pub fn is_blocked_xz(&self, x: f32, z: f32) -> bool {
        !self.blocked.is_empty() && self.blocked.contains(&super::nav::cell_of(x, z))
    }

    pub fn blocked_cell_count(&self) -> usize {
        self.blocked.len()
    }

    /// Como `raster_at`, pero para quien está fuera del módulo: `line_of_sight` necesita preguntar
    /// por un PUNTO y no por un cuerpo, y esta es la única puerta al ráster crudo.
    pub fn raster_for(&self, x: f32, z: f32) -> Option<&Wg3Raster> {
        self.raster_at(x, z)
    }

    fn raster_at(&self, x: f32, z: f32) -> Option<&Wg3Raster> {
        self.rasters.get(&Wg3ChunkCoord::containing(x, z))
    }

    /// ¿Estorba algo a un cuerpo de pie aquí?
    ///
    /// **Un chunk que no está en el caché BLOQUEA**, y eso se queda como estaba en WG2: fallar hacia
    /// sólido es lo que impide caer al vacío mientras el mundo se genera. Lo que cambia con ADR-106 es
    /// que ahora sí se puede generar — antes `update_ownership` sólo hacía la capa 0 y subir una
    /// planta dejaba al jugador contra un chunk que nunca iba a existir.
    pub fn blocked_at(&self, pos: Vec3, radius: f32) -> bool {
        // **El cuerpo empieza UN ESCALÓN por encima de los pies, y sin eso una escalera no se sube.**
        //
        // Un peldaño mide 25,5 cm de contrahuella, o sea que el siguiente cae DENTRO del cuerpo: la
        // muestra que aterriza sobre él dice «bloqueado» y el movimiento se resuelve como `Blocked`.
        // Y sólo pasa a veces, porque depende de en qué parte de la huella de 60 cm se esté cuando se
        // pregunta — que es exactamente el síntoma que se vio jugando: el robapieles clavado a mitad
        // de escalera, unas veces sí y otras no.
        //
        // Es lo mismo que hace cualquier `CharacterController` con su `stepOffset`, y el cliente ya lo
        // hace con 0,275: sin esta tolerancia el servidor frenaba donde el cliente pasaba, y esa
        // discrepancia se siente como un tirón sin causa.
        let feet = pos.y - PLAYER_BODY_M + STEP_UP_M;
        for (x, z) in capsule_samples(pos, radius) {
            let Some(raster) = self.raster_at(x, z) else {
                return true;
            };
            if raster.blocked_standing_at(x, feet, z, PLAYER_BODY_M - STEP_UP_M) {
                return true;
            }
        }
        false
    }

    /// La cota a la que queda el JUGADOR —no el suelo— apoyado aquí.
    ///
    /// Devuelve una Y de jugador porque es lo que devuelve su equivalente de WG2 y lo que espera todo
    /// `resolve_move_src`: mezclar las dos convenciones mete al jugador 1,8 m dentro del suelo o 1,8 m
    /// por encima, y las dos cosas se ven raro sin decir por qué.
    pub fn floor_y(&self, pos: Vec3) -> f32 {
        let feet = pos.y - PLAYER_BODY_M;
        let Some(raster) = self.raster_at(pos.x, pos.z) else {
            return pos.y;
        };
        // Se busca desde un peldaño POR ENCIMA de los pies: es lo que convierte una escalera en algo
        // que se sube en vez de una pared de 25 cm.
        match raster.floor_below(pos.x, feet + STEP_UP_M, pos.z) {
            Some(floor) => floor + PLAYER_BODY_M,
            // Sin nada debajo: no se inventa suelo. Se queda donde está y ya caerá quien tenga que
            // hacerlo — el daño y la caída son reglas de juego, no de mundo (ADR-106 D6).
            None => pos.y,
        }
    }

    /// La cota del suelo pisable, **o `None` si no hay ninguno**.
    ///
    /// Existe porque [`Self::floor_y`] conserva la cota de entrada cuando no encuentra suelo —lo que
    /// es correcto para el movimiento, que no debe teletransportar a nadie— y eso deja a quien
    /// pregunta sin forma de distinguir «el suelo está justo aquí» de «aquí no hay suelo». Para
    /// navegar, esa diferencia es la que separa una sala de un vacío: sin ella, un agujero de ADR-104
    /// se recorre como si fuera pasillo.
    pub fn floor_below_m(&self, x: f32, z: f32, from_floor: f32) -> Option<f32> {
        self.raster_at(x, z)?
            .floor_below(x, from_floor + STEP_UP_M, z)
    }

    /// La superficie pisable ESTRICTAMENTE por debajo de una cota. `None` si no hay.
    ///
    /// Hace falta para BAJAR. [`Self::floor_below_m`] se queda con la más alta que no pase del
    /// escalón, y en una escalera el rellano y el primer peldaño comparten celda —la huella mide 60 cm
    /// y la celda 50—, así que siempre devuelve el rellano: **el peldaño de abajo no se ofrece jamás y
    /// el grafo sube pero no baja**. Con esta consulta, una vecina puede ofrecer las dos.
    pub fn floor_strictly_below_m(&self, x: f32, z: f32, y: f32) -> Option<f32> {
        self.raster_at(x, z)?.floor_below(x, y - 0.01, z)
    }

    /// Hueco libre por encima del suelo de esta columna, en metros. `None` si no hay suelo.
    ///
    /// **Es la pregunta correcta para NAVEGAR, y `blocked_at` es la equivocada.** Aquélla barre una
    /// cápsula de radio 35 cm, y en una escalera esa cápsula siempre invade el peldaño de al lado —
    /// que está 25 cm más alto y por tanto dentro del cuerpo—, así que **ninguna escalera pasaría
    /// jamás el filtro**. La altura libre mide la COLUMNA: en un peldaño son los 3,80 m que hay hasta
    /// el techo del hueco, que es lo que de verdad decide si ahí cabe alguien de pie.
    pub fn headroom_m(&self, x: f32, z: f32, from_floor: f32) -> Option<f32> {
        self.raster_at(x, z)?
            .headroom_above_floor(x, from_floor + STEP_UP_M, z)
    }

    /// Qué bloqueó, para la traza. WG3 no tiene celdas ni banderas: las devuelve a cero y nombra el
    /// motivo, que es lo único que aquí puede ser cierto.
    pub fn describe(
        &self,
        pos: Vec3,
        radius: f32,
    ) -> ((i32, i32), (usize, usize), u16, &'static str) {
        for (x, z) in capsule_samples(pos, radius) {
            let coord = Wg3ChunkCoord::containing(x, z);
            let Some(raster) = self.rasters.get(&coord) else {
                return ((coord.x, coord.z), (0, 0), 0, "wg3_chunk_ausente");
            };
            if raster.blocked_standing_at(x, pos.y - PLAYER_BODY_M, z, PLAYER_BODY_M) {
                return ((coord.x, coord.z), (0, 0), 0, "wg3_macizo");
            }
        }
        let c = Wg3ChunkCoord::containing(pos.x, pos.z);
        ((c.x, c.z), (0, 0), 0, "wg3_libre")
    }
}

/// Radio en metros hasta donde se busca un sitio de pie, y paso de la búsqueda.
///
/// Medio metro de paso porque es la celda del ráster: buscar más fino no encuentra nada nuevo, y más
/// grueso se salta salas enteras. Cincuenta metros de radio es un chunk — más allá, el ráster ya no
/// está precalentado y la búsqueda mentiría diciendo «macizo» donde sólo hay «no lo sé».
const SPAWN_SEARCH_STEP_M: f32 = 0.5;
const SPAWN_SEARCH_RADIUS_M: f32 = 24.0;

/// Hueco libre mínimo sobre el suelo para considerar un sitio habitable, en metros.
///
/// El cuerpo mide 1,8 y se pide un pelo más: aparecer en un hueco donde cabes exacto es aparecer con
/// la cabeza dentro del techo en cuanto el suelo tenga un centímetro de irregularidad.
const SPAWN_MIN_HEADROOM_M: f32 = 2.0;

impl Wg3CollisionCache {
    /// ADR-106 — un sitio de pie en el mundo de WG3, buscando en anillos alrededor de `preferred`.
    ///
    /// **Hace falta porque el spawn de WG2 resuelve contra otra rejilla.** Con la autoridad ya movida,
    /// aparecer en una celda que WG2 da por buena y WG3 tiene maciza no te deja flotando: te deja
    /// ATASCADO, porque el movimiento ya lo resuelve el ráster y un macizo no se cruza. Es el fallo
    /// que ADR-106 D6 dejó nombrado como exclusión y que la propia D6 convierte en obligatorio en
    /// cuanto el movimiento cruza.
    ///
    /// Devuelve `None` cuando no hay nada habitable en el radio: **no se inventa un sitio**. Quien
    /// llama decide si eso es quedarse en el de WG2 o fallar, y las dos cosas son mejores que meter al
    /// jugador dentro de una pared con un número que parece válido.
    ///
    /// Tres condiciones, y las tres hacen falta: que haya SUELO debajo, que quepa uno de pie sobre él,
    /// y que estando ahí no le estorbe nada. La tercera no sobra — el suelo puede estar libre y tener
    /// un pilar justo al lado, dentro del radio de la cápsula.
    pub fn standable_near(&self, preferred: Vec3) -> Option<Vec3> {
        let rings = (SPAWN_SEARCH_RADIUS_M / SPAWN_SEARCH_STEP_M) as i32;
        for ring in 0..=rings {
            let mut best: Option<Vec3> = None;
            for dz in -ring..=ring {
                for dx in -ring..=ring {
                    // Sólo el BORDE del anillo: el interior ya se miró en la vuelta anterior.
                    if ring > 0 && dx.abs() != ring && dz.abs() != ring {
                        continue;
                    }
                    let x = preferred.x + dx as f32 * SPAWN_SEARCH_STEP_M;
                    let z = preferred.z + dz as f32 * SPAWN_SEARCH_STEP_M;
                    let Some(raster) = self.raster_at(x, z) else {
                        continue;
                    };
                    // Se busca el suelo desde ARRIBA del todo de la columna para que un atrio o una
                    // planta alta cuenten: partir de la cota preferida ataría el spawn a la planta
                    // baja para siempre.
                    let from_y = preferred.y.max(0.0) + 0.5;
                    let Some(floor) = raster.floor_below(x, from_y, z) else {
                        continue;
                    };
                    let head = raster
                        .headroom_above_floor(x, from_y, z)
                        .unwrap_or(f32::INFINITY);
                    if head < SPAWN_MIN_HEADROOM_M {
                        continue;
                    }
                    let candidate = Vec3::new(x, floor + PLAYER_BODY_M, z);
                    if self.blocked_at(candidate, crate::world::collision::PLAYER_RADIUS) {
                        continue;
                    }
                    // Dentro de un anillo se prefiere lo más cercano: los anillos son cuadrados y una
                    // esquina está un 41 % más lejos que un lado.
                    let d = (x - preferred.x).powi(2) + (z - preferred.z).powi(2);
                    if best.is_none_or(|b| {
                        (b.x - preferred.x).powi(2) + (b.z - preferred.z).powi(2) > d
                    }) {
                        best = Some(candidate);
                    }
                }
            }
            if best.is_some() {
                return best;
            }
        }
        None
    }
}

/// Las nueve muestras de la cápsula. **Copia deliberada de `collision::capsule_samples`**: son la
/// misma huella y tienen que serlo, porque un jugador que cruza de WG2 a WG3 con anchuras distintas se
/// quedaría enganchado justo en el cambio. Se copia en vez de compartirse porque la de allí es privada
/// del módulo y abrirla sólo para esto ataría los dos sistemas por un detalle.
fn capsule_samples(pos: Vec3, radius: f32) -> [(f32, f32); 9] {
    [
        (pos.x, pos.z),
        (pos.x - radius, pos.z),
        (pos.x + radius, pos.z),
        (pos.x, pos.z - radius),
        (pos.x, pos.z + radius),
        (pos.x - radius, pos.z - radius),
        (pos.x + radius, pos.z - radius),
        (pos.x - radius, pos.z + radius),
        (pos.x + radius, pos.z + radius),
    ]
}
