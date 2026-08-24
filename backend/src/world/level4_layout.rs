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
        if layer != 0 {
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
    use crate::world::grid_gen::level4::{region_chunk_local, REGION_CHUNKS, REGION_ORIGIN_CHUNK};

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
                    let local = region_chunk_local(pos).unwrap();
                    let chunk = generate_region_chunk(seed, pos, 0, local);
                    let fine = crate::world::grid_gen::level4::generate_region_layer(
                        seed,
                        crate::world::grid_gen::level4::EPOCH_V1,
                        local,
                        0,
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
        let local = region_chunk_local(pos).unwrap();
        let a = generate_region_chunk(42, pos, 0, local);
        let b = generate_region_chunk(42, pos, 0, local);
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
                let left =
                    generate_region_chunk(seed, left_pos, 0, region_chunk_local(left_pos).unwrap());
                let right = generate_region_chunk(
                    seed,
                    right_pos,
                    0,
                    region_chunk_local(right_pos).unwrap(),
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
                let chunk = generate_region_chunk(seed, pos, 0, region_chunk_local(pos).unwrap());
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
        let local = region_chunk_local(pos).unwrap();
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
        if !self.window_open {
            return requester_pos;
        }
        self.refresh_return_dest(now);
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
            requester_pos
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

/// ADR-093 (E5): ¿el bloque de sorteo de fantasmas (`phantom_spawn::block_of`, 200×200 m) cae
/// dentro de la reserva del Level 4? La reserva (3×3 chunks, 150×150 m) cabe ENTERA en un solo
/// bloque de sorteo (4×4 chunks, 200×200 m) porque `REGION_ORIGIN_CHUNK` está alineado a bloque
/// por construcción (2000 = 500 × `BLOCK_CHUNKS`) — no hace falta manejar solapamiento parcial
/// entre bloque y reserva, con comprobar el chunk de origen del bloque basta.
pub fn block_is_in_region(block: (i32, i32)) -> bool {
    let chunk = (
        block.0 * crate::world::phantom_spawn::BLOCK_CHUNKS,
        block.1 * crate::world::phantom_spawn::BLOCK_CHUNKS,
    );
    level4::region_chunk_local(chunk).is_some()
}

// El invariante que hace correcto `block_is_in_region` sin manejar solapamiento parcial: la
// reserva tiene que caber en un bloque. Comprobación en tiempo de COMPILACIÓN — si alguien
// agranda `REGION_CHUNKS` más allá de `BLOCK_CHUNKS` algún día, esto deja de compilar en vez de
// fallar en silencio en el sorteo de fantasmas.
const _: () = assert!(level4::REGION_CHUNKS <= crate::world::phantom_spawn::BLOCK_CHUNKS);

#[cfg(test)]
mod region_state_tests {
    use super::*;

    #[test]
    fn process_door_dispatches_entry_and_treats_anything_else_as_return() {
        let now = Instant::now();
        let mut state = Level4RegionState::default();

        let entry_dest = state.process_door(42, [5.0, 0.0, 5.0], DOOR_ENTRY, now);
        assert_eq!(entry_dest, [5.0, 0.0, 5.0]);
        assert!(state.window_open);

        // Dentro de la ventana: vuelve al punto de entrada exacto.
        let return_dest = state.process_door(42, [1.0, 0.0, 1.0], DOOR_RETURN, now);
        assert_eq!(return_dest, [5.0, 0.0, 5.0]);

        // Cualquier valor que no sea DOOR_ENTRY colapsa a Return — mismo criterio que
        // `CellType::kind()` con un byte desconocido: el lado seguro, no un pánico.
        let unknown_dest = state.process_door(42, [1.0, 0.0, 1.0], 255, now);
        assert_eq!(unknown_dest, [5.0, 0.0, 5.0]);
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

    #[test]
    fn return_with_no_window_open_is_a_safe_no_op() {
        let mut state = Level4RegionState::default();
        let dest = state.process_return([5.0, 0.0, 5.0], Instant::now());
        assert_eq!(
            dest,
            [5.0, 0.0, 5.0],
            "sin ventana, el jugador se queda donde está"
        );
    }

    #[test]
    fn return_inside_the_window_goes_to_the_exact_entry_point() {
        let now = Instant::now();
        let mut state = Level4RegionState::default();
        state.process_entry(42, [10.0, 0.0, 20.0], now);

        let dest = state.process_return(
            [1.0, 0.0, 1.0],
            now + WINDOW_DURATION - Duration::from_secs(1),
        );
        assert_eq!(dest, [10.0, 0.0, 20.0]);
    }

    #[test]
    fn return_past_the_window_drifts_proportionally_to_overstay() {
        let now = Instant::now();
        let mut state = Level4RegionState::default();
        state.process_entry(42, [0.0, 0.0, 0.0], now);

        let at_1min_over =
            state.process_return([0.0; 3], now + WINDOW_DURATION + Duration::from_secs(60));
        let dist_1min = (at_1min_over[0].powi(2) + at_1min_over[2].powi(2)).sqrt();
        assert!(
            (dist_1min - DRIFT_RADIUS_PER_MINUTE_M).abs() < 0.01,
            "1 min de overstay debe derivar ~{DRIFT_RADIUS_PER_MINUTE_M} m, dio {dist_1min}"
        );

        let at_5min_over =
            state.process_return([0.0; 3], now + WINDOW_DURATION + Duration::from_secs(300));
        let dist_5min = (at_5min_over[0].powi(2) + at_5min_over[2].powi(2)).sqrt();
        assert!(
            (dist_5min - 5.0 * DRIFT_RADIUS_PER_MINUTE_M).abs() < 0.01,
            "5 min de overstay debe derivar ~{} m, dio {dist_5min}",
            5.0 * DRIFT_RADIUS_PER_MINUTE_M
        );
    }

    #[test]
    fn all_returns_in_the_same_window_share_the_same_destination() {
        // Verificación (c) de ADR-093: dos peers que vuelven en la MISMA ventana ven el MISMO
        // destino, sea cual sea SU propia posición al pedir la vuelta.
        let now = Instant::now();
        let mut state = Level4RegionState::default();
        state.process_entry(7, [3.0, 0.0, 4.0], now);
        let overstay_instant = now + WINDOW_DURATION + Duration::from_secs(120);

        let dest_a = state.process_return([1.0, 0.0, 1.0], overstay_instant);
        let dest_b = state.process_return([-50.0, 0.0, 900.0], overstay_instant);
        assert_eq!(
            dest_a, dest_b,
            "misma ventana, mismo instante -> mismo destino"
        );
    }

    #[test]
    fn different_windows_drift_in_different_directions() {
        let now = Instant::now();
        let mut state = Level4RegionState::default();
        state.process_entry(1, [0.0; 3], now);
        let overstay = now + WINDOW_DURATION + Duration::from_secs(180);
        let first_dest = state.process_return([0.0; 3], overstay);

        // Cierra la primera ventana a mano (E4 lo hará de verdad) y abre otra con el mismo
        // punto de entrada: el CONTADOR cambia, así que el rumbo debe cambiar con él.
        state.window_open = false;
        state.process_entry(1, [0.0; 3], overstay);
        let second_dest = state.process_return(
            [0.0; 3],
            overstay + WINDOW_DURATION + Duration::from_secs(180),
        );

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
        use level4::REGION_ORIGIN_CHUNK;

        let region_block = (
            REGION_ORIGIN_CHUNK.0 / BLOCK_CHUNKS,
            REGION_ORIGIN_CHUNK.1 / BLOCK_CHUNKS,
        );
        // El invariante REGION_CHUNKS <= BLOCK_CHUNKS (la reserva cabe en un bloque) lo fija un
        // `const _: ()` junto a `block_is_in_region`, no un assert aquí — clippy tiene razón en
        // que assertar dos constantes en tiempo de ejecución no comprueba nada que el compilador
        // no comprobara ya mejor.
        assert!(
            block_is_in_region(region_block),
            "el bloque que contiene el origen de la reserva debe marcarse"
        );

        assert!(
            !block_is_in_region((0, 0)),
            "el origen de Level 0 no es la reserva"
        );
        assert!(
            !block_is_in_region((region_block.0 + 1, region_block.1)),
            "el bloque vecino no es la reserva"
        );
    }
}
