//! ADR-096 — el contrato de junta: cómo dos regiones vecinas abren puertas la una a la otra **sin
//! hablarse**.
//!
//! # El problema
//!
//! Cada región se compone sola y acotada a su caja, así que nacen selladas: entre dos hay dos muros
//! espalda contra espalda y el mundo es un tablero de bloques. Abrirlas exige que las dos coincidan
//! en DÓNDE está la puerta, y no pueden consultarse — una puede componerse hoy y la otra dentro de
//! media hora, en otra máquina.
//!
//! # La solución: la puerta es función pura de la JUNTA, no de la región
//!
//! El borde entre dos regiones se identifica igual desde los dos lados (ver [`BorderId`]), y de ese
//! identificador salen por hash las puertas. Las dos regiones calculan la misma lista sin saber nada
//! la una de la otra. Es A1 aplicado al borde de región en vez de al de chunk, que es exactamente lo
//! que decidió ADR-096.
//!
//! # La trampa, y por qué el cumplimiento tiene que estar GARANTIZADO
//!
//! Una puerta solo sirve si las dos regiones ponen geometría en ella. Y ahí hay un fallo que no
//! perdona: si A pone su tramo y B no, **A abre un vano al vacío** y el jugador se cae por él.
//!
//! No vale dejar la boca abierta y que la selle la pasada final: entonces las dos regiones sellarían
//! su lado y la puerta quedaría tapiada por ambas caras — construida y cerrada, lo peor de los dos
//! mundos.
//!
//! Así que el cumplimiento no puede depender de si hay sitio: tiene que ser **imposible que falle**.
//! De ahí las tres restricciones de abajo, que juntas lo garantizan sin necesidad de comprobar nada:
//!
//! 1. **El tramo de puerta es SIEMPRE la misma pieza**, y estrecha. No se sortea entre el catálogo:
//!    una pieza grande podría no caber o pisar a la de al lado, y "no cabe" es justo el caso que no
//!    puede existir.
//! 2. **Los tramos se colocan LOS PRIMEROS**, antes que la semilla del centro. Así nada puede estar
//!    ya en su sitio; el resto del recorrido los esquiva con la comprobación de solape de siempre.
//! 3. **Las puertas guardan distancia entre sí y a las esquinas** ([`GATE_MARGIN_M`]), lo bastante
//!    para que dos tramos —ni de la misma junta ni de dos juntas que se encuentran en una esquina—
//!    puedan tocarse.
//!
//! Con eso, la boca del tramo que da al exterior se marca CONECTADA de salida: no es una suposición
//! optimista, es que la vecina va a poner la suya sí o sí.

use super::compose::Wg3Anchor;
use super::hash;
use super::manifest::Wg3Manifest;
use super::placement::local_point;

/// Distancia mínima de una puerta a los extremos de su borde, y entre dos puertas de la misma junta.
///
/// Tiene que ser mayor que el fondo del tramo de puerta ([`GATE_STUB_MAX_DEPTH_M`]) para que dos
/// tramos de bordes distintos que se encuentran en una esquina no se pisen: cada uno entra
/// perpendicular a SU borde, así que a menos de su propio fondo de la esquina se cruzarían.
pub const GATE_MARGIN_M: f32 = 18.0;

/// Fondo máximo que puede tener el tramo de puerta. Es el que hace de cota para [`GATE_MARGIN_M`];
/// si se elige un tramo más largo hay que subir el margen, y hay un test que lo vigila.
pub const GATE_STUB_MAX_DEPTH_M: f32 = 16.0;

/// El margen tiene que despejar el fondo del tramo, o dos tramos de bordes que se encuentran en una
/// esquina se pisarían. Se comprueba EN COMPILACIÓN: es una relación entre dos constantes, y si
/// alguien alarga el tramo sin mirar el margen, lo correcto es que no compile — no que se entere un
/// test dentro de media hora.
const _: () = assert!(GATE_MARGIN_M > GATE_STUB_MAX_DEPTH_M);

/// Sal del sorteo de puertas.
const SALT_GATES: i32 = 0x4A_55_4E_54u32 as i32;

/// Identificador canónico de un borde entre dos regiones.
///
/// **Canónico quiere decir que los dos lados lo calculan igual**, y es toda la gracia: el borde
/// derecho de la región `(3,5)` y el izquierdo de la `(4,5)` son el MISMO borde, y tienen que dar el
/// mismo identificador o cada una sortearía sus puertas por su cuenta. Se resuelve nombrando el
/// borde por la región de coordenada MENOR y el eje que cruza.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct BorderId {
    /// Región de coordenada menor de las dos que comparten el borde.
    pub x: i32,
    pub z: i32,
    /// `0` = el borde corre en Z (separa vecinas en X); `1` = corre en X.
    pub axis: u8,
}

/// Una puerta acordada: un punto del borde y hacia dónde da, visto desde una región concreta.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Wg3Gate {
    /// Punto exacto de la junta, en metros de mundo.
    pub x: f32,
    pub z: f32,
    /// Lado de la región que mira AFUERA por esta puerta: `0 = N (+Z)`, `1 = E (+X)`, `2 = S (−Z)`,
    /// `3 = O (−X)`. Las dos regiones ven la misma puerta con lados opuestos.
    pub outward_side: u8,
}

impl BorderId {
    /// El borde que le toca a una región por uno de sus lados.
    pub fn of_region_side(region_x: i32, region_z: i32, side: u8) -> Self {
        match side % 4 {
            // N: el borde superior de esta región es el inferior de la de arriba. La menor es ésta.
            0 => Self {
                x: region_x,
                z: region_z,
                axis: 1,
            },
            // E: la menor es ésta.
            1 => Self {
                x: region_x,
                z: region_z,
                axis: 0,
            },
            // S: la menor es la de abajo.
            2 => Self {
                x: region_x,
                z: region_z - 1,
                axis: 1,
            },
            // O: la menor es la de la izquierda.
            _ => Self {
                x: region_x - 1,
                z: region_z,
                axis: 0,
            },
        }
    }
}

