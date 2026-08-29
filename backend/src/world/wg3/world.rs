//! ADR-095 F4 — el mundo que se sirve: componer una vez, repartir por chunk.
//!
//! # ADR-100 — EL MUNDO SERVIDO SALE DEL PLAN, no del compositor
//!
//! [`Wg3ServedWorld::plan_region`] es lo que responde a un chunk desde ADR-100: la región se reparte
//! en espacios con papel y en las conexiones que deben existir (`wg3::plan`), y sólo después se
//! construye (`wg3::fill`). Medido, superficie andable: **21 → 83 %, 26 → 78 %, 3,5 → 80 %,
//! 24 → 75 %**, con la mancha mayor al 100 % de lo pisable en las cuatro.
//!
//! [`Wg3ServedWorld::compose_region`] sigue aquí y sigue siendo correcta, pero **ya no sirve el
//! mundo**: es el compositor por bocas, que el oráculo de composición fija y que las sondas que lo
//! miden a él siguen usando. Leer una cifra suya y hablar del mundo servido es comparar dos cosas
//! distintas.
//!
//! # A3 INTERINO, Y ESTÁ DECIDIDO ASÍ A PROPÓSITO
//!
//! El compositor por bocas es un recorrido finito desde una semilla: sin acotar, **el mundo se
//! acaba**. ADR-095 dejó el modelo de troceado fuera (A1 contrato de frontera, A2 regiones, A3 mundo
//! finito) y ADR-096 cerró A2, así que hoy hay infinitas regiones y ninguna se acaba. El párrafo se
//! conserva porque `compose`/`compose_with` —sin caja— siguen siendo A3.
//!
//! # NO HAY GLOBAL (R3)
//!
//! La composición se cachea en una estructura que el bucle de juego POSEE y pasa por parámetro. Ni
//! `OnceLock` ni `static mut`: el manifiesto de las salas autoradas es un global de proceso y costó
//! una sesión entera de números falsos, porque en cuanto una sonda tocaba el entorno la siguiente
//! medía otro mundo sin enterarse. Además hace falta que sea local: un *joiner* no conoce la
//! semilla hasta el HandshakeAck, así que la composición es PEREZOSA y se rehace si la semilla
//! cambia debajo.
//!
//! # UNA PIEZA, UN CHUNK
//!
//! Una pieza a caballo de dos chunks se manda en UNO SOLO —el que contiene su CENTRO—, no en los
//! dos. El cliente monta un `GameObject` por chunk y no deduplica: mandarla dos veces la dibujaría
//! dos veces, con su colisión duplicada y peleando en el z-buffer. El andamio no tenía este
//! problema porque encajaba cada pieza entera dentro de su chunk, y perder esa propiedad es
//! justamente el precio de que ahora las piezas conecten.
//!
//! El CENTRO y no la esquina mínima porque acota el vuelo: ninguna pieza del catálogo llega a los
//! 50 m del chunk, así que centrada nunca asoma más allá de los vecinos inmediatos de su dueño, y
//! el cliente con radio 1 ve entera cualquier pieza que toque el chunk donde está. Hay un test que
//! lo comprueba sobre el catálogo real, para que añadir una nave de 60 m no lo rompa en silencio.
//!
//! El RÁSTER del servidor no usa este reparto: `chunk::build_chunk_raster` sigue recibiendo todas
//! las colocaciones que TOCAN el chunk, porque una pared que cruza la frontera tiene que bloquear a
//! los dos lados. Repartir es cosa de lo que se dibuja, no de lo que choca.

use super::chunk::{Wg3ChunkCoord, WG3_CHUNK_M};
use super::compose::{self, Wg3ComposerSettings};
use super::fill;
use super::hash;
use super::junction;
use super::manifest::Wg3Manifest;
use super::placement::Wg3Placement;
use super::plan;
use super::route;
use super::segment::{Wg3Carve, Wg3Segment, Wg3Solid};

/// Tope de piezas del mundo interino.
///
/// **Es un techo, no un objetivo, y la diferencia está medida.** Con 300 de tope, seis semillas dan
/// entre 20 y 268 piezas (de 134 m a 921 m de lado, de 0 a 9 ms de composición). El que manda no es
/// este número: es que la frontera se seca sola —hay tapones voluntarios y las candidatas que pisan
/// algo ya colocado se descartan—, así que el árbol termina cuando termina. Subirlo no agranda el
/// mundo; lo agrandará cerrar bucles, que ADR-095 deja abierto.
///
/// Lo que sí acota es el peor caso: el coste sube con el cuadrado, porque cada candidata comprueba
/// solape contra todo lo ya puesto. A 300 el peor caso medido son 9 ms, y se pagan una sola vez al
/// primer chunk que se pide.
///
/// CONSECUENCIA QUE HAY QUE SABER ANTES DE ELEGIR UNA SEMILLA: hay semillas que dan un mundo de dos
/// chunks de lado. No es un fallo del reparto ni del port; es el compositor, y está anotado como
/// límite conocido.
pub const INTERIM_BUDGET: usize = 300;

