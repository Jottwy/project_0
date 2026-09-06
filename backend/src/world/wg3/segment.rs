//! ADR-098 T1 — la CELDA generada: geometría que el servidor sintetiza cuando el catálogo no encaja.
//!
//! # Qué es
//!
//! Un rectángulo alineado a los ejes con sus bocas declaradas igual que las de una pieza —lado más
//! offset recorriendo el perímetro en horario desde `(0, D)`—, su cota de suelo y su altura libre.
//! De ahí sale la geometría por una regla que cabe en tres líneas: losa de suelo bajo la huella,
//! losa de techo sobre ella, y en cada lado la pared partida por las bocas de ese lado.
//!
//! **No hay geometría nueva: hay una pieza que nadie dibujó.** La regla es exactamente la que
//! `Wg3Geometry.Build` aplica en C# a una pieza sin volúmenes horneados, y el lado de Unity la
//! reutiliza literalmente construyendo un `Wg3Piece` sintético en vez de escribirla otra vez.
//!
//! # Por qué un solo tipo, y no "tramos y esquinas"
//!
//! Con bocas libres, la misma tramo cubre los cuatro casos que necesita un conector:
//!
//! - **tramo recto** — dos bocas en lados opuestos;
//! - **quiebro** — dos bocas en lados perpendiculares;
//! - **transición de ancho** — dos bocas opuestas de anchura distinta: la pared del lado estrecho se
//!   parte sola y quedan sus dos jambas, sin un caso especial;
//! - **escalón** — dos tramos contiguas con `floor_y_cm` distinto: la losa de la de arriba ES la
//!   contrahuella.
//!
//! # La partida doble, y quién la vigila
//!
//! Esta expansión está escrita dos veces —C# dibuja, Rust rasteriza— y dos implementaciones
//! internamente consistentes pueden diferir sin que nada reviente: el síntoma sería una pared que se
//! ve y no frena, o al revés. La ata `backend/tests/fixtures/wg3_connector_oracle.json`, exportado
//! desde Unity (que es el lado que fija el aspecto) y reproducido por un test de aquí. Solo se
//! comparan los volúmenes SÓLIDOS: la decoración no cruza la frontera de autoridad (R25), así que el
//! rodapié de un conector es asunto del cliente y no entra en el fixture.
//!
//! Y el digest del catálogo NO cubre esto —un tramo no está en el manifiesto—, dicho aquí para que
//! nadie lea un verde de más.

use super::placement::PlacedBox;
use super::raster::CM_PER_M;

/// Centimetros a metros, con la MISMA operacion que C# (`cm * 0.01f`) y no dividiendo entre 100.
///
/// **No es equivalente, y costo un test rojo.** `0.01` no es representable en binario, asi que
/// `240 * 0.01f` sale un pelo POR DEBAJO de 2,4 y `240 / 100.0` un pelo por encima. Con una pared de
/// 15 mm eso mueve su centro de 232,4999 a 232,5001 cm, y al redondear salen 232 y 233: un
/// centimetro de diferencia entre lo que dibuja el cliente y lo que bloquea el servidor. La
/// resolucion del raster lo taparia hoy, pero el oraculo compara al centimetro y hace bien.
#[inline]
pub fn metres(centimetres: i32) -> f32 {
    centimetres as f32 * 0.01
}

/// Grosor de losa de suelo y techo. Espejo de `Wg3Geometry.SlabThickness`.
pub const SLAB_THICKNESS_M: f32 = 0.12;

/// Grosor de pared, hacia DENTRO de la huella. Espejo del valor por defecto de
/// `Wg3Piece.wallThickness`, que es el que usa el catálogo de código.
pub const WALL_THICKNESS_M: f32 = 0.15;

/// Discriminantes de `Wg3VolumeKind`. Duplicados y no importados: el ráster no los mira —todo lo que
/// llega bloquea— pero hacen legible un volcado, y el fixture los compara.
pub const KIND_FLOOR: u8 = 0;
pub const KIND_CEILING: u8 = 1;
pub const KIND_WALL: u8 = 2;

