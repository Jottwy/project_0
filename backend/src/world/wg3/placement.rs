//! ADR-095 — una pieza colocada, y sus cajas ya en coordenadas de mundo.
//!
//! ESPEJO EXACTO de `Wg3Placement` y `Wg3Manifest.PlacedCollision` en C#. Es duplicación
//! consciente, no descuido: el cliente tiene que poder dibujar la pieza sin preguntar, y el
//! servidor tiene que poder colisionarla sin dibujarla. Lo que no puede pasar es que las dos
//! rotaciones se separen — de ahí que el contrato esté escrito aquí y que haya un test que compara
//! contra los valores que produce el lado de Unity.
//!
//! CONTRATO DE ORIGEN: `origin_x`/`origin_z` es la ESQUINA MÍNIMA de la huella YA GIRADA, no el
//! centro. Es lo contrario del contrato de `RoomPool.RoomEntry`, que pone el pivote en el centro.
//! Aquí manda la esquina porque con ella el giro y el solape son aritmética de rectángulos sin un
//! solo caso especial.

use super::manifest::Wg3Piece;

/// Dónde y cómo está puesta una pieza.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Wg3Placement {
    /// Índice en `Wg3Manifest::pieces`.
    pub piece: u16,
    /// Cuartos de vuelta, horario visto desde +Y. 0..=3.
    pub rotation: u8,
    /// Esquina mínima, en centímetros de mundo.
    ///
    /// ENTEROS Y NO FLOTANTES a propósito: una colocación es un dato que viaja y que se compara, y
    /// dos backends tienen que coincidir en ella bit a bit. Un `f32` acumulado a lo largo de una
    /// cadena de piezas no garantiza eso; un entero sí, y al centímetro sobra para un mundo cuyo
    /// grano de colisión es medio metro.
    pub origin_x_cm: i32,
    pub origin_z_cm: i32,

    /// Cota del SUELO de la pieza, en centímetros de mundo. ADR-097.
    ///
    /// Hasta F5 toda pieza estaba a cero y la verticalidad solo existía DENTRO de una. Es el mismo
    /// agujero que fundó WG3 —en WG2 la altura del suelo era función del índice de capa, así que no
    /// había dónde escribir una rampa— heredado en otra forma.
    ///
    /// En centímetros y entero por lo mismo que X y Z: viaja, se compara, y una cadena de sumas en
    /// `f32` no garantiza que dos backends coincidan bit a bit.
    pub origin_y_cm: i32,
}

impl Wg3Placement {
    pub fn origin_x(&self) -> f32 {
        self.origin_x_cm as f32 * 0.01
    }
    pub fn origin_z(&self) -> f32 {
        self.origin_z_cm as f32 * 0.01
    }
    pub fn origin_y(&self) -> f32 {
        self.origin_y_cm as f32 * 0.01
    }

    /// Huella ya girada. Un cuarto impar intercambia los ejes.
    pub fn footprint(&self, piece: &Wg3Piece) -> (f32, f32) {
        if self.rotation.is_multiple_of(2) {
            (piece.size_x, piece.size_z)
        } else {
            (piece.size_z, piece.size_x)
        }
    }

    /// Caja envolvente en XZ: `(min_x, min_z, max_x, max_z)`.
    pub fn bounds(&self, piece: &Wg3Piece) -> (f32, f32, f32, f32) {
        let (w, d) = self.footprint(piece);
        let (x, z) = (self.origin_x(), self.origin_z());
        (x, z, x + w, z + d)
    }

    /// Lado del socket `index` visto en coordenadas de mundo.
    pub fn world_side(&self, piece: &Wg3Piece, index: usize) -> u8 {
        (piece.sockets[index].side + self.rotation) % 4
    }

