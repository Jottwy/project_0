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
        let feet = pos.y - PLAYER_BODY_M;
        for (x, z) in capsule_samples(pos, radius) {
            let Some(raster) = self.raster_at(x, z) else {
                return true;
            };
            if raster.blocked_standing_at(x, feet, z, PLAYER_BODY_M) {
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