/// **ANCHURA MÍNIMA DE UNA BOCA GENERADA, Y ES UN NÚMERO MEDIDO, NO ELEGIDO.**
///
/// El ráster es CONSERVADOR: toda celda que una caja toque queda maciza (`raster.rs`), así que cada
/// pared de 15 cm se infla hasta ocupar su celda de 50 cm entera y **come vano por los dos lados**.
/// `narrowest_doorway_clearance` lo mide barriendo la alineación sub-celda, que es la que manda
/// porque el mundo se coloca en centímetros arbitrarios:
///
/// | boca | hueco libre en el peor caso |
/// |---|---|
/// | 120 cm | **0,00 m** — tapiada |
/// | 200 cm | 0,99 m |
/// | 240 cm | 1,49 m |
/// | 500 cm | 3,99 m |
///
/// El jugador mide 0,70 m de diámetro, así que 200 es el primer escalón de 50 cm que pasa. Por
/// debajo, el cliente dibuja un pasillo abierto y el servidor no deja entrar — el peor fallo
/// posible, porque no se ve en una captura.
///
/// El catálogo autorado se libró por accidente: sus bocas son de 2,4 y 5,0 m. Lo que ADR-098 empezó
/// a GENERAR bajaba de ahí.
pub const MIN_GENERATED_WIDTH_CM: i32 = 200;

/// Lado máximo de un tramo, en metros.
///
/// **Es lo que deja intacto el reparto por chunk.** «Una pieza, un chunk» se sostiene sobre que
/// ninguna pieza llega a los 50 m del chunk, así que centrada nunca asoma más allá de los vecinos
/// inmediatos de su dueño. Una ruta larga se parte en más tramos —que es gratis— en vez de obligar a
/// recortar geometría en la frontera.
pub const MAX_SEGMENT_M: f32 = 25.0;

/// Una boca de el tramo. Misma parametrización que `Wg3Socket`: el lado más el offset recorriendo el
/// perímetro en horario desde `(0, D)`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Wg3Opening {
    /// `0 = N (+Z)`, `1 = E (+X)`, `2 = S (−Z)`, `3 = O (−X)`.
    pub side: u8,
    /// Metros a lo largo del lado, hasta el CENTRO de la boca. En centímetros enteros.
    pub offset_cm: i32,
    pub width_cm: i32,
}

/// ADR-099 D3 — un vano excavado: materia que se QUITA después de estampar.
///
/// Existe porque una pieza colocada es inmutable y su pared viene horneada del catálogo, así que
/// cuando un tramo absorbido muere contra ella no hay forma de abrirle paso poniendo geometría —
/// sólo quitándola. Es la operación que el sistema de salas tenía (`carve_authored_into_layout`) y
/// WG3 no.
///
/// EN CENTÍMETROS ENTEROS por lo mismo que todo lo demás que cruza procesos.
///
/// **Se excava en los DOS lados o en ninguno**: medio vano es un muro con una marca. Por eso la caja
/// cubre el grosor entero del contacto y no se para en la cara.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Wg3Carve {
    pub x_cm: i32,
    pub z_cm: i32,
    pub size_x_cm: i32,
    pub size_z_cm: i32,
    /// Banda vertical que se abre. NO llega al suelo: ver `carve_box`.
    pub bottom_y_cm: i32,
    pub top_y_cm: i32,
}

/// ADR-105 — **un MACIZO: materia que se AÑADE, y que no pertenece a la cáscara de ninguna sala.**
///
/// Es el espejo exacto de [`Wg3Carve`] más un `style`, y la simetría es la decisión: los dos lados ya
/// saben leer esta caja, ya la comparan en centímetros enteros y ya la rasterizan. Uno resta, el otro
/// suma. Quien entienda uno entiende el otro, y el fallo de un lado se busca donde el del otro.
///
/// # Por qué hacía falta un canal entero para esto
///
/// WG3 sabía quitar materia y no sabía ponerla suelta. Un [`Wg3Segment`] es un rectángulo con suelo,
/// techo y cuatro paredes, y **no puede ser macizo**: [`Wg3Segment::problems`] rechaza un tramo sin
/// bocas con todas las letras. Un tramo diminuto haciendo de pilar traería su losa de suelo y la de
/// techo, coplanares con las del atrio — el z-fighting que ADR-102 pagó con 456 pares. Y una pieza de
/// catálogo sirve para un pilar concreto y no para un pretil, que mide lo que mida cada borde.
///
/// # No se le puede excavar (ADR-105 D2)
///
/// **Un macizo es inmune a los [`Wg3Carve`].** No es un detalle: el vano de atrio de ADR-104 cubre la
/// huella del atrio ensanchada medio metro, que es exactamente donde va un pretil. Restando después de
/// sumar, cada pretil emitido desaparecería y el síntoma sería «el pretil no sale», sin un solo error
/// en ninguna parte.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Wg3Solid {
    pub x_cm: i32,
    pub z_cm: i32,
    pub size_x_cm: i32,
    pub size_z_cm: i32,
    pub bottom_y_cm: i32,
    pub top_y_cm: i32,
    /// Aspecto, como en [`Wg3Segment`]. El servidor no lo interpreta: sin él un pretil se lee como un
    /// objeto pegado en vez de como arquitectura de la sala a la que pertenece.
    pub style: u8,
    /// ADR-121 D1 — giro alrededor del CENTRO de la huella, en grados enteros, positivo horario
    /// visto desde arriba (la convención de Unity y de `raster::add_box`). Cero es el macizo de
    /// siempre. `x_cm/z_cm/size_*` siguen describiendo la caja SIN girar.
    pub yaw_deg: i16,
    /// ADR-125 — la forma dentro de la huella: [`SHAPE_BOX`], [`SHAPE_CYLINDER`],
    /// [`SHAPE_HALF_CYLINDER`] u [`SHAPE_OCTAGON`]. La huella sigue siendo la caja: la forma se
    /// inscribe en ella, y por eso `centre()` y el reparto por chunk no cambian.
    pub shape: u8,
}

