//! ADR-100 — EL PLAN DE REGIÓN: qué edificio hay aquí, decidido antes de colocar una sola pieza.
//!
//! # Qué problema resuelve, y por qué no lo resolvían las palancas anteriores
//!
//! Hasta aquí el mundo lo decidía `compose`: se saca una boca de una frontera BFS, se sortea una
//! pieza que case y la posición sale *determinada* por esa boca. Nada en ese bucle mira el conjunto.
//! La consecuencia está medida y escrita en ADR-099 con las palabras de quien lo juega —«todo es
//! pasillos», «cosas sin solaparse bien»—: **el mundo crece en CADENA, así que dos salas nunca
//! acaban lado a lado porque no hay nada que las ponga ahí.**
//!
//! Ninguna de las palancas que se probaron ataca eso, y las tres se midieron: compartir pared sube
//! el llenado nueve décimas, la absorción cambia la topología sin tocar la superficie, y densificar
//! lo dobla pero rompe la conectividad porque planta al azar. Son parches sobre un reparto que nadie
//! ha decidido.
//!
//! Este módulo decide el reparto. **Es la nueva fuente de verdad arquitectónica**: aquí se dice qué
//! espacios existen, de qué tamaño, con qué papel y unidos a cuáles. Todo lo que viene después
//! —elegir pieza, tender conector, rasterizar, dibujar— ejecuta este plan y no puede contradecirlo.
//!
//! # Cómo, en una frase
//!
//! Subdivisión recursiva del rectángulo de región, **con los corredores tallados en los cortes de
//! los primeros niveles**. Eso da tres cosas de golpe que el compositor-árbol no podía dar:
//!
//! 1. **Masa contigua por construcción.** Los hijos de un corte llenan al padre entero, así que el
//!    vacío deja de ser lo que sobra y pasa a ser algo que se marca a propósito ([`SpaceRole::Void`]).
//! 2. **Jerarquía real.** El corte de nivel 0 es la espina; los de nivel 1 y 2, corredores
//!    secundarios; de ahí para abajo los cortes NO tallan banda, así que las salas hermanas comparten
//!    pared y se comunican por un vano. Un edificio, no una cuadrícula de pasillos.
//! 3. **El grafo antes que la geometría.** Las adyacencias del reparto SON las conexiones
//!    candidatas, y salen gratis: dos rectángulos que comparten borde ya se tocan.
//!
//! # Lo que este módulo NO hace, y es deliberado
//!
//! No mira el catálogo, no conoce `Wg3Piece`, no emite geometría y no sabe qué es una malla. Un plan
//! es válido con un catálogo vacío. Esa frontera es lo que permite medir la arquitectura sola —el
//! criterio de aceptación del ADR es que **el plano se lea como un edificio con las mallas
//! apagadas**— y lo que impide que el contenido vuelva a decidir la forma por la puerta de atrás.
//!
//! # Determinismo (R3)
//!
//! Cada decisión abre su propio flujo desde la POSICIÓN —el centro del rectángulo que se está
//! partiendo— y una sal propia, nunca desde un índice ni desde el orden de proceso. Es la misma
//! regla que ya cumplen el compositor y el campo de escala, y es lo que permitirá que el plan sea
//! troceable el día que haga falta: partir el mismo rectángulo dos veces da el mismo corte sin que
//! nadie recuerde nada.
//!
//! **EN CENTÍMETROS ENTEROS**, por lo mismo que `Wg3Placement` y `Wg3Segment`: un plan se compara
//! entre procesos y una cadena de sumas en `f32` no garantiza que dos backends coincidan bit a bit.

use super::hash;
use super::junction::Wg3Gate;
use super::raster::CM_PER_M;
use super::scale;
use super::segment::SLAB_THICKNESS_M;

/// El grosor de losa en centímetros enteros, que es la unidad del plan.
///
/// Derivado y no escrito a mano: si alguien engorda el forjado, la altura de planta y el tope de techo
/// tienen que moverse con él o la geometría de abajo vuelve a atravesar el suelo de arriba.
pub const SLAB_THICKNESS_CM: i32 = (SLAB_THICKNESS_M * CM_PER_M) as i32;

/// Sal del sorteo de corte: eje y posición.
const SALT_SPLIT: u32 = 0x9A17_0000;
/// Sal de la decisión de parar de subdividir.
const SALT_STOP: u32 = 0x9A17_0001;
/// Sal del reparto de papeles.
const SALT_ROLE: u32 = 0x9A17_0002;
/// Sal del sorteo de vacío intencionado.
const SALT_VOID: u32 = 0x9A17_0003;
/// Sal de los vanos de más, los que cierran anillos.
const SALT_RING: u32 = 0x9A17_0004;
/// Sal del sorteo de desnivel.
const SALT_TERRACE: u32 = 0x9A17_0005;
/// Sal de la huella de una planta alta (ADR-102 D3). Se le suma el número de planta para que cada
/// una abra su propio flujo: la posición del sorteo sigue siendo la del mundo, que es lo que pide R3
/// — lo que el número de planta cambia es el flujo, no el origen.
const SALT_STOREY: u32 = 0x9A17_0006;
/// Sal de la elección de hueco de escalera.
const SALT_WELL: u32 = 0x9A17_0007;

/// Plantas que se sirven por región (ADR-102 D3).
///
/// **Dos, y no porque dos sea el número correcto**: el salto de una a dos es donde están todos los
/// fallos —el forjado que hay que perforar, la geometría de abajo que atraviesa el suelo de arriba, la
/// métrica que deja de significar lo mismo— y el de dos a tres no aporta ninguno nuevo. Subirlo es
/// cambiar este número y volver a medir, no escribir código.
pub const REGION_STOREYS: usize = 2;

/// Cuánto tiene que apartarse un corte del centro de una puerta de junta, en centímetros.
///
/// **Un corte que cae sobre una puerta la parte entre dos espacios y ninguno puede abrirla**: media
/// puerta es un muro, y la región nace sellada por ese lado mientras la vecina abre la suya contra
/// él. Medido antes de arreglarlo: 64 de 256 puertas perdidas en 49 regiones, y sólo 2 de 4 regiones
/// alcanzables andando —36 632 m² contra 71 393—. El único rastro era un `warn` en el log.
///
/// Es media puerta (120) + media banda de corredor (160) + una jamba (60), y los tres sumandos hacen
/// falta: sin el segundo la banda cae encima de la puerta y la deja con 40 cm de jamba, por debajo de
/// lo que [`Planner::touches_border_point`] acepta, así que se pierde igual.
const GATE_CLEARANCE_CM: i32 = DOORWAY_CM / 2 + BAND_WIDTH_CM[0] / 2 + 60;

/// **CONTRAHUELLA, y el número NO es de comodidad: es el grosor de la losa.**
///
/// El suelo de un tramo cuelga por DEBAJO de su cota (`SLAB_THICKNESS_M`, 12 cm) para que la cara
/// pisable quede exactamente en ella. Con una contrahuella de 12, la losa del escalón de arriba llega
/// justo hasta la cara del de abajo y **el peldaño queda cerrado sin una sola caja nueva**. Con los 18
/// de la escalera del catálogo quedaría una rendija de 6 cm en cada escalón, por la que se ve el
/// vacío.
///
/// Y se anda de sobra: el jugador sube 27,5 cm sin saltar (`probe_what_step_each_piece_demands`).
pub const STEP_RISE_CM: i32 = 12;

/// Escalones de un desnivel, como mucho. 5 × 12 = 60 cm: se ve desde la puerta y sigue siendo un
/// tramo corto de bajar.
///
/// Es un TOPE, no un objetivo: cada peldaño tiene que ser una franja más ancha que un vano
/// (`MIN_GENERATED_WIDTH_CM`), así que una sala poco profunda baja menos escalones. Sin ese suelo, la
/// pared que se abre entre dos franjas cae por debajo de lo que el ráster conservador deja pasar y la
/// escalera nace tapiada — el mismo número que ya gobierna todo lo generado.
const TERRACE_STEPS: i32 = 5;

/// Lo que el jugador sube sin saltar, en centímetros. Medido, no elegido.
///
/// Es el tope de cualquier salto de cota entre dos espacios unidos por un vano. Por encima, la puerta
/// se dibuja abierta y no se pasa — el peor fallo posible, porque no sale en una captura.
pub const MAX_WALK_STEP_CM: i32 = 27;

/// **La planta canónica (ADR-102 D2): 320 cm de altura libre más 12 de losa.**
///
/// 320 es el `_ => 320` de `fill::clear_height_cm` y la altura de diez de las diecinueve piezas del
/// catálogo, así que una retícula a 332 encaja lo generado y casi todo lo autorado sin tocar nada. Y
/// es cara pisable a cara pisable: la losa cuelga por DEBAJO de su cota, no por encima.
pub const STOREY_HEIGHT_CM: i32 = 332;

/// Contrahuella de una escalera que sube una planta entera (ADR-102 D4).
///
/// No son los [`STEP_RISE_CM`] de una terraza, y la diferencia no es un gusto. Con 12 cm, subir 332
/// pide 28 escalones; como la huella sale de repartir el ancho del espacio entre los escalones, harían
/// falta unos 8 m de tiro para que cada peldaño midiera 28 cm. Eso no es un hueco de escalera, es un
/// salón inclinado. Con 24 son **14 escalones de 23,7 cm** —por debajo de [`MAX_WALK_STEP_CM`], que es
/// lo que de verdad manda— y 4 m de tiro dan huellas de 28,6.
pub const STOREY_RISE_CM: i32 = 24;

/// Huella de un peldaño de escalera de planta, en centímetros. 15 × 60 = 9 m de tiro.
///
/// **Sesenta porque la celda del ráster mide cincuenta, y no porque sea cómodo.** El primer intento
/// puso 30 —una huella cómoda de verdad— y la escalera salió INANDABLE, con el plan coherente y todos
/// los contadores en verde. El ráster es conservador: toda celda que una caja toque queda maciza a la
/// cota de esa caja, así que con la huella por debajo de la celda cada celda de 50 cm se come dos
/// peldaños y su cara pisable salta a la del más alto. Medido en el volcado de (0,0): celdas vecinas a
/// 0,5 m se llevaban 40 cm de desnivel entre ellas, más de los 27 que sube el jugador. La escalera
/// existía, se dibujaba entera, y no se podía subir.
///
/// Con 60 una celda alcanza dos peldaños como mucho y el salto entre celdas vecinas se queda en una
/// contrahuella. El punto 3 de la documentación de `fill::emit_stair` ya lo decía con todas las
/// letras; el precio de no leerlo fue una tarde.
const STAIR_TREAD_CM: i32 = 60;

/// Ancho de un hueco de escalera, en centímetros.
///
/// **El recorte importa tanto como el tiro.** Sin él, el hueco es el espacio ENTERO que le tocó, y en
/// las cuatro regiones de referencia eso daba escaleras de hasta 12 × 15 m: catorce peldaños repartidos
/// en 15 metros son huellas de 109 cm, que no es una escalera sino una rampa con escalones. 360 cm dan
/// dos personas de frente y dejan sitio para que el resto del espacio siga siendo suelo.
const STAIR_WIDTH_CM: i32 = 360;

/// Huella mínima de un peldaño, en centímetros: la celda del ráster y un poco más.
///
/// **El suelo no lo pone el pie del jugador, lo pone la resolución de la colisión.** Al
/// `CharacterController` le basta con que la contrahuella quepa en su `m_StepOffset` y la huella le da
/// igual; al ráster no. Por debajo de la celda, un peldaño desaparece dentro de otro y el desnivel
/// entre celdas vecinas pasa a ser de varias contrahuellas. Ver [`STAIR_TREAD_CM`].
pub const MIN_TREAD_CM: i32 = (super::raster::WG3_CELL_M * CM_PER_M) as i32 + 5;

/// Probabilidad de que un corte interior se lleve un desnivel.
const TERRACE_CHANCE: f32 = 0.34;

/// Profundidad a partir de la cual un corte YA NO talla banda de corredor.
///
/// **Es el número que separa un edificio de una cuadrícula de pasillos, y por eso es tan bajo.** Con
/// banda en todos los cortes, a profundidad 6 hay corredor entre cualesquiera dos salas y el
/// resultado se lee como una retícula —justo lo que WG3 vino a quitar—. Con banda sólo en los tres
/// primeros niveles, el mundo tiene una espina, un par de ramas, y **a partir de ahí las salas
/// comparten pared y se comunican por un vano**, que es como está hecho un edificio de oficinas.
const CORRIDOR_DEPTH: u8 = 3;

/// Ancho de la banda de corredor por profundidad de corte, en centímetros.
///
/// Decrece con la profundidad porque la jerarquía tiene que VERSE andando: la espina es más ancha
/// que la rama, y la rama más que el ramal. El último valor es también el suelo: 240 cm es la
/// anchura de boca del catálogo y el mínimo que el ráster conservador deja pasar
/// (`MIN_GENERATED_WIDTH_CM`).
const BAND_WIDTH_CM: [i32; CORRIDOR_DEPTH as usize] = [320, 280, 240];

/// Lado mínimo de un espacio, en centímetros.
///
/// Por debajo de esto no es una sala: es el hueco que queda entre dos paredes. Se comprueba ANTES de
/// cortar —los dos hijos tienen que cumplirlo, banda incluida— para que la subdivisión nunca produzca
/// una astilla que luego haya que tirar.
const MIN_SIDE_CM: i32 = 500;

/// Profundidad máxima del árbol de subdivisión.
///
/// **Es una cota de seguridad, y tiene que estar MUY por encima de lo que se usa** — si se convierte
/// en el criterio que para la subdivisión, el campo de escala deja de mandar y todas las regiones
/// salen con el mismo tamaño de sala. Pasó con 7: una región de 22 500 m² no llega a hojas de 150 m²
/// antes de agotarla, así que el reparto se quedaba en 34 naves de 460 m² de media y el área objetivo
/// no pintaba nada. Con 12 el que corta es siempre `TARGET_AREA_M2`, que es lo que se quería.
const MAX_DEPTH: u8 = 12;

