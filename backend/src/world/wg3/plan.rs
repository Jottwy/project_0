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
/// Sal del ensanche de una banda (repetición con variación, auditoría 2026-09-02).
const SALT_BAND: u32 = 0x9A17_0008;
/// Sal del sorteo de corredor ciego en un corte profundo.
const SALT_STUB: u32 = 0x9A17_0009;
/// Sal de dónde cae una puerta a lo largo de la pared que comparten dos espacios.
const SALT_DOOR: u32 = 0x9A17_000A;
/// Desalineación de vanos (2026-09-03): el sorteo de si un vano alineado con otro se mueve, y adónde.
const SALT_MISALIGN: u32 = 0x9A17_000F;
/// Sal del empuje hacia `Weird` de las plantas altas.
const SALT_WEIRD_UP: u32 = 0x9A17_000B;
/// ADR-120 — sal de la composición de huellas: si dispara, quién cede, y qué mordisco sale.
const SALT_SHAPE: u32 = 0x9A17_000C;
/// Sal de la altura de techo por espacio.
const SALT_CEILING: u32 = 0x9A17_000D;
/// Sal de la MEGASALA: cuántas plantas de vacío pide una nave por encima de sí misma.
const SALT_MEGA: u32 = 0x9A17_000E;
/// Sal de la PLANTA ABIERTA de oficina: qué par de hermanas se funde en la sala grande.
const SALT_OPEN_PLAN: u32 = 0x9A17_0010;
/// ADR-155 — sal de los huecos de la zona laberinto: cuántos, de qué ancho y con cuánta pared entre ellos.
const SALT_GAP: u32 = 0x9A17_0011;

/// ADR-120 D3 — las perillas de la GRAMÁTICA de composición.
///
/// La probabilidad no es un número: es una base que el contexto sube y baja. Un dado plano da
/// mordisquitos repartidos por igual, que es exactamente lo que se pidió NO hacer — «prefiero un
/// 25-35 % de espacios realmente deformados y visualmente evidentes antes que un 80 % con pequeñas
/// mordidas irrelevantes».
const DEFORM_BASE: f32 = 0.88;
/// Zona rara: ADR-110 D2 manda que la rareza suba, y una huella irregular es rareza barata.
const DEFORM_WEIRD: f32 = 0.20;
/// A partir de doce metros de pared compartida, un quiebro se lee desde dentro.
const DEFORM_LONG_WALL_CM: i32 = 1100;
const DEFORM_LONG_BONUS: f32 = 0.22;
/// Por debajo de siete metros, el mordisco no cabe o no se ve.
const DEFORM_SHORT_WALL_CM: i32 = 700;
const DEFORM_SHORT_MALUS: f32 = 0.14;
/// Grano fino al fondo del árbol: ahí las salas son pequeñas y una mordida no significa nada.
const DEFORM_DEEP_MALUS: f32 = 0.14;
/// Una nave deformada se ve; un trastero deformado no lo ve nadie.
const DEFORM_HALL_BONUS: f32 = 0.20;

/// **CINCO METROS, y el número no es de gusto: sale de dos sitios a la vez.**
///
/// Por gusto, porque es lo que separa una FORMA de un chaflán: con 150 cm el histograma de partes
/// mejora igual, la compacidad se mueve, y el mundo sigue siendo una cuadrícula de cajas con las
/// esquinas mordidas. La deformación tiene que medir lo que mide una sala pequeña o no se percibe
/// como arquitectura.
///
/// Y por obligación, que es lo que fija el número exacto: **una cara de parte es una línea de rejilla
/// que nadie puede mover** (ADR-120 D4), así que la celda que nace contra ella tiene que poder alojar
/// el vano MÁS ANCHO que el plan sabe pedir — [`WIDE_DOORWAY_CM`], el de una nave. Medido con 350: el
/// barrido se quedó en 5 huecos perdidos de 27 regiones, y un hueco perdido es una sala sellada con
/// la puerta dibujada. Todos los mínimos de esta tabla salen de ahí.
const BITE_MIN_CM: i32 = 220;
/// Y el fondo mínimo como FRACCIÓN del fondo del donante, que es lo que de verdad decide si se ve.
const BITE_MIN_FRACTION: f32 = 0.28;
/// Tope duro de fondo. Por encima, el mordisco deja de ser un bulto y se traga la sala.
const BITE_MAX_CM: i32 = 2000;
/// Fracción máxima del fondo del donante que se cede.
const BITE_MAX_FRACTION: f32 = 0.62;
/// Lo que le tiene que quedar al donante, en fracción de su área.
const BITE_DONOR_KEEPS: f32 = 0.50;
/// Área mínima del donante en m². Por debajo no hay de dónde sacar.
const BITE_DONOR_MIN_M2: f32 = 45.0;
/// Lo que le queda al donante en el eje que se muerde, como mínimo.
///
/// **No es [`MIN_SIDE_CM`], y la diferencia vale una de cada tres deformaciones.** Aquél es el lado
/// mínimo de una HOJA del reparto —lo que hace falta para que un corte del BSP produzca dos salas—;
/// lo que queda al lado de un mordisco no es una hoja nueva, es el resto de la misma sala, y con
/// pedirle los 500 se descartaban todas las salas de menos de diez metros de fondo. Es el mismo
/// razonamiento que ya hace `dig_wells` con [`GOOD_WALL_CM`]: un tramo con su puerta, no una sala.
const BITE_KEEP_MIN_CM: i32 = 300;

/// Longitud mínima del tramo cedido a lo largo de la pared.
const BITE_RUN_MIN_CM: i32 = 400;
/// Fracción máxima de la pared compartida que puede ocupar el mordisco. Ocuparla entera no es un
/// mordisco: es mover la pared, y los dos siguen siendo rectángulos.
const BITE_RUN_MAX_FRACTION: f32 = 0.94;
/// Si un extremo del tramo queda a menos de esto de la esquina del donante, se PEGA a ella.
///
/// Dos cosas de una: no deja muñones —una tira de 40 cm no es sala, y `merge_parts` no la puede
/// fundir con nadie— y hace que la L de esquina salga más veces que la U central, que es el reparto
/// que se quiere (la U cuesta una parte más y se lee peor desde dentro).
const BITE_END_SNAP_CM: i32 = 450;
/// Tramo mínimo de CADA peldaño en un mordisco de dos.
const BITE_STEP_RUN_MIN_CM: i32 = 400;
/// Diferencia mínima de fondo entre los dos peldaños. Por debajo, la Z se lee como un borde mal
/// cortado y no como una escalonada.
const BITE_STEP_DELTA_CM: i32 = 250;
/// Cuántos mordiscos son de dos peldaños. Baja a propósito: la Z es la forma más rara de la tanda y
/// tiene que seguir siéndolo.
const BITE_TWO_STEPS: f32 = 0.22;
/// Cuantas veces se vuelve a sortear el mordisco de una pareja antes de rendirse.
///
/// Cuatro, y sale de una medida: con uno solo, 15 de 22 intentos morian en la geometria -la tirada
/// caia sobre una puerta, el fondo no llegaba, los dos peldanos no cabian- y la dosis se quedaba en el
/// 2,2 % de huellas compuestas contra el 25-35 % que se pidio.
const BITE_TRIES: usize = 4;

/// Plantas que se sirven por región (ADR-102 D3).
///
/// **Dos, y no porque dos sea el número correcto**: el salto de una a dos es donde están todos los
/// fallos —el forjado que hay que perforar, la geometría de abajo que atraviesa el suelo de arriba, la
/// métrica que deja de significar lo mismo— y el de dos a tres no aporta ninguno nuevo. Subirlo es
/// cambiar este número y volver a medir, no escribir código.
///
/// **Diez desde 2026-08-29**: medido al subir (sonda `probe_ten_storeys_cost` en tests.rs) — el coste
/// de plan+fill crece lineal con las plantas y el raster sigue siendo spans `i16` en cm (33,2 m de
/// edificio contra un tope de ±327 m). El reparto de población sigue eligiendo el espacio más bajo,
/// así que las plantas altas nacen sin criaturas: hueco conocido, no regresión.
pub const REGION_STOREYS: usize = 10;

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
const GATE_CLEARANCE_CM: i32 = DOORWAY_CM / 2 + (BAND_WIDTH_CM[0] + BAND_EXTRA_MAX_CM) / 2 + 60;

/// **Ensanche de una banda, sorteado por su posición** (auditoría 2026-09-02, Fase 5: repetición
/// con variación). Cada corte que talla corredor suma uno de estos a su anchura base; en zona
/// `Weird`, uno de los de la segunda fila. Nunca resta —ADR-103 D6: el suelo de 240 es del ráster—
/// y el mayor entra en [`GATE_CLEARANCE_CM`], que tiene que despejar la banda MÁS ancha posible.
/// Es lo que hace que dos espinas de dos regiones no midan lo mismo, y que un pasillo salga «un
/// poco demasiado ancho» de vez en cuando sin que ningún corredor baje del mínimo.
///
/// **Medido**: cada 40 cm de banda de más se pagan en salas más pequeñas, o sea en menos sitios donde
/// cabe una escalera de 12,6 m. Con 0/0/40/80 el barrido perdió 0,3 plantas de media; por eso la tabla
/// normal sólo ensancha una de cada cuatro bandas y la rara se queda en 120.
const BAND_EXTRA_CM: [i32; 4] = [0, 0, 0, 40];
const BAND_EXTRA_WEIRD_CM: [i32; 4] = [0, 40, 80, 120];
const BAND_EXTRA_MAX_CM: i32 = 120;

/// Probabilidad de que un corte PROFUNDO (≥ [`CORRIDOR_DEPTH`]) talle banda igualmente: un
/// corredor entre dos salas hermanas que sólo se conecta por donde toque. Es la regla
/// `CORRIDOR → BRANCH | DEAD_END` de la gramática: la mayoría acaban con una sola salida
/// (medido con `PlanStats::dead_corridors`), y son el «pasillo que no lleva a nada» de un
/// Backrooms. Baja a propósito: por encima el mundo vuelve a ser «todo pasillos».
const STUB_CHANCE: f32 = 0.10;
/// La misma, en zona `Weird`.
const STUB_CHANCE_WEIRD: f32 = 0.22;
/// Área mínima de un bloque para que se le pueda tallar un corredor ciego, en m².
const STUB_MIN_AREA_M2: f32 = 400.0;

/// Probabilidad de que una puerta caiga CENTRADA en la pared que comparten dos espacios. El resto
/// se reparte a lo largo de la pared, sorteado por posición: «una puerta en un sitio raro» de vez en
/// cuando, sin que deje de caber (jambas de [`DOOR_JAMB_CM`] a cada lado).
const DOOR_CENTRED_CHANCE: f32 = 0.55;
/// **Probabilidad de DESALINEAR un vano** que ha quedado en el mismo eje que otro del mismo espacio
/// (2026-09-03). Dos vanos enfrentados y alineados dejan ver de sala en sala en línea recta: la
/// planta se lee como un pasillo con paredes de quita y pon. Con esta probabilidad el vano que se
/// creó más tarde se corre por su pared hasta salir del eje del otro. Sorteo por posición, nunca
/// RNG compartido: la misma pared da el mismo resultado en cualquier proceso.
pub const DOOR_MISALIGN_CHANCE: f32 = 0.75;

/// Dos vanos sobre paredes PARALELAS de un mismo espacio se consideran alineados si sus centros
/// distan menos de esto a lo largo de la pared: un vano y una jamba, o sea, cuando los huecos se
/// solapan en proyección y se ve a través.
const DOOR_ALIGN_TOL_CM: i32 = DOORWAY_CM + DOOR_JAMB_CM;
/// Jamba mínima entre una puerta descentrada y la esquina de la pared.
const DOOR_JAMB_CM: i32 = 30;

/// Cuánto sube, por planta a partir de la segunda, la probabilidad de que una zona `Large` se lea
/// como `Weird`. Fase 7: la progresión del jugador es la PROFUNDIDAD (plantas), así que la rareza
/// crece con ella y no con la distancia al origen, que en un mundo infinito no significa nada.
const WEIRD_PER_STOREY: f32 = 0.10;
const WEIRD_STOREY_CAP: f32 = 0.40;

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

/// **ADR-110 D3 / T2 — LA PLANTA A LA QUE PERTENECE UNA COTA DE SUELO.**
///
/// El eje vertical de WG3, y el sustituto de `grid_gen::world_pos_to_layer` para todo lo que decida
/// población. La diferencia no es el número, es la UNIDAD: aquélla parte el mundo en cajones de 4 m
/// que no alinean con plantas de 3,32, así que la planta 1 caía en la capa 1 —densidad cero— y de la
/// 4 en adelante todas colapsaban en la capa 3 por saturación de `u8`. Medido en T0
/// (`docs/PLAN-PLANTAS-ALTAS.md`, sección «T0 EJECUTADA»).
///
/// `div_euclid` y no una división normal, porque **hay geometría por debajo de la cota 0** —peldaños
/// y celdas de conector que bajan, 113 tramos en las cuatro regiones auditadas— y una división que
/// trunca hacia cero metería la planta −1 en la 0. Que es justo lo que hacía `(y / 4.0) as u8`, donde
/// una Y negativa satura a 0 en Rust y un jugador a −1,52 m se clasificaba en la planta baja.
///
/// Un peldaño a 306 cm pertenece a la planta 0, que es de donde sale su escalera.
///
/// # El canto de la losa cuenta como la planta de ARRIBA (2026-09-06)
///
/// **Una cota que cae dentro de los 12 cm de losa que hay justo debajo de una planta es esa planta.**
/// La losa cuelga por DEBAJO de la cota de su planta (ver [`STOREY_HEIGHT_CM`]), asi que un sitio de
/// pie apoyado en el canto -el suelo desnudo donde no llega el forjado de la sala- sale a
/// `332 n - 12` y la division lo mandaba una planta ABAJO. Medido con una cama atascada en la planta
/// 2: el resolutor la corregia medio metro al lado, a un suelo de 6,52 contra los 6,64 de la planta,
/// y el respawn la daba por planta 1 -o sea, quien renace conserva una planta asignada que ya no es
/// la suya-. Es la costura de 664 que `docs/STATE.md` llevaba declarada como deuda.
pub fn storey_of_floor_cm(floor_y_cm: i32) -> i32 {
    let storey = floor_y_cm.div_euclid(STOREY_HEIGHT_CM);
    let within = floor_y_cm.rem_euclid(STOREY_HEIGHT_CM);
    // **Y solo de la planta 1 hacia arriba**, que es donde el canto es canto y nada mas.
    // Por debajo de cero la regla se daria de bruces con la de T0 -- una cota negativa NO es la
    // planta baja, porque el eje viejo saturaba ahi y poblaba la calle con peldanos que bajan --, y
    // en la costura de 332 viven las contrahuellas de las escaleras de la planta baja, que
    // pertenecen a la planta de la que ARRANCAN. Las dos cosas tienen test propio.
    if storey >= 1 && within >= STOREY_HEIGHT_CM - SLAB_CM {
        storey + 1
    } else {
        storey
    }
}

/// El canto de la losa en centimetros enteros. Espejo de `segment::SLAB_THICKNESS_M`, que va en
/// metros porque lo consume la geometria; aqui todo son centimetros.
const SLAB_CM: i32 = (SLAB_THICKNESS_M * CM_PER_M) as i32;

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
///
/// **ADR-124 (2026-09-04): se probó bajarlo y NO se sostiene, ni a dos ni a «dos y medio».** Joel:
/// «menos pasillos pero más largos». Con banda sólo en dos niveles: 6 de 300 regiones rotas (enlaces
/// del plan que el enrutador no construye, un tramo de 2540 cm sobre el tope, una planta 2
/// inalcanzable), plantas 3,2 → 2,9 y cruces 24,6 → 10,0 por región — lo contrario de «que se
/// encuentren caminos». Con el tercer nivel tallando banda la mitad de las veces: 1 de 300 rota por
/// el mismo enlace y plantas 3,0. El tercer nivel de banda es lo que da acceso a las salas que no
/// tocan la espina; quitarlo pide reescribir el enrutador, no una constante. Queda pendiente ahí.
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

/// ADR-155 enm. 2 — **interruptor del bioma laberinto en el PLAN.** Apagado hasta que los huecos
/// múltiples (L1d) y sus barridos (L1e) estén en verde: con esto a `false` el mundo servido es el de
/// antes al centímetro, aunque el edificio ya lleve la semilla de la zona.
const MAZE_BIOME_ENABLED: bool = false;
/// ADR-155 enm. 1 D1 — profundidad del BSP a la que se decide la zona: los CUARTOS de región (~75 m).
/// Por encima (región, mitades) la zona sería una región entera o nada; por debajo, un tablero de
/// casillas sueltas sin megazona que recorrer. Los hijos heredan la marca.
const MAZE_ZONE_DEPTH: u8 = 2;
/// ADR-155 D2 / enm. 1 — área a la que PARA la subdivisión dentro de la zona. Hojas de 15 × 30 m de
/// media, entre los 10 y 25 m de lado que pide el ADR y bajo el tope de tramo de 25 m.
const MAZE_TARGET_AREA_M2: f32 = 450.0;
/// ADR-155 D2 — anchos de un hueco de la zona laberinto. Por encima de `DOORWAY_CM` y hasta 4 m: el
/// tabique a trozos de Level 0, no una puerta.
const MAZE_GAP_WIDTHS_CM: [i32; 5] = [240, 280, 320, 360, 400];
/// ADR-155 enm. 1/2 — trozo de pared mínimo entre dos huecos y en cada esquina. Igual que
/// `fill::OPENING_JAMB_CM`: por debajo de ~100 cm el ráster convierte el trozo en una celda maciza o
/// lo borra, y 180 es la jamba que el relleno ya usa para no pisar vanos.
const MAZE_GAP_PIECE_CM: i32 = 180;
/// ADR-155 — cuánto puede crecer al azar ese trozo, para que los huecos no caigan a paso fijo.
const MAZE_GAP_PIECE_EXTRA_CM: i32 = 320;

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
///
/// # ADR-119 D2 — subidos, y el reparto de TAMANOS no lo decide el area de cada clase
///
/// **El numero de hojas de una zona es `area / objetivo`, no `area`.** Con 110/240/700/380 una zona
/// estrecha producia seis veces mas hojas por metro cuadrado que una grande, asi que el 18,6 % de
/// superficie `Large` de ADR-095 se convertia en un 5 % de los ESPACIOS y el mundo medido salia con
/// el 93,3 % por debajo de 300 m2 y una media de 145 m2 — la queja de «demasiadas salas pequenas»,
/// que resulta ser aritmetica y no gusto.
///
/// Estos valores se eligieron con el reparto nuevo de `scale_at` delante y la calibracion medida de
/// que **una hoja mide de media 0,71 veces su objetivo** (la subdivision para cuando el area cae por
/// debajo, no cuando lo alcanza). Prediccion antes de correr nada: media ~270 m2 y ~25 % de espacios
/// por encima de 300 m2. Lo que valga de verdad lo dice `probe_architecture_metrics`.
const TARGET_AREA_M2: [f32; 4] = [
    150.0,  // SCALE_NARROW — despachos
    360.0,  // SCALE_MEDIUM — oficinas normales
    1100.0, // SCALE_LARGE  — naves, salas diáfanas de un chunk de lado
    700.0,  // SCALE_WEIRD  — ver `WEIRD_SPREAD`: aquí el número no manda solo
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

/// **LA PLANTA ABIERTA DE OFICINA: el rango de área que se busca, en m².**
///
/// La imagen canónica —300-500 m² diáfanos con treinta puestos en filas— no salía del reparto y no
/// es culpa de un número mal puesto: el tamaño de hoja lo fija `TARGET_AREA_M2[class]` y el objetivo
/// de la zona `Medium` es 360, así que una hoja media mide 256 m² (0,71 × objetivo) y las que pasan
/// de 300 se convierten en `Hall`. Subir el objetivo para conseguir una sala grande sube TODAS las
/// salas de la zona, que es justamente lo que ADR-119 D2 midió y calibró.
///
/// Por eso esto **no toca la subdivisión**: se funden DOS hermanas ya repartidas (ver
/// [`Planner::fuse_open_plan`]). El árbol, las bandas de corredor, los ciegos y los candidatos a
/// agujero de forjado quedan idénticos, y por tanto el reparto vertical no se mueve.
const OPEN_PLAN_MIN_M2: f32 = 300.0;
const OPEN_PLAN_MAX_M2: f32 = 500.0;

/// Proporción máxima de la sala fundida. Dos hermanas estrechas dan una unión de 3:1, y en una nave
/// de 3:1 las filas de cubículos salen a lo largo de una sola pared: eso no es una planta abierta,
/// es un pasillo con mesas.
const OPEN_PLAN_MAX_ASPECT: f32 = 1.9;

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
/// **ADR-124 (2026-09-04): se probó 0,45 y se revirtió.** «Que se encuentren caminos» pide más vanos
/// de más, y con 0,45 el barrido salía limpio (210 enlaces por región contra 190, mancha mayor
/// 99,8 %), pero el plan cambia bajo los pozos y los agujeros de forjado de las cuatro regiones de
/// referencia caían de 8 a 5, por debajo del listón de `a_hole_drops_you_a_whole_storey`. Mover
/// esto es mover el reparto vertical entero: va con el enrutador, no solo.
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
    // BORRADA `Junction` («donde dos bandas se cruzan») el 2026-09-05, bloque B4 del saneamiento:
    // estaba declarada y en tres `match`, y NADIE se la asignaba nunca a un espacio. El cruce de
    // dos bandas sí existe en el plan, pero como `LinkKind::Junction` (un enlace, que sí se
    // produce en `plan.rs`), no como papel de un espacio. Dos cosas distintas con el mismo nombre,
    // y sólo una viva. Si algún día una intersección tiene que leerse como sitio propio, esto se
    // vuelve a añadir CON su productor en el mismo commit.
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
        matches!(self, SpaceRole::Spine | SpaceRole::Corridor)
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
            SpaceRole::Hall => "hall",
            SpaceRole::Office => "office",
            SpaceRole::Service => "service",
            SpaceRole::Storage => "storage",
            SpaceRole::DeadEnd => "dead_end",
            SpaceRole::Void => "void",
        }
    }
}

/// ADR-120 D1 — cuántos rectángulos puede tener la HUELLA de un espacio.
///
/// **Cuatro, y el número sale de las formas que se quieren, no de un gusto.** Un mordisco de un solo
/// escalón deja al donante en L (2) o en U (3); uno de dos escalones lo deja en Z con los dos
/// extremos libres, que es el caso peor (4). Por encima de cuatro no hay ninguna forma nueva en esta
/// tanda: hay polígonos, y ésos piden su propia medida.
///
/// Es un array y no un `Vec` **para no perder `Copy`**: `PlannedSpace` se copia en 59 sitios de este
/// módulo y 35 del relleno, y quitarle `Copy` es exactamente la refactorización masiva que este
/// trabajo tiene prohibida.
pub const MAX_PARTS: usize = 4;

/// Cara mínima entre dos partes de la MISMA huella, en centímetros.
///
/// No es estética: por debajo del mínimo generado el ráster conservador tapia el paso, así que dos
/// partes que se tocan por menos que esto **no son un espacio, son dos** — y uno de los dos nace
/// sellado sin que ningún contador se entere.
const PART_JOIN_CM: i32 = super::segment::MIN_GENERATED_WIDTH_CM;

/// Un espacio del plan: un sitio del edificio, con su papel, antes de que exista una sola pieza.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlannedSpace {
    /// **LA CAJA ENVOLVENTE, y desde ADR-120 sólo eso.**
    ///
    /// Hasta ADR-120 este campo era las dos cosas a la vez —la envolvente Y la huella exacta— porque
    /// el BSP tesela en rectángulos y las dos coincidían para todo espacio. En cuanto una hoja se
    /// deforma dejan de coincidir: `rect` cubre terreno que es del vecino, `rect.area_m2()` cuenta de
    /// más y `rect.contains_point` dice que sí a un punto que está fuera.
    ///
    /// Se conserva, y no por compatibilidad: **sobreestimar es la dirección segura** en casi todo lo
    /// que la usa —el tope de altura de la planta de abajo, la ocupación que el enrutador esquiva, la
    /// caja que decide si dos espacios pueden llegar a tocarse—. Lo que quiere saber si un punto o un
    /// rectángulo está sobre suelo de este espacio tiene que preguntar por la HUELLA
    /// ([`PlannedSpace::parts`] y sus ayudas), no por esto.
    pub rect: PlanRect,
    /// La huella real: hasta [`MAX_PARTS`] rectángulos disjuntos cuya unión es conexa.
    ///
    /// Sólo las `part_count` primeras cuentan. Con `part_count == 1`, `parts[0] == rect` y el mundo
    /// es exactamente el de antes de ADR-120.
    pub(super) parts: [PlanRect; MAX_PARTS],
    /// Cuántas de [`PlannedSpace::parts`] valen. Nunca cero.
    pub(super) part_count: u8,
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

    /// **Cuántas plantas seguidas hay vacías justo encima de esta huella.**
    ///
    /// `void_above` es esto mismo preguntado con un sí o un no, y durante dos ADRs bastó porque la
    /// respuesta sólo se usaba para decidir si una nave medía una planta o dos. La MEGASALA necesita
    /// el número: su altura libre es `(1 + n)` plantas menos dos losas, y el vano que abre en el muro
    /// se recorta **planta por planta** — un solo cajón desde la primera hasta la última se lleva por
    /// delante los forjados intermedios, que es exactamente cómo se midió el fallo:
    /// `espacio 0 (spine) a cota 664 con suelo en el 0 % de sus celdas`.
    ///
    /// Cuenta sólo plantas que EXISTEN: la última del edificio vale cero, igual que `void_above`.
    pub void_storeys_above: u8,

    /// **Cuántas alturas de planta mide este atrio.** `0` = no es atrio.
    ///
    /// Dos es el atrio de ADR-104; más es una MEGASALA (enm. 4). Lo pone [`atrium_storeys_for`] al
    /// cerrar el edificio, que es el único sitio donde se sabe cuántas plantas hay encima.
    pub atrium_storeys: u8,

    /// **La altura libre que ESTE espacio pide, en centímetros. `0` = la de su papel.**
    ///
    /// Cero no es un techo de cero: es «este espacio no ha pedido nada», y entonces manda
    /// `fill::clear_height_by_role` igual que antes de que este campo existiera. Es lo que deja la
    /// perilla apagada devolviendo el mundo de siempre al centímetro, sin una segunda rama de código
    /// que mantener.
    ///
    /// Lo pone [`assign_ceilings`], y **no lo pone el papel**: dos oficinas contiguas medían lo mismo
    /// porque las dos eran oficinas, y una planta de oficinas era un techo plano de ciento cincuenta
    /// metros. El tope de [`PlannedSpace::max_clear_cm`] sigue mandando por encima de esto.
    pub ceiling_clear_cm: i32,

    /// **Es LA planta abierta de esta planta del edificio** (ver [`Planner::fuse_open_plan`]).
    ///
    /// Una por planta como mucho, y no es lo mismo que «oficina grande»: el relleno la viste SIEMPRE
    /// de puestos, sin pasar por el sorteo de `Knobs::cubicles`, porque la sala existe precisamente
    /// para ser eso. Sin la marca habría que adivinarlo por el área, y por área también pasan salas
    /// que el reparto dio grandes por su cuenta.
    pub open_plan: bool,
    /// ADR-155 — **hoja del bioma laberinto.** No se vacía, no se funde, no se deforma y un bolsillo
    /// suyo no se declara vacío en silencio: la zona es una planta continua o no es nada.
    pub maze: bool,
}