/// ADR-096 — lado de una región, en chunks. `3 × 50 m = 150 m`.
///
/// **Sale de un barrido, y el primer intento estaba mal.** Se eligió 8 (400 m) mirando la EXTENSIÓN
/// de los mundos sin acotar —de 134 a 921 m— y medirlo lo desmintió: llenados del 1 al 12 %. La
/// extensión no es el dato bueno; un mundo puede medir 900 m y ser cuatro ramas finas. El dato
/// bueno es la superficie CONSTRUIDA. Barrido (`region_size_sweep`, siete regiones cada uno):
///
/// | chunks | lado  | llenado medio | mínimo | piezas |
/// |--------|-------|---------------|--------|--------|
/// | 2      | 100 m | 19 %          | 9 %    | 6–17   |
/// | **3**  | 150 m | **20 %**      | **11 %** | 13–30 |
/// | 4      | 200 m | 14 %          | 5 %    | 15–42  |
/// | 6      | 300 m | 6 %           | 1 %    | 4–60   |
/// | 8      | 400 m | 6 %           | 1 %    | 5–85   |
///
/// Gana 3 en las dos métricas, y la que decide es **el mínimo**: una región al 1 % es un descampado
/// con cuatro edificios sueltos, y con regiones grandes eso le toca a alguien. A 150 m el peor caso
/// sigue teniendo 13 piezas.
///
/// EL PRECIO, dicho: una costura de región cada 150 m. Es frecuente, y hace el contrato de junta más
/// urgente de lo que parecía — pero un mundo con costuras cada 150 m y siempre poblado se anda mejor
/// que uno con costuras cada 400 m del que un tercio está vacío.
///
/// Y el 20 % de llenado sigue siendo poco en absoluto: cuatro quintos de cada región son vacío. Eso
/// NO lo arregla el tamaño, lo arreglará que el compositor deje de ser un árbol. Es el mismo límite
/// que ya se midió en `closing_loops_measures_how_much_more_world_there_is`.
pub const REGION_CHUNKS: i32 = 3;

/// Lado de región en metros.
pub const REGION_M: f32 = REGION_CHUNKS as f32 * WG3_CHUNK_M;

/// Coordenada de región. La región `(0,0)` va de `(0,0)` a `(150,150)` — `REGION_CHUNKS` chunks de
/// `WG3_CHUNK_M`, y NO un número fijo: este comentario decía 400 desde cuando la región eran 8
/// chunks, y un comentario que miente sobre los límites de la región es peor que no tenerlo.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Wg3RegionCoord {
    pub x: i32,
    pub z: i32,
}

impl Wg3RegionCoord {
    /// La región a la que pertenece un chunk. `div_euclid` y no `/`: la división trunca hacia cero,
    /// así que los chunks −1 y +1 caerían en la misma región y todo el hemisferio negativo saldría
    /// espejado. Es el mismo fallo que ya obligó a `div_euclid` dos veces en este proyecto.
    pub fn of_chunk(chunk: Wg3ChunkCoord) -> Self {
        Self {
            x: chunk.x.div_euclid(REGION_CHUNKS),
            z: chunk.z.div_euclid(REGION_CHUNKS),
        }
    }

    /// `(min_x, min_z, max_x, max_z)` en metros.
    pub fn bounds(&self) -> (f32, f32, f32, f32) {
        let x = self.x as f32 * REGION_M;
        let z = self.z as f32 * REGION_M;
        (x, z, x + REGION_M, z + REGION_M)
    }

    /// Semilla del compositor para esta región.
    ///
    /// Mezcla la del mundo con la coordenada, así que dos regiones vecinas del mismo mundo componen
    /// cosas distintas y la misma región del mismo mundo compone siempre lo mismo — que es todo lo
    /// que A2 necesita para ser determinista sin que las regiones se hablen.
    pub fn composer_seed(&self, world_seed: u64) -> i32 {
        // `composer_seed(world_seed)` ya recorta los 32 bits bajos y ese recorte está documentado
        // allí: dos semillas que solo difieran en los altos dan el mismo mundo de WG3.
        hash::mix(composer_seed(world_seed), self.x, self.z, REGION_SALT) as u32 as i32
    }
}

