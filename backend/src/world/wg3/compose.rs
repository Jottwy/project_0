//! ADR-095 F4 — el compositor: semilla + catálogo ⇒ lista de piezas colocadas.
//!
//! Port de `Wg3Composer` (C#). El original se queda donde está: Unity autora y prueba el catálogo, y
//! este lado es el que sirve el mundo. Que el algoritmo esté escrito dos veces no es descuido —es la
//! misma partida doble que ya tienen la rotación y el ráster— pero sí es una deuda con una sola
//! forma de pagarla: el oráculo. `wg3_composition_oracle.json` lleva el mundo entero que produce C#
//! para cinco semillas, y el test que lo reproduce es lo único capaz de cazar una deriva entre los
//! dos idiomas antes de que aparezca como una pared donde debía haber una puerta.
//!
//! AQUÍ NO HAY GEOMETRÍA (R1). Se trabaja solo con huella y bocas, que es exactamente lo que trae el
//! manifiesto. La chuleta de colisión no se mira hasta rasterizar.
//!
//! LO QUE ESTE FICHERO NO RESUELVE, y conviene tenerlo delante: es un recorrido incremental desde una
//! semilla —la ruta del mundo finito—, no un generador por chunk. Lo que hace que migrar al contrato
//! de frontera NO sea una reescritura es que **ninguna decisión depende del orden de proceso**: cada
//! sorteo abre su flujo a partir de la POSICIÓN de la boca y el campo de escala es función pura de la
//! posición. Lo único atado al recorrido es `depth`, y su sustituto ya está anotado en el brief:
//! distancia a un ancla.
//!
//! TAMPOCO CIERRA BUCLES: el resultado es un árbol, una pieza nueva jamás vuelve a engancharse a una
//! ya colocada. Es un límite conocido del original, y reproducirlo es obligatorio mientras el oráculo
//! sea el criterio de corrección.

use super::hash;
use super::manifest::{Wg3Manifest, Wg3Piece, Wg3Socket};
use super::placement::{local_point, Wg3Placement};
use super::route::{self, Mouth, Rect, RouteSettings};
use super::scale;
use super::segment::Wg3Segment;

/// Estado de una boca. Espejo de las constantes de `Wg3World`.
pub const SOCKET_OPEN: u8 = 0;
pub const SOCKET_CONNECTED: u8 = 1;
pub const SOCKET_CAPPED: u8 = 2;

/// ADR-098 — boca que el recorrido decidió cerrar pero cuya geometría todavía no se ha puesto.
///
/// **Existe porque el orden importaba más de lo que parecía.** Sellar en el momento —que es lo que
/// hace C# y lo que fija el oráculo— deja al enrutador sin nada que unir: cuando termina el
/// recorrido, todas las bocas del árbol están ya CONECTADAS o TAPADAS, y las únicas abiertas son las
/// de los tramos de junta. Medido: con el sellado inmediato, tres de cuatro regiones daban CERO
/// conectores, y no por falta de sitio sino por falta de candidatas.
///
/// Así que con el enrutador encendido la decisión se toma igual pero la geometría se aplaza a la
/// pasada final. **Solo con el enrutador encendido**: sin él, el compositor se comporta exactamente
/// como antes y el oráculo sigue valiendo.
///
/// Efecto lateral declarado: aplazar deja libre el hueco donde iría el tapón durante el recorrido,
/// así que otra rama puede crecer ahí. El mundo enrutado no es el mundo de hoy más conectores; es
/// otro mundo, y las sondas lo miden como tal.
pub const SOCKET_PENDING_CAP: u8 = 3;

/// Sal del sorteo de tapón voluntario.
const SALT_CAP: u32 = 0xC0DE_C0DE;
/// Sal de la elección de pieza. Distinta de la anterior para que las dos decisiones que ocurren en el
/// MISMO punto no queden correlacionadas.
const SALT_PICK: u32 = 0x0F1C_E5ED;

/// Hueco libre mínimo para que un jugador pase, en metros. Espejo de `Wg3Validator.MinHeadroom`.
const MIN_HEADROOM: f32 = 2.0;
/// Tolerancia al casar cotas de suelo entre dos bocas.
const FLOOR_MATCH_TOLERANCE: f32 = 0.01;
/// Margen para comparar anchuras. Milímetro: dos bocas de 2,4 m autoradas por separado tienen que
/// casar, pero 2,4 y 2,5 son incompatibles a propósito.
const WIDTH_MATCH_TOLERANCE: f32 = 0.001;

/// Solape estricto de huellas. El epsilon existe porque dos piezas encajadas COMPARTEN el plano de la
/// junta: tocarse es correcto, penetrar no.
const OVERLAP_EPS: f32 = 0.02;

/// Perillas de composición. Separadas del algoritmo porque son los números que se tocan al mirar el
/// mundo, y ninguno debería exigir recompilar la cabeza.
#[derive(Debug, Clone, PartialEq)]
pub struct Wg3ComposerSettings {
    /// Tope de piezas.
    pub budget: usize,

    /// Probabilidad de NO usar una boca aunque haya candidata. Es lo que produce paredes ciegas y
    /// espacios residuales. A 0 el mundo se ramifica hasta llenar el presupuesto y se lee como un
    /// árbol; a 0,5 se ahoga enseguida.
    pub deliberate_cap_chance: f32,

    /// Piezas colocadas antes de permitir tapones voluntarios. Sin esto la semilla puede sellarse a
    /// sí misma y el mundo son dos piezas.
    pub cap_grace_count: usize,

    /// Multiplicador cuando la clase de escala de la pieza es la que pide el campo.
    pub scale_exact_bonus: f32,
    /// Multiplicador a una clase de distancia (estrecha↔media, media↔grande…).
    pub scale_near_bonus: f32,
    /// Multiplicador a dos o más clases. No es cero a propósito: un salto brusco de escala de vez en
    /// cuando es deseable, solo tiene que ser raro.
    pub scale_far_bonus: f32,

    /// Penalización si la candidata repite la pieza a la que se engancha.
    pub repeat_parent_penalty: f32,
    /// Penalización si repite la de dos pasos atrás. Más suave: A-B-A cansa menos que A-A.
    pub repeat_grandparent_penalty: f32,

    /// ADR-096 — unir dos bocas abiertas que caen enfrentadas en el mismo punto, en vez de tratar
    /// cada una por su lado.
    ///
    /// **Convierte el árbol en un grafo con anillos, y eso arregla DOS cosas a la vez.** La que se
    /// veía: un mundo que nunca vuelve sobre sí mismo no tiene el «esto ya lo he visto» que sostiene
    /// media liminalidad. Y la que no se veía hasta medirla: la frontera se seca sola —con tope de
    /// 300 piezas, seis semillas daban de 20 a 268—, porque cada rama termina en tapones y nadie
    /// reengancha. Subir el presupuesto no lo arreglaba.
    ///
    /// **`false` por defecto A PROPÓSITO.** El compositor de C# no cierra bucles, y el oráculo de
    /// composición fija ese mundo. Encenderlo por defecto pondría rojo el test que vigila la paridad
    /// entre los dos idiomas, que es lo único que caza una deriva silenciosa. Lo enciende quien
    /// sirve el mundo (`wg3::world`); el oráculo lo deja apagado y sigue vigilando el algoritmo
    /// base.
    pub close_loops: bool,