/// ADR-125 — la caja de siempre.
pub const SHAPE_BOX: u8 = 0;
/// ADR-125 — cilindro inscrito en la huella. **Sólo círculo**: `size_x_cm == size_z_cm`, para que el
/// giro no signifique nada y el ráster lo estampe como un disco exacto.
pub const SHAPE_CYLINDER: u8 = 1;
/// ADR-125 — MEDIA LUNA: medio disco con la cara plana en el lado de z mínima de la caja sin girar
/// y la panza hacia +z. `size_z_cm == size_x_cm / 2`. Con `yaw_deg` múltiplo de 90 se adosa a
/// cualquier pared.
pub const SHAPE_HALF_CYLINDER: u8 = 2;
/// ADR-125 — prisma octogonal REGULAR inscrito en la huella (cuadrada), con caras planas sobre los
/// ejes. Sustituye al «octógono de dos macizos» de ADR-121 D5, que era una estrella de ocho puntas.
pub const SHAPE_OCTAGON: u8 = 3;
/// ADR-125 enm. 1 — ARCO DE PUERTA: la banda sobre una boca, con el intradós curvo. El eje LARGO de
/// la huella es la cuerda (el ancho de la boca), el corto el grosor de pared. Media elipse desde
/// `bottom_y_cm` en los dos extremos hasta `top_y_cm - ARCH_KEY_CM` en la clave; por encima de la
/// curva, macizo. Sin giro: la orientación la da la propia caja.
pub const SHAPE_ARCH: u8 = 4;
/// ADR-125 enm. 1 — grosor de la clave del arco: lo que queda de macizo sobre el intradós en el
/// centro. El cliente lo refleja (`Wg3MeshBuilder.ArchKeyM`).
pub const ARCH_KEY_CM: i32 = 10;

/// ADR-129 D2 — bit 0x40 de `style`: el macizo es INVISIBLE. El ráster lo estampa y el cliente le
/// pone collider, pero nadie lo dibuja: la malla la pone el prefab del mueble que va encima
/// (`Wg3Prop`). Es lo que hace que una mesa frene igual en los dos lados sin mandar mallas por el
/// cable ni dibujar una caja debajo del mueble.
pub const STYLE_HIDDEN_BIT: u8 = 0x40;

/// ADR-129 D1 — un ANCLA de atrezo: dónde va un mueble y cuál, no cómo es. El cliente resuelve
/// `kind` → prefab; el servidor sólo decide posición y giro, y para lo que frena emite además un
/// macizo invisible ([`STYLE_HIDDEN_BIT`]) con su huella. `x_cm`/`z_cm` es el pivote en el suelo
/// (centro de la huella), `y_cm` la cota del suelo (o de la mesa, para lo que va encima).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Wg3Prop {
    pub x_cm: i32,
    pub z_cm: i32,
    pub y_cm: i32,
    /// Giro horario visto desde arriba, 0 = mira a +z (la convención de Unity y de `yaw_deg`).
    pub yaw_deg: i16,
    pub kind: u8,
    pub style: u8,
}