/// Dónde puede caer un corte dentro del lado que parte, en tantos por uno.
///
/// **No es la mitad, y eso es la mitad del aspecto.** Un corte centrado da hijos iguales, y un árbol
/// de hijos iguales es una cuadrícula por mucho que se llame BSP. Cortar entre el 32 % y el 68 % da
/// hermanos de tamaños distintos en cada nivel, que es de donde sale que un edificio tenga una sala
/// grande al lado de tres pequeñas.
const SPLIT_LO: f32 = 0.32;
const SPLIT_HI: f32 = 0.68;

/// Probabilidad de partir por el lado CORTO aunque el largo sea el candidato natural.
///
/// Partir siempre por el lado largo converge a rectángulos cuadrados, y un edificio real tiene
/// pasillos largos y salas alargadas. Este escape es lo que deja aparecer proporciones raras sin que
/// sean la norma.
const CROSS_SPLIT_CHANCE: f32 = 0.18;

/// Proporción a partir de la cual ya no se permite el escape: sólo se parte el lado largo.
///
/// **Una proporción no se arregla subdividiendo, se hereda.** Un rectángulo 3:1 al que se le corta
/// el lado corto pasa a 6:1 y todos sus descendientes salen de ahí. Este tope es lo que impide que
/// un escape pensado para dar variedad acabe produciendo una región entera de pasillos de 5 m de
/// ancho que nadie pidió.
const MAX_ASPECT: f32 = 2.6;

/// Área objetivo de un espacio según la clase de escala del campo, en metros cuadrados.
///
/// **Aquí es donde `scale_at` pasa a decidir arquitectura en vez de sesgar un sorteo.** Antes
/// multiplicaba el peso de una pieza candidata —o sea que sólo influía en cuál de las que ya cabían
/// salía elegida—; ahora fija cuánto se subdivide una zona, y por tanto el TAMAÑO de los espacios que
/// habrá allí. Una zona `Narrow` se trocea en despachos; una `Large` se queda en nave.
/// **Estos números se midieron dos veces y las dos primeras estaban mal**, y el histograma de la
/// sonda es lo que lo dijo: con 70/150/360 salían 268 espacios por región de los que 81 eran
/// trasteros de menos de 55 m² y CERO naves — variedad en el papel, todo pequeño en la práctica. Un
/// mínimo y un máximo separados no son variedad; hay que mirar el reparto.
const TARGET_AREA_M2: [f32; 4] = [
    110.0, // SCALE_NARROW — despachos
    240.0, // SCALE_MEDIUM — oficinas normales
    700.0, // SCALE_LARGE  — naves, salas diáfanas
    380.0, // SCALE_WEIRD  — ver `WEIRD_SPREAD`: aquí el número no manda solo
];

/// Cuánto puede desviarse el área objetivo en una zona `Weird`, como factor.
///
/// La escala rara no significa «grande» ni «pequeña»: significa que ahí las proporciones no siguen
/// la regla. Un factor entre 0,35 y 2,6 mete en el mismo mundo el armario absurdo y la nave
/// desproporcionada, que es lo que hace que un sitio se lea como Backrooms y no como un edificio de
/// oficinas bien diseñado.
const WEIRD_SPREAD: (f32, f32) = (0.35, 2.6);

/// Probabilidad base de que una hoja se marque como vacío intencionado.
///
/// **El vacío deja de ser un fallo y pasa a ser una decisión.** Hasta ahora el 75-96 % de una región
/// era vacío porque nadie fue a mirar ahí; aquí el hueco existe porque se ha dicho que exista —patio,
/// zona clausurada, hueco de instalaciones— y por eso puede ser poco y estar donde tiene que estar.
const VOID_CHANCE: f32 = 0.11;

/// Probabilidad extra de vacío en zona rara. Una zona `Weird` con un descampado dentro es liminal;
/// la misma zona llena es sólo un edificio.
const VOID_CHANCE_WEIRD: f32 = 0.22;

/// Área a partir de la cual una hoja es una sala grande y no una oficina, en metros cuadrados.
const HALL_AREA_M2: f32 = 300.0;

/// Área por debajo de la cual una hoja es trastero y no oficina.
const STORAGE_AREA_M2: f32 = 45.0;

/// Anchura de un vano normal, en centímetros. Es la boca `Corridor` del catálogo.
pub const DOORWAY_CM: i32 = 240;

/// Anchura de un vano ancho — el que abre a una nave. Es la boca `Wide` del catálogo.
pub const WIDE_DOORWAY_CM: i32 = 500;

/// Solape de pared mínimo para que DOS ESPACIOS SE CONSIDEREN VECINOS, en centímetros.
///
/// Es el vano pelado, sin jambas. **Y tiene que ser exactamente el vano, no más**: dos bandas de
/// corredor que se cruzan comparten justo el ancho de la más estrecha, así que exigir jambas dejaba
/// los cruces fuera y el plano salía con CERO intersecciones — una red de corredores que no se
/// tocaban. Las jambas se prefieren donde importa (ver [`GOOD_WALL_CM`]), no se exigen aquí.
const MIN_SHARED_WALL_CM: i32 = DOORWAY_CM;

/// Solape de pared que se PREFIERE al elegir por dónde entra una sala. El vano más sus dos jambas.
///
/// Preferencia y no ley: entre dos paredes candidatas gana la ancha, pero una sala que sólo toca el
/// corredor por el mínimo entra por ahí igual — quedarse sin acceso es peor que una jamba estrecha.
const GOOD_WALL_CM: i32 = DOORWAY_CM + 120;

/// Probabilidad de abrir un vano de MÁS entre dos salas ya conectadas por otro camino.
///
/// Es lo que convierte el grafo en un edificio con anillos en vez de en un árbol. Un edificio real
/// tiene más de una forma de llegar a sitio, y sin eso vuelve el «llega un punto que se cierra».
const RING_CHANCE: f32 = 0.30;

/// Un rectángulo del plan, en centímetros enteros de mundo.
///
/// Propio y no `route::Rect` a propósito: aquél está en metros y en `f32` porque el enrutador compara
/// contra geometría ya colocada. Un plan se compara entre procesos, así que va en enteros — la misma
/// razón por la que la llevan `Wg3Placement` y `Wg3Segment`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlanRect {
    pub min_x_cm: i32,
    pub min_z_cm: i32,
    pub max_x_cm: i32,
    pub max_z_cm: i32,
}

impl PlanRect {
    pub fn width_cm(&self) -> i32 {
        self.max_x_cm - self.min_x_cm
    }
    pub fn depth_cm(&self) -> i32 {
        self.max_z_cm - self.min_z_cm
    }
    pub fn area_m2(&self) -> f32 {
        (self.width_cm() as f32 / CM_PER_M) * (self.depth_cm() as f32 / CM_PER_M)
    }
    /// El mismo rectángulo metido `margin` hacia dentro por los cuatro lados (ADR-102 D5).
    pub fn shrunk(&self, margin_cm: i32) -> PlanRect {
        PlanRect {
            min_x_cm: self.min_x_cm + margin_cm,
            min_z_cm: self.min_z_cm + margin_cm,
            max_x_cm: self.max_x_cm - margin_cm,
            max_z_cm: self.max_z_cm - margin_cm,
        }
    }
    /// ¿Se pisan estos dos rectángulos, tocarse NO cuenta? (ADR-102 D2)
    pub fn overlaps(&self, other: &PlanRect) -> bool {
        self.min_x_cm < other.max_x_cm
            && self.max_x_cm > other.min_x_cm
            && self.min_z_cm < other.max_z_cm
            && self.max_z_cm > other.min_z_cm
    }
    /// ¿Cae este punto dentro, bordes EXCLUIDOS? (ADR-102 D4)
    ///
    /// Excluidos porque se usa para preguntar si una puerta se la lleva por delante el recorte de la
    /// escalera, y una puerta en el borde exacto de la franja está en su pared, no dentro.
    pub fn contains_point(&self, x_cm: i32, z_cm: i32) -> bool {
        x_cm > self.min_x_cm && x_cm < self.max_x_cm && z_cm > self.min_z_cm && z_cm < self.max_z_cm
    }
    /// ¿Cabe `other` dentro de éste, bordes incluidos? (ADR-102 D1)
    ///
    /// Contener y no solapar: un hueco de escalera a caballo de dos salas de la planta de arriba
    /// perfora la pared que las separa por debajo, y eso no se ve hasta que se anda por ahí.
    pub fn contains_rect(&self, other: &PlanRect) -> bool {
        other.min_x_cm >= self.min_x_cm
            && other.max_x_cm <= self.max_x_cm
            && other.min_z_cm >= self.min_z_cm
            && other.max_z_cm <= self.max_z_cm
    }
    /// Centro en metros. Es lo que siembra los sorteos: la POSICIÓN, nunca el índice.
    pub fn centre_m(&self) -> (f32, f32) {
        (
            (self.min_x_cm + self.max_x_cm) as f32 * 0.5 / CM_PER_M,
            (self.min_z_cm + self.max_z_cm) as f32 * 0.5 / CM_PER_M,
        )
    }
    /// `(min_x, min_z, max_x, max_z)` en metros, que es como lo quiere todo lo de aguas abajo.
    pub fn bounds_m(&self) -> (f32, f32, f32, f32) {
        (
            self.min_x_cm as f32 / CM_PER_M,
            self.min_z_cm as f32 / CM_PER_M,
            self.max_x_cm as f32 / CM_PER_M,
            self.max_z_cm as f32 / CM_PER_M,
        )
    }
    fn shorter_side_cm(&self) -> i32 {
        self.width_cm().min(self.depth_cm())
    }
}

/// Qué papel juega un espacio en el edificio.
///
/// **Es la pieza de vocabulario que WG3 no tenía**, y la que separa «una colección de rectángulos» de
/// «una planta». El relleno lo lee para elegir contenido, y las sondas lo leen para decir si el
/// reparto se parece a un edificio.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SpaceRole {
    /// La banda del corte de nivel 0: el eje principal de la región. Hay como mucho una.
    Spine,
    /// Banda de corte de nivel 1 o 2: corredor secundario que cuelga de la espina.
    Corridor,
    /// ADR-100 enmienda 2 — **un espacio HUNDIDO**: se entra por su puerta y el suelo baja a peldaños
    /// alejándose de ella.
    ///
    /// Es el único espacio del plan que no es plano, y `rise_cm` dice cuánto baja el fondo respecto a
    /// la puerta.
    ///
    /// # Por qué SÓLO aquí, y por qué el primer intento no valía
    ///
    /// El intento obvio era aterrazar bloques enteros del árbol: un ala 60 cm más alta, con la banda
    /// del corte haciendo de escalera. **No se sostiene, y el validador lo cazó con 17 enlaces
    /// ilegales**: un bloque no toca sólo la banda de SU corte —toca también las bandas de todos sus
    /// ancestros, que se quedan a la cota vieja—, así que el desnivel se escapa por los lados. Y como
    /// esas bandas conectan con todo, propagar la corrección aplana el mundo entero: con esta
    /// topología ningún desnivel por subárbol puede sobrevivir.
    ///
    /// Lo que sí se sostiene es un desnivel **contenido en un espacio con UNA sola puerta**: la cota
    /// de esa puerta no cambia, así que ningún vecino se entera y ningún enlace queda impasable. Un
    /// callejón que baja tres peldaños es además exactamente lo que se busca — el sitio raro del que
    /// no se sale a otro lado.
    Stair,
    /// Donde dos bandas se cruzan. Es un espacio propio porque una intersección es un sitio, no una
    /// arista: es donde se decide por dónde seguir, y merece leerse distinto.
    Junction,
    /// Sala grande y diáfana.
    Hall,
    /// El caso normal: una sala con una o dos salidas.
    Office,
    /// Trasero de la escena: cuartos técnicos, pasos de instalaciones. Se agrupan al fondo de una
    /// rama, lejos del corredor.
    Service,
    /// Almacén: sala pequeña colgada de otra, sin salida propia al corredor.
    Storage,
    /// Sala con UNA sola conexión. No es un fallo del reparto: es una decisión, y es la mitad de lo
    /// que hace que un sitio se recorra con inquietud.
    DeadEnd,
    /// **Vacío INTENCIONADO**: patio, hueco, zona clausurada. No se rellena y no se conecta.
    ///
    /// Existir como papel es lo que lo separa del vacío de antes: aquél era el terreno al que no
    /// llegó ninguna boca, y no había forma de distinguir «aquí no hay nada porque así se ha
    /// decidido» de «aquí no hay nada porque el generador se quedó corto».
    Void,
}

impl SpaceRole {
    /// ¿Es una banda de circulación? Las bandas se rellenan y se conectan distinto que las salas.
    /// ¿Es una banda de circulación? Las bandas se rellenan y se conectan distinto que las salas.
    ///
    /// `Stair` NO lo es: es una sala hundida con una sola puerta, no un sitio por el que se pasa.
    pub fn is_circulation(&self) -> bool {
        matches!(
            self,
            SpaceRole::Spine | SpaceRole::Corridor | SpaceRole::Junction
        )
    }
    /// ¿Se rellena con contenido? El vacío no.
    pub fn is_built(&self) -> bool {
        !matches!(self, SpaceRole::Void)
    }
    /// Nombre corto para logs y volcados.
    pub fn name(&self) -> &'static str {
        match self {
            SpaceRole::Spine => "spine",
            SpaceRole::Corridor => "corridor",
            SpaceRole::Stair => "stair",
            SpaceRole::Junction => "junction",
            SpaceRole::Hall => "hall",
            SpaceRole::Office => "office",
            SpaceRole::Service => "service",
            SpaceRole::Storage => "storage",
            SpaceRole::DeadEnd => "dead_end",
            SpaceRole::Void => "void",
        }
    }
}

