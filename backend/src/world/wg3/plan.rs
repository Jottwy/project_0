//! ADR-100 Ã¢â‚¬â€ EL PLAN DE REGIÃƒâ€œN: quÃƒÂ© edificio hay aquÃƒÂ­, decidido antes de colocar una sola pieza.
//!
//! # QuÃƒÂ© problema resuelve, y por quÃƒÂ© no lo resolvÃƒÂ­an las palancas anteriores
//!
//! Hasta aquÃƒÂ­ el mundo lo decidÃƒÂ­a `compose`: se saca una boca de una frontera BFS, se sortea una
//! pieza que case y la posiciÃƒÂ³n sale *determinada* por esa boca. Nada en ese bucle mira el conjunto.
//! La consecuencia estÃƒÂ¡ medida y escrita en ADR-099 con las palabras de quien lo juega Ã¢â‚¬â€Ã‚Â«todo es
//! pasillosÃ‚Â», Ã‚Â«cosas sin solaparse bienÃ‚Â»Ã¢â‚¬â€: **el mundo crece en CADENA, asÃƒÂ­ que dos salas nunca
//! acaban lado a lado porque no hay nada que las ponga ahÃƒÂ­.**
//!
//! Ninguna de las palancas que se probaron ataca eso, y las tres se midieron: compartir pared sube
//! el llenado nueve dÃƒÂ©cimas, la absorciÃƒÂ³n cambia la topologÃƒÂ­a sin tocar la superficie, y densificar
//! lo dobla pero rompe la conectividad porque planta al azar. Son parches sobre un reparto que nadie
//! ha decidido.
//!
//! Este mÃƒÂ³dulo decide el reparto. **Es la nueva fuente de verdad arquitectÃƒÂ³nica**: aquÃƒÂ­ se dice quÃƒÂ©
//! espacios existen, de quÃƒÂ© tamaÃƒÂ±o, con quÃƒÂ© papel y unidos a cuÃƒÂ¡les. Todo lo que viene despuÃƒÂ©s
//! Ã¢â‚¬â€elegir pieza, tender conector, rasterizar, dibujarÃ¢â‚¬â€ ejecuta este plan y no puede contradecirlo.
//!
//! # CÃƒÂ³mo, en una frase
//!
//! SubdivisiÃƒÂ³n recursiva del rectÃƒÂ¡ngulo de regiÃƒÂ³n, **con los corredores tallados en los cortes de
//! los primeros niveles**. Eso da tres cosas de golpe que el compositor-ÃƒÂ¡rbol no podÃƒÂ­a dar:
//!
//! 1. **Masa contigua por construcciÃƒÂ³n.** Los hijos de un corte llenan al padre entero, asÃƒÂ­ que el
//!    vacÃƒÂ­o deja de ser lo que sobra y pasa a ser algo que se marca a propÃƒÂ³sito ([`SpaceRole::Void`]).
//! 2. **JerarquÃƒÂ­a real.** El corte de nivel 0 es la espina; los de nivel 1 y 2, corredores
//!    secundarios; de ahÃƒÂ­ para abajo los cortes NO tallan banda, asÃƒÂ­ que las salas hermanas comparten
//!    pared y se comunican por un vano. Un edificio, no una cuadrÃƒÂ­cula de pasillos.
//! 3. **El grafo antes que la geometrÃƒÂ­a.** Las adyacencias del reparto SON las conexiones
//!    candidatas, y salen gratis: dos rectÃƒÂ¡ngulos que comparten borde ya se tocan.
//!
//! # Lo que este mÃƒÂ³dulo NO hace, y es deliberado
//!
//! No mira el catÃƒÂ¡logo, no conoce `Wg3Piece`, no emite geometrÃƒÂ­a y no sabe quÃƒÂ© es una malla. Un plan
//! es vÃƒÂ¡lido con un catÃƒÂ¡logo vacÃƒÂ­o. Esa frontera es lo que permite medir la arquitectura sola Ã¢â‚¬â€el
//! criterio de aceptaciÃƒÂ³n del ADR es que **el plano se lea como un edificio con las mallas
//! apagadas**Ã¢â‚¬â€ y lo que impide que el contenido vuelva a decidir la forma por la puerta de atrÃƒÂ¡s.
//!
//! # Determinismo (R3)
//!
//! Cada decisiÃƒÂ³n abre su propio flujo desde la POSICIÃƒâ€œN Ã¢â‚¬â€el centro del rectÃƒÂ¡ngulo que se estÃƒÂ¡
//! partiendoÃ¢â‚¬â€ y una sal propia, nunca desde un ÃƒÂ­ndice ni desde el orden de proceso. Es la misma
//! regla que ya cumplen el compositor y el campo de escala, y es lo que permitirÃƒÂ¡ que el plan sea
//! troceable el dÃƒÂ­a que haga falta: partir el mismo rectÃƒÂ¡ngulo dos veces da el mismo corte sin que
//! nadie recuerde nada.
//!
//! **EN CENTÃƒÂMETROS ENTEROS**, por lo mismo que `Wg3Placement` y `Wg3Segment`: un plan se compara
//! entre procesos y una cadena de sumas en `f32` no garantiza que dos backends coincidan bit a bit.

use super::hash;
use super::junction::Wg3Gate;
use super::raster::CM_PER_M;
use super::scale;

/// Sal del sorteo de corte: eje y posiciÃƒÂ³n.
const SALT_SPLIT: u32 = 0x9A17_0000;
/// Sal de la decisiÃƒÂ³n de parar de subdividir.
const SALT_STOP: u32 = 0x9A17_0001;
/// Sal del reparto de papeles.
const SALT_ROLE: u32 = 0x9A17_0002;
/// Sal del sorteo de vacÃƒÂ­o intencionado.
const SALT_VOID: u32 = 0x9A17_0003;
/// Sal de los vanos de mÃƒÂ¡s, los que cierran anillos.
const SALT_RING: u32 = 0x9A17_0004;