pub const PROP_DESK: u8 = 1;
pub const PROP_CHAIR: u8 = 2;
pub const PROP_CABINET: u8 = 3;
pub const PROP_SHELF: u8 = 4;
pub const PROP_WHITEBOARD: u8 = 5;
pub const PROP_TRASH: u8 = 6;
pub const PROP_BOX: u8 = 7;
pub const PROP_PAPER: u8 = 8;
pub const PROP_MONITOR: u8 = 9;
/// ADR-129 iteración 1 (2026-09-06) — el desorden de la foto: reloj de pared, teléfono, teclado y
/// bandeja sobre la mesa, y la silla CAÍDA (el cliente la tumba de lado).
pub const PROP_CLOCK: u8 = 10;
pub const PROP_PHONE: u8 = 11;
pub const PROP_KEYBOARD: u8 = 12;
pub const PROP_TRAY: u8 = 13;
pub const PROP_CHAIR_FALLEN: u8 = 14;
/// ADR-105 enm. 19 (2026-09-06) — el deterioro del falso techo: una placa que cuelga de un lado y
/// una luminaria descolgada en diagonal. **No tienen prefab**: el cliente los construye a mano
/// (caja fina inclinada con el material de placa o de luminaria) porque `Wg3Solid` sólo gira en Y y
/// una placa colgando pide inclinación. Kinds nuevos, no wire nuevo: `kind` ya era un byte.
pub const PROP_CEILING_TILE_HUNG: u8 = 21;
pub const PROP_LIGHT_HUNG: u8 = 22;

/// ADR-129 enm. 1 (2026-09-06) — **un CARTEL**: la única superficie del mundo con TEXTO. Placa de
/// despacho junto a una puerta, rótulo en una mampara de cubículo, señal de salida en un pasillo,
/// tablón de corcho en una sala grande, calendario parado. No es un prefab: el cliente monta un
/// quad con la celda que le toque de un atlas de decals, y por eso el ancla no lleva tamaño (lo da
/// la variante, con la tabla de [`sign_size_cm`] a los dos lados).
///
/// **No sube el wire**: el `kind` es un `u8` con catorce valores gastados, y el `variant` viaja en
/// el `style` que ya existe y que el cliente no leía para el atrezo.
pub const PROP_SIGN: u8 = 15;

/// ADR-129 enm. 1 — lo que un cartel se despega de la superficie a la que va pegado, en cm. Un
/// centímetro: pegado a ras es z-fighting garantizado, y a más de eso se lee como una bandeja.
/// El servidor ya emite el ancla DESPLAZADA, así que el cliente instancia donde le dicen.
pub const SIGN_PROUD_CM: i32 = 1;

/// ADR-129 enm. 1 — la variante de un cartel, en los seis bits bajos de `style`: 0–31 el texto
/// bueno, 32–63 el mismo cartel con el texto ESTROPEADO (letras cambiadas, fechas imposibles), que
/// es lo que se sirve al bajar (ADR-130 D4). Sumar esto a una variante buena da su gemela rota, y
/// por eso el atlas es de 8 × 8 celdas: fila `v / 8`, columna `v % 8`.
pub const SIGN_GARBLED_BASE: u8 = 32;
/// Primera variante de cada familia y cuántas hay. El cliente refleja exactamente esta tabla.
pub const SIGN_DOOR_PLATE: u8 = 0;
pub const SIGN_DOOR_PLATE_N: u8 = 8;
pub const SIGN_CUBICLE_TAG: u8 = 8;
pub const SIGN_CUBICLE_TAG_N: u8 = 8;
pub const SIGN_EXIT: u8 = 16;
pub const SIGN_EXIT_N: u8 = 4;
pub const SIGN_CORKBOARD: u8 = 20;
pub const SIGN_CORKBOARD_N: u8 = 4;
pub const SIGN_CALENDAR: u8 = 24;
pub const SIGN_CALENDAR_N: u8 = 4;

/// ADR-129 enm. 1 — el tamaño del quad de una variante, `(ancho, alto)` en cm. **Espejo exacto de
/// `Wg3SignCatalog.SizeCm`**: el servidor lo necesita para no colgar un tablón de corcho encima de
/// una ventana, y el cliente para dibujarlo del tamaño con el que se midió el hueco.
pub fn sign_size_cm(variant: u8) -> (i32, i32) {
    match variant % SIGN_GARBLED_BASE {
        v if v < SIGN_CUBICLE_TAG => (30, 12),
        v if v < SIGN_EXIT => (24, 9),
        v if v < SIGN_CORKBOARD => (40, 15),
        v if v < SIGN_CALENDAR => (120, 90),
        v if v < SIGN_CALENDAR + SIGN_CALENDAR_N => (30, 42),
        // 28–31 están libres en el atlas: si alguien las emite, que se vean del tamaño de una placa
        // y no de cero por cero.
        _ => (30, 12),
    }
}
/// ADR-129 enm. 1 (2026-09-06) — **las variantes de sala**: los cinco tipos que no tenían sustituto
/// entre los catorce de arriba. `kind` es un byte y no entra en la forma del mensaje, así que
/// añadir valores NO sube el wire: un cliente viejo no resuelve el prefab, avisa una vez y se salta
/// el mueble (`Wg3PropCatalog.NameOf` → `null`). Las huellas, medidas sobre el prefab horneado y en
/// centímetros: mesa larga 530 × 160 × 75, mostrador 485 × 70 × 105, microondas 55 × 49 × 34,
/// nevera 80 × 71 × 181, rack 115 × 91 × 260. El eje LARGO del prefab es su X local, o sea el que
/// corre en `x` con `yaw_deg` 0.
pub const PROP_TABLE_LONG: u8 = 16;
pub const PROP_COUNTER: u8 = 17;
pub const PROP_MICROWAVE: u8 = 18;
pub const PROP_FRIDGE: u8 = 19;
pub const PROP_RACK: u8 = 20;