/// Un espacio del plan: un sitio del edificio, con su papel, antes de que exista una sola pieza.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlannedSpace {
    pub rect: PlanRect,
    /// Cota del suelo, en centímetros de mundo (ADR-097, mismas unidades que la colocación).
    ///
    /// **Toda a cero en esta versión, y está declarado.** El plan es el sitio natural donde decidir
    /// un desnivel —una nave hundida, una entreplanta— pero hacerlo exige que los enlaces lleven
    /// escalón y que el relleno sepa construirlo, y eso es trabajo aparte. Cero aquí no es un
    /// descuido: es que la verticalidad del plan todavía no se ha decidido.
    pub floor_y_cm: i32,
    pub role: SpaceRole,
    /// Clase del campo de escala en el centro del espacio. Se guarda porque el relleno la necesita
    /// para elegir contenido y recalcularla allí invitaría a que los dos no coincidieran.
    pub scale: u8,
    /// Profundidad en el árbol de subdivisión. **ES LA JERARQUÍA**, medible: un plano donde todo
    /// tiene la misma profundidad es una cuadrícula por mucho que los rectángulos midan distinto.
    pub depth: u8,

    /// ADR-100 enmienda 2 — cuánto baja el FONDO respecto a la puerta, en centímetros. `0` = plano.
    ///
    /// Negativo baja, positivo sube. `floor_y_cm` sigue siendo la cota de la PUERTA, y por eso un
    /// espacio hundido no afecta a ningún vecino: lo que ve el de al lado no cambia.
    pub rise_cm: i32,

    /// El lado por el que se ENTRA (`0 = N`, `1 = E`, `2 = S`, `3 = O`). Los peldaños se alejan de él.
    ///
    /// Se guarda el lado y no un eje porque un eje no dice hacia dónde: con «eje X» habría que
    /// adivinar si se baja hacia +X o hacia −X, y adivinar mal pone la puerta en el fondo del pozo.
    pub rise_from_side: u8,

    /// ADR-102 D4 — cuánto mide UNA contrahuella de este espacio, en centímetros.
    ///
    /// Va en el espacio y no es una constante global porque una terraza y un hueco de escalera piden
    /// números distintos por razones distintas: la terraza usa [`STEP_RISE_CM`] para que el peldaño
    /// cierre contra la losa, y la escalera de planta usa [`STOREY_RISE_CM`] para caber en un hueco y
    /// no en un salón. Con un solo número, el que se elija arruina el otro caso.
    pub rise_step_cm: i32,

    /// ADR-102 D2 — tope de altura libre en centímetros. `0` = sin tope.
    ///
    /// Lo pone [`plan_building`] a los espacios que tienen algo CONSTRUIDO encima, y es lo que impide
    /// que una nave de 4,50 m plante su losa de techo 130 cm por encima del suelo de la planta de
    /// arriba. Que valga cero cuando lo de encima es vacío intencionado no es un descuido: es la
    /// decisión de que una sala a doble altura exista **porque el plan la quiso**, y no porque nadie
    /// mirara.
    pub max_clear_cm: i32,
    /// ADR-104 D1 — **hay planta encima y encima de ESTE espacio no hay nada construido.**
    ///
    /// No es lo mismo que `max_clear_cm == 0`, y confundirlos convierte el ADR en un bulto en el
    /// tejado. Cero quiere decir «sin tope», y eso pasa por DOS motivos distintos: porque arriba hay
    /// vacío intencionado, o porque **no hay planta arriba** —la última siempre vale cero—. Un atrio
    /// es lo primero; subirle el techo a lo segundo es levantar 3,08 m de sala por encima del edificio.
    ///
    /// Sólo lo pone [`cap_headroom_under`], que por definición se llama con una planta encima delante.
    pub void_above: bool,
}

/// Cómo se conectan dos espacios.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LinkKind {
    /// Dos salas que COMPARTEN pared: la conexión es un vano en la pared común. No necesita
    /// enrutador ni geometría nueva.
    Doorway,
    /// Una sala y la banda de corredor que la bordea. Es el caso mayoritario, y es lo que hace que
    /// el corredor sea arquitectura: existe para dar acceso, no para coser.
    Access,
    /// Dos bandas que se encuentran. Marca un cruce.
    Junction,
    /// Dos espacios que NO se tocan. **Es lo único que llega al enrutador**, y llega como encargo:
    /// «une esto con esto», no «busca a ver qué quedó suelto».
    Route,
}

/// Una conexión que el plan DECIDE que existe, antes de que haya geometría que la pueda cumplir.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlannedLink {
    pub a: usize,
    pub b: usize,
    pub width_cm: i32,
    pub kind: LinkKind,
    /// Dónde cae el paso, en centímetros de mundo: el centro del solape de pared para los tres
    /// primeros tipos, el punto medio entre los dos espacios para un `Route`.
    ///
    /// Va en el enlace y no se recalcula aguas abajo porque es una DECISIÓN del plan: dos vanos a la
    /// misma sala tienen que caer donde el plan dijo o el reparto de puertas deja de ser suyo.
    pub at_x_cm: i32,
    pub at_z_cm: i32,
}

/// Una puerta de junta, ya asignada al espacio que la abre.
///
/// **No es un espacio, y ése fue el primer intento fallido.** Plantar un tocón de puerta como
/// rectángulo propio lo mete dentro de la hoja que ya ocupaba ese trozo de borde, y el plan salía con
/// siete solapes por región — geometría cruzada, que el ráster estampa maciza sin quejarse. La puerta
/// no necesita espacio: necesita SABER en qué pared se abre, y la pared es de alguien que ya existe.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlannedGate {
    /// El espacio del plan cuya pared se abre.
    pub space: usize,
    pub x_cm: i32,
    pub z_cm: i32,
    /// Lado de la región que mira AFUERA por esta puerta.
    pub outward_side: u8,
    pub width_cm: i32,
}

/// El edificio de una región, decidido y todavía sin una sola pieza dentro.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct RegionPlan {
    pub spaces: Vec<PlannedSpace>,
    pub links: Vec<PlannedLink>,
    /// Las puertas de junta, cada una colgada del espacio que la abre. Vacío si la región no tenía.
    pub gates: Vec<PlannedGate>,
    /// Caja de la región, para que una sonda no tenga que volver a preguntarla.
    pub bounds_cm: Option<PlanRect>,
}

impl RegionPlan {
    /// Espacios que se van a construir (todos menos el vacío intencionado).
    pub fn built(&self) -> impl Iterator<Item = (usize, &PlannedSpace)> {
        self.spaces
            .iter()
            .enumerate()
            .filter(|(_, s)| s.role.is_built())
    }

    /// Área construida, en metros cuadrados. **La métrica que sustituye al «porcentaje ocupado»**:
    /// lo que importa no es llenar, es que lo que hay tenga masa.
    pub fn built_area_m2(&self) -> f32 {
        self.built().map(|(_, s)| s.rect.area_m2()).sum()
    }

    /// Componentes conexas sobre los espacios construidos. Uno es el objetivo.
    pub fn components(&self) -> usize {
        let n = self.spaces.len();
        let mut uf = UnionFind::new(n);
        for l in &self.links {
            uf.union(l.a, l.b);
        }
        let mut roots: Vec<usize> = self.built().map(|(i, _)| uf.find(i)).collect();
        roots.sort_unstable();
        roots.dedup();
        roots.len()
    }

    /// Los enlaces de cada espacio. Sirve para contar callejones y para el relleno.
    pub fn degree(&self) -> Vec<usize> {
        let mut out = vec![0usize; self.spaces.len()];
        for l in &self.links {
            out[l.a] += 1;
            out[l.b] += 1;
        }
        out
    }

    /// Lo que este módulo necesita que sea cierto antes de que nadie construya nada. Vacío = sano.
    ///
    /// No comprueba que el plan sea BONITO —eso lo miden las sondas— sino que sea COHERENTE: que no
    /// haya rectángulos degenerados, que los espacios no se pisen, y que ningún enlace apunte fuera.
    /// Un plan incoherente construye un edificio incoherente sin dar un solo error.
    pub fn problems(&self) -> Vec<String> {
        let mut out = Vec::new();
        for (i, s) in self.spaces.iter().enumerate() {
            if s.rect.width_cm() <= 0 || s.rect.depth_cm() <= 0 {
                out.push(format!(
                    "espacio {i}: huella no positiva {}×{} cm",
                    s.rect.width_cm(),
                    s.rect.depth_cm()
                ));
            }
        }
        // Solape. La subdivisión no puede producirlo por construcción, así que si aparece es que
        // alguien ha tocado el tallado de bandas — y el síntoma sería geometría cruzada, que el
        // ráster estampa maciza sin quejarse.
        for i in 0..self.spaces.len() {
            for j in (i + 1)..self.spaces.len() {
                let (a, b) = (&self.spaces[i].rect, &self.spaces[j].rect);
                if a.min_x_cm < b.max_x_cm
                    && a.max_x_cm > b.min_x_cm
                    && a.min_z_cm < b.max_z_cm
                    && a.max_z_cm > b.min_z_cm
                {
                    out.push(format!("espacios {i} y {j} se solapan"));
                }
            }
        }
        for (i, l) in self.links.iter().enumerate() {
            if l.a >= self.spaces.len() || l.b >= self.spaces.len() {
                out.push(format!("enlace {i}: índice fuera de rango"));
                continue;
            }
            if l.a == l.b {
                out.push(format!("enlace {i}: un espacio consigo mismo"));
            }
            if l.width_cm < DOORWAY_CM {
                out.push(format!(
                    "enlace {i}: {} cm por debajo del vano mínimo de {DOORWAY_CM} cm",
                    l.width_cm
                ));
            }
            if !self.spaces[l.a].role.is_built() || !self.spaces[l.b].role.is_built() {
                out.push(format!(
                    "enlace {i}: toca un espacio VACÍO, que no se construye"
                ));
            }

            // **ADR-100 enmienda 2 — NINGÚN VANO PUEDE SER UN ESCALÓN QUE NO SE SUBE.**
            //
            // Es el modo de fallo de la verticalidad, y no se ve en una captura: el cliente dibuja la
            // puerta abierta y el servidor no deja entrar. Una escalera queda fuera de la cuenta a
            // propósito: su cota es la de su cara de ENTRADA y la de salida es `+ rise_cm`, así que
            // sus dos extremos son legales por construcción.
            let (a, b) = (&self.spaces[l.a], &self.spaces[l.b]);
            if a.role != SpaceRole::Stair && b.role != SpaceRole::Stair {
                let step = (a.floor_y_cm - b.floor_y_cm).abs();
                if step > MAX_WALK_STEP_CM {
                    out.push(format!(
                        "enlace {i}: escalón de {step} cm entre los espacios {} y {} — el jugador \
                         sube {MAX_WALK_STEP_CM}, así que la puerta se dibuja abierta y no se pasa",
                        l.a, l.b
                    ));
                }
            }
        }
        out
    }
}

/// Un vecino de un espacio, con la pared que comparten: `(otro, solape, x del paso, z del paso)`.
type Touching = (usize, i32, i32, i32);

/// Las coordenadas que un corte NO puede pisar, sacadas de las puertas de junta.
///
/// Una puerta en un borde HORIZONTAL (lados N/S) corre a lo largo de X, así que la parte un corte en
/// X; una en un borde vertical (E/O) la parte un corte en Z. `along_x` pide las primeras.
fn gate_coordinates(gates: &[Wg3Gate], along_x: bool) -> Vec<i32> {
    let mut out: Vec<i32> = gates
        .iter()
        .filter(|g| g.outward_side.is_multiple_of(2) == along_x)
        .map(|g| {
            let v = if along_x { g.x } else { g.z };
            (v * CM_PER_M).round() as i32
        })
        .collect();
    out.sort_unstable();
    out.dedup();
    out
}

/// Nodo del árbol de subdivisión. Vive sólo mientras se planifica.
struct Node {
    rect: PlanRect,
    depth: u8,
    children: Option<(usize, usize)>,
}

/// **EL PLAN DE UNA REGIÓN.** Función pura: misma semilla, misma caja y mismas puertas ⇒ mismo
/// edificio (R3).
///
/// `gates` son las puertas de junta que ya acordó [`super::junction`] con la región vecina. Entran
/// como restricción y no como sugerencia: son el único punto del plan que NO se decide aquí, porque
/// ya está acordado con alguien que no puede consultarse.
pub fn plan_region(seed: i32, bounds: (f32, f32, f32, f32), gates: &[Wg3Gate]) -> RegionPlan {
    // Una planta suelta no tiene edificio encima que le pida atrios.
    plan_storey(seed, bounds, gates, 0, true, &[])
}

/// **UNA PLANTA.** Lo mismo que [`plan_region`], pero a la cota que se le diga.
///
/// Existe por ADR-102 D1: una planta es un [`RegionPlan`] entero y no un campo dentro de uno. La
/// razón es que este módulo es 2D hasta el fondo —[`RegionPlan::problems`] declara un fallo cualquier
/// par de rectángulos que se pisen en XZ, y `rects_share_wall` decide vecindad sin mirar la cota—, así
/// que meter la planta como campo obligaría a dar Y a todas esas comprobaciones para una propiedad
/// que sólo importa ENTRE plantas y nunca dentro de una. Aquí dentro no cambia nada: se planifica en
/// el propio cero y la cota entra al crear cada espacio.
///
/// `may_sink` apaga los espacios hundidos de ADR-100 enmienda 2, y **una planta que tiene otra debajo
/// tiene que apagarlos**: una terraza baja 60 cm desde su cota, y desde 332 eso son 272 — por debajo
/// del techo de la planta de abajo, que está en 320. Los peldaños atraviesan el forjado y quedan
/// colgando dentro de las salas de abajo. Se vio como caras peleando por el mismo plano a cota 3,14 m,
/// que es donde no tenía por qué haber nada de la planta alta.
pub fn plan_storey(
    seed: i32,
    bounds: (f32, f32, f32, f32),
    gates: &[Wg3Gate],
    base_y_cm: i32,
    may_sink: bool,
    atria_below: &[PlanRect],
) -> RegionPlan {
    let root = PlanRect {
        min_x_cm: (bounds.0 * CM_PER_M).round() as i32,
        min_z_cm: (bounds.1 * CM_PER_M).round() as i32,
        max_x_cm: (bounds.2 * CM_PER_M).round() as i32,
        max_z_cm: (bounds.3 * CM_PER_M).round() as i32,
    };

    let mut planner = Planner {
        seed,
        base_y_cm,
        nodes: vec![Node {
            rect: root,
            depth: 0,
            children: None,
        }],
        spaces: Vec::new(),
        band_of_node: Vec::new(),
        links: Vec::new(),
        bounds: root,
        // **LAS PUERTAS ENTRAN ANTES DE CORTAR, y esto costó una de cada cuatro.** Ver
        // [`GATE_CLEARANCE_CM`].
        gate_cuts_x: gate_coordinates(gates, true),
        gate_cuts_z: gate_coordinates(gates, false),
    };
    planner.band_of_node.push(None);

    planner.subdivide();
    planner.emit_leaves();
    planner.assign_void();
    // ADR-104 D2 — y JUSTO AQUÍ, entre el vacío sorteado y el grafo. Antes no hay papeles que mirar;
    // después habría que deshacer enlaces ya tejidos, que es cómo se fabrican espacios sellados.
    planner.carve_atria(atria_below);
    // Las puertas ANTES de enlazar: una puerta acordada con la vecina no se negocia, así que el
    // espacio que la abre no puede ser vacío y tiene que estar dentro del edificio. Decidirlo después
    // obligaría a rehacer el grafo.
    let gates = planner.attach_gates(gates);
    planner.link_all();
    planner.ensure_connected();
    planner.retag_dead_ends();
    if may_sink {
        planner.sink_dead_ends(&gates);
    }

    RegionPlan {
        spaces: planner.spaces,
        links: planner.links,
        gates,
        bounds_cm: Some(root),
    }
}