impl PlannedSpace {
    /// Un espacio rectangular: la huella es su rectángulo y no hay nada que componer.
    pub fn of_rect(rect: PlanRect, template: PlannedSpace) -> PlannedSpace {
        PlannedSpace {
            rect,
            parts: [rect; MAX_PARTS],
            part_count: 1,
            ..template
        }
    }

    /// Las partes de la huella. Una sola en el caso normal, y entonces ES el rectángulo.
    pub fn parts(&self) -> &[PlanRect] {
        &self.parts[..self.part_count as usize]
    }

    /// ¿Tiene este espacio una huella deformada? Lo que no lo es se comporta como siempre.
    pub fn is_composite(&self) -> bool {
        self.part_count > 1
    }

    /// Área de la HUELLA en m². No es `rect.area_m2()` en cuanto hay más de una parte, y confundirlas
    /// infla el área construida de la región con terreno que es del vecino.
    pub fn area_m2(&self) -> f32 {
        self.parts().iter().map(|p| p.area_m2()).sum()
    }

    /// ¿Cae este punto dentro de la huella, bordes EXCLUIDOS? Mismo criterio que
    /// [`PlanRect::contains_point`], que es de donde sale.
    pub fn contains_point(&self, x_cm: i32, z_cm: i32) -> bool {
        self.parts().iter().any(|p| p.contains_point(x_cm, z_cm))
    }

    /// ¿Pisa este rectángulo algo de la huella? Tocarse NO cuenta, igual que en [`PlanRect::overlaps`].
    pub fn hits_rect(&self, r: &PlanRect) -> bool {
        self.parts().iter().any(|p| p.overlaps(r))
    }

    /// ¿Está este rectángulo ENTERO sobre suelo de este espacio?
    ///
    /// **No basta con preguntárselo a cada parte por separado**: un rectángulo a caballo de dos partes
    /// no está dentro de ninguna y sí está dentro de la unión. Se resuelve partiendo por todas las
    /// líneas que aportan las partes y comprobando cada trozo, que con cuatro partes son 81 pruebas
    /// como mucho y es exacto — una aproximación aquí planta un macizo o un agujero de suelo en la
    /// sala de al lado.
    pub fn covers_rect(&self, r: &PlanRect) -> bool {
        if r.width_cm() <= 0 || r.depth_cm() <= 0 {
            return false;
        }
        if self.part_count == 1 {
            return self.parts[0].contains_rect(r);
        }
        let mut xs = vec![r.min_x_cm, r.max_x_cm];
        let mut zs = vec![r.min_z_cm, r.max_z_cm];
        for p in self.parts() {
            for v in [p.min_x_cm, p.max_x_cm] {
                if v > r.min_x_cm && v < r.max_x_cm {
                    xs.push(v);
                }
            }
            for v in [p.min_z_cm, p.max_z_cm] {
                if v > r.min_z_cm && v < r.max_z_cm {
                    zs.push(v);
                }
            }
        }
        xs.sort_unstable();
        xs.dedup();
        zs.sort_unstable();
        zs.dedup();
        // **Por CONTENCIÓN del trozo, no por su punto medio.** Partiendo por todas las caras, cada
        // trozo está entero dentro de una parte o entero fuera, así que preguntar por el rectángulo
        // es exacto — y preguntar por su centro NO lo es: el punto medio entero de un trozo de un
        // centímetro cae sobre la cara que separa dos partes y las dos lo excluyen. Con eso, un pilar
        // que asomaba un centímetro de una parte a la otra de SU MISMA sala salía «fuera de todo
        // espacio construido».
        for w in xs.windows(2) {
            for d in zs.windows(2) {
                let cell = PlanRect {
                    min_x_cm: w[0],
                    min_z_cm: d[0],
                    max_x_cm: w[1],
                    max_z_cm: d[1],
                };
                if !self.parts().iter().any(|p| p.contains_rect(&cell)) {
                    return false;
                }
            }
        }
        true
    }

    /// **¿En qué pared de este espacio cae este punto? LA fuente única de esa respuesta.**
    ///
    /// `half_cm` es lo que tiene que caber a cada lado dentro de esa pared. Devuelve el lado
    /// (`0 = N`, `1 = E`, `2 = S`, `3 = O`) o `None`.
    ///
    /// La pregunta la hacen dos módulos —el plan, para declarar ilegal un enlace que nadie podrá
    /// abrir; el relleno, para abrirlo— y **tienen que dar la misma respuesta**, o el plan promete
    /// puertas que la geometría no pone. Con huellas compuestas eso deja de ser evidente: hay paredes
    /// que sólo tiene una parte, y hay caras entre dos partes que NO son pared. Por eso vive aquí una
    /// sola vez y `fill::wall_side` la llama.
    ///
    /// La cara que separa dos partes del mismo espacio no cuenta: se comprueba empujando el punto
    /// tres centímetros hacia afuera y mirando si sigue dentro de la huella. En una U, el fondo de la
    /// muesca SÍ es pared —lo de enfrente es del vecino—, y esta prueba lo distingue sola.
    pub fn wall_side_of(&self, x_cm: i32, z_cm: i32, half_cm: i32) -> Option<u8> {
        const EPS: i32 = 2;
        for r in self.parts() {
            for side in 0u8..4 {
                let on = match side {
                    0 => {
                        (r.max_z_cm - z_cm).abs() <= EPS
                            && x_cm - half_cm >= r.min_x_cm
                            && x_cm + half_cm <= r.max_x_cm
                    }
                    1 => {
                        (r.max_x_cm - x_cm).abs() <= EPS
                            && z_cm - half_cm >= r.min_z_cm
                            && z_cm + half_cm <= r.max_z_cm
                    }
                    2 => {
                        (r.min_z_cm - z_cm).abs() <= EPS
                            && x_cm - half_cm >= r.min_x_cm
                            && x_cm + half_cm <= r.max_x_cm
                    }
                    _ => {
                        (r.min_x_cm - x_cm).abs() <= EPS
                            && z_cm - half_cm >= r.min_z_cm
                            && z_cm + half_cm <= r.max_z_cm
                    }
                };
                if !on {
                    continue;
                }
                let (ox, oz) = match side {
                    0 => (x_cm, z_cm + EPS + 1),
                    1 => (x_cm + EPS + 1, z_cm),
                    2 => (x_cm, z_cm - EPS - 1),
                    _ => (x_cm - EPS - 1, z_cm),
                };
                if !self.contains_point(ox, oz) {
                    return Some(side);
                }
            }
        }
        None
    }

    /// Instala una huella compuesta: fija las partes y **recalcula la envolvente**, que es la única
    /// forma de que `rect` no mienta.
    ///
    /// Devuelve `false` y no toca nada si lo que se le pasa no es una huella legal: más de
    /// [`MAX_PARTS`] partes, ninguna, alguna degenerada, dos que se pisen, o una unión que no es
    /// conexa por caras de al menos [`PART_JOIN_CM`].
    pub(super) fn set_parts(&mut self, parts: &[PlanRect]) -> bool {
        if parts.is_empty() || parts.len() > MAX_PARTS {
            return false;
        }
        if parts.iter().any(|p| p.width_cm() <= 0 || p.depth_cm() <= 0) {
            return false;
        }
        for i in 0..parts.len() {
            for j in (i + 1)..parts.len() {
                if parts[i].overlaps(&parts[j]) {
                    return false;
                }
            }
        }
        if !parts_are_joined(parts) {
            return false;
        }
        let mut slot = [parts[0]; MAX_PARTS];
        for (k, p) in parts.iter().enumerate() {
            slot[k] = *p;
        }
        self.parts = slot;
        self.part_count = parts.len() as u8;
        self.rect = PlanRect {
            min_x_cm: parts.iter().map(|p| p.min_x_cm).min().unwrap(),
            min_z_cm: parts.iter().map(|p| p.min_z_cm).min().unwrap(),
            max_x_cm: parts.iter().map(|p| p.max_x_cm).max().unwrap(),
            max_z_cm: parts.iter().map(|p| p.max_z_cm).max().unwrap(),
        };
        true
    }
}

/// ¿Deja esta huella celdas de rejilla suficientemente anchas en los dos ejes?
///
/// El relleno tesela por la rejilla que forman las CARAS de las partes, así que la distancia entre
/// dos caras consecutivas ES el lado de una celda y también la anchura de la boca que la une con su
/// vecina. Por debajo de [`PART_JOIN_CM`] el ráster tapia esa boca y el espacio nace partido por
/// dentro.
/// ¿Toda celda de la rejilla que forman estas partes mide al menos `min_cm` en los dos ejes?
///
/// Es [`grid_spans_are_wide`] con el umbral por parámetro. Existe aparte porque el umbral de aquélla
/// es una regla del ráster —lo que se puede generar— y éste es una regla de circulación: por dónde
/// se tiene que poder pasar.
fn cells_are_at_least(parts: &[PlanRect], min_cm: i32) -> bool {
    if parts.len() < 2 {
        return true;
    }
    for along_x in [true, false] {
        let mut faces: Vec<i32> = Vec::with_capacity(parts.len() * 2);
        for p in parts {
            if along_x {
                faces.push(p.min_x_cm);
                faces.push(p.max_x_cm);
            } else {
                faces.push(p.min_z_cm);
                faces.push(p.max_z_cm);
            }
        }
        faces.sort_unstable();
        faces.dedup();
        if faces.windows(2).any(|w| w[1] - w[0] < min_cm) {
            return false;
        }
    }
    true
}

fn grid_spans_are_wide(parts: &[PlanRect]) -> bool {
    // **Un rectángulo suelto no tiene ninguna cara INTERNA, así que aquí no se le pide nada.**
    //
    // La regla existe por las líneas que una huella compuesta añade dentro del espacio; las de fuera
    // son sus propias paredes y siempre han podido medir lo que midieran. Aplicársela igualmente
    // costó una planta entera: `dig_wells` llama aquí con la sala YA recortada por la franja de
    // escalera, y a esa sala sólo se le pide un vestíbulo con su puerta (`GOOD_WALL_CM`, 360) — la
    // región (0,1) de la semilla viva perdió su única candidata por 140 cm.
    if parts.len() < 2 {
        return true;
    }
    for along_x in [true, false] {
        let mut faces: Vec<i32> = Vec::with_capacity(parts.len() * 2);
        for p in parts {
            if along_x {
                faces.push(p.min_x_cm);
                faces.push(p.max_x_cm);
            } else {
                faces.push(p.min_z_cm);
                faces.push(p.max_z_cm);
            }
        }
        faces.sort_unstable();
        faces.dedup();
        if faces.windows(2).any(|w| w[1] - w[0] < PART_JOIN_CM) {
            return false;
        }
    }
    true
}

/// ¿Es la unión de estas partes UNA sola pieza, unidas por caras por las que se pasa?
///
/// Recorrido por componentes sobre la relación «comparten cara de al menos [`PART_JOIN_CM`]». Una
/// huella que no lo cumpla no es un espacio raro: son dos espacios, y uno nace tapiado.
fn parts_are_joined(parts: &[PlanRect]) -> bool {
    let n = parts.len();
    let mut seen = vec![false; n];
    let mut stack = vec![0usize];
    seen[0] = true;
    while let Some(i) = stack.pop() {
        for j in 0..n {
            if seen[j] || !parts_touch(&parts[i], &parts[j]) {
                continue;
            }
            seen[j] = true;
            stack.push(j);
        }
    }
    seen.iter().all(|&s| s)
}

/// ¿Se tocan estos dos rectángulos por una cara de al menos [`PART_JOIN_CM`]?
///
/// Es [`rects_share_wall`] con otro umbral y sin devolver el punto: aquélla pide sitio para una
/// PUERTA, y entre dos partes del mismo espacio no hay puerta, hay continuidad.
fn parts_touch(a: &PlanRect, b: &PlanRect) -> bool {
    let touch_x = a.max_x_cm == b.min_x_cm || b.max_x_cm == a.min_x_cm;
    if touch_x {
        let lo = a.min_z_cm.max(b.min_z_cm);
        let hi = a.max_z_cm.min(b.max_z_cm);
        return hi - lo >= PART_JOIN_CM;
    }
    let touch_z = a.max_z_cm == b.min_z_cm || b.max_z_cm == a.min_z_cm;
    if touch_z {
        let lo = a.min_x_cm.max(b.min_x_cm);
        let hi = a.max_x_cm.min(b.max_x_cm);
        return hi - lo >= PART_JOIN_CM;
    }
    false
}