/// ADR-125 enm. 2 — bit alto de `style`: el macizo es DECORACIÓN. Se dibuja y nada más: ni el
/// ráster lo estampa ni el cliente le cuelga collider. Existe porque un marco de puerta que
/// sobresale 2 cm de la pared cerraría media celda del ráster (50 cm) a cada lado de la boca.
pub const STYLE_DECOR_BIT: u8 = 0x80;
/// ADR-125 enm. 2 — ancho del marco (jambas y dintel), y del anillo de la arquivolta. Nueve y no
/// ocho ni diez: ocho es un barrote de rejilla y diez una cornisa, y los tests distinguen por forma.
pub const CASING_W_CM: i32 = 9;
/// ADR-125 enm. 2 — cuánto sobresale el marco de la cara de la pared, por cada sala. **Cuatro y no
/// dos desde la nota de enm. 2 (2026-09-04):** el cliente talla un perfil de dos escalones de 2 cm
/// en la banda (`Wg3MeshBuilder.CasingStepM`), y con 2 cm proud los bordes quedaban a ras de la
/// pared. El ráster no lo mira (decoración), así que no cuesta celdas.
pub const CASING_PROUD_CM: i32 = 4;
/// ADR-125 enm. 2 — cuánto entra el marco en la luz de la boca: un centímetro, para que su cara
/// interior no sea coplanar con la mocheta (z-fighting) y para que la arquivolta no comparta el
/// intradós con el arco. El cliente lo refleja: la curva interior de la arquivolta es el intradós
/// del arco (la caja menos `CASING_W_CM`) empujado esto hacia el hueco por su normal
/// (`Wg3MeshBuilder.ArchCasingWM` / `ArchCasingInM`).
pub const CASING_IN_CM: i32 = 1;

/// ADR-121 D4 — paso del sorteo de giros. Un giro arbitrario no se lee como intención.
pub const YAW_STEP_DEG: i16 = 15;
/// ADR-121 D4 — lado mínimo de un macizo GIRADO. Por debajo de la celda (50) la geometría cambia de
/// significado al cruzar el cable: el ráster la engorda a la celda y el cliente la dibuja fina.
pub const ROTATED_SIDE_MIN_CM: i32 = 45;

impl Wg3Solid {
    /// ADR-129 D2 — ¿es invisible (frena y no se dibuja)? Ver [`STYLE_HIDDEN_BIT`].
    pub fn is_hidden(&self) -> bool {
        self.style & STYLE_HIDDEN_BIT != 0
    }

    /// ADR-125 enm. 2 — ¿es decoración? Ver [`STYLE_DECOR_BIT`].
    pub fn is_decoration(&self) -> bool {
        self.style & STYLE_DECOR_BIT != 0
    }

    /// El centro de la huella, en metros. Es lo que decide de qué chunk es el macizo (ADR-105 D3).
    pub fn centre(&self) -> (f32, f32) {
        (
            metres(self.x_cm) + metres(self.size_x_cm) * 0.5,
            metres(self.z_cm) + metres(self.size_z_cm) * 0.5,
        )
    }

    /// `(min_x, min_z, max_x, max_z)` en metros: la ENVOLVENTE de la huella girada (ADR-121 D1). Es
    /// lo que usan las exclusiones del relleno y el filtro por chunk del ráster, y por eso tiene que
    /// crecer con el giro: un tabique a 45° toca celdas que su caja sin girar no toca.
    pub fn bounds(&self) -> (f32, f32, f32, f32) {
        let (cx, cz) = self.centre();
        let (hx, hz) = (metres(self.size_x_cm) * 0.5, metres(self.size_z_cm) * 0.5);
        if self.yaw_deg == 0 {
            return (cx - hx, cz - hz, cx + hx, cz + hz);
        }
        let (sin, cos) = (self.yaw_deg as f32).to_radians().sin_cos();
        let ext_x = hx * cos.abs() + hz * sin.abs();
        let ext_z = hx * sin.abs() + hz * cos.abs();
        (cx - ext_x, cz - ext_z, cx + ext_x, cz + ext_z)
    }