    /// ADR-096 — caja `(min_x, min_z, max_x, max_z)` fuera de la cual no se coloca nada.
    ///
    /// **Es lo que convierte una composición en una REGIÓN.** Sin ella el recorrido se va donde
    /// quiera y dos composiciones vecinas se pisarían: cada una colocaría piezas en el terreno de
    /// la otra sin saberlo, y el solape solo se vería al llegar el jugador.
    ///
    /// Se rechaza la candidata que ASOME, no la que tenga el centro fuera: una pieza a medias entre
    /// dos regiones es exactamente el caso que no puede existir mientras no haya contrato de junta.
    ///
    /// `None` compone sin límite, que es el mundo A3 de antes y lo que sigue usando el oráculo.
    pub bounds: Option<(f32, f32, f32, f32)>,

    /// Dónde va la pieza semilla. `None` la centra en el origen del mundo.
    ///
    /// Con `bounds` puesto se centra en la caja: sembrar en el origen y acotar a una región lejana
    /// daría una región vacía, porque la primera pieza ya caería fuera.
    pub seed_at: Option<(f32, f32)>,

    /// ADR-098 — el enrutador de conectores generados. `None` lo deja APAGADO.
    ///
    /// **Apagado por defecto por lo mismo que `close_loops`:** el oráculo de composición fija el
    /// mundo que produce C#, y C# no enruta. Encenderlo por defecto pondría rojo lo único que caza
    /// una deriva silenciosa entre los dos idiomas. Lo enciende quien sirve el mundo
    /// (`wg3::world`).
    ///
    /// Lo que hace, en una línea: donde el catálogo no puede encajar una pieza —porque dos bocas
    /// nunca coinciden clavadas—, **genera** la geometría que las une. Arregla de una vez las islas
    /// (cruzar una junta lleva a un armario), los anillos (el mundo es un árbol) y las juntas que no
    /// llevan a ninguna parte, que son el mismo problema visto por tres sitios.
    pub route: Option<RouteSettings>,

    /// SONDA — cuánto se deja que dos piezas se pisen, en metros. `0.0` es la regla de siempre.
    ///
    /// **No es una perilla de producción: existe para MEDIR.** Hoy el compositor rechaza toda
    /// candidata que pise algo ya colocado, y eso tiene un precio que nadie había puesto en un
    /// número: cada pieza carga sus cuatro paredes, así que entre dos salas contiguas hay dos muros
    /// y un hueco, el llenado se queda en ~20 % y el mundo se lee como cajas sueltas unidas por
    /// tubos en vez de como un edificio.
    ///
    /// Con holgura `s`, una candidata se acepta mientras la intersección no pase de `s` en alguno
    /// de los dos ejes — o sea, se permite que las piezas COMPARTAN pared en vez de duplicarla.
    ///
    /// **Ojo con lo que este número NO dice:** permitir el solape sin excavar el muro común deja
    /// geometría cruzada, y el ráster la estampa maciza. Sirve para saber cuánto subiría el
    /// llenado; no para servir un mundo. El excavado es el trabajo de verdad y necesita ADR.
    pub overlap_slack_m: f32,

    /// SONDA — apuntar CONTRA QUÉ se choca cada candidata rechazada por solape, no solo cuántas.
    ///
    /// Mide el techo de la absorción: la idea de que un pasillo que topa con una sala no se
    /// descarte, sino que se recorte contra ella, le abra un vano y deje de expandirse. Hoy cada
    /// uno de esos choques se cuenta y se tira, así que el material está ahí sin que nadie lo haya
    /// mirado. Cuesta memoria, así que `false` salvo en la sonda.
    pub collect_absorption_hits: bool,

    /// ADR-096 — piezas que van puestas ANTES del recorrido, con una boca ya conectada al otro lado
    /// de una junta.
    ///
    /// Es el contrato de junta hecho geometría: la vecina pondrá la suya en el mismo punto porque el
    /// sorteo de puertas es función pura del borde. Van primero para que nada pueda estar ya en su
    /// sitio — el cumplimiento de una puerta no puede depender de si queda hueco.
    pub anchors: Vec<Wg3Anchor>,
}

/// ADR-096 — una pieza plantada de antemano, con una boca que NO se toca.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Wg3Anchor {
    pub piece: u16,
    pub rotation: u8,
    pub origin_x: f32,
    pub origin_z: f32,
    /// La boca que da al otro lado de la junta. Se marca CONECTADA de salida: no entra en la
    /// frontera y la pasada final no la tapona.
    ///
    /// Marcarla conectada NO es optimismo: el tramo de puerta es siempre la misma pieza estrecha,
    /// va antes que nada y las puertas guardan distancia entre sí, así que la vecina la pone sí o
    /// sí. Dejarla abierta sería peor de lo que parece — las dos regiones la sellarían y la puerta
    /// quedaría tapiada por las dos caras.
    pub connected_socket: usize,
}

impl Default for Wg3ComposerSettings {
    fn default() -> Self {
        Self {
            budget: 30,
            deliberate_cap_chance: 0.05,
            cap_grace_count: 3,
            scale_exact_bonus: 4.2,
            scale_near_bonus: 1.0,
            scale_far_bonus: 0.22,
            repeat_parent_penalty: 0.18,
            repeat_grandparent_penalty: 0.45,
            close_loops: false,
            bounds: None,
            seed_at: None,
            route: None,
            overlap_slack_m: 0.0,
            collect_absorption_hits: false,
            anchors: Vec::new(),
        }
    }
}

/// Una pieza colocada, con lo que el recorrido sabe de ella.
///
/// `placement` es el dato que viaja (índice, giro, esquina en centímetros); `depth` y `parent` son
/// del recorrido y no cruzan el wire. Van aparte y no dentro de `Wg3Placement` justo por eso: lo que
/// se manda al cliente no debe engordar con la contabilidad del generador.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Wg3Composed {
    pub placement: Wg3Placement,
    /// Profundidad de rama desde la pieza semilla.
    pub depth: i32,
    /// Índice de la pieza a la que se enganchó, o `None` si es la semilla.
    pub parent: Option<usize>,
}

/// Una boca que quedó sin pareja y hubo que sellar. Un socket sin usar NO se deja abierto: sin tapón,
/// "no usar todos los sockets" y "conectividad por construcción" se contradicen y el mundo acaba con
/// agujeros al vacío.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Wg3Cap {
    pub x: f32,
    pub z: f32,
    pub side: u8,
    pub width: f32,
    /// Discriminante de `Wg3SocketType`.
    pub kind: u8,
    /// `true` si se selló por falta de candidata; `false` si fue decisión de composición.
    pub forced: bool,
}

/// SONDA — un choque de huella que HOY se descarta y que la absorción convertiría en conexión.
///
/// La idea que mide: un pasillo que topa con una sala no se descarta, se recorta contra ella, le
/// abre un vano y deja de expandirse — la sala manda sobre el pasillo. Cada rechazo por solape es
/// un sitio donde el mundo QUISO crecer, y hoy no queda constancia de dónde.
#[derive(Debug, Clone, Copy)]
pub struct Wg3AbsorptionHit {
    /// Nodo ya colocado contra el que se choca: quien absorbería.
    pub hit_node: usize,
    /// Pieza candidata que venía: quien sería absorbido.
    pub candidate_piece: u16,
    /// Fachada de contacto, PERPENDICULAR a la dirección de llegada. Es lo que decide si cabe un
    /// vano: por debajo del ancho de una puerta el choque no sirve de conexión.
    pub frontage_m: f32,
    /// Cuánto se metería la candidata dentro de la otra, o sea lo que habría que recortarle.
    pub depth_m: f32,
}

