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
use super::scale;

/// Estado de una boca. Espejo de las constantes de `Wg3World`.
pub const SOCKET_OPEN: u8 = 0;
pub const SOCKET_CONNECTED: u8 = 1;
pub const SOCKET_CAPPED: u8 = 2;

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
}

impl Default for Wg3ComposerSettings {
    fn default() -> Self {
        Self {
            budget: 30,
            deliberate_cap_chance: 0.17,
            cap_grace_count: 3,
            scale_exact_bonus: 4.2,
            scale_near_bonus: 1.0,
            scale_far_bonus: 0.22,
            repeat_parent_penalty: 0.18,
            repeat_grandparent_penalty: 0.45,
            close_loops: false,
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

/// Resultado de una composición.
#[derive(Debug, Clone, Default)]
pub struct Wg3ComposedWorld {
    pub world_seed: i32,
    pub placements: Vec<Wg3Composed>,
    pub caps: Vec<Wg3Cap>,

    /// Candidatas descartadas porque la huella pisaba algo ya colocado. No es un error: es la medida
    /// de cuánto aprieta el mundo. Un cero sostenido significa que el catálogo es demasiado pequeño
    /// para llenar el espacio.
    pub rejected_by_overlap: u32,
    /// Candidatas descartadas por anchura o cota con el tipo ya coincidiendo. Un número alto delata un
    /// catálogo con bocas que casi casan — falta una transición.
    pub rejected_by_validator: u32,
    /// Bocas selladas por no haber ninguna candidata viable.
    pub forced_caps: u32,

    /// ADR-096 — bucles cerrados: veces que dos bocas abiertas se unieron entre sí en vez de abrir
    /// rama nueva. Cero con `close_loops` apagado; con él encendido es la medida de cuánto deja de
    /// ser un árbol el mundo, y de dónde sale el tamaño de región.
    pub loops_closed: u32,
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
            rejected_by_overlap: 0,
            rejected_by_validator: 0,
            forced_caps: 0,
        }
    }

    fn run(&mut self) {
        let Some(seed_piece) = self.manifest.pieces.first() else {
            return;
        };

        // La semilla es la PRIMERA pieza del catálogo, centrada en el origen del mundo. Elegirla por
        // sorteo haría que cambiar el catálogo moviera mundos ya generados.
        self.place(
            seed_piece.index,
            0,
            -seed_piece.size_x * 0.5,
            -seed_piece.size_z * 0.5,
            0,
            None,
        );

        let mut frontier: Vec<(usize, usize)> = Vec::new();
        push_sockets(&mut frontier, &self.nodes, 0);

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
                    self.cap(pi, si, px, pz, parent_world_side, &parent_socket, false);
                    continue;
                }
            }

            self.collect_candidates(pi, &parent_socket, px, pz, needed_side, child_depth);

            if self.candidates.is_empty() {
                self.cap(pi, si, px, pz, parent_world_side, &parent_socket, true);
                self.forced_caps += 1;
                continue;
            }

            let mut pick_stream = hash::stream_at(self.world_seed, px, pz, SALT_PICK);
            let chosen = weighted_pick(&self.candidates, &mut pick_stream);

            let child = self.place(
                chosen.piece,
                chosen.rotation,
                chosen.origin_x,
                chosen.origin_z,
                child_depth,
                Some(pi),
            );
            self.nodes[pi].socket_state[si] = SOCKET_CONNECTED;
            self.nodes[child].socket_state[chosen.socket_index] = SOCKET_CONNECTED;
            push_sockets(&mut frontier, &self.nodes, child);
        }

        self.cap_everything_still_open();
    }

    /// Presupuesto agotado o frontera sin recorrer: todo lo que quede abierto se sella. Sin esta
    /// pasada el mundo termina en bocas que dan a la nada.
    fn cap_everything_still_open(&mut self) {
        for i in 0..self.nodes.len() {
            for s in 0..self.nodes[i].socket_state.len() {
                if self.nodes[i].socket_state[s] != SOCKET_OPEN {
                    continue;
                }
                let piece = self.piece_of(i);
                let socket = piece.sockets[s].clone();
                let (x, z) = world_socket_point(&self.nodes[i], piece, s);
                let side = (socket.side + self.nodes[i].rotation) % 4;
                self.cap(i, s, x, z, side, &socket, true);
                self.forced_caps += 1;
            }
        }
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

                if overlaps_any(&self.nodes, manifest, ox, oz, w, d) {
                    self.rejected_by_overlap += 1;
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
                self.loops_closed += 1;
                return true;
            }
        }
        false
    }

    fn place(
        &mut self,
        piece: u16,
        rotation: u8,
        origin_x: f32,
        origin_z: f32,
        depth: i32,
        parent: Option<usize>,
    ) -> usize {
        let sockets = self.manifest.pieces[piece as usize].sockets.len();
        self.nodes.push(Node {
            piece,
            rotation,
            origin_x,
            origin_z,
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
                },
                depth: n.depth,
                parent: n.parent,
            })
            .collect();

        Wg3ComposedWorld {
            world_seed: self.world_seed,
            placements,
            caps: self.caps,
            rejected_by_overlap: self.rejected_by_overlap,
            rejected_by_validator: self.rejected_by_validator,
            forced_caps: self.forced_caps,
            loops_closed: self.loops_closed,
        }
    }
}

/// ¿Casan estas dos bocas? Espejo de `Wg3Validator.ValidateConnection`, sin el motivo: aquí nadie lo
/// lee, y devolverlo obligaría a formatear una cadena por candidata descartada — que son decenas por
/// boca.
fn connection_ok(a: &Wg3Socket, b: &Wg3Socket) -> bool {
    a.kind == b.kind
        && (a.width - b.width).abs() <= WIDTH_MATCH_TOLERANCE
        && (a.floor_y - b.floor_y).abs() <= FLOOR_MATCH_TOLERANCE
        && a.ceiling_y.min(b.ceiling_y) - a.floor_y.max(b.floor_y) >= MIN_HEADROOM
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

fn overlaps_any(nodes: &[Node], manifest: &Wg3Manifest, x: f32, z: f32, w: f32, d: f32) -> bool {
    nodes.iter().any(|n| {
        let piece = &manifest.pieces[n.piece as usize];
        let (nw, nd) = if n.rotation.is_multiple_of(2) {
            (piece.size_x, piece.size_z)
        } else {
            (piece.size_z, piece.size_x)
        };
        n.origin_x < x + w - OVERLAP_EPS
            && n.origin_x + nw - OVERLAP_EPS > x
            && n.origin_z < z + d - OVERLAP_EPS
            && n.origin_z + nd - OVERLAP_EPS > z
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