/// Cuántas puertas y dónde, para un borde. Función pura de la junta y de la semilla del mundo.
///
/// Devuelve las posiciones a lo largo del borde en metros desde su extremo menor, **cuantizadas al
/// centímetro**: las dos regiones tienen que llegar al MISMO número, y dos cadenas de operaciones en
/// coma flotante no lo garantizan aunque la fórmula sea idéntica.
pub fn gate_offsets(world_seed: i32, border: BorderId, border_length_m: f32) -> Vec<f32> {
    let usable = border_length_m - GATE_MARGIN_M * 2.0;
    if usable <= 0.0 {
        return Vec::new();
    }

    // Sembrado por la JUNTA en enteros, no por una posición en metros: dos regiones tienen que
    // llegar al mismo flujo, y el identificador de borde ya es exacto por construcción.
    let mut stream = hash::Stream::new(hash::mix(
        world_seed,
        border.x,
        border.z,
        border.axis as i32 ^ SALT_GATES,
    ));

    // Una o dos puertas por junta. Cero dejaría regiones aisladas —el defecto que esto viene a
    // quitar— y tres en 150 m dejarían el borde hecho un colador.
    let count = if stream.next01() < 0.35 { 2 } else { 1 };

    let mut offsets: Vec<f32> = Vec::with_capacity(count);
    for _ in 0..count {
        let raw = GATE_MARGIN_M + stream.next01() * usable;
        let snapped = (raw * 100.0).round() / 100.0;

        // Separación entre puertas de la misma junta: si la sorteada cae demasiado cerca de otra, se
        // descarta en vez de moverse. Moverla haría que el resultado dependiera del orden en que se
        // sortearon, y ese orden es justo lo que no queremos que importe.
        if offsets
            .iter()
            .any(|o: &f32| (o - snapped).abs() < GATE_MARGIN_M)
        {
            continue;
        }
        offsets.push(snapped);
    }
    offsets
}

/// Las puertas de una región: hasta dos por cada uno de sus cuatro bordes.
///
/// `bounds` es la caja de la región en metros.
pub fn gates_of_region(
    world_seed: i32,
    region_x: i32,
    region_z: i32,
    bounds: (f32, f32, f32, f32),
) -> Vec<Wg3Gate> {
    let (min_x, min_z, max_x, max_z) = bounds;
    let side_x = max_x - min_x;
    let side_z = max_z - min_z;
    let mut gates = Vec::new();

    for side in 0..4u8 {
        let border = BorderId::of_region_side(region_x, region_z, side);
        let length = if side % 2 == 0 { side_x } else { side_z };

        for offset in gate_offsets(world_seed, border, length) {
            // El offset corre SIEMPRE desde el extremo menor del borde, no desde el que le tocaría a
            // esta región según su recorrido perimetral: es lo que hace que las dos lo lean igual.
            let (x, z) = match side % 4 {
                0 => (min_x + offset, max_z),
                1 => (max_x, min_z + offset),
                2 => (min_x + offset, min_z),
                _ => (min_x, min_z + offset),
            };
            gates.push(Wg3Gate {
                x,
                z,
                outward_side: side,
            });
        }
    }
    gates
}

/// El ancla del tramo de puerta de una `gate`, ya girado y situado.
pub fn stub_anchor(manifest: &Wg3Manifest, stub: u16, gate: Wg3Gate) -> Option<Wg3Anchor> {
    let piece = manifest.piece(stub)?;

    // La boca que dará al exterior: la primera de pasillo. La otra queda abierta hacia dentro y es
    // por donde el recorrido entra en la región.
    let socket_index = piece.sockets.iter().position(|s| s.kind == 0)?;
    let socket = &piece.sockets[socket_index];

    // Giro tal que esa boca acabe mirando AFUERA. Girar solo suma al lado (contrato de socket), así
    // que la rotación queda determinada y no hay nada que buscar.
    let rotation = ((gate.outward_side as i32 - socket.side as i32).rem_euclid(4)) as u8;
    let (w, d) = if rotation.is_multiple_of(2) {
        (piece.size_x, piece.size_z)
    } else {
        (piece.size_z, piece.size_x)
    };

    let (lx, lz) = local_point(gate.outward_side, socket.offset, w, d);
    Some(Wg3Anchor {
        piece: stub,
        rotation,
        origin_x: gate.x - lx,
        origin_z: gate.z - lz,
        connected_socket: socket_index,
    })
}

/// El índice de la pieza que hace de tramo de puerta: la primera del catálogo con AL MENOS dos bocas
/// de pasillo y un fondo que quepa en [`GATE_STUB_MAX_DEPTH_M`].
///
/// «La primera» y no una sorteada, a propósito: el tramo tiene que caber SIEMPRE, y sortear entre el
/// catálogo mete piezas grandes que podrían no caber o pisarse entre ellas. La variedad del mundo la
/// pone lo que crece detrás del tramo, no el tramo.
pub fn gate_stub_piece(manifest: &Wg3Manifest) -> Option<u16> {
    manifest
        .pieces
        .iter()
        .find(|p| {
            let corridor_sockets = p.sockets.iter().filter(|s| s.kind == 0).count();
            corridor_sockets >= 2
                && p.size_x.max(p.size_z) <= GATE_STUB_MAX_DEPTH_M
                && p.size_x.min(p.size_z) <= 4.0
        })
        .map(|p| p.index)
}