/// Resultado de una composición.
#[derive(Debug, Clone, Default)]
pub struct Wg3ComposedWorld {
    pub world_seed: i32,
    pub placements: Vec<Wg3Composed>,
    pub caps: Vec<Wg3Cap>,

    /// SONDA — vacío salvo con `collect_absorption_hits`. Ver [`Wg3AbsorptionHit`].
    pub absorption_hits: Vec<Wg3AbsorptionHit>,

    /// Candidatas descartadas porque la huella pisaba algo ya colocado. No es un error: es la medida
    /// de cuánto aprieta el mundo. Un cero sostenido significa que el catálogo es demasiado pequeño
    /// para llenar el espacio.
    pub rejected_by_overlap: u32,
    /// Candidatas descartadas por anchura o cota con el tipo ya coincidiendo. Un número alto delata un
    /// catálogo con bocas que casi casan — falta una transición.
    pub rejected_by_validator: u32,
    /// Bocas selladas por no haber ninguna candidata viable.
    pub forced_caps: u32,

    /// ADR-096 — candidatas descartadas por asomar fuera de la región. Es la medida de cuánto
    /// aprieta el borde: si crece mucho respecto a `rejected_by_overlap`, la región va pequeña para
    /// el catálogo.
    pub rejected_by_bounds: u32,

    /// ADR-096 — bucles cerrados: veces que dos bocas abiertas se unieron entre sí en vez de abrir
    /// rama nueva. Cero con `close_loops` apagado; con él encendido es la medida de cuánto deja de
    /// ser un árbol el mundo, y de dónde sale el tamaño de región.
    pub loops_closed: u32,

    /// ADR-098 — la geometría generada: los conectores, tramo a tramo, en el orden en que se
    /// tendieron. Vacío con el enrutador apagado.
    pub segments: Vec<Wg3Segment>,

    /// Conectores tendidos, y de ellos cuántos unieron dos islas. La diferencia son anillos.
    pub connectors: u32,
    pub connectors_joining_islands: u32,
    /// De ellos, cuantos se engancharon a MITAD de otro conector en vez de a una boca.
    pub taps: u32,

    /// Parejas de bocas que el enrutador tuvo que descartar. **Es la mitad útil del resultado**: no
    /// son errores, son la lista de lo que falta —transiciones de cota, de anchura— y de cuánto
    /// aprieta el mundo ya colocado.
    pub rejected_by_cota: u32,
    pub rejected_by_width: u32,
    pub rejected_by_kind: u32,
    pub rejected_by_route_geometry: u32,

    /// Bocas y parejas que llegó a mirar el enrutador. Un cero de conectores con cero parejas es un
    /// problema distinto de un cero con doscientas.
    pub route_mouths: u32,
    pub route_pairs: u32,
    pub route_unused_mouths: u32,
    pub route_components_left: u32,
    /// Bocas que el enrutador no pudo enganchar, con su sitio. Es lo que se mira cuando una region
    /// se queda en islas.
    pub route_leftover: Vec<(i32, i32, u8, i32)>,
}

/// Compone el mundo de una semilla. Función pura: mismo manifiesto y mismos ajustes ⇒ mismo mundo,
/// sin estado de proceso de por medio (R3).
pub fn compose(
    world_seed: i32,
    manifest: &Wg3Manifest,
    settings: &Wg3ComposerSettings,
) -> Wg3ComposedWorld {
    let mut composer = Composer::new(world_seed, manifest, settings);
    composer.run();
    composer.finish()
}

/// Una pieza colocada mientras se compone.
///
/// EN METROS Y EN COMA FLOTANTE, no en centímetros. El origen de una hija sale del punto de mundo de
/// la boca de su madre, así que redondear en cada paso metería un error que se arrastra por la cadena
/// entera. Se compone en `f32` —como C#— y se redondea UNA VEZ al emitir.
struct Node {
    piece: u16,
    rotation: u8,
    origin_x: f32,
    origin_z: f32,
    /// ADR-097 — cota del suelo de la pieza. La propaga el compositor: la semilla va a 0 y cada
    /// hija se coloca a la altura que hace coincidir su boca con la del padre.
    origin_y: f32,
    depth: i32,
    parent: Option<usize>,
    socket_state: Vec<u8>,
}

#[derive(Clone, Copy)]
struct Candidate {
    piece: u16,
    socket_index: usize,
    rotation: u8,
    origin_x: f32,
    origin_z: f32,
    weight: f32,
}

struct Composer<'a> {
    world_seed: i32,
    manifest: &'a Wg3Manifest,
    settings: &'a Wg3ComposerSettings,
    nodes: Vec<Node>,
    caps: Vec<Wg3Cap>,
    candidates: Vec<Candidate>,
    rejected_by_overlap: u32,
    rejected_by_validator: u32,
    forced_caps: u32,
    loops_closed: u32,
    rejected_by_bounds: u32,
    /// SONDA — ver [`Wg3AbsorptionHit`]. Vacío salvo con `collect_absorption_hits`.
    absorption_hits: Vec<Wg3AbsorptionHit>,

    /// ADR-098 — la geometría generada y el grafo que hace falta para decidir dónde tenderla.
    ///
    /// `edges` se lleva aparte de `parent` porque el mundo deja de ser un árbol en cuanto hay un
    /// bucle o un conector: con solo el padre no se puede saber si dos piezas ya están unidas, que
    /// es justo la pregunta que separa «unir una isla» de «cerrar un anillo».
    segments: Vec<Wg3Segment>,
    edges: Vec<(usize, usize)>,
    connectors: u32,
    connectors_joining_islands: u32,
    taps: u32,
    rejected_by_cota: u32,
    rejected_by_width: u32,
    rejected_by_kind: u32,
    rejected_by_route_geometry: u32,
    route_mouths: u32,
    route_pairs: u32,
    route_unused_mouths: u32,
    route_components_left: u32,
    route_leftover: Vec<(i32, i32, u8, i32)>,
}

impl<'a> Composer<'a> {
    fn new(world_seed: i32, manifest: &'a Wg3Manifest, settings: &'a Wg3ComposerSettings) -> Self {
        Self {
            world_seed,
            manifest,
            settings,
            nodes: Vec::new(),
            caps: Vec::new(),
            candidates: Vec::with_capacity(64),
            loops_closed: 0,
            rejected_by_bounds: 0,
            rejected_by_overlap: 0,
            rejected_by_validator: 0,
            forced_caps: 0,
            absorption_hits: Vec::new(),
            segments: Vec::new(),
            edges: Vec::new(),
            connectors: 0,
            connectors_joining_islands: 0,
            taps: 0,
            rejected_by_cota: 0,
            rejected_by_width: 0,
            rejected_by_kind: 0,
            rejected_by_route_geometry: 0,
            route_mouths: 0,
            route_pairs: 0,
            route_unused_mouths: 0,
            route_components_left: 0,
            route_leftover: Vec::new(),
        }
    }