    /// ADR-121 D4 / ADR-125 — lo que este módulo exige antes de emitir un macizo. Vacío = utilizable.
    pub fn problems(&self) -> Vec<String> {
        let mut out = Vec::new();
        if self.size_x_cm <= 0 || self.size_z_cm <= 0 {
            out.push(format!(
                "huella no positiva: {}×{} cm",
                self.size_x_cm, self.size_z_cm
            ));
        }
        if self.top_y_cm <= self.bottom_y_cm {
            out.push(format!(
                "banda vertical vacía: {}..{} cm",
                self.bottom_y_cm, self.top_y_cm
            ));
        }
        // 0..360 y no los 0..165 de ADR-121 D4: una caja es simétrica y le bastaba media vuelta,
        // pero la media luna de ADR-125 tiene DIRECCIÓN (la panza), y 270 no es 90.
        if !(0..360).contains(&self.yaw_deg) || self.yaw_deg % YAW_STEP_DEG != 0 {
            out.push(format!(
                "giro de {}°: tiene que ser múltiplo de {} en 0..360",
                self.yaw_deg, YAW_STEP_DEG
            ));
        }
        if self.yaw_deg != 0 && self.size_x_cm.min(self.size_z_cm) < ROTATED_SIDE_MIN_CM {
            out.push(format!(
                "macizo girado de {}×{} cm: por debajo de {} cm el ráster y el cliente ya no dicen \
                 lo mismo",
                self.size_x_cm, self.size_z_cm, ROTATED_SIDE_MIN_CM
            ));
        }
        match self.shape {
            SHAPE_BOX => {}
            SHAPE_CYLINDER | SHAPE_OCTAGON => {
                if self.size_x_cm != self.size_z_cm {
                    out.push(format!(
                        "forma {} sobre huella {}×{}: sólo se inscribe en un cuadrado",
                        self.shape, self.size_x_cm, self.size_z_cm
                    ));
                }
                if self.shape == SHAPE_CYLINDER && self.yaw_deg != 0 {
                    out.push("un cilindro girado no significa nada".to_string());
                }
            }
            SHAPE_ARCH => {
                if self.yaw_deg != 0 {
                    out.push("un arco no gira: la caja ya dice su orientación".to_string());
                }
                if self.size_x_cm == self.size_z_cm {
                    out.push(format!(
                        "arco sobre huella cuadrada {}×{}: no se sabe cuál es la cuerda",
                        self.size_x_cm, self.size_z_cm
                    ));
                }
                if self.top_y_cm - self.bottom_y_cm <= ARCH_KEY_CM {
                    out.push(format!(
                        "arco de {} cm de banda: no cabe ni la clave ({})",
                        self.top_y_cm - self.bottom_y_cm,
                        ARCH_KEY_CM
                    ));
                }
            }
            SHAPE_HALF_CYLINDER => {
                if self.size_z_cm * 2 != self.size_x_cm {
                    out.push(format!(
                        "media luna de {}×{}: el fondo tiene que ser la mitad del ancho",
                        self.size_x_cm, self.size_z_cm
                    ));
                }
                if self.yaw_deg % 90 != 0 {
                    out.push(format!(
                        "media luna a {}°: sólo se adosa a paredes, múltiplos de 90",
                        self.yaw_deg
                    ));
                }
            }
            other => out.push(format!("forma {other} desconocida")),
        }
        out
    }
}

/// La caja de colisión de un macizo: la HUELLA girada. Para las formas no-caja es la envolvente
/// (conservadora); el estampado exacto de disco y octógono lo hace `Wg3RasterBuilder::add_solid`.
pub fn solid_box(s: &Wg3Solid) -> PlacedBox {
    let (x, z) = (metres(s.x_cm), metres(s.z_cm));
    let (sx, sz) = (metres(s.size_x_cm), metres(s.size_z_cm));
    let sy = metres(s.top_y_cm - s.bottom_y_cm);
    PlacedBox {
        center: [x + sx * 0.5, metres(s.bottom_y_cm) + sy * 0.5, z + sz * 0.5],
        size: [sx, sy, sz],
        yaw_degrees: s.yaw_deg as f32,
        kind: KIND_WALL,
    }
}

/// ADR-099 D3 — cuánto se deja intacto por encima del suelo al excavar, en centímetros.
///
/// Sin esta guarda el vano se lleva la losa sobre la que se anda y abre un agujero por el que se cae
/// en vez de una puerta. Es el mismo fallo que ADR-095 ya pagó con las bocas al vacío, y aquí sería
/// más difícil de ver porque el agujero está DENTRO de una puerta que funciona.
pub const CARVE_FLOOR_GUARD_CM: i32 = 5;

