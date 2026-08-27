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

use super::chunk::Wg3ChunkCoord;
use super::compose::{self, Wg3ComposerSettings};
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
    world: Option<Wg3ServedWorld>,
}

impl Wg3WorldCache {
    /// El mundo de esta semilla, componiéndolo si hace falta.
    pub fn world_for(&mut self, manifest: &Wg3Manifest, world_seed: u64) -> &Wg3ServedWorld {
        let stale = match &self.world {
            Some(w) => w.world_seed != world_seed,
            None => true,
        };
        if stale {
            let world = Wg3ServedWorld::compose(manifest, world_seed);
            log::info!(
                "[wg3] mundo compuesto para la semilla {world_seed}: {} piezas",
                world.placements().len()
            );
            self.world = Some(world);
        }
        self.world.as_ref().expect("se acaba de componer")
    }
}