    fn run(&mut self) {
        let Some(seed_piece) = self.manifest.pieces.first() else {
            return;
        };

        // La semilla es la PRIMERA pieza del catálogo. Elegirla por sorteo haría que cambiar el
        // catálogo moviera mundos ya generados.
        //
        // ADR-096 — va al centro de la región cuando hay una. Sembrar siempre en el origen del
        // mundo y luego acotar daría regiones VACÍAS en todas partes menos en el centro: la primera
        // pieza ya caería fuera de su caja.
        let (seed_x, seed_z) = match (self.settings.seed_at, self.settings.bounds) {
            (Some(at), _) => at,
            (None, Some((min_x, min_z, max_x, max_z))) => {
                ((min_x + max_x) * 0.5, (min_z + max_z) * 0.5)
            }
            (None, None) => (0.0, 0.0),
        };
        // ADR-096 — las anclas de junta van LAS PRIMERAS, antes que la semilla del centro. Es lo
        // que garantiza que una puerta acordada se cumpla: si fueran después, la semilla podría
        // estar en su sitio y el tramo no cabría, que es justo el caso que no puede existir.
        for anchor in &self.settings.anchors {
            // Las anclas van a cota 0: la junta es un acuerdo entre dos regiones que se compone
            // cada una por su lado, y una cota propagada no llegaría igual a los dos. Cuando el
            // desnivel cruce juntas habrá que meter la cota en el contrato, no deducirla.
            let node = self.place(
                anchor.piece,
                anchor.rotation,
                anchor.origin_x,
                anchor.origin_z,
                0.0,
                0,
                None,
            );
            if anchor.connected_socket < self.nodes[node].socket_state.len() {
                self.nodes[node].socket_state[anchor.connected_socket] = SOCKET_CONNECTED;
            }
        }

        // La semilla del centro solo si cabe: con anclas puestas puede que su sitio esté ocupado, y
        // pisarlas sería meter el solape que todo lo demás evita. Una región sin semilla central
        // sigue creciendo desde sus puertas, que es de donde tiene que crecer.
        let seed_ox = seed_x - seed_piece.size_x * 0.5;
        let seed_oz = seed_z - seed_piece.size_z * 0.5;
        let seed_fits = !overlaps_any(
            &self.nodes,
            self.manifest,
            seed_ox,
            seed_oz,
            seed_piece.size_x,
            seed_piece.size_z,
            self.settings.overlap_slack_m,
        ) && match self.settings.bounds {
            Some((bmin_x, bmin_z, bmax_x, bmax_z)) => {
                seed_ox >= bmin_x
                    && seed_oz >= bmin_z
                    && seed_ox + seed_piece.size_x <= bmax_x
                    && seed_oz + seed_piece.size_z <= bmax_z
            }
            None => true,
        };
        let seed_node = if seed_fits {
            Some(self.place(seed_piece.index, 0, seed_ox, seed_oz, 0.0, 0, None))
        } else {
            None
        };

        // **LAS ANCLAS DE JUNTA NO SE RAMIFICAN, Y ESTA ES LA LÍNEA.**
        //
        // Antes la frontera salía de TODOS los nodos, anclas incluidas, así que cada puerta crecía
        // su propio árbol. Dos árboles no se unen jamás —unirlos ES cerrar un bucle— y el resultado
        // medido era `islas == puertas + 1` en las cuatro regiones probadas, con el árbol mayor
        // reuniendo solo el 26-36 % de las piezas. Andando eso se siente como «llega un punto que se
        // cierra y no hay manera de moverte»: no es que se cierre, es que te tocó una isla.
        //
        // Creciendo solo desde la semilla, la región es UN mundo. **El precio, declarado:** el tramo
        // de cada puerta queda como bolsillo de una pieza hasta que exista un router que lleve el
        // árbol hasta él, así que cruzar una junta lleva a un armario. ADR-096 sigue siendo cierto
        // —la junta se cruza— pero deja de llevar a algún sitio. Se probó antes la vía barata:
        // `close_loops` encendido da EXACTAMENTE las mismas islas, porque para engancharse dos bocas
        // tienen que coincidir clavadas y la desviación es lateral y acumulada.
        //
        // **ADR-098 lo probó a levantar, y la medida dijo que no.** Dejar ramificar las anclas —lo
        // que ahora podría hacerse, porque el enrutador SÍ une dos árboles— llena mucho más la
        // región: de 28 piezas a 52. Pero las llena tanto que el enrutador se queda sin hueco por
        // donde pasar, y el resultado medido fue peor: de 2 islas a 6, con el árbol mayor bajando
        // del 86 % al 27 %. El llenado y la conectividad tiran en direcciones opuestas mientras el
        // catálogo tenga pocas bocas por pieza, y entre las dos manda poder andar.
        let mut frontier: Vec<(usize, usize)> = Vec::new();
        match seed_node {
            Some(node) => push_sockets(&mut frontier, &self.nodes, node),
            // Sin semilla —su sitio lo ocupaba un ancla— las puertas son lo único que hay, así que
            // ahí sí crecen: una región vacía es peor que una región en islas.
            None => {
                for node in 0..self.nodes.len() {
                    push_sockets(&mut frontier, &self.nodes, node);
                }
            }
        }

        let mut cursor = 0usize;
        while cursor < frontier.len() && self.nodes.len() < self.settings.budget {
            let (pi, si) = frontier[cursor];
            cursor += 1;
            if self.nodes[pi].socket_state[si] != SOCKET_OPEN {
                continue;
            }

            let parent_piece = self.piece_of(pi);
            let parent_socket = parent_piece.sockets[si].clone();
            let (px, pz) = world_socket_point(&self.nodes[pi], parent_piece, si);
            let parent_world_side = (parent_socket.side + self.nodes[pi].rotation) % 4;
            let needed_side = (parent_world_side + 2) % 4;
            let child_depth = self.nodes[pi].depth + 1;

            // ADR-096 — antes que nada, mirar si esta boca ya tiene con quién casar entre lo puesto.
            // Va PRIMERO, delante del tapón deliberado: sellar una boca que podía cerrar un anillo
            // es perder el anillo, y los anillos son lo escaso. Las paredes ciegas, no.
            if self.settings.close_loops
                && self.try_close_loop(pi, si, px, pz, needed_side, &parent_socket)
            {
                continue;
            }

            // A veces la boca se sella aunque hubiera con qué seguir. Es lo que produce paredes ciegas
            // y espacios residuales; sin ello el mundo se lee como un árbol de pasillos.
            if self.nodes.len() > self.settings.cap_grace_count
                && self.settings.deliberate_cap_chance > 0.0
            {
                let mut cap_stream = hash::stream_at(self.world_seed, px, pz, SALT_CAP);
                if cap_stream.next01() < self.settings.deliberate_cap_chance {
                    if self.settings.route.is_some() {
                        // ADR-098 — decidido cerrar, pero la geometría espera: hasta que el
                        // enrutador mire, esta boca todavía puede ser la que una una isla.
                        self.nodes[pi].socket_state[si] = SOCKET_PENDING_CAP;
                    } else if !self.seal_mouth(pi, si, px, pz, parent_world_side, &parent_socket) {
                        self.cap(pi, si, px, pz, parent_world_side, &parent_socket, false);
                    }
                    continue;
                }
            }

            self.collect_candidates(pi, &parent_socket, px, pz, needed_side, child_depth);

            if self.candidates.is_empty() {
                if self.settings.route.is_some() {
                    // Sin candidata de catálogo es cuando MÁS falta hace mirar si un conector la
                    // salva: ésta es la boca contra la que se choca andando.
                    self.nodes[pi].socket_state[si] = SOCKET_PENDING_CAP;
                } else if !self.seal_mouth(pi, si, px, pz, parent_world_side, &parent_socket) {
                    self.cap(pi, si, px, pz, parent_world_side, &parent_socket, true);
                    self.forced_caps += 1;
                }
                continue;
            }

            let mut pick_stream = hash::stream_at(self.world_seed, px, pz, SALT_PICK);
            let chosen = weighted_pick(&self.candidates, &mut pick_stream);

            // ADR-097 D2 — LA COTA SE PROPAGA. La boca del padre está a `origin_y + floor_y` en
            // el mundo; la hija se coloca a la altura que hace coincidir la suya. Con todas las
            // bocas a 0 esto es un no-op y el mundo sale plano; una pieza con bocas a cotas
            // distintas —una rampa— sube o baja todo lo que cuelgue de ella.
            let child_y = self.nodes[pi].origin_y + parent_socket.floor_y
                - self.manifest.pieces[chosen.piece as usize].sockets[chosen.socket_index].floor_y;

            let child = self.place(
                chosen.piece,
                chosen.rotation,
                chosen.origin_x,
                chosen.origin_z,
                child_y,
                child_depth,
                Some(pi),
            );
            self.nodes[pi].socket_state[si] = SOCKET_CONNECTED;
            self.nodes[child].socket_state[chosen.socket_index] = SOCKET_CONNECTED;
            self.edges.push((pi, child));
            push_sockets(&mut frontier, &self.nodes, child);
        }

        // ADR-098 — el enrutador va AQUÍ: después del árbol, para saber qué quedó suelto, y antes de
        // la pasada de tapones, porque sellar una boca que podía unir una isla es perder la isla.
        self.run_router();

        self.cap_everything_still_open();
    }