/// Un rectángulo generado, con sus bocas.
///
/// EN CENTÍMETROS ENTEROS, por lo mismo que `Wg3Placement`: esto viaja, se compara entre dos
/// procesos y tiene que coincidir bit a bit. Una cadena de sumas en `f32` no lo garantiza.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Wg3Segment {
    /// Esquina mínima de la huella, en centímetros de mundo.
    pub x_cm: i32,
    pub z_cm: i32,
    pub size_x_cm: i32,
    pub size_z_cm: i32,
    /// Cota del suelo, en centímetros de mundo (ADR-097, mismas unidades que la colocación).
    pub floor_y_cm: i32,
    /// Altura LIBRE, de suelo a techo. La losa de techo va por encima.
    pub height_cm: i32,
    /// De una a cuatro. Un tramo sin bocas sería una caja maciza, y hay test que lo prohíbe.
    pub openings: Vec<Wg3Opening>,
    /// Aspecto. El servidor no lo interpreta: es el gancho para que el cliente vista los conectores
    /// y el mundo no se lea generado.
    pub style: u8,
}

impl Wg3Segment {
    pub fn min_x(&self) -> f32 {
        metres(self.x_cm)
    }
    pub fn min_z(&self) -> f32 {
        metres(self.z_cm)
    }
    pub fn size_x(&self) -> f32 {
        metres(self.size_x_cm)
    }
    pub fn size_z(&self) -> f32 {
        metres(self.size_z_cm)
    }
    pub fn floor_y(&self) -> f32 {
        metres(self.floor_y_cm)
    }
    pub fn height(&self) -> f32 {
        metres(self.height_cm)
    }

    /// `(min_x, min_z, max_x, max_z)` en metros.
    pub fn bounds(&self) -> (f32, f32, f32, f32) {
        let (x, z) = (self.min_x(), self.min_z());
        (x, z, x + self.size_x(), z + self.size_z())
    }

    /// Centro de la huella, en metros. Es lo que decide de qué chunk es el tramo.
    pub fn centre(&self) -> (f32, f32) {
        let (x, z) = (self.min_x(), self.min_z());
        (x + self.size_x() * 0.5, z + self.size_z() * 0.5)
    }

    /// Lo que este módulo necesita que sea cierto antes de emitir un tramo. Vacío = utilizable.
    pub fn problems(&self) -> Vec<String> {
        let mut out = Vec::new();
        if self.size_x_cm <= 0 || self.size_z_cm <= 0 {
            out.push(format!(
                "huella no positiva: {}×{} cm",
                self.size_x_cm, self.size_z_cm
            ));
        }
        let max_cm = (MAX_SEGMENT_M * CM_PER_M) as i32;
        if self.size_x_cm > max_cm || self.size_z_cm > max_cm {
            // El tope no es estético: es lo que sostiene el reparto por chunk (ver `MAX_SEGMENT_M`).
            out.push(format!(
                "tramo de {}×{} cm por encima del tope de {} cm — el reparto por chunk depende de \
                 este número",
                self.size_x_cm, self.size_z_cm, max_cm
            ));
        }
        if self.height_cm <= 0 {
            out.push(format!("altura no positiva: {} cm", self.height_cm));
        }
        if self.openings.is_empty() {
            out.push("sin bocas: sería una caja maciza".to_string());
        }
        for o in &self.openings {
            if o.width_cm <= 0 {
                out.push(format!("boca de anchura {} cm", o.width_cm));
            } else if o.width_cm < MIN_GENERATED_WIDTH_CM {
                // No es estética: por debajo de aquí el ráster tapia el vano y el conector nace
                // impasable mientras el cliente lo dibuja abierto. Ver `MIN_GENERATED_WIDTH_CM`.
                out.push(format!(
                    "boca de {} cm por debajo del mínimo de {} cm: el ráster la tapiaría",
                    o.width_cm, MIN_GENERATED_WIDTH_CM
                ));
            }
            let side_cm = if o.side.is_multiple_of(2) {
                self.size_x_cm
            } else {
                self.size_z_cm
            };
            let half = o.width_cm / 2;
            if o.offset_cm - half < 0 || o.offset_cm + half > side_cm {
                out.push(format!(
                    "boca del lado {} a {} cm ± {} cm se sale del lado ({} cm)",
                    o.side, o.offset_cm, half, side_cm
                ));
            }
        }
        out
    }
}

