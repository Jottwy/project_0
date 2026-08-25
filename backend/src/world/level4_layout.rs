//! ADR-093 E1 — la MITAD DE COLISIÓN de la región del Level 4.
//!
//! Mismo reparto que la habitación construible (`build_room_layout`) y las salas
//! autoradas (`authored_room_layout`): la lógica compartida y la rejilla fina de 2,5 m
//! viven en `grid_gen::level4`; aquí se construye el `ChunkLayoutV1` de 5 m que lee la
//! colisión del jugador (`Level0Collision::resolve_move`). Las dos representaciones
//! rasterizan EL MISMO `Level4Layout`, y la paridad par del generador hace cada tile de
//! 5 m uniforme (sus 4 celdas finas coinciden): no pueden discrepar ni en media celda.

use super::architecture::chunk_generator::chunk_seed_layer;
use super::architecture::layout_grammars::TEMPLATE_OFFICE;
use super::chunk::{
    Chunk, ChunkLayer, ChunkLayoutV1, ChunkState, CELL_BLOCKED, CELL_WALKABLE, CELL_WALL,
    EDGE_KIND_OPEN, EDGE_KIND_WALL, LAYOUT_GRID_SIZE, ZONE_OFFICE,
};
use super::grid_gen::level4;
use crate::utils::ChunkPos;