    /// ADR-098 — tiende conectores generados entre lo que quedó abierto.
    ///
    /// Aquí no hay geometría ni decisiones: se traduce el estado del compositor al vocabulario del
    /// enrutador —bocas, rectángulos ocupados, grafo—, se le deja decidir, y se aplica lo que
    /// devuelve. Que la traducción sea todo lo que hay es lo que permite probar el enrutador solo,
    /// sin componer un mundo entero.
    fn run_router(&mut self) {
        let Some(settings) = self.settings.route.clone() else {
            return;
        };

        let mut mouths: Vec<Mouth> = Vec::new();
        for i in 0..self.nodes.len() {
            let piece = self.piece_of(i);
            for s in 0..self.nodes[i].socket_state.len() {
                // Las pendientes de tapón entran: son la mayoría, y son justamente las bocas contra
                // las que se choca al andar. Las de los tramos de junta llegan como OPEN, porque las
                // anclas no ramifican.
                let state = self.nodes[i].socket_state[s];
                if state != SOCKET_OPEN && state != SOCKET_PENDING_CAP {
                    continue;
                }
                let socket = &piece.sockets[s];
                let (x, z) = world_socket_point(&self.nodes[i], piece, s);
                mouths.push(Mouth {
                    node: i,
                    socket: s,
                    x,
                    z,
                    side: (socket.side + self.nodes[i].rotation) % 4,
                    width: socket.width,
                    // La cota de MUNDO, que es la que tiene que casar: la local no dice nada desde
                    // que ADR-097 propaga la altura por el árbol.
                    floor_y: self.nodes[i].origin_y + socket.floor_y,
                    clear_height: socket.ceiling_y - socket.floor_y,
                    kind: socket.kind,
                });
            }
        }

        let mut occupancy: Vec<Rect> = self
            .nodes
            .iter()
            .map(|n| {
                let piece = &self.manifest.pieces[n.piece as usize];
                let (w, d) = if n.rotation.is_multiple_of(2) {
                    (piece.size_x, piece.size_z)
                } else {
                    (piece.size_z, piece.size_x)
                };
                Rect {
                    min_x: n.origin_x,
                    min_z: n.origin_z,
                    max_x: n.origin_x + w,
                    max_z: n.origin_z + d,
                }
            })
            .collect();
        occupancy.extend(self.segments.iter().map(|c| {
            let (min_x, min_z, max_x, max_z) = c.bounds();
            Rect {
                min_x,
                min_z,
                max_x,
                max_z,
            }
        }));

        let mut adjacency: Vec<Vec<usize>> = vec![Vec::new(); self.nodes.len()];
        for &(a, b) in &self.edges {
            adjacency[a].push(b);
            adjacency[b].push(a);
        }

        let outcome = route::route(
            &mouths,
            &occupancy,
            self.settings.bounds,
            self.nodes.len(),
            &adjacency,
            &settings,
        );

        // Las bocas que el enrutador usó quedan CONECTADAS: tienen geometría al otro lado, y la
        // pasada de tapones no debe volver a mirarlas.
        for &m in &outcome.used_mouths {
            let mouth = &mouths[m];
            self.nodes[mouth.node].socket_state[mouth.socket] = SOCKET_CONNECTED;
        }
        self.edges.extend(outcome.edges.iter().copied());
        self.segments.extend(outcome.segments.iter().cloned());
        self.connectors += outcome.connectors;
        self.connectors_joining_islands += outcome.connectors_joining_islands;
        self.taps += outcome.taps;

        self.rejected_by_cota += outcome.rejected_by_cota;
        self.rejected_by_width += outcome.rejected_by_width;
        self.rejected_by_kind += outcome.rejected_by_kind;
        self.rejected_by_route_geometry += outcome.rejected_by_geometry;
        self.route_mouths = outcome.mouths;
        self.route_pairs = outcome.pairs;
        self.route_unused_mouths = outcome.unused_mouths;
        self.route_components_left = outcome.components_left;
        self.route_leftover = outcome.leftover.clone();

        self.absorb_mouths_behind_connector_walls();
    }

    /// Una boca que da contra la PARED de un conector ya está cerrada, y hay que decirlo.
    ///
    /// Si no, la pasada de tapones intenta plantarle una pieza, choca contra la tramo y la anota como
    /// tapón forzado — que es la cuenta con la que se vigila que no haya bocas al vacío. El agujero
    /// no existe: detrás de esa pared hay suelo de conector. Callarlo dejaría la sonda mintiendo en
    /// la dirección peligrosa, que es la que hace ignorar un aviso de verdad.
    fn absorb_mouths_behind_connector_walls(&mut self) {
        if self.segments.is_empty() {
            return;
        }
        for i in 0..self.nodes.len() {
            for s in 0..self.nodes[i].socket_state.len() {
                let state = self.nodes[i].socket_state[s];
                if state != SOCKET_OPEN && state != SOCKET_PENDING_CAP {
                    continue;
                }
                let piece = self.piece_of(i);
                let (x, z) = world_socket_point(&self.nodes[i], piece, s);
                if self.segments.iter().any(|c| on_segment_wall(c, x, z)) {
                    self.nodes[i].socket_state[s] = SOCKET_CAPPED;
                }
            }
        }
    }

    /// Presupuesto agotado o frontera sin recorrer: todo lo que quede abierto se sella. Sin esta
    /// pasada el mundo termina en bocas que dan a la nada.
    fn cap_everything_still_open(&mut self) {
        for i in 0..self.nodes.len() {
            for s in 0..self.nodes[i].socket_state.len() {
                // ADR-098 — aquí caen las dos: las que nadie tocó y las que el recorrido dejó
                // pendientes de tapón para que el enrutador pudiera mirarlas primero.
                let state = self.nodes[i].socket_state[s];
                if state != SOCKET_OPEN && state != SOCKET_PENDING_CAP {
                    continue;
                }
                let piece = self.piece_of(i);
                let socket = piece.sockets[s].clone();
                let (x, z) = world_socket_point(&self.nodes[i], piece, s);
                let side = (socket.side + self.nodes[i].rotation) % 4;
                if !self.seal_mouth(i, s, x, z, side, &socket) {
                    self.cap(i, s, x, z, side, &socket, true);
                    self.forced_caps += 1;
                }
            }
        }
    }