/// La geometría de un tramo, ya en coordenadas de mundo.
///
/// ORDEN DE EMISIÓN: suelo, techo, y luego los lados 0, 1, 2 y 3, cada uno con sus tramos de pared
/// de menor a mayor offset. **El orden es parte del contrato**: el oráculo compara caja a caja y en
/// orden, y reordenar aquí lo pondría rojo sin que nada esté mal — que es peor que un test que no
/// existe.
///
/// La decoración (rodapié) NO se emite: es del cliente (R25). Lo que sale de aquí es exactamente lo
/// que bloquea.
pub fn segment_boxes(cell: &Wg3Segment) -> Vec<PlacedBox> {
    let w = cell.size_x();
    let d = cell.size_z();
    let h = cell.height();
    let (ox, oz, oy) = (cell.min_x(), cell.min_z(), cell.floor_y());

    let mut out = Vec::with_capacity(8);
    let mut push = |kind: u8, cx: f32, cy: f32, cz: f32, sx: f32, sy: f32, sz: f32| {
        out.push(PlacedBox {
            center: [ox + cx, oy + cy, oz + cz],
            size: [sx, sy, sz],
            // Un tramo está alineada a los ejes por construcción: nunca hay giro que aplicar. Es lo
            // que permite partirla en la frontera de un chunk o alargarla sin tocar nada más.
            yaw_degrees: 0.0,
            kind,
        })
    };

    // El suelo cuelga por DEBAJO de la cota de el tramo para que la cara pisable quede exactamente
    // en ella: dos tramos contiguas a la misma cota no dejan escalón de losa, y dos a cotas
    // distintas dejan exactamente su diferencia.
    push(
        KIND_FLOOR,
        w * 0.5,
        -SLAB_THICKNESS_M * 0.5,
        d * 0.5,
        w,
        SLAB_THICKNESS_M,
        d,
    );
    push(
        KIND_CEILING,
        w * 0.5,
        h + SLAB_THICKNESS_M * 0.5,
        d * 0.5,
        w,
        SLAB_THICKNESS_M,
        d,
    );

    for side in 0..4u8 {
        emit_side(cell, side, w, d, h, &mut push);
    }

    out
}

/// La pared de un lado, partida por sus bocas. Espejo de `Wg3Geometry.BuildSide`.
///
/// Es el punto donde «el vano existe» deja de ser una afirmación: si este recorrido se equivoca, la
/// colisión tapa una puerta que se ve abierta.
fn emit_side<F>(cell: &Wg3Segment, side: u8, w: f32, d: f32, h: f32, push: &mut F)
where
    F: FnMut(u8, f32, f32, f32, f32, f32, f32),
{
    let length = if side.is_multiple_of(2) { w } else { d };

    // Se ordenan aquí y no se presume el orden de quien construyó el tramo: dos bocas declaradas al
    // revés dejarían un tramo de longitud negativa.
    let mut cuts: Vec<(f32, f32)> = cell
        .openings
        .iter()
        .filter(|o| o.side % 4 == side)
        .map(|o| {
            let centre = o.offset_cm as f32 / CM_PER_M;
            let half = o.width_cm as f32 / CM_PER_M * 0.5;
            (centre - half, centre + half)
        })
        .collect();
    cuts.sort_by(|a, b| a.0.total_cmp(&b.0));

    let mut cursor = 0.0f32;
    for (lo, hi) in cuts {
        if lo > cursor {
            emit_wall(side, cursor, lo, w, d, h, push);
        }
        cursor = cursor.max(hi);
    }
    if cursor < length {
        emit_wall(side, cursor, length, w, d, h, push);
    }
}

/// Un tramo de pared entre dos offsets del lado. Espejo de `Wg3Geometry.EmitWall`, sin el rodapié.
fn emit_wall<F>(side: u8, from: f32, to: f32, w: f32, d: f32, h: f32, push: &mut F)
where
    F: FnMut(u8, f32, f32, f32, f32, f32, f32),
{
    let mid = (from + to) * 0.5;
    let len = to - from;
    if len <= 1e-3 {
        return;
    }
    let t = WALL_THICKNESS_M;

    match side % 4 {
        // N, z = d. El offset corre en +X.
        0 => push(KIND_WALL, mid, h * 0.5, d - t * 0.5, len, h, t),
        // E, x = w. El offset corre en −Z desde z = d.
        1 => push(KIND_WALL, w - t * 0.5, h * 0.5, d - mid, t, h, len),
        // S, z = 0. El offset corre en −X desde x = w.
        2 => push(KIND_WALL, w - mid, h * 0.5, t * 0.5, len, h, t),
        // O, x = 0. El offset corre en +Z.
        _ => push(KIND_WALL, t * 0.5, h * 0.5, mid, t, h, len),
    }
}