/// Sal propia del sorteo de región, para que la coordenada de región no quede correlacionada con
/// ninguna otra decisión que se tome en el mismo punto.
const REGION_SALT: i32 = 0x5EED_0A2Bu32 as i32;

/// Semilla del compositor a partir de la del mundo.
///
/// La del mundo es `u64` y la del compositor `i32`, porque nació de un `int` de C# y el oráculo lo
/// fija. Se cogen los 32 bits bajos y se reinterpretan: es determinista, no depende de la
/// plataforma y no hay nada que elegir. Dos semillas que solo se diferencien en los 32 bits altos
/// dan el mismo mundo de WG3 — se acepta, y aquí queda dicho para que no se descubra como un
/// "hallazgo".
pub fn composer_seed(world_seed: u64) -> i32 {
    world_seed as u32 as i32
}

/// El mundo compuesto de una semilla, listo para repartir.
#[derive(Debug, Clone)]
pub struct Wg3ServedWorld {
    world_seed: u64,
    placements: Vec<Wg3Placement>,
    /// ADR-098 — la geometría GENERADA: los conectores que unen lo que el catálogo no puede
    /// encajar. Van aparte de las colocaciones y no dentro porque no son piezas: no tienen índice
    /// de catálogo, y ésa es toda la novedad del ADR.
    segments: Vec<Wg3Segment>,
    /// ADR-099 D3 — los vanos excavados. Aparte de los tramos porque no son geometria del tramo:
    /// son materia que se le quita a otra pieza, y el raster los aplica al final.
    carves: Vec<Wg3Carve>,
    /// ADR-105 — los MACIZOS: materia que se anade y a la que los vanos no tocan (D2). Aparte de
    /// los tramos porque no son la cascara de ninguna sala.
    solids: Vec<Wg3Solid>,
}