/// Profundidad a partir de la cual un corte YA NO talla banda de corredor.
///
/// **Es el nÃƒÂºmero que separa un edificio de una cuadrÃƒÂ­cula de pasillos, y por eso es tan bajo.** Con
/// banda en todos los cortes, a profundidad 6 hay corredor entre cualesquiera dos salas y el
/// resultado se lee como una retÃƒÂ­cula Ã¢â‚¬â€justo lo que WG3 vino a quitarÃ¢â‚¬â€. Con banda sÃƒÂ³lo en los tres
/// primeros niveles, el mundo tiene una espina, un par de ramas, y **a partir de ahÃƒÂ­ las salas
/// comparten pared y se comunican por un vano**, que es como estÃƒÂ¡ hecho un edificio de oficinas.
const CORRIDOR_DEPTH: u8 = 3;

/// Ancho de la banda de corredor por profundidad de corte, en centÃƒÂ­metros.
///
/// Decrece con la profundidad porque la jerarquÃƒÂ­a tiene que VERSE andando: la espina es mÃƒÂ¡s ancha
/// que la rama, y la rama mÃƒÂ¡s que el ramal. El ÃƒÂºltimo valor es tambiÃƒÂ©n el suelo: 240 cm es la
/// anchura de boca del catÃƒÂ¡logo y el mÃƒÂ­nimo que el rÃƒÂ¡ster conservador deja pasar
/// (`MIN_GENERATED_WIDTH_CM`).
const BAND_WIDTH_CM: [i32; CORRIDOR_DEPTH as usize] = [320, 280, 240];

/// Lado mÃƒÂ­nimo de un espacio, en centÃƒÂ­metros.
///
/// Por debajo de esto no es una sala: es el hueco que queda entre dos paredes. Se comprueba ANTES de
/// cortar Ã¢â‚¬â€los dos hijos tienen que cumplirlo, banda incluidaÃ¢â‚¬â€ para que la subdivisiÃƒÂ³n nunca produzca
/// una astilla que luego haya que tirar.
const MIN_SIDE_CM: i32 = 500;

/// Profundidad mÃƒÂ¡xima del ÃƒÂ¡rbol de subdivisiÃƒÂ³n.
///
/// **Es una cota de seguridad, y tiene que estar MUY por encima de lo que se usa** Ã¢â‚¬â€ si se convierte
/// en el criterio que para la subdivisiÃƒÂ³n, el campo de escala deja de mandar y todas las regiones
/// salen con el mismo tamaÃƒÂ±o de sala. PasÃƒÂ³ con 7: una regiÃƒÂ³n de 22 500 mÃ‚Â² no llega a hojas de 150 mÃ‚Â²
/// antes de agotarla, asÃƒÂ­ que el reparto se quedaba en 34 naves de 460 mÃ‚Â² de media y el ÃƒÂ¡rea objetivo
/// no pintaba nada. Con 12 el que corta es siempre `TARGET_AREA_M2`, que es lo que se querÃƒÂ­a.
const MAX_DEPTH: u8 = 12;

/// DÃƒÂ³nde puede caer un corte dentro del lado que parte, en tantos por uno.
///
/// **No es la mitad, y eso es la mitad del aspecto.** Un corte centrado da hijos iguales, y un ÃƒÂ¡rbol
/// de hijos iguales es una cuadrÃƒÂ­cula por mucho que se llame BSP. Cortar entre el 32 % y el 68 % da
/// hermanos de tamaÃƒÂ±os distintos en cada nivel, que es de donde sale que un edificio tenga una sala
/// grande al lado de tres pequeÃƒÂ±as.
const SPLIT_LO: f32 = 0.32;
const SPLIT_HI: f32 = 0.68;

/// Probabilidad de partir por el lado CORTO aunque el largo sea el candidato natural.
///
/// Partir siempre por el lado largo converge a rectÃƒÂ¡ngulos cuadrados, y un edificio real tiene
/// pasillos largos y salas alargadas. Este escape es lo que deja aparecer proporciones raras sin que
/// sean la norma.
const CROSS_SPLIT_CHANCE: f32 = 0.18;

/// ProporciÃƒÂ³n a partir de la cual ya no se permite el escape: sÃƒÂ³lo se parte el lado largo.
///
/// **Una proporciÃƒÂ³n no se arregla subdividiendo, se hereda.** Un rectÃƒÂ¡ngulo 3:1 al que se le corta
/// el lado corto pasa a 6:1 y todos sus descendientes salen de ahÃƒÂ­. Este tope es lo que impide que
/// un escape pensado para dar variedad acabe produciendo una regiÃƒÂ³n entera de pasillos de 5 m de
/// ancho que nadie pidiÃƒÂ³.
const MAX_ASPECT: f32 = 2.6;

/// ÃƒÂrea objetivo de un espacio segÃƒÂºn la clase de escala del campo, en metros cuadrados.
///
/// **AquÃƒÂ­ es donde `scale_at` pasa a decidir arquitectura en vez de sesgar un sorteo.** Antes
/// multiplicaba el peso de una pieza candidata Ã¢â‚¬â€o sea que sÃƒÂ³lo influÃƒÂ­a en cuÃƒÂ¡l de las que ya cabÃƒÂ­an
/// salÃƒÂ­a elegidaÃ¢â‚¬â€; ahora fija cuÃƒÂ¡nto se subdivide una zona, y por tanto el TAMAÃƒâ€˜O de los espacios que
/// habrÃƒÂ¡ allÃƒÂ­. Una zona `Narrow` se trocea en despachos; una `Large` se queda en nave.
/// **Estos nÃƒÂºmeros se midieron dos veces y las dos primeras estaban mal**, y el histograma de la
/// sonda es lo que lo dijo: con 70/150/360 salÃƒÂ­an 268 espacios por regiÃƒÂ³n de los que 81 eran
/// trasteros de menos de 55 mÃ‚Â² y CERO naves Ã¢â‚¬â€ variedad en el papel, todo pequeÃƒÂ±o en la prÃƒÂ¡ctica. Un
/// mÃƒÂ­nimo y un mÃƒÂ¡ximo separados no son variedad; hay que mirar el reparto.
const TARGET_AREA_M2: [f32; 4] = [
    110.0, // SCALE_NARROW Ã¢â‚¬â€ despachos
    240.0, // SCALE_MEDIUM Ã¢â‚¬â€ oficinas normales
    700.0, // SCALE_LARGE  Ã¢â‚¬â€ naves, salas diÃƒÂ¡fanas
    380.0, // SCALE_WEIRD  Ã¢â‚¬â€ ver `WEIRD_SPREAD`: aquÃƒÂ­ el nÃƒÂºmero no manda solo
];