/// El hueco por el que se sube de una planta a la siguiente (ADR-102 D1 y D4).
///
/// No es un espacio nuevo: es un espacio de la planta de abajo al que se le cambia el papel por
/// `Stair` y se le pone a subir una planta entera. Lo que este registro guarda es la RELACIÓN, que es
/// justamente lo que ningún [`RegionPlan`] puede guardar por ser 2D.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StairWell {
    /// Huella del hueco: el rectángulo del espacio de abajo que se ha vuelto escalera. Es también el
    /// trozo de forjado que hay que perforar, que es para lo que lo necesita el relleno.
    pub rect: PlanRect,
    /// Planta de la que se sube. La de llegada es siempre `storey_below + 1`.
    pub storey_below: usize,
    /// El espacio de la planta de abajo, ya con papel `Stair`.
    pub space_below: usize,
    /// El espacio de la planta de arriba al que se sale. Tiene que estar construido: salir a un
    /// vacío intencionado es una caída, no una escalera.
    pub space_above: usize,
}

/// **EL EDIFICIO DE UNA REGIÓN, CON SUS PLANTAS.**
///
/// Una planta por [`RegionPlan`], y los huecos de escalera aparte. Lo vertical vive aquí y sólo aquí:
/// ninguna planta sabe que hay otra, y por eso ninguna comprobación 2D de [`RegionPlan`] tiene que
/// aprender una tercera coordenada.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct RegionBuilding {
    pub storeys: Vec<RegionPlan>,
    pub wells: Vec<StairWell>,
}

impl RegionBuilding {
    /// Lo que tiene que ser cierto ENTRE plantas, más lo de cada una por su cuenta.
    ///
    /// La parte de cada planta la sigue midiendo [`RegionPlan::problems`], que no cambia. Lo que se
    /// añade aquí es exactamente lo que aquélla no puede ver por construcción — y hay una que importa
    /// más que las otras: **una planta a la que no sube ninguna escalera es decorado.** Se dibuja, se
    /// ilumina, cuesta geometría y no se pisa; y como todo lo demás sale bien, no hay contador que se
    /// queje.
    pub fn problems(&self) -> Vec<String> {
        let mut out = Vec::new();
        for (n, plan) in self.storeys.iter().enumerate() {
            for p in plan.problems() {
                out.push(format!("planta {n}: {p}"));
            }
            // La cota es de la planta entera y no de cada espacio: si dos plantas comparten cota, lo
            // que hay no son dos plantas sino dos edificios cruzados en el mismo aire.
            let want = n as i32 * STOREY_HEIGHT_CM;
            if let Some(s) = plan.spaces.iter().find(|s| s.floor_y_cm != want) {
                out.push(format!(
                    "planta {n}: un espacio a cota {} cuando la planta está a {want}",
                    s.floor_y_cm
                ));
            }
        }

        for (i, w) in self.wells.iter().enumerate() {
            let (Some(below), Some(above)) = (
                self.storeys.get(w.storey_below),
                self.storeys.get(w.storey_below + 1),
            ) else {
                out.push(format!("hueco {i}: apunta a una planta que no existe"));
                continue;
            };
            let (Some(sb), Some(sa)) = (
                below.spaces.get(w.space_below),
                above.spaces.get(w.space_above),
            ) else {
                out.push(format!("hueco {i}: apunta a un espacio que no existe"));
                continue;
            };
            if sb.role != SpaceRole::Stair {
                out.push(format!(
                    "hueco {i}: el espacio de abajo es {} y no una escalera",
                    sb.role.name()
                ));
            }
            if sb.rise_cm != STOREY_HEIGHT_CM {
                out.push(format!(
                    "hueco {i}: sube {} cm y una planta mide {STOREY_HEIGHT_CM}",
                    sb.rise_cm
                ));
            }
            if !sa.role.is_built() {
                out.push(format!("hueco {i}: se sale a un VACÍO, que es una caída"));
            }
            // Se sale DENTRO del espacio de arriba, no al lado. Si el hueco asoma fuera, lo que se
            // perfora es forjado de nadie y el último peldaño da a la nada.
            if !sa.rect.contains_rect(&w.rect) {
                out.push(format!(
                    "hueco {i}: la huella se sale del espacio de arriba al que dice llegar"
                ));
            }
        }

        // Y el remate: toda planta por encima de la baja necesita al menos un hueco que llegue a ella.
        for n in 1..self.storeys.len() {
            if !self.wells.iter().any(|w| w.storey_below + 1 == n) {
                out.push(format!(
                    "planta {n}: no sube ninguna escalera — es decorado"
                ));
            }
        }
        out
    }
}

/// **EL EDIFICIO.** Planta baja completa, y encima las que quepan (ADR-102 D1, D3 y D4).
///
/// Las plantas se planifican de ABAJO ARRIBA y en una sola dirección, porque la de arriba necesita
/// saber de la de abajo —dónde cae el hueco de escalera— y la de abajo no necesita saber nada de la
/// de arriba. Al revés obligaría a rehacer una de las dos.
pub fn plan_building(
    seed: i32,
    bounds: (f32, f32, f32, f32),
    gates: &[Wg3Gate],
    storeys: usize,
) -> RegionBuilding {
    // La planta baja SÍ se hunde: debajo de ella no hay nada que perforar.
    // La planta baja no tiene nada debajo, así que no hay atrios que abrirle a nadie.
    let mut out = vec![plan_storey(seed, bounds, gates, 0, true, &[])];
    let mut wells = Vec::new();

    for n in 1..storeys.max(1) {
        // La huella se estrecha (D3): si cada planta llenara la región, esto no sería un edificio
        // sino losas de 150 × 150 apiladas, sin una silueta que mirar.
        let Some(up) = upper_bounds(&out[n - 1], seed) else {
            break;
        };
        // **SIN PUERTAS DE JUNTA ARRIBA, y es una decisión.** El contrato de junta de ADR-096 se
        // acuerda entre regiones vecinas en 2D y a ras de suelo; una puerta de junta en la primera
        // planta exigiría que la región de al lado tuviera planta ahí y a la misma cota, que es un
        // acuerdo que hoy nadie negocia. Cruzar de región se hace por abajo.
        // Sin hundir: encima de la planta baja, el suelo de una terraza sería el techo de abajo.
        // ADR-104 D2 — **la única consulta que la planta de arriba le hace a la de abajo, y va en un
        // solo sentido.** Es lo que rompe la premisa de ADR-102 («ninguna planta sabe que hay otra»),
        // así que se acota a esto: las huellas de sus naves, nada más. Quien coordina sigue siendo el
        // EDIFICIO, igual que con `dig_wells` y `cap_headroom_under`; `RegionPlan` sigue sin aprender
        // una tercera coordenada.
        let atria: Vec<PlanRect> = out[n - 1]
            .spaces
            .iter()
            .filter(|s| s.role == SpaceRole::Hall)
            .map(|s| s.rect)
            .collect();
        let plan = plan_storey(
            storey_seed(seed, n),
            up,
            &[],
            n as i32 * STOREY_HEIGHT_CM,
            false,
            &atria,
        );
        // **EL EDIFICIO SUBE SÓLO HASTA DONDE SE PUEDE SUBIR.** El hueco pide que COINCIDAN dos
        // geometrías planificadas por separado —un espacio de abajo que quepa entero dentro de uno
        // construido de arriba, y con tiro para catorce peldaños—, y eso es coincidencia de semilla:
        // en el barrido de 49 regiones hay unas cuantas donde no la hay. Si no se puede subir, la
        // planta no se levanta. Una planta a la que no se llega no es media victoria: es geometría
        // que se dibuja, se ilumina, cuesta y no se pisa, y como todo lo demás sale bien, ningún
        // contador se queja.
        //
        // El hueco se abre en la planta de ABAJO, así que hay que tenerla a mano y mutable.
        let dug = dig_wells(&mut out[n - 1], &plan, n - 1, seed);
        if dug.is_empty() {
            break;
        }
        cap_headroom_under(&mut out[n - 1], &plan);
        wells.extend(dug);
        out.push(plan);
    }

    RegionBuilding {
        storeys: out,
        wells,
    }
}

/// Semilla propia de una planta alta. Sin esto todas las plantas salen calcadas, y un edificio cuyas
/// plantas son fotocopias no se lee como un edificio: se lee como un fallo.
fn storey_seed(seed: i32, storey: usize) -> i32 {
    seed ^ (SALT_STOREY.wrapping_mul(storey as u32 + 1) as i32)
}

/// La huella de la planta de encima, tomada del corte PRINCIPAL de la de abajo (ADR-102 D3).
///
/// Del corte principal y no de un margen sorteado: la banda de profundidad 0 —el `Spine`— es la
/// única línea que ya organiza toda la planta, así que alinear el borde de arriba con ella hace que
/// lo alto se apoye en la estructura de abajo en vez de cortarla por donde caiga. Se queda un lado
/// del espinazo, más el espinazo entero, para que la planta alta herede una circulación.
///
/// `None` cuando lo que quedaría es demasiado pequeño para ser una planta.
fn upper_bounds(below: &RegionPlan, seed: i32) -> Option<(f32, f32, f32, f32)> {
    let region = below.bounds_cm?;
    let spine = below
        .spaces
        .iter()
        .find(|s| s.role == SpaceRole::Spine && s.depth == 0)?
        .rect;

    // Un espinazo estrecho en X corre a lo largo de Z, y entonces reparte en X.
    let along_z = spine.width_cm() < spine.depth_cm();
    let (cx, cz) = region.centre_m();
    let keep_high = hash::stream_at(seed, cx, cz, SALT_STOREY).next01() < 0.5;

    let kept = if along_z {
        if keep_high {
            PlanRect {
                min_x_cm: spine.min_x_cm,
                ..region
            }
        } else {
            PlanRect {
                max_x_cm: spine.max_x_cm,
                ..region
            }
        }
    } else if keep_high {
        PlanRect {
            min_z_cm: spine.min_z_cm,
            ..region
        }
    } else {
        PlanRect {
            max_z_cm: spine.max_z_cm,
            ..region
        }
    };

    // Que quepa un edificio, no un pasillo: dos veces el lado mínimo por banda.
    if kept.width_cm() < MIN_SIDE_CM * 2 || kept.depth_cm() < MIN_SIDE_CM * 2 {
        return None;
    }
    Some((
        kept.min_x_cm as f32 / CM_PER_M,
        kept.min_z_cm as f32 / CM_PER_M,
        kept.max_x_cm as f32 / CM_PER_M,
        kept.max_z_cm as f32 / CM_PER_M,
    ))
}

