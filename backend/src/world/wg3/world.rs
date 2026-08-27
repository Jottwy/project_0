//! ADR-095 F4 — el mundo que se sirve: componer una vez, repartir por chunk.
//!
//! Sustituye al andamio `demo`, que ponía una pieza suelta por chunk sorteada por hash y no
//! prometía que dos vecinas conectaran. Aquí el mundo lo compone `wg3::compose`, así que las bocas
//! casan por construcción (R7) y lo que llega al cliente es un mundo que se puede andar.
//!
//! # A3 INTERINO, Y ESTÁ DECIDIDO ASÍ A PROPÓSITO
//!
//! El compositor es un recorrido finito desde una semilla. Eso significa que **el mundo se acaba**:
//! a unos cientos de metros del origen no hay más piezas y los chunks salen vacíos. No es un fallo
//! que se le haya escapado a nadie — ADR-095 deja el modelo de troceado fuera (A1 contrato de
//! frontera, A2 regiones, A3 mundo finito) y este fichero es A3 mientras ese ADR no exista.
//! Cuando llegue, lo que cambia es de dónde salen las colocaciones; el reparto por chunk de aquí
//! abajo sigue valiendo igual.
//!
//! # NO HAY GLOBAL (R3)
//!
//! La composición se cachea en una estructura que el bucle de juego POSEE y pasa por parámetro. Ni
//! `OnceLock` ni `static mut`: el manifiesto de las salas autoradas es un global de proceso y costó
//! una sesión entera de números falsos, porque en cuanto una sonda tocaba el entorno la siguiente
//! medía otro mundo sin enterarse. Además hace falta que sea local: un *joiner* no conoce la
//! semilla hasta el HandshakeAck, así que la composición es PEREZOSA y se rehace si la semilla
//! cambia debajo.
//!
//! # UNA PIEZA, UN CHUNK
//!
//! Una pieza a caballo de dos chunks se manda en UNO SOLO —el que contiene su CENTRO—, no en los
//! dos. El cliente monta un `GameObject` por chunk y no deduplica: mandarla dos veces la dibujaría
//! dos veces, con su colisión duplicada y peleando en el z-buffer. El andamio no tenía este
//! problema porque encajaba cada pieza entera dentro de su chunk, y perder esa propiedad es
//! justamente el precio de que ahora las piezas conecten.
//!
//! El CENTRO y no la esquina mínima porque acota el vuelo: ninguna pieza del catálogo llega a los
//! 50 m del chunk, así que centrada nunca asoma más allá de los vecinos inmediatos de su dueño, y
//! el cliente con radio 1 ve entera cualquier pieza que toque el chunk donde está. Hay un test que
//! lo comprueba sobre el catálogo real, para que añadir una nave de 60 m no lo rompa en silencio.
//!
//! El RÁSTER del servidor no usa este reparto: `chunk::build_chunk_raster` sigue recibiendo todas
//! las colocaciones que TOCAN el chunk, porque una pared que cruza la frontera tiene que bloquear a
//! los dos lados. Repartir es cosa de lo que se dibuja, no de lo que choca.

use super::chunk::{Wg3ChunkCoord, WG3_CHUNK_M};
use super::compose::{self, Wg3ComposerSettings};
use super::hash;
use super::junction;
use super::manifest::Wg3Manifest;
use super::placement::Wg3Placement;

/// Tope de piezas del mundo interino.
///
/// **Es un techo, no un objetivo, y la diferencia está medida.** Con 300 de tope, seis semillas dan
/// entre 20 y 268 piezas (de 134 m a 921 m de lado, de 0 a 9 ms de composición). El que manda no es
/// este número: es que la frontera se seca sola —hay tapones voluntarios y las candidatas que pisan
/// algo ya colocado se descartan—, así que el árbol termina cuando termina. Subirlo no agranda el
/// mundo; lo agrandará cerrar bucles, que ADR-095 deja abierto.
///
/// Lo que sí acota es el peor caso: el coste sube con el cuadrado, porque cada candidata comprueba
/// solape contra todo lo ya puesto. A 300 el peor caso medido son 9 ms, y se pagan una sola vez al
/// primer chunk que se pide.
///
/// CONSECUENCIA QUE HAY QUE SABER ANTES DE ELEGIR UNA SEMILLA: hay semillas que dan un mundo de dos
/// chunks de lado. No es un fallo del reparto ni del port; es el compositor, y está anotado como
/// límite conocido.
pub const INTERIM_BUDGET: usize = 300;

