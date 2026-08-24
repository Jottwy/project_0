//! Generador de grafo del Level 4 (ADR-093, etapa E0).
//!
//! Sortea salas rectangulares dentro del rect de región y las conecta con pasillos
//! ortogonales en L, garantizando conectividad total POR CONSTRUCCIÓN: cada sala nueva
//! se conecta a la componente ya conectada (árbol), y después se añaden aristas extra
//! para crear ciclos (rutas de escape).
//!
//! Unidades: celdas de 2,5 m (las de `grid_gen`). Invariante de PARIDAD: todo origen y
//! todo tamaño son PARES, para que el layout sea representable en la rejilla de colisión
//! de 5 m sin el modo de fallo de ADR-083 enmienda 3 (origen impar = sala que nunca
//! aparece).
//!
//! Determinismo: `(seed_base, epoch)` ⇒ mismo layout, byte a byte. Sin reloj, sin
//! entropía externa (mismo contrato que `Level0Builder`).

use rand::rngs::StdRng;
use rand::{Rng, SeedableRng};

/// Lado de la región en celdas de 2,5 m (48 celdas = 120 m). Valor v1 del roadmap;
/// lo fija el playtest, no este módulo.
pub const REGION_CELLS: i32 = 48;

/// Cuántas salas intenta colocar el sorteo (el espacio puede admitir menos).
pub const ROOM_TARGET: usize = 12;

/// Salas mínimas para dar el layout por válido; por debajo, el sorteo es un bug.
pub const ROOM_MIN_COUNT: usize = 6;

/// Grosor de pasillo en celdas (2 celdas = 5 m, un tile de colisión).
pub const CORRIDOR_THICKNESS: i32 = 2;

/// Separación mínima entre rects de sala, en celdas.
const ROOM_SEPARATION: i32 = 2;

/// Lados de sala permitidos (pares, dentro de los topes reales de salas autoradas).
const ROOM_SIDES: [i32; 4] = [6, 8, 10, 12];

/// Intentos de colocación antes de rendirse con las salas que hayan cabido.
const PLACEMENT_ATTEMPTS: usize = 200;

/// Sal del nivel. Mismo esquema que `Level0Builder` (`world_seed ^ SALT`); no se cambia
/// sin cambiar el mundo de todos los seeds existentes.
const LEVEL4_SALT: u64 = 0xBACB_0004_0FF1_CE00;

/// Rect alineado a ejes en celdas de 2,5 m. `min` inclusivo, `size` en celdas.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CellRect {
    pub min: (i32, i32),
    pub size: (i32, i32),
}

impl CellRect {
    pub fn max_exclusive(&self) -> (i32, i32) {
        (self.min.0 + self.size.0, self.min.1 + self.size.1)
    }

    pub fn center(&self) -> (i32, i32) {
        (self.min.0 + self.size.0 / 2, self.min.1 + self.size.1 / 2)
    }

    /// Centro forzado a coordenadas pares (ancla de pasillos, hereda la paridad).
    pub fn center_even(&self) -> (i32, i32) {
        let (cx, cz) = self.center();
        (cx & !1, cz & !1)
    }

    pub fn contains(&self, cell: (i32, i32)) -> bool {
        let (mx, mz) = self.max_exclusive();
        cell.0 >= self.min.0 && cell.0 < mx && cell.1 >= self.min.1 && cell.1 < mz
    }

    fn inflated(&self, by: i32) -> CellRect {
        CellRect {
            min: (self.min.0 - by, self.min.1 - by),
            size: (self.size.0 + 2 * by, self.size.1 + 2 * by),
        }
    }

    fn overlaps(&self, other: &CellRect) -> bool {
        let (amx, amz) = self.max_exclusive();
        let (bmx, bmz) = other.max_exclusive();
        self.min.0 < bmx && other.min.0 < amx && self.min.1 < bmz && other.min.1 < amz
    }
}

/// Una sala colocada. `is_return_room` marca la sala que contendrá la puerta de vuelta
/// (etapa E3); siempre existe exactamente una.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlacedRoom {
    pub rect: CellRect,
    pub is_return_room: bool,
}

/// Layout abstracto de la región para un `(seed_base, epoch)`. Los pasillos son rects
/// de grosor `CORRIDOR_THICKNESS`; pueden solapar salas y entre sí (el tallado lo
/// resuelve: celda vaciada es celda vaciada).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Level4Layout {
    pub epoch: u32,
    pub rooms: Vec<PlacedRoom>,
    pub corridors: Vec<CellRect>,
}