/// Convierte espacios de la planta de abajo en huecos de escalera. Vacío = no se puede subir.
///
/// Cada candidato tiene que cumplir cinco cosas a la vez, y las cinco por una razón distinta:
/// **caber** la escalera entera con huellas andables, **no ser circulación** —comerse el espinazo
/// para meter una escalera parte la planta en dos—, **tener puerta** por la que entrar, **caer dentro
/// de un espacio construido y PLANO de la planta de arriba**, porque el último peldaño tiene que dar a
/// un sitio y no al aire, y **estar lejos de las otras**.
///
/// **Varias y no una, y esto salió de jugarlo.** Con una sola por región, un hueco de 3,6 × 9 m en
/// 150 × 150 es el 0,14 % de la superficie y no hay nada que lo anuncie: la sonda decía que el 100 %
/// de la planta alta era ALCANZABLE y era verdad, pero alcanzable no es encontrable. La primera
/// partida con dos plantas se resumió en «no sé dónde ir a subir».
fn dig_wells(
    below: &mut RegionPlan,
    above: &RegionPlan,
    storey_below: usize,
    seed: i32,
) -> Vec<StairWell> {
    let steps = storey_steps();
    let run_cm = steps * STAIR_TREAD_CM;

    // Todos los huecos de cada espacio, no sólo el primero. Hacen falta los dos usos: el PRIMERO dice
    // por dónde se entra, y TODOS dicen si el recorte se lleva alguno por delante.
    let mut doors: Vec<Vec<(i32, i32)>> = vec![Vec::new(); below.spaces.len()];
    for l in &below.links {
        doors[l.a].push((l.at_x_cm, l.at_z_cm));
        doors[l.b].push((l.at_x_cm, l.at_z_cm));
    }
    for g in &below.gates {
        doors[g.space].push((g.x_cm, g.z_cm));
    }

    let mut candidates: Vec<(u64, usize, usize, u8)> = Vec::new();
    for (i, s) in below.spaces.iter().enumerate() {
        if !s.role.is_built() || s.role.is_circulation() || s.rise_cm != 0 {
            continue;
        }
        // **POR CADA PUERTA, no sólo por la primera.** El lado por el que se entra fija el eje del
        // tiro —los peldaños se alejan de la puerta—, así que una sala de 20 × 6 m con una puerta en
        // la pared larga da un tiro de 6 m y se descarta, cuando la misma sala con otra puerta daría
        // 20. Mirando sólo la primera, el catálogo de candidatas se quedaba en una o dos por región y
        // no había con qué repartir.
        for side in doors[i]
            .iter()
            .filter_map(|&(dx, dz)| side_of_point_in(&s.rect, dx, dz))
            .collect::<Vec<_>>()
        {
            // Los peldaños se alejan de la puerta, así que el tiro corre perpendicular a su pared.
            let (run, across) = if side.is_multiple_of(2) {
                (s.rect.depth_cm(), s.rect.width_cm())
            } else {
                (s.rect.width_cm(), s.rect.depth_cm())
            };
            // **TIENE QUE SOBRAR ESPACIO, y no es un lujo.** El hueco se recorta contra la pared de
            // ENFRENTE de la puerta, así que lo que queda entre la puerta y el pie de la escalera es lo
            // que sigue siendo sala — y es también lo que sostiene todos los enlaces que ya tenía este
            // espacio. Sin sobrante no hay dónde repartirlos.
            // Lo que sobra tiene que dar para un vestíbulo con su puerta, no para una sala entera:
            // exigirle [`MIN_SIDE_CM`] descartaba la mitad de los sitios donde cabe una escalera.
            if run < run_cm + GOOD_WALL_CM || across < STAIR_WIDTH_CM {
                continue;
            }
            // Y ningún hueco puede caer DENTRO de la franja que se va a volver escalera: ahí la cota sube
            // hasta 332, y una puerta a media escalera es un vano que se dibuja y no se pasa.
            if doors[i]
                .iter()
                .any(|&(x, z)| band_of(&s.rect, side, run_cm).contains_point(x, z))
            {
                continue;
            }
            // El sitio al que se sale, y contra la franja RECORTADA: lo que tiene que caber dentro de un
            // espacio de arriba es el hueco, no la sala entera de la que se recorta.
            let (stair, _) = stair_and_flank(&s.rect, side, run_cm, seed);
            // Y el sitio al que se sale tiene que ser PLANO. Un espacio hundido de la planta de arriba
            // baja sus peldaños de 12 en 12 justo dentro del pozo: la escalera sube sus trece perfectos y
            // los ocho de arriba se quedan con menos de un metro de techo, colgando de la terraza de
            // encima. Salió en `(1,0)` como una escalera que se andaba hasta la mitad.
            let Some(j) = above.spaces.iter().position(|t| {
                t.role.is_built()
                    && t.rise_cm == 0
                    && t.role != SpaceRole::Stair
                    && t.rect.shrunk(STAIR_MARGIN_CM).contains_rect(&stair)
            }) else {
                continue;
            };
            // Sorteo por posición, que es lo que hace que el orden no dependa del orden del vector.
            let (cx, cz) = s.rect.centre_m();
            candidates.push((hash::at_position(seed, cx, cz, SALT_WELL), i, j, side));
            // Una orientación por espacio: la sala se parte una sola vez.
            break;
        }
    }

    if std::env::var("WG3_WELL_DEBUG").is_ok() {
        eprintln!(
            "[well] {} espacios, {} candidatas",
            below.spaces.len(),
            candidates.len()
        );
    }
    // **REPARTIDAS, no las primeras que salgan.** Una escalera sirve si se tropieza con ella, y con
    // todas juntas en una esquina la mitad de la región sigue sin salida aunque el contador diga seis.
    // Se toman por orden de sorteo y se descarta la que caiga cerca de una ya tomada.
    candidates.sort_unstable_by_key(|&(k, i, ..)| (k, i));
    let mut taken: Vec<(usize, usize, u8)> = Vec::new();
    for &(_, i, j, side) in &candidates {
        if taken.len() >= WELLS_PER_REGION {
            break;
        }
        let (cx, cz) = below.spaces[i].rect.centre_m();
        let far = taken.iter().all(|&(ti, ..)| {
            let (tx, tz) = below.spaces[ti].rect.centre_m();
            let (dx, dz) = ((cx - tx) * CM_PER_M, (cz - tz) * CM_PER_M);
            dx * dx + dz * dz >= (WELL_SPACING_CM * WELL_SPACING_CM) as f32
        });
        if far {
            taken.push((i, j, side));
        }
    }

    // El corte se hace DESPUÉS de elegirlas todas, y no sobre la marcha: partir un espacio le cambia
    // el rectángulo, y un candidato medido antes del corte dejaría de ser el que se midió. Cada
    // espacio se parte una sola vez, así que los índices de los demás siguen valiendo.
    taken
        .into_iter()
        .map(|(i, j, side)| {
            let space_below = split_for_stair(below, i, side, run_cm, seed);
            StairWell {
                rect: below.spaces[space_below].rect,
                storey_below,
                space_below,
                space_above: j,
            }
        })
        .collect()
}

/// Le pone techo a los espacios de `below` que tengan algo CONSTRUIDO encima (ADR-102 D2).
///
/// El tope sale de la aritmética de la planta y no de un gusto: el suelo de arriba está a
/// [`STOREY_HEIGHT_CM`] y su losa cuelga [`SLAB_THICKNESS_CM`] por debajo, así que lo que queda libre
/// son los 320 de rigor. Sin esto, una nave pide 450 y su losa de techo aterriza en `[450, 462]`,
/// metro y pico dentro de las salas de arriba.
///
/// Y sólo si lo de encima está CONSTRUIDO. Donde arriba hay vacío intencionado —o no hay planta— no
/// hay tope, y la nave se queda con sus 4,50 m: eso es una sala a doble altura, y existe porque el
/// plan puso vacío ahí, no porque se le olvidara a nadie.
fn cap_headroom_under(below: &mut RegionPlan, above: &RegionPlan) {
    // **DOS LOSAS, NO UNA, y contarlas mal produjo el peor artefacto visual del sistema.**
    //
    // Entre dos plantas hay el TECHO de la de abajo y el SUELO de la de arriba: `segment_boxes` emite
    // las dos, el techo en `[floor + h, floor + h + SLAB]` y el suelo colgando en
    // `[floor - SLAB, floor]`. Con el tope a `STOREY_HEIGHT_CM - SLAB` las dos caían en `[320, 332]`:
    // **la misma caja dibujada dos veces**, en toda la huella donde una planta se apoya en la otra.
    // Dos caras coplanares y visibles es z-fighting garantizado, y sale por metros cuadrados.
    //
    // Restando las dos, el techo de abajo queda en `[308, 320]` y el suelo de arriba en `[320, 332]`:
    // espalda contra espalda, con la junta dentro del macizo y ninguna cara repetida.
    let cap = STOREY_HEIGHT_CM - 2 * SLAB_THICKNESS_CM;
    for s in &mut below.spaces {
        if above
            .spaces
            .iter()
            .any(|t| t.role.is_built() && t.rect.overlaps(&s.rect))
        {
            s.max_clear_cm = cap;
        } else {
            // ADR-104 D1 — el otro lado del mismo `if`, y hasta hoy no lo miraba nadie: hay planta
            // encima y sobre este espacio no hay nada construido. Eso es un atrio en potencia, y es
            // la ÚNICA vez que se puede saber, porque aquí es donde las dos plantas se ven a la vez.
            s.void_above = true;
        }
    }
}

/// TIRAS de una escalera que sube una planta entera — no contrahuellas: son DOS más.
///
/// Una más porque la primera tira está a la cota de entrada y no sube nada. Y otra más porque **el
/// rellano de arriba tiene que medir dos celdas del ráster**: el vano que abre el forjado es
/// conservador igual que todo lo demás, así que se come la losa de cualquier celda que TOQUE, y con
/// un rellano de una sola tira la celda que comparte con el peldaño de abajo se queda sin suelo. Con
/// dos tiras siempre sobra una celda entera de rellano, que es donde se pisa al salir.
/// Las contrahuellas salen de dividir HACIA ABAJO, igual que en `fill::emit_stair`, y las dos cuentas
/// tienen que dar lo mismo o el tiro se dimensiona para una escalera que no es la que se construye.
/// [`STOREY_RISE_CM`] es un objetivo; el tope de verdad es [`MAX_WALK_STEP_CM`], y 332 / 13 = 25,5 lo
/// cumple de sobra.
pub fn storey_steps() -> i32 {
    STOREY_HEIGHT_CM.div_euclid(STOREY_RISE_CM).max(1) + 2
}

/// Cuánto tiene que separarse un hueco de escalera de las paredes de la sala de arriba, en cm.
///
/// **Pegado a la pared, el rellano no existe.** El muro perimetral de la planta alta llena su celda
/// del ráster de arriba abajo, así que un rellano contra él queda dentro de una columna maciza: la
/// escalera sube trece peldaños perfectos y el último no tiene dónde dejarte. Salió en `(1,0)`, con el
/// hueco naciendo justo en `x = 150,00` — la costura entre regiones.
///
/// Grosor de pared más una celda del ráster, y ni un centímetro más: con 100 cm la segunda planta se
/// caía de 49 regiones a 23, porque lo que este número recorta no es la escalera sino la lista de
/// sitios donde puede haberla.
const STAIR_MARGIN_CM: i32 = 70;

/// Huecos de escalera por región, como mucho.
///
/// **Un tope y no un objetivo**: se ponen los que haya sitio para poner. El número sale de la
/// geometría, no del gusto — una región mide 150 m, así que con seis repartidas la distancia típica a
/// la más cercana baja al orden de los 30 m, que es lo que se recorre sin preguntarse dónde está la
/// salida. Con una sola, que es como salió la primera versión, hay 150 m de región y un hueco de 3,6 ×
/// 9: el 0,14 % de la superficie.
const WELLS_PER_REGION: usize = 6;

/// Distancia mínima entre dos huecos de escalera de la misma región, en centímetros.
///
/// Sin ella, «seis escaleras» puede querer decir seis en la misma esquina: el sorteo no sabe de
/// reparto, y las salas grandes que cumplen los requisitos tienden a estar juntas.
///
/// 25 m y no 35: con 35 se descartaban candidatas de las poquísimas que hay. Medido, el catálogo de
/// sitios donde cabe una escalera recta es de 2 a 5 por región sobre ~170 espacios, así que el reparto
/// no puede permitirse tirar ninguna. El número correcto lo dirá el día que haya más candidatas.
const WELL_SPACING_CM: i32 = 2500;

/// La franja de `rect` que se vuelve escalera: contra la pared de ENFRENTE de la puerta, `run_cm` de
/// fondo.
///
/// De enfrente y no de al lado de la puerta, y ésta es LA decisión geométrica de todo el hueco. Contra
/// la puerta, se entra y se sube inmediatamente, y lo que quedara detrás de la escalera se queda sin
/// acceso; contra la de enfrente, se entra a la sala, se cruza y se sube — y la sala sigue siendo una
/// sala con su puerta.
fn band_of(rect: &PlanRect, side: u8, run_cm: i32) -> PlanRect {
    match side % 4 {
        0 => PlanRect {
            max_z_cm: rect.min_z_cm + run_cm,
            ..*rect
        },
        1 => PlanRect {
            max_x_cm: rect.min_x_cm + run_cm,
            ..*rect
        },
        2 => PlanRect {
            min_z_cm: rect.max_z_cm - run_cm,
            ..*rect
        },
        _ => PlanRect {
            min_x_cm: rect.max_x_cm - run_cm,
            ..*rect
        },
    }
}

/// El hueco dentro de su franja y lo que sobra al lado: `(escalera, costado)`.
///
/// Arrimado a un extremo y no centrado para que lo que sobra sea UN rectángulo y no dos. Dos sobrantes
/// son dos espacios más que conectar, y uno acaba siendo una tira que no admite puerta.
///
/// **Una sola función, y llamada desde los dos sitios a propósito.** El primer intento calculaba el
/// recorte al ELEGIR el candidato y otra vez al partirlo, y no siempre daban lo mismo: cuando el
/// costado no llegaba al mínimo, el que partía se quedaba con la franja entera mientras el que había
/// elegido creía que el hueco era estrecho. Resultado: `(-3,-3)` con una escalera que asomaba fuera
/// del espacio de arriba al que decía llegar. Dos cálculos del mismo número son un número de más.
fn stair_and_flank(
    rect: &PlanRect,
    side: u8,
    run_cm: i32,
    seed: i32,
) -> (PlanRect, Option<PlanRect>) {
    let band = band_of(rect, side, run_cm);
    let across_x = side.is_multiple_of(2);
    let (cx, cz) = rect.centre_m();
    let low = hash::stream_at(seed, cx, cz, SALT_WELL).next01() < 0.5;

    let (stair, flank) = match (across_x, low) {
        (true, true) => (
            PlanRect {
                max_x_cm: band.min_x_cm + STAIR_WIDTH_CM,
                ..band
            },
            PlanRect {
                min_x_cm: band.min_x_cm + STAIR_WIDTH_CM,
                ..band
            },
        ),
        (true, false) => (
            PlanRect {
                min_x_cm: band.max_x_cm - STAIR_WIDTH_CM,
                ..band
            },
            PlanRect {
                max_x_cm: band.max_x_cm - STAIR_WIDTH_CM,
                ..band
            },
        ),
        (false, true) => (
            PlanRect {
                max_z_cm: band.min_z_cm + STAIR_WIDTH_CM,
                ..band
            },
            PlanRect {
                min_z_cm: band.min_z_cm + STAIR_WIDTH_CM,
                ..band
            },
        ),
        (false, false) => (
            PlanRect {
                min_z_cm: band.max_z_cm - STAIR_WIDTH_CM,
                ..band
            },
            PlanRect {
                max_z_cm: band.max_z_cm - STAIR_WIDTH_CM,
                ..band
            },
        ),
    };

    // ¿Da el costado para una sala? Lo único que se le exige es la PARED QUE COMPARTE CON LA SALA:
    // por ahí entra, y es su única puerta posible —contra la escalera no puede abrir, porque ahí
    // suben los peldaños—. Su fondo no se mira: es el tiro de la escalera, 4,20 m, y siempre vale.
    //
    // Pedirle [`MIN_SIDE_CM`] a los dos lados, como pedía el primer intento, lo descartaba SIEMPRE:
    // 420 nunca llega a 500. El síntoma no era un fallo sino un silencio — las cuatro regiones de
    // referencia daban escaleras de 4,2 × 8,9 m, con el recorte del tiro puesto y el del ancho no.
    let shared = if across_x {
        flank.width_cm()
    } else {
        flank.depth_cm()
    };
    // Sin costado utilizable, la franja entera es escalera: un rectángulo suelto que no cabe en el
    // reparto es un solape, y un solape es geometría cruzada que el ráster estampa maciza.
    if shared >= GOOD_WALL_CM {
        (stair, Some(flank))
    } else {
        (band, None)
    }
}