/// Los ajustes con los que se compone una región del mundo SERVIDO.
///
/// Público y aparte de [`Wg3ServedWorld::compose_region`] para que una sonda pueda medir la misma
/// composición que se juega sin copiar su montaje: la sonda de islas ya lo copió una vez y una copia
/// que envejece mide un mundo que no existe.
pub fn region_settings(
    manifest: &Wg3Manifest,
    world_seed: u64,
    region: Wg3RegionCoord,
) -> Wg3ComposerSettings {
    let bounds = region.bounds();

    // ADR-096 — el contrato de junta. Las puertas salen del BORDE, no de la región, así que la
    // vecina calcula las mismas sin que nadie pregunte a nadie. La semilla que las sortea es la del
    // MUNDO y no la de la región: la de la región es distinta a cada lado del borde y daría dos
    // listas de puertas que no casan.
    let stub = junction::gate_stub_piece(manifest);
    let anchors: Vec<compose::Wg3Anchor> = match stub {
        Some(stub) => {
            junction::gates_of_region(composer_seed(world_seed), region.x, region.z, bounds)
                .into_iter()
                .filter_map(|gate| junction::stub_anchor(manifest, stub, gate))
                .collect()
        }
        None => {
            // Sin pieza que sirva de tramo no hay puertas, y el mundo vuelve a ser un tablero de
            // regiones selladas. Se avisa fuerte: es una propiedad del CATÁLOGO que se rompe sin que
            // nada falle.
            log::error!(
                "[wg3] el catálogo no tiene ninguna pieza que sirva de tramo de puerta — las \
                 regiones quedarán selladas"
            );
            Vec::new()
        }
    };

    Wg3ComposerSettings {
        budget: INTERIM_BUDGET,
        close_loops: true,
        bounds: Some(bounds),
        // ADR-098 — el enrutador SE ENCIENDE aquí, en el mundo que se sirve, y no en el compositor
        // por defecto: el oráculo fija el mundo de C#, y C# no enruta.
        //
        // Sin esto una región es un árbol más N bolsillos de dos piezas —uno por puerta de junta—,
        // que es exactamente lo que se siente andando como «llega un punto que se cierra y no hay
        // manera de moverte».
        route: Some(route::RouteSettings::default()),
        // ADR-098 enmienda 4 — CON EL ENRUTADOR ENCENDIDO, EL COMPOSITOR DEJA MÁS BOCAS SIN TAPAR.
        //
        // El enrutador estaba hambriento: el catálogo pone 1,96 bocas por pieza y un árbol de N las
        // gasta de dos en dos, así que al terminar el recorrido no quedaba NADA a lo que engancharse
        // dentro del árbol del jugador (enmienda 3). Una boca que el compositor decide tapar a
        // propósito queda en `SOCKET_PENDING_CAP` y SÍ llega al enrutador; si no la usa, la pasada
        // final la sella igual que antes. Subir esta perilla no añade geometría: reparte la que hay.
        //
        // Barrido sobre 16 regiones (`sweep_cap_chance`), y el estado de partida era mucho peor de
        // lo que decían cuatro: **solo 4 de 16 regiones se recorrían enteras y el 35 % de las
        // puertas de junta era inalcanzable**, o sea que la mayoría de los cruces entre regiones no
        // existían andando.
        //
        //   cap    m² ALCANZABLES  regiones enteras  tramos pisados  puertas  piezas/región
        //   0,05       3216             4/16              54 %         35 %       29,5
        //   0,08       3155            10/16              83 %         66 %       25,8
        //   0,11       3119            10/16              82 %         68 %       24,0
        //   0,14       3072            12/16              89 %         79 %       21,6
        //   0,18       2943            14/16              95 %         87 %       19,7
        //   0,22       2562            15/16              97 %         93 %       15,9
        //   0,30       1909            16/16             100 %        100 %       12,9
        //
        // **LA PRIMERA COLUMNA ES LA QUE MANDA, y la primera versión de esta tabla no la tenía.**
        // Con el porcentaje de lo pisable el 0,30 parecía perfecto; en metros cuadrados es un mundo
        // un 41 % más pequeño. Una región que pasa de 3298 m² al 89 % a 789 m² al 100 % ha perdido
        // dos tercios de sitio donde estar. Mismo error que ya costó tres conclusiones falsas el
        // 08-27: contar bien una cosa que no es la que importa.
        //
        // **El precio es real: piezas autoradas a cambio de conectores generados**, y un conector
        // hoy no tiene aspecto (el byte `style` no lo lee nadie). 0,18 cuesta un 8 % del área
        // alcanzable y compra 14 de 16 regiones recorribles y el 87 % de las puertas. Es un número:
        // bajarlo a 0,14 cuesta la mitad de área y da algo menos de conectividad; subirlo a 0,30 da
        // un mundo perfecto y un 41 % más pequeño.
        //
        // **Solo con el enrutador encendido.** El compositor por defecto sigue en 0,05 y el oráculo,
        // que fija el mundo de C#, no se mueve.
        deliberate_cap_chance: ROUTED_CAP_CHANCE,
        anchors,
        ..Wg3ComposerSettings::default()
    }
}

/// Probabilidad de dejar una boca sin usar cuando el enrutador está encendido. Ver la tabla del
/// barrido en `region_settings`.
pub const ROUTED_CAP_CHANCE: f32 = 0.18;