    /// Sella una boca CON GEOMETRÍA, y solo apunta una ficha si no hay con qué.
    ///
    /// EL FALLO QUE ARREGLA: `cap` marcaba el socket y añadía un registro que no consumía NADIE —ni
    /// el ráster de colisión, ni el wire, ni el cliente—. La boca quedaba abierta con el vacío
    /// detrás y el jugador se caía del mundo. Medido antes de arreglarlo: una de cada seis bocas del
    /// mundo SERVIDO no tenía suelo al otro lado.
    ///
    /// LA REGLA DE ELECCIÓN es idéntica a la de `Wg3Composer.SealMouth` en C#, y tiene que serlo o
    /// el oráculo se pone rojo: entre las piezas de UNA SOLA boca que casan con ésta y CABEN, la de
    /// menor huella; a igualdad, la de menor índice. Menor huella porque un tapón grande choca
    /// contra lo ya colocado justo donde hacía falta cerrar.
    ///
    /// Respeta `bounds` como todo lo demás: un tapón que asome fuera de su región pisaría lo que
    /// compone la vecina, que no sabe de él. Eso deja sin sellar algunas bocas del BORDE, y es una
    /// deuda declarada — la cuenta `probe_open_mouths_in_the_served_world`.
    fn seal_mouth(
        &mut self,
        parent_index: usize,
        socket_index: usize,
        px: f32,
        pz: f32,
        parent_world_side: u8,
        parent_socket: &Wg3Socket,
    ) -> bool {
        let needed_side = (parent_world_side + 2) % 4;
        let manifest = self.manifest;

        let mut best: Option<(u16, u8, f32, f32, f32)> = None;
        for piece in &manifest.pieces {
            if piece.sockets.len() != 1 {
                continue;
            }
            let socket = &piece.sockets[0];
            if socket.kind != parent_socket.kind || !connection_ok(parent_socket, socket) {
                continue;
            }

            let rotation = (needed_side + 4 - socket.side % 4) % 4;
            let (w, d) = if rotation.is_multiple_of(2) {
                (piece.size_x, piece.size_z)
            } else {
                (piece.size_z, piece.size_x)
            };
            let (lx, lz) = local_point(needed_side, socket.offset, w, d);
            let (ox, oz) = (px - lx, pz - lz);

            if let Some((bmin_x, bmin_z, bmax_x, bmax_z)) = self.settings.bounds {
                if ox < bmin_x || oz < bmin_z || ox + w > bmax_x || oz + d > bmax_z {
                    continue;
                }
            }
            if overlaps_any(
                &self.nodes,
                manifest,
                ox,
                oz,
                w,
                d,
                self.settings.overlap_slack_m,
            ) {
                continue;
            }
            // ADR-098 — y tampoco encima de un conector. La pasada final de tapones corre DESPUÉS
            // del enrutador, así que sin esto el tapón se plantaría dentro de un pasillo generado:
            // una pared en mitad de la ruta que acaba de abrirse.
            if overlaps_segments(&self.segments, ox, oz, w, d) {
                continue;
            }

            let area = w * d;
            if best.is_none_or(|b| area < b.4) {
                best = Some((piece.index, rotation, ox, oz, area));
            }
        }

        let Some((piece, rotation, ox, oz, _)) = best else {
            return false;
        };
        let depth = self.nodes[parent_index].depth + 1;
        let cap_y = self.nodes[parent_index].origin_y + parent_socket.floor_y
            - self.manifest.pieces[piece as usize].sockets[0].floor_y;
        let child = self.place(piece, rotation, ox, oz, cap_y, depth, Some(parent_index));
        self.nodes[parent_index].socket_state[socket_index] = SOCKET_CONNECTED;
        self.nodes[child].socket_state[0] = SOCKET_CONNECTED;
        self.edges.push((parent_index, child));
        true
    }

    /// Las candidatas que casan con una boca, ya situadas y pesadas.
    ///
    /// EL GIRO NO SE BUSCA: queda determinado. La boca hija tiene que acabar mirando a `needed_side`
    /// y girar suma al lado sin tocar el offset, así que hay exactamente una rotación válida por boca
    /// candidata. Probar las cuatro sería tirar tres.
    fn collect_candidates(
        &mut self,
        parent_index: usize,
        parent_socket: &Wg3Socket,
        px: f32,
        pz: f32,
        needed_side: u8,
        child_depth: i32,
    ) {
        self.candidates.clear();

        let manifest = self.manifest;
        let parent_id = self.piece_of(parent_index).id.as_str();
        let grandparent_id = self.nodes[parent_index]
            .parent
            .map(|gp| self.piece_of(gp).id.as_str());

        for piece in &manifest.pieces {
            if child_depth < piece.min_depth {
                continue;
            }

            for (s, socket) in piece.sockets.iter().enumerate() {
                // El tipo distinto es lo normal y no se cuenta: sería contar todo el catálogo en cada
                // boca. Lo que interesa medir es la boca que CASI casa —mismo tipo, otra anchura o
                // cota— porque eso delata que falta una transición.
                if socket.kind != parent_socket.kind {
                    continue;
                }
                if !connection_ok(parent_socket, socket) {
                    self.rejected_by_validator += 1;
                    continue;
                }

                let rotation = (needed_side + 4 - socket.side % 4) % 4;
                let (w, d) = if rotation.is_multiple_of(2) {
                    (piece.size_x, piece.size_z)
                } else {
                    (piece.size_z, piece.size_x)
                };
                let (lx, lz) = local_point(needed_side, socket.offset, w, d);
                let ox = px - lx;
                let oz = pz - lz;

                // ADR-096 — fuera de la región no se coloca. Va ANTES del solape porque es más
                // barato y porque una pieza que asoma ya está descartada aunque no pise nada.
                if let Some((bmin_x, bmin_z, bmax_x, bmax_z)) = self.settings.bounds {
                    if ox < bmin_x || oz < bmin_z || ox + w > bmax_x || oz + d > bmax_z {
                        self.rejected_by_bounds += 1;
                        continue;
                    }
                }

                if let Some(hit) = first_overlap(
                    &self.nodes,
                    manifest,
                    ox,
                    oz,
                    w,
                    d,
                    self.settings.overlap_slack_m,
                ) {
                    self.rejected_by_overlap += 1;
                    if self.settings.collect_absorption_hits {
                        let n = &self.nodes[hit];
                        let hp = &manifest.pieces[n.piece as usize];
                        let (nw, nd) = if n.rotation.is_multiple_of(2) {
                            (hp.size_x, hp.size_z)
                        } else {
                            (hp.size_z, hp.size_x)
                        };
                        let ix = (n.origin_x + nw).min(ox + w) - n.origin_x.max(ox);
                        let iz = (n.origin_z + nd).min(oz + d) - n.origin_z.max(oz);
                        // Se llega por `needed_side`: pares (0/2) avanzan en Z, impares en X. La
                        // fachada del vano es la del OTRO eje — el ancho del pasillo que llega,
                        // no lo que se ha metido dentro.
                        let (frontage_m, depth_m) = if needed_side.is_multiple_of(2) {
                            (ix, iz)
                        } else {
                            (iz, ix)
                        };
                        self.absorption_hits.push(Wg3AbsorptionHit {
                            hit_node: hit,
                            candidate_piece: piece.index,
                            frontage_m,
                            depth_m,
                        });
                    }
                    continue;
                }

                self.candidates.push(Candidate {
                    piece: piece.index,
                    socket_index: s,
                    rotation,
                    origin_x: ox,
                    origin_z: oz,
                    weight: weigh(
                        self.world_seed,
                        self.settings,
                        piece,
                        ox + w * 0.5,
                        oz + d * 0.5,
                        parent_id,
                        grandparent_id,
                    ),
                });
            }
        }
    }