/// Parte el espacio `i` en sala, escalera y —si sobra sitio— un costado. Devuelve el índice de la
/// escalera.
///
/// **El espacio `i` se queda de SALA y la escalera es nueva**, no al revés. Así todos los enlaces que
/// ya apuntaban a `i` siguen siendo correctos sin tocar uno solo: se comprobó antes de llegar aquí que
/// ninguno cae dentro de la franja, así que todos están en pared de la sala. Hacerlo al revés
/// obligaría a repuntar el grafo entero y a acertar con cada uno.
fn split_for_stair(plan: &mut RegionPlan, i: usize, side: u8, run_cm: i32, seed: i32) -> usize {
    let original = plan.spaces[i];
    let band = band_of(&original.rect, side, run_cm);
    let (stair, flank) = stair_and_flank(&original.rect, side, run_cm, seed);

    // La sala se queda con lo que no es franja.
    plan.spaces[i].rect = match side % 4 {
        0 => PlanRect {
            min_z_cm: band.max_z_cm,
            ..original.rect
        },
        1 => PlanRect {
            min_x_cm: band.max_x_cm,
            ..original.rect
        },
        2 => PlanRect {
            max_z_cm: band.min_z_cm,
            ..original.rect
        },
        _ => PlanRect {
            max_x_cm: band.min_x_cm,
            ..original.rect
        },
    };

    let s = plan.spaces.len();
    plan.spaces.push(PlannedSpace {
        rect: stair,
        role: SpaceRole::Stair,
        rise_cm: STOREY_HEIGHT_CM,
        rise_step_cm: STOREY_RISE_CM,
        // Se entra por el mismo lado por el que se entraba a la sala: se viene de ahí.
        rise_from_side: side,
        ..original
    });
    link_along(plan, i, s);

    // El costado que sobra al lado de la escalera, si lo hay. Su puerta va contra la SALA y no contra
    // la escalera: pegado a la escalera compartiría pared con los peldaños, y una puerta a media
    // escalera es un vano que se dibuja y no se pasa.
    if let Some(f) = flank {
        let k = plan.spaces.len();
        plan.spaces.push(PlannedSpace {
            rect: f,
            ..original
        });
        link_along(plan, i, k);
    }
    s
}

/// Une la sala con lo que le acaba de nacer al otro lado del corte de la franja.
fn link_along(plan: &mut RegionPlan, a: usize, b: usize) {
    let (ra, rb) = (plan.spaces[a].rect, plan.spaces[b].rect);
    let Some((overlap, x, z)) = rects_share_wall(ra, rb) else {
        return;
    };
    plan.links.push(PlannedLink {
        a,
        b,
        width_cm: overlap.clamp(DOORWAY_CM, WIDE_DOORWAY_CM),
        kind: LinkKind::Doorway,
        at_x_cm: x,
        at_z_cm: z,
    });
}

/// Por qué lado de un rectángulo cae un punto de su borde. `None` si no cae en ninguno.
fn side_of_point_in(r: &PlanRect, x_cm: i32, z_cm: i32) -> Option<u8> {
    const EPS: i32 = 2;
    if (r.max_z_cm - z_cm).abs() <= EPS {
        return Some(0);
    }
    if (r.max_x_cm - x_cm).abs() <= EPS {
        return Some(1);
    }
    if (r.min_z_cm - z_cm).abs() <= EPS {
        return Some(2);
    }
    if (r.min_x_cm - x_cm).abs() <= EPS {
        return Some(3);
    }
    None
}

struct Planner {
    seed: i32,
    /// Cota del suelo de la planta que se está planificando (ADR-102 D1). Cero en la baja.
    base_y_cm: i32,
    /// La caja de la región. Hace falta para saber si un bloque toca el borde, que es lo que decide
    /// si puede llevarse un desnivel (ver [`Planner::may_terrace`]).
    bounds: PlanRect,
    /// Coordenadas de puerta que un corte en X no puede pisar. Ver [`GATE_CLEARANCE_CM`].
    gate_cuts_x: Vec<i32>,
    /// Las mismas para un corte en Z.
    gate_cuts_z: Vec<i32>,
    nodes: Vec<Node>,
    spaces: Vec<PlannedSpace>,
    /// Para cada nodo, el índice del espacio-banda que talló su corte. `None` si el corte no talló
    /// banda (profundidad ≥ [`CORRIDOR_DEPTH`]) o si el nodo es hoja.
    band_of_node: Vec<Option<usize>>,
    links: Vec<PlannedLink>,
}

impl Planner {
    /// El reparto: se parte en anchura, y los cortes de los primeros niveles se llevan su banda.
    ///
    /// En ANCHURA y no en profundidad para que la banda de nivel 0 exista antes que ninguna de nivel
    /// 1 — la jerarquía tiene que quedar en el orden de creación, porque es la que decide qué
    /// corredor es el principal cuando dos se cruzan.
    fn subdivide(&mut self) {
        let mut cursor = 0usize;
        while cursor < self.nodes.len() {
            let node = cursor;
            cursor += 1;
            let rect = self.nodes[node].rect;
            let depth = self.nodes[node].depth;

            let Some((axis, at_cm, band_cm)) = self.decide_split(rect, depth) else {
                continue;
            };

            let half = band_cm / 2;
            let (a_rect, b_rect, band_rect) = if axis == 0 {
                (
                    PlanRect {
                        max_x_cm: at_cm - half,
                        ..rect
                    },
                    PlanRect {
                        min_x_cm: at_cm - half + band_cm,
                        ..rect
                    },
                    PlanRect {
                        min_x_cm: at_cm - half,
                        max_x_cm: at_cm - half + band_cm,
                        ..rect
                    },
                )
            } else {
                (
                    PlanRect {
                        max_z_cm: at_cm - half,
                        ..rect
                    },
                    PlanRect {
                        min_z_cm: at_cm - half + band_cm,
                        ..rect
                    },
                    PlanRect {
                        min_z_cm: at_cm - half,
                        max_z_cm: at_cm - half + band_cm,
                        ..rect
                    },
                )
            };

            if band_cm > 0 {
                let role = if depth == 0 {
                    SpaceRole::Spine
                } else {
                    SpaceRole::Corridor
                };
                let idx = self.push_space(band_rect, role, depth, 0, 0);
                self.band_of_node[node] = Some(idx);
            }

            let a = self.nodes.len();
            self.nodes.push(Node {
                rect: a_rect,
                depth: depth + 1,
                children: None,
            });
            self.band_of_node.push(None);
            let b = self.nodes.len();
            self.nodes.push(Node {
                rect: b_rect,
                depth: depth + 1,
                children: None,
            });
            self.band_of_node.push(None);
            self.nodes[node].children = Some((a, b));
        }
    }

    /// **ADR-100 enmienda 2 — LOS ESPACIOS HUNDIDOS.**
    ///
    /// Va DESPUÉS de `retag_dead_ends` porque necesita el grado del grafo: sólo se hunde un espacio
    /// con UNA sola puerta, y eso no se sabe hasta que el grafo está hecho. Ver [`SpaceRole::Stair`]
    /// para por qué esa condición no es una comodidad sino lo único que hace el desnivel seguro.
    fn sink_dead_ends(&mut self, gates: &[PlannedGate]) {
        let mut degree = vec![0usize; self.spaces.len()];
        let mut only_link = vec![None; self.spaces.len()];
        for l in &self.links {
            degree[l.a] += 1;
            degree[l.b] += 1;
            only_link[l.a] = Some((l.at_x_cm, l.at_z_cm));
            only_link[l.b] = Some((l.at_x_cm, l.at_z_cm));
        }
        // **LAS PUERTAS DE JUNTA CUENTAN COMO HUECO, y olvidarlo costó una sala sellada.**
        //
        // Un espacio con una puerta de junta tiene DOS huecos aunque el grafo le vea un enlace: los
        // peldaños parten la pared lateral en franjas, y una franja no da los 240 cm que mide un
        // vano. El síntoma fue `región (-1,0): 1 huecos perdidos` en una partida real — la sala nace
        // tapiada y la región vecina abre su puerta contra el muro.
        for g in gates {
            degree[g.space] += 1;
        }

        for i in 0..self.spaces.len() {
            if degree[i] != 1 {
                continue;
            }
            let s = self.spaces[i];
            if !s.role.is_built() || s.role.is_circulation() {
                continue;
            }
            let Some((dx, dz)) = only_link[i] else {
                continue;
            };
            let Some(side) = self.side_of_point(i, dx, dz) else {
                continue;
            };

            // Fondo disponible en la dirección de los peldaños.
            let depth_cm = if side.is_multiple_of(2) {
                s.rect.depth_cm()
            } else {
                s.rect.width_cm()
            };
            // **Cada peldaño tiene que ser una tira ancha de verdad.** Por debajo de la anchura de
            // vano mínima, la pared que se abre entre dos tiras cae por debajo de lo que el ráster
            // conservador deja pasar y la escalera nace tapiada. Es el mismo suelo de siempre.
            // Con la anchura de VANO y no con el mínimo generable: una franja tiene que poder alojar
            // una puerta entera, no sólo dejar pasar al jugador.
            //
            // Y menos uno, porque N contrahuellas se construyen con N+1 tiras (ver `fill::emit_stair`):
            // contando tiras como contrahuellas, la última franja salía por debajo del vano y volvía
            // el fallo de la sala tapiada.
            let max_steps = depth_cm / DOORWAY_CM - 1;
            let steps = TERRACE_STEPS.min(max_steps);
            if steps < 2 {
                continue;
            }

            let (cx, cz) = s.rect.centre_m();
            let mut st = hash::stream_at(self.seed, cx, cz, SALT_TERRACE);
            if st.next01() >= TERRACE_CHANCE {
                continue;
            }

            self.spaces[i].role = SpaceRole::Stair;
            // Baja, no sube: se entra y el suelo se hunde. Un callejón que sube se lee como un
            // altillo; uno que baja, como un sótano al que has llegado sin querer.
            self.spaces[i].rise_cm = -steps * STEP_RISE_CM;
            self.spaces[i].rise_from_side = side;
        }
    }

    /// ¿En qué lado de este espacio cae el punto? `None` si no está sobre ninguna de sus paredes.
    fn side_of_point(&self, space: usize, x_cm: i32, z_cm: i32) -> Option<u8> {
        side_of_point_in(&self.spaces[space].rect, x_cm, z_cm)
    }

    /// ¿Se parte este rectángulo, por dónde, y con cuánta banda? `None` = es una hoja.
    ///
    /// Devuelve `(eje, posición del centro de la banda, ancho de banda)`. El ancho es 0 cuando el
    /// corte no talla corredor, y entonces los dos hijos comparten la línea exacta — que es como
    /// acaban dos salas pared con pared.
    fn decide_split(&self, rect: PlanRect, depth: u8) -> Option<(u8, i32, i32)> {
        if depth >= MAX_DEPTH {
            return None;
        }
        let band_cm = if depth < CORRIDOR_DEPTH {
            BAND_WIDTH_CM[depth as usize]
        } else {
            0
        };

        let (cx, cz) = rect.centre_m();
        let class = scale::scale_at(self.seed, cx, cz);

        // El área objetivo es lo que para la subdivisión, y es donde el campo de escala pasa a
        // decidir tamaños de espacio en vez de sesgar un sorteo de pieza.
        let target = if class == scale::SCALE_WEIRD {
            let mut s = hash::stream_at(self.seed, cx, cz, SALT_STOP);
            let f = WEIRD_SPREAD.0 + s.next01() * (WEIRD_SPREAD.1 - WEIRD_SPREAD.0);
            TARGET_AREA_M2[scale::SCALE_WEIRD as usize] * f
        } else {
            TARGET_AREA_M2[class as usize]
        };
        if rect.area_m2() <= target {
            return None;
        }

        // Elección de eje: se parte el lado LARGO, con un escape hacia el corto para que aparezcan
        // proporciones que un BSP disciplinado nunca daría.
        //
        // **`axis == 0` corta la ANCHURA** (plano vertical, hijos izquierda/derecha), así que para
        // acortar el lado largo hay que pedir 0 cuando el largo es X. La primera versión pedía lo
        // contrario y partía siempre el lado corto: la región entera salía en lonchas verticales de
        // 5 × 30 m, y se vio en el volcado antes que en ningún número — ninguna métrica del plan
        // miraba la proporción.
        let mut s = hash::stream_at(self.seed, cx, cz, SALT_SPLIT);
        let long_is_x = rect.width_cm() >= rect.depth_cm();
        let long_axis: u8 = if long_is_x { 0 } else { 1 };

        // El escape sólo se permite mientras la pieza siga siendo razonablemente cuadrada. Partir el
        // lado corto de un rectángulo que ya es 3:1 lo lleva a 6:1, y de ahí no vuelve: ninguna
        // subdivisión posterior arregla una proporción, sólo la hereda.
        let slim = rect.width_cm().max(rect.depth_cm()) as f32
            / rect.width_cm().min(rect.depth_cm()).max(1) as f32;
        let cross = slim < MAX_ASPECT && s.next01() < CROSS_SPLIT_CHANCE;
        let wanted: u8 = if cross { 1 - long_axis } else { long_axis };

        // **SI EL EJE SORTEADO NO DA, SE PRUEBA EL OTRO ANTES DE RENDIRSE.**
        //
        // Rendirse al primer intento dejaba de subdividir rectángulos que sí tenían sitio por el otro
        // lado, y el efecto no era pequeño: hojas del doble del área objetivo, que luego se contaban
        // como naves porque pasaban el umbral de `Hall`. Una región salía con más naves que oficinas.
        let fits = |axis: u8| -> bool {
            let side = if axis == 0 {
                rect.width_cm()
            } else {
                rect.depth_cm()
            };
            // Los dos hijos y la banda tienen que caber. Se comprueba ANTES de cortar: una hoja
            // pequeña es una sala, una astilla no es nada.
            side >= 2 * MIN_SIDE_CM + band_cm
        };
        let axis = if fits(wanted) {
            wanted
        } else if fits(1 - wanted) {
            1 - wanted
        } else {
            return None;
        };

        let side = if axis == 0 {
            rect.width_cm()
        } else {
            rect.depth_cm()
        };
        // El lado que NO se corta también tiene que dar una sala, o saldrían dos astillas de canto.
        let other = if axis == 0 {
            rect.depth_cm()
        } else {
            rect.width_cm()
        };
        if other < MIN_SIDE_CM {
            return None;
        }

        let lo = MIN_SIDE_CM + band_cm / 2;
        let hi = side - MIN_SIDE_CM - band_cm / 2;
        let want = (side as f32 * (SPLIT_LO + s.next01() * (SPLIT_HI - SPLIT_LO))) as i32;
        let at = want.clamp(lo, hi);

        let base = if axis == 0 {
            rect.min_x_cm
        } else {
            rect.min_z_cm
        };
        // **Y el corte se aparta de las puertas de junta.** Si no puede —lo que queda libre está
        // entero dentro de una zona prohibida— se renuncia a cortar: una hoja algo mayor no le
        // importa a nadie, y una puerta partida sella la región contra su vecina.
        let world_at = self.clear_of_gates(base + at, axis, base + lo, base + hi)?;
        Some((axis, world_at, band_cm))
    }