/// Funde las partes que juntas forman un rectángulo exacto, hasta que no quede ninguna.
///
/// **No es cosmética: es lo que impide que un ensanche se cuente como deformación.** Si una parte
/// cedida cubre toda la cara del receptor, la unión vuelve a ser un rectángulo; dejarlo en dos partes
/// emitiría dos tramos donde hay uno y contaría como «compuesto» un espacio que no lo es.
fn merge_parts(parts: &mut Vec<PlanRect>) {
    loop {
        let mut fused = None;
        'outer: for i in 0..parts.len() {
            for j in (i + 1)..parts.len() {
                let (a, b) = (parts[i], parts[j]);
                let same_z = a.min_z_cm == b.min_z_cm && a.max_z_cm == b.max_z_cm;
                let same_x = a.min_x_cm == b.min_x_cm && a.max_x_cm == b.max_x_cm;
                let joins_x = a.max_x_cm == b.min_x_cm || b.max_x_cm == a.min_x_cm;
                let joins_z = a.max_z_cm == b.min_z_cm || b.max_z_cm == a.min_z_cm;
                if (same_z && joins_x) || (same_x && joins_z) {
                    fused = Some((
                        i,
                        j,
                        PlanRect {
                            min_x_cm: a.min_x_cm.min(b.min_x_cm),
                            min_z_cm: a.min_z_cm.min(b.min_z_cm),
                            max_x_cm: a.max_x_cm.max(b.max_x_cm),
                            max_z_cm: a.max_z_cm.max(b.max_z_cm),
                        },
                    ));
                    break 'outer;
                }
            }
        }
        let Some((i, j, r)) = fused else { return };
        parts.remove(j);
        parts[i] = r;
    }
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
    /// ADR-155 D2 — uno de VARIOS huecos entre dos hojas de la zona laberinto, en la misma pared.
    /// Para el relleno es un vano como `Doorway`; la diferencia es que no se desalinea: su sitio lo
    /// fija el reparto de trozos de pared.
    Gap,
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
        self.built().map(|(_, s)| s.area_m2()).sum()
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
        //
        // **Y se compara HUELLA contra HUELLA, no envolvente contra envolvente** (ADR-120 D1). Dos
        // espacios entrelazados tienen envolventes que se pisan por definición: es lo que significa
        // que uno se meta en el otro. Lo que sigue estando prohibido —y es lo que esta comprobación
        // sostiene— es que dos espacios reclamen el mismo suelo.
        for i in 0..self.spaces.len() {
            for j in (i + 1)..self.spaces.len() {
                if !self.spaces[i].rect.overlaps(&self.spaces[j].rect) {
                    continue;
                }
                let clash = self.spaces[i]
                    .parts()
                    .iter()
                    .any(|a| self.spaces[j].parts().iter().any(|b| a.overlaps(b)));
                if clash {
                    out.push(format!("espacios {i} y {j} se solapan"));
                }
            }
        }
        // Y la huella de cada uno tiene que ser legal por sí sola: partes disjuntas y unión conexa.
        // Sin esto, una composición mal calculada produce un trozo de sala tapiado que no se ve en
        // ningún contador — el suelo existe, se dibuja, y no se llega.
        for (i, s) in self.spaces.iter().enumerate() {
            if !s.is_composite() {
                continue;
            }
            let ps = s.parts();
            for a in 0..ps.len() {
                for b in (a + 1)..ps.len() {
                    if ps[a].overlaps(&ps[b]) {
                        out.push(format!("espacio {i}: sus partes {a} y {b} se pisan"));
                    }
                }
            }
            if !parts_are_joined(ps) {
                out.push(format!(
                    "espacio {i}: huella de {} partes que no forman una sola pieza",
                    ps.len()
                ));
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

            // **Auditoría 2026-09-02 — EL PUNTO DEL PASO TIENE QUE ESTAR EN UNA PARED DE LOS DOS.**
            //
            // Es lo único que el relleno necesita de un enlace, y era lo único que nadie comprobaba:
            // un espacio recortado por una escalera se quedaba con enlaces en paredes que ya no
            // tenía, `fill` los apuntaba como fallidos y el edificio pasaba `problems()` con una
            // docena de salas selladas dentro. Un `Route` queda fuera: no tiene pared, tiene
            // enrutador.
            if l.kind != LinkKind::Route {
                for (who, s) in [(l.a, a), (l.b, b)] {
                    if !door_fits_in(s, l.at_x_cm, l.at_z_cm) {
                        out.push(format!(
                            "enlace {i}: su paso en ({},{}) no cae en ninguna pared del espacio \
                             {who} — el relleno no puede abrirlo",
                            l.at_x_cm, l.at_z_cm
                        ));
                    }
                }
            }
        }
        for (i, g) in self.gates.iter().enumerate() {
            match self.spaces.get(g.space) {
                Some(s) if door_fits_in(s, g.x_cm, g.z_cm) => {}
                Some(_) => out.push(format!(
                    "puerta de junta {i}: su punto ({},{}) no cae en ninguna pared del espacio {}",
                    g.x_cm, g.z_cm, g.space
                )),
                None => out.push(format!("puerta de junta {i}: espacio fuera de rango")),
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

/// Un corte prohibido por una puerta de junta: `(coordenada, desde, hasta)` sobre el eje
/// perpendicular. Ver [`gate_cut_spans`].
type GateCut = (i32, i32, i32);

/// Los mismos cortes que [`gate_coordinates`], **pero con hasta dónde llegan hacia dentro**.
///
/// El corte de una puerta de junta es `(coordenada, desde, hasta)` sobre el eje perpendicular. Es la
/// diferencia entre «ninguna pared nueva a esta x» y «ninguna pared nueva a esta x *donde la puerta
/// la puede sentir*»: lo primero prohíbe una pared a ciento cuarenta metros de la puerta, en el otro
/// extremo de la región, que no la puede sellar de ninguna manera. Medido en la región (1,0): **174
/// de 216 mordiscos descartados por geometría morían aquí**, y eran el mayor motivo con diferencia.
///
/// El alcance es el mismo que el del cerco blando —lo que hay que proteger no es la puerta sino el
/// camino que llega a ella— y por la misma razón: más allá hay región de sobra para rodear.
fn gate_cut_spans(gates: &[Wg3Gate], along_x: bool, reach_cm: i32) -> Vec<GateCut> {
    let mut out: Vec<GateCut> = gates
        .iter()
        .filter(|g| g.outward_side.is_multiple_of(2) == along_x)
        .map(|g| {
            let cut = ((if along_x { g.x } else { g.z }) * CM_PER_M).round() as i32;
            let perp = ((if along_x { g.z } else { g.x }) * CM_PER_M).round() as i32;
            let (dx, dz) = match g.outward_side % 4 {
                0 => (0, -1),
                1 => (-1, 0),
                2 => (0, 1),
                _ => (1, 0),
            };
            let step = if along_x { dz } else { dx } * reach_cm;
            (cut, perp.min(perp + step), perp.max(perp + step))
        })
        .collect();
    out.sort_unstable();
    out.dedup();
    out
}

/// Hasta dónde hacia dentro puede una pared nueva sellar una puerta de junta. Ver [`gate_cut_spans`].
const GATE_CUT_REACH_CM: i32 = 3000;

/// Nodo del árbol de subdivisión. Vive sólo mientras se planifica.
struct Node {
    rect: PlanRect,
    depth: u8,
    children: Option<(usize, usize)>,
    /// **Este nodo ya no existe**: su padre se lo comió al fundir la planta abierta. Se marca en vez
    /// de borrarse porque los índices de `children` son posiciones en este vector, y compactarlo
    /// invalidaría los de todos los nodos posteriores.
    dropped: bool,
    /// Este nodo es la planta abierta de su planta: sale como hoja aunque tenga el área de una nave,
    /// y con papel de oficina. Ver [`Planner::fuse_open_plan`].
    open_plan: bool,
    /// ADR-155 — el nodo cae en zona laberinto: sus cortes no tallan pasillo y paran en
    /// [`MAZE_TARGET_AREA_M2`]. Se decide en [`MAZE_ZONE_DEPTH`] y se hereda.
    maze: bool,
}

impl Node {
    /// Un nodo recién nacido: hoja, vivo y sin papel especial. Lo demás se le pone después.
    fn leaf(rect: PlanRect, depth: u8) -> Node {
        Node {
            rect,
            depth,
            children: None,
            dropped: false,
            open_plan: false,
            maze: false,
        }
    }
}

/// **EL PLAN DE UNA REGIÓN.** Función pura: misma semilla, misma caja y mismas puertas ⇒ mismo
/// edificio (R3).
///
/// `gates` son las puertas de junta que ya acordó [`super::junction`] con la región vecina. Entran
/// como restricción y no como sugerencia: son el único punto del plan que NO se decide aquí, porque
/// ya está acordado con alguien que no puede consultarse.
/// **LA ALTURA DE TECHO POR ESPACIO — el techo deja de ser una propiedad del PAPEL.**
///
/// Hasta aquí la altura libre salía entera de `fill::clear_height_by_role`: dos oficinas contiguas
/// medían exactamente lo mismo porque las dos eran oficinas, y una planta de oficinas era un techo
/// plano de ciento cincuenta metros. Esto la mueve al ESPACIO y la sortea por POSICIÓN (R3), así que
/// dos vecinas se distinguen sin que nadie las coordine y la misma semilla da siempre el mismo techo.
///
/// **El suelo NO se mueve.** Bajar una cota es trabajo de `rise_cm` y arrastra escalones, vecinos y
/// vanos; mover el techo no arrastra nada más que el faldón del vano.
pub const CEILING_MIN_CM: i32 = 300;
/// El otro extremo del rango normal.
///
/// **Por debajo de [`CEILING_TALL_MIN_CM`], y esa separación es la regla.** Subiéndolo a 420 el rango
/// normal se metía dentro del alto: un sorteo corriente salía a 400 y contaba como doble altura, con
/// lo que la excepción pasaba del 8 % al 15 % de los espacios y dejaba de leerse como excepción. Lo
/// que hace que un techo alto se note no es su número, es que el de al lado no lo tenga.
pub const CEILING_MAX_CM: i32 = 380;

/// A qué escalón se cuantiza. Diez centímetros: por debajo la diferencia no se lee desde dentro y
/// sólo ensucia el histograma.
const CEILING_STEP_CM: i32 = 10;

/// Exponente del sesgo, `u^k`. Con `k > 1` la masa se va al extremo BAJO, que es lo que hace que un
/// techo alto se note: con reparto plano, «alto» es la mitad del mundo y deja de significar nada.
///
/// **Bajado de 2,2 a 1,15 con el jugador delante**, que mide 1,86 m. Con 2,2 la mediana MEDIDA del
/// mundo servido era 2,50 m de techo: 64 cm por encima de la cabeza, 1,34 alturas de jugador. Eso no
/// es el techo bajo de Backrooms, es un sótano. El sesgo sigue existiendo —lo bajo sigue siendo más
/// probable que lo alto— pero deja de ser el mundo entero.
const CEILING_SKEW: f32 = 1.15;

/// Superficie a partir de la cual un espacio puede pedir doble altura, en m².
pub const CEILING_TALL_AREA_M2: f32 = 200.0;

/// Y con qué probabilidad la pide. Baja a propósito, por lo mismo que el sesgo.
const CEILING_TALL_CHANCE: f32 = 0.12;

/// El rango de la doble altura. El tope de [`PlannedSpace::max_clear_cm`] la recorta a 308 en cuanto
/// hay planta encima, así que seis metros sólo salen donde de verdad no hay nada arriba.
pub const CEILING_TALL_MIN_CM: i32 = 400;
/// El techo del techo.
pub const CEILING_TALL_MAX_CM: i32 = 600;

/// **LA MEGASALA: superficie a partir de la cual una nave puede pedir más de un vacío encima.**
///
/// Cuatrocientos metros cuadrados son veinte por veinte. El número no es un gusto: quince metros de
/// altura sobre doscientos metros cuadrados no es una nave, es un hueco de ascensor, y desde dentro
/// se lee como un fallo del generador y no como arquitectura. La proporción es la que decide si un
/// volumen alto se siente grande o se siente estrecho.
pub const ATRIUM_MEGA_AREA_M2: f32 = 400.0;

/// Y la superficie a la que ya pide el máximo. Entre las dos se interpola.
pub const ATRIUM_MEGA_FULL_AREA_M2: f32 = 900.0;

/// Con qué probabilidad una nave que cumple el área lo pide. Una de cada cinco de las que YA son
/// candidatas — que son pocas — porque lo que hace grande a una megasala es que no haya otra.
const ATRIUM_MEGA_CHANCE: f32 = 0.20;

/// Cuántas alturas de planta llega a medir una megasala. Un atrio corriente mide dos.
///
/// Cinco son `5 * 332 - 24 = 1636 cm`: dieciséis metros y medio de altura libre, casi nueve alturas de
/// jugador. Es el volumen que se pidió.
const ATRIUM_MEGA_MAX_STOREYS: u8 = 5;

/// **Cuántas alturas de planta mide este atrio.** Dos es el atrio de ADR-104; más es una MEGASALA.
///
/// # Por qué el número NO sale de contar vacíos
///
/// Ése fue el primer intento y está medido: en 49 regiones el mundo levanta **2 plantas en 47 y 1 en
/// 2 — nunca 3**. Un atrio no puede vaciar una planta que no existe, así que apilar vacíos tiene un
/// tope duro de dos alturas y la megasala era imposible por construcción, no por un número mal
/// elegido. El vacío que faltaba no está entre plantas: está **encima del edificio**.
///
/// Por eso la condición es que no haya nada construido en NINGUNA planta de arriba —
/// `void_storeys_above` cuenta hasta la última, así que eso es que las cuente todas—. Cumplida, subir
/// el techo no atraviesa nada: sale por el tejado, y por dentro no hay tejado que mirar.
///
/// Determinista por posición como todo lo demás, y **por el rectángulo de la nave**: si la
/// composición de huellas la deforma, el sorteo sigue cayendo donde caía.
fn atrium_storeys_for(seed: i32, s: &PlannedSpace, storeys_above: u8) -> u8 {
    const PLAIN: u8 = 2;
    let area = s.area_m2();
    if area <= ATRIUM_MEGA_AREA_M2 || s.void_storeys_above < storeys_above {
        return PLAIN;
    }
    let (cx, cz) = s.rect.centre_m();
    let mut st = hash::stream_at(seed, cx, cz, SALT_MEGA);
    if st.next01() >= ATRIUM_MEGA_CHANCE {
        return PLAIN;
    }
    // Interpolada por área: una nave justa se queda en tres alturas, una enorme llega arriba. Quince
    // metros sobre cuatrocientos metros cuadrados ya es un volumen esbelto; sobre novecientos es una
    // nave.
    let t = ((area - ATRIUM_MEGA_AREA_M2) / (ATRIUM_MEGA_FULL_AREA_M2 - ATRIUM_MEGA_AREA_M2))
        .clamp(0.0, 1.0);
    let h = PLAIN as f32 + 1.0 + t * (ATRIUM_MEGA_MAX_STOREYS - PLAIN - 1) as f32;
    (h.round() as u8).clamp(PLAIN + 1, ATRIUM_MEGA_MAX_STOREYS)
}

/// **LA PERILLA: qué parte de los espacios sortea su propia altura.**
///
/// `0` la apaga entera y **el mundo vuelve a ser el de antes al centímetro** —todo espacio se queda
/// con la altura de su papel—, que es lo que permite comparar el mundo contra sí mismo sin cambiar de
/// rama. No es un interruptor disfrazado de flotante: los valores intermedios reparten de verdad, y
/// el sorteo que decide va por posición como todo lo demás.
pub const CEILING_VARIETY: f32 = 1.0;

/// La altura que pide un espacio, o `0` si se queda con la de su papel.
fn ceiling_for(seed: i32, s: &PlannedSpace, variety: f32) -> i32 {
    // Lo que no se construye no tiene techo. Y la ESCALERA se queda fuera: sus 380 son un número
    // medido y no un gusto —los peldaños suben mientras el techo no— y con 2,40 el último queda
    // debajo de la losa. Ver `fill::clear_height_by_role`.
    if variety <= 0.0 || !s.role.is_built() || s.role == SpaceRole::Stair {
        return 0;
    }
    let (cx, cz) = s.rect.centre_m();
    let mut st = hash::stream_at(seed, cx, cz, SALT_CEILING);
    if st.next01() >= variety {
        return 0;
    }
    // El sorteo de doble altura se tira SIEMPRE, quepa o no, para que la perilla del área no corra el
    // flujo de los espacios pequeños: un mundo no puede cambiar de techos porque una sala cruce los
    // 200 m² por un centímetro.
    let tall = st.next01() < CEILING_TALL_CHANCE && s.area_m2() > CEILING_TALL_AREA_M2;
    let (lo, hi) = if tall {
        (CEILING_TALL_MIN_CM, CEILING_TALL_MAX_CM)
    } else {
        (CEILING_MIN_CM, CEILING_MAX_CM)
    };
    let u = st.next01().powf(CEILING_SKEW);
    let raw = lo as f32 + (hi - lo) as f32 * u;
    let stepped = (raw / CEILING_STEP_CM as f32).round() as i32 * CEILING_STEP_CM;
    stepped.clamp(lo, hi)
}

/// Reparte la altura de techo por toda una planta.
///
/// **Se llama con la huella ya definitiva**, y por eso corre dos veces: al cerrar
/// [`plan_storey_with`] —que es lo que hace que [`plan_region`] suelta también tenga techos— y otra
/// vez al final de [`plan_building_with`], después de `compose_shapes`. Un receptor deformado crece y
/// un donante mengua, así que el área con la que se decidió la doble altura ya no es la suya. Es
/// idempotente: misma posición y misma área, misma respuesta.
fn assign_ceilings(plan: &mut RegionPlan, seed: i32, variety: f32) {
    for s in &mut plan.spaces {
        s.ceiling_clear_cm = ceiling_for(seed, s, variety);
    }
}

/// ADR-105 enm. 14 — **el carácter de la zona pone un TOPE al techo**: 2,40 en laberinto, 2,00 en lo
/// raro. Se aplica en el plan y no en el relleno porque el techo es una decisión del plan y el
/// relleno la lee por `ceiling_clear_cm`; sin esto, la mitad de los consumidores verían un techo y
/// la otra mitad otro. **Con la semilla del MUNDO, no la de la planta**: el carácter es un campo de
/// la posición y el relleno lo consulta con `building.seed`; con la semilla de planta, el plan y el
/// relleno leerían dos caracteres distintos en la misma sala. La escalera se queda fuera (sus 380
/// son medidos), y con la perilla a cero el mundo tiene que ser el de antes al centímetro.
fn cap_ceilings_by_character(plan: &mut RegionPlan, world_seed: i32, variety: f32) {
    if variety <= 0.0 {
        return;
    }
    for s in &mut plan.spaces {
        let cap = super::fill::ceiling_cap_cm(world_seed, s);
        if cap > 0 && s.role.is_built() && s.role != SpaceRole::Stair {
            let base = if s.ceiling_clear_cm > 0 {
                s.ceiling_clear_cm
            } else {
                super::fill::clear_height_by_role(s.role)
            };
            s.ceiling_clear_cm = base.min(cap);
        }
    }
}

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
    plan_storey_with(
        seed,
        bounds,
        gates,
        base_y_cm,
        may_sink,
        atria_below,
        CEILING_VARIETY,
        DOOR_MISALIGN_CHANCE,
    )
}

/// [`plan_storey`] con la perilla de techos a la vista. Ver [`CEILING_VARIETY`].
#[allow(clippy::too_many_arguments)]
pub fn plan_storey_with(
    seed: i32,
    bounds: (f32, f32, f32, f32),
    gates: &[Wg3Gate],
    base_y_cm: i32,
    may_sink: bool,
    atria_below: &[PlanRect],
    ceiling_variety: f32,
    misalign_chance: f32,
) -> RegionPlan {
    plan_storey_zoned(
        None,
        seed,
        bounds,
        gates,
        base_y_cm,
        may_sink,
        atria_below,
        ceiling_variety,
        misalign_chance,
    )
}

/// [`plan_storey_with`] con la semilla del mundo para el bioma laberinto (ADR-155).
#[allow(clippy::too_many_arguments)]
pub fn plan_storey_zoned(
    zone_seed: Option<i32>,
    seed: i32,
    bounds: (f32, f32, f32, f32),
    gates: &[Wg3Gate],
    base_y_cm: i32,
    may_sink: bool,
    atria_below: &[PlanRect],
    ceiling_variety: f32,
    misalign_chance: f32,
) -> RegionPlan {
    let root = PlanRect {
        min_x_cm: (bounds.0 * CM_PER_M).round() as i32,
        min_z_cm: (bounds.1 * CM_PER_M).round() as i32,
        max_x_cm: (bounds.2 * CM_PER_M).round() as i32,
        max_z_cm: (bounds.3 * CM_PER_M).round() as i32,
    };

    let mut planner = Planner {
        seed,
        zone_seed,
        base_y_cm,
        nodes: vec![Node::leaf(root, 0)],
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
    // Entre el reparto y las hojas: aquí el árbol todavía es lo único que existe, así que quitar un
    // tabique no obliga a deshacer ni un papel, ni un vacío, ni un enlace. Ver `fuse_open_plan`.
    planner.fuse_open_plan(atria_below);
    planner.emit_leaves();
    planner.assign_void();
    // ADR-104 D2 — y JUSTO AQUÍ, entre el vacío sorteado y el grafo. Antes no hay papeles que mirar;
    // después habría que deshacer enlaces ya tejidos, que es cómo se fabrican espacios sellados.
    planner.carve_atria(atria_below);
    // **ADR-120 — y JUSTO AQUÍ.** Después del vacío y los atrios (no se deforma lo que no se
    // construye) y antes de las puertas y el grafo (las puertas se reparten ya contra las huellas
    // reales, así que no hay ninguna que reubicar). Ver `compose_shapes`.
    // Las puertas ANTES de enlazar: una puerta acordada con la vecina no se negocia, así que el
    // espacio que la abre no puede ser vacío y tiene que estar dentro del edificio. Decidirlo después
    // obligaría a rehacer el grafo.
    let gates = planner.attach_gates(gates);
    planner.link_all();
    planner.ensure_connected(&gates.iter().map(|g| g.space).collect::<Vec<_>>());
    // La sala grande, con el grafo ya conexo: sólo puede añadir vanos, nunca quitarlos.
    planner.ensure_open_plan_doors();
    // Con el grafo YA cerrado: sólo se mueven vanos por su propia pared, nunca se quita ni se añade
    // un enlace, así que la conectividad que acaba de asegurarse no cambia.
    planner.misalign_doorways(misalign_chance);
    planner.retag_dead_ends();
    if may_sink {
        planner.sink_dead_ends(&gates);
    }

    let mut plan = RegionPlan {
        spaces: planner.spaces,
        links: planner.links,
        gates,
        bounds_cm: Some(root),
    };
    // Al final: hasta aquí la huella todavía se movía —vacío, atrios, hundidos— y el área es lo que
    // decide si un espacio puede pedir doble altura.
    assign_ceilings(&mut plan, seed, ceiling_variety);
    plan
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
    /// De abajo arriba: sótanos primero (B_b … B_1), la calle en [`RegionBuilding::ground`], y las
    /// plantas altas después. `n − 1` es SIEMPRE la planta de debajo de `n`, sea sótano o no.
    pub storeys: Vec<RegionPlan>,
    pub wells: Vec<StairWell>,
    /// ADR-130 — el índice de la CALLE en `storeys`. Cero si no hay sótanos, que es lo de siempre.
    /// Todo lo que antes preguntaba por `storeys[0]` pregunta por esto.
    pub ground: usize,
    /// La semilla con la que se planificó, para los sorteos que se hacen al RELLENAR (los agujeros
    /// de forjado). Auditoría 2026-09-02: `fill::hole_carves` sorteaba con semilla 0, así que dos
    /// mundos distintos ponían los agujeros en el mismo sitio si un espacio caía igual — y ningún
    /// test lo veía porque el resultado seguía siendo determinista.
    pub seed: i32,
    /// ADR-155 L1a — la semilla del MUNDO para el campo del bioma laberinto (`density::in_maze_zone`).
    /// No la de la región: con ella cada región sortearía un campo distinto y la zona se cortaría en
    /// cada borde. `None` = sin bioma, que es lo que planifican las sondas sin mundo.
    pub zone_seed: Option<i32>,
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
            // ADR-130 — la cota es relativa a la calle: los sótanos van por debajo.
            let want = (n as i32 - self.ground as i32) * STOREY_HEIGHT_CM;
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
            // Se sale DENTRO de la planta de arriba, no al lado — y vale la UNIÓN de espacios,
            // no hace falta que uno solo contenga el tiro (misma regla que `landing_over`): los
            // espacios teselan los bounds, así que basta con que los bounds contengan la huella y
            // que todo lo que la solapa sea construido, plano y no-escalera.
            // `space_above` es contabilidad de cavado, no geometría: un split posterior en la
            // planta de llegada puede dejar el índice en un trozo que ya no toca la boca sin que
            // eso rompa nada — la salida la garantiza la UNIÓN, comprobada abajo.
            let _ = sa;
            match above.bounds_cm {
                Some(b) if b.contains_rect(&w.rect) => {}
                _ => out.push(format!(
                    "hueco {i}: la huella se sale de la planta de arriba"
                )),
            }
            for t in above.spaces.iter().filter(|t| t.rect.overlaps(&w.rect)) {
                if !t.role.is_built() {
                    out.push(format!("hueco {i}: se sale a un VACÍO, que es una caída"));
                } else if t.rise_cm != 0 || t.role == SpaceRole::Stair {
                    out.push(format!(
                        "hueco {i}: se sale sobre un desnivel o dentro de otra escalera"
                    ));
                }
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
    plan_building_with(seed, bounds, gates, storeys, CEILING_VARIETY)
}

/// ADR-130 — tope de sótanos de la REBANADA 1: tres, para medir con el validador y andarlos antes
/// de que exista el streaming vertical (D5). Los treinta de D1 esperan a ese wire.
pub const REGION_BASEMENTS: usize = 3;

/// ADR-130 D2 — ¿cuántos sótanos tiene esta región? Una de cada cuatro es TORRE; las demás,
/// ninguno. Por coordenada de región y no por semilla: la identidad de una región es su sitio, y
/// así la (0,0) —la de todas las sondas y capturas— es torre.
pub fn basements_for(rx: i32, rz: i32) -> usize {
    if (rx * 3 + rz * 5).rem_euclid(4) == 0 {
        REGION_BASEMENTS
    } else {
        0
    }
}

/// [`plan_building`] con sótanos: lo que sirve el backend.
pub fn plan_building_at(
    seed: i32,
    bounds: (f32, f32, f32, f32),
    gates: &[Wg3Gate],
    storeys: usize,
    basements: usize,
) -> RegionBuilding {
    plan_building_deep(
        seed,
        bounds,
        gates,
        storeys,
        basements,
        CEILING_VARIETY,
        None,
    )
}

/// [`plan_building_at`] con la semilla del mundo para el bioma laberinto (ADR-155): lo que sirve
/// el backend y lo que mide el validador.
pub fn plan_building_zoned(
    seed: i32,
    zone_seed: i32,
    bounds: (f32, f32, f32, f32),
    gates: &[Wg3Gate],
    storeys: usize,
    basements: usize,
) -> RegionBuilding {
    plan_building_deep(
        seed,
        bounds,
        gates,
        storeys,
        basements,
        CEILING_VARIETY,
        Some(zone_seed),
    )
}

/// [`plan_building`] con la perilla de techos a la vista. Ver [`CEILING_VARIETY`].
pub fn plan_building_with(
    seed: i32,
    bounds: (f32, f32, f32, f32),
    gates: &[Wg3Gate],
    storeys: usize,
    ceiling_variety: f32,
) -> RegionBuilding {
    plan_building_deep(seed, bounds, gates, storeys, 0, ceiling_variety, None)
}

/// ADR-130 D3 — el edificio entero: `storeys` plantas hacia arriba (contando la calle) y
/// `basements` sótanos hacia abajo, con **el mismo apilado, espejado**: la calle se recorta al corte
/// de la de arriba subiendo, y cada sótano se planifica bajo el anterior y le abre un pozo de
/// escalera igual que una planta alta se lo abre a la de debajo. Los sótanos usan la huella entera
/// de la región (bajo tierra no hay silueta que estrechar), no tienen puertas de junta (ADR-096 sólo
/// negocia la calle) ni atrios hacia arriba, y no se hunden.
pub fn plan_building_deep(
    seed: i32,
    bounds: (f32, f32, f32, f32),
    gates: &[Wg3Gate],
    storeys: usize,
    basements: usize,
    ceiling_variety: f32,
    zone_seed: Option<i32>,
) -> RegionBuilding {
    // La planta baja SÍ se hunde: debajo de ella no hay nada que perforar… **salvo que haya
    // sótanos** (ADR-130). Una terraza hundida a −12 es exactamente la losa del techo de B1 —
    // medido: 41 pares de caras coplanares a −0,24 en la región (0,0)—, por lo mismo que una
    // planta alta no se hunde sobre la de abajo.
    // La planta baja no tiene nada debajo, así que no hay atrios que abrirle a nadie.
    let mut out = vec![plan_storey_zoned(
        zone_seed,
        seed,
        bounds,
        gates,
        0,
        basements == 0,
        &[],
        ceiling_variety,
        DOOR_MISALIGN_CHANCE,
    )];
    let mut wells = Vec::new();

    for n in 1..storeys.max(1) {
        // La huella se estrecha (D3): si cada planta llenara la región, esto no sería un edificio
        // sino losas de 150 × 150 apiladas, sin una silueta que mirar. Y cuando el estrechamiento
        // ya no da para planta completa, sigue una TORRE de huella constante (VERTICALITY-ROADMAP
        // D1): zigurat abajo, torre arriba.
        let Some(up) = upper_bounds(&out[n - 1], seed).or_else(|| tower_bounds(&out[n - 1])) else {
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
        let plan = plan_storey_zoned(
            zone_seed,
            storey_seed(seed, n),
            up,
            &[],
            n as i32 * STOREY_HEIGHT_CM,
            false,
            &atria,
            ceiling_variety,
            DOOR_MISALIGN_CHANCE,
        );
        // **EL EDIFICIO SUBE SÓLO HASTA DONDE SE PUEDE SUBIR.** El hueco pide que COINCIDAN dos
        // geometrías planificadas por separado —un espacio de abajo que quepa entero dentro de uno
        // construido de arriba, y con tiro para catorce peldaños—, y eso es coincidencia de semilla:
        // en el barrido de 49 regiones hay unas cuantas donde no la hay. Si no se puede subir, la
        // planta no se levanta. Una planta a la que no se llega no es media victoria: es geometría
        // que se dibuja, se ilumina, cuesta y no se pisa, y como todo lo demás sale bien, ningún
        // contador se queja.
        //
        // El hueco se abre en la planta de ABAJO, así que hay que tenerla a mano y mutable. Y las
        // bocas de los pozos que ya llegan a esa planta van protegidas con su margen: una franja de
        // escalera nueva encima de una boca vieja rompe la salida del pozo de abajo (ver
        // `dig_wells`).
        let landings: Vec<PlanRect> = wells
            .iter()
            .filter(|w: &&StairWell| w.storey_below + 1 == n - 1)
            .map(|w| PlanRect {
                min_x_cm: w.rect.min_x_cm - STAIR_MARGIN_CM,
                min_z_cm: w.rect.min_z_cm - STAIR_MARGIN_CM,
                max_x_cm: w.rect.max_x_cm + STAIR_MARGIN_CM,
                max_z_cm: w.rect.max_z_cm + STAIR_MARGIN_CM,
            })
            .collect();
        let mut plan = plan;
        // **Una planta de UN solo espacio no es una planta** (auditoría 2026-09-02). Sin enlaces no
        // tiene ni una puerta, y un espacio sin puertas no se construye (`fill::emit_space` no emite
        // cajas macizas): el pozo subía a una losa que no existía. Pasaba en la torre, cuya huella
        // de 18 × 18 cabe entera en el área objetivo de una zona `Large`.
        let mut dug = if plan.links.is_empty() {
            Vec::new()
        } else {
            dig_wells(&mut out[n - 1], &plan, n - 1, seed, &landings)
        };
        // **LA TORRE ES UN REINTENTO, no una rama** (VERTICALITY-ROADMAP D1). Medido antes de
        // escribirla: con la torre sólo como sustituto de `upper_bounds` la distribución de 49
        // regiones no movió ni una — lo que rompe la subida casi nunca es que no quede huella,
        // es que el pozo no encaja en la planta que salió. Así que cuando no encaja, se planifica
        // otra vez con la huella de torre, anclada sobre el espacio más grande de abajo, que es
        // donde un aterrizaje tiene más sitio para caer. `dig_wells` sin resultado no mutó nada,
        // así que reintentar es legal.
        if dug.is_empty() {
            if let Some(tb) = tower_bounds(&out[n - 1]) {
                let retry = plan_storey_zoned(
                    zone_seed,
                    storey_seed(seed, n),
                    tb,
                    &[],
                    n as i32 * STOREY_HEIGHT_CM,
                    false,
                    &atria,
                    ceiling_variety,
                    DOOR_MISALIGN_CHANCE,
                );
                if !retry.links.is_empty() {
                    dug = dig_wells(&mut out[n - 1], &retry, n - 1, seed, &landings);
                    if !dug.is_empty() {
                        plan = retry;
                    }
                }
            }
        }
        if dug.is_empty() {
            break;
        }
        wells.extend(dug);
        out.push(plan);
    }

    // ADR-130 D3 — LOS SÓTANOS. Cada uno se planifica bajo el anterior (o bajo la calle) y le
    // abre un pozo de escalera con `dig_wells`, exactamente como una planta alta se lo abre a la
    // de debajo: aquí el sótano es el `below` y el de arriba el `above`. Si el pozo no encaja, el
    // edificio deja de bajar, por lo mismo que deja de subir: un sótano al que no se llega es
    // geometría que cuesta y no se pisa.
    let mut downs: Vec<RegionPlan> = Vec::new();
    let mut down_wells: Vec<StairWell> = Vec::new();
    for k in 1..=basements {
        let mut lower = plan_storey_zoned(
            zone_seed,
            storey_seed(seed, 1000 + k),
            bounds,
            &[],
            -(k as i32) * STOREY_HEIGHT_CM,
            false,
            &[],
            ceiling_variety,
            DOOR_MISALIGN_CHANCE,
        );
        if lower.links.is_empty() {
            break;
        }
        let dug = {
            let above: &RegionPlan = if k == 1 { &out[0] } else { &downs[k - 2] };
            let dug = dig_wells(&mut lower, above, k, seed, &[]);
            // **Y el techo del sótano se recorta bajo el forjado de arriba**, como el de cualquier
            // planta (ADR-102): sin esto una nave de B1 sube 6,40 y atraviesa la calle — medido,
            // 41 pares de caras coplanares a −0,12 y los espacios hundidos de la calle abiertos.
            cap_headroom_under(&mut lower, above);
            // Bajo tierra NO hay atrios: donde arriba no hay sala hay TIERRA, no cielo. Sin esto
            // seis naves de B1 salían a 6,39 m, se convertían en atrio y perforaban la calle.
            for s in &mut lower.spaces {
                if s.void_above {
                    s.void_above = false;
                    s.max_clear_cm = STOREY_HEIGHT_CM - 2 * SLAB_THICKNESS_CM;
                }
            }
            dug
        };
        if dug.is_empty() {
            break;
        }
        down_wells.extend(dug);
        downs.push(lower);
    }
    // De abajo arriba: B_b … B_1, calle, altas. Los índices de los pozos se reasignan a ese orden:
    // el pozo abierto en B_k tiene a B_k en `b − k`, y los de arriba se desplazan `b`.
    let ground = downs.len();
    let mut storeys_all: Vec<RegionPlan> = downs.into_iter().rev().collect();
    storeys_all.extend(out);
    let mut wells_all: Vec<StairWell> = Vec::with_capacity(down_wells.len() + wells.len());
    for mut w in down_wells {
        w.storey_below = ground - w.storey_below;
        wells_all.push(w);
    }
    for mut w in wells {
        w.storey_below += ground;
        wells_all.push(w);
    }
    let mut out = storeys_all;
    let wells = wells_all;

    // **ADR-120 — LA COMPOSICIÓN DE HUELLAS, y va AQUÍ.**
    //
    // Después de los pozos porque los pozos no se pueden recuperar: una planta que no sube se pierde
    // entera, y una puerta que no cabe sólo descarta un mordisco. Ver `compose_shapes` para la medida
    // que obligó a moverla desde `plan_storey`.
    let (gx, gz) = (
        gate_cut_spans(gates, true, GATE_CUT_REACH_CM),
        gate_cut_spans(gates, false, GATE_CUT_REACH_CM),
    );
    for (n, storey) in out.iter_mut().enumerate() {
        // Todo lo que un pozo necesita intacto: su tiro y la boca que abre arriba, con el margen con
        // el que ya se eligieron.
        // **Dos cercos, no uno.** El duro veta el espacio entero; el blando sólo veta que el
        // mordisco caiga ahí. Ver `compose_shapes`.
        let keep_hard: Vec<PlanRect> = wells
            .iter()
            .filter(|w| w.storey_below == n || w.storey_below + 1 == n)
            .map(|w| w.rect.shrunk(-STAIR_MARGIN_CM))
            .collect();
        // **Y el entorno de cada puerta de junta.** No basta con no deformar el espacio que la abre:
        // el borde de región se comporta como un hueco, así que una forma nueva en el VECINO puede
        // encerrar la puerta entre el borde y ella sin llegar a tocarla. Medido: `puerta de junta en
        // (20.5,150.0) sin suelo alcanzable por dentro`, con la puerta intacta y su sala también.
        //
        // Y el cerco es GENEROSO —tres veces la holgura del reparto— porque lo que hay que proteger
        // no es la puerta: es el camino que llega a ella. Con un cerco justo volvió a salir una
        // puerta sellada en 1 de 27 regiones, con la puerta intacta y la deformación a diez metros.
        // Y el cerco no es un cuadrado alrededor del punto: es un PASILLO desde la puerta hacia
        // dentro. Lo que hay que dejar en paz no es la puerta —ésa no se toca nunca— sino el camino
        // que llega a ella; con un cuadrado de doce metros seguían saliendo dos regiones de 270 con
        // un ala de mil metros cuadrados aislada y la puerta dentro.
        const GATE_KEEP_CM: i32 = 3 * GATE_CLEARANCE_CM;
        // **Y llega TRES metros por decena, no ochenta.** Con el cerco convertido en una comprobación
        // por mordisco (ver `compose_shapes`) ya no hace falta un pasillo que cruce media región: lo
        // que se protege es la pared nueva, no el espacio entero. A 80 m el cerco seguía matando 138
        // intentos de 111 parejas en la región (0,0) — más que cualquier otro motivo junto.
        const GATE_REACH_CM: i32 = 3000;
        /// Y lo que del pasillo va en el cerco DURO: el arranque, junto al borde.
        ///
        /// Es donde la puerta se puede quedar encerrada sin que nadie la toque: ahí la franja entre
        /// el borde y el primer vecino es estrecha, y a ese vecino le basta con retirarse para que
        /// deje de comunicar. Más adentro hay región de sobra y el cerco blando basta —vetar el
        /// espacio entero a sesenta metros costaba un tercio de la composición de huellas—.
        const GATE_HARD_REACH_CM: i32 = 2000;
        debug_assert_eq!(GATE_REACH_CM, GATE_CUT_REACH_CM);
        let gate_box = |g: &PlannedGate, reach: i32| -> PlanRect {
            let (dx, dz) = match g.outward_side % 4 {
                0 => (0, -1),
                1 => (-1, 0),
                2 => (0, 1),
                _ => (1, 0),
            };
            let (ix, iz) = (g.x_cm + dx * reach, g.z_cm + dz * reach);
            PlanRect {
                min_x_cm: g.x_cm.min(ix) - GATE_KEEP_CM,
                min_z_cm: g.z_cm.min(iz) - GATE_KEEP_CM,
                max_x_cm: g.x_cm.max(ix) + GATE_KEEP_CM,
                max_z_cm: g.z_cm.max(iz) + GATE_KEEP_CM,
            }
        };
        // **Y el cerco duro es ESTRECHO: la holgura de reparto, no tres veces.**
        //
        // El ancho del blando es generoso porque ahí sólo veta dónde cae un mordisco; el duro veta el
        // espacio ENTERO, y con los mismos doce metros a cada lado se llevaba por delante 48 de los
        // 96 espacios de la región (1,0) —la mitad del mundo sin poder deformarse para proteger cinco
        // puertas—. Lo que hay que blindar es la franja entre el borde y el primer vecino, y eso es
        // el ancho de un paso.
        let keep_hard: Vec<PlanRect> =
            keep_hard
                .into_iter()
                .chain(storey.gates.iter().map(|g| {
                    gate_box(g, GATE_HARD_REACH_CM).shrunk(GATE_KEEP_CM - GATE_CLEARANCE_CM)
                }))
                .collect();
        let keep_soft: Vec<PlanRect> = storey
            .gates
            .iter()
            .map(|g| gate_box(g, GATE_REACH_CM))
            .collect();
        let storey_gates: (&[GateCut], &[GateCut]) = if n == 0 { (&gx, &gz) } else { (&[], &[]) };
        let storey_seed = if n == 0 { seed } else { storey_seed(seed, n) };
        compose_shapes(
            storey,
            storey_seed,
            &keep_hard,
            &keep_soft,
            storey_gates.0,
            storey_gates.1,
        );
        // **Y los techos se reparten OTRA VEZ, con la huella ya deformada.** `compose_shapes` mueve
        // área de un espacio a otro y la escalera de `dig_wells` nació heredando el techo de la sala
        // que partió: sin este segundo reparto habría salas de 90 m² con seis metros de techo porque
        // ANTES tenían 210, y huecos de escalera con el techo de una oficina.
        assign_ceilings(storey, storey_seed, ceiling_variety);
        cap_ceilings_by_character(storey, seed, ceiling_variety);
    }

    // **Y el tope de altura se calcula DESPUÉS de deformar**, no dentro del bucle. Un receptor crece
    // hacia terreno que antes era del vecino, y ese terreno puede estar debajo de algo construido:
    // con el tope calculado antes, ese trozo se quedaría sin tope y su losa de techo atravesaría el
    // forjado de la planta de arriba. Es el artefacto que ADR-102 D2 ya pagó una vez.
    for n in 1..out.len() {
        let (below, above) = out.split_at_mut(n);
        cap_headroom_under(&mut below[n - 1], &above[0]);
    }

    // **Y cuántas plantas seguidas hay vacías encima, que es lo que separa un atrio de una MEGASALA.**
    //
    // Va aparte de `cap_headroom_under` a propósito: aquél mira exactamente una planta —es lo único
    // que necesita para poner el tope— y esto mira la columna entera. Se cuenta hacia arriba y se
    // para en la primera planta con algo construido encima; la última planta del edificio no cuenta
    // ninguna, por el mismo motivo por el que no tiene `void_above`: no hay planta que vaciar.
    for n in 0..out.len() {
        let counts: Vec<u8> = out[n]
            .spaces
            .iter()
            .map(|s| {
                let mut k = 0u8;
                for above in &out[n + 1..] {
                    if above
                        .spaces
                        .iter()
                        .any(|t| t.role.is_built() && t.rect.overlaps(&s.rect))
                    {
                        break;
                    }
                    k += 1;
                }
                k
            })
            .collect();
        let above = (out.len() - 1 - n).min(u8::MAX as usize) as u8;
        for (s, k) in out[n].spaces.iter_mut().zip(counts) {
            s.void_storeys_above = k;
            // Y con el número de plantas vacías ya puesto, la altura del atrio. Va aquí y no en
            // `fill` porque es una decisión del PLAN, igual que `ceiling_clear_cm`: el relleno la lee.
            s.atrium_storeys = if s.void_above && s.role == SpaceRole::Hall && !s.is_composite() {
                atrium_storeys_for(seed, s, above)
            } else {
                0
            };
        }
    }

    RegionBuilding {
        storeys: out,
        wells,
        seed,
        ground,
        zone_seed,
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

/// Lado de la TORRE (VERTICALITY-ROADMAP D1, decidido 2026-08-29): cuando el estrechamiento de
/// ADR-102 D3 ya no deja planta completa, el edificio sigue subiendo con un núcleo de huella
/// constante. Dieciocho metros y no menos, por la escalera: el tiro son 9 m (15 tiras × 60 cm) más
/// [`GOOD_WALL_CM`] de sobrante, y los espacios de la torre tienen que poder ser candidatos a
/// escalera para la planta siguiente — una torre en la que no cabe escalera es la última planta.
/// Medido también a 26 m: la distribución de plantas apenas se mueve ({3:9,4:27,5:10,6:3} contra
/// {3:10,4:29,5:8,6:2}), porque el cuello no es la huella sino que arriba escaseen salas con tiro de
/// 12,6 m — pasar de 6 plantas pide escalera de ida y vuelta (media huella), no torre más gorda.
const TOWER_SIDE_CM: i32 = 1800;

/// La huella de una planta de torre: un cuadrado de [`TOWER_SIDE_CM`] centrado en el espacio
/// elegible más grande de la planta de abajo, clampado a sus bounds.
///
/// **Sobre el espacio más grande y no sorteado**: la escalera de subida tiene que salir de un
/// espacio de abajo Y aterrizar dentro de la torre, así que anclarla donde más sitio hay es lo que
/// hace probable que el pozo encaje. Si los bounds de abajo son más pequeños que la torre, la torre
/// se queda con ellos: la huella deja de encoger, que es el rasgo que la define frente a D3.
fn tower_bounds(below: &RegionPlan) -> Option<(f32, f32, f32, f32)> {
    let region = below.bounds_cm?;
    let anchor = below
        .spaces
        .iter()
        .filter(|s| s.role.is_built() && !s.role.is_circulation() && s.rise_cm == 0)
        .max_by_key(|s| (s.rect.area_m2() as i64, s.rect.min_x_cm, s.rect.min_z_cm))?
        .rect;

    let side_x = TOWER_SIDE_CM.min(region.width_cm());
    let side_z = TOWER_SIDE_CM.min(region.depth_cm());
    // Que siga cabiendo un edificio, no un poste.
    if side_x < MIN_SIDE_CM * 2 || side_z < MIN_SIDE_CM * 2 {
        return None;
    }
    let (ax, az) = anchor.centre_m();
    let min_x =
        ((ax * CM_PER_M) as i32 - side_x / 2).clamp(region.min_x_cm, region.max_x_cm - side_x);
    let min_z =
        ((az * CM_PER_M) as i32 - side_z / 2).clamp(region.min_z_cm, region.max_z_cm - side_z);
    Some((
        min_x as f32 / CM_PER_M,
        min_z as f32 / CM_PER_M,
        (min_x + side_x) as f32 / CM_PER_M,
        (min_z + side_z) as f32 / CM_PER_M,
    ))
}

/// ¿Dónde aterriza este tiro en la planta de arriba? `None` = no se puede salir ahí.
///
/// **La unión de espacios vale como aterrizaje** (VERTICALITY-ROADMAP D1, medido antes de cambiar):
/// exigir que UNA sola sala contuviera el tiro entero con margen mataba decenas de candidatos por
/// planta —92 en la baja de (0,0)— y era la mitad de por qué el edificio se quedaba en 3-4 plantas.
/// Los espacios de una planta TESELAN sus bounds, así que «la unión cubre el tiro» es exactamente
/// «los bounds lo contienen y todo lo que solapa es construido, plano y no-escalera». Salir de una
/// escalera a un pasillo es arquitectura normal; la pared que cruce la boca la recorta `fill`
/// (`well_mouth_carves`), que es quien pone y quita paredes.
fn landing_over(above: &RegionPlan, stair: &PlanRect, entry_side: u8) -> Option<usize> {
    // **EL RELLANO TIENE QUE CABER ENTERO EN UN SOLO ESPACIO DE ARRIBA, con una celda de holgura**
    // (auditoría 2026-09-02). Mide dos tiras —120 cm, dos celdas del ráster— y las dos se pueden
    // perder: una pared de la planta de arriba que cruce el pozo a 8 cm de su extremo infla la celda
    // del rellano que toca, y la pared lateral del primer peldaño infla la otra por un centímetro.
    // Medido en una de 270 regiones: un rellano de 2 × 7 celdas cerrado por los cuatro lados y la
    // planta entera al 0 %. Una pared cruzando el TIRO la recorta `well_mouth_carves`; cruzando el
    // rellano no hay recorte que valga, así que ahí no puede haber pared.
    let landing = landing_zone(stair, entry_side);
    let landing_grown = PlanRect {
        min_x_cm: landing.min_x_cm - 50,
        min_z_cm: landing.min_z_cm - 50,
        max_x_cm: landing.max_x_cm + 50,
        max_z_cm: landing.max_z_cm + 50,
    };
    // `covers_rect` y no `rect.contains_rect` (ADR-120 D5): sobre una huella compuesta la envolvente
    // dice que sí a un rellano que cae en la muesca, o sea sobre el suelo del vecino — y ahí no hay
    // rellano, hay pared. Con una sola parte es exactamente la comprobación de antes.
    if !above.spaces.iter().any(|t| {
        t.role.is_built()
            && t.rise_cm == 0
            && t.role != SpaceRole::Stair
            && t.covers_rect(&landing_grown)
    }) {
        return None;
    }
    let grown = PlanRect {
        min_x_cm: stair.min_x_cm - STAIR_MARGIN_CM,
        min_z_cm: stair.min_z_cm - STAIR_MARGIN_CM,
        max_x_cm: stair.max_x_cm + STAIR_MARGIN_CM,
        max_z_cm: stair.max_z_cm + STAIR_MARGIN_CM,
    };
    if !above.bounds_cm?.contains_rect(&grown) {
        return None;
    }
    // **Auditoría 2026-09-02 — LA UNIÓN VALE, PERO EL POZO NO PUEDE PARTIR LO QUE ATRAVIESA.**
    //
    // Con la regla de unión sin más, un tiro de 9 × 3,6 m podía cruzar la ESPINA de la planta de
    // arriba de lado a lado: el vano del forjado se llevaba el suelo del corredor en toda su
    // anchura y la planta quedaba en dos mitades unidas sólo por el rellano — y la puerta del
    // corredor caía dentro del agujero, dos metros por encima del peldaño. Medido en la semilla en
    // vivo, región (0,0): la planta 4 entera con el 0 % alcanzable y todos los contadores en verde.
    //
    // Tres cosas que un espacio de arriba pisado por el pozo no puede ser: circulación (una banda
    // de 2,4-3,2 m nunca sobrevive a un hueco de 3,6), partido en dos por el hueco, ni tener un
    // hueco de puerta sobre él. Lo que sí puede: ser varias salas que comparten pared sobre el
    // tiro, que es lo que la unión vino a permitir y `well_mouth_carves` resuelve.
    let mut best: Option<(i64, usize)> = None;
    for (idx, t) in above.spaces.iter().enumerate() {
        // **Por la HUELLA** (ADR-120 D5). Con envolventes, un espacio cuya muesca queda encima del
        // pozo se contaba como pisado y disparaba el `return None` de abajo: la región (0,1) de la
        // semilla viva se quedó en UNA planta por esto, con todos los contadores en verde.
        if !t.hits_rect(&grown) {
            continue;
        }
        if !t.role.is_built() || t.rise_cm != 0 || t.role == SpaceRole::Stair {
            return None;
        }
        // El corte se mide contra la parte que de verdad está encima del hueco.
        let hit = t
            .parts()
            .iter()
            .filter(|p| p.overlaps(&grown))
            .copied()
            .collect::<Vec<_>>();
        if t.role.is_circulation() || hit.iter().any(|p| hole_severs(p, stair)) {
            return None;
        }
        let area: i64 = hit
            .iter()
            .map(|p| {
                let ox = (p.max_x_cm.min(grown.max_x_cm) - p.min_x_cm.max(grown.min_x_cm)) as i64;
                let oz = (p.max_z_cm.min(grown.max_z_cm) - p.min_z_cm.max(grown.min_z_cm)) as i64;
                ox.max(0) * oz.max(0)
            })
            .sum();
        if best.is_none_or(|(b, _)| area > b) {
            best = Some((area, idx));
        }
    }
    // Ningún paso de la planta de arriba puede caer sobre el hueco: se abriría al aire.
    let danger = PlanRect {
        min_x_cm: stair.min_x_cm - DOORWAY_CM / 2,
        min_z_cm: stair.min_z_cm - DOORWAY_CM / 2,
        max_x_cm: stair.max_x_cm + DOORWAY_CM / 2,
        max_z_cm: stair.max_z_cm + DOORWAY_CM / 2,
    };
    if above
        .links
        .iter()
        .any(|l| danger.contains_point(l.at_x_cm, l.at_z_cm))
    {
        return None;
    }
    best.map(|(_, idx)| idx)
}

/// Las dos últimas tiras de un tiro: donde se pisa al salir. El tiro corre desde la puerta (lado
/// `entry_side`) hacia la pared de enfrente, así que el rellano está en el extremo opuesto a la
/// puerta — la misma cuenta que hace `fill::emit_stair` con `from_max`.
fn landing_zone(stair: &PlanRect, entry_side: u8) -> PlanRect {
    let len = 2 * STAIR_TREAD_CM;
    match entry_side % 4 {
        0 => PlanRect {
            max_z_cm: stair.min_z_cm + len,
            ..*stair
        },
        1 => PlanRect {
            max_x_cm: stair.min_x_cm + len,
            ..*stair
        },
        2 => PlanRect {
            min_z_cm: stair.max_z_cm - len,
            ..*stair
        },
        _ => PlanRect {
            min_x_cm: stair.max_x_cm - len,
            ..*stair
        },
    }
}

/// ¿Parte este agujero la sala en dos? Lo hace si la cruza de pared a pared por alguno de los dos
/// ejes sin dejar paso a un lado: un vano más una celda de ráster, que es lo mínimo que se anda.
fn hole_severs(room: &PlanRect, hole: &PlanRect) -> bool {
    const PASS_CM: i32 = super::segment::MIN_GENERATED_WIDTH_CM + 50;
    let spans_x =
        hole.min_x_cm <= room.min_x_cm + PASS_CM && hole.max_x_cm >= room.max_x_cm - PASS_CM;
    let spans_z =
        hole.min_z_cm <= room.min_z_cm + PASS_CM && hole.max_z_cm >= room.max_z_cm - PASS_CM;
    spans_x || spans_z
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
    landings: &[PlanRect],
) -> Vec<StairWell> {
    let steps = storey_steps();
    let run_cm = steps * STAIR_TREAD_CM;

    // Todos los huecos de cada espacio, no sólo el primero. Hacen falta los dos usos: el PRIMERO dice
    // por dónde se entra, y TODOS dicen si el recorte se lleva alguno por delante.
    // ADR-155: el tercer campo es el enlace si es un hueco de la zona laberinto, que el corte puede
    // tragarse mientras la pareja conserve otro hueco.
    let mut doors: Vec<Vec<(i32, i32, Option<usize>)>> = vec![Vec::new(); below.spaces.len()];
    for (k, l) in below.links.iter().enumerate() {
        let gap = (l.kind == LinkKind::Gap).then_some(k);
        doors[l.a].push((l.at_x_cm, l.at_z_cm, gap));
        doors[l.b].push((l.at_x_cm, l.at_z_cm, gap));
    }
    for g in &below.gates {
        doors[g.space].push((g.x_cm, g.z_cm, None));
    }

    type Candidate = (u64, usize, usize, usize, u8, bool, Vec<usize>);
    let mut candidates: Vec<Candidate> = Vec::new();
    let (mut k_run, mut k_band, mut k_door) = (0u32, 0u32, 0u32);
    for (i, s) in below.spaces.iter().enumerate() {
        if !s.role.is_built() || s.role.is_circulation() || s.rise_cm != 0 {
            continue;
        }
        // **La escalera se talla en UNA PARTE, no en la envolvente** (ADR-120 D5). Sobre una huella
        // compuesta, la envolvente cubre terreno del vecino: el tiro se dimensionaría con metros que
        // no son suyos y el hueco de forjado saldría sobre el suelo de otro.
        //
        // Y por partes en vez de descartar la huella compuesta entera, porque descartarla cuesta
        // PLANTAS: medido en la primera versión, la región (0,1) de la semilla viva se quedó en una
        // sola planta y las tres pruebas de verticalidad se pusieron rojas a la vez. Una L con un
        // brazo de 12 m aloja una escalera igual de bien que un rectángulo de 12 m.
        for (pi, base) in s.parts().iter().copied().enumerate() {
            // **POR CADA PUERTA, no sólo por la primera.** El lado por el que se entra fija el eje del
            // tiro —los peldaños se alejan de la puerta—, así que una sala de 20 × 6 m con una puerta en
            // la pared larga da un tiro de 6 m y se descarta, cuando la misma sala con otra puerta daría
            // 20. Mirando sólo la primera, el catálogo de candidatas se quedaba en una o dos por región y
            // no había con qué repartir.
            for side in doors[i]
                .iter()
                .filter_map(|&(dx, dz, _)| side_of_point_in(&base, dx, dz))
                .collect::<Vec<_>>()
            {
                // Los peldaños se alejan de la puerta, así que el tiro corre perpendicular a su pared.
                let (run, across) = if side.is_multiple_of(2) {
                    (base.depth_cm(), base.width_cm())
                } else {
                    (base.width_cm(), base.depth_cm())
                };
                // **TIENE QUE SOBRAR ESPACIO, y no es un lujo.** El hueco se recorta contra la pared
                // de ENFRENTE de la puerta, así que lo que queda entre la puerta y el pie de la
                // escalera es lo que sigue siendo sala — y es también lo que sostiene todos los
                // enlaces que ya tenía este espacio. Sin sobrante no hay dónde repartirlos.
                // Lo que sobra tiene que dar para un vestíbulo con su puerta, no para una sala entera:
                // exigirle [`MIN_SIDE_CM`] descartaba la mitad de los sitios donde cabe una escalera.
                if run < run_cm + GOOD_WALL_CM || across < STAIR_WIDTH_CM {
                    k_run += 1;
                    continue;
                }
                // **Los DOS lados del recorte, el sorteado primero** (auditoría 2026-09-02). Con las
                // puertas repartidas por la pared, la mitad de las laterales cae sobre la franja por
                // el lado en que el sorteo puso la escalera — y por el otro lado hubiera cabido.
                // Probar el segundo recupera candidatos escasos, y sigue siendo función de la
                // posición.
                let (cx, cz) = base.centre_m();
                let first_low = hash::stream_at(seed, cx, cz, SALT_WELL).next01() < 0.5;
                let mut chosen: Option<(PlanRect, usize, bool, Vec<usize>)> = None;
                for low in [first_low, !first_low] {
                    let (stair, flank) = stair_and_flank(&base, side, run_cm, low);
                    // Lo que le queda al espacio: esta parte recortada más las otras intactas. Tiene
                    // que seguir siendo una sola pieza y con celdas anchas, o la sala nace partida.
                    let remaining = room_after_band(&base, side, run_cm);
                    let mut left = s.parts().to_vec();
                    left[pi] = remaining;
                    merge_parts(&mut left);
                    if left.len() > MAX_PARTS
                        || !parts_are_joined(&left)
                        || !grid_spans_are_wide(&left)
                    {
                        continue;
                    }
                    // Todo hueco tiene que caber en lo que queda de sala o en el costado; sobre la
                    // franja de la escalera, el candidato muere por este lado.
                    let mut left_space = *s;
                    if !left_space.set_parts(&left) {
                        continue;
                    }
                    let mut swallowed: Vec<usize> = Vec::new();
                    let blocked = doors[i].iter().any(|&(x, z, gap)| {
                        if door_fits_in(&left_space, x, z)
                            || flank.is_some_and(|f| door_fits_on(&f, x, z))
                        {
                            return false;
                        }
                        match gap {
                            Some(k) => {
                                swallowed.push(k);
                                false
                            }
                            None => true,
                        }
                    });
                    if blocked || !gaps_survive(below, &swallowed) {
                        k_door += 1;
                        continue;
                    }
                    // Una banda nueva no pisa una boca vieja (ver abajo).
                    if landings.iter().any(|l| l.overlaps(&stair)) {
                        continue;
                    }
                    if let Some(j) = landing_over(above, &stair, side) {
                        chosen = Some((stair, j, low, swallowed));
                        break;
                    }
                }
                let Some((stair, j, low, swallowed)) = chosen else {
                    k_band += 1;
                    continue;
                };
                let _ = stair;
                // Sorteo por posición: el orden no depende del orden del vector.
                candidates.push((
                    hash::at_position(seed, cx, cz, SALT_WELL),
                    i,
                    pi,
                    j,
                    side,
                    low,
                    swallowed,
                ));
                // Una orientación por espacio: la sala se parte una sola vez.
                break;
            }
            if candidates.last().is_some_and(|c| c.1 == i) {
                break;
            }
        }
    }

    if std::env::var("WG3_WELL_DEBUG").is_ok() {
        eprintln!(
            "[well] {} espacios, {} candidatas — muertes: tiro {k_run}, puerta {k_door}, \
             puerta-o-aterrizaje {k_band}",
            below.spaces.len(),
            candidates.len()
        );
    }
    // **REPARTIDAS, no las primeras que salgan.** Una escalera sirve si se tropieza con ella, y con
    // todas juntas en una esquina la mitad de la región sigue sin salida aunque el contador diga seis.
    // Se toman por orden de sorteo y se descarta la que caiga cerca de una ya tomada.
    candidates.sort_unstable_by_key(|c| (c.0, c.1));
    let mut taken: Vec<(usize, usize, usize, u8, bool)> = Vec::new();
    let mut gone: Vec<usize> = Vec::new();
    for (_, i, pi, j, side, low, swallowed) in &candidates {
        if taken.len() >= WELLS_PER_REGION {
            break;
        }
        let (cx, cz) = below.spaces[*i].rect.centre_m();
        let far = taken.iter().all(|&(ti, ..)| {
            let (tx, tz) = below.spaces[ti].rect.centre_m();
            let (dx, dz) = ((cx - tx) * CM_PER_M, (cz - tz) * CM_PER_M);
            dx * dx + dz * dz >= (WELL_SPACING_CM * WELL_SPACING_CM) as f32
        });
        // Dos pozos no pueden tragarse entre los dos todos los huecos de una misma pareja.
        let mut all_gone = gone.clone();
        all_gone.extend_from_slice(swallowed);
        if far && gaps_survive(below, &all_gone) {
            taken.push((*i, *pi, *j, *side, *low));
            gone = all_gone;
        }
    }
    // Los huecos tragados se quitan ANTES de partir: el corte reparte los enlaces que quedan.
    gone.sort_unstable();
    gone.dedup();
    for k in gone.into_iter().rev() {
        below.links.remove(k);
    }

    // El corte se hace DESPUÉS de elegirlas todas, y no sobre la marcha: partir un espacio le cambia
    // el rectángulo, y un candidato medido antes del corte dejaría de ser el que se midió. Cada
    // espacio se parte una sola vez, así que los índices de los demás siguen valiendo.
    taken
        .into_iter()
        .map(|(i, pi, j, side, low)| {
            let space_below = split_for_stair(below, i, pi, side, run_cm, low);
            StairWell {
                rect: below.spaces[space_below].rect,
                storey_below,
                space_below,
                space_above: j,
            }
        })
        .collect()
}

/// ADR-155 — si quitar los huecos `gone` deja a cada pareja tocada con otro hueco que la una.
fn gaps_survive(plan: &RegionPlan, gone: &[usize]) -> bool {
    gone.iter().all(|&k| {
        let (a, b) = (plan.links[k].a, plan.links[k].b);
        plan.links
            .iter()
            .enumerate()
            .any(|(m, l)| !gone.contains(&m) && ((l.a == a && l.b == b) || (l.a == b && l.b == a)))
    })
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
    low: bool,
) -> (PlanRect, Option<PlanRect>) {
    let band = band_of(rect, side, run_cm);
    let across_x = side.is_multiple_of(2);

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
fn split_for_stair(
    plan: &mut RegionPlan,
    i: usize,
    part: usize,
    side: u8,
    run_cm: i32,
    low: bool,
) -> usize {
    let original = plan.spaces[i];
    let base = original.parts()[part];
    let (stair, flank) = stair_and_flank(&base, side, run_cm, low);

    // La sala se queda con lo que no es franja: ESA parte recortada, las demás intactas. Por la
    // huella y no por el campo `rect` (ADR-120 D1), que es sólo la envolvente — asignarlo suelto
    // dejaría las partes describiendo la sala entera, franja incluida, y el ráster estamparía suelo
    // dentro de la escalera.
    let mut left = original.parts().to_vec();
    let kept_base = room_after_band(&base, side, run_cm);
    left[part] = kept_base;
    merge_parts(&mut left);
    let mut room = original;
    // `dig_wells` ya comprobó que la huella resultante es legal antes de elegir esta candidata; si
    // aun así no lo fuera, se prefiere la sala entera a una huella incoherente.
    if room.set_parts(&left) {
        // **Y si la mordida le toca a la planta abierta, deja de serlo.** El hueco de escalera manda:
        // apartarlo a otra sala cuesta PLANTAS (es la sensibilidad que ya midió `dig_wells`), y una
        // sala a la que se le ha comido un tiro de escalera ya no es una nave de 400 m² con treinta
        // puestos — su huella es compuesta, así que `fill::office_cubicles` tampoco la vestiría.
        room.open_plan = false;
        plan.spaces[i] = room;
    }

    let s = plan.spaces.len();
    plan.spaces.push(PlannedSpace {
        role: SpaceRole::Stair,
        rise_cm: STOREY_HEIGHT_CM,
        rise_step_cm: STOREY_RISE_CM,
        // Se entra por el mismo lado por el que se entraba a la sala: se viene de ahí.
        rise_from_side: side,
        // Un hueco de escalera no es la planta abierta aunque se lo coma a ella: la marca dice qué
        // se rellena con puestos, y en unos peldaños no se sienta nadie.
        open_plan: false,
        ..PlannedSpace::of_rect(stair, original)
    });
    link_along(plan, i, kept_base, s);

    // El costado que sobra al lado de la escalera, si lo hay. Su puerta va contra la SALA y no contra
    // la escalera: pegado a la escalera compartiría pared con los peldaños, y una puerta a media
    // escalera es un vano que se dibuja y no se pasa.
    if let Some(f) = flank {
        let k = plan.spaces.len();
        plan.spaces.push(PlannedSpace {
            // El costado que sobra tampoco lo es: mide una franja, no una planta.
            open_plan: false,
            ..PlannedSpace::of_rect(f, original)
        });
        // **Los huecos que quedaron en la pared del costado pasan a ser SUYOS** (auditoría
        // 2026-09-02). `dig_wells` ya comprobó que todo hueco de la sala cabe en la sala recortada o
        // en el costado; los del costado siguen apuntando a `i`, y `i` ya no tiene pared ahí. Sin
        // esta reasignación el relleno los declara fallidos y el vecino nace sellado.
        let remaining = plan.spaces[i];
        for l in &mut plan.links {
            let moves = !door_fits_in(&remaining, l.at_x_cm, l.at_z_cm)
                && door_fits_on(&f, l.at_x_cm, l.at_z_cm);
            if !moves {
                continue;
            }
            if l.a == i {
                l.a = k;
            } else if l.b == i {
                l.b = k;
            }
        }
        for g in &mut plan.gates {
            if g.space == i
                && !door_fits_in(&remaining, g.x_cm, g.z_cm)
                && door_fits_on(&f, g.x_cm, g.z_cm)
            {
                g.space = k;
            }
        }
        link_along(plan, i, kept_base, k);
    }
    s
}

/// Lo que queda de una sala cuando se le recorta la franja de escalera de `run_cm` contra el lado
/// `side`. Una sola función para el candidato y para el corte: dos cálculos del mismo rectángulo
/// son un rectángulo de más.
fn room_after_band(rect: &PlanRect, side: u8, run_cm: i32) -> PlanRect {
    let band = band_of(rect, side, run_cm);
    match side % 4 {
        0 => PlanRect {
            min_z_cm: band.max_z_cm,
            ..*rect
        },
        1 => PlanRect {
            min_x_cm: band.max_x_cm,
            ..*rect
        },
        2 => PlanRect {
            max_z_cm: band.min_z_cm,
            ..*rect
        },
        _ => PlanRect {
            max_x_cm: band.min_x_cm,
            ..*rect
        },
    }
}

/// ¿Cabe un hueco centrado en este punto en alguna pared de `r`?
///
/// **La misma pregunta que hace `fill::wall_side`, y tiene que ser la misma**: sobre una de las
/// cuatro paredes, con medio vano mínimo a cada lado dentro de esa pared. Un hueco que pase aquí y
/// no allí sería un enlace del plan que el relleno no puede cumplir — que es exactamente la clase de
/// fallo que la auditoría del 2026-09-02 encontró a razón de una docena por región.
/// La misma pregunta sobre la HUELLA de un espacio, que es la que vale desde ADR-120.
///
/// Delega en [`PlannedSpace::wall_side_of`], que es donde vive la respuesta una sola vez.
/// [`door_fits_on`] se conserva para los sitios que sólo tienen un rectángulo a mano —el recorte de
/// una escalera— y da lo mismo mientras el espacio no esté compuesto.
pub fn door_fits_in(s: &PlannedSpace, x_cm: i32, z_cm: i32) -> bool {
    s.wall_side_of(x_cm, z_cm, super::segment::MIN_GENERATED_WIDTH_CM / 2)
        .is_some()
}

pub fn door_fits_on(r: &PlanRect, x_cm: i32, z_cm: i32) -> bool {
    const EPS: i32 = 2;
    let half = super::segment::MIN_GENERATED_WIDTH_CM / 2;
    let on_x_wall = (r.min_x_cm - x_cm).abs() <= EPS || (r.max_x_cm - x_cm).abs() <= EPS;
    let on_z_wall = (r.min_z_cm - z_cm).abs() <= EPS || (r.max_z_cm - z_cm).abs() <= EPS;
    (on_x_wall && z_cm - half >= r.min_z_cm && z_cm + half <= r.max_z_cm)
        || (on_z_wall && x_cm - half >= r.min_x_cm && x_cm + half <= r.max_x_cm)
}

/// Une la sala con lo que le acaba de nacer al otro lado del corte de la franja.
fn link_along(plan: &mut RegionPlan, a: usize, a_rect: PlanRect, b: usize) {
    // **Contra la PARTE que se acaba de recortar, ni la envolvente ni la más ancha** (ADR-120 D1).
    //
    // La envolvente de una sala recortada por una escalera no comparte pared con la franja, así que
    // preguntándoselo a ella el enlace no se crea: la escalera nace construida y sin una sola
    // conexión — 85 espacios huérfanos medidos en 300 regiones.
    //
    // Y quedarse con la pared más ancha de todas las partes es igual de malo por el otro lado: en una
    // huella compuesta, el COSTADO de la escalera puede ser más largo que su cabecera, y entonces la
    // puerta sale de 5 m a media escalera. Ninguna tira de peldaño puede alojar un vano así, y el
    // síntoma es un hueco perdido — medido en la semilla viva, espacio 71 de la región (0,−1).
    //
    // La respuesta correcta es la única pared que significa algo: la del trozo del que se ha tallado.
    let Some((overlap, x, z)) = plan.spaces[b]
        .parts()
        .iter()
        .filter_map(|rb| rects_share_wall(a_rect, *rb))
        .max_by_key(|&(w, ..)| w)
    else {
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
    /// ADR-155 — semilla del MUNDO para el campo del laberinto. `None` = sin bioma.
    zone_seed: Option<i32>,
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

            let maze = self.nodes[node].maze;
            let Some((axis, at_cm, band_cm)) = self.decide_split(rect, depth, maze) else {
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
            self.nodes.push(Node::leaf(a_rect, depth + 1));
            self.band_of_node.push(None);
            let b = self.nodes.len();
            self.nodes.push(Node::leaf(b_rect, depth + 1));
            self.band_of_node.push(None);
            self.nodes[node].children = Some((a, b));
            // ADR-155 — la zona se hereda, y se decide una sola vez al llegar a los cuartos.
            self.nodes[a].maze = maze || self.starts_maze(a_rect, depth + 1);
            self.nodes[b].maze = maze || self.starts_maze(b_rect, depth + 1);
        }
    }

    /// **LA PLANTA ABIERTA DE OFICINA: dos hermanas se funden en UNA sala de 300-500 m².**
    ///
    /// La imagen canónica de oficina —una nave diáfana con treinta puestos en filas— no la daba el
    /// reparto, y la razón es aritmética y no de gusto: **el número de hojas de una zona es
    /// `área / objetivo`**, así que el tamaño de sala lo fija `TARGET_AREA_M2[class]` y no hay forma
    /// de pedir una sala grande sin agrandar TODAS las de esa zona (ADR-119 D2 midió esa calibración
    /// y no se toca).
    ///
    /// **Por eso esto no es un corte que no se hace: es un tabique que se quita.** Se elige el par de
    /// hojas hermanas cuyo padre ya mide lo que se busca y se deshace su corte. Consecuencias, todas
    /// por construcción: el árbol de subdivisión que ven las demás zonas es el mismo, las bandas de
    /// corredor son las mismas (sólo se mira un corte que NO talló banda, o entre las dos hermanas
    /// habría pasillo y no pared), y los candidatos a agujero de forjado, escalera y ciego siguen
    /// siendo los mismos menos uno — que es el único sitio donde esto puede costar plantas, y por eso
    /// se mide con el barrido de 27 regiones antes y después.
    ///
    /// UNA por planta y por región: dos naves diáfanas en el mismo forjado ya no son una oficina, son
    /// un almacén. El sorteo elige por puntuación de posición (R3), no por orden de recorrido: el
    /// orden de los nodos es «primero el más al noroeste», y quedarse con el primero pondría la sala
    /// grande siempre en la misma esquina de todas las regiones.
    fn fuse_open_plan(&mut self, atria_below: &[PlanRect]) {
        // **El interruptor del ANTES**, como `WG3_NO_SHAPE` en `compose_shapes`: con
        // `WG3_NO_OPEN_PLAN=1` el reparto sale exactamente como salía sin esta pasada, y eso es
        // lo que permite poner una medida de antes y una de después de la misma semilla sin
        // recompilar entre medias.
        if std::env::var("WG3_NO_OPEN_PLAN").is_ok() {
            return;
        }
        let mut best: Option<(f32, usize)> = None;
        for n in 0..self.nodes.len() {
            let Some((a, b)) = self.nodes[n].children else {
                continue;
            };
            // Las dos hijas tienen que ser hojas: fundir un nodo con nietos tiraría un reparto
            // entero, no un tabique.
            if self.nodes[a].children.is_some() || self.nodes[b].children.is_some() {
                continue;
            }
            // Y su corte no puede haber tallado banda: entre las dos hermanas hay un corredor, y la
            // unión de las dos NO es el rectángulo del padre.
            if self.band_of_node[n].is_some() {
                continue;
            }
            // ADR-155 — la planta abierta es de oficina; en la zona laberinto no se funde nada.
            if self.nodes[n].maze {
                continue;
            }
            let rect = self.nodes[n].rect;
            let area = rect.area_m2();
            if !(OPEN_PLAN_MIN_M2..=OPEN_PLAN_MAX_M2).contains(&area) {
                continue;
            }
            // **Ni encima de una nave de la planta de abajo.** `carve_atria` vacía TODO lo que pisa
            // una nave —ADR-104 D2, y se toma entera o no se toma—, así que fundir ahí sale gratis y
            // se pierde: la sala nacía `Void` y el edificio se quedaba sin planta abierta y sin
            // ninguna señal. Se midió: en el barrido, plantas altas con la sala fundida y vaciada.
            if atria_below.iter().any(|a| rect.overlaps(a)) {
                continue;
            }
            let slim = rect.width_cm().max(rect.depth_cm()) as f32
                / rect.width_cm().min(rect.depth_cm()).max(1) as f32;
            if slim > OPEN_PLAN_MAX_ASPECT {
                continue;
            }
            let (cx, cz) = rect.centre_m();
            // El carácter por el centro de la envolvente, exactamente el mismo criterio que
            // `fill::knobs_of`: una sala es de UN carácter entero.
            if super::fill::character_at(self.seed, cx, cz) != super::fill::Character::Office {
                continue;
            }
            let score = hash::stream_at(self.seed, cx, cz, SALT_OPEN_PLAN).next01();
            if best.is_none_or(|(bs, _)| score > bs) {
                best = Some((score, n));
            }
        }
        let Some((_, n)) = best else {
            return;
        };
        let (a, b) = self.nodes[n].children.expect("la candidata tiene hijas");
        self.nodes[a].dropped = true;
        self.nodes[b].dropped = true;
        self.nodes[n].children = None;
        self.nodes[n].open_plan = true;
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
            // Y **nunca una huella compuesta** (ADR-120 D5): `fill::emit_stair` reparte las
            // contrahuellas sobre el rectángulo del espacio, así que sobre una L pondría peldaños en
            // el trozo que ahora es del vecino — y un peldaño de más en la sala de al lado no lo ve
            // ningún contador.
            if !s.role.is_built() || s.role.is_circulation() || s.is_composite() {
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

    /// ADR-155 enm. 1 D1 — ¿empieza aquí una zona laberinto? Sólo en [`MAZE_ZONE_DEPTH`], sólo con
    /// semilla de mundo y con el interruptor puesto, y por el centro del rectángulo.
    fn starts_maze(&self, rect: PlanRect, depth: u8) -> bool {
        let Some(zone_seed) = self.zone_seed else {
            return false;
        };
        if !MAZE_BIOME_ENABLED || depth != MAZE_ZONE_DEPTH {
            return false;
        }
        let (cx, cz) = rect.centre_m();
        super::density::in_maze_zone(zone_seed, cx, cz)
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
    fn decide_split(&self, rect: PlanRect, depth: u8, maze: bool) -> Option<(u8, i32, i32)> {
        if depth >= MAX_DEPTH {
            return None;
        }
        let (cx, cz) = rect.centre_m();
        let class = self.scale_class(cx, cz);
        let weird = class == scale::SCALE_WEIRD;

        // ADR-155 D2 — dentro de la zona NO se talla pasillo: las hojas quedan pegadas.
        let base_band = if maze {
            0
        } else if depth < CORRIDOR_DEPTH {
            BAND_WIDTH_CM[depth as usize]
        } else if rect.area_m2() >= STUB_MIN_AREA_M2 {
            // Un corte profundo talla corredor sólo si le toca: es el corredor ciego.
            let chance = if weird {
                STUB_CHANCE_WEIRD
            } else {
                STUB_CHANCE
            };
            if hash::stream_at(self.seed, cx, cz, SALT_STUB).next01() < chance {
                BAND_WIDTH_CM[CORRIDOR_DEPTH as usize - 1]
            } else {
                0
            }
        } else {
            0
        };
        let band_cm = if base_band > 0 {
            let table = if weird {
                BAND_EXTRA_WEIRD_CM
            } else {
                BAND_EXTRA_CM
            };
            let k = (hash::stream_at(self.seed, cx, cz, SALT_BAND).next01() * table.len() as f32)
                as usize;
            base_band + table[k.min(table.len() - 1)]
        } else {
            0
        };

        // El área objetivo es lo que para la subdivisión, y es donde el campo de escala pasa a
        // decidir tamaños de espacio en vez de sesgar un sorteo de pieza.
        let target = if maze {
            MAZE_TARGET_AREA_M2
        } else if class == scale::SCALE_WEIRD {
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
            .filter(|&n| self.nodes[n].children.is_none() && !self.nodes[n].dropped)
            .collect();
        for n in leaves {
            let rect = self.nodes[n].rect;
            let depth = self.nodes[n].depth;
            // La cota la hereda del bloque: una sala es plana, y el desnivel vive en la escalera que
            // separa dos bloques. Sin esa regla haría falta un escalón en cada puerta.
            let (cx, cz) = rect.centre_m();
            let class = self.scale_class(cx, cz);
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
            // **La planta abierta es OFICINA aunque mida lo que una nave.** Por área le tocaría
            // `Hall`, y una nave no lleva falso techo (`fill::ceiling_cap_cm` mira el papel) ni
            // puestos (`fill::office_cubicles` sólo entra en `Office`): saldría un galpón vacío de
            // 400 m², que es lo contrario de lo que se ha fundido.
            let role = if self.nodes[n].open_plan {
                SpaceRole::Office
            } else {
                role
            };
            // Toda sala nace PLANA y a cota 0. El desnivel llega después y sólo a las que tienen una
            // sola puerta (`sink_dead_ends`), que es la única forma de que no se le escape a nadie.
            let s = self.push_space(rect, role, depth, 0, 0);
            self.spaces[s].open_plan = self.nodes[n].open_plan;
            self.spaces[s].maze = self.nodes[n].maze;
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
            // Ni la circulación ni la planta abierta: la sala que se acaba de fundir para que exista
            // no puede desaparecer en el sorteo siguiente. Sería el único hueco de la región que se
            // ha decidido dos veces y en direcciones contrarias.
            // Ni la zona laberinto (ADR-155): un hueco en ella la parte, y la zona es una planta continua.
            if s.role.is_circulation() || s.open_plan || s.maze {
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

        // 2b — ADR-155 D2: la ZONA LABERINTO. Toda pared entre dos hojas de la zona se abre en varios
        //      huecos anchos con trozos de pared entre ellos. Va antes de la 3 para que sus uniones
        //      cuenten al rescatar lo suelto, y las pasadas 3, 3b y 4 ya no tocan esas parejas.
        for &(i, j, ..) in &adj {
            if !(self.spaces[i].maze && self.spaces[j].maze)
                || !self.spaces[i].role.is_built()
                || !self.spaces[j].role.is_built()
            {
                continue;
            }
            for (w, x, z) in gaps_along_wall(self.seed, self.spaces[i].rect, self.spaces[j].rect) {
                self.links.push(PlannedLink {
                    a: i,
                    b: j,
                    width_cm: w,
                    kind: LinkKind::Gap,
                    at_x_cm: x,
                    at_z_cm: z,
                });
            }
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
                if self.spaces[i].maze && self.spaces[j].maze && self.linked(i, j) {
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

    /// **La planta abierta entra por DOS sitios como mínimo.**
    ///
    /// No es una preferencia estética: una sala de 400 m² con una sola boca es un fondo de saco de
    /// veinte metros —se entra, se recorre entera y se vuelve por donde se ha venido—, y además
    /// `retag_dead_ends` la degradaría a `DeadEnd`, con lo que perdería el papel `Office` del que
    /// dependen su falso techo y sus puestos. Es la misma regla que ya pide una sala autorada grande.
    ///
    /// Va DESPUÉS de `ensure_connected` y sólo AÑADE vanos sobre paredes que ya existen: la
    /// conectividad recién asegurada no puede empeorar por abrir una puerta de más, que es
    /// exactamente lo que hace la pasada de anillos.
    fn ensure_open_plan_doors(&mut self) {
        let Some(room) = self.spaces.iter().position(|s| s.open_plan) else {
            return;
        };
        if !self.spaces[room].role.is_built() {
            return;
        }
        let degree = self
            .links
            .iter()
            .filter(|l| l.a == room || l.b == room)
            .count();
        if degree >= 2 {
            return;
        }
        // La más ancha primero, y el índice del vecino para desempatar: el orden de `adjacencies`
        // ya es determinista, pero un empate resuelto por recorrido es un empate sin dueño.
        let mut cand: Vec<(i32, usize, i32, i32)> = self
            .adjacencies()
            .into_iter()
            .filter_map(|(i, j, w, x, z)| {
                let other = if i == room {
                    j
                } else if j == room {
                    i
                } else {
                    return None;
                };
                if !self.spaces[other].role.is_built() || self.linked(room, other) {
                    return None;
                }
                Some((w, other, x, z))
            })
            .collect();
        cand.sort_by_key(|&(w, other, _, _)| (std::cmp::Reverse(w), other));
        for (w, other, x, z) in cand.into_iter().take(2 - degree) {
            let kind = if self.spaces[other].role.is_circulation() {
                LinkKind::Access
            } else {
                LinkKind::Doorway
            };
            self.links.push(PlannedLink {
                a: room,
                b: other,
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
                // **Y con una sola boca deja de ser la planta abierta.** `ensure_open_plan_doors`
                // abre la segunda cuando hay pared donde abrirla, pero una sala cuyos vecinos son
                // todos vacío intencionado no tiene contra quién: entonces la marca miente, y una
                // marca que miente hace que el relleno vista de puestos un fondo de saco.
                space.open_plan = false;
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
        // Por PARTES (ADR-120 D1): la pared exterior de un espacio compuesto la puede poner
        // cualquiera de sus trozos, y preguntárselo a la envolvente diría que sí sobre un pedazo de
        // borde que ahora es del vecino — o sea, una puerta de junta abierta contra nada.
        let half = DOORWAY_CM / 2;
        self.spaces[i].parts().iter().any(|r| match side % 4 {
            0 => (r.max_z_cm - z).abs() <= EPS && x - half >= r.min_x_cm && x + half <= r.max_x_cm,
            1 => (r.max_x_cm - x).abs() <= EPS && z - half >= r.min_z_cm && z + half <= r.max_z_cm,
            2 => (r.min_z_cm - z).abs() <= EPS && x - half >= r.min_x_cm && x + half <= r.max_x_cm,
            _ => (r.min_x_cm - x).abs() <= EPS && z - half >= r.min_z_cm && z + half <= r.max_z_cm,
        })
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
    fn ensure_connected(&mut self, gates_in: &[usize]) {
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
            // **Un bolsillo de una o dos salas sin puente se declara VACÍO** (auditoría 2026-09-02).
            // El enrutador no garantiza nada —una pared enfrentada más corta que un vano y no hay
            // ruta— y lo que dejaba era una sala construida, con suelo y sin una sola puerta: la
            // peor versión de «aquí no hay nada». Un vacío intencionado es lo mismo que se quería
            // decir, dicho de verdad. Los bolsillos grandes siguen yendo al enrutador: perder tres
            // salas por no tender un pasillo sí sería tapar un fallo.
            let root_b = uf.find(b);
            let pocket: Vec<usize> = (0..self.spaces.len())
                .filter(|&i| self.spaces[i].role.is_built() && uf.find(i) == root_b)
                .collect();
            let has_gate = pocket.iter().any(|&i| gates_in.contains(&i));
            // ADR-155 enm. 3 (corrige la enm. 1 D2) — el laberinto trata sus bolsillos pequeños como
            // el resto: medido con el bioma encendido, 3 de 300 regiones mandaban al enrutador un
            // bolsillo de una o dos salas sin vecinos y el enrutador no sacaba ruta.
            if pocket.len() <= 2 && !has_gate {
                for i in pocket {
                    self.spaces[i].role = SpaceRole::Void;
                }
                self.links.retain(|l| {
                    self.spaces[l.a].role.is_built() && self.spaces[l.b].role.is_built()
                });
                continue;
            }
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
                // **La pared se busca entre PARTES** (ADR-120 D1). Con huellas compuestas dos
                // espacios pueden tocarse por más de una pared; se queda la MÁS LARGA, que es donde
                // una puerta se lee y donde cabe un vano ancho. Una segunda puerta entre los mismos
                // dos espacios es lo que ya hace el sorteo de anillos, y no se dobla aquí.
                let mut widest: Option<(i32, i32, i32, PlanRect, PlanRect)> = None;
                for &ra in self.spaces[i].parts() {
                    for &rb in self.spaces[j].parts() {
                        let Some((w, x, z)) = rects_share_wall(ra, rb) else {
                            continue;
                        };
                        if widest.is_none_or(|(bw, ..)| w > bw) {
                            widest = Some((w, x, z, ra, rb));
                        }
                    }
                }
                if let Some((w, x, z, ra, rb)) = widest {
                    // **La puerta no siempre va al centro** (auditoría 2026-09-02, Fase 5). A una
                    // nave sí: su vano puede ser ancho y tiene que caber. Al resto, sorteo por la
                    // posición de la pared: una puerta pegada a la esquina de vez en cuando es la
                    // «posición extraña» que se pidió, y sigue cabiendo porque se deja jamba.
                    // Y entre dos BANDAS tampoco: su cruce es tan ancho como el solape (hasta
                    // 500), y un cruce descentrado tres centímetros ya no cabe en su pared — el
                    // primer barrido lo pagó con un corredor de 12 m sin construir.
                    // Área del ESPACIO, no de la parte: una nave partida en tres trozos sigue siendo
                    // una nave, y su vano tiene que poder ser ancho.
                    let big = self.spaces[i].area_m2() >= HALL_AREA_M2
                        || self.spaces[j].area_m2() >= HALL_AREA_M2;
                    let both_bands = self.spaces[i].role.is_circulation()
                        && self.spaces[j].role.is_circulation();
                    let (x, z) = if big || both_bands {
                        (x, z)
                    } else {
                        door_along_wall(self.seed, ra, rb, w, x, z)
                    };
                    out.push((i, j, w, x, z));
                }
            }
        }
        out
    }

    /// La clase de escala de un punto, con el EMPUJE de las plantas altas (Fase 7): a partir de
    /// la segunda planta, una zona `Large` puede leerse como `Weird` con probabilidad creciente.
    /// Función pura de la posición, la semilla y la planta, como todo lo demás.
    fn scale_class(&self, cx: f32, cz: f32) -> u8 {
        let class = scale::scale_at(self.seed, cx, cz);
        let storey = storey_of_floor_cm(self.base_y_cm).max(0) as f32;
        if class == scale::SCALE_LARGE && storey >= 2.0 {
            let chance = ((storey - 1.0) * WEIRD_PER_STOREY).min(WEIRD_STOREY_CAP);
            if hash::stream_at(self.seed, cx, cz, SALT_WEIRD_UP).next01() < chance {
                return scale::SCALE_WEIRD;
            }
        }
        class
    }

    /// **Desalineación de vanos** (2026-09-03). Para cada espacio, dos vanos suyos sobre paredes
    /// paralelas distintas y a menos de [`DOOR_ALIGN_TOL_CM`] a lo largo de la pared son un eje
    /// recto: se corre el que se creó más tarde con [`DOOR_MISALIGN_CHANCE`].
    ///
    /// Lo que NO toca: la partición, el número de enlaces, los `Route` (no tienen pared), los cruces
    /// de bandas (un cruce descentrado no cabe en su pared, ver `adjacencies`), los vanos anchos y
    /// las escaleras (su puerta la recoloca `sink_dead_ends` contra las tiras de peldaño). El vano se
    /// mueve SÓLO dentro de la pared que los dos espacios comparten, con jamba a cada lado, y se
    /// vuelve a comprobar que cae en pared de ambos — si no, se queda donde estaba.
    fn misalign_doorways(&mut self, chance: f32) {
        for s in 0..self.spaces.len() {
            if self.spaces[s].role == SpaceRole::Stair {
                continue;
            }
            // Los vanos de este espacio: (enlace, ¿pared vertical?, coordenada de la pared,
            // coordenada a lo largo de la pared).
            let mut mine: Vec<(usize, bool, i32, i32)> = Vec::new();
            for (k, l) in self.links.iter().enumerate() {
                // ADR-155 — un hueco de la zona laberinto no se mueve: su sitio lo fija el reparto
                // de trozos de pared, y correrlo podría dejarlo encima de su vecino.
                if l.kind == LinkKind::Route || l.kind == LinkKind::Gap || (l.a != s && l.b != s) {
                    continue;
                }
                let half = l.width_cm / 2 + DOOR_JAMB_CM;
                let Some(side) = self.spaces[s].wall_side_of(l.at_x_cm, l.at_z_cm, half) else {
                    continue;
                };
                let vertical = side % 2 == 1;
                let (wall, along) = if vertical {
                    (l.at_x_cm, l.at_z_cm)
                } else {
                    (l.at_z_cm, l.at_x_cm)
                };
                mine.push((k, vertical, wall, along));
            }
            for i in 0..mine.len() {
                for j in (i + 1)..mine.len() {
                    let (ki, vi, wi, ci) = mine[i];
                    let (kj, vj, wj, cj) = mine[j];
                    if vi != vj || wi == wj || (ci - cj).abs() >= DOOR_ALIGN_TOL_CM {
                        continue;
                    }
                    // El que se creó más tarde es el que se mueve: el acceso al corredor manda.
                    let (mover, other_c) = if ki > kj { (i, cj) } else { (j, ci) };
                    let k = mine[mover].0;
                    if let Some((x, z)) = self.misaligned_door(k, vi, other_c, chance) {
                        self.links[k].at_x_cm = x;
                        self.links[k].at_z_cm = z;
                        mine[mover].3 = if vi { z } else { x };
                    }
                }
            }
        }
    }

    /// Adónde se corre el vano `k` para salir del eje de `other_c`, o `None` si el sorteo dice que
    /// no, si el vano no es de los que se mueven, o si su pared no tiene sitio fuera del eje.
    fn misaligned_door(
        &self,
        k: usize,
        vertical: bool,
        other_c: i32,
        chance: f32,
    ) -> Option<(i32, i32)> {
        let l = self.links[k];
        let (a, b) = (&self.spaces[l.a], &self.spaces[l.b]);
        if l.kind == LinkKind::Junction
            || l.width_cm > DOORWAY_CM
            || a.void_above
            || b.void_above
            || a.role == SpaceRole::Stair
            || b.role == SpaceRole::Stair
        {
            return None;
        }
        let mut st = hash::stream_at(
            self.seed,
            l.at_x_cm as f32 / CM_PER_M,
            l.at_z_cm as f32 / CM_PER_M,
            SALT_MISALIGN,
        );
        if st.next01() >= chance {
            return None;
        }
        let t = st.next01();

        // La pared compartida sobre la que está el vano: el par de partes cuya pared común contiene
        // el punto actual. El recorrido a lo largo de esa pared, con jamba, es donde puede caer.
        const EPS: i32 = 2;
        let margin = l.width_cm / 2 + DOOR_JAMB_CM;
        let mut range: Option<(i32, i32)> = None;
        for ra in a.parts() {
            for rb in b.parts() {
                let Some((_, x, z)) = rects_share_wall(*ra, *rb) else {
                    continue;
                };
                let (lo, hi) = if vertical {
                    if (x - l.at_x_cm).abs() > EPS {
                        continue;
                    }
                    (ra.min_z_cm.max(rb.min_z_cm), ra.max_z_cm.min(rb.max_z_cm))
                } else {
                    if (z - l.at_z_cm).abs() > EPS {
                        continue;
                    }
                    (ra.min_x_cm.max(rb.min_x_cm), ra.max_x_cm.min(rb.max_x_cm))
                };
                let along = if vertical { l.at_z_cm } else { l.at_x_cm };
                if along < lo || along > hi {
                    continue;
                }
                range = Some((lo + margin, hi - margin));
            }
        }
        let (lo, hi) = range?;
        if hi <= lo {
            return None;
        }

        // Fuera del eje: a un lado o al otro de `other_c`, el tramo que tenga más sitio.
        let left = (lo, (other_c - DOOR_ALIGN_TOL_CM).min(hi));
        let right = ((other_c + DOOR_ALIGN_TOL_CM).max(lo), hi);
        let len = |(p, q): (i32, i32)| (q - p).max(-1);
        let pick = if len(left) >= len(right) { left } else { right };
        if len(pick) < 0 {
            return None;
        }
        let along = pick.0 + ((pick.1 - pick.0) as f32 * t) as i32;
        let (x, z) = if vertical {
            (l.at_x_cm, along)
        } else {
            (along, l.at_z_cm)
        };
        if !door_fits_in(a, x, z) || !door_fits_in(b, x, z) {
            return None;
        }
        Some((x, z))
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
            // Toda hoja nace RECTANGULAR. La deformación es una pasada aparte y posterior
            // (`compose_shapes`), para que el reparto siga siendo un teselado exacto mientras se hace.
            parts: [rect; MAX_PARTS],
            part_count: 1,
            // ADR-102 D1 — la cota de la planta se suma AQUÍ y en ningún otro sitio, así que el resto
            // del planificador sigue trabajando en su propio cero y no se entera de a qué altura está.
            floor_y_cm: self.base_y_cm + floor_y_cm,
            role,
            scale: self.scale_class(cx, cz),
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
            void_storeys_above: 0,
            atrium_storeys: 0,
            // La sortea `assign_ceilings` al cerrar la planta, con la huella ya definitiva.
            ceiling_clear_cm: 0,
            // Lo pone `emit_leaves` a la ÚNICA hoja fundida, si es que hubo alguna.
            open_plan: false,
            // Lo pone `emit_leaves` desde el nodo; una banda nunca es de la zona.
            maze: false,
        });
        self.spaces.len() - 1
    }
}

/// **ADR-120 — LA COMPOSICIÓN DE HUELLAS: dos hojas rectangulares se entrelazan.**
///
/// # Por qué aquí y no dentro del BSP
///
/// Un corte de guillotina sobre un rectángulo da rectángulos, y ninguna perilla cambia eso: la
/// rectangularidad no es un parámetro del reparto, es su tipo de dato. Cortar en quiebro obligaría a
/// que los `Node` llevasen huella compuesta y a que todo corte posterior supiera partir una L, que es
/// rehacer el planificador entero. **Se deforman las HOJAS, y el BSP no se entera.**
///
/// # Por qué se TRANSFIERE y no se recorta
///
/// Recortar una esquina deja terreno que no es de nadie: ni se construye, ni se declara vacío, ni
/// aparece en ningún contador. Cediendo el rectángulo al vecino **el teselado sigue siendo exacto y
/// el área se conserva al centímetro** —que es la comprobación que caza cualquier error de esta
/// pasada de un plumazo— y salen DOS formas por operación: el donante queda en L, U o Z y el receptor
/// gana un bulto.
///
/// # Dónde va en el orden, y por qué se movió
///
/// **Al final del EDIFICIO, no dentro de la planta.** El primer intento la puso en `plan_storey`,
/// entre el vacío y las puertas, con el argumento de que así ninguna puerta habría que reubicarla. Es
/// verdad y no basta: a esas alturas todavía no existen ni las escaleras ni los rellanos, así que la
/// deformación se comía sitios que la verticalidad necesitaba sin que nada lo dijera. Medido en la
/// semilla viva: la región (0,1) pasó de tener UNA candidata de escalera a cero, se quedó en una sola
/// planta, y con ella se cayeron las tres pruebas de verticalidad a la vez.
///
/// Corriéndola al final se invierte el trato: las puertas ya existen —y hay que respetarlas, lo que
/// se comprueba una por una— pero los pozos, los rellanos y los tiros también, y ésos se pueden
/// esquivar. Una puerta que no cabe descarta un mordisco; una planta que no sube no se recupera.
///
/// # Lo que esta versión NO hace
///
/// No toca las bandas de circulación —ni como donante ni como receptor—: ahí vive la conectividad del
/// edificio, y la intrusión de un corredor en una sala es el operador siguiente, que se mide aparte.
/// Y no deforma dos veces el mismo espacio: es la forma más fuerte de la regla «si ya está deformado,
/// mucho menos», y es lo que mantiene la Z como excepción y no como estilo.
/// **Los TRAMOS de pared recta de una planta, por coordenada y por eje.**
///
/// Devuelve `(por X, por Z)`: para cada coordenada, los intervalos de pared continua que hay sobre
/// ella, ya fusionados. Es la medida de «cuadrícula» convertida en dato: lo que delata a un BSP no es
/// que cada sala sea un rectángulo, es que el corte de guillotina deja una LÍNEA recta compartida por
/// todas las hojas de su subárbol, y esa línea se ve desde dentro aunque ninguna de las paredes que
/// la forman mida más de seis metros.
///
/// **Sólo cuentan las caras que son PARED.** Una cara entre dos partes del mismo espacio es interior
/// —ahí hay sala, no muro—, y contarla hacía que la línea siguiera «existiendo» detrás de cada bahía:
/// con la composición encendida y apagada la cuenta daba exactamente lo mismo.
fn wall_runs(plan: &RegionPlan) -> (WallRuns, WallRuns) {
    fn subtract(segs: &[(i32, i32)], a: i32, b: i32) -> Vec<(i32, i32)> {
        let mut out = Vec::new();
        for &(s0, s1) in segs {
            if b <= s0 || a >= s1 {
                out.push((s0, s1));
                continue;
            }
            if s0 < a {
                out.push((s0, a));
            }
            if b < s1 {
                out.push((b, s1));
            }
        }
        out
    }
    let mut fx: WallRuns = WallRuns::new();
    let mut fz: WallRuns = WallRuns::new();
    for s in plan.spaces.iter().filter(|s| s.role.is_built()) {
        let ps = s.parts();
        for r in ps {
            for (k, hi) in [(r.min_x_cm, false), (r.max_x_cm, true)] {
                let mut segs = vec![(r.min_z_cm, r.max_z_cm)];
                for o in ps {
                    if (hi && o.min_x_cm == k) || (!hi && o.max_x_cm == k) {
                        segs = subtract(&segs, o.min_z_cm, o.max_z_cm);
                    }
                }
                fx.entry(k).or_default().extend(segs);
            }
            for (k, hi) in [(r.min_z_cm, false), (r.max_z_cm, true)] {
                let mut segs = vec![(r.min_x_cm, r.max_x_cm)];
                for o in ps {
                    if (hi && o.min_z_cm == k) || (!hi && o.max_z_cm == k) {
                        segs = subtract(&segs, o.min_x_cm, o.max_x_cm);
                    }
                }
                fz.entry(k).or_default().extend(segs);
            }
        }
    }
    for m in [&mut fx, &mut fz] {
        for v in m.values_mut() {
            v.sort_unstable();
            let mut merged: Vec<(i32, i32)> = Vec::with_capacity(v.len());
            for &(a, b) in v.iter() {
                match merged.last_mut() {
                    Some(last) if a <= last.1 => last.1 = last.1.max(b),
                    _ => merged.push((a, b)),
                }
            }
            *v = merged;
        }
    }
    (fx, fz)
}

/// Los tramos de pared continua de un eje, por coordenada. Ver [`wall_runs`].
type WallRuns = std::collections::HashMap<i32, Vec<(i32, i32)>>;

/// Una pareja de espacios que comparten pared y podrían intercambiar volumen.
///
/// Lleva encima el TRAMO de línea recta sobre el que se apoya esa pared, porque es lo que ordena la
/// pasada: ver [`wall_runs`].
#[derive(Clone, Copy)]
struct Cand {
    x: i32,
    z: i32,
    i: usize,
    j: usize,
    wall_cm: i32,
    pi: usize,
    pj: usize,
    /// La pared es vertical: los dos se tocan por una cara en X.
    vertical: bool,
    run: (i32, i32),
}

impl Cand {
    /// Primero la LÍNEA más larga, luego la pared. La posición va detrás como desempate: el
    /// resultado no puede depender del orden en que salieron las hojas del árbol.
    fn rank(&self) -> (i32, i32, i32, i32, usize, usize) {
        (
            -(self.run.1 - self.run.0),
            -self.wall_cm,
            self.x,
            self.z,
            self.i,
            self.j,
        )
    }

    fn run_on(&self, lx: &WallRuns, lz: &WallRuns) -> (i32, i32) {
        let (map, key, along) = if self.vertical {
            (lx, self.x, self.z)
        } else {
            (lz, self.z, self.x)
        };
        map.get(&key)
            .and_then(|v| v.iter().find(|&&(a, b)| along >= a && along <= b))
            .copied()
            .unwrap_or((0, 0))
    }
}

fn compose_shapes(
    plan: &mut RegionPlan,
    seed: i32,
    keep_hard: &[PlanRect],
    keep_soft: &[PlanRect],
    gate_cuts_x: &[GateCut],
    gate_cuts_z: &[GateCut],
) {
    // **Un espacio con puerta de junta NO se deforma, y no es prudencia: es la misma exclusión que
    // ADR-105 enmienda 3 ya pagó con dos puertas selladas.**
    //
    // El borde de región se comporta como un hueco: una forma nueva junto a él puede encerrar la
    // puerta entre el borde y ella sin tocarla, y entonces la región nace sellada contra su vecina
    // mientras aquélla abre la suya contra el muro. Medido aquí también: `puerta de junta en
    // (20.5,150.0) sin suelo alcanzable por dentro`. Una sala menos deformada no es un precio.
    // **El interruptor del ANTES.** `WG3_NO_SHAPE=1` deja el teselado del BSP tal cual salió, y es
    // lo único que permite poner un volcado de antes y uno de después de la misma semilla uno al
    // lado del otro sin recompilar entre medias. Sin él, el «antes» hay que sacarlo de un worktree
    // en otro commit y deja de ser comparable en cuanto la rama avanza.
    if std::env::var("WG3_NO_SHAPE").is_ok() {
        return;
    }

    let gated: Vec<usize> = plan.gates.iter().map(|g| g.space).collect();
    // Las puertas que ya existen, por espacio. Son la restricción de esta pasada: un mordisco que
    // deje una puerta fuera de la pared de su dueño la convierte en un vano que se dibuja y no se
    // pasa, y eso no lo ve ningún contador.
    let mut doors: Vec<Vec<(i32, i32, i32)>> = vec![Vec::new(); plan.spaces.len()];
    for l in &plan.links {
        if l.kind == LinkKind::Route {
            continue;
        }
        doors[l.a].push((l.at_x_cm, l.at_z_cm, l.width_cm));
        doors[l.b].push((l.at_x_cm, l.at_z_cm, l.width_cm));
    }
    for g in &plan.gates {
        doors[g.space].push((g.x_cm, g.z_cm, g.width_cm));
    }

    // **CUÁNTA PARED HAY SOBRE CADA COORDENADA, por eje.**
    //
    // Es la medida de «cuadrícula» convertida en dato de entrada. Lo que delata a un BSP no es que
    // cada sala sea un rectángulo: es que el corte de guillotina deja una LÍNEA recta compartida por
    // todas las hojas de su subárbol, y esa línea cruza media región aunque ninguna de las paredes
    // que la forman mida más de seis metros. Medido en el barrido de 10 semillas: **20,4 líneas de
    // más de media región por planta**, y la pasada de mordiscos las estaba ignorando porque ordena
    // por pared y una línea larga son muchas paredes cortas apiladas.
    let (line_x, line_z) = wall_runs(plan);

    // Candidatos ordenados por la POSICIÓN de su pared y no por índice: así el resultado no depende
    // de en qué orden salieron las hojas del árbol.
    let mut cands: Vec<Cand> = Vec::new();
    for i in 0..plan.spaces.len() {
        for j in (i + 1)..plan.spaces.len() {
            if gated.contains(&i) || gated.contains(&j) {
                continue;
            }
            if !may_deform(&plan.spaces[i], keep_hard) || !may_deform(&plan.spaces[j], keep_hard) {
                continue;
            }
            // Y alguien tiene que poder ceder: dos bandas contiguas no tienen nada que intercambiar.
            if !may_donate(&plan.spaces[i]) && !may_donate(&plan.spaces[j]) {
                continue;
            }
            // Entre PARTES y la pared más ancha: un espacio ya deformado puede volver a serlo por
            // otro lado, y entonces la envolvente ya no es quien toca a nadie.
            let mut best: Option<Cand> = None;
            for (pi, ra) in plan.spaces[i].parts().iter().enumerate() {
                for (pj, rb) in plan.spaces[j].parts().iter().enumerate() {
                    let Some((w, x, z)) = rects_share_wall(*ra, *rb) else {
                        continue;
                    };
                    // Sobre qué línea se apoya esta pared. Vertical si los dos rectángulos se tocan
                    // por una cara en X; el `rects_share_wall` que la encontró ya lo decidió, así que
                    // se relee de la geometría en vez de devolverse por el mismo sitio dos veces.
                    let vertical = (ra.max_x_cm - rb.min_x_cm).abs() <= 1
                        || (rb.max_x_cm - ra.min_x_cm).abs() <= 1;
                    // El TRAMO de esa línea al que pertenece esta pared, no la línea entera: una
                    // coordenada puede tener dos tramos separados y sólo el que toca importa.
                    let (map, along) = if vertical { (&line_x, z) } else { (&line_z, x) };
                    let run = map
                        .get(&if vertical { x } else { z })
                        .and_then(|v| v.iter().find(|&&(a, b)| along >= a && along <= b))
                        .copied()
                        .unwrap_or((0, 0));
                    if best.is_none_or(|b| w > b.wall_cm) {
                        best = Some(Cand {
                            x,
                            z,
                            i,
                            j,
                            wall_cm: w,
                            pi,
                            pj,
                            vertical,
                            run,
                        });
                    }
                }
            }
            if let Some(c) = best {
                cands.push(c);
            }
        }
    }
    // **Y por pared MÁS LARGA primero, no sólo por posición.**
    //
    // El presupuesto es de dos mordiscos por espacio, así que el orden decide en qué paredes se
    // gasta. Recorriendo por posición se lo comían las paredes cortas que el barrido encontraba
    // antes, que son justo las que `deform_pressure` ya penaliza por no leerse. La posición sigue
    // detrás como desempate: el resultado no puede depender del orden en que salieron las hojas.
    // **Y primero la LÍNEA más larga, luego la pared.**
    //
    // El presupuesto es de dos o tres mordiscos por espacio, así que el orden decide en qué paredes
    // se gasta. Ordenando sólo por pared se lo comían las paredes largas sueltas —una nave contra
    // otra— y quedaban intactas las líneas de guillotina, que son muchas paredes cortas sobre la
    // misma coordenada y son las que se ven desde dentro. La posición sigue detrás como desempate: el
    // resultado no puede depender del orden en que salieron las hojas.
    cands.sort_unstable_by_key(Cand::rank);
    if std::env::var("WG3_SHAPE_DEBUG").is_ok() {
        let n_void = plan.spaces.iter().filter(|s| !s.role.is_built()).count();
        let n_circ = plan
            .spaces
            .iter()
            .filter(|s| s.role.is_circulation())
            .count();
        let n_keep = plan
            .spaces
            .iter()
            .filter(|s| keep_hard.iter().any(|k| s.hits_rect(k)))
            .count();
        eprintln!(
            "[shape] cands {} | espacios {} (vacio {n_void}, banda {n_circ}, pozo {n_keep}, con-junta {})",
            cands.len(),
            plan.spaces.len(),
            gated.len()
        );
    }

    let mut touched = vec![0u8; plan.spaces.len()];
    // Por dónde se cae cada mordisco. `WG3_SHAPE_DEBUG=1` lo escupe: sin esto, subir la dosis es
    // mover números a ciegas, que es exactamente lo que este trabajo tiene prohibido.
    let mut why = [0usize; 7];
    let mut miss = BiteMisses::default();
    // **Y la tabla de líneas va VIVA.** Cada mordisco puesto parte la línea sobre la que cayó, y con
    // la tabla congelada los siguientes se gastaban en la mitad ya rota: 18 mordiscos por planta para
    // quitar 4 líneas de 12. Recalcular cuesta un recorrido de las huellas por mordisco aceptado
    // —quince veces por planta sobre cien espacios— y hace que el presupuesto vaya siempre a la línea
    // más larga que QUEDA.
    let mut pending = cands;
    let mut stale = false;
    while !pending.is_empty() {
        if stale {
            let (lx, lz) = wall_runs(plan);
            for c in pending.iter_mut() {
                c.run = c.run_on(&lx, &lz);
            }
            pending.sort_unstable_by_key(Cand::rank);
            stale = false;
        }
        let Cand {
            x,
            z,
            i,
            j,
            wall_cm,
            pi,
            pj,
            run,
            ..
        } = pending.remove(0);
        let line_cm = run.1 - run.0;
        if touched[i] >= deform_budget(&plan.spaces[i])
            || touched[j] >= deform_budget(&plan.spaces[j])
        {
            why[0] += 1;
            continue;
        }
        let mut st = hash::stream_at(seed, x as f32 / CM_PER_M, z as f32 / CM_PER_M, SALT_SHAPE);
        if st.next01() >= deform_pressure(&plan.spaces[i], &plan.spaces[j], wall_cm, line_cm) {
            why[1] += 1;
            continue;
        }

        // **Quién cede.** Con tamaños dispares manda el grande: una nave que crece de lado hacia su
        // vecina pequeña es una extensión lateral, y la pequeña se queda con la esquina retirada. Con
        // tamaños parecidos lo decide el sorteo, y sale un entrelazado mutuo.
        let (aa, ab) = (plan.spaces[i].area_m2(), plan.spaces[j].area_m2());
        let ratio = aa.max(ab) / aa.min(ab).max(1.0);
        // **Quién cede, y si ése no da, el otro.** Con tamaños dispares manda el grande: una nave
        // que crece de lado hacia su vecina pequeña es una extensión lateral, y la pequeña se queda
        // con la esquina retirada. Con tamaños parecidos lo decide el sorteo.
        //
        // Y probar el otro sentido cuando el primero no da es el mismo razonamiento que `decide_split`
        // ya hace con el eje del corte: rendirse al primer intento deja sin deformar parejas que sí
        // admitían una forma por el otro lado, y no cuesta nada.
        let (can_i, can_j) = (may_donate(&plan.spaces[i]), may_donate(&plan.spaces[j]));
        // Si sólo uno puede ceder, el sentido no se sortea: lo impone el papel. Y entonces no hay
        // segundo intento, porque el segundo es el sentido prohibido.
        let first = if !can_j {
            (i, j, pi, pj)
        } else if !can_i {
            (j, i, pj, pi)
        } else if ratio >= 1.6 {
            if aa > ab {
                (j, i, pj, pi)
            } else {
                (i, j, pi, pj)
            }
        } else if st.next01() < 0.5 {
            (i, j, pi, pj)
        } else {
            (j, i, pj, pi)
        };
        let both = can_i && can_j;
        let second = (first.1, first.0, first.3, first.2);

        'pair: for (k_try, (donor, recip, dpart, rpart)) in [first, second].into_iter().enumerate()
        {
            if k_try == 1 && !both {
                break;
            }
            // **Se muerde la PARTE, no la envolvente.** Con espacios que ya pueden estar deformados,
            // la envolvente cubre terreno del vecino y el mordisco saldría de donde no hay suelo.
            let dr = plan.spaces[donor].parts()[dpart];
            let rr = plan.spaces[recip].parts()[rpart];
            let Some(side) = side_towards(&dr, &rr) else {
                why[2] += 1;
                continue;
            };
            // Todas las caras que los dos espacios ya tienen, por eje. Los extremos del tramo y el
            // fondo se pegan a la más cercana, y así no nace ninguna celda de rejilla estrecha en vez
            // de nacer y descartarse.
            let along_is_z = side % 2 == 1;
            let face_lines = |along: bool| -> Vec<i32> {
                plan.spaces[donor]
                    .parts()
                    .iter()
                    .chain(plan.spaces[recip].parts().iter())
                    .flat_map(|r| {
                        if along == along_is_z {
                            [r.min_z_cm, r.max_z_cm]
                        } else {
                            [r.min_x_cm, r.max_x_cm]
                        }
                    })
                    .collect()
            };
            let snap_lines = face_lines(true);
            let perp_lines = face_lines(false);

            // **La puerta que une a estos dos se puede MOVER, y por eso no estorba.**
            //
            // Está en la pared que el mordisco se lleva —es la única puerta que hay ahí por
            // construcción—, así que tratarla como intocable dejaba el tramo sin sitio: 73 de 111
            // intentos morían por no encontrar hueco. Pero es SU puerta: después del mordisco los dos
            // siguen compartiendo pared, sólo que corrida, y reubicarla ahí es una decisión del plan
            // igual que ponerla la primera vez. Las de terceros no se tocan.
            let own = plan.links.iter().position(|l| {
                l.kind != LinkKind::Route
                    && ((l.a == donor && l.b == recip) || (l.a == recip && l.b == donor))
            });
            let own_at = own.map(|k| (plan.links[k].at_x_cm, plan.links[k].at_z_cm));
            let others: Vec<(i32, i32, i32)> = doors[donor]
                .iter()
                .copied()
                .filter(|&(dx, dz, _)| own_at != Some((dx, dz)))
                .collect();

            for _ in 0..BITE_TRIES {
                let Some((kept, ceded)) = plan_bite(
                    &dr,
                    &rr,
                    side,
                    wall_cm,
                    &mut st,
                    &BiteLimits {
                        doors: &others,
                        snap_lines: &snap_lines,
                        perp_lines: &perp_lines,
                        gate_cuts_x,
                        gate_cuts_z,
                        cut_at: (line_cm >= DEFORM_LINE_CM).then(|| (run.0 + run.1) / 2),
                    },
                    &mut miss,
                ) else {
                    why[3] += 1;
                    continue;
                };
                // Lo cedido no puede pisar un tiro, una boca de pozo ni un rellano: esa geometría ya
                // está decidida y la deformación llega después justamente para no tocarla.
                //
                // **Y el cerco de las juntas se comprueba AQUÍ, con holgura, en vez de vetar el
                // espacio entero.** Vetarlo costaba la pasada: el cerco de una puerta de junta es un
                // pasillo de ochenta metros hacia dentro, y con cuatro o cinco juntas por región
                // tapaba 75 de los 103 espacios de la región (0,0) — o sea que la composición de
                // huellas sólo podía tocar el resto, y por eso el volcado seguía siendo la misma
                // cuadrícula.
                //
                // El veto general era además la pregunta equivocada. El área se CONSERVA: un
                // mordisco no borra suelo, lo cambia de dueño. Lo único que puede sellar una puerta
                // es la pared NUEVA, y esa pared está en el contorno de lo cedido. Comprobar lo
                // cedido inflado una holgura cubre exactamente eso, y deja deformarse a un espacio
                // que toca el cerco por un lado y muerde por el otro, a treinta metros.
                if ceded.iter().any(|c| {
                    let grown = c.shrunk(-KEEP_SOFT_MARGIN_CM);
                    keep_hard.iter().any(|k| k.overlaps(c))
                        || keep_soft.iter().any(|k| k.overlaps(&grown))
                }) {
                    why[4] += 1;
                    continue;
                }

                let mut recip_parts: Vec<PlanRect> = plan.spaces[recip].parts().to_vec();
                recip_parts.extend_from_slice(&ceded);
                let mut kept_parts: Vec<PlanRect> = plan.spaces[donor]
                    .parts()
                    .iter()
                    .enumerate()
                    .filter(|&(k, _)| k != dpart)
                    .map(|(_, r)| *r)
                    .collect();
                kept_parts.extend_from_slice(&kept);
                merge_parts(&mut kept_parts);
                merge_parts(&mut recip_parts);
                if kept_parts.len() > MAX_PARTS || recip_parts.len() > MAX_PARTS {
                    why[5] += 1;
                    continue;
                }
                // **Las dos huellas tienen que caber en una rejilla de celdas anchas.**
                //
                // El relleno emite una celda por casilla de la rejilla que forman las caras de las
                // partes (ADR-120 D4), así que dos caras a 183 cm producen una celda de 183 y una
                // boca de 183 — por debajo del mínimo que el ráster conservador deja pasar, o sea un
                // trozo de la sala tapiado por dentro. Es más barato descartarlo aquí que arreglarlo
                // allí: allí ya no se sabe qué forma se quería.
                if !grid_spans_are_wide(&kept_parts) || !grid_spans_are_wide(&recip_parts) {
                    why[5] += 1;
                    continue;
                }
                // **Y a toda celda de rejilla se le pide holgura de VANO, no el mínimo generable.**
                //
                // Los 200 cm de `grid_spans_are_wide` son lo que el ráster deja GENERAR; por una
                // celda de rejilla, además, se pasa. Con el mínimo justo, una celda de 200 sobrevive
                // al ráster conservador y no sobrevive a la nav: salió como `nav sólo alcanza el
                // 12 %` en 2 de 270 regiones del barrido en cuanto la pasada empezó a apuntar a las
                // líneas largas y a morder más hondo. Cuarenta centímetros más cuestan algunos
                // mordiscos y quitan la clase de fallo entera.
                if !cells_are_at_least(&kept_parts, DOORWAY_CM)
                    || !cells_are_at_least(&recip_parts, DOORWAY_CM)
                {
                    why[5] += 1;
                    continue;
                }

                let mut d_space = plan.spaces[donor];
                let mut r_space = plan.spaces[recip];
                if !d_space.set_parts(&kept_parts) || !r_space.set_parts(&recip_parts) {
                    why[5] += 1;
                    continue;
                }
                // **Y ninguna puerta se queda sin pared.** Cubre de un golpe los dos casos que
                // importan: la del propio donante, y la de un TERCERO que entraba justo por el trozo
                // cedido — su enlace sigue apuntando aquí y esa pared ya es del vecino.
                let recip_others: Vec<(i32, i32, i32)> = doors[recip]
                    .iter()
                    .copied()
                    .filter(|&(dx, dz, _)| own_at != Some((dx, dz)))
                    .collect();
                if others
                    .iter()
                    .any(|&(dx, dz, _)| !door_fits_in(&d_space, dx, dz))
                    || recip_others
                        .iter()
                        .any(|&(dx, dz, _)| !door_fits_in(&r_space, dx, dz))
                    || !doors_fit_cells(&kept_parts, &others)
                    || !doors_fit_cells(&recip_parts, &recip_others)
                {
                    why[6] += 1;
                    continue;
                }
                // Y la puerta propia se recoloca en la pared que los dos comparten AHORA. Si de esa
                // pared no queda un vano, el mordisco no vale: los habría separado.
                let moved = own.map(|k| {
                    let w = plan.links[k].width_cm;
                    d_space
                        .parts()
                        .iter()
                        .flat_map(|ra| {
                            r_space
                                .parts()
                                .iter()
                                .filter_map(move |rb| rects_share_wall(*ra, *rb))
                        })
                        .max_by_key(|&(o, ..)| o)
                        .map(|(o, x, z)| {
                            // **Y se coloca con `door_along_wall`, no en el centro exacto.**
                            //
                            // El centro de una pared es justo donde el relleno pone su corte de
                            // teselado (`emit_space` parte a mitades), y aunque `shift_cuts` lo
                            // aparta, dos puertas que caen ahí a la vez lo dejan sin sitio adonde ir
                            // y una acaba a caballo de dos tramos: se pierde, y con ella el ala que
                            // colgaba. Es la misma función con la que el plan reparte cualquier otra
                            // puerta, con su misma sal — así que sigue siendo función de la posición.
                            let w2 = o.min(w).max(DOORWAY_CM).min(o);
                            let (dx, dz) = door_along_wall(seed, dr, rr, o, x, z);
                            (k, w2, dx, dz)
                        })
                });
                if let Some(None) = moved {
                    why[6] += 1;
                    continue;
                }
                if let Some(Some((k, w, x, z))) = moved {
                    // Y tiene que caer en pared de los dos. `rects_share_wall` mira dos PARTES; esa
                    // pared puede ser interior a la huella completa del otro —dos partes suyas que se
                    // tocan ahí—, y entonces el vano no existe.
                    //
                    // **Y tiene que caber en su CELDA, igual que las demás.** Este punto se calcula
                    // después de la comprobación de rejilla, así que era la única puerta del mundo
                    // que nadie miraba contra ella: si cae a caballo de una cara de parte no la aloja
                    // ningún tramo, y lo que se pierde no es una puerta — es el ala entera que
                    // colgaba de ella. Medido en el barrido de 30 semillas: 2 regiones de 270 con una
                    // isla de 5 000 celdas y la puerta de junta dentro.
                    let one = [(x, z, w)];
                    if w < DOORWAY_CM
                        || !door_fits_in(&d_space, x, z)
                        || !door_fits_in(&r_space, x, z)
                        || !doors_fit_cells(&kept_parts, &one)
                        || !doors_fit_cells(&recip_parts, &one)
                    {
                        why[6] += 1;
                        continue;
                    }
                    plan.links[k].width_cm = w;
                    plan.links[k].at_x_cm = x;
                    plan.links[k].at_z_cm = z;
                    // **Y la lista de puertas se entera.** Un espacio puede morderse dos veces
                    // (`MAX_DEFORMS`), y el segundo mordisco valida contra esta lista: dejándola con
                    // el punto viejo aceptaba mordiscos que rompían la puerta que el primero acababa
                    // de mover.
                    for who in [donor, recip] {
                        for d in doors[who].iter_mut() {
                            if own_at == Some((d.0, d.1)) {
                                *d = (x, z, w);
                            }
                        }
                    }
                }
                if std::env::var("WG3_SHAPE_DEBUG").is_ok() {
                    let ced: f32 = ceded.iter().map(|c| c.area_m2()).sum();
                    eprintln!(
                        "[bite] {:?}({:.0}m2) -> {:?}({:.0}m2) pared {}cm cedido {:.0}m2",
                        plan.spaces[donor].role,
                        plan.spaces[donor].area_m2(),
                        plan.spaces[recip].role,
                        plan.spaces[recip].area_m2(),
                        wall_cm,
                        ced
                    );
                }
                plan.spaces[donor] = d_space;
                plan.spaces[recip] = r_space;
                touched[donor] += 1;
                touched[recip] += 1;
                stale = true;
                break 'pair;
            }
        }
    }
    if std::env::var("WG3_SHAPE_DEBUG").is_ok() {
        let count = |m: &WallRuns| -> usize {
            m.values()
                .map(|v| v.iter().filter(|&&(a, b)| b - a >= DEFORM_LINE_CM).count())
                .sum()
        };
        let before = count(&line_x) + count(&line_z);
        let (ax, az) = wall_runs(plan);
        eprintln!(
            "[shape] lineas largas {before} -> {}",
            count(&ax) + count(&az)
        );
        let done: usize = touched.iter().map(|&t| t as usize).sum::<usize>() / 2;
        eprintln!(
            "[shape] {done} mordiscos | descartes: ya-tocado {}, dado {}, sin-lado {}, \
             geometria {}, pozo {}, rejilla {}, puerta {}",
            why[0], why[1], why[2], why[3], why[4], why[5], why[6]
        );
        eprintln!(
            "[shape]   geometria: poco-fondo {}, sin-fondo {}, sin-hueco {}, tirada-corta {}, \
             junta {}, avaricioso {}",
            miss.shallow, miss.no_depth, miss.no_gap, miss.short_run, miss.gate, miss.greedy
        );
    }
}

/// **Cuántas veces puede deformarse un mismo espacio.**
///
/// Dos, no una y no tres. Una deja fuera un cuarto de las parejas que querían disparar —medido:
/// 22 descartes por «ya tocado» contra 10 mordiscos puestos— y con ella no hay forma de que un
/// espacio se quiebre por dos lados, que es de donde salen las plantas que no se leen de un vistazo.
/// Tres desborda el presupuesto de cuatro partes y convierte la excepción en estilo.
const MAX_DEFORMS: u8 = 2;

/// Y **tres si es una banda**, porque una banda es lo único cuya forma cruza la región entera.
///
/// Un corredor con una sola bahía sigue siendo una línea recta con un bulto; con dos o tres, deja de
/// tener una anchura y pasa a tener un recorrido. Tres es además el techo real: la banda más sus
/// bahías tienen que caber en las cuatro partes de [`MAX_PARTS`], y esta pasada no las amplía.
fn deform_budget(s: &PlannedSpace) -> u8 {
    if s.role.is_circulation() {
        MAX_PARTS as u8 - 1
    } else {
        MAX_DEFORMS
    }
}

/// Longitud de cara acumulada a partir de la cual una coordenada es una LÍNEA de guillotina.
///
/// Media región. Por debajo de eso una coordenada compartida es una coincidencia entre dos salas;
/// por encima es el corte que el árbol dejó, y es lo que se ve desde dentro.
const DEFORM_LINE_CM: i32 = 7500;

/// Cuánto sube la presión sobre una línea de guillotina. Es el sesgo más fuerte de la pasada a
/// propósito: quebrar una línea larga vale por diez mordiscos en paredes sueltas.
const DEFORM_LINE_BONUS: f32 = 0.30;

/// Cuánto se infla lo cedido antes de mirarlo contra el cerco blando de las juntas.
///
/// Es el grosor de la pared nueva más el hueco por el que se pasa: por debajo de esto la pared que
/// nace en el contorno de lo cedido puede caer DENTRO del cerco aunque lo cedido no lo pise.
///
/// **Cuatro metros y no dos**: con dos, y la pasada apuntando ya al MEDIO de cada línea de
/// guillotina en vez de a un sitio sorteado, volvió a salir una puerta de junta sellada en 1 de 270
/// regiones del barrido —`(-150.0, 28.4)`, con la puerta intacta—. La pared nueva no es una línea
/// sin grosor: arrastra su tramo y su jamba.
const KEEP_SOFT_MARGIN_CM: i32 = 400;

/// ¿Puede este espacio participar en la deformación, en cualquiera de los dos papeles?
///
/// El vacío no se construye; un espacio hundido tiene los peldaños calculados sobre su rectángulo; y
/// lo que toca un pozo de escalera se queda como está, que es lo que esta pasada vino a dejar de
/// romper.
fn may_deform(s: &PlannedSpace, keep_hard: &[PlanRect]) -> bool {
    s.role.is_built()
        && s.role != SpaceRole::Stair
        // **Ni la planta abierta.** Un mordisco le quita justo lo que se acaba de fundir: medido, la
        // envolvente caía de 380 a 196 m² y las filas de puestos, que se reparten sobre el rectángulo
        // interior, quedaban colgando fuera de la huella. Una sala menos deformada no es un precio.
        && !s.open_plan
        // Ni la zona laberinto (ADR-155): sus huecos múltiples se reparten sobre la pared recta.
        && !s.maze
        && s.rise_cm == 0
        && !keep_hard.iter().any(|k| s.hits_rect(k))
}

/// ¿Puede CEDER volumen? Una banda no.
///
/// **Y ésta es la única asimetría de la pasada, con motivo medido.** Lo que hace que la región se
/// lea como una cuadrícula no es el mordisco que falta en una oficina: es la retícula de bandas, que
/// cruza la región de lado a lado y cuyas paredes eran intocables porque `may_deform` excluía la
/// circulación entera. Con la exclusión en los dos papeles, la deformación sólo podía reordenar el
/// relleno de cada casilla del gofre y jamás la casilla.
///
/// Recibir sí puede: una banda que se ensancha localmente en una bahía sólo GANA suelo —la
/// conectividad no puede empeorar— y el quiebro cae justo en la línea recta que había que romper.
/// Ceder no, porque un mordisco en una banda sí puede estrangularla o partirla, y eso es la
/// conectividad de la región.
fn may_donate(s: &PlannedSpace) -> bool {
    !s.role.is_circulation()
}

/// Cuántas veces dispara la deformación en esta pareja. **Sale del contexto, no de un dado plano**:
/// dónde está, cuánto miden, cómo de larga es la pared que comparten y qué grano tiene esa parte del
/// árbol.
fn deform_pressure(a: &PlannedSpace, b: &PlannedSpace, wall_cm: i32, line_cm: i32) -> f32 {
    let mut p = DEFORM_BASE;
    // La línea de guillotina manda sobre todo lo demás: ver `DEFORM_LINE_BONUS`.
    if line_cm >= DEFORM_LINE_CM {
        p += DEFORM_LINE_BONUS;
    }
    // Zona rara: es donde ADR-110 D2 dice que la rareza tiene que subir, y una huella irregular es
    // rareza de la barata.
    if a.scale == scale::SCALE_WEIRD || b.scale == scale::SCALE_WEIRD {
        p += DEFORM_WEIRD;
    }
    // Una pared larga es donde un quiebro se LEE. En una pared de tres metros no se ve nada.
    if wall_cm >= DEFORM_LONG_WALL_CM {
        p += DEFORM_LONG_BONUS;
    } else if wall_cm < DEFORM_SHORT_WALL_CM {
        p -= DEFORM_SHORT_MALUS;
    }
    // Grano fino al fondo del árbol: si ahí se deforma igual, el mundo se llena de mordisquitos en
    // salas de cuarenta metros cuadrados y la forma deja de significar nada.
    if a.depth.max(b.depth) >= CORRIDOR_DEPTH + 3 {
        p -= DEFORM_DEEP_MALUS;
    }
    // Una nave deformada se ve desde dentro; un trastero deformado no lo ve nadie.
    if a.role == SpaceRole::Hall || b.role == SpaceRole::Hall {
        p += DEFORM_HALL_BONUS;
    }
    p.clamp(0.10, 0.92)
}

/// Sortea el mordisco concreto y devuelve `(lo que le queda al donante, lo que se cede)`.
///
/// Uno o dos peldaños. Con uno salen la L y la U; con dos, la Z y la escalonada — que es la única
/// forma de esta tanda que no se puede describir con «un rectángulo al que le falta un trozo».
/// Lo que un mordisco tiene que esquivar y a qué se puede pegar. Va junto porque son cuatro listas
/// que sólo tienen sentido a la vez, y sueltas convertían la firma en un parámetro por invariante.
struct BiteLimits<'a> {
    /// Puertas de TERCEROS del donante: `(x, z, anchura)`. La que une a los dos no está aquí, porque
    /// ésa se recoloca.
    doors: &'a [(i32, i32, i32)],
    /// Caras que los dos espacios ya tienen en el eje que el mordisco recorre.
    snap_lines: &'a [i32],
    /// Y las del eje perpendicular, al que se pega el FONDO por la misma razón.
    perp_lines: &'a [i32],
    gate_cuts_x: &'a [GateCut],
    gate_cuts_z: &'a [GateCut],
    /// Dónde hay que CORTAR la línea de guillotina en la que se apoya esta pared, si es larga.
    ///
    /// Es el punto medio del TRAMO de pared recta, no el de la pared de esta pareja. Un mordisco en
    /// el extremo de una línea de 150 m deja 125 m de recta y no se nota; el mismo mordisco en el
    /// medio la parte en dos de 62 y **deja de existir** como línea larga —el umbral es media
    /// región—. Es la diferencia entre ponerle un diente a la cuadrícula y quitarla.
    cut_at: Option<i32>,
}

/// Por dónde muere un mordisco dentro de [`plan_bite`]. Sólo para `WG3_SHAPE_DEBUG`.
#[derive(Default)]
struct BiteMisses {
    shallow: usize,
    no_depth: usize,
    no_gap: usize,
    short_run: usize,
    gate: usize,
    greedy: usize,
}

fn plan_bite(
    donor: &PlanRect,
    recip: &PlanRect,
    side: u8,
    wall_cm: i32,
    st: &mut hash::Stream,
    lim: &BiteLimits,
    miss: &mut BiteMisses,
) -> Option<(Vec<PlanRect>, Vec<PlanRect>)> {
    let BiteLimits {
        doors,
        snap_lines,
        perp_lines,
        gate_cuts_x,
        gate_cuts_z,
        cut_at,
    } = *lim;
    let perp_is_x = side % 2 == 1;
    let from_high = side == 0 || side == 1;
    let (p_lo, p_hi) = if perp_is_x {
        (donor.min_x_cm, donor.max_x_cm)
    } else {
        (donor.min_z_cm, donor.max_z_cm)
    };
    let (a_lo, a_hi) = if perp_is_x {
        (donor.min_z_cm, donor.max_z_cm)
    } else {
        (donor.min_x_cm, donor.max_x_cm)
    };
    let (o_lo, o_hi) = if perp_is_x {
        (
            donor.min_z_cm.max(recip.min_z_cm),
            donor.max_z_cm.min(recip.max_z_cm),
        )
    } else {
        (
            donor.min_x_cm.max(recip.min_x_cm),
            donor.max_x_cm.min(recip.max_x_cm),
        )
    };

    let perp_span = p_hi - p_lo;
    if donor.area_m2() < BITE_DONOR_MIN_M2 {
        miss.shallow += 1;
        return None;
    }
    // **El fondo mínimo es RELATIVO a la sala, con un suelo absoluto.**
    //
    // Pedir 3,5 m a todo el mundo mataba 56 de 81 intentos por «poco fondo»: son las salas de menos
    // de 7,5 m de fondo, que son legión. Y pedirlos era además la pregunta equivocada — «visualmente
    // evidente» no es una medida en metros, es una fracción de la sala: 2,5 m de un cuarto de seis es
    // un mordisco enorme, y de una nave de veinte no se ve. El suelo absoluto existe para que por
    // debajo de él siga sin haber nada, porque eso ya no es una forma, es un chaflán.
    let d_min = BITE_MIN_CM.max((perp_span as f32 * BITE_MIN_FRACTION) as i32);
    let d_max = BITE_MAX_CM
        .min((perp_span as f32 * BITE_MAX_FRACTION) as i32)
        .min(perp_span - BITE_KEEP_MIN_CM);
    if d_max < d_min {
        miss.no_depth += 1;
        return None;
    }

    // El tramo a lo largo de la pared. Se sortea la longitud y luego dónde empieza dentro del solape;
    // si un extremo queda a menos de un trozo utilizable de la esquina del donante, se pega a ella —
    // y así el mordisco de esquina (la L limpia) sale más veces que el central.
    // **EL TRAMO SE ELIGE DONDE NO HAY PUERTAS, no a ciegas.**
    //
    // El mordisco se lleva un pedazo de la pared que el donante comparte con el receptor, y en esa
    // pared está precisamente la puerta que los une. Sorteando el tramo sin mirar, dos de cada tres
    // mordiscos se comían una puerta y había que descartarlos: medido, 60 descartes por puerta contra
    // 6 mordiscos puestos. Mirando primero dónde están, el sitio bueno se encuentra a la primera.
    //
    // Se prohíbe el tramo de cada puerta de la pared mordida, más los extremos donde haya puerta en
    // una pared perpendicular a la que el mordisco pueda llegar. De lo que queda se toma el hueco MÁS
    // ANCHO, que es función de la geometría y no del orden de recorrido.
    let mut blocked: Vec<(i32, i32)> = Vec::new();
    // Las puertas de las paredes de los EXTREMOS no prohíben el tramo: limitan el FONDO.
    //
    // Prohibirlas era lo cómodo y costaba justo la forma que se quiere: bloqueaban las dos esquinas,
    // así que el único hueco libre quedaba en medio y el mordisco salía en U —tres partes, y desde
    // dentro se lee peor— cuando lo que pedía la pared era una L de esquina limpia. Ahora se apunta a
    // qué profundidad está cada una y se recorta el fondo lo justo para no llegar a ella.
    let mut depth_cap: Vec<(i32, i32, i32)> = Vec::new();
    for &(dx, dz, dw) in doors {
        let (perp, along) = if perp_is_x { (dx, dz) } else { (dz, dx) };
        let reach = dw / 2 + DOOR_JAMB_CM;
        let face = if from_high { p_hi } else { p_lo };
        let depth_of = if from_high { p_hi - perp } else { perp - p_lo };
        let on_end = (along - a_lo).abs() <= 2 || (along - a_hi).abs() <= 2;
        if (perp - face).abs() <= 2 || (on_end && depth_of <= BITE_MAX_CM + reach) {
            blocked.push((along - reach, along + reach));
        } else {
            depth_cap.push((along - reach, along + reach, depth_of - reach));
        }
    }
    blocked.sort_unstable();
    let mut gaps: Vec<(i32, i32)> = Vec::new();
    let mut cursor = o_lo;
    for (b0, b1) in blocked {
        if b0 > cursor {
            gaps.push((cursor, b0.min(o_hi)));
        }
        cursor = cursor.max(b1);
    }
    if cursor < o_hi {
        gaps.push((cursor, o_hi));
    }
    let Some(gap) = gaps
        .into_iter()
        .filter(|(a, b)| b > a)
        .max_by_key(|(a, b)| b - a)
    else {
        miss.no_gap += 1;
        return None;
    };

    let run_max = ((wall_cm as f32 * BITE_RUN_MAX_FRACTION) as i32).min(gap.1 - gap.0);
    if run_max < BITE_RUN_MIN_CM {
        miss.short_run += 1;
        return None;
    }
    // **La tirada se sesga a LARGA, y ésta es la que decide si el trabajo se ve.**
    //
    // Lo que hace que un plano se lea como cuadrícula no es que cada sala sea un rectángulo: es que
    // el corte de guillotina deja una LÍNEA recta que cruza media región, compartida por todas las
    // hojas de ese subárbol. Un mordisco corto le pone un diente a esa línea y la línea sigue ahí;
    // uno que se lleva casi toda la pared común la QUIEBRA. Con la tirada uniforme el volcado seguía
    // pareciendo una rejilla con las esquinas mordidas, que es exactamente lo que se pidió no hacer.
    let long_roll = st.next01().max(st.next01());
    let run = BITE_RUN_MIN_CM + (long_roll * (run_max - BITE_RUN_MIN_CM) as f32) as i32;
    let slack = (gap.1 - gap.0) - run;
    // Sobre una línea de guillotina, tan cerca del corte como el hueco deje: ver
    // `BiteLimits::cut_at`. Si el corte cae fuera del hueco, el tramo se pega al extremo que más se
    // le acerca, que es lo más que esta pareja puede hacer por esa línea.
    let mut c0 = match cut_at {
        Some(cut) => (cut - run / 2).clamp(gap.0, gap.0 + slack),
        None => gap.0 + (st.next01() * slack as f32) as i32,
    };
    let mut c1 = c0 + run;
    // **Los extremos se pegan a las caras que ya existen, las del donante Y las del receptor.**
    //
    // Un extremo a 150 cm de una cara deja una celda de rejilla de 150, por debajo de lo que el
    // ráster deja pasar, y el mordisco se descarta entero más adelante: 38 de 141 intentos se caían
    // así. Pegándolo, esa celda desaparece en vez de nacer estrecha. Las caras del receptor cuentan
    // igual que las del donante porque el trozo cedido pasa a ser suyo.
    for &edge in snap_lines {
        if edge < gap.0 || edge > gap.1 {
            continue;
        }
        if (c0 - edge).abs() < BITE_END_SNAP_CM {
            c0 = edge;
        }
        if (c1 - edge).abs() < BITE_END_SNAP_CM {
            c1 = edge;
        }
    }
    if c1 - c0 < BITE_RUN_MIN_CM || c1 - c0 > a_hi - a_lo {
        miss.short_run += 1;
        return None;
    }

    // Uno o dos peldaños. Dos exigen tramo para los dos y una diferencia de fondo que se note: por
    // debajo de dos metros la escalera se lee como un borde mal cortado.
    let two = st.next01() < BITE_TWO_STEPS && (c1 - c0) >= 2 * BITE_STEP_RUN_MIN_CM;
    // Sesgado a HONDO -el maximo de dos tiradas-: un mordisco de tres metros y medio existe pero
    // es el suelo, no la media. Lo que se pidio es que la forma se vea, no que se pueda demostrar.
    let deep_roll = st.next01().max(st.next01());
    // **Y el fondo se pega a una cara perpendicular que ya exista**, por lo mismo que la tirada se
    // pega a las suyas: una cara nueva a 80 cm de una vieja deja una celda de rejilla de 80, que el
    // ráster tapia. Medido: 51 de 143 intentos se caían ahí, y son los de las salas que ya tenían un
    // bulto — o sea justo las que estaban a punto de convertirse en una forma de verdad.
    let snap_depth = |d: i32| -> i32 {
        let line = if from_high { p_hi - d } else { p_lo + d };
        let best = perp_lines
            .iter()
            .filter(|&&v| v > p_lo && v < p_hi && (v - line).abs() < BITE_END_SNAP_CM)
            .min_by_key(|&&v| (v - line).abs());
        match best {
            Some(&v) => (if from_high { p_hi - v } else { v - p_lo }).clamp(d_min, d_max),
            None => d,
        }
    };
    let d1 = snap_depth(d_min + (deep_roll * (d_max - d_min) as f32) as i32);
    let mut steps: Vec<(i32, i32, i32)> = Vec::new();
    if two {
        let cm = c0
            + BITE_STEP_RUN_MIN_CM
            + (st.next01() * ((c1 - c0) - 2 * BITE_STEP_RUN_MIN_CM) as f32) as i32;
        let deep = snap_depth((d1 + BITE_STEP_DELTA_CM).min(d_max));
        let shallow = snap_depth((d1 - BITE_STEP_DELTA_CM).max(d_min));
        let (da, db) = if st.next01() < 0.5 {
            (deep, shallow)
        } else {
            (shallow, deep)
        };
        if da == db {
            miss.no_depth += 1;
            return None;
        }
        steps.push((c0, cm, da));
        steps.push((cm, c1, db));
    } else {
        steps.push((c0, c1, d1));
    }

    // **Ninguna pared nueva puede caer sobre una puerta de junta.** Media puerta es un muro, y la
    // región nace sellada contra su vecina mientras aquélla abre la suya contra ella.
    let (perp_cuts, along_cuts) = if perp_is_x {
        (gate_cuts_x, gate_cuts_z)
    } else {
        (gate_cuts_z, gate_cuts_x)
    };
    for &(z0, z1, d) in &steps {
        let line = if from_high { p_hi - d } else { p_lo + d };
        if !clears_gate_cuts(line, (z0, z1), perp_cuts) {
            miss.gate += 1;
            return None;
        }
    }
    // Una cara del tramo va del fondo a la pared: su extensión es todo el mordisco.
    let deep = steps.iter().map(|&(_, _, d)| d).max().unwrap_or(0);
    let (w_lo, w_hi) = if from_high {
        (p_hi - deep, p_hi)
    } else {
        (p_lo, p_lo + deep)
    };
    for line in [c0, c1] {
        if line != a_lo && line != a_hi && !clears_gate_cuts(line, (w_lo, w_hi), along_cuts) {
            miss.gate += 1;
            return None;
        }
    }
    if steps.len() == 2 && !clears_gate_cuts(steps[0].1, (w_lo, w_hi), along_cuts) {
        miss.gate += 1;
        return None;
    }

    let mk = |lo_p: i32, hi_p: i32, lo_a: i32, hi_a: i32| -> PlanRect {
        if perp_is_x {
            PlanRect {
                min_x_cm: lo_p,
                max_x_cm: hi_p,
                min_z_cm: lo_a,
                max_z_cm: hi_a,
            }
        } else {
            PlanRect {
                min_x_cm: lo_a,
                max_x_cm: hi_a,
                min_z_cm: lo_p,
                max_z_cm: hi_p,
            }
        }
    };

    let mut kept: Vec<PlanRect> = Vec::new();
    let mut ceded: Vec<PlanRect> = Vec::new();
    if c0 > a_lo {
        kept.push(mk(p_lo, p_hi, a_lo, c0));
    }
    for &(z0, z1, d) in &steps {
        let (keep_lo, keep_hi, cede_lo, cede_hi) = if from_high {
            (p_lo, p_hi - d, p_hi - d, p_hi)
        } else {
            (p_lo + d, p_hi, p_lo, p_lo + d)
        };
        kept.push(mk(keep_lo, keep_hi, z0, z1));
        ceded.push(mk(cede_lo, cede_hi, z0, z1));
    }
    if c1 < a_hi {
        kept.push(mk(p_lo, p_hi, c1, a_hi));
    }

    // Al donante le tiene que quedar SALA. Un espacio que cede la mitad de lo suyo no se ha
    // deformado: se ha mudado, y su papel deja de significar lo que dice.
    let kept_area: f32 = kept.iter().map(|r| r.area_m2()).sum();
    if kept_area < donor.area_m2() * BITE_DONOR_KEEPS {
        miss.greedy += 1;
        return None;
    }
    Some((kept, ceded))
}

/// ¿Cabe cada puerta ENTERA dentro de la celda de rejilla que le toca?
///
/// Es la comprobación que sustituye a pedirle a toda cara de parte que fuera tan ancha como el vano
/// MÁS ANCHO que el plan sabe pedir. Aquélla era correcta y carísima: dejaba la dosis de deformación
/// en el 2,2 % porque exigía cinco metros donde casi siempre bastan dos y medio. Ahora que la pasada
/// corre con las puertas ya repartidas, se puede mirar la anchura de CADA una.
///
/// Una cara de parte no se mueve (ADR-120 D4), así que una puerta a caballo de ella no la aloja
/// ninguna de las dos celdas y se pierde — una sala sellada con la puerta dibujada en el plano.
fn doors_fit_cells(parts: &[PlanRect], doors: &[(i32, i32, i32)]) -> bool {
    const EPS: i32 = 2;
    let faces = |along_x: bool| -> Vec<i32> {
        let mut v: Vec<i32> = parts
            .iter()
            .flat_map(|p| {
                if along_x {
                    [p.min_x_cm, p.max_x_cm]
                } else {
                    [p.min_z_cm, p.max_z_cm]
                }
            })
            .collect();
        v.sort_unstable();
        v.dedup();
        v
    };
    let (xs, zs) = (faces(true), faces(false));
    for &(x, z, w) in doors {
        // Sobre una cara vertical, la puerta corre a lo largo de Z; sobre una horizontal, de X.
        let on_vertical = parts
            .iter()
            .any(|p| (p.min_x_cm - x).abs() <= EPS || (p.max_x_cm - x).abs() <= EPS);
        let (lines, at) = if on_vertical { (&zs, z) } else { (&xs, x) };
        let half = w / 2;
        let Some(k) = lines.windows(2).position(|c| at >= c[0] && at <= c[1]) else {
            continue;
        };
        if at - half < lines[k] || at + half > lines[k + 1] {
            return false;
        }
    }
    true
}

/// ¿Se aparta este corte de todas las puertas de junta de su eje?
///
/// Mismo umbral que usa el reparto ([`GATE_CLEARANCE_CM`]); aquí no hay adónde correr el corte, así
/// que la respuesta es sí o no y un no descarta el mordisco entero.
fn clears_gate_cuts(at: i32, seg: (i32, i32), blocked: &[GateCut]) -> bool {
    blocked
        .iter()
        .all(|&(g, lo, hi)| (at - g).abs() >= GATE_CLEARANCE_CM || seg.1 <= lo || seg.0 >= hi)
}

/// ¿Por qué lado de `a` está `b`? `None` si no se tocan por ninguno.
///
/// `0 = N (+Z)`, `1 = E (+X)`, `2 = S (−Z)`, `3 = O (−X)`, la misma numeración que usa todo lo demás.
fn side_towards(a: &PlanRect, b: &PlanRect) -> Option<u8> {
    const TOUCH_CM: i32 = 1;
    if (a.max_x_cm - b.min_x_cm).abs() <= TOUCH_CM {
        return Some(1);
    }
    if (b.max_x_cm - a.min_x_cm).abs() <= TOUCH_CM {
        return Some(3);
    }
    if (a.max_z_cm - b.min_z_cm).abs() <= TOUCH_CM {
        return Some(0);
    }
    if (b.max_z_cm - a.min_z_cm).abs() <= TOUCH_CM {
        return Some(2);
    }
    None
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

/// Dónde cae la puerta a lo largo de la pared que comparten `a` y `b`: en el centro con
/// [`DOOR_CENTRED_CHANCE`], y si no en cualquier punto del solape que deje medio vano más jamba a
/// cada lado. `(w, x, z)` es lo que devolvió [`rects_share_wall`]: el solape y su centro.
///
/// Sorteado por la POSICIÓN del centro de la pared (R3), así que la misma pared da la misma
/// puerta en cualquier proceso y en cualquier orden de recorrido.
fn door_along_wall(seed: i32, a: PlanRect, b: PlanRect, w: i32, x: i32, z: i32) -> (i32, i32) {
    let margin = DOORWAY_CM / 2 + DOOR_JAMB_CM;
    if w < 2 * margin + 1 {
        return (x, z);
    }
    let mut st = hash::stream_at(seed, x as f32 / CM_PER_M, z as f32 / CM_PER_M, SALT_DOOR);
    if st.next01() < DOOR_CENTRED_CHANCE {
        return (x, z);
    }
    let t = st.next01();
    // ¿Corre la pared en X (los dos se tocan por N/S) o en Z?
    let vertical = (a.max_x_cm - b.min_x_cm).abs() <= 1 || (b.max_x_cm - a.min_x_cm).abs() <= 1;
    if vertical {
        let lo = a.min_z_cm.max(b.min_z_cm) + margin;
        let hi = a.max_z_cm.min(b.max_z_cm) - margin;
        (x, lo + ((hi - lo) as f32 * t) as i32)
    } else {
        let lo = a.min_x_cm.max(b.min_x_cm) + margin;
        let hi = a.max_x_cm.min(b.max_x_cm) - margin;
        (lo + ((hi - lo) as f32 * t) as i32, z)
    }
}

/// ADR-155 D2 / enm. 2 — los HUECOS de la zona laberinto en la pared que comparten `a` y `b`, como
/// `(ancho, x, z)`: anchos de [`MAZE_GAP_WIDTHS_CM`] con un trozo de pared de [`MAZE_GAP_PIECE_CM`] como
/// mínimo en cada esquina y entre dos huecos. Una pared que no da para eso lleva un único vano normal
/// centrado, y una que ni para eso, ninguno. Determinista por la posición de la pared (R3).
fn gaps_along_wall(seed: i32, a: PlanRect, b: PlanRect) -> Vec<(i32, i32, i32)> {
    let Some((len, cx, cz)) = rects_share_wall(a, b) else {
        return Vec::new();
    };
    let vertical = (a.max_x_cm - b.min_x_cm).abs() <= 1 || (b.max_x_cm - a.min_x_cm).abs() <= 1;
    let (lo, hi) = if vertical {
        (a.min_z_cm.max(b.min_z_cm), a.max_z_cm.min(b.max_z_cm))
    } else {
        (a.min_x_cm.max(b.min_x_cm), a.max_x_cm.min(b.max_x_cm))
    };
    let at = |c: i32| if vertical { (cx, c) } else { (c, cz) };
    let narrow = MAZE_GAP_WIDTHS_CM[0];
    if len < narrow + 2 * MAZE_GAP_PIECE_CM {
        // Un vano del plan no baja de `DOORWAY_CM` (`RegionPlan::problems`): o cabe entero con
        // jamba, o esta pared no lleva hueco y la pareja la cosen las pasadas de siempre.
        if len < DOORWAY_CM + 2 * DOOR_JAMB_CM {
            return Vec::new();
        }
        let (x, z) = at((lo + hi) / 2);
        return vec![(DOORWAY_CM, x, z)];
    }
    let mut st = hash::stream_at(seed, cx as f32 / CM_PER_M, cz as f32 / CM_PER_M, SALT_GAP);
    let mut out = Vec::new();
    let mut cursor = lo + MAZE_GAP_PIECE_CM + (st.next01() * MAZE_GAP_PIECE_CM as f32) as i32;
    while cursor + narrow + MAZE_GAP_PIECE_CM <= hi {
        let k = (st.next01() * MAZE_GAP_WIDTHS_CM.len() as f32) as usize;
        let w = MAZE_GAP_WIDTHS_CM[k.min(MAZE_GAP_WIDTHS_CM.len() - 1)]
            .min(hi - MAZE_GAP_PIECE_CM - cursor);
        // Ancho par a la decena: el centro cae en centímetro entero y las dos jambas miden lo mismo.
        let w = w - w % 20;
        let (x, z) = at(cursor + w / 2);
        out.push((w, x, z));
        cursor += w + MAZE_GAP_PIECE_CM + (st.next01() * MAZE_GAP_PIECE_EXTRA_CM as f32) as i32;
    }
    // El primer trozo sorteado puede comerse el sitio del único hueco: entonces, uno centrado.
    if out.is_empty() {
        let (x, z) = at((lo + hi) / 2);
        out.push((narrow, x, z));
    }
    out
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

/// Los techos por espacio: que sean deterministas, que respeten el rango y que no salgan todos
/// iguales.
#[cfg(test)]
mod ceiling_tests {
    use super::*;

    /// Una región entera de cuatro plantas, sin puertas de junta: lo que se mide aquí es el reparto
    /// de alturas, y una junta no lo cambia.
    fn building(seed: i32, variety: f32) -> RegionBuilding {
        plan_building_with(seed, (0.0, 0.0, 150.0, 150.0), &[], 4, variety)
    }

    /// Todas las alturas pedidas de un edificio, en orden de planta y de espacio.
    fn ceilings(b: &RegionBuilding) -> Vec<i32> {
        b.storeys
            .iter()
            .flat_map(|p| p.spaces.iter().map(|s| s.ceiling_clear_cm))
            .collect()
    }

    #[test]
    fn the_same_seed_always_asks_for_the_same_ceilings() {
        for seed in [1, 7, 42, 1009] {
            assert_eq!(
                ceilings(&building(seed, CEILING_VARIETY)),
                ceilings(&building(seed, CEILING_VARIETY)),
                "la semilla {seed} pidió dos juegos de techos distintos"
            );
        }
    }

    #[test]
    fn the_knob_at_zero_gives_back_the_world_of_before() {
        for seed in [1, 7, 42, 1009] {
            let b = building(seed, 0.0);
            assert!(
                ceilings(&b).iter().all(|&h| h == 0),
                "con la perilla apagada algún espacio de la semilla {seed} sigue pidiendo techo"
            );
        }
    }

    #[test]
    fn every_ceiling_asked_for_is_inside_its_range() {
        for seed in 1..24 {
            let b = building(seed, CEILING_VARIETY);
            for (n, storey) in b.storeys.iter().enumerate() {
                for s in &storey.spaces {
                    let h = s.ceiling_clear_cm;
                    if h == 0 {
                        continue;
                    }
                    assert!(
                        s.role.is_built() && s.role != SpaceRole::Stair,
                        "un {} de la planta {n} (semilla {seed}) pidió techo: {h} cm",
                        s.role.name()
                    );
                    let normal = (CEILING_MIN_CM..=CEILING_MAX_CM).contains(&h);
                    let tall = (CEILING_TALL_MIN_CM..=CEILING_TALL_MAX_CM).contains(&h);
                    // ADR-105 enm. 14 — el carácter de la zona puede TOPAR el techo por debajo del
                    // rango normal (2,40 en laberinto, 2,00 en lo raro), nunca por debajo de 2,00.
                    let cap = crate::world::wg3::fill::ceiling_cap_cm(seed, s);
                    assert!(
                        normal || tall || (cap > 0 && h >= 200 && h <= cap),
                        "techo de {h} cm fuera de los dos rangos (semilla {seed}, planta {n}, tope {cap})"
                    );
                    // La doble altura es de las salas GRANDES, y esa es la mitad de la regla: sin
                    // esto el rango alto se colaría en un trastero de doce metros.
                    assert!(
                        !tall || s.area_m2() > CEILING_TALL_AREA_M2,
                        "un espacio de {:.0} m² pidió {h} cm (semilla {seed}, planta {n})",
                        s.area_m2()
                    );
                }
            }
        }
    }

    /// **Ni plano ni uniforme**, que son los dos fallos que dejan verde un histograma roto: uno
    /// devuelve siempre el mismo número, el otro reparte por igual y hace que «alto» no signifique
    /// nada.
    #[test]
    fn the_spread_of_ceilings_is_neither_flat_nor_uniform() {
        let mut all: Vec<i32> = Vec::new();
        for seed in 1..40 {
            all.extend(
                ceilings(&building(seed, CEILING_VARIETY))
                    .into_iter()
                    .filter(|&h| h > 0),
            );
        }
        assert!(all.len() > 200, "muestra corta: {} techos", all.len());

        let mut distinct: Vec<i32> = all.clone();
        distinct.sort_unstable();
        distinct.dedup();
        assert!(
            distinct.len() >= 6,
            "sólo {} alturas distintas en {} espacios: {distinct:?}",
            distinct.len(),
            all.len()
        );

        let top = distinct
            .iter()
            .map(|&h| all.iter().filter(|&&x| x == h).count())
            .max()
            .unwrap_or(0);
        assert!(
            top * 2 < all.len(),
            "una sola altura se lleva {top} de {} espacios",
            all.len()
        );

        // Y el sesgo, que es lo que se pidió: la mitad baja del rango normal pesa más que la alta.
        let mid = (CEILING_MIN_CM + CEILING_MAX_CM) / 2;
        let low = all
            .iter()
            .filter(|&&h| h <= mid && h <= CEILING_MAX_CM)
            .count();
        let high = all
            .iter()
            .filter(|&&h| h > mid && h <= CEILING_MAX_CM)
            .count();
        assert!(
            low > high,
            "el reparto no está sesgado hacia lo bajo: {low} por debajo de {mid} cm contra {high} por encima"
        );

        // Y la doble altura es RARA. Si fuera la norma dejaría de leerse como excepción.
        let tall = all.iter().filter(|&&h| h >= CEILING_TALL_MIN_CM).count();
        assert!(
            tall * 10 < all.len(),
            "la doble altura se lleva {tall} de {} espacios",
            all.len()
        );
    }
}

#[cfg(test)]
mod misalign_tests {
    use super::*;

    const BOUNDS: (f32, f32, f32, f32) = (0.0, 0.0, 150.0, 150.0);

    /// Pares de vanos de un mismo espacio sobre paredes paralelas distintas y en el mismo eje: lo
    /// que la pasada existe para quitar.
    fn aligned_pairs(plan: &RegionPlan) -> usize {
        let mut n = 0;
        for (s, space) in plan.spaces.iter().enumerate() {
            let mut mine: Vec<(bool, i32, i32)> = Vec::new();
            for l in &plan.links {
                if l.kind == LinkKind::Route || (l.a != s && l.b != s) {
                    continue;
                }
                let half = l.width_cm / 2 + DOOR_JAMB_CM;
                let Some(side) = space.wall_side_of(l.at_x_cm, l.at_z_cm, half) else {
                    continue;
                };
                let v = side % 2 == 1;
                mine.push(if v {
                    (v, l.at_x_cm, l.at_z_cm)
                } else {
                    (v, l.at_z_cm, l.at_x_cm)
                });
            }
            for i in 0..mine.len() {
                for j in (i + 1)..mine.len() {
                    let (vi, wi, ci) = mine[i];
                    let (vj, wj, cj) = mine[j];
                    if vi == vj && wi != wj && (ci - cj).abs() < DOOR_ALIGN_TOL_CM {
                        n += 1;
                    }
                }
            }
        }
        n
    }

    #[test]
    fn misalignment_moves_doors_off_axis_without_touching_the_graph() {
        let mut aligned_off = 0;
        let mut aligned_on = 0;
        let mut moved = 0;
        for seed in 1..=12 {
            let off = plan_storey_with(seed, BOUNDS, &[], 0, true, &[], CEILING_VARIETY, 0.0);
            let on = plan_storey_with(seed, BOUNDS, &[], 0, true, &[], CEILING_VARIETY, 1.0);
            assert_eq!(
                off.spaces.len(),
                on.spaces.len(),
                "semilla {seed}: la partición cambió"
            );
            assert_eq!(
                off.links.len(),
                on.links.len(),
                "semilla {seed}: el grafo cambió"
            );
            for (a, b) in off.links.iter().zip(&on.links) {
                assert_eq!(
                    (a.a, a.b, a.kind, a.width_cm),
                    (b.a, b.b, b.kind, b.width_cm)
                );
                if (a.at_x_cm, a.at_z_cm) != (b.at_x_cm, b.at_z_cm) {
                    moved += 1;
                }
            }
            assert!(
                on.problems().is_empty(),
                "semilla {seed}: {:?}",
                on.problems()
            );
            aligned_off += aligned_pairs(&off);
            aligned_on += aligned_pairs(&on);
        }
        assert!(moved > 0, "la pasada no movió ni un vano");
        assert!(
            aligned_on < aligned_off,
            "alineados: {aligned_on} con la pasada, {aligned_off} sin ella"
        );
    }

    #[test]
    fn misalignment_is_deterministic() {
        for seed in [3, 77, 1234] {
            let a = plan_storey_with(seed, BOUNDS, &[], 0, true, &[], CEILING_VARIETY, 1.0);
            let b = plan_storey_with(seed, BOUNDS, &[], 0, true, &[], CEILING_VARIETY, 1.0);
            assert_eq!(a.links, b.links, "semilla {seed}");
        }
    }
}

/// ADR-155 L1d — los huecos de la zona laberinto: trozo de pared ≥ `MAZE_GAP_PIECE_CM` en cada
/// esquina y entre dos huecos, ninguno se pisa, todos en la pared común y deterministas.
#[cfg(test)]
mod maze_gap_tests {
    use super::*;

    fn rect(x0: i32, z0: i32, x1: i32, z1: i32) -> PlanRect {
        PlanRect {
            min_x_cm: x0,
            min_z_cm: z0,
            max_x_cm: x1,
            max_z_cm: z1,
        }
    }

    #[test]
    fn maze_gaps_leave_wall_pieces_and_never_overlap() {
        let mut walls_with_many = 0usize;
        for seed in 0..200 {
            for len in [600, 900, 1500, 2500] {
                // Pared vertical en x = 1000 entre dos hojas, desplazada por la semilla.
                let z0 = seed * 37;
                let a = rect(0, z0, 1000, z0 + len);
                let b = rect(1000, z0, 2000, z0 + len);
                let gaps = gaps_along_wall(seed, a, b);
                assert!(!gaps.is_empty(), "pared de {len}: sin huecos");
                assert_eq!(gaps, gaps_along_wall(seed, a, b), "no determinista");
                let mut spans: Vec<(i32, i32)> = gaps
                    .iter()
                    .map(|&(w, x, z)| {
                        assert_eq!(x, 1000, "hueco fuera de la pared");
                        // El último hueco de la pared puede recortarse al sitio que queda.
                        assert!(
                            (MAZE_GAP_WIDTHS_CM[0]..=MAZE_GAP_WIDTHS_CM[4]).contains(&w)
                                && w % 20 == 0,
                            "ancho {w}"
                        );
                        (z - w / 2, z + w / 2)
                    })
                    .collect();
                spans.sort_unstable();
                let mut cursor = z0;
                for (lo, hi) in &spans {
                    assert!(
                        lo - cursor >= MAZE_GAP_PIECE_CM,
                        "semilla {seed} pared {len}: trozo de {} cm antes de {lo}..{hi}",
                        lo - cursor
                    );
                    cursor = *hi;
                }
                assert!(
                    z0 + len - cursor >= MAZE_GAP_PIECE_CM,
                    "semilla {seed} pared {len}: esquina final de {} cm",
                    z0 + len - cursor
                );
                walls_with_many += (gaps.len() >= 2) as usize;
            }
        }
        assert!(
            walls_with_many > 200,
            "casi ninguna pared con varios huecos: {walls_with_many}"
        );
    }

    #[test]
    fn a_short_maze_wall_gets_one_centred_doorway_or_none() {
        let a = rect(0, 0, 1000, 450);
        let b = rect(1000, 0, 2000, 450);
        assert_eq!(gaps_along_wall(7, a, b), vec![(DOORWAY_CM, 1000, 225)]);
        let tiny_a = rect(0, 0, 1000, 220);
        let tiny_b = rect(1000, 0, 2000, 220);
        assert!(gaps_along_wall(7, tiny_a, tiny_b).is_empty());
    }
}