/// SplitMix64 — misma difusión que `grid_gen::generator` (ADR-019), local para no abrir
/// la visibilidad de aquel módulo.
#[inline]
fn splitmix64(mut z: u64) -> u64 {
    z = (z ^ (z >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    z ^ (z >> 31)
}

fn derive_seed(seed_base: u64, epoch: u32) -> u64 {
    splitmix64(
        (seed_base ^ LEVEL4_SALT)
            .wrapping_add(0x9e37_79b9_7f4a_7c15)
            .wrapping_add(u64::from(epoch)),
    )
}

/// Genera el layout de la región. Determinista: misma entrada ⇒ misma salida.
pub fn generate(seed_base: u64, epoch: u32) -> Level4Layout {
    let mut rng = StdRng::seed_from_u64(derive_seed(seed_base, epoch));

    let rooms = place_rooms(&mut rng);
    let corridors = connect_rooms(&mut rng, &rooms);

    Level4Layout {
        epoch,
        rooms,
        corridors,
    }
}

fn place_rooms(rng: &mut StdRng) -> Vec<PlacedRoom> {
    let mut rooms: Vec<PlacedRoom> = Vec::new();
    for _ in 0..PLACEMENT_ATTEMPTS {
        if rooms.len() >= ROOM_TARGET {
            break;
        }
        let w = ROOM_SIDES[rng.gen_range(0..ROOM_SIDES.len())];
        let h = ROOM_SIDES[rng.gen_range(0..ROOM_SIDES.len())];
        // Origen par dentro de bounds: sorteo en la rejilla de paso 2.
        let max_x = (REGION_CELLS - w) / 2;
        let max_z = (REGION_CELLS - h) / 2;
        if max_x < 0 || max_z < 0 {
            continue;
        }
        let rect = CellRect {
            min: (rng.gen_range(0..=max_x) * 2, rng.gen_range(0..=max_z) * 2),
            size: (w, h),
        };
        let padded = rect.inflated(ROOM_SEPARATION);
        if rooms.iter().any(|r| r.rect.overlaps(&padded)) {
            continue;
        }
        rooms.push(PlacedRoom {
            rect,
            is_return_room: rooms.is_empty(),
        });
    }
    rooms
}

fn connect_rooms(rng: &mut StdRng, rooms: &[PlacedRoom]) -> Vec<CellRect> {
    let mut corridors = Vec::new();
    // Árbol: cada sala i se conecta a la sala YA conectada con centro más cercano
    // (manhattan). Conectividad total por construcción.
    for i in 1..rooms.len() {
        let from = rooms[i].rect.center_even();
        let nearest = rooms[..i]
            .iter()
            .min_by_key(|r| {
                let c = r.rect.center_even();
                (c.0 - from.0).abs() + (c.1 - from.1).abs()
            })
            .expect("rooms[..i] no vacío para i >= 1");
        push_l_corridor(&mut corridors, rng, from, nearest.rect.center_even());
    }
    // Ciclos: una arista extra por cada 4 salas, entre pares al azar distintos.
    let extra = rooms.len() / 4;
    for _ in 0..extra {
        let a = rng.gen_range(0..rooms.len());
        let b = rng.gen_range(0..rooms.len());
        if a == b {
            continue;
        }
        push_l_corridor(
            &mut corridors,
            rng,
            rooms[a].rect.center_even(),
            rooms[b].rect.center_even(),
        );
    }
    corridors
}

/// Pasillo en L entre dos anclas pares: tramo horizontal + tramo vertical, grosor
/// `CORRIDOR_THICKNESS`, con el codo elegido por sorteo. Los rects cubren ambas anclas
/// y el codo (rangos inclusivos + grosor).
fn push_l_corridor(out: &mut Vec<CellRect>, rng: &mut StdRng, a: (i32, i32), b: (i32, i32)) {
    let pivot = if rng.gen::<bool>() {
        (b.0, a.1)
    } else {
        (a.0, b.1)
    };
    push_axis_segment(out, a, pivot);
    push_axis_segment(out, pivot, b);
}

fn push_axis_segment(out: &mut Vec<CellRect>, a: (i32, i32), b: (i32, i32)) {
    debug_assert!(a.0 == b.0 || a.1 == b.1, "segmento no alineado a eje");
    let min = (a.0.min(b.0), a.1.min(b.1));
    let max = (a.0.max(b.0), a.1.max(b.1));
    let rect = CellRect {
        min,
        size: (
            (max.0 - min.0) + CORRIDOR_THICKNESS,
            (max.1 - min.1) + CORRIDOR_THICKNESS,
        ),
    };
    if rect.size.0 > 0 && rect.size.1 > 0 {
        out.push(rect);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::{HashSet, VecDeque};

    /// Rasteriza el layout a celdas transitables (interior de salas + pasillos).
    fn walkable(layout: &Level4Layout) -> HashSet<(i32, i32)> {
        let mut cells = HashSet::new();
        let rects = layout
            .rooms
            .iter()
            .map(|r| r.rect)
            .chain(layout.corridors.iter().copied());
        for rect in rects {
            let (mx, mz) = rect.max_exclusive();
            for x in rect.min.0..mx {
                for z in rect.min.1..mz {
                    cells.insert((x, z));
                }
            }
        }
        cells
    }

    fn reachable_from(cells: &HashSet<(i32, i32)>, start: (i32, i32)) -> HashSet<(i32, i32)> {
        let mut seen = HashSet::new();
        let mut queue = VecDeque::new();
        if cells.contains(&start) {
            seen.insert(start);
            queue.push_back(start);
        }
        while let Some((x, z)) = queue.pop_front() {
            for next in [(x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1)] {
                if cells.contains(&next) && seen.insert(next) {
                    queue.push_back(next);
                }
            }
        }
        seen
    }

    #[test]
    fn same_input_same_layout() {
        for epoch in [0u32, 1, 7] {
            assert_eq!(generate(42, epoch), generate(42, epoch));
        }
        assert_eq!(generate(0, 0), generate(0, 0));
    }

    #[test]
    fn different_epoch_different_layout() {
        // No es un requisito duro celda a celda, pero dos epochs consecutivos idénticos
        // en TODO el layout delatarían que el epoch no entra en la semilla.
        assert_ne!(generate(42, 0), generate(42, 1));
    }

    #[test]
    fn full_connectivity_100_draws() {
        // Verificación (b) de ADR-093: cero salas incomunicadas en 100 sorteos.
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw), draw % 5);
            let cells = walkable(&layout);
            let start = layout.rooms[0].rect.center_even();
            let seen = reachable_from(&cells, start);
            for (i, room) in layout.rooms.iter().enumerate() {
                assert!(
                    seen.contains(&room.rect.center_even()),
                    "sorteo {draw}: sala {i} incomunicada"
                );
            }
        }
    }

    #[test]
    fn rooms_inside_region_and_even_parity() {
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw).wrapping_mul(7919), 0);
            for room in &layout.rooms {
                let (mx, mz) = room.rect.max_exclusive();
                assert!(room.rect.min.0 >= 0 && room.rect.min.1 >= 0);
                assert!(mx <= REGION_CELLS && mz <= REGION_CELLS);
                assert_eq!(room.rect.min.0 % 2, 0, "origen x impar");
                assert_eq!(room.rect.min.1 % 2, 0, "origen z impar");
                assert_eq!(room.rect.size.0 % 2, 0, "ancho impar");
                assert_eq!(room.rect.size.1 % 2, 0, "alto impar");
            }
        }
    }

    #[test]
    fn rooms_keep_separation() {
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw).wrapping_mul(104_729), 3);
            for (i, a) in layout.rooms.iter().enumerate() {
                for b in layout.rooms.iter().skip(i + 1) {
                    let padded = a.rect.inflated(ROOM_SEPARATION);
                    assert!(
                        !padded.overlaps(&b.rect),
                        "sorteo {draw}: salas a menos de {ROOM_SEPARATION} celdas"
                    );
                }
            }
        }
    }

    #[test]
    fn exactly_one_return_room_and_enough_rooms() {
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw) ^ 0xDEAD_BEEF, 0);
            let returns = layout.rooms.iter().filter(|r| r.is_return_room).count();
            assert_eq!(returns, 1, "sorteo {draw}: {returns} salas de retorno");
            assert!(layout.rooms[0].is_return_room, "la sala 0 es la de retorno");
            assert!(
                layout.rooms.len() >= ROOM_MIN_COUNT,
                "sorteo {draw}: solo {} salas",
                layout.rooms.len()
            );
        }
    }

    #[test]
    fn corridors_stay_inside_region() {
        // Los pasillos van entre centros de sala: no pueden salirse de la región.
        for draw in 0..100u32 {
            let layout = generate(u64::from(draw).wrapping_add(31_337), 1);
            for c in &layout.corridors {
                let (mx, mz) = c.max_exclusive();
                assert!(c.min.0 >= 0 && c.min.1 >= 0, "sorteo {draw}");
                assert!(mx <= REGION_CELLS && mz <= REGION_CELLS, "sorteo {draw}");
            }
        }
    }
}