/// CuÃƒÂ¡nto puede desviarse el ÃƒÂ¡rea objetivo en una zona `Weird`, como factor.
///
/// La escala rara no significa Ã‚Â«grandeÃ‚Â» ni Ã‚Â«pequeÃƒÂ±aÃ‚Â»: significa que ahÃƒÂ­ las proporciones no siguen
/// la regla. Un factor entre 0,35 y 2,6 mete en el mismo mundo el armario absurdo y la nave
/// desproporcionada, que es lo que hace que un sitio se lea como Backrooms y no como un edificio de
/// oficinas bien diseÃƒÂ±ado.
const WEIRD_SPREAD: (f32, f32) = (0.35, 2.6);

/// Probabilidad base de que una hoja se marque como vacÃƒÂ­o intencionado.
///
/// **El vacÃƒÂ­o deja de ser un fallo y pasa a ser una decisiÃƒÂ³n.** Hasta ahora el 75-96 % de una regiÃƒÂ³n
/// era vacÃƒÂ­o porque nadie fue a mirar ahÃƒÂ­; aquÃƒÂ­ el hueco existe porque se ha dicho que exista Ã¢â‚¬â€patio,
/// zona clausurada, hueco de instalacionesÃ¢â‚¬â€ y por eso puede ser poco y estar donde tiene que estar.
const VOID_CHANCE: f32 = 0.11;

/// Probabilidad extra de vacÃƒÂ­o en zona rara. Una zona `Weird` con un descampado dentro es liminal;
/// la misma zona llena es sÃƒÂ³lo un edificio.
const VOID_CHANCE_WEIRD: f32 = 0.22;

/// ÃƒÂrea a partir de la cual una hoja es una sala grande y no una oficina, en metros cuadrados.
const HALL_AREA_M2: f32 = 300.0;

/// ÃƒÂrea por debajo de la cual una hoja es trastero y no oficina.
const STORAGE_AREA_M2: f32 = 45.0;

/// Anchura de un vano normal, en centÃƒÂ­metros. Es la boca `Corridor` del catÃƒÂ¡logo.
pub const DOORWAY_CM: i32 = 240;

/// Anchura de un vano ancho Ã¢â‚¬â€ el que abre a una nave. Es la boca `Wide` del catÃƒÂ¡logo.
pub const WIDE_DOORWAY_CM: i32 = 500;

/// Solape de pared mÃƒÂ­nimo para que DOS ESPACIOS SE CONSIDEREN VECINOS, en centÃƒÂ­metros.
///
/// Es el vano pelado, sin jambas. **Y tiene que ser exactamente el vano, no mÃƒÂ¡s**: dos bandas de
/// corredor que se cruzan comparten justo el ancho de la mÃƒÂ¡s estrecha, asÃƒÂ­ que exigir jambas dejaba
/// los cruces fuera y el plano salÃƒÂ­a con CERO intersecciones Ã¢â‚¬â€ una red de corredores que no se
/// tocaban. Las jambas se prefieren donde importa (ver [`GOOD_WALL_CM`]), no se exigen aquÃƒÂ­.
const MIN_SHARED_WALL_CM: i32 = DOORWAY_CM;

/// Solape de pared que se PREFIERE al elegir por dÃƒÂ³nde entra una sala. El vano mÃƒÂ¡s sus dos jambas.
///
/// Preferencia y no ley: entre dos paredes candidatas gana la ancha, pero una sala que sÃƒÂ³lo toca el
/// corredor por el mÃƒÂ­nimo entra por ahÃƒÂ­ igual Ã¢â‚¬â€ quedarse sin acceso es peor que una jamba estrecha.
const GOOD_WALL_CM: i32 = DOORWAY_CM + 120;

/// Probabilidad de abrir un vano de MÃƒÂS entre dos salas ya conectadas por otro camino.
///
/// Es lo que convierte el grafo en un edificio con anillos en vez de en un ÃƒÂ¡rbol. Un edificio real
/// tiene mÃƒÂ¡s de una forma de llegar a sitio, y sin eso vuelve el Ã‚Â«llega un punto que se cierraÃ‚Â».
const RING_CHANCE: f32 = 0.30;

/// Un rectÃƒÂ¡ngulo del plan, en centÃƒÂ­metros enteros de mundo.
///
/// Propio y no `route::Rect` a propÃƒÂ³sito: aquÃƒÂ©l estÃƒÂ¡ en metros y en `f32` porque el enrutador compara
/// contra geometrÃƒÂ­a ya colocada. Un plan se compara entre procesos, asÃƒÂ­ que va en enteros Ã¢â‚¬â€ la misma
/// razÃƒÂ³n por la que la llevan `Wg3Placement` y `Wg3Segment`.
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
    /// Centro en metros. Es lo que siembra los sorteos: la POSICIÃƒâ€œN, nunca el ÃƒÂ­ndice.
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