impl Wg3ServedWorld {
    /// **ADR-100 — LA REGIÓN QUE SE SIRVE: primero el plan, después la geometría.**
    ///
    /// Sustituye a [`Wg3ServedWorld::compose_region`] como fuente del mundo servido. La diferencia no
    /// es de calidad sino de QUIÉN DECIDE: allí la posición de cada pieza salía de la boca de la
    /// anterior y la topología emergía del orden de recorrido; aquí el reparto de espacios y las
    /// conexiones que deben existir se deciden mirando la región entera, y esto sólo los construye.
    ///
    /// Medido sobre las cuatro regiones de la auditoría, superficie andable: **21 → 82 %, 26 → 77 %,
    /// 3,5 → 80 %, 24 → 75 %**, con la mancha mayor entre el 98 y el 100 % de lo pisable.
    ///
    /// **ADR-101 — el catálogo está ENCENDIDO desde que los vanos viajan.** Hasta el wire 49 iba
    /// apagado: una pieza colocada necesita que se le excaven las puertas del plan —las suyas están
    /// donde las puso quien la dibujó—, y sin los vanos en el cable el servidor habría abierto puertas
    /// que el cliente dibuja tapiadas, que es el modo de fallo que R6 existe para impedir. Ahora
    /// `Wg3ChunkView` los lleva y las dos partes restan la misma caja.
    pub fn plan_region(manifest: &Wg3Manifest, world_seed: u64, region: Wg3RegionCoord) -> Self {
        let bounds = region.bounds();
        // Las puertas de junta se sortean con la semilla del MUNDO y no con la de la región: la de la
        // región es distinta a cada lado del borde y daría dos listas que no casan. El PLAN sí usa la
        // de la región, porque dos vecinas tienen que planificar edificios distintos.
        let gates =
            junction::gates_of_region(composer_seed(world_seed), region.x, region.z, bounds);
        // **ADR-102 — y ahora son PLANTAS.** Lo que se sirve es un edificio y no un plano: la baja
        // completa, la de encima recortada al corte principal de la de abajo, y el suelo de arriba
        // perforado por donde sube la escalera.
        let building = plan::plan_building(
            region.composer_seed(world_seed),
            bounds,
            &gates,
            plan::REGION_STOREYS,
        );
        let filled = fill::fill_building(&building, manifest);

        // **Un enlace que el plan pidió y que nadie pudo construir se dice EN VOZ ALTA.** El sistema
        // viejo tapaba ese hueco inventando un conector; aquí no hay nada que inventar, así que lo
        // único correcto es que aparezca con los dos espacios delante y se pueda ir a mirarlo.
        if !filled.links_failed.is_empty() || filled.openings_dropped > 0 {
            log::warn!(
                "[wg3] región ({},{}): {} enlaces del plan sin construir y {} huecos perdidos",
                region.x,
                region.z,
                filled.links_failed.len(),
                filled.openings_dropped
            );
        }
        for (a, b) in &filled.links_to_route {
            log::debug!(
                "[wg3] región ({},{}): PlannedLink {a} → {b} pendiente de enrutador",
                region.x,
                region.z
            );
        }

        Self {
            world_seed,
            placements: filled.placements,
            segments: filled.segments,
            carves: filled.carves,
            solids: filled.solids,
        }
    }

    /// ADR-096 — compone UNA REGIÓN: acotada a su caja y sembrada en su centro.
    ///
    /// Es lo que hace el mundo infinito sin tocar el compositor: una región es exactamente lo que
    /// éste ya sabía hacer —un recorrido finito desde una semilla—, y hay infinitas regiones.
    ///
    /// **Las regiones nacen SELLADAS en este paso, y es deuda declarada.** ADR-096 pide que no lo
    /// estén; el contrato de junta que las abre viene después y necesita este cimiento puesto.
    /// Mientras tanto la costura entre regiones se ve, y verla es lo que dirá si el contrato tiene
    /// que ser fino o basta con poco.
    pub fn compose_region(manifest: &Wg3Manifest, world_seed: u64, region: Wg3RegionCoord) -> Self {
        let settings = region_settings(manifest, world_seed, region);
        Self::compose_region_with(manifest, world_seed, region, &settings)
    }

    /// La misma composición de región, con los ajustes puestos desde fuera.
    ///
    /// Existe para que una sonda pueda medir el mundo que SE SIRVE cambiando un solo ajuste, en vez
    /// de rehacer el montaje. Copiarlo ya salió mal una vez: `compose_with` usa
    /// `composer_seed(world_seed)` y no la semilla de la REGIÓN, así que un barrido escrito con ella
    /// mide cuatro veces el mismo mundo con bordes distintos — números que no se parecen en nada a
    /// los del mundo servido y que invitan a una conclusión falsa.
    pub fn compose_region_with(
        manifest: &Wg3Manifest,
        world_seed: u64,
        region: Wg3RegionCoord,
        settings: &Wg3ComposerSettings,
    ) -> Self {
        let composed = compose::compose(region.composer_seed(world_seed), manifest, settings);
        if composed.connectors > 0 {
            log::debug!(
                "[wg3] región ({},{}): {} conectores ({} unieron islas), {} tramos",
                region.x,
                region.z,
                composed.connectors,
                composed.connectors_joining_islands,
                composed.segments.len()
            );
        }
        Self {
            world_seed,
            placements: composed.placements.iter().map(|c| c.placement).collect(),
            segments: composed.segments,
            carves: composed.carves,
            // El compositor por bocas es legado y no emite macizos: ADR-105 vive en el PLAN.
            solids: Vec::new(),
        }
    }

    /// Compone con los ajustes del mundo interino.
    pub fn compose(manifest: &Wg3Manifest, world_seed: u64) -> Self {
        let settings = Wg3ComposerSettings {
            budget: INTERIM_BUDGET,
            // ADR-096 — el mundo que se SIRVE cierra bucles; el que fija el oráculo, no. El default
            // es `false` porque el oráculo vigila la paridad con C#, y C# no los cierra: encenderlo
            // ahí pondría rojo lo único que caza una deriva silenciosa entre los dos idiomas.
            close_loops: true,
            ..Wg3ComposerSettings::default()
        };
        Self::compose_with(manifest, world_seed, &settings)
    }