/// ADR-096 — lado de una región, en chunks. `3 × 50 m = 150 m`.
///
/// **Sale de un barrido, y el primer intento estaba mal.** Se eligió 8 (400 m) mirando la EXTENSIÓN
/// de los mundos sin acotar —de 134 a 921 m— y medirlo lo desmintió: llenados del 1 al 12 %. La
/// extensión no es el dato bueno; un mundo puede medir 900 m y ser cuatro ramas finas. El dato
/// bueno es la superficie CONSTRUIDA. Barrido (`region_size_sweep`, siete regiones cada uno):
///
/// | chunks | lado  | llenado medio | mínimo | piezas |
/// |--------|-------|---------------|--------|--------|
/// | 2      | 100 m | 19 %          | 9 %    | 6–17   |
/// | **3**  | 150 m | **20 %**      | **11 %** | 13–30 |
/// | 4      | 200 m | 14 %          | 5 %    | 15–42  |
/// | 6      | 300 m | 6 %           | 1 %    | 4–60   |
/// | 8      | 400 m | 6 %           | 1 %    | 5–85   |
///
/// Gana 3 en las dos métricas, y la que decide es **el mínimo**: una región al 1 % es un descampado
/// con cuatro edificios sueltos, y con regiones grandes eso le toca a alguien. A 150 m el peor caso
/// sigue teniendo 13 piezas.
///
/// EL PRECIO, dicho: una costura de región cada 150 m. Es frecuente, y hace el contrato de junta más
/// urgente de lo que parecía — pero un mundo con costuras cada 150 m y siempre poblado se anda mejor
/// que uno con costuras cada 400 m del que un tercio está vacío.
///
/// Y el 20 % de llenado sigue siendo poco en absoluto: cuatro quintos de cada región son vacío. Eso
/// NO lo arregla el tamaño, lo arreglará que el compositor deje de ser un árbol. Es el mismo límite
/// que ya se midió en `closing_loops_measures_how_much_more_world_there_is`.
pub const REGION_CHUNKS: i32 = 3;

/// Lado de región en metros.
pub const REGION_M: f32 = REGION_CHUNKS as f32 * WG3_CHUNK_M;

/// Coordenada de región. La región `(0,0)` va de `(0,0)` a `(150,150)` — `REGION_CHUNKS` chunks de
/// `WG3_CHUNK_M`, y NO un número fijo: este comentario decía 400 desde cuando la región eran 8
/// chunks, y un comentario que miente sobre los límites de la región es peor que no tenerlo.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Wg3RegionCoord {
    pub x: i32,
    pub z: i32,
}

impl Wg3RegionCoord {
    /// La región a la que pertenece un chunk. `div_euclid` y no `/`: la división trunca hacia cero,
    /// así que los chunks −1 y +1 caerían en la misma región y todo el hemisferio negativo saldría
    /// espejado. Es el mismo fallo que ya obligó a `div_euclid` dos veces en este proyecto.
    pub fn of_chunk(chunk: Wg3ChunkCoord) -> Self {
        Self {
            x: chunk.x.div_euclid(REGION_CHUNKS),
            z: chunk.z.div_euclid(REGION_CHUNKS),
        }
    }

    /// `(min_x, min_z, max_x, max_z)` en metros.
    pub fn bounds(&self) -> (f32, f32, f32, f32) {
        let x = self.x as f32 * REGION_M;
        let z = self.z as f32 * REGION_M;
        (x, z, x + REGION_M, z + REGION_M)
    }

    /// Semilla del compositor para esta región.
    ///
    /// Mezcla la del mundo con la coordenada, así que dos regiones vecinas del mismo mundo componen
    /// cosas distintas y la misma región del mismo mundo compone siempre lo mismo — que es todo lo
    /// que A2 necesita para ser determinista sin que las regiones se hablen.
    pub fn composer_seed(&self, world_seed: u64) -> i32 {
        // `composer_seed(world_seed)` ya recorta los 32 bits bajos y ese recorte está documentado
        // allí: dos semillas que solo difieran en los altos dan el mismo mundo de WG3.
        hash::mix(composer_seed(world_seed), self.x, self.z, REGION_SALT) as u32 as i32
    }
}

/// Sal propia del sorteo de región, para que la coordenada de región no quede correlacionada con
/// ninguna otra decisión que se tome en el mismo punto.
const REGION_SALT: i32 = 0x5EED_0A2Bu32 as i32;

