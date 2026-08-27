//! ANDAMIO DE F2. **Este fichero se borra entero en F4.**
//!
//! F2 tiene que demostrar el camino completo —manifiesto, colocación, ráster, wire, cliente— y para
//! eso hace falta que haya ALGO puesto en el mundo. El compositor de verdad (emparejado por sockets,
//! campo de escala, tapones) vive hoy en C# y se porta en F4, con la decisión de troceado ya
//! cerrada.
//!
//! Mientras tanto, esto: **una pieza por chunk, sorteada por hash de la coordenada y encajada
//! entera dentro de su chunk.** No empareja bocas, no garantiza que dos piezas vecinas conecten y
//! no pretende ser liminal. Es un mundo de piezas sueltas que sirve para caminar dentro de una y
//! chocar con sus columnas, que es exactamente lo que pide la verificación (e) de ADR-095.
//!
//! Se deja aparte y con este nombre a propósito: un andamio que se disfraza de sistema es un
//! andamio que nadie quita.

use super::chunk::{Wg3ChunkCoord, WG3_CHUNK_M};
use super::manifest::Wg3Manifest;
use super::placement::Wg3Placement;
use super::raster::CM_PER_M;

/// Sal del sorteo. Propia y no compartida con nada: cuando esto se borre, no debe llevarse por
/// delante el hash de ningún otro sistema.
const DEMO_SALT: u64 = 0x57_41_4C_4B_57_47_33_00;

/// Mezcla determinista de la coordenada. splitmix64, el mismo avance que el resto del proyecto.
fn mix(seed: u64, x: i32, z: i32) -> u64 {
    let mut h = seed ^ DEMO_SALT;
    h = h.wrapping_add((x as i64 as u64).wrapping_mul(0xFF51_AFD7_ED55_8CCD));
    h ^= h >> 33;
    h = h.wrapping_add((z as i64 as u64).wrapping_mul(0xC4CE_B9FE_1A85_EC53));
    h ^= h >> 29;
    h = h.wrapping_mul(0x9E37_79B1_85EB_CA87);
    h ^ (h >> 32)
}

/// Las piezas que el andamio pone en un chunk. Hoy: cero o una.
///
/// Devuelve vacío cuando la pieza sorteada no cabe entera en el chunk, y eso es un resultado válido
/// —un chunk sin nada—, no un fallo: el cliente ya tiene que saber distinguir "vacío" de "aún no ha
/// llegado", y un mundo con huecos lo ejercita desde el primer día en vez de dejarlo para F4.
pub fn placements_for_chunk(
    manifest: &Wg3Manifest,
    world_seed: u64,
    coord: Wg3ChunkCoord,
) -> Vec<Wg3Placement> {
    if manifest.pieces.is_empty() {
        return Vec::new();
    }

    let h = mix(world_seed, coord.x, coord.z);

    // Un chunk de cada tres se queda vacío: sin huecos, el mundo de prueba es una cuadrícula
    // perfecta de piezas y no se distingue una frontera de chunk de una junta de pieza.
    if h.is_multiple_of(3) {
        return Vec::new();
    }

    let index = ((h >> 8) % manifest.pieces.len() as u64) as u16;
    let rotation = ((h >> 24) % 4) as u8;
    let piece = match manifest.piece(index) {
        Some(p) => p,
        None => return Vec::new(),
    };

    let (w, d) = if rotation.is_multiple_of(2) {
        (piece.size_x, piece.size_z)
    } else {
        (piece.size_z, piece.size_x)
    };
    if w > WG3_CHUNK_M || d > WG3_CHUNK_M {
        return Vec::new();
    }

    // Hueco libre en centímetros, para que el origen salga ENTERO. Sortear en metros y convertir
    // después metería un redondeo distinto en cada máquina justo en el dato que tiene que coincidir
    // bit a bit entre procesos.
    let free_x_cm = ((WG3_CHUNK_M - w) * CM_PER_M) as i64;
    let free_z_cm = ((WG3_CHUNK_M - d) * CM_PER_M) as i64;
    let (base_x, base_z) = coord.origin_cm();

    let off_x = if free_x_cm > 0 {
        ((h >> 32) % free_x_cm as u64) as i32
    } else {
        0
    };
    let off_z = if free_z_cm > 0 {
        ((h >> 44) % free_z_cm as u64) as i32
    } else {
        0
    };

    vec![Wg3Placement {
        piece: index,
        rotation,
        origin_x_cm: base_x + off_x,
        origin_z_cm: base_z + off_z,
    }]
}