    /// Compone con ajustes dados. Para tests y para cuando las perillas dejen de ser constantes.
    pub fn compose_with(
        manifest: &Wg3Manifest,
        world_seed: u64,
        settings: &Wg3ComposerSettings,
    ) -> Self {
        let composed = compose::compose(composer_seed(world_seed), manifest, settings);
        Self {
            world_seed,
            placements: composed.placements.iter().map(|c| c.placement).collect(),
            segments: composed.segments,
            carves: composed.carves,
            // El compositor por bocas es legado y no emite macizos: ADR-105 vive en el PLAN.
            solids: Vec::new(),
        }
    }

    pub fn world_seed(&self) -> u64 {
        self.world_seed
    }

    /// Todas las colocaciones del mundo, en el orden en que las puso el compositor.
    pub fn placements(&self) -> &[Wg3Placement] {
        &self.placements
    }

    /// El chunk que dibuja esta pieza: el que contiene su centro.
    pub fn owner_chunk(manifest: &Wg3Manifest, placement: &Wg3Placement) -> Option<Wg3ChunkCoord> {
        let piece = manifest.piece(placement.piece)?;
        let (min_x, min_z, max_x, max_z) = placement.bounds(piece);
        Some(Wg3ChunkCoord::containing(
            (min_x + max_x) * 0.5,
            (min_z + max_z) * 0.5,
        ))
    }

    /// Lo que se manda por el cable para un chunk: las piezas de las que ESE chunk es dueño.
    ///
    /// La lista vacía es un resultado válido y frecuente —el mundo es finito—, no una señal de que
    /// algo falta.
    pub fn placements_for_chunk(
        &self,
        manifest: &Wg3Manifest,
        coord: Wg3ChunkCoord,
    ) -> Vec<Wg3Placement> {
        self.placements
            .iter()
            .filter(|p| Self::owner_chunk(manifest, p) == Some(coord))
            .copied()
            .collect()
    }

    /// ADR-098 — todas las tramos generadas del mundo.
    pub fn segments(&self) -> &[Wg3Segment] {
        &self.segments
    }

    /// ADR-109 D5 — el espacio de MÁS ABAJO en esa vertical, sin saber la cota.
    ///
    /// Lo pide el reparto de criaturas: sortea un punto en XZ y no tiene cota que darle —la que traía
    /// era la de una capa de WG2. La planta baja es la que hoy se puebla, así que el de suelo más bajo
    /// es la respuesta; el día que el reparto sepa de plantas, esto se sustituye por `space_at`.
    pub fn lowest_space_at_xz(&self, x: f32, z: f32) -> Option<&Wg3Segment> {
        let x_cm = (x * 100.0).round() as i32;
        let z_cm = (z * 100.0).round() as i32;
        self.segments
            .iter()
            .filter(|s| {
                x_cm >= s.x_cm
                    && x_cm <= s.x_cm + s.size_x_cm
                    && z_cm >= s.z_cm
                    && z_cm <= s.z_cm + s.size_z_cm
            })
            // Empate por área como en `space_at`, y por lo mismo: que la respuesta no dependa del
            // orden del vector, que no es el mismo en las dos partes.
            .min_by_key(|s| (s.floor_y_cm, (s.size_x_cm as i64) * (s.size_z_cm as i64)))
    }