/// QuÃƒÂ© papel juega un espacio en el edificio.
///
/// **Es la pieza de vocabulario que WG3 no tenÃƒÂ­a**, y la que separa Ã‚Â«una colecciÃƒÂ³n de rectÃƒÂ¡ngulosÃ‚Â» de
/// Ã‚Â«una plantaÃ‚Â». El relleno lo lee para elegir contenido, y las sondas lo leen para decir si el
/// reparto se parece a un edificio.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SpaceRole {
    /// La banda del corte de nivel 0: el eje principal de la regiÃƒÂ³n. Hay como mucho una.
    Spine,
    /// Banda de corte de nivel 1 o 2: corredor secundario que cuelga de la espina.
    Corridor,
    /// Donde dos bandas se cruzan. Es un espacio propio porque una intersecciÃƒÂ³n es un sitio, no una
    /// arista: es donde se decide por dÃƒÂ³nde seguir, y merece leerse distinto.
    Junction,
    /// Sala grande y diÃƒÂ¡fana.
    Hall,
    /// El caso normal: una sala con una o dos salidas.
    Office,
    /// Trasero de la escena: cuartos tÃƒÂ©cnicos, pasos de instalaciones. Se agrupan al fondo de una
    /// rama, lejos del corredor.
    Service,
    /// AlmacÃƒÂ©n: sala pequeÃƒÂ±a colgada de otra, sin salida propia al corredor.
    Storage,
    /// Sala con UNA sola conexiÃƒÂ³n. No es un fallo del reparto: es una decisiÃƒÂ³n, y es la mitad de lo
    /// que hace que un sitio se recorra con inquietud.
    DeadEnd,
    /// **VacÃƒÂ­o INTENCIONADO**: patio, hueco, zona clausurada. No se rellena y no se conecta.
    ///
    /// Existir como papel es lo que lo separa del vacÃƒÂ­o de antes: aquÃƒÂ©l era el terreno al que no
    /// llegÃƒÂ³ ninguna boca, y no habÃƒÂ­a forma de distinguir Ã‚Â«aquÃƒÂ­ no hay nada porque asÃƒÂ­ se ha
    /// decididoÃ‚Â» de Ã‚Â«aquÃƒÂ­ no hay nada porque el generador se quedÃƒÂ³ cortoÃ‚Â».
    Void,
}

impl SpaceRole {
    /// Ã‚Â¿Es una banda de circulaciÃƒÂ³n? Las bandas se rellenan y se conectan distinto que las salas.
    pub fn is_circulation(&self) -> bool {
        matches!(
            self,
            SpaceRole::Spine | SpaceRole::Corridor | SpaceRole::Junction
        )
    }
    /// Ã‚Â¿Se rellena con contenido? El vacÃƒÂ­o no.
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
    /// Cota del suelo, en centÃƒÂ­metros de mundo (ADR-097, mismas unidades que la colocaciÃƒÂ³n).
    ///
    /// **Toda a cero en esta versiÃƒÂ³n, y estÃƒÂ¡ declarado.** El plan es el sitio natural donde decidir
    /// un desnivel Ã¢â‚¬â€una nave hundida, una entreplantaÃ¢â‚¬â€ pero hacerlo exige que los enlaces lleven
    /// escalÃƒÂ³n y que el relleno sepa construirlo, y eso es trabajo aparte. Cero aquÃƒÂ­ no es un
    /// descuido: es que la verticalidad del plan todavÃƒÂ­a no se ha decidido.
    pub floor_y_cm: i32,
    pub role: SpaceRole,
    /// Clase del campo de escala en el centro del espacio. Se guarda porque el relleno la necesita
    /// para elegir contenido y recalcularla allÃƒÂ­ invitarÃƒÂ­a a que los dos no coincidieran.
    pub scale: u8,
    /// Profundidad en el ÃƒÂ¡rbol de subdivisiÃƒÂ³n. **ES LA JERARQUÃƒÂA**, medible: un plano donde todo
    /// tiene la misma profundidad es una cuadrÃƒÂ­cula por mucho que los rectÃƒÂ¡ngulos midan distinto.
    pub depth: u8,
}

/// CÃƒÂ³mo se conectan dos espacios.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LinkKind {
    /// Dos salas que COMPARTEN pared: la conexiÃƒÂ³n es un vano en la pared comÃƒÂºn. No necesita
    /// enrutador ni geometrÃƒÂ­a nueva.
    Doorway,
    /// Una sala y la banda de corredor que la bordea. Es el caso mayoritario, y es lo que hace que
    /// el corredor sea arquitectura: existe para dar acceso, no para coser.
    Access,
    /// Dos bandas que se encuentran. Marca un cruce.
    Junction,
    /// Dos espacios que NO se tocan. **Es lo ÃƒÂºnico que llega al enrutador**, y llega como encargo:
    /// Ã‚Â«une esto con estoÃ‚Â», no Ã‚Â«busca a ver quÃƒÂ© quedÃƒÂ³ sueltoÃ‚Â».
    Route,
}

/// Una conexiÃƒÂ³n que el plan DECIDE que existe, antes de que haya geometrÃƒÂ­a que la pueda cumplir.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlannedLink {
    pub a: usize,
    pub b: usize,
    pub width_cm: i32,
    pub kind: LinkKind,
    /// DÃƒÂ³nde cae el paso, en centÃƒÂ­metros de mundo: el centro del solape de pared para los tres
    /// primeros tipos, el punto medio entre los dos espacios para un `Route`.
    ///
    /// Va en el enlace y no se recalcula aguas abajo porque es una DECISIÃƒâ€œN del plan: dos vanos a la
    /// misma sala tienen que caer donde el plan dijo o el reparto de puertas deja de ser suyo.
    pub at_x_cm: i32,
    pub at_z_cm: i32,
}

/// Una puerta de junta, ya asignada al espacio que la abre.
///
/// **No es un espacio, y ÃƒÂ©se fue el primer intento fallido.** Plantar un tocÃƒÂ³n de puerta como
/// rectÃƒÂ¡ngulo propio lo mete dentro de la hoja que ya ocupaba ese trozo de borde, y el plan salÃƒÂ­a con
/// siete solapes por regiÃƒÂ³n Ã¢â‚¬â€ geometrÃƒÂ­a cruzada, que el rÃƒÂ¡ster estampa maciza sin quejarse. La puerta
/// no necesita espacio: necesita SABER en quÃƒÂ© pared se abre, y la pared es de alguien que ya existe.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PlannedGate {
    /// El espacio del plan cuya pared se abre.
    pub space: usize,
    pub x_cm: i32,
    pub z_cm: i32,
    /// Lado de la regiÃƒÂ³n que mira AFUERA por esta puerta.
    pub outward_side: u8,
    pub width_cm: i32,
}