    fn piece_of(&self, node: usize) -> &'a Wg3Piece {
        &self.manifest.pieces[self.nodes[node].piece as usize]
    }

    /// ADR-096 — busca otra boca ABIERTA en el mismo punto de mundo, enfrentada y compatible, y las
    /// une. Devuelve `true` si cerró un bucle.
    ///
    /// # Se compara en CENTÍMETROS ENTEROS, no con epsilon
    ///
    /// Las dos bocas llegaron a ese punto por cadenas de sumas distintas, así que en `f32` sus
    /// coordenadas casi nunca son idénticas bit a bit. Cuantizar al centímetro —la misma resolución
    /// que viaja por el wire y la que el ráster de 0,5 m puede distinguir— convierte «casi igual» en
    /// una comparación de enteros, que además es reproducible: un `abs() < eps` haría que el mundo
    /// dependiera del orden en que se acumularon los errores.
    ///
    /// # Barrido lineal, y es a propósito
    ///
    /// Un índice por punto sería más rápido, pero un `HashMap` recorre en orden no determinista y
    /// aquí puede haber más de una candidata: elegir «la que salga» haría que el mundo cambiara
    /// entre ejecuciones sin que cambie nada más. El barrido devuelve SIEMPRE la primera en orden
    /// (nodo, boca), que es un criterio estable. A 300 piezas son unas decenas de miles de
    /// comparaciones de enteros por mundo: gratis.
    fn try_close_loop(
        &mut self,
        node: usize,
        socket: usize,
        px: f32,
        pz: f32,
        needed_side: u8,
        parent_socket: &Wg3Socket,
    ) -> bool {
        let key = (quantize_cm(px), quantize_cm(pz));

        for other in 0..self.nodes.len() {
            if other == node {
                continue;
            }
            let other_piece = self.piece_of(other);
            for os in 0..self.nodes[other].socket_state.len() {
                if self.nodes[other].socket_state[os] != SOCKET_OPEN {
                    continue;
                }
                let other_socket = &other_piece.sockets[os];
                if (other_socket.side + self.nodes[other].rotation) % 4 != needed_side {
                    continue;
                }
                let (ox, oz) = world_socket_point(&self.nodes[other], other_piece, os);
                if (quantize_cm(ox), quantize_cm(oz)) != key {
                    continue;
                }
                if !connection_ok(parent_socket, other_socket) {
                    continue;
                }

                self.nodes[node].socket_state[socket] = SOCKET_CONNECTED;
                self.nodes[other].socket_state[os] = SOCKET_CONNECTED;
                self.edges.push((node, other));
                self.loops_closed += 1;
                return true;
            }
        }
        false
    }

    #[allow(clippy::too_many_arguments)]
    fn place(
        &mut self,
        piece: u16,
        rotation: u8,
        origin_x: f32,
        origin_z: f32,
        origin_y: f32,
        depth: i32,
        parent: Option<usize>,
    ) -> usize {
        let sockets = self.manifest.pieces[piece as usize].sockets.len();
        self.nodes.push(Node {
            piece,
            rotation,
            origin_x,
            origin_z,
            origin_y,
            depth,
            parent,
            socket_state: vec![SOCKET_OPEN; sockets],
        });
        self.nodes.len() - 1
    }

    #[allow(clippy::too_many_arguments)]
    fn cap(
        &mut self,
        node: usize,
        socket_index: usize,
        x: f32,
        z: f32,
        world_side: u8,
        socket: &Wg3Socket,
        forced: bool,
    ) {
        self.nodes[node].socket_state[socket_index] = SOCKET_CAPPED;
        self.caps.push(Wg3Cap {
            x,
            z,
            side: world_side,
            width: socket.width,
            kind: socket.kind,
            forced,
        });
    }

    fn finish(self) -> Wg3ComposedWorld {
        let placements = self
            .nodes
            .iter()
            .map(|n| Wg3Composed {
                placement: Wg3Placement {
                    piece: n.piece,
                    rotation: n.rotation,
                    origin_x_cm: to_centimetres(n.origin_x),
                    origin_z_cm: to_centimetres(n.origin_z),
                    origin_y_cm: to_centimetres(n.origin_y),
                },
                depth: n.depth,
                parent: n.parent,
            })
            .collect();

        Wg3ComposedWorld {
            world_seed: self.world_seed,
            placements,
            caps: self.caps,
            absorption_hits: self.absorption_hits,
            rejected_by_overlap: self.rejected_by_overlap,
            rejected_by_validator: self.rejected_by_validator,
            forced_caps: self.forced_caps,
            loops_closed: self.loops_closed,
            rejected_by_bounds: self.rejected_by_bounds,
            segments: self.segments,
            connectors: self.connectors,
            connectors_joining_islands: self.connectors_joining_islands,
            taps: self.taps,
            rejected_by_cota: self.rejected_by_cota,
            rejected_by_width: self.rejected_by_width,
            rejected_by_kind: self.rejected_by_kind,
            rejected_by_route_geometry: self.rejected_by_route_geometry,
            route_mouths: self.route_mouths,
            route_pairs: self.route_pairs,
            route_unused_mouths: self.route_unused_mouths,
            route_components_left: self.route_components_left,
            route_leftover: self.route_leftover,
        }
    }
}

/// ¿Pisa este rectángulo alguna tramo generada? Mismo epsilon que el solape entre piezas: tocarse es
/// correcto, penetrar no.
fn overlaps_segments(segments: &[Wg3Segment], x: f32, z: f32, w: f32, d: f32) -> bool {
    segments.iter().any(|c| {
        let (min_x, min_z, max_x, max_z) = c.bounds();
        min_x < x + w - OVERLAP_EPS
            && max_x - OVERLAP_EPS > x
            && min_z < z + d - OVERLAP_EPS
            && max_z - OVERLAP_EPS > z
    })
}