/// Genera el chunk COMPLETO (colisión de 5 m) de una posición de la reserva de región.
///
/// `local` es el índice devuelto por `level4::region_chunk_local(pos)` — el llamador ya
/// lo comprobó. Estado `stabilized + anchored`: la región queda FUERA del chunk
/// displacement de ADR-067; que el intercambio simétrico eligiera un chunk de la
/// reserva mandaría medio Level 4 al Level 0 y viceversa.
pub fn generate_region_chunk(
    world_seed: u64,
    pos: ChunkPos,
    layer: ChunkLayer,
    local: (i32, i32),
) -> Chunk {
    let grid = LAYOUT_GRID_SIZE as usize;
    // ADR-093 E4: epoch VIGENTE, no la constante de sesión — el llamador (`World`, vía
    // `purge_level4_region_cache`) ya se aseguró de que este chunk se pida de nuevo cuando el
    // epoch cambia, así que aquí toca leer el mismo global que la rejilla fina. Igual con la
    // sala preservada (E4b): mismo global, misma razón.
    let layout_l4 = level4::generate_with_preserved(
        world_seed,
        level4::current_epoch(),
        level4::preserved_room(),
    );

    // Tile de 5 m (tx,tz) del chunk local → celda fina (2tx, 2tz) en coordenadas de
    // REGIÓN. Con paridad par, esa celda representa el tile entero.
    let open_at = |tx: i32, tz: i32| -> bool {
        if layer as i32 != level4::REGION_LAYER {
            return false;
        }
        let cell = (
            local.0 * grid as i32 * 2 + tx * 2,
            local.1 * grid as i32 * 2 + tz * 2,
        );
        layout_l4.cell_open(cell)
    };

    let mut cells = vec![CELL_WALL | CELL_BLOCKED; grid * grid];
    for tz in 0..grid {
        for tx in 0..grid {
            if open_at(tx as i32, tz as i32) {
                cells[tz * grid + tx] = CELL_WALKABLE;
            }
        }
    }

    let mut layout = ChunkLayoutV1::new(cells, 0, ZONE_OFFICE);

    // Aristas desde la misma verdad: OPEN solo entre dos tiles abiertos; todo lo demás,
    // WALL. `open_at` acepta índices fuera del chunk (−1, grid): consulta el layout
    // GLOBAL de región, así que un pasillo que cruza de chunk abre su arista de borde
    // en los dos chunks por construcción. Fuera de la reserva no hay tiles abiertos y
    // el perímetro exterior queda sellado.
    for tz in 0..grid as i32 {
        for bx in 0..=grid as i32 {
            let kind = if open_at(bx - 1, tz) && open_at(bx, tz) {
                EDGE_KIND_OPEN
            } else {
                EDGE_KIND_WALL
            };
            layout.set_edge_v(bx as usize, tz as usize, kind);
        }
    }
    for bz in 0..=grid as i32 {
        for tx in 0..grid as i32 {
            let kind = if open_at(tx, bz - 1) && open_at(tx, bz) {
                EDGE_KIND_OPEN
            } else {
                EDGE_KIND_WALL
            };
            layout.set_edge_h(tx as usize, bz as usize, kind);
        }
    }

    Chunk {
        pos,
        layer,
        state: ChunkState::Active {
            stabilized: true,
            anchored: true,
        },
        seed: chunk_seed_layer(world_seed, pos, layer),
        owner: None,
        entities: Vec::new(),
        items: Vec::new(),
        // Nunca elegible para displacement; el valor solo existe porque el campo existe.
        teleport_timer: f32::MAX,
        template_id: TEMPLATE_OFFICE,
        rotation: 0,
        mirrored: false,
        has_workbench: false,
        layout,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::world::grid_gen::level4::{
        region_chunk_local, REGION_CHUNKS, REGION_LAYER, REGION_ORIGIN_CHUNK,
    };

    /// Capa de la reserva como `ChunkLayer` — los helpers de chunk la piden en el tipo estrecho.
    const RL: ChunkLayer = REGION_LAYER as ChunkLayer;

    fn region_pos(lx: i32, lz: i32) -> ChunkPos {
        (REGION_ORIGIN_CHUNK.0 + lx, REGION_ORIGIN_CHUNK.1 + lz)
    }

    /// Verificación (a) de ADR-093 en la mitad de 5 m, cruzada contra la rejilla fina:
    /// cada tile caminable del layout de colisión coincide con sus 4 celdas finas, en
    /// TODOS los chunks de la reserva. Es el test cruzado que a `build_rooms` le faltó
    /// (auditoría 2026-08-18, punto 2) — aquí entra desde el primer día.
    #[test]
    fn every_5m_tile_matches_its_four_fine_cells() {
        for seed in [42u64, 7778] {
            for lx in 0..REGION_CHUNKS {
                for lz in 0..REGION_CHUNKS {
                    let pos = region_pos(lx, lz);
                    let local = region_chunk_local(pos, REGION_LAYER).unwrap();
                    let chunk = generate_region_chunk(seed, pos, RL, local);
                    let fine = crate::world::grid_gen::level4::generate_region_layer(
                        seed,
                        crate::world::grid_gen::level4::EPOCH_V1,
                        local,
                        REGION_LAYER,
                    );
                    let grid = LAYOUT_GRID_SIZE as usize;
                    for tx in 0..grid {
                        for tz in 0..grid {
                            let walk5 = chunk.layout.is_cell_walkable(tx, tz);
                            for (dx, dz) in [(0, 0), (1, 0), (0, 1), (1, 1)] {
                                let walk25 = fine.grid.get(tx * 2 + dx, tz * 2 + dz).is_walkable();
                                assert_eq!(
                                    walk5, walk25,
                                    "seed {seed} chunk ({lx},{lz}) tile ({tx},{tz}) subcelda ({dx},{dz})"
                                );
                            }
                        }
                    }
                }
            }
        }
    }

    #[test]
    fn region_chunks_are_deterministic_and_displacement_proof() {
        let pos = region_pos(1, 1);
        let local = region_chunk_local(pos, REGION_LAYER).unwrap();
        let a = generate_region_chunk(42, pos, RL, local);
        let b = generate_region_chunk(42, pos, RL, local);
        assert_eq!(a.layout, b.layout);
        assert!(
            matches!(
                a.state,
                ChunkState::Active {
                    stabilized: true,
                    anchored: true
                }
            ),
            "un chunk de la reserva elegible para displacement mezclaría los niveles"
        );
        assert_eq!(a.layout.zone_kind, ZONE_OFFICE);
        assert!(a.entities.is_empty() && a.items.is_empty());
    }

    /// Un pasillo que cruza de chunk abre la arista de borde EN LOS DOS chunks; donde no
    /// hay pasillo, el borde queda tabicado. Y el perímetro exterior de la reserva es
    /// muro siempre.
    #[test]
    fn seams_open_exactly_where_the_global_layout_crosses() {
        let grid = LAYOUT_GRID_SIZE as usize;
        for seed in [42u64, 7778] {
            let layout_l4 = level4::generate(seed, level4::EPOCH_V1);
            for lz in 0..REGION_CHUNKS {
                let left_pos = region_pos(0, lz);
                let right_pos = region_pos(1, lz);
                let left = generate_region_chunk(
                    seed,
                    left_pos,
                    RL,
                    region_chunk_local(left_pos, REGION_LAYER).unwrap(),
                );
                let right = generate_region_chunk(
                    seed,
                    right_pos,
                    RL,
                    region_chunk_local(right_pos, REGION_LAYER).unwrap(),
                );
                for tz in 0..grid {
                    let a = (grid as i32 * 2 - 2, lz * grid as i32 * 2 + tz as i32 * 2);
                    let b = (grid as i32 * 2, a.1);
                    let crosses = layout_l4.cell_open(a) && layout_l4.cell_open(b);
                    let expected = if crosses {
                        EDGE_KIND_OPEN
                    } else {
                        EDGE_KIND_WALL
                    };
                    assert_eq!(
                        left.layout.edge_v(grid, tz),
                        expected,
                        "seed {seed} fila {lz}/{tz}: borde este del chunk (0,{lz})"
                    );
                    assert_eq!(
                        right.layout.edge_v(0, tz),
                        expected,
                        "seed {seed} fila {lz}/{tz}: borde oeste del chunk (1,{lz})"
                    );
                }
            }

            // Perímetro exterior oeste de la reserva: tabicado entero.
            for lz in 0..REGION_CHUNKS {
                let pos = region_pos(0, lz);
                let chunk = generate_region_chunk(
                    seed,
                    pos,
                    RL,
                    region_chunk_local(pos, REGION_LAYER).unwrap(),
                );
                for tz in 0..grid {
                    assert_eq!(
                        chunk.layout.edge_v(0, tz),
                        EDGE_KIND_WALL,
                        "seed {seed}: el perímetro exterior de la reserva tiene un hueco"
                    );
                }
            }
        }
    }

    #[test]
    fn non_zero_layers_have_no_walkable_tiles() {
        let pos = region_pos(2, 0);
        let local = region_chunk_local(pos, REGION_LAYER).unwrap();
        for layer in [-1, 1] {
            let chunk = generate_region_chunk(42, pos, layer, local);
            let grid = LAYOUT_GRID_SIZE as usize;
            for tx in 0..grid {
                for tz in 0..grid {
                    assert!(!chunk.layout.is_cell_walkable(tx, tz), "capa {layer}");
                }
            }
        }
    }
}

// ─── ADR-093 E2: estado de región host-autoritativo (puerta de vuelta + ventana) ───
//
// Wire y dispatch viven en `network/` (protocol.rs, events.rs, handlers.rs, sync.rs); aquí
// vive SOLO la máquina de estados pura, para que se pueda testear sin construir un paquete.
// Es la misma separación que `build_room_layout` mantiene entre "dónde vive la sala" y "cómo
// se talla" — lo puro no depende de tokio ni de `NetworkManager`.

use std::time::{Duration, Instant};

/// Cruce hacia el Level 4 (Level 0 → región).
pub const DOOR_ENTRY: u8 = 0;
/// Cruce de vuelta (región → Level 0).
pub const DOOR_RETURN: u8 = 1;

/// Ventana de estabilización: una Return dentro de esta duración vuelve exactamente al punto
/// de entrada. Vencida, el destino empieza a derivar. Valor de arranque para E6 (tuning);
/// tocarlo no cambia forma de wire ni de protocolo.
pub const WINDOW_DURATION: Duration = Duration::from_secs(5 * 60);

/// Metros de deriva por minuto de overstay MÁS ALLÁ de `WINDOW_DURATION`. Ver ADR-093 punto 4.
pub const DRIFT_RADIUS_PER_MINUTE_M: f32 = 100.0;

/// ADR-093 (E4): duración de un epoch — cuánto tiempo pasa entre re-sorteos de la región. Reloj
/// INDEPENDIENTE de `WINDOW_DURATION` (la de la puerta de vuelta), aunque los dos anclan al
/// mismo instante de apertura (`opened_at`): la ventana de vuelta y la mutación del interior son
/// dos preocupaciones distintas que solo comparten el "cuándo empezó todo esto". Valor de
/// arranque para E6 (tuning); tocarlo no cambia forma de wire ni de protocolo.
pub const EPOCH_DURATION: Duration = Duration::from_secs(10 * 60);

/// Sal propia para el sorteo de rumbo — distinta de `LEVEL4_SALT` del generador de grafo para
/// que un cambio en uno no arrastre al otro (son sorteos independientes: layout vs. deriva).
const DRIFT_SALT: u64 = 0xBACB_0004_0FF1_D817;

#[inline]
fn splitmix64(mut z: u64) -> u64 {
    z = (z ^ (z >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    z ^ (z >> 31)
}

/// Rumbo unitario (XZ) de la deriva de una ventana, determinista por `(world_seed,
/// window_count)`. Cada ventana nueva sortea el suyo — dos ventanas sucesivas no derivan hacia
/// el mismo sitio por construcción (el contador entra en la semilla).
fn drift_direction(world_seed: u64, window_count: u32) -> (f32, f32) {
    let bits = splitmix64((world_seed ^ DRIFT_SALT).wrapping_add(u64::from(window_count)));
    // Los 32 bits altos a [0, 2π): suficiente resolución angular, y deja los bajos sin usar
    // por si algún día hiciera falta un segundo valor de la misma tirada.
    let angle = (bits >> 32) as f32 / u32::MAX as f32 * std::f32::consts::TAU;
    (angle.cos(), angle.sin())
}

/// TEMPORAL, a petición de Joel (2026-08-25): la vuelta aterriza SIEMPRE delante de la puerta de
/// entrada, sin deriva y sin depender de por dónde entraste.
///
/// Apaga a sabiendas la inestabilidad del enlace de ADR-093, que es una mecánica central — por eso
/// es una constante con nombre y no código borrado: `resolve_return_dest` y todos sus tests siguen
/// vivos y en verde, y volver a encenderla es cambiar este `true` por `false`.
///
/// No es solo comodidad de pruebas: el PORTAL (ver el otro lado al abrir la puerta) necesita saber
/// a dónde apuntar la cámara, y un destino que deriva con el tiempo no da un punto fijo al que
/// mirar. Mientras el portal exista en esta forma, el par de puertas tiene que ser fijo.
pub const RETURN_TO_FIXED_DOOR: bool = true;

/// Destino de vuelta puro: dentro de la ventana, el punto de entrada exacto; vencida, un punto
/// a radio proporcional al overstay en la dirección ya sorteada de esa ventana.
fn resolve_return_dest(
    entry_point: [f32; 3],
    direction: (f32, f32),
    elapsed: Duration,
) -> [f32; 3] {
    if elapsed <= WINDOW_DURATION {
        return entry_point;
    }
    let overstay_min = (elapsed - WINDOW_DURATION).as_secs_f32() / 60.0;
    let radius = overstay_min * DRIFT_RADIUS_PER_MINUTE_M;
    [
        entry_point[0] + direction.0 * radius,
        entry_point[1],
        entry_point[2] + direction.1 * radius,
    ]
}

/// Estado de la región, host-autoritativo. Un joiner mirroriza el broadcast (`Level4State`)
/// verbatim; los campos marcados HOST-ONLY nunca viajan por wire porque un joiner no los
/// necesita para nada — mismo reparto que `SettlingItem` frente a `StpItemInfo`.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct Level4RegionState {
    pub epoch: u32,
    pub window_open: bool,
    /// HOST-ONLY: el punto exacto al que responde una Return dentro de la ventana.
    pub entry_point: [f32; 3],
    /// HOST-ONLY: rumbo unitario (XZ) de la deriva, sorteado una vez al abrir la ventana.
    pub direction: (f32, f32),
    /// HOST-ONLY: instante en que se abrió la ventana vigente.
    pub opened_at: Option<Instant>,
    /// HOST-ONLY: ventanas abiertas en esta sesión — entra en la semilla del rumbo.
    pub window_count: u32,
    /// El destino de vuelta YA RESUELTO. Es lo único que un joiner guarda del broadcast; el
    /// host lo refresca antes de cada ronda (`refresh_return_dest`) así que emisor y espejo
    /// leen el mismo campo con el mismo significado.
    pub return_dest: [f32; 3],
}

impl Level4RegionState {
    /// Abre la ventana en el primer cruce de Entry. Si ya estaba abierta, no hace NADA — la
    /// ventana es COMPARTIDA (ADR-093 punto 4): el segundo, tercero... jugador que entra
    /// hereda el mismo reloj y el mismo punto de vuelta que fijó el primero.
    pub fn process_entry(&mut self, world_seed: u64, requester_pos: [f32; 3], now: Instant) {
        if self.window_open {
            return;
        }
        self.window_open = true;
        self.entry_point = requester_pos;
        self.opened_at = Some(now);
        self.direction = drift_direction(world_seed, self.window_count);
        self.window_count += 1;
        self.return_dest = self.entry_point;
    }

    /// Recalcula `return_dest` desde el tiempo transcurrido. La llaman tanto el broadcast
    /// periódico como `process_return`, así que las dos rutas leen la MISMA fórmula y nunca
    /// pueden divergir sobre qué destino está vigente ahora mismo.
    pub fn refresh_return_dest(&mut self, now: Instant) {
        self.return_dest = if self.window_open {
            let elapsed = self
                .opened_at
                .map(|t| now.duration_since(t))
                .unwrap_or_default();
            resolve_return_dest(self.entry_point, self.direction, elapsed)
        } else {
            self.entry_point
        };
    }

    /// Veredicto de una Return. Sin ventana abierta no hay de qué volver: el jugador se queda
    /// donde está — no-op seguro, nunca un teleport al origen del mundo. Es la garantía de
    /// "la puerta de vuelta SIEMPRE funciona" del ADR: ni con estado a medio inicializar
    /// produce un destino sin sentido.
    pub fn process_return(&mut self, requester_pos: [f32; 3], now: Instant) -> [f32; 3] {
        // Sin ventana abierta la deriva no tiene de dónde salir, pero la VUELTA sí: el destino de
        // portal es puramente geométrico. Devolver `requester_pos` aquí, como se hacía antes, no
        // era el "no-op seguro" que decía el comentario — dejaba al jugador ENCERRADO en la
        // reserva en cuanto reiniciaba el backend, porque la posición se persiste y la ventana no
        // (visto en el log del playtest del 2026-08-25: la puerta devolvía su propia posición una
        // y otra vez). Que la vuelta funcione siempre es la garantía que ADR-093 promete.
        if self.window_open {
            self.refresh_return_dest(now);
        }
        if RETURN_TO_FIXED_DOOR || !self.window_open {
            // La ventana y la deriva se siguen calculando arriba para que el estado que viaja al
            // joiner y el que se guarda no cambien de significado mientras este modo esté puesto:
            // lo único que se ignora es el resultado.
            return level4::portal_exit(requester_pos, false);
        }
        self.return_dest
    }

    /// ADR-093 (E3): punto único que procesa un cruce, sea `DOOR_ENTRY` o `DOOR_RETURN`. Los
    /// dos sitios que reciben un cruce — la petición host-directa del propio host (acción IPC)
    /// y la petición P2P de un joiner (`NetworkEvent::Level4DoorRequest`) — llaman a ESTA
    /// función, para que la rama de decisión (qué hacer con cada valor de `door`) exista en un
    /// solo lugar y no pueda divergir entre las dos rutas.
    pub fn process_door(
        &mut self,
        world_seed: u64,
        requester_pos: [f32; 3],
        door: u8,
        now: Instant,
    ) -> [f32; 3] {
        if door == DOOR_ENTRY {
            self.process_entry(world_seed, requester_pos, now);
            // El punto EQUIVALENTE al otro lado, no un punto fijo del vestíbulo: sales a la
            // misma distancia del plano a la que entraste, con tu mismo desplazamiento lateral,
            // andando en la misma dirección. Es lo que hace que el salto no se note y lo que
            // evita reaparecer encima de la puerta de salida (`portal_exit`).
            //
            // Antes esto devolvía `requester_pos` —"teletranspórtate a donde ya estás"— y ese fue
            // el bug que hizo que cruzar no hiciera nada visible durante tres playtests.
            level4::portal_exit(requester_pos, true)
        } else {
            self.process_return(requester_pos, now)
        }
    }

    /// ADR-093 (E4): epoch vigente dado el tiempo transcurrido desde que se abrió la ventana.
    /// Sin ventana abierta, epoch 0 — la región nunca ha mutado porque nadie ha entrado. Pura:
    /// el llamador (`game_loop`) es quien decide qué hacer cuando el resultado cambia (fijar
    /// `grid_gen::level4::set_current_epoch`, purgar la caché) — esta función solo responde
    /// "qué epoch tocaría ahora mismo".
    pub fn current_epoch(&self, now: Instant) -> u32 {
        match self.opened_at {
            Some(opened) => (now.duration_since(opened).as_secs() / EPOCH_DURATION.as_secs())
                .try_into()
                .unwrap_or(u32::MAX),
            None => 0,
        }
    }
}

// ─── ADR-093 E5: reglas de zona — densidad de fantasmas por epoch ───

/// Multiplicador de `density_scale` por epoch transcurrido: la presión sube con el tiempo que
/// la región lleva mutando, saturada en `DENSITY_SCALE_CAP` para que un epoch muy alto no
/// dispare la densidad sin límite. Valores de arranque para E6 (tuning); tocarlos no cambia
/// forma de wire ni de protocolo — son puro parámetro de `phantom_spawn::draw_into`.
pub const DENSITY_SCALE_PER_EPOCH: f32 = 0.5;
pub const DENSITY_SCALE_CAP: f32 = 4.0;

/// El factor de densidad vigente para un epoch dado. Epoch 0 (región recién abierta, o nadie
/// dentro) ⇒ 1.0, densidad normal.
pub fn density_scale_for_epoch(epoch: u32) -> f32 {
    (1.0 + epoch as f32 * DENSITY_SCALE_PER_EPOCH).min(DENSITY_SCALE_CAP)
}

/// Cuántos robapieles vale la reserva frente a un trozo cualquiera de laberinto, ANTES del
/// escalado por epoch.
///
/// `PHANTOM_LAYER_DENSITY[0] = 1.0` es la densidad del LABERINTO: uno esperado por bloque de
/// 200 m, medido para un mundo por el que se camina horas. La reserva es lo contrario — 150 m
/// cerrados que se visitan en incursiones de minutos con una ventana de vuelta corriendo — y
/// heredar esa cifra dejaba UN robapieles en el mapa entero (medido, `level4_population_probe`).
/// Con los facelings de ADR-093 neutrales dentro, ese único robapieles es LA amenaza de la
/// zona: la incursión no tiene tensión ninguna si hay uno y se le esquiva.
///
/// DILUCIÓN, que hay que tener presente al tocar este número: el sorteo reparte sobre el BLOQUE
/// entero (4×4 chunks) y la reserva solo ocupa 9 de esos 16, así que ~56 % de lo sorteado cae
/// dentro y el resto va a parar al laberinto vecino, a 10 km de cualquier jugador. La sonda
/// imprime las dos cuentas por separado justo para que este valor se elija mirando la de dentro.
///
/// No toca `PHANTOM_LAYER_DENSITY`: el laberinto no cambia, esto es un multiplicador local.
///
/// El 8 sale del barrido de la sonda sobre TRES semillas, no de una (con cuentas de un dígito la
/// suerte del sorteo pesa más que el propio valor: a 5, el epoch 0 daba 1 robapieles con la
/// semilla 42 y 3 con la 7778). Lo que produce, contando solo lo que cae dentro:
///   epoch 0 → 3-5    entras y hay uno cada dos o tres salas: presente, esquivable
///   epoch 2 → 7-8    ya cuesta cruzar la planta sin encontrarse a uno
///   epoch 8 → 15-17  el techo (`DENSITY_SCALE_CAP`), que se lee como "vete"
///
/// TECHO QUE NO SE VE EN ESOS NÚMEROS: `PHANTOM_ACTIVE_CAP` = 6 limita cuántos se SIMULAN a la
/// vez, así que a partir de epoch ~1 el sorteo produce más candidatos de los que llegan a
/// existir. La progresión no se pierde, cambia de forma: lo que sube no es cuántos hay delante
/// sino lo CERCA que aparecen y lo rápido que se repone otro al alejarte del anterior. Subir ese
/// tope es una decisión aparte y con medida propia — la zancada del planificador de IA está
/// dimensionada contra ese 6 (ver `faceling.rs`, nota de `PHANTOM_ACTIVE_CAP`).
pub const REGION_PHANTOM_DENSITY_MULT: f32 = 8.0;

/// ADR-093 (E5): ¿el bloque de sorteo de fantasmas (`phantom_spawn::block_of`, 200×200 m) cae
/// dentro de la reserva del Level 4? La reserva (3×3 chunks, 150×150 m) cabe ENTERA en un solo
/// bloque de sorteo (4×4 chunks, 200×200 m) porque `REGION_ORIGIN_CHUNK` está alineado a bloque
/// por construcción (2000 = 500 × `BLOCK_CHUNKS`) — no hace falta manejar solapamiento parcial
/// entre bloque y reserva, con comprobar el chunk de origen del bloque basta.
pub fn block_is_in_region(block: (i32, i32), layer: u8) -> bool {
    let chunk = (
        block.0 * crate::world::phantom_spawn::BLOCK_CHUNKS,
        block.1 * crate::world::phantom_spawn::BLOCK_CHUNKS,
    );
    level4::region_chunk_local(chunk, layer as i32).is_some()
}

// El invariante que hace correcto `block_is_in_region` sin manejar solapamiento parcial: la
// reserva tiene que caber en un bloque. Comprobación en tiempo de COMPILACIÓN — si alguien
// agranda `REGION_CHUNKS` más allá de `BLOCK_CHUNKS` algún día, esto deja de compilar en vez de
// fallar en silencio en el sorteo de fantasmas.
const _: () = assert!(level4::REGION_CHUNKS <= crate::world::phantom_spawn::BLOCK_CHUNKS);

#[cfg(test)]
mod region_state_tests {
    use super::*;

    /// EL test del bug de E3: cruzar la puerta de entrada tiene que DEJARTE DENTRO de la
    /// región. La versión anterior de este test afirmaba `entry_dest == requester_pos` — es
    /// decir, daba por bueno "teletranspórtate a donde ya estás", que es exactamente por lo que
    /// el playtest no hacía nada. Un test puede fijar un fallo tan bien como una garantía.
    #[test]
    fn crossing_the_entry_door_lands_you_inside_the_region_not_where_you_stood() {
        let now = Instant::now();
        let mut state = Level4RegionState::default();

        let standing_in_level0 = [5.0, 0.0, 5.0];
        let entry_dest = state.process_door(42, standing_in_level0, DOOR_ENTRY, now);

        assert_ne!(
            entry_dest, standing_in_level0,
            "la entrada no puede devolverte tu propia posición"
        );
        assert_eq!(
            entry_dest,
            level4::portal_exit(standing_in_level0, true),
            "se sale por el punto EQUIVALENTE al otro lado, no por un punto fijo"
        );
        assert!(
            level4::world_pos_to_region_cell(entry_dest).is_some(),
            "el destino de entrada tiene que caer dentro de la reserva"
        );
        assert!(state.window_open);

        // Y la vuelta te devuelve junto a la puerta por la que entraste. Ya NO es la inversa
        // exacta —el eje de cruce se fija a la puerta a propósito, ver `portal_exit`— pero sí
        // conserva el lateral, así que sales por donde entraste y no recentrado de golpe.
        let back = state.process_door(42, entry_dest, DOOR_RETURN, now);
        assert!(
            level4::world_pos_to_region_cell(back).is_none(),
            "volver saca de la reserva: {back:?}"
        );
        let from_door = ((back[0] - level4::ENTRY_DOOR_WORLD_POS[0]).powi(2)
            + (back[2] - level4::ENTRY_DOOR_WORLD_POS[2]).powi(2))
        .sqrt();
        assert!(
            from_door < 2.0,
            "se vuelve junto a la puerta de entrada, no a cualquier sitio: {from_door} m"
        );

        // Cualquier valor que no sea DOOR_ENTRY colapsa a Return — mismo criterio que
        // `CellType::kind()` con un byte desconocido: el lado seguro, no un pánico.
        let unknown_dest = state.process_door(42, [1.0, 0.0, 1.0], 255, now);
        assert_eq!(unknown_dest, level4::portal_exit([1.0, 0.0, 1.0], false));
    }

    /// El modo fijo hace lo que dice: mismo destino entres por donde entres, pidas desde donde
    /// pidas y hayas tardado lo que hayas tardado. Es lo que permite que el portal tenga un punto
    /// al que apuntar la cámara.
    #[test]
    fn the_fixed_return_lands_at_the_entry_door_whatever_happened() {
        assert!(
            RETURN_TO_FIXED_DOOR,
            "este test describe el modo fijo; con la deriva encendida el que manda es \
             resolve_return_dest y sus propios tests"
        );
        let now = Instant::now();
        let asking_from = [1.0, 0.0, 1.0];
        let expected = level4::portal_exit(asking_from, false);

        for entered_at in [[10.0, 0.0, 20.0], [-500.0, 0.0, 900.0], [0.0; 3]] {
            let mut state = Level4RegionState::default();
            state.process_entry(42, entered_at, now);
            // Dentro de la ventana, y muy pasada: el mismo punto en los dos casos. Lo que ya NO
            // interviene es por dónde entraste ni cuánto tardaste — sólo dónde cruzas.
            for elapsed in [
                Duration::from_secs(1),
                WINDOW_DURATION + Duration::from_secs(600),
            ] {
                assert_eq!(
                    state.process_return(asking_from, now + elapsed),
                    expected,
                    "entrada {entered_at:?} tras {elapsed:?}"
                );
            }
        }
    }

    #[test]
    fn entry_opens_the_window_once_and_ignores_later_entries() {
        let now = Instant::now();
        let mut state = Level4RegionState::default();
        state.process_entry(42, [10.0, 0.0, 20.0], now);
        assert!(state.window_open);
        assert_eq!(state.entry_point, [10.0, 0.0, 20.0]);
        assert_eq!(state.window_count, 1);

        // Segundo cruce, otra posición: NO desplaza el punto de entrada ni reabre el reloj.
        state.process_entry(42, [999.0, 0.0, 999.0], now + Duration::from_secs(30));
        assert_eq!(state.entry_point, [10.0, 0.0, 20.0]);
        assert_eq!(state.window_count, 1);
    }

    /// BLINDAJE (playtest 2026-08-25): el destino NO puede depender de que la posición de quien
    /// cruza sea correcta en el eje de cruce.
    ///
    /// En el log apareció `dest z=127.49` — 57,5 m de más — porque el backend creía al jugador
    /// DENTRO del vestíbulo mientras el cliente cruzaba la puerta de Level 0, y la traslación
    /// heredó el error entero. Con el eje de cruce fijado a la puerta, una posición desincronizada
    /// (o directamente absurda) sólo puede mover el resultado dentro del ancho del vano.
    #[test]
    fn a_desynced_position_cannot_throw_the_exit_off_the_door() {
        use level4::{PORTAL_EXIT_M, PORTAL_HALF_WIDTH_M};

        for entry in [true, false] {
            let door = if entry {
                level4::return_door_world_pos()
            } else {
                level4::ENTRY_DOOR_WORLD_POS
            };
            // Posiciones que un backend desincronizado podría traer: el otro nivel, muy lejos en
            // el eje de cruce, y el propio vestíbulo.
            for bogus in [
                [10075.0f32, 1.9, 70.0],
                [2.5, 0.0, -900.0],
                [-4000.0, 50.0, 4000.0],
            ] {
                let exit = level4::portal_exit(bogus, entry);
                assert!(
                    (exit[0] - door[0]).abs() <= PORTAL_HALF_WIDTH_M + 0.001,
                    "salida fuera del ancho del vano: {exit:?} para {bogus:?}"
                );
                assert!(
                    (exit[2] - door[2]).abs() - PORTAL_EXIT_M < 0.001,
                    "el eje de cruce tiene que ser fijo: {exit:?}"
                );
                assert!(
                    exit[1] < 1.0,
                    "se sale a ras de suelo, no a altura de ojos: una caída por cruce es el                      micro-salto que se sentía"
                );
            }
        }
    }

    /// Cruzar por CUALQUIERA de los dos lados tiene que dejarte en la cara transitable de la
    /// gemela. Un marco exento se cruza en los dos sentidos, y la traslación pura anterior
    /// escupía por la cara equivocada la mitad de las veces — contra la pared y del lado de
    /// "todavía no he cruzado", con lo que el primer paso te devolvía: tres cruces en 340 ms.
    #[test]
    fn crossing_from_either_side_lands_on_the_walkable_face() {
        let hall = level4::return_door_world_pos();
        let entry = level4::ENTRY_DOOR_WORLD_POS;

        for delta in [-0.1f32, 0.1] {
            // Entrar, viniendo de cada lado de la puerta de entrada.
            let exit = level4::portal_exit([entry[0], 0.0, entry[2] + delta], true);
            assert!(
                exit[2] > hall[2],
                "entrar deja siempre del lado del vestíbulo (+Z): {exit:?}"
            );
            assert!(
                level4::world_pos_to_region_cell(exit).is_some(),
                "y dentro de la reserva: {exit:?}"
            );

            // Y volver, desde cada lado de la de vuelta.
            let back = level4::portal_exit([hall[0], 0.0, hall[2] + delta], false);
            assert!(
                back[2] < entry[2],
                "volver deja siempre del lado del spawn (−Z): {back:?}"
            );
            assert!(
                level4::world_pos_to_region_cell(back).is_none(),
                "y fuera de la reserva: {back:?}"
            );
        }
    }

    /// Sin ventana abierta la vuelta TIENE que seguir funcionando. Devolver `requester_pos`
    /// —lo que se hacía antes, llamándolo "no-op seguro"— dejaba al jugador encerrado en la
    /// reserva en cuanto reiniciaba el backend: la posición se persiste, la ventana no.
    #[test]
    fn return_without_a_window_still_gets_you_out() {
        let mut state = Level4RegionState::default();
        let inside = level4::entry_hall_world_pos();
        let dest = state.process_return(inside, Instant::now());
        assert_ne!(dest, inside, "la puerta de vuelta no puede ser un no-op");
        assert!(
            level4::world_pos_to_region_cell(dest).is_none(),
            "sin ventana o con ella, volver saca de la reserva: {dest:?}"
        );
    }

    // Los tres de abajo prueban la DERIVA de ADR-093, que sigue implementada y viva aunque
    // `RETURN_TO_FIXED_DOOR` la deje fuera del camino de `process_return`. Van contra
    // `resolve_return_dest`/`refresh_return_dest`, que es donde vive la mecánica: así el modo
    // fijo se puede apagar mañana con la certeza de que lo que hay debajo nunca dejó de
    // comprobarse. Ir contra `process_return` los habría convertido en tests del modo, no de la
    // deriva.

    #[test]
    fn return_inside_the_window_goes_to_the_exact_entry_point() {
        let now = Instant::now();
        let mut state = Level4RegionState::default();
        state.process_entry(42, [10.0, 0.0, 20.0], now);

        state.refresh_return_dest(now + WINDOW_DURATION - Duration::from_secs(1));
        assert_eq!(state.return_dest, [10.0, 0.0, 20.0]);
    }

    #[test]
    fn return_past_the_window_drifts_proportionally_to_overstay() {
        let now = Instant::now();
        let mut state = Level4RegionState::default();
        state.process_entry(42, [0.0, 0.0, 0.0], now);

        state.refresh_return_dest(now + WINDOW_DURATION + Duration::from_secs(60));
        let at_1min_over = state.return_dest;
        let dist_1min = (at_1min_over[0].powi(2) + at_1min_over[2].powi(2)).sqrt();
        assert!(
            (dist_1min - DRIFT_RADIUS_PER_MINUTE_M).abs() < 0.01,
            "1 min de overstay debe derivar ~{DRIFT_RADIUS_PER_MINUTE_M} m, dio {dist_1min}"
        );

        state.refresh_return_dest(now + WINDOW_DURATION + Duration::from_secs(300));
        let at_5min_over = state.return_dest;
        let dist_5min = (at_5min_over[0].powi(2) + at_5min_over[2].powi(2)).sqrt();
        assert!(
            (dist_5min - 5.0 * DRIFT_RADIUS_PER_MINUTE_M).abs() < 0.01,
            "5 min de overstay debe derivar ~{} m, dio {dist_5min}",
            5.0 * DRIFT_RADIUS_PER_MINUTE_M
        );
    }

    #[test]
    fn everyone_leaves_through_the_same_door_however_long_they_stayed() {
        // Verificación (c) de ADR-093, releída para el modo fijo: la propiedad que importa es que
        // todos salgan POR LA MISMA PUERTA, no que aterricen en el mismo píxel. Con la traslación
        // de portal el destino conserva el offset de cada uno respecto al umbral —que es lo que
        // hace que el salto no se note y lo que evita que dos peers cruzando a la vez se
        // incrusten uno en otro— así que dos que cruzan a un metro salen a un metro.
        //
        // Cuando `RETURN_TO_FIXED_DOOR` se apague, el destino compartido exacto vuelve a ser cosa
        // de `resolve_return_dest`, que tiene sus propios tests.
        let now = Instant::now();
        let mut state = Level4RegionState::default();
        state.process_entry(7, [3.0, 0.0, 4.0], now);
        let overstay_instant = now + WINDOW_DURATION + Duration::from_secs(120);

        let door = level4::return_door_world_pos();
        let a = state.process_return([door[0] - 0.5, 0.0, door[2] - 0.1], overstay_instant);
        let b = state.process_return([door[0] + 0.5, 0.0, door[2] - 0.1], overstay_instant);

        let apart = ((a[0] - b[0]).powi(2) + (a[2] - b[2]).powi(2)).sqrt();
        assert!(
            apart < 2.0,
            "dos que cruzan el mismo umbral salen juntos; salieron a {apart} m"
        );
        for dest in [a, b] {
            assert!(
                level4::world_pos_to_region_cell(dest).is_none(),
                "volver saca de la reserva: {dest:?}"
            );
        }
    }

    #[test]
    fn different_windows_drift_in_different_directions() {
        let now = Instant::now();
        let mut state = Level4RegionState::default();
        state.process_entry(1, [0.0; 3], now);
        let overstay = now + WINDOW_DURATION + Duration::from_secs(180);
        state.refresh_return_dest(overstay);
        let first_dest = state.return_dest;

        // Cierra la primera ventana a mano (E4 lo hará de verdad) y abre otra con el mismo
        // punto de entrada: el CONTADOR cambia, así que el rumbo debe cambiar con él.
        state.window_open = false;
        state.process_entry(1, [0.0; 3], overstay);
        state.refresh_return_dest(overstay + WINDOW_DURATION + Duration::from_secs(180));
        let second_dest = state.return_dest;

        assert_ne!(
            first_dest, second_dest,
            "dos ventanas del mismo punto de entrada deben derivar distinto"
        );
    }

    #[test]
    fn refresh_return_dest_before_window_opens_is_the_entry_point_itself() {
        let mut state = Level4RegionState::default();
        state.refresh_return_dest(Instant::now());
        assert_eq!(state.return_dest, state.entry_point);
    }

    // ─── E4 ───

    #[test]
    fn epoch_is_zero_before_the_window_opens() {
        let state = Level4RegionState::default();
        assert_eq!(state.current_epoch(Instant::now()), 0);
    }

    #[test]
    fn epoch_advances_once_per_epoch_duration_since_open() {
        let now = Instant::now();
        let mut state = Level4RegionState::default();
        state.process_entry(42, [0.0; 3], now);

        assert_eq!(state.current_epoch(now), 0);
        assert_eq!(
            state.current_epoch(now + EPOCH_DURATION - Duration::from_secs(1)),
            0,
            "un segundo antes de vencer el epoch, sigue en 0"
        );
        assert_eq!(
            state.current_epoch(now + EPOCH_DURATION),
            1,
            "al vencer EXACTO, avanza a 1"
        );
        assert_eq!(
            state.current_epoch(now + EPOCH_DURATION * 3 + Duration::from_secs(1)),
            3,
            "3 epochs y pico -> 3, no 4: el pico no cuenta hasta completar el siguiente"
        );
    }
}

// ─── E5 ───

#[cfg(test)]
mod zone_rule_tests {
    use super::*;

    #[test]
    fn density_scale_starts_at_one_and_grows_with_epoch() {
        assert_eq!(density_scale_for_epoch(0), 1.0);
        assert_eq!(density_scale_for_epoch(1), 1.0 + DENSITY_SCALE_PER_EPOCH);
        assert_eq!(
            density_scale_for_epoch(2),
            1.0 + 2.0 * DENSITY_SCALE_PER_EPOCH
        );
    }

    #[test]
    fn density_scale_saturates_at_the_cap() {
        assert_eq!(density_scale_for_epoch(1_000_000), DENSITY_SCALE_CAP);
        assert!(density_scale_for_epoch(u32::MAX) <= DENSITY_SCALE_CAP);
    }

    #[test]
    fn only_the_regions_own_block_is_flagged() {
        use crate::world::phantom_spawn::BLOCK_CHUNKS;
        use level4::{REGION_LAYER, REGION_ORIGIN_CHUNK};

        let region_block = (
            REGION_ORIGIN_CHUNK.0 / BLOCK_CHUNKS,
            REGION_ORIGIN_CHUNK.1 / BLOCK_CHUNKS,
        );
        // El invariante REGION_CHUNKS <= BLOCK_CHUNKS (la reserva cabe en un bloque) lo fija un
        // `const _: ()` junto a `block_is_in_region`, no un assert aquí — clippy tiene razón en
        // que assertar dos constantes en tiempo de ejecución no comprueba nada que el compilador
        // no comprobara ya mejor.
        assert!(
            block_is_in_region(region_block, REGION_LAYER as u8),
            "el bloque que contiene el origen de la reserva debe marcarse"
        );

        assert!(
            !block_is_in_region((0, 0), 0),
            "el origen de Level 0 no es la reserva"
        );
        assert!(
            !block_is_in_region((region_block.0 + 1, region_block.1), REGION_LAYER as u8),
            "el bloque vecino no es la reserva"
        );
    }

    /// SONDA (no aserción): cuánta vida hay HOY dentro de la reserva, chunk a chunk, contra la
    /// que hay en un trozo equivalente de Level 0. Es el número que decide si "repartir
    /// entidades" necesita constantes nuevas o solo alinear la zona.
    ///
    /// `#[ignore]` con motivo, como las otras sondas del proyecto: imprime, no comprueba.
    /// Lanzar sola:
    /// `cargo test --manifest-path backend/Cargo.toml level4_population_probe -- --ignored --nocapture`
    #[test]
    #[ignore = "sonda de medición: imprime la población de la reserva, no comprueba nada"]
    fn level4_population_probe() {
        use crate::world::chunk::ZONE_OFFICE;
        use crate::world::faceling_spawn::{draw_adults, draw_child_pack};
        use crate::world::phantom_spawn::{draw_all, BLOCK_CHUNKS};
        use crate::world::zone_density::zone_kind_for;
        use level4::{REGION_CHUNKS, REGION_LAYER, REGION_ORIGIN_CHUNK};

        let seed = 42u64;
        let layer = REGION_LAYER as u8;

        let mut adults = 0usize;
        let mut packs = 0usize;
        let mut office_chunks = 0usize;
        println!("--- reserva Level 4 (seed {seed}) ---");
        for dz in 0..REGION_CHUNKS {
            for dx in 0..REGION_CHUNKS {
                let (cx, cz) = (REGION_ORIGIN_CHUNK.0 + dx, REGION_ORIGIN_CHUNK.1 + dz);
                let zone = zone_kind_for(seed, cx, cz, layer);
                let a = draw_adults(seed, cx, cz, layer, 1.0).len();
                let p = draw_child_pack(seed, cx, cz, layer, 1.0).len();
                office_chunks += usize::from(zone == ZONE_OFFICE);
                adults += a;
                packs += usize::from(p > 0);
                println!(
                    "  chunk ({cx},{cz}) zone_kind={zone} office={} adultos={a} crias={p}",
                    zone == ZONE_OFFICE
                );
            }
        }
        let block = (
            REGION_ORIGIN_CHUNK.0 / BLOCK_CHUNKS,
            REGION_ORIGIN_CHUNK.1 / BLOCK_CHUNKS,
        );
        // Los sorteos caen sobre el BLOQUE (4×4 chunks) y la reserva son 9 de esos 16, así que
        // solo cuenta lo que aterriza dentro — es lo que `phantom::level4_spot_is_usable`
        // despierta y lo único que un jugador puede encontrarse.
        let inside_count = |seed: u64, scale: f32| -> usize {
            draw_all(seed, block, layer, scale)
                .iter()
                .filter(|p| level4::world_pos_to_region_cell(**p).is_some())
                .count()
        };
        // Multiplicador vigente primero, y luego el barrido: elegir esta constante mirando UNA
        // semilla es elegirla por suerte del sorteo, que con cuentas de un dígito manda más que
        // el propio valor.
        let seeds = [42u64, 7778, 9_999_999];
        for mult in [REGION_PHANTOM_DENSITY_MULT, 8.0, 12.0, 16.0] {
            print!("  mult {mult:>5}: ");
            for epoch in [0u32, 1, 2, 4, 8] {
                // Exactamente lo que `phantom::level4_scaled_density` calcula, con el `base` de
                // sesión a 1.0. Si esa función cambia, este número deja de medir lo que corre
                // en el juego — única duplicación de la sonda, a propósito.
                let scale = mult * density_scale_for_epoch(epoch);
                let per_seed: Vec<usize> = seeds.iter().map(|&s| inside_count(s, scale)).collect();
                print!("epoch{epoch}={per_seed:?} ");
            }
            println!();
        }
        println!(
            "  TOTAL reserva: {adults} adultos, {packs} packs de crias, \
             {office_chunks}/{} chunks vistos como ZONE_OFFICE por zone_kind_for",
            REGION_CHUNKS * REGION_CHUNKS
        );

        // Control: el mismo tamaño de trozo en Level 0, para leer los de arriba con escala.
        let mut l0_adults = 0usize;
        let mut l0_office = 0usize;
        for dz in 0..REGION_CHUNKS {
            for dx in 0..REGION_CHUNKS {
                l0_adults += draw_adults(seed, dx, dz, 0, 1.0).len();
                l0_office += usize::from(zone_kind_for(seed, dx, dz, 0) == ZONE_OFFICE);
            }
        }
        println!(
            "--- control Level 0, mismo area: {l0_adults} adultos, \
             {l0_office}/{} chunks ZONE_OFFICE ---",
            REGION_CHUNKS * REGION_CHUNKS
        );
    }
}