/// Semilla del compositor a partir de la del mundo.
///
/// La del mundo es `u64` y la del compositor `i32`, porque nació de un `int` de C# y el oráculo lo
/// fija. Se cogen los 32 bits bajos y se reinterpretan: es determinista, no depende de la
/// plataforma y no hay nada que elegir. Dos semillas que solo se diferencien en los 32 bits altos
/// dan el mismo mundo de WG3 — se acepta, y aquí queda dicho para que no se descubra como un
/// "hallazgo".
pub fn composer_seed(world_seed: u64) -> i32 {
    world_seed as u32 as i32
}

/// El mundo compuesto de una semilla, listo para repartir.
#[derive(Debug, Clone)]
pub struct Wg3ServedWorld {
    world_seed: u64,
    placements: Vec<Wg3Placement>,
}

impl Wg3ServedWorld {
    /// ADR-096 — compone UNA REGIÓN: acotada a su caja y sembrada en su centro.
    ///
    /// Es lo que hace el mundo infinito sin tocar el compositor: una región es exactamente lo que
    /// éste ya sabía hacer —un recorrido finito desde una semilla—, y hay infinitas regiones.
    ///
    /// **Las regiones nacen SELLADAS en este paso, y es deuda declarada.** ADR-096 pide que no lo
    /// estén; el contrato de junta que las abre viene después y necesita este cimiento puesto.
    /// Mientras tanto la costura entre regiones se ve, y verla es lo que dirá si el contrato tiene
    /// que ser fino o basta con poco.
    pub fn compose_region(manifest: &Wg3Manifest, world_seed: u64, region: Wg3RegionCoord) -> Self {
        let bounds = region.bounds();

        // ADR-096 — el contrato de junta. Las puertas salen del BORDE, no de la región, así que la
        // vecina calcula las mismas sin que nadie pregunte a nadie. La semilla que las sortea es la
        // del MUNDO y no la de la región: la de la región es distinta a cada lado del borde y daría
        // dos listas de puertas que no casan.
        let stub = junction::gate_stub_piece(manifest);
        let anchors: Vec<compose::Wg3Anchor> = match stub {
            Some(stub) => {
                junction::gates_of_region(composer_seed(world_seed), region.x, region.z, bounds)
                    .into_iter()
                    .filter_map(|gate| junction::stub_anchor(manifest, stub, gate))
                    .collect()
            }
            None => {
                // Sin pieza que sirva de tramo no hay puertas, y el mundo vuelve a ser un tablero de
                // regiones selladas. Se avisa fuerte: es una propiedad del CATÁLOGO que se rompe sin
                // que nada falle.
                log::error!(
                    "[wg3] el catálogo no tiene ninguna pieza que sirva de tramo de puerta — las \
                     regiones quedarán selladas"
                );
                Vec::new()
            }
        };

        let settings = Wg3ComposerSettings {
            budget: INTERIM_BUDGET,
            close_loops: true,
            bounds: Some(bounds),
            anchors,
            ..Wg3ComposerSettings::default()
        };
        let composed = compose::compose(region.composer_seed(world_seed), manifest, &settings);
        Self {
            world_seed,
            placements: composed.placements.iter().map(|c| c.placement).collect(),
        }
    }

    /// Compone con los ajustes del mundo interino.
    pub fn compose(manifest: &Wg3Manifest, world_seed: u64) -> Self {
        let settings = Wg3ComposerSettings {
            budget: INTERIM_BUDGET,
            // ADR-096 — el mundo que se SIRVE cierra bucles; el que fija el oráculo, no. El default
            // es `false` porque el oráculo vigila la paridad con C#, y C# no los cierra: encenderlo
            // ahí pondría rojo lo único que caza una deriva silenciosa entre los dos idiomas.
            close_loops: true,
            ..Wg3ComposerSettings::default()
        };
        Self::compose_with(manifest, world_seed, &settings)
    }

    /// Compone con ajustes dados. Para tests y para cuando las perillas dejen de ser constantes.
    pub fn compose_with(
        manifest: &Wg3Manifest,
        world_seed: u64,
        settings: &Wg3ComposerSettings,
    ) -> Self {
        let composed = compose::compose(composer_seed(world_seed), manifest, settings);
        Self {
            world_seed,
            placements: composed.placements.iter().map(|c| c.placement).collect(),
        }
    }

    pub fn world_seed(&self) -> u64 {
        self.world_seed
    }