/// El edificio de una regiÃƒÂ³n, decidido y todavÃƒÂ­a sin una sola pieza dentro.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct RegionPlan {
    pub spaces: Vec<PlannedSpace>,
    pub links: Vec<PlannedLink>,
    /// Las puertas de junta, cada una colgada del espacio que la abre. VacÃƒÂ­o si la regiÃƒÂ³n no tenÃƒÂ­a.
    pub gates: Vec<PlannedGate>,
    /// Caja de la regiÃƒÂ³n, para que una sonda no tenga que volver a preguntarla.
    pub bounds_cm: Option<PlanRect>,
}

impl RegionPlan {
    /// Espacios que se van a construir (todos menos el vacÃƒÂ­o intencionado).
    pub fn built(&self) -> impl Iterator<Item = (usize, &PlannedSpace)> {
        self.spaces
            .iter()
            .enumerate()
            .filter(|(_, s)| s.role.is_built())
    }

    /// ÃƒÂrea construida, en metros cuadrados. **La mÃƒÂ©trica que sustituye al Ã‚Â«porcentaje ocupadoÃ‚Â»**:
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

    /// Lo que este mÃƒÂ³dulo necesita que sea cierto antes de que nadie construya nada. VacÃƒÂ­o = sano.
    ///
    /// No comprueba que el plan sea BONITO Ã¢â‚¬â€eso lo miden las sondasÃ¢â‚¬â€ sino que sea COHERENTE: que no
    /// haya rectÃƒÂ¡ngulos degenerados, que los espacios no se pisen, y que ningÃƒÂºn enlace apunte fuera.
    /// Un plan incoherente construye un edificio incoherente sin dar un solo error.
    pub fn problems(&self) -> Vec<String> {
        let mut out = Vec::new();
        for (i, s) in self.spaces.iter().enumerate() {
            if s.rect.width_cm() <= 0 || s.rect.depth_cm() <= 0 {
                out.push(format!(
                    "espacio {i}: huella no positiva {}Ãƒâ€”{} cm",
                    s.rect.width_cm(),
                    s.rect.depth_cm()
                ));
            }
        }
        // Solape. La subdivisiÃƒÂ³n no puede producirlo por construcciÃƒÂ³n, asÃƒÂ­ que si aparece es que
        // alguien ha tocado el tallado de bandas Ã¢â‚¬â€ y el sÃƒÂ­ntoma serÃƒÂ­a geometrÃƒÂ­a cruzada, que el
        // rÃƒÂ¡ster estampa maciza sin quejarse.
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
                out.push(format!("enlace {i}: ÃƒÂ­ndice fuera de rango"));
                continue;
            }
            if l.a == l.b {
                out.push(format!("enlace {i}: un espacio consigo mismo"));
            }
            if l.width_cm < DOORWAY_CM {
                out.push(format!(
                    "enlace {i}: {} cm por debajo del vano mÃƒÂ­nimo de {DOORWAY_CM} cm",
                    l.width_cm
                ));
            }
            if !self.spaces[l.a].role.is_built() || !self.spaces[l.b].role.is_built() {
                out.push(format!(
                    "enlace {i}: toca un espacio VACÃƒÂO, que no se construye"
                ));
            }
        }
        out
    }
}

/// Un vecino de un espacio, con la pared que comparten: `(otro, solape, x del paso, z del paso)`.
type Touching = (usize, i32, i32, i32);

/// Nodo del ÃƒÂ¡rbol de subdivisiÃƒÂ³n. Vive sÃƒÂ³lo mientras se planifica.
struct Node {
    rect: PlanRect,
    depth: u8,
    children: Option<(usize, usize)>,
}

