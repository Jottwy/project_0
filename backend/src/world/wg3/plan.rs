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
        }
        out
    }
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
    let root = PlanRect {
        min_x_cm: (bounds.0 * CM_PER_M).round() as i32,
        min_z_cm: (bounds.1 * CM_PER_M).round() as i32,
        max_x_cm: (bounds.2 * CM_PER_M).round() as i32,
        max_z_cm: (bounds.3 * CM_PER_M).round() as i32,
    };

    let mut planner = Planner {
        seed,
        nodes: vec![Node {
            rect: root,
            depth: 0,
            children: None,
        }],
        spaces: Vec::new(),
        band_of_node: Vec::new(),
        links: Vec::new(),
    };
    planner.band_of_node.push(None);

    planner.subdivide();
    planner.emit_leaves();
    planner.assign_void();
    // Las puertas ANTES de enlazar: una puerta acordada con la vecina no se negocia, así que el
    // espacio que la abre no puede ser vacío y tiene que estar dentro del edificio. Decidirlo después
    // obligaría a rehacer el grafo.
    let gates = planner.attach_gates(gates);
    planner.link_all();
    planner.ensure_connected();
    planner.retag_dead_ends();

    RegionPlan {
        spaces: planner.spaces,
        links: planner.links,
        gates,
        bounds_cm: Some(root),
    }
}

struct Planner {
    seed: i32,
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
                let idx = self.push_space(band_rect, role, depth);
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
        Some((axis, base + at, band_cm))
    }

    /// Las hojas del árbol pasan a ser espacios, con el papel que les toca por tamaño y sitio.
    fn emit_leaves(&mut self) {
        let leaves: Vec<usize> = (0..self.nodes.len())
            .filter(|&n| self.nodes[n].children.is_none())
            .collect();
        for n in leaves {
            let rect = self.nodes[n].rect;
            let depth = self.nodes[n].depth;
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
            self.push_space(rect, role, depth);
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
        for room in 0..self.spaces.len() {
            let Some((w, band, x, z)) = best_access[room] else {
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
        for i in 0..self.spaces.len() {
            let role = self.spaces[i].role;
            if degree[i] == 1 && matches!(role, SpaceRole::Office | SpaceRole::Storage) {
                self.spaces[i].role = SpaceRole::DeadEnd;
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
            let mut bridge: Option<(usize, Vec<(usize, i32, i32, i32)>)> = None;
            for v in 0..self.spaces.len() {
                if self.spaces[v].role != SpaceRole::Void {
                    continue;
                }
                let touching: Vec<(usize, i32, i32, i32)> = adj
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

    fn push_space(&mut self, rect: PlanRect, role: SpaceRole, depth: u8) -> usize {
        let (cx, cz) = rect.centre_m();
        self.spaces.push(PlannedSpace {
            rect,
            floor_y_cm: 0,
            role,
            scale: scale::scale_at(self.seed, cx, cz),
            depth,
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