/// ¿Cae este punto sobre una PARED de esta tramo, y no sobre una de sus bocas?
///
/// Se usa para dar por cerrada una boca que da contra un conector. La distinción importa: contra la
/// pared, la boca está sellada y detrás hay suelo; contra una boca del conector, estaría abierta a
/// él, y decir que está tapiada sería mentir en la dirección contraria.
fn on_segment_wall(cell: &Wg3Segment, x: f32, z: f32) -> bool {
    const EPS: f32 = 0.02;
    let (min_x, min_z, max_x, max_z) = cell.bounds();
    if x < min_x - EPS || x > max_x + EPS || z < min_z - EPS || z > max_z + EPS {
        return false;
    }
    let on_edge = (x - min_x).abs() < EPS
        || (x - max_x).abs() < EPS
        || (z - min_z).abs() < EPS
        || (z - max_z).abs() < EPS;
    if !on_edge {
        return false;
    }

    for o in &cell.openings {
        let (w, d) = (cell.size_x(), cell.size_z());
        let half = o.width_cm as f32 / 200.0;
        let offset = o.offset_cm as f32 / 100.0;
        let (lo, hi) = (offset - half, offset + half);
        let (lx, lz) = (x - min_x, z - min_z);
        let inside = match o.side % 4 {
            0 => (d - lz).abs() < EPS && lx >= lo - EPS && lx <= hi + EPS,
            1 => (w - lx).abs() < EPS && (d - lz) >= lo - EPS && (d - lz) <= hi + EPS,
            2 => lz.abs() < EPS && (w - lx) >= lo - EPS && (w - lx) <= hi + EPS,
            _ => lx.abs() < EPS && lz >= lo - EPS && lz <= hi + EPS,
        };
        if inside {
            return false;
        }
    }
    true
}

/// ¿Casan estas dos bocas? Espejo de `Wg3Validator.ValidateConnection`, sin el motivo: aquí nadie lo
/// lee, y devolverlo obligaría a formatear una cadena por candidata descartada — que son decenas por
/// boca.
fn connection_ok(a: &Wg3Socket, b: &Wg3Socket) -> bool {
    // ADR-097 D3 — LA COTA YA NO SE COMPARA AQUÍ, y quitarla es lo que permite una rampa. Antes se
    // exigía misma altura LOCAL, o sea que una pieza con la salida más alta que la entrada no podía
    // engancharse a nada. Ahora la altura la resuelve el compositor colocando a la hija donde su
    // boca coincida con la del padre, así que casan en cota de MUNDO por construcción.
    //
    // El hueco caminable SÍ se sigue midiendo, y es lo que impide una conexión por la que no cabe
    // nadie. Se mide contra cada boca por separado porque ya no comparten cota.
    a.kind == b.kind
        && (a.width - b.width).abs() <= WIDTH_MATCH_TOLERANCE
        && a.ceiling_y - a.floor_y >= MIN_HEADROOM
        && b.ceiling_y - b.floor_y >= MIN_HEADROOM
}

/// Peso de una candidata: base × campo de escala × penalización de repetición.
///
/// El campo se lee en el CENTRO de donde caería la pieza, no en la boca — una nave de 40 m enganchada
/// al borde de una zona estrecha pertenece a donde va su masa.
fn weigh(
    world_seed: i32,
    settings: &Wg3ComposerSettings,
    piece: &Wg3Piece,
    centre_x: f32,
    centre_z: f32,
    parent_id: &str,
    grandparent_id: Option<&str>,
) -> f32 {
    let mut w = piece.weight;

    let target = scale::scale_at(world_seed, centre_x, centre_z);
    let distance = (piece.scale as i32 - target as i32).abs();
    if distance == 0 {
        w *= settings.scale_exact_bonus;
    } else if distance == 1 {
        w *= settings.scale_near_bonus;
    } else {
        w *= settings.scale_far_bonus;
    }

    // Se compara por ID y no por índice porque es lo que compara C#. Con ids únicos —que el validador
    // del horneado exige— son la misma condición; si algún día dejaran de serlo, el mundo tiene que
    // moverse igual a los dos lados.
    if piece.id == parent_id {
        w *= settings.repeat_parent_penalty;
    } else if Some(piece.id.as_str()) == grandparent_id {
        w *= settings.repeat_grandparent_penalty;
    }

    w.max(1e-6)
}

/// Sorteo por peso. La suma se hace en el MISMO orden en que se recogieron las candidatas: el orden
/// es parte del resultado, porque acumular en `f32` no es asociativo.
fn weighted_pick(candidates: &[Candidate], stream: &mut hash::Stream) -> Candidate {
    let mut total = 0.0f32;
    for c in candidates {
        total += c.weight;
    }

    let roll = stream.next01() * total;
    let mut acc = 0.0f32;
    for c in candidates {
        acc += c.weight;
        if roll <= acc {
            return *c;
        }
    }
    // Solo alcanzable por acumulación de error en coma flotante.
    candidates[candidates.len() - 1]
}

fn overlaps_any(
    nodes: &[Node],
    manifest: &Wg3Manifest,
    x: f32,
    z: f32,
    w: f32,
    d: f32,
    slack: f32,
) -> bool {
    first_overlap(nodes, manifest, x, z, w, d, slack).is_some()
}

/// `slack` en metros: cuánto se tolera que las cajas se pisen. Con `0.0` —el defecto y lo único que
/// sirve un mundo hoy— la condición es la de siempre, intersección de AABB con epsilon. Con holgura
/// se mide la PROFUNDIDAD de la intersección en cada eje y solo se rechaza si las dos pasan de
/// `slack`: dos piezas que se solapan el grosor de un muro comparten pared en vez de duplicarla.
///
/// Devuelve CUÁL se pisa y no solo si se pisa, porque el nodo contra el que se choca es el dato que
/// hace falta para medir la absorción: sin él, un rechazo es un número y no un sitio.
fn first_overlap(
    nodes: &[Node],
    manifest: &Wg3Manifest,
    x: f32,
    z: f32,
    w: f32,
    d: f32,
    slack: f32,
) -> Option<usize> {
    nodes.iter().position(|n| {
        let piece = &manifest.pieces[n.piece as usize];
        let (nw, nd) = if n.rotation.is_multiple_of(2) {
            (piece.size_x, piece.size_z)
        } else {
            (piece.size_z, piece.size_x)
        };
        if slack <= 0.0 {
            return n.origin_x < x + w - OVERLAP_EPS
                && n.origin_x + nw - OVERLAP_EPS > x
                && n.origin_z < z + d - OVERLAP_EPS
                && n.origin_z + nd - OVERLAP_EPS > z;
        }
        let depth_x = (n.origin_x + nw).min(x + w) - n.origin_x.max(x);
        let depth_z = (n.origin_z + nd).min(z + d) - n.origin_z.max(z);
        depth_x > slack && depth_z > slack
    })
}

/// Centímetros enteros para COMPARAR, no para emitir. Redondeo al más cercano, que es lo que hace
/// que dos bocas llegadas por cadenas de sumas distintas caigan en el mismo entero.
fn quantize_cm(v: f32) -> i32 {
    (v * 100.0).round() as i32
}

fn world_socket_point(node: &Node, piece: &Wg3Piece, index: usize) -> (f32, f32) {
    let (w, d) = if node.rotation.is_multiple_of(2) {
        (piece.size_x, piece.size_z)
    } else {
        (piece.size_z, piece.size_x)
    };
    let side = (piece.sockets[index].side + node.rotation) % 4;
    let (lx, lz) = local_point(side, piece.sockets[index].offset, w, d);
    (node.origin_x + lx, node.origin_z + lz)
}

fn push_sockets(frontier: &mut Vec<(usize, usize)>, nodes: &[Node], node: usize) {
    for (s, state) in nodes[node].socket_state.iter().enumerate() {
        if *state == SOCKET_OPEN {
            frontier.push((node, s));
        }
    }
}

/// Metros a centímetros con el MISMO redondeo que `Mathf.RoundToInt`: producto en `f32` y redondeo a
/// la par en los empates. Es el único sitio donde la composición deja la coma flotante, y por eso el
/// oráculo compara al centímetro y no bit a bit.
fn to_centimetres(v: f32) -> i32 {
    ((v * 100.0) as f64).round_ties_even() as i32
}