/// **EL PLAN DE UNA REGIÃƒâ€œN.** FunciÃƒÂ³n pura: misma semilla, misma caja y mismas puertas Ã¢â€¡â€™ mismo
/// edificio (R3).
///
/// `gates` son las puertas de junta que ya acordÃƒÂ³ [`super::junction`] con la regiÃƒÂ³n vecina. Entran
/// como restricciÃƒÂ³n y no como sugerencia: son el ÃƒÂºnico punto del plan que NO se decide aquÃƒÂ­, porque
/// ya estÃƒÂ¡ acordado con alguien que no puede consultarse.
pub fn plan_region(seed: i32, bounds: (f32, f32, f32, f32), gates: &[Wg3Gate]) -> RegionPlan {
    let root = PlanRect {
        min_x_cm: (bounds.0 * CM_PER_M).round() as i32,
        min_z_cm: (bounds.1 * CM_PER_M).round() as i32,
        max_x_cm: (bounds.2 * CM_PER_M).round() as i32,
        max_z_cm: (bounds.3 * CM_PER_M).round() as i32,
    };

    let mut planner = Planner {
        seed,
        // **LAS PUERTAS ENTRAN ANTES DE CORTAR, y esto costÃƒÂ³ una de cada cuatro.** Un corte que cae
        // sobre una puerta de junta la parte entre dos espacios y ninguno de los dos puede abrirla:
        // la regiÃƒÂ³n nace SELLADA por ese lado mientras la vecina abre la suya contra el muro.
        // Medido antes de arreglarlo: **64 de 256 puertas perdidas** en un barrido de 49 regiones, y
        // el ÃƒÂºnico rastro era un `warn` en el log del backend. Ver `forbidden_for`.
        gate_cuts_x: gate_coordinates(gates, true),
        gate_cuts_z: gate_coordinates(gates, false),
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
    // Las puertas ANTES de enlazar: una puerta acordada con la vecina no se negocia, asÃƒÂ­ que el
    // espacio que la abre no puede ser vacÃƒÂ­o y tiene que estar dentro del edificio. Decidirlo despuÃƒÂ©s
    // obligarÃƒÂ­a a rehacer el grafo.
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

/// CuÃƒÂ¡nto tiene que apartarse un corte del centro de una puerta de junta, en centÃƒÂ­metros.
///
/// Es media puerta (120) + media banda de corredor (160) + una jamba (60). Los tres sumandos hacen
/// falta: sin el primero el corte parte el vano, sin el segundo la banda de corredor cae encima y
/// deja la puerta con 40 cm de jamba Ã¢â‚¬â€por debajo de lo que `touches_border_point` aceptaÃ¢â‚¬â€, y sin el
/// tercero la puerta queda pegada a la esquina del espacio.
const GATE_CLEARANCE_CM: i32 = DOORWAY_CM / 2 + BAND_WIDTH_CM[0] / 2 + 60;

/// Las coordenadas que un corte NO puede pisar, sacadas de las puertas de junta.
///
/// Una puerta en un borde HORIZONTAL (lados N/S) corre a lo largo de X, asÃƒÂ­ que la parte un corte en
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

struct Planner {
    seed: i32,
    /// Coordenadas de puerta que un corte en X no puede pisar. Ver [`GATE_CLEARANCE_CM`].
    gate_cuts_x: Vec<i32>,
    /// Las mismas para un corte en Z.
    gate_cuts_z: Vec<i32>,
    nodes: Vec<Node>,
    spaces: Vec<PlannedSpace>,
    /// Para cada nodo, el ÃƒÂ­ndice del espacio-banda que tallÃƒÂ³ su corte. `None` si el corte no tallÃƒÂ³
    /// banda (profundidad Ã¢â€°Â¥ [`CORRIDOR_DEPTH`]) o si el nodo es hoja.
    band_of_node: Vec<Option<usize>>,
    links: Vec<PlannedLink>,
}

impl Planner {
    /// El reparto: se parte en anchura, y los cortes de los primeros niveles se llevan su banda.
    ///
    /// En ANCHURA y no en profundidad para que la banda de nivel 0 exista antes que ninguna de nivel
    /// 1 Ã¢â‚¬â€ la jerarquÃƒÂ­a tiene que quedar en el orden de creaciÃƒÂ³n, porque es la que decide quÃƒÂ©
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

    /// Ã‚Â¿Se parte este rectÃƒÂ¡ngulo, por dÃƒÂ³nde, y con cuÃƒÂ¡nta banda? `None` = es una hoja.
    ///
    /// Devuelve `(eje, posiciÃƒÂ³n del centro de la banda, ancho de banda)`. El ancho es 0 cuando el
    /// corte no talla corredor, y entonces los dos hijos comparten la lÃƒÂ­nea exacta Ã¢â‚¬â€ que es como
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

        // El ÃƒÂ¡rea objetivo es lo que para la subdivisiÃƒÂ³n, y es donde el campo de escala pasa a
        // decidir tamaÃƒÂ±os de espacio en vez de sesgar un sorteo de pieza.
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

        // ElecciÃƒÂ³n de eje: se parte el lado LARGO, con un escape hacia el corto para que aparezcan
        // proporciones que un BSP disciplinado nunca darÃƒÂ­a.
        //
        // **`axis == 0` corta la ANCHURA** (plano vertical, hijos izquierda/derecha), asÃƒÂ­ que para
        // acortar el lado largo hay que pedir 0 cuando el largo es X. La primera versiÃƒÂ³n pedÃƒÂ­a lo
        // contrario y partÃƒÂ­a siempre el lado corto: la regiÃƒÂ³n entera salÃƒÂ­a en lonchas verticales de
        // 5 Ãƒâ€” 30 m, y se vio en el volcado antes que en ningÃƒÂºn nÃƒÂºmero Ã¢â‚¬â€ ninguna mÃƒÂ©trica del plan
        // miraba la proporciÃƒÂ³n.
        let mut s = hash::stream_at(self.seed, cx, cz, SALT_SPLIT);
        let long_is_x = rect.width_cm() >= rect.depth_cm();
        let long_axis: u8 = if long_is_x { 0 } else { 1 };

        // El escape sÃƒÂ³lo se permite mientras la pieza siga siendo razonablemente cuadrada. Partir el
        // lado corto de un rectÃƒÂ¡ngulo que ya es 3:1 lo lleva a 6:1, y de ahÃƒÂ­ no vuelve: ninguna
        // subdivisiÃƒÂ³n posterior arregla una proporciÃƒÂ³n, sÃƒÂ³lo la hereda.
        let slim = rect.width_cm().max(rect.depth_cm()) as f32
            / rect.width_cm().min(rect.depth_cm()).max(1) as f32;
        let cross = slim < MAX_ASPECT && s.next01() < CROSS_SPLIT_CHANCE;
        let wanted: u8 = if cross { 1 - long_axis } else { long_axis };

        // **SI EL EJE SORTEADO NO DA, SE PRUEBA EL OTRO ANTES DE RENDIRSE.**
        //
        // Rendirse al primer intento dejaba de subdividir rectÃƒÂ¡ngulos que sÃƒÂ­ tenÃƒÂ­an sitio por el otro
        // lado, y el efecto no era pequeÃƒÂ±o: hojas del doble del ÃƒÂ¡rea objetivo, que luego se contaban
        // como naves porque pasaban el umbral de `Hall`. Una regiÃƒÂ³n salÃƒÂ­a con mÃƒÂ¡s naves que oficinas.
        let fits = |axis: u8| -> bool {
            let side = if axis == 0 {
                rect.width_cm()
            } else {
                rect.depth_cm()
            };
            // Los dos hijos y la banda tienen que caber. Se comprueba ANTES de cortar: una hoja
            // pequeÃƒÂ±a es una sala, una astilla no es nada.
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
        // El lado que NO se corta tambiÃƒÂ©n tiene que dar una sala, o saldrÃƒÂ­an dos astillas de canto.
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
        // **Y el corte se aparta de las puertas de junta.** Si no puede Ã¢â‚¬â€lo que queda libre estÃƒÂ¡
        // entero dentro de una zona prohibidaÃ¢â‚¬â€ se renuncia a cortar: una hoja algo mayor no le
        // importa a nadie, y una puerta de junta partida sella la regiÃƒÂ³n contra su vecina.
        let world_at = self.clear_of_gates(base + at, axis, base + lo, base + hi)?;
        Some((axis, world_at, band_cm))
    }

    /// Aparta un corte de las puertas de junta, o `None` si no hay dÃƒÂ³nde ponerlo.
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
                // Al borde mÃƒÂ¡s cercano de la zona prohibida, que es el que menos deforma el reparto.
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

    /// Las hojas del ÃƒÂ¡rbol pasan a ser espacios, con el papel que les toca por tamaÃƒÂ±o y sitio.
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
            // La escala del campo mueve el umbral de Ã‚Â«esto es una naveÃ‚Â»: lo que en zona estrecha ya
            // es enorme, en zona grande es una sala normal. Sin esto, `Hall` serÃƒÂ­a puro tamaÃƒÂ±o y una
            // regiÃƒÂ³n `Large` saldrÃƒÂ­a entera de naves.
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
                // tipo de boca `Service` tiene sentido, y el ÃƒÂºnico sitio del plan que sabe de ÃƒÂ©l.
                SpaceRole::Service
            } else {
                SpaceRole::Office
            };
            self.push_space(rect, role, depth);
        }
    }

    /// El vacÃƒÂ­o intencionado. Va DESPUÃƒâ€°S de repartir papeles y ANTES de enlazar: un hueco tiene que
    /// existir antes de que nadie decida a quÃƒÂ© se conecta.
    ///
    /// **No se vacÃƒÂ­a nunca una banda de circulaciÃƒÂ³n.** Un agujero en la espina parte el edificio en
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
    /// El orden ES la jerarquÃƒÂ­a: primero se cose la red de circulaciÃƒÂ³n entre sÃƒÂ­, luego cada sala
    /// entra por su corredor, luego se rescata lo que no toca ninguno, y sÃƒÂ³lo al final se abren
    /// vanos de mÃƒÂ¡s. Hacerlo al revÃƒÂ©s darÃƒÂ­a un grafo igual de conexo y un edificio en el que el
    /// corredor no manda sobre nada.
    fn link_all(&mut self) {
        let adj = self.adjacencies();

        // 1 Ã¢â‚¬â€ la red de circulaciÃƒÂ³n. Todas las bandas que se tocan quedan unidas: es la espina y sus
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

        // 2 Ã¢â‚¬â€ cada sala entra por el corredor que la bordea. Ãƒâ€°sta es la pasada que hace que el
        //     corredor sea arquitectura: existe para dar acceso, y el acceso se decide aquÃƒÂ­.
        //
        //     Se queda con la pared MÃƒÂS ANCHA de las que tocan corredor, no con la primera que
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

        // 3 Ã¢â‚¬â€ las salas que no tocan ninguna banda cuelgan de una vecina que sÃƒÂ­ llegue. Es la suite
        //     de despachos a la que se entra por otro despacho, y es arquitectura normal: sin esto
        //     habrÃƒÂ­a que meter corredor hasta la ÃƒÂºltima puerta y volverÃƒÂ­amos a la cuadrÃƒÂ­cula.
        let mut uf = UnionFind::new(self.spaces.len());
        for l in &self.links {
            uf.union(l.a, l.b);
        }
        // RaÃƒÂ­z de la circulaciÃƒÂ³n: todo lo construido tiene que acabar colgando de aquÃƒÂ­.
        let hub = self
            .spaces
            .iter()
            .position(|s| s.role == SpaceRole::Spine)
            .or_else(|| self.spaces.iter().position(|s| s.role.is_circulation()));

        // Se repite hasta que deje de crecer: una sala puede engancharse a otra que acaba de
        // engancharse, y ÃƒÂ©sa es justo la cadena de dos y tres puertas que da profundidad.
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
                // SÃƒÂ³lo se une si UNO de los dos ya llega a la circulaciÃƒÂ³n. Coser dos bolsillos entre
                // sÃƒÂ­ crearÃƒÂ­a una isla mayor, que es el error que ADR-098 ya midiÃƒÂ³ en el enrutador.
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

        // 3b Ã¢â‚¬â€ lo que siga suelto se une igual, aunque no toque la circulaciÃƒÂ³n. Sin esta pasada, un
        //      corro de salas rodeado de vacÃƒÂ­o se queda fuera del edificio, y eso no es una decisiÃƒÂ³n
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

        // 4 Ã¢â‚¬â€ vanos de MÃƒÂS: los anillos. Un edificio con un solo camino a cada sitio se recorre como
        //     un ÃƒÂ¡rbol, y el Ã‚Â«esto ya lo he vistoÃ‚Â» que sostiene la liminalidad necesita volver por
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

    /// Las salas que acabaron con UNA sola conexiÃƒÂ³n pasan a llamarse lo que son.
    ///
    /// Se hace al final y no al repartir papeles porque un callejÃƒÂ³n no es una propiedad de la sala:
    /// es una propiedad del GRAFO, y no se sabe hasta que el grafo estÃƒÂ¡ hecho.
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
    /// **Una puerta no es un espacio, y el primer intento que la hizo espacio saliÃƒÂ³ mal**: un tocÃƒÂ³n
    /// plantado hacia dentro desde el borde cae encima de la hoja que ya ocupaba ese trozo de regiÃƒÂ³n,
    /// y el plan salÃƒÂ­a con siete solapes Ã¢â‚¬â€ geometrÃƒÂ­a cruzada que el rÃƒÂ¡ster estampa maciza sin dar un
    /// solo error. Como los espacios teselan la regiÃƒÂ³n, el punto de la puerta cae SIEMPRE sobre el
    /// borde de exactamente uno: se busca, y se le abre el vano ahÃƒÂ­.
    ///
    /// **Y ese espacio deja de poder ser vacÃƒÂ­o.** Una puerta acordada con la vecina que da a un patio
    /// clausurado es el peor caso posible: la otra regiÃƒÂ³n pone su tramo y aquÃƒÂ­ no hay a dÃƒÂ³nde entrar.
    /// Si toca vacÃƒÂ­o, se rescata Ã¢â‚¬â€ la puerta manda sobre la decoraciÃƒÂ³n.
    fn attach_gates(&mut self, gates: &[Wg3Gate]) -> Vec<PlannedGate> {
        let mut out = Vec::with_capacity(gates.len());
        for gate in gates {
            let gx = (gate.x * CM_PER_M).round() as i32;
            let gz = (gate.z * CM_PER_M).round() as i32;

            // El espacio cuyo BORDE contiene el punto. Se prefiere la circulaciÃƒÂ³n cuando la puerta
            // cae justo en la esquina entre dos: entrar a una regiÃƒÂ³n por un pasillo es mejor que
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
                // sÃƒÂ­ntoma serÃƒÂ­a que la regiÃƒÂ³n vecina abre un vano al vacÃƒÂ­o, y eso es una caÃƒÂ­da.
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

    /// Ã‚Â¿Cae este punto sobre el borde del espacio `i` que mira a `side`?
    fn touches_border_point(&self, i: usize, x: i32, z: i32, side: u8) -> bool {
        const EPS: i32 = 2;
        let r = self.spaces[i].rect;
        // Media puerta a cada lado tiene que caber dentro del espacio, o el vano se saldrÃƒÂ­a de su
        // pared y quedarÃƒÂ­a medio abierto contra el vecino.
        let half = DOORWAY_CM / 2;
        match side % 4 {
            0 => (r.max_z_cm - z).abs() <= EPS && x - half >= r.min_x_cm && x + half <= r.max_x_cm,
            1 => (r.max_x_cm - x).abs() <= EPS && z - half >= r.min_z_cm && z + half <= r.max_z_cm,
            2 => (r.min_z_cm - z).abs() <= EPS && x - half >= r.min_x_cm && x + half <= r.max_x_cm,
            _ => (r.min_x_cm - x).abs() <= EPS && z - half >= r.min_z_cm && z + half <= r.max_z_cm,
        }
    }

    /// **EL EDIFICIO TIENE QUE SER UNO, y aquÃƒÂ­ se garantiza.**
    ///
    /// Las cuatro pasadas de `link_all` cosen todo lo que se toca, pero el vacÃƒÂ­o intencionado puede
    /// aislar un ala entera: un corro de salas rodeado de patios no toca nada construido y se queda
    /// fuera. Eso no es una decisiÃƒÂ³n Ã¢â‚¬â€nadie ha decidido que ese ala sea inaccesibleÃ¢â‚¬â€, es el agujero
    /// de conectividad de siempre entrando por otra puerta.
    ///
    /// Se arregla **rescatando el vacÃƒÂ­o que estorba, no enrutando alrededor**. Un patio que parte el
    /// edificio en dos no es un patio; devolverlo a sala es mÃƒÂ¡s barato y mÃƒÂ¡s honesto que tender un
    /// pasillo generado por encima de ÃƒÂ©l. SÃƒÂ³lo si no hay ningÃƒÂºn vacÃƒÂ­o que sirva de puente se recurre
    /// a un [`LinkKind::Route`], que es el encargo explÃƒÂ­cito al enrutador.
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
            // La componente MAYOR es el edificio; lo demÃƒÂ¡s se le engancha. Medirlo por ÃƒÂ¡rea y no por
            // nÃƒÂºmero de espacios: un ala de tres naves pesa mÃƒÂ¡s que veinte trasteros, y el edificio
            // es donde estÃƒÂ¡ la superficie.
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
                // Desempate por ÃƒÂ­ndice de raÃƒÂ­z: a igual ÃƒÂ¡rea, la menor. Ã‚Â«El que salgaÃ‚Â» harÃƒÂ­a que el
                // mundo cambiara entre ejecuciones sin que cambie nada mÃƒÂ¡s.
                .max_by(|a, b| a.1.total_cmp(&b.1).then(b.0.cmp(&a.0)))
                .map(|(r, _)| *r)
                .expect("hay al menos dos componentes");

            // Un vacÃƒÂ­o que toca la componente mayor y alguna otra: devolverlo a sala las une.
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

            // Sin puente disponible: se le ENCARGA al enrutador. Es la ÃƒÂºnica forma de conexiÃƒÂ³n del
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

    /// La pareja mÃƒÂ¡s cercana entre la componente mayor y cualquier otra. Determinista: a igual
    /// distancia gana el ÃƒÂ­ndice menor, nunca Ã‚Â«el que salgaÃ‚Â».
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
    /// **Es cuadrÃƒÂ¡tico, y a este tamaÃƒÂ±o no importa.** Una regiÃƒÂ³n da del orden de cincuenta espacios,
    /// o sea ~1 250 comparaciones de enteros por regiÃƒÂ³n y una sola vez. Lo que hay que no hacer es
    /// llevarlo al mundo entero: aquÃƒÂ­ estÃƒÂ¡ acotado por la regiÃƒÂ³n y no crece con la partida.
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

/// Ã‚Â¿Comparten estos dos rectÃƒÂ¡ngulos una pared con sitio para un vano?
///
/// Devuelve `(longitud del solape en cm, x del centro del paso, z del centro del paso)`.
///
/// **Comparten pared quiere decir que se TOCAN, no que se penetren.** Los rectÃƒÂ¡ngulos del plan
/// tesela la regiÃƒÂ³n, asÃƒÂ­ que dos vecinos comparten exactamente su lÃƒÂ­nea de corte; se admite un
/// centÃƒÂ­metro de holgura por si un redondeo mueve un borde, y ni uno mÃƒÂ¡s Ã¢â‚¬â€ dos rectÃƒÂ¡ngulos que se
/// pisan son un fallo del reparto, no una pared comÃƒÂºn.
pub fn rects_share_wall(a: PlanRect, b: PlanRect) -> Option<(i32, i32, i32)> {
    const TOUCH_CM: i32 = 1;

    // Pared vertical: a la derecha de `a` estÃƒÂ¡ `b`, o al revÃƒÂ©s.
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

/// Union-find con compresiÃƒÂ³n de caminos. Propio y no el de `route.rs` porque aquÃƒÂ©l es privado de
/// aquel mÃƒÂ³dulo, y exportarlo atarÃƒÂ­a dos cosas que no tienen por quÃƒÂ© moverse juntas.
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