    /// Aparta un corte de las puertas de junta, o `None` si no hay dónde ponerlo.
    ///
    /// Se recorre la lista varias veces porque salir de una zona prohibida puede meter el corte en la
    /// siguiente; con dos o tres puertas por borde converge en un par de vueltas, y si no converge se
    /// renuncia, que es la respuesta segura.
    fn clear_of_gates(&self, at: i32, axis: u8, lo: i32, hi: i32) -> Option<i32> {
        let blocked = if axis == 0 {
            &self.gate_cuts_x
        } else {
            &self.gate_cuts_z
        };
        if blocked.is_empty() {
            return Some(at);
        }

        let mut at = at;
        for _ in 0..4 {
            let mut moved = false;
            for &g in blocked {
                if (at - g).abs() >= GATE_CLEARANCE_CM {
                    continue;
                }
                // Al borde más cercano de la zona prohibida, que es el que menos deforma el reparto.
                let before = g - GATE_CLEARANCE_CM;
                let after = g + GATE_CLEARANCE_CM;
                at = if (at - before).abs() <= (at - after).abs() {
                    before
                } else {
                    after
                };
                moved = true;
            }
            if !moved {
                return if at >= lo && at <= hi { Some(at) } else { None };
            }
        }
        None
    }

    /// Las hojas del árbol pasan a ser espacios, con el papel que les toca por tamaño y sitio.
    fn emit_leaves(&mut self) {
        let leaves: Vec<usize> = (0..self.nodes.len())
            .filter(|&n| self.nodes[n].children.is_none())
            .collect();
        for n in leaves {
            let rect = self.nodes[n].rect;
            let depth = self.nodes[n].depth;
            // La cota la hereda del bloque: una sala es plana, y el desnivel vive en la escalera que
            // separa dos bloques. Sin esa regla haría falta un escalón en cada puerta.
            let (cx, cz) = rect.centre_m();
            let class = scale::scale_at(self.seed, cx, cz);
            let area = rect.area_m2();

            let mut s = hash::stream_at(self.seed, cx, cz, SALT_ROLE);
            // La escala del campo mueve el umbral de «esto es una nave»: lo que en zona estrecha ya
            // es enorme, en zona grande es una sala normal. Sin esto, `Hall` sería puro tamaño y una
            // región `Large` saldría entera de naves.
            let hall_at = match class {
                scale::SCALE_NARROW => HALL_AREA_M2 * 0.6,
                scale::SCALE_LARGE => HALL_AREA_M2 * 1.4,
                _ => HALL_AREA_M2,
            };
            let role = if area >= hall_at {
                SpaceRole::Hall
            } else if area <= STORAGE_AREA_M2 {
                SpaceRole::Storage
            } else if depth >= CORRIDOR_DEPTH + 2 && s.next01() < 0.28 {
                // Al fondo de una rama, lejos del corredor: el trasero de la escena. Es donde el
                // tipo de boca `Service` tiene sentido, y el único sitio del plan que sabe de él.
                SpaceRole::Service
            } else {
                SpaceRole::Office
            };
            // Toda sala nace PLANA y a cota 0. El desnivel llega después y sólo a las que tienen una
            // sola puerta (`sink_dead_ends`), que es la única forma de que no se le escape a nadie.
            self.push_space(rect, role, depth, 0, 0);
        }
    }

    /// El vacío intencionado. Va DESPUÉS de repartir papeles y ANTES de enlazar: un hueco tiene que
    /// existir antes de que nadie decida a qué se conecta.
    ///
    /// **No se vacía nunca una banda de circulación.** Un agujero en la espina parte el edificio en
    /// dos, y eso no es un patio: es el fallo de conectividad de siempre con otro nombre.
    fn assign_void(&mut self) {
        for i in 0..self.spaces.len() {
            let s = &self.spaces[i];
            if s.role.is_circulation() {
                continue;
            }
            let (cx, cz) = s.rect.centre_m();
            let chance = if s.scale == scale::SCALE_WEIRD {
                VOID_CHANCE_WEIRD
            } else {
                VOID_CHANCE
            };
            let mut st = hash::stream_at(self.seed, cx, cz, SALT_VOID);
            if st.next01() < chance {
                self.spaces[i].role = SpaceRole::Void;
            }
        }
    }

    /// ADR-104 D2 — **el vacío se BUSCA encima de una nave, no se tolera donde caiga.**
    ///
    /// Sin esto un atrio es casualidad: hay que acertar a que el sorteo de [`Self::assign_void`]
    /// ponga hueco justo sobre una nave de abajo. Medido antes de escribir esta función, cuatro
    /// regiones de referencia: **9 atrios sobre 26 naves, con 311 espacios con vacío encima.** O sea
    /// que vacío sobra y lo que falta es la COINCIDENCIA — que es lo que se arregla aquí, y por eso
    /// no hace falta subir ninguna probabilidad.
    ///
    /// # Todo o nada, y no es escrúpulo
    ///
    /// `PlannedSpace::void_above` sólo vale cuando **ningún** espacio construido de arriba pisa la
    /// nave. Vaciar tres de los cuatro que la cubren no da tres cuartos de atrio: da cero, y encima
    /// deja tres agujeros en la planta alta a cambio de nada. Así que cada nave se toma entera o se
    /// descarta entera.
    ///
    /// # Y no se vacía circulación, por el mismo motivo que `assign_void`
    ///
    /// Un agujero en la espina parte la planta alta en dos. Si la huella de una nave toca cualquier
    /// banda de circulación, esa nave no puede ser atrio — se descarta antes de tocar nada, y no se
    /// intenta a medias.
    fn carve_atria(&mut self, atria_below: &[PlanRect]) {
        for target in atria_below {
            let covering: Vec<usize> = (0..self.spaces.len())
                .filter(|&i| self.spaces[i].rect.overlaps(target))
                .collect();

            // Nadie encima: la nave ya es atrio sin ayuda, y contarla aquí sería contarla dos veces.
            if covering.is_empty() {
                continue;
            }
            // Un solo espacio de circulación encima basta para descartar la nave entera.
            if covering.iter().any(|&i| {
                let r = self.spaces[i].role;
                r.is_circulation() || r == SpaceRole::Spine
            }) {
                continue;
            }
            for i in covering {
                self.spaces[i].role = SpaceRole::Void;
            }
        }
    }

    /// El grafo del edificio, en cuatro pasadas y en este orden.
    ///
    /// El orden ES la jerarquía: primero se cose la red de circulación entre sí, luego cada sala
    /// entra por su corredor, luego se rescata lo que no toca ninguno, y sólo al final se abren
    /// vanos de más. Hacerlo al revés daría un grafo igual de conexo y un edificio en el que el
    /// corredor no manda sobre nada.
    fn link_all(&mut self) {
        let adj = self.adjacencies();

        // 1 — la red de circulación. Todas las bandas que se tocan quedan unidas: es la espina y sus
        //     ramas, y tiene que ser conexa antes de que cuelgue nada de ella.
        for &(i, j, w, x, z) in &adj {
            if self.spaces[i].role.is_circulation() && self.spaces[j].role.is_circulation() {
                self.links.push(PlannedLink {
                    a: i,
                    b: j,
                    width_cm: w.min(WIDE_DOORWAY_CM),
                    kind: LinkKind::Junction,
                    at_x_cm: x,
                    at_z_cm: z,
                });
            }
        }

        // 2 — cada sala entra por el corredor que la bordea. Ésta es la pasada que hace que el
        //     corredor sea arquitectura: existe para dar acceso, y el acceso se decide aquí.
        //
        //     Se queda con la pared MÁS ANCHA de las que tocan corredor, no con la primera que
        //     aparezca: la primera sale del orden del recorrido, que no significa nada, y una sala
        //     que da al pasillo por dos lados tiene que entrar por el bueno.
        let mut best_access: Vec<Option<(i32, usize, i32, i32)>> = vec![None; self.spaces.len()];
        for &(i, j, w, x, z) in &adj {
            let (room, band) = match (
                self.spaces[i].role.is_circulation(),
                self.spaces[j].role.is_circulation(),
            ) {
                (false, true) => (i, j),
                (true, false) => (j, i),
                _ => continue,
            };
            if !self.spaces[room].role.is_built() {
                continue;
            }
            if best_access[room].is_none_or(|(bw, _, _, _)| w > bw) {
                best_access[room] = Some((w, band, x, z));
            }
        }
        for (room, slot) in best_access.iter().enumerate() {
            let Some((w, band, x, z)) = *slot else {
                continue;
            };
            let width = if self.spaces[room].role == SpaceRole::Hall && w >= WIDE_DOORWAY_CM {
                WIDE_DOORWAY_CM
            } else {
                DOORWAY_CM
            };
            self.links.push(PlannedLink {
                a: room,
                b: band,
                width_cm: width,
                kind: LinkKind::Access,
                at_x_cm: x,
                at_z_cm: z,
            });
        }

        // 3 — las salas que no tocan ninguna banda cuelgan de una vecina que sí llegue. Es la suite
        //     de despachos a la que se entra por otro despacho, y es arquitectura normal: sin esto
        //     habría que meter corredor hasta la última puerta y volveríamos a la cuadrícula.
        let mut uf = UnionFind::new(self.spaces.len());
        for l in &self.links {
            uf.union(l.a, l.b);
        }
        // Raíz de la circulación: todo lo construido tiene que acabar colgando de aquí.
        let hub = self
            .spaces
            .iter()
            .position(|s| s.role == SpaceRole::Spine)
            .or_else(|| self.spaces.iter().position(|s| s.role.is_circulation()));

        // Se repite hasta que deje de crecer: una sala puede engancharse a otra que acaba de
        // engancharse, y ésa es justo la cadena de dos y tres puertas que da profundidad.
        let mut grew = true;
        while grew {
            grew = false;
            for &(i, j, w, x, z) in &adj {
                if !self.spaces[i].role.is_built() || !self.spaces[j].role.is_built() {
                    continue;
                }
                if self.spaces[i].role.is_circulation() || self.spaces[j].role.is_circulation() {
                    continue;
                }
                let (ri, rj) = (uf.find(i), uf.find(j));
                if ri == rj {
                    continue;
                }
                let hub_root = hub.map(|h| uf.find(h));
                // Sólo se une si UNO de los dos ya llega a la circulación. Coser dos bolsillos entre
                // sí crearía una isla mayor, que es el error que ADR-098 ya midió en el enrutador.
                if hub_root != Some(ri) && hub_root != Some(rj) {
                    continue;
                }
                uf.union(i, j);
                self.links.push(PlannedLink {
                    a: i,
                    b: j,
                    width_cm: w.min(DOORWAY_CM),
                    kind: LinkKind::Doorway,
                    at_x_cm: x,
                    at_z_cm: z,
                });
                grew = true;
            }
        }

        // 3b — lo que siga suelto se une igual, aunque no toque la circulación. Sin esta pasada, un
        //      corro de salas rodeado de vacío se queda fuera del edificio, y eso no es una decisión
        //      del plan: es el mismo agujero de siempre.
        for &(i, j, w, x, z) in &adj {
            if !self.spaces[i].role.is_built() || !self.spaces[j].role.is_built() {
                continue;
            }
            if uf.find(i) == uf.find(j) {
                continue;
            }
            uf.union(i, j);
            self.links.push(PlannedLink {
                a: i,
                b: j,
                width_cm: w.min(DOORWAY_CM),
                kind: LinkKind::Doorway,
                at_x_cm: x,
                at_z_cm: z,
            });
        }

        // 4 — vanos de MÁS: los anillos. Un edificio con un solo camino a cada sitio se recorre como
        //     un árbol, y el «esto ya lo he visto» que sostiene la liminalidad necesita volver por
        //     otro lado.
        for &(i, j, w, x, z) in &adj {
            if !self.spaces[i].role.is_built() || !self.spaces[j].role.is_built() {
                continue;
            }
            if self.linked(i, j) {
                continue;
            }
            let (mx, mz) = ((x as f32) / CM_PER_M, (z as f32) / CM_PER_M);
            let mut s = hash::stream_at(self.seed, mx, mz, SALT_RING);
            if s.next01() >= RING_CHANCE {
                continue;
            }
            let kind = match (
                self.spaces[i].role.is_circulation(),
                self.spaces[j].role.is_circulation(),
            ) {
                (true, true) => LinkKind::Junction,
                (false, false) => LinkKind::Doorway,
                _ => LinkKind::Access,
            };
            self.links.push(PlannedLink {
                a: i,
                b: j,
                width_cm: w.min(DOORWAY_CM),
                kind,
                at_x_cm: x,
                at_z_cm: z,
            });
        }
    }

    /// Las salas que acabaron con UNA sola conexión pasan a llamarse lo que son.
    ///
    /// Se hace al final y no al repartir papeles porque un callejón no es una propiedad de la sala:
    /// es una propiedad del GRAFO, y no se sabe hasta que el grafo está hecho.
    fn retag_dead_ends(&mut self) {
        let mut degree = vec![0usize; self.spaces.len()];
        for l in &self.links {
            degree[l.a] += 1;
            degree[l.b] += 1;
        }
        for (space, d) in self.spaces.iter_mut().zip(degree) {
            if d == 1 && matches!(space.role, SpaceRole::Office | SpaceRole::Storage) {
                space.role = SpaceRole::DeadEnd;
            }
        }
    }