    /// ADR-108 D6 — el ESPACIO que contiene ese punto del mundo, si alguno.
    ///
    /// La cota MANDA y no es un adorno: dos plantas se solapan en XZ, así que un test sólo
    /// horizontal contestaría el de abajo la mitad de las veces. El margen de una losa por arriba
    /// existe porque la posición que llega de una colocación es la del objeto sobre el suelo, no la
    /// del suelo.
    ///
    /// Sobre TRAMOS y no sobre el plan a propósito: es exactamente el mismo dato que el cliente
    /// recibe por el cable, así que la regla del host y el aviso del cliente no pueden desalinearse
    /// —que es la propiedad que la puerta de construcción necesita y la única forma barata de
    /// tenerla.
    pub fn space_at(&self, x: f32, y: f32, z: f32) -> Option<&Wg3Segment> {
        const HEAD_MARGIN_CM: i32 = 40;
        let x_cm = (x * 100.0).round() as i32;
        let z_cm = (z * 100.0).round() as i32;
        let y_cm = (y * 100.0).round() as i32;
        self.segments
            .iter()
            .filter(|s| {
                x_cm >= s.x_cm
                    && x_cm <= s.x_cm + s.size_x_cm
                    && z_cm >= s.z_cm
                    && z_cm <= s.z_cm + s.size_z_cm
                    && y_cm >= s.floor_y_cm - HEAD_MARGIN_CM
                    && y_cm <= s.floor_y_cm + s.height_cm
            })
            // EL SUELO MÁS ALTO POR DEBAJO DEL PUNTO, no el primero de la lista. Los espacios SÍ se
            // solapan —medido: 26 pares de 47.895 en la región (0,0), y el caso típico es una
            // escalera de dos plantas cruzando el volumen de una sala de arriba—, así que «el primero
            // que contenga el punto» depende del orden del vector. Ese orden no es el mismo en el
            // cliente, que los recibe repartidos por chunk, y la puerta de construcción quedaría
            // contestando dos cosas distintas al host y al que la mira. Empate por área: manda el más
            // pequeño, que es el más específico.
            .min_by_key(|s| (-s.floor_y_cm, (s.size_x_cm as i64) * (s.size_z_cm as i64)))
    }

    /// El chunk que dibuja una tramo: el que contiene su CENTRO, igual que una pieza.
    ///
    /// Y por la misma razón puede usarse la misma regla: el tope de 25 m de lado de tramo
    /// (`segment::MAX_SEGMENT_M`) mantiene el invariante en el que se apoya —nada llega a los 50 m del
    /// chunk, así que centrado nunca asoma más allá de los vecinos inmediatos de su dueño—. Ese tope
    /// es la razón de que una ruta larga se parta en más tramos en vez de recortarse en la frontera.
    pub fn segment_owner_chunk(cell: &Wg3Segment) -> Wg3ChunkCoord {
        let (cx, cz) = cell.centre();
        Wg3ChunkCoord::containing(cx, cz)
    }

    /// Lo que se manda por el cable: las tramos de las que ESE chunk es dueño.
    pub fn segments_for_chunk(&self, coord: Wg3ChunkCoord) -> Vec<Wg3Segment> {
        self.segments
            .iter()
            .filter(|c| Self::segment_owner_chunk(c) == coord)
            .cloned()
            .collect()
    }

    /// ADR-099 D3 — los vanos que TOCAN el chunk.
    ///
    /// Se filtra ancho: un vano se excava justo en la frontera entre dos piezas, y ésa es
    /// exactamente la clase de sitio donde cae también una frontera de chunk. Perderlo por un
    /// centímetro dejaría la puerta abierta por un lado y tapiada por el otro, que es el mismo
    /// fallo de siempre visto desde otro sitio.
    /// ADR-105 D3 — los MACIZOS que TOCAN este chunk, para el raster.
    ///
    /// **Tocan, no pertenecen**, y es la misma asimetria que ya tienen las piezas: al cliente se le
    /// manda el macizo en UN chunk --el de su centro, porque el cliente monta un GameObject por chunk
    /// y no deduplica-- pero el raster necesita todos los que tocan, porque un pilar a caballo de la
    /// frontera bloquea a los dos lados. Romper esta asimetria da un pilar dibujado dos veces o un
    /// pilar que no colisiona por un lado, y ninguno de los dos sale en una captura.
    pub fn solids_touching_chunk(&self, coord: Wg3ChunkCoord) -> Vec<Wg3Solid> {
        let (cmin_x, cmin_z, cmax_x, cmax_z) = coord.bounds();
        self.solids
            .iter()
            .filter(|s| {
                let (min_x, min_z, max_x, max_z) = s.bounds();
                max_x > cmin_x && min_x < cmax_x && max_z > cmin_z && min_z < cmax_z
            })
            .copied()
            .collect()
    }

    /// ADR-105 D3 — los macizos de los que este chunk es DUENO, por su centro. Es lo que viaja.
    pub fn solids_owned_by_chunk(&self, coord: Wg3ChunkCoord) -> Vec<Wg3Solid> {
        self.solids
            .iter()
            .filter(|s| {
                let (cx, cz) = s.centre();
                Wg3ChunkCoord::containing(cx, cz) == coord
            })
            .copied()
            .collect()
    }