    /// Punto de mundo del socket `index`.
    ///
    /// El `offset` NO se toca al girar: el socket se parametriza recorriendo el perímetro en
    /// sentido horario desde la esquina `(0, D)`, y de esa parametrización sale que girar solo suma
    /// al lado. Es la propiedad que hace que el emparejado no tenga que buscar la rotación.
    pub fn world_socket_point(&self, piece: &Wg3Piece, index: usize) -> (f32, f32) {
        let (w, d) = self.footprint(piece);
        let side = self.world_side(piece, index);
        let (lx, lz) = local_point(side, piece.sockets[index].offset, w, d);
        (self.origin_x() + lx, self.origin_z() + lz)
    }
}

/// Una caja de colisión ya situada en el mundo.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlacedBox {
    pub center: [f32; 3],
    pub size: [f32; 3],
    pub yaw_degrees: f32,
    pub kind: u8,
}

/// Longitud del lado `side` en una pieza de `w` × `d`. N y S corren en X; E y O corren en Z.
pub fn side_length(side: u8, w: f32, d: f32) -> f32 {
    // N (0) y S (2) son los pares; E (1) y O (3), los impares.
    if (side % 4).is_multiple_of(2) {
        w
    } else {
        d
    }
}

/// Punto local de un socket dentro de una pieza de `w` × `d`.
///
/// Recorrido horario desde `(0, D)`:
/// ```text
///   (0,D) ────N───► (W,D)
///     ▲                │
///     W                E
///     │                ▼
///   (0,0) ◄───S──── (W,0)
/// ```
pub fn local_point(side: u8, offset: f32, w: f32, d: f32) -> (f32, f32) {
    match side % 4 {
        0 => (offset, d),
        1 => (w, d - offset),
        2 => (w - offset, 0.0),
        _ => (0.0, offset),
    }
}

/// Normal hacia AFUERA del lado, en XZ.
pub fn outward_normal(side: u8) -> (f32, f32) {
    match side % 4 {
        0 => (0.0, 1.0),
        1 => (1.0, 0.0),
        2 => (0.0, -1.0),
        _ => (-1.0, 0.0),
    }
}

/// Giro horario visto desde +Y, sobre la caja `[0,w] × [0,d]`. Espejo de `RotateLocal` en C#.
///
/// Se mantiene en el cuadrante positivo porque el contrato de origen es la esquina mínima: si el
/// giro sacara puntos a negativo, la huella dejaría de empezar en su origen y el solape necesitaría
/// un caso especial por rotación.
fn rotate_local(x: f32, z: f32, rotation: u8, w: f32, d: f32) -> (f32, f32) {
    match rotation % 4 {
        0 => (x, z),
        1 => (z, w - x),
        2 => (w - x, d - z),
        _ => (d - z, x),
    }
}

/// La chuleta de una pieza, girada y trasladada a su sitio.
///
/// EL GIRO VA SOLO AL YAW. Intercambiar además X y Z aplicaría la rotación dos veces: una caja de
/// 4 × 1 girada 90° sigue midiendo 4 × 1 en su propio eje, y es el yaw el que la pone atravesada en
/// el mundo. Ese fallo, si se cuela, no revienta nada: deja paredes de grosor equivocado, que es
/// justo la clase de error que no se ve hasta que alguien se queda encajado.
pub fn placed_collision(piece: &Wg3Piece, placement: &Wg3Placement) -> Vec<PlacedBox> {
    let (w, d) = (piece.size_x, piece.size_z);
    let (ox, oz) = (placement.origin_x(), placement.origin_z());
    let r = placement.rotation % 4;

    piece
        .collision
        .iter()
        .map(|b| {
            let (rx, rz) = rotate_local(b.cx, b.cz, r, w, d);
            PlacedBox {
                // ADR-097 — la Y de la colocación se suma AQUÍ, que es por donde la chuleta entra al
                // ráster. El ráster ya resolvía en altura (`Span { bottom_cm, top_cm }`), así que
                // una pieza elevada colisiona a su cota sin tocar una línea del rasterizado.
                center: [ox + rx, placement.origin_y() + b.cy, oz + rz],
                size: [b.sx, b.sy, b.sz],
                yaw_degrees: b.yaw + r as f32 * 90.0,
                kind: b.kind,
            }
        })
        .collect()
}