    /// Cuelga cada puerta de junta del espacio cuya pared abre.
    ///
    /// **Una puerta no es un espacio, y el primer intento que la hizo espacio salió mal**: un tocón
    /// plantado hacia dentro desde el borde cae encima de la hoja que ya ocupaba ese trozo de región,
    /// y el plan salía con siete solapes — geometría cruzada que el ráster estampa maciza sin dar un
    /// solo error. Como los espacios teselan la región, el punto de la puerta cae SIEMPRE sobre el
    /// borde de exactamente uno: se busca, y se le abre el vano ahí.
    ///
    /// **Y ese espacio deja de poder ser vacío.** Una puerta acordada con la vecina que da a un patio
    /// clausurado es el peor caso posible: la otra región pone su tramo y aquí no hay a dónde entrar.
    /// Si toca vacío, se rescata — la puerta manda sobre la decoración.
    fn attach_gates(&mut self, gates: &[Wg3Gate]) -> Vec<PlannedGate> {
        let mut out = Vec::with_capacity(gates.len());
        for gate in gates {
            let gx = (gate.x * CM_PER_M).round() as i32;
            let gz = (gate.z * CM_PER_M).round() as i32;

            // El espacio cuyo BORDE contiene el punto. Se prefiere la circulación cuando la puerta
            // cae justo en la esquina entre dos: entrar a una región por un pasillo es mejor que
            // entrar por el fondo de un despacho.
            let mut best: Option<(usize, bool)> = None;
            for i in 0..self.spaces.len() {
                if !self.touches_border_point(i, gx, gz, gate.outward_side) {
                    continue;
                }
                let circ = self.spaces[i].role.is_circulation();
                if best.is_none_or(|(_, bcirc)| circ && !bcirc) {
                    best = Some((i, circ));
                }
            }
            let Some((space, _)) = best else {
                // Sin espacio en ese punto la puerta no se puede cumplir. Se avisa fuerte: el
                // síntoma sería que la región vecina abre un vano al vacío, y eso es una caída.
                log::warn!(
                    "[wg3] puerta de junta en ({:.1},{:.1}) sin espacio del plan que la abra",
                    gate.x,
                    gate.z
                );
                continue;
            };

            if !self.spaces[space].role.is_built() {
                let area = self.spaces[space].rect.area_m2();
                self.spaces[space].role = if area >= HALL_AREA_M2 {
                    SpaceRole::Hall
                } else {
                    SpaceRole::Office
                };
            }

            out.push(PlannedGate {
                space,
                x_cm: gx,
                z_cm: gz,
                outward_side: gate.outward_side,
                width_cm: DOORWAY_CM,
            });
        }
        out
    }

    /// ¿Cae este punto sobre el borde del espacio `i` que mira a `side`?
    fn touches_border_point(&self, i: usize, x: i32, z: i32, side: u8) -> bool {
        const EPS: i32 = 2;
        let r = self.spaces[i].rect;
        // Media puerta a cada lado tiene que caber dentro del espacio, o el vano se saldría de su
        // pared y quedaría medio abierto contra el vecino.
        let half = DOORWAY_CM / 2;
        match side % 4 {
            0 => (r.max_z_cm - z).abs() <= EPS && x - half >= r.min_x_cm && x + half <= r.max_x_cm,
            1 => (r.max_x_cm - x).abs() <= EPS && z - half >= r.min_z_cm && z + half <= r.max_z_cm,
            2 => (r.min_z_cm - z).abs() <= EPS && x - half >= r.min_x_cm && x + half <= r.max_x_cm,
            _ => (r.min_x_cm - x).abs() <= EPS && z - half >= r.min_z_cm && z + half <= r.max_z_cm,
        }
    }

    /// **EL EDIFICIO TIENE QUE SER UNO, y aquí se garantiza.**
    ///
    /// Las cuatro pasadas de `link_all` cosen todo lo que se toca, pero el vacío intencionado puede
    /// aislar un ala entera: un corro de salas rodeado de patios no toca nada construido y se queda
    /// fuera. Eso no es una decisión —nadie ha decidido que ese ala sea inaccesible—, es el agujero
    /// de conectividad de siempre entrando por otra puerta.
    ///
    /// Se arregla **rescatando el vacío que estorba, no enrutando alrededor**. Un patio que parte el
    /// edificio en dos no es un patio; devolverlo a sala es más barato y más honesto que tender un
    /// pasillo generado por encima de él. Sólo si no hay ningún vacío que sirva de puente se recurre
    /// a un [`LinkKind::Route`], que es el encargo explícito al enrutador.
    fn ensure_connected(&mut self) {
        for _ in 0..self.spaces.len() {
            let mut uf = UnionFind::new(self.spaces.len());
            for l in &self.links {
                uf.union(l.a, l.b);
            }
            let mut roots: Vec<usize> = (0..self.spaces.len())
                .filter(|&i| self.spaces[i].role.is_built())
                .map(|i| uf.find(i))
                .collect();
            roots.sort_unstable();
            roots.dedup();
            if roots.len() <= 1 {
                return;
            }

            let adj = self.adjacencies();
            // La componente MAYOR es el edificio; lo demás se le engancha. Medirlo por área y no por
            // número de espacios: un ala de tres naves pesa más que veinte trasteros, y el edificio
            // es donde está la superficie.
            let mut area_of: Vec<(usize, f32)> = roots.iter().map(|&r| (r, 0.0)).collect();
            for i in 0..self.spaces.len() {
                if !self.spaces[i].role.is_built() {
                    continue;
                }
                let r = uf.find(i);
                if let Some(slot) = area_of.iter_mut().find(|(root, _)| *root == r) {
                    slot.1 += self.spaces[i].rect.area_m2();
                }
            }
            let main = area_of
                .iter()
                // Desempate por índice de raíz: a igual área, la menor. «El que salga» haría que el
                // mundo cambiara entre ejecuciones sin que cambie nada más.
                .max_by(|a, b| a.1.total_cmp(&b.1).then(b.0.cmp(&a.0)))
                .map(|(r, _)| *r)
                .expect("hay al menos dos componentes");

            // Un vacío que toca la componente mayor y alguna otra: devolverlo a sala las une.
            let mut bridge: Option<(usize, Vec<Touching>)> = None;
            for v in 0..self.spaces.len() {
                if self.spaces[v].role != SpaceRole::Void {
                    continue;
                }
                let touching: Vec<Touching> = adj
                    .iter()
                    .filter_map(|&(i, j, w, x, z)| match (i == v, j == v) {
                        (true, false) => Some((j, w, x, z)),
                        (false, true) => Some((i, w, x, z)),
                        _ => None,
                    })
                    .filter(|&(o, _, _, _)| self.spaces[o].role.is_built())
                    .collect();
                let mut seen: Vec<usize> = touching.iter().map(|&(o, ..)| uf.find(o)).collect();
                seen.sort_unstable();
                seen.dedup();
                if seen.len() >= 2 && seen.contains(&main) {
                    bridge = Some((v, touching));
                    break;
                }
            }

            if let Some((v, touching)) = bridge {
                let area = self.spaces[v].rect.area_m2();
                self.spaces[v].role = if area >= HALL_AREA_M2 {
                    SpaceRole::Hall
                } else {
                    SpaceRole::Office
                };
                for (o, w, x, z) in touching {
                    self.links.push(PlannedLink {
                        a: v,
                        b: o,
                        width_cm: w.min(DOORWAY_CM),
                        kind: LinkKind::Doorway,
                        at_x_cm: x,
                        at_z_cm: z,
                    });
                }
                continue;
            }

            // Sin puente disponible: se le ENCARGA al enrutador. Es la única forma de conexión del
            // plan que no puede cumplirse con un vano, y por eso sale marcada.
            let Some((a, b)) = self.closest_pair_between(&uf, main, &roots) else {
                return;
            };
            let (ax, az) = self.spaces[a].rect.centre_m();
            let (bx, bz) = self.spaces[b].rect.centre_m();
            self.links.push(PlannedLink {
                a,
                b,
                width_cm: DOORWAY_CM,
                kind: LinkKind::Route,
                at_x_cm: ((ax + bx) * 0.5 * CM_PER_M) as i32,
                at_z_cm: ((az + bz) * 0.5 * CM_PER_M) as i32,
            });
        }
    }

    /// La pareja más cercana entre la componente mayor y cualquier otra. Determinista: a igual
    /// distancia gana el índice menor, nunca «el que salga».
    fn closest_pair_between(
        &self,
        uf: &UnionFind,
        main: usize,
        roots: &[usize],
    ) -> Option<(usize, usize)> {
        let mut uf = UnionFind {
            parent: uf.parent.clone(),
        };
        let others: Vec<usize> = roots.iter().copied().filter(|&r| r != main).collect();
        let mut best: Option<(i64, usize, usize)> = None;
        for i in 0..self.spaces.len() {
            if !self.spaces[i].role.is_built() || uf.find(i) != main {
                continue;
            }
            for j in 0..self.spaces.len() {
                if !self.spaces[j].role.is_built() || !others.contains(&uf.find(j)) {
                    continue;
                }
                let (ax, az) = self.spaces[i].rect.centre_m();
                let (bx, bz) = self.spaces[j].rect.centre_m();
                let d = (((ax - bx) * (ax - bx) + (az - bz) * (az - bz)) * 100.0) as i64;
                if best.is_none_or(|(bd, ..)| d < bd) {
                    best = Some((d, i, j));
                }
            }
        }
        best.map(|(_, a, b)| (a, b))
    }

    /// Parejas de espacios que comparten pared con sitio para un vano, con anchura y punto del paso.
    ///
    /// **Es cuadrático, y a este tamaño no importa.** Una región da del orden de cincuenta espacios,
    /// o sea ~1 250 comparaciones de enteros por región y una sola vez. Lo que hay que no hacer es
    /// llevarlo al mundo entero: aquí está acotado por la región y no crece con la partida.
    fn adjacencies(&self) -> Vec<(usize, usize, i32, i32, i32)> {
        let mut out = Vec::new();
        for i in 0..self.spaces.len() {
            for j in (i + 1)..self.spaces.len() {
                if let Some((w, x, z)) = rects_share_wall(self.spaces[i].rect, self.spaces[j].rect)
                {
                    out.push((i, j, w, x, z));
                }
            }
        }
        out
    }

    fn linked(&self, i: usize, j: usize) -> bool {
        self.links
            .iter()
            .any(|l| (l.a == i && l.b == j) || (l.a == j && l.b == i))
    }

    #[allow(clippy::too_many_arguments)]
    fn push_space(
        &mut self,
        rect: PlanRect,
        role: SpaceRole,
        depth: u8,
        floor_y_cm: i32,
        rise_cm: i32,
    ) -> usize {
        let (cx, cz) = rect.centre_m();
        self.spaces.push(PlannedSpace {
            rect,
            // ADR-102 D1 — la cota de la planta se suma AQUÍ y en ningún otro sitio, así que el resto
            // del planificador sigue trabajando en su propio cero y no se entera de a qué altura está.
            floor_y_cm: self.base_y_cm + floor_y_cm,
            role,
            scale: scale::scale_at(self.seed, cx, cz),
            depth,
            rise_cm,
            // El lado de entrada lo pone `sink_dead_ends` cuando hunde el espacio; aquí no se sabe
            // todavía, porque depende del grafo.
            rise_from_side: 0,
            rise_step_cm: STEP_RISE_CM,
            // Sin tope mientras no haya nada encima. Lo pone el edificio, no la planta.
            max_clear_cm: 0,
            // Y esto sólo lo sabe el edificio: una planta sola no puede saber si tiene otra encima.
            void_above: false,
        });
        self.spaces.len() - 1
    }
}

/// ¿Comparten estos dos rectángulos una pared con sitio para un vano?
///
/// Devuelve `(longitud del solape en cm, x del centro del paso, z del centro del paso)`.
///
/// **Comparten pared quiere decir que se TOCAN, no que se penetren.** Los rectángulos del plan
/// tesela la región, así que dos vecinos comparten exactamente su línea de corte; se admite un
/// centímetro de holgura por si un redondeo mueve un borde, y ni uno más — dos rectángulos que se
/// pisan son un fallo del reparto, no una pared común.
pub fn rects_share_wall(a: PlanRect, b: PlanRect) -> Option<(i32, i32, i32)> {
    const TOUCH_CM: i32 = 1;

    // Pared vertical: a la derecha de `a` está `b`, o al revés.
    let vertical = if (a.max_x_cm - b.min_x_cm).abs() <= TOUCH_CM {
        Some((a.max_x_cm + b.min_x_cm) / 2)
    } else if (b.max_x_cm - a.min_x_cm).abs() <= TOUCH_CM {
        Some((b.max_x_cm + a.min_x_cm) / 2)
    } else {
        None
    };
    if let Some(x) = vertical {
        let lo = a.min_z_cm.max(b.min_z_cm);
        let hi = a.max_z_cm.min(b.max_z_cm);
        if hi - lo >= MIN_SHARED_WALL_CM {
            return Some((hi - lo, x, (lo + hi) / 2));
        }
        return None;
    }

    // Pared horizontal.
    let horizontal = if (a.max_z_cm - b.min_z_cm).abs() <= TOUCH_CM {
        Some((a.max_z_cm + b.min_z_cm) / 2)
    } else if (b.max_z_cm - a.min_z_cm).abs() <= TOUCH_CM {
        Some((b.max_z_cm + a.min_z_cm) / 2)
    } else {
        None
    };
    if let Some(z) = horizontal {
        let lo = a.min_x_cm.max(b.min_x_cm);
        let hi = a.max_x_cm.min(b.max_x_cm);
        if hi - lo >= MIN_SHARED_WALL_CM {
            return Some((hi - lo, (lo + hi) / 2, z));
        }
    }
    None
}

/// Union-find con compresión de caminos. Propio y no el de `route.rs` porque aquél es privado de
/// aquel módulo, y exportarlo ataría dos cosas que no tienen por qué moverse juntas.
struct UnionFind {
    parent: Vec<usize>,
}

impl UnionFind {
    fn new(n: usize) -> Self {
        Self {
            parent: (0..n).collect(),
        }
    }
    fn find(&mut self, mut x: usize) -> usize {
        while self.parent[x] != x {
            self.parent[x] = self.parent[self.parent[x]];
            x = self.parent[x];
        }
        x
    }
    fn union(&mut self, a: usize, b: usize) {
        let (ra, rb) = (self.find(a), self.find(b));
        if ra != rb {
            self.parent[rb] = ra;
        }
    }
}