    /// Todas las colocaciones del mundo, en el orden en que las puso el compositor.
    pub fn placements(&self) -> &[Wg3Placement] {
        &self.placements
    }

    /// El chunk que dibuja esta pieza: el que contiene su centro.
    pub fn owner_chunk(manifest: &Wg3Manifest, placement: &Wg3Placement) -> Option<Wg3ChunkCoord> {
        let piece = manifest.piece(placement.piece)?;
        let (min_x, min_z, max_x, max_z) = placement.bounds(piece);
        Some(Wg3ChunkCoord::containing(
            (min_x + max_x) * 0.5,
            (min_z + max_z) * 0.5,
        ))
    }

    /// Lo que se manda por el cable para un chunk: las piezas de las que ESE chunk es dueño.
    ///
    /// La lista vacía es un resultado válido y frecuente —el mundo es finito—, no una señal de que
    /// algo falta.
    pub fn placements_for_chunk(
        &self,
        manifest: &Wg3Manifest,
        coord: Wg3ChunkCoord,
    ) -> Vec<Wg3Placement> {
        self.placements
            .iter()
            .filter(|p| Self::owner_chunk(manifest, p) == Some(coord))
            .copied()
            .collect()
    }

    /// Las colocaciones que TOCAN el chunk, dueñas o no. Es lo que necesita el ráster de colisión:
    /// una pared que cruza la frontera bloquea a los dos lados.
    pub fn placements_touching_chunk(
        &self,
        manifest: &Wg3Manifest,
        coord: Wg3ChunkCoord,
    ) -> Vec<Wg3Placement> {
        let (cmin_x, cmin_z, cmax_x, cmax_z) = coord.bounds();
        self.placements
            .iter()
            .filter(|p| match manifest.piece(p.piece) {
                Some(piece) => {
                    let (min_x, min_z, max_x, max_z) = p.bounds(piece);
                    max_x > cmin_x && min_x < cmax_x && max_z > cmin_z && min_z < cmax_z
                }
                None => false,
            })
            .copied()
            .collect()
    }
}

/// La composición cacheada del bucle de juego.
///
/// Perezosa porque un *joiner* no sabe la semilla hasta el HandshakeAck, y rehecha si la semilla
/// cambia porque servir el mundo de otra semilla es servir un mundo que el resto de la partida no
/// ve. Componer trescientas piezas cuesta milisegundos, así que rehacerla no necesita más
/// ceremonia que ésta.
#[derive(Debug, Default)]
pub struct Wg3WorldCache {
    world_seed: u64,
    regions: std::collections::HashMap<Wg3RegionCoord, Wg3ServedWorld>,
}

/// Regiones vivas a la vez. Cada una son ~300 piezas de `Wg3Placement` (11 bytes) más el vector:
/// unos kilobytes. El tope existe para que una sesión larga que recorra mucho mundo no acumule sin
/// fin, no porque una región pese.
const MAX_CACHED_REGIONS: usize = 16;

impl Wg3WorldCache {
    /// ADR-096 — la región de un chunk, componiéndola si hace falta.
    ///
    /// La caché se vacía entera si cambia la semilla: servir el mundo de otra semilla es servir un
    /// mundo que el resto de la partida no ve, y un *joiner* no la sabe hasta el HandshakeAck.
    pub fn region_for(
        &mut self,
        manifest: &Wg3Manifest,
        world_seed: u64,
        coord: Wg3ChunkCoord,
    ) -> &Wg3ServedWorld {
        if self.world_seed != world_seed {
            self.regions.clear();
            self.world_seed = world_seed;
        }

        let region = Wg3RegionCoord::of_chunk(coord);
        if !self.regions.contains_key(&region) {
            // Poda tonta y a propósito: al pasar del tope se tira TODO en vez de mantener un LRU.
            // Recomponer cuesta milisegundos, y un LRU aquí sería estado con orden que mantener —
            // justo lo que R3 evita— a cambio de ahorrar algo que ya es barato.
            if self.regions.len() >= MAX_CACHED_REGIONS {
                self.regions.clear();
            }
            let world = Wg3ServedWorld::compose_region(manifest, world_seed, region);
            log::info!(
                "[wg3] región ({},{}) compuesta para la semilla {world_seed}: {} piezas",
                region.x,
                region.z,
                world.placements().len()
            );
            self.regions.insert(region, world);
        }
        self.regions.get(&region).expect("se acaba de componer")
    }
}