    pub fn carves_touching_chunk(&self, coord: Wg3ChunkCoord) -> Vec<Wg3Carve> {
        let (cmin_x, cmin_z, cmax_x, cmax_z) = coord.bounds();
        self.carves
            .iter()
            .filter(|k| {
                let min_x = k.x_cm as f32 / 100.0;
                let min_z = k.z_cm as f32 / 100.0;
                let max_x = (k.x_cm + k.size_x_cm) as f32 / 100.0;
                let max_z = (k.z_cm + k.size_z_cm) as f32 / 100.0;
                max_x > cmin_x && min_x < cmax_x && max_z > cmin_z && min_z < cmax_z
            })
            .copied()
            .collect()
    }

    /// Las tramos que TOCAN el chunk, dueñas o no. Es lo que necesita el ráster, por lo mismo que
    /// las piezas: una pared que cruza la frontera bloquea a los dos lados.
    pub fn segments_touching_chunk(&self, coord: Wg3ChunkCoord) -> Vec<Wg3Segment> {
        let (cmin_x, cmin_z, cmax_x, cmax_z) = coord.bounds();
        self.segments
            .iter()
            .filter(|c| {
                let (min_x, min_z, max_x, max_z) = c.bounds();
                max_x > cmin_x && min_x < cmax_x && max_z > cmin_z && min_z < cmax_z
            })
            .cloned()
            .collect()
    }

    /// Las colocaciones que TOCAN el chunk, dueñas o no. Es lo que necesita el ráster de colisión:
    /// una pared que cruza la frontera bloquea a los dos lados.
    pub fn placements_touching_chunk(
        &self,
        manifest: &Wg3Manifest,
        coord: Wg3ChunkCoord,
    ) -> Vec<Wg3Placement> {
        let (cmin_x, cmin_z, cmax_x, cmax_z) = coord.bounds();
        self.placements
            .iter()
            .filter(|p| match manifest.piece(p.piece) {
                Some(piece) => {
                    let (min_x, min_z, max_x, max_z) = p.bounds(piece);
                    max_x > cmin_x && min_x < cmax_x && max_z > cmin_z && min_z < cmax_z
                }
                None => false,
            })
            .copied()
            .collect()
    }
}

/// La composición cacheada del bucle de juego.
///
/// Perezosa porque un *joiner* no sabe la semilla hasta el HandshakeAck, y rehecha si la semilla
/// cambia porque servir el mundo de otra semilla es servir un mundo que el resto de la partida no
/// ve. Componer trescientas piezas cuesta milisegundos, así que rehacerla no necesita más
/// ceremonia que ésta.
#[derive(Debug, Default)]
pub struct Wg3WorldCache {
    world_seed: u64,
    regions: std::collections::HashMap<Wg3RegionCoord, Wg3ServedWorld>,
}

/// Regiones vivas a la vez. Cada una son ~300 piezas de `Wg3Placement` (11 bytes) más el vector:
/// unos kilobytes. El tope existe para que una sesión larga que recorra mucho mundo no acumule sin
/// fin, no porque una región pese.
const MAX_CACHED_REGIONS: usize = 16;

impl Wg3WorldCache {
    /// ADR-096 — la región de un chunk, componiéndola si hace falta.
    ///
    /// La caché se vacía entera si cambia la semilla: servir el mundo de otra semilla es servir un
    /// mundo que el resto de la partida no ve, y un *joiner* no la sabe hasta el HandshakeAck.
    pub fn region_for(
        &mut self,
        manifest: &Wg3Manifest,
        world_seed: u64,
        coord: Wg3ChunkCoord,
    ) -> &Wg3ServedWorld {
        if self.world_seed != world_seed {
            self.regions.clear();
            self.world_seed = world_seed;
        }

        let region = Wg3RegionCoord::of_chunk(coord);
        if !self.regions.contains_key(&region) {
            // Poda tonta y a propósito: al pasar del tope se tira TODO en vez de mantener un LRU.
            // Recomponer cuesta milisegundos, y un LRU aquí sería estado con orden que mantener —
            // justo lo que R3 evita— a cambio de ahorrar algo que ya es barato.
            if self.regions.len() >= MAX_CACHED_REGIONS {
                self.regions.clear();
            }
            // ADR-100 — el mundo servido sale del PLAN. `compose_region` queda como el compositor por
            // bocas, que sigue vivo para el oráculo y para las sondas que lo miden a él.
            let world = Wg3ServedWorld::plan_region(manifest, world_seed, region);
            log::info!(
                "[wg3] región ({},{}) planificada para la semilla {world_seed}: {} piezas, {} tramos",
                region.x,
                region.z,
                world.placements().len(),
                world.segments().len()
            );
            self.regions.insert(region, world);
        }
        self.regions.get(&region).expect("se acaba de componer")
    }
}
