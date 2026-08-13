//! ADR-068 — sprays (pintadas de trazo libre sobre las paredes del mundo).
//!
//! Este módulo es SOLO el modelo: la forma del dato, sus topes y el almacén por chunk.
//! No sabe de wire, de save ni de render — esas capas llegan en los commits siguientes de S1.
//!
//! Dos invariantes que el resto del sistema da por hechos:
//!
//! 1. **El anclaje es CHUNK-LOCAL, nunca global** (ADR-068 decisión 3). `Spray::local_pos`
//!    guarda X/Z relativos al origen del chunk, en `[0, CHUNK_SIZE)`. La razón es ADR-067:
//!    cuando el chunk displacement aterrice, la geometría viaja, y una pintada en coordenadas
//!    globales quedaría flotando donde ya no hay pared. **Cuidado con el falso amigo**: el
//!    `local_pos_in_chunk` de `world/levels/level_0/content.rs` acepta coordenadas locales y
//!    DEVUELVE globales (trampa documentada en la enmienda de ADR-067). Las conversiones de
//!    aquí se llaman `to_chunk_local` / `to_world` y hacen exactamente lo que dicen.
//!
//! 2. **Todo tope se valida en el host** (ADR-068 decisión 5). `validate` es la única puerta:
//!    un cliente modificado no puede inflar el save ni el wire por encima de estos números.
//!    El cap no es UX, es la frontera del tamaño del save.
//!
//! La Y NO es chunk-local: los chunks parten el mundo en X/Z, la altura la separa `layer`.
//! `local_pos[1]` es por tanto altura de mundo tal cual, y un displacement no la toca.

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

use crate::utils::{Vec3, CHUNK_SIZE};

/// Máximo de pintadas vivas por chunk. Al llenarse, la más antigua se desaloja (FIFO): pintar
/// encima es mecánica (decisión 4), pero un chunk que acumula pintadas sin fin es crecimiento
/// de save sin límite, que es el único "grief" que este ADR sí ataja.
pub const MAX_SPRAYS_PER_CHUNK: usize = 64;

/// Trazos por pintada y puntos por pintada (sumando todos sus trazos).
///
/// El presupuesto se MIDE en `the_worst_case_spray_stays_within_its_declared_budget`, no se
/// estima: el borrador de ADR-068 prometía ~1,2 KB y la medida dijo 3,1 KB. Y lo que domina
/// NO son los puntos sino la cabecera de cada trazo — un trazo se serializa como mapa con
/// nombres (`color`/`width`/`points`), ~40 B, de los que solo los puntos son dato. Por eso el
/// tope de trazos baja de 64 a 32 (ADR-068 §2 declara los números afinables en implementación,
/// la existencia de topes es lo que es ley): el de PUNTOS es el que limita cuánto detalle cabe
/// en un dibujo, el de trazos solo limita cuántas veces levantas el dedo del gatillo.
pub const MAX_STROKES_PER_SPRAY: usize = 32;
pub const MAX_POINTS_PER_SPRAY: usize = 512;

/// Lado del lienzo en metros. El tope superior es el de la decisión 2; el inferior evita
/// lienzos degenerados (un lienzo de 0 no se puede rasterizar y dividiría por cero).
pub const MAX_CANVAS_METERS: f32 = 2.0;
pub const MIN_CANVAS_METERS: f32 = 0.1;

/// Alcance jugador→pared admitido al colocar, coherente con el `max_distance` de interacción.
pub const MAX_PLACE_DISTANCE: f32 = 5.0;

/// Tamaño de la paleta: `SprayStroke::color` es un índice, no un RGB suelto. Un índice acota
/// el dato por construcción y deja el color real como decisión de arte del cliente.
pub const PALETTE_LEN: u8 = 16;

/// Clave del almacén: chunk XZ + capa. La misma tripleta con la que el cliente pide geometría
/// (`ClientMessage::RequestChunk`), para que hidratar pintadas sea la misma pregunta.
pub type SprayChunkKey = (i32, i32, u8);

/// Un trazo: el recorrido continuo de la boquilla mientras el jugador mantiene el gatillo.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SprayStroke {
    /// Índice en la paleta, `< PALETTE_LEN`.
    pub color: u8,
    /// Grosor de boquilla cuantizado a la misma retícula de 256 que los puntos.
    pub width: u8,
    /// Puntos en espacio de LIENZO: retícula 256×256 sobre el rectángulo de `Spray::size`,
    /// **X e Y intercalados** (`[x0,y0,x1,y1,…]`), así que la longitud es siempre PAR.
    /// Cuantizados a u8 a propósito — a 2 m de lienzo, un paso de retícula son 7,8 mm, por
    /// debajo de lo que el ojo separa en una pared, y cuesta 1 byte en vez de 4.
    ///
    /// `serde_bytes` NO es decoración, y la razón ya está escrita en este repo: el doc de
    /// `PeerVoice::data` mide que un array de enteros cuesta ~1,5× lo que el mismo `bin`.
    /// Aquí se midió igual — un `Vec<[u8; 2]>` gastaba 3 bytes por punto (cabecera de array
    /// por punto) y ponía el peor caso en 3,1 KB; como `bin` plano baja a 1 byte por
    /// coordenada. El cliente escribe estos bytes con el `MsgPackWriter.WriteBin` que ya usa
    /// para la voz.
    #[serde(with = "serde_bytes")]
    pub points: Vec<u8>,
}

impl SprayStroke {
    /// Puntos reales del trazo. La longitud del blob es el DOBLE (X e Y intercalados).
    pub fn point_count(&self) -> usize {
        self.points.len() / 2
    }
}

/// Una pintada colocada: registro INMUTABLE. No se edita ni se borra la de otro — la única
/// interacción con la pintada ajena es colocar una nueva que la tape (ADR-068 decisión 4).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Spray {
    /// Id acuñado por el host, monótono. Desempata el orden de render entre pintadas del
    /// mismo tick.
    pub id: u32,
    /// Chunk al que pertenece. Junto con `local_pos` es el anclaje del invariante 1.
    pub cx: i32,
    pub cz: i32,
    pub layer: u8,
    /// X/Z LOCALES al chunk en `[0, CHUNK_SIZE)`; Y es altura de mundo (ver cabecera).
    pub local_pos: [f32; 3],
    /// Giro del lienzo en grados. CONVENIO, fijado por el render de S2 y espejado en
    /// `SprayMsg.yaw`: es la dirección hacia la que MIRA la pintada — hacia quien la lee, no
    /// hacia la pared. Quien pinte apuntando a una pared manda su propio yaw + 180.
    pub yaw: f32,
    /// Ancho y alto del lienzo en metros.
    pub size: [f32; 2],
    /// `PeerId` del autor. Informativo — no concede derechos sobre la pintada.
    pub author: u16,
    /// Tick del host al aceptarla. ES el orden de render: la más nueva se dibuja encima.
    pub tick: u64,
    pub strokes: Vec<SprayStroke>,
}

/// Por qué el host rechazó una pintada. Tipado y no un `bool` para que el log diga cuál de
/// los topes se cruzó — un rechazo silencioso sobre un cliente legítimo es indepurable.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SprayReject {
    NoStrokes,
    TooManyStrokes(usize),
    TooManyPoints(usize),
    EmptyStroke,
    /// Blob de puntos de longitud impar: X e Y van intercalados, así que un byte suelto
    /// significa que el emisor y este formato no coinciden. Rechazo, no truncado.
    OddPointBytes(usize),
    BadColor(u8),
    CanvasOutOfRange,
    LocalOutOfChunk,
    NonFinite,
    TooFar,
}

impl std::fmt::Display for SprayReject {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::NoStrokes => write!(f, "no_strokes"),
            Self::TooManyStrokes(n) => write!(f, "too_many_strokes={n}"),
            Self::TooManyPoints(n) => write!(f, "too_many_points={n}"),
            Self::EmptyStroke => write!(f, "empty_stroke"),
            Self::OddPointBytes(n) => write!(f, "odd_point_bytes={n}"),
            Self::BadColor(c) => write!(f, "bad_color={c}"),
            Self::CanvasOutOfRange => write!(f, "canvas_out_of_range"),
            Self::LocalOutOfChunk => write!(f, "local_out_of_chunk"),
            Self::NonFinite => write!(f, "non_finite"),
            Self::TooFar => write!(f, "too_far"),
        }
    }
}

/// Chunk que contiene una posición de mundo, con la misma división por suelo que
/// `utils::world_to_chunk` (que toma `Vec3`; ésta trabaja sobre el array del wire).
pub fn chunk_of(world_pos: [f32; 3]) -> (i32, i32) {
    (
        (world_pos[0] / CHUNK_SIZE).floor() as i32,
        (world_pos[2] / CHUNK_SIZE).floor() as i32,
    )
}

/// Mundo → local al chunk. X/Z quedan en `[0, CHUNK_SIZE)`; Y pasa tal cual.
pub fn to_chunk_local(world_pos: [f32; 3], cx: i32, cz: i32) -> [f32; 3] {
    [
        world_pos[0] - cx as f32 * CHUNK_SIZE,
        world_pos[1],
        world_pos[2] - cz as f32 * CHUNK_SIZE,
    ]
}

/// Local al chunk → mundo. Inversa exacta de `to_chunk_local`.
pub fn to_world(local_pos: [f32; 3], cx: i32, cz: i32) -> [f32; 3] {
    [
        local_pos[0] + cx as f32 * CHUNK_SIZE,
        local_pos[1],
        local_pos[2] + cz as f32 * CHUNK_SIZE,
    ]
}

impl Spray {
    /// Posición de mundo de esta pintada HOY. Deliberadamente un método y no un campo: el
    /// dato guardado es el local, y el global se deriva al leer (la "traducción en el punto
    /// de lectura" que la enmienda de ADR-067 señala como la salida barata).
    pub fn world_pos(&self) -> [f32; 3] {
        to_world(self.local_pos, self.cx, self.cz)
    }

    pub fn key(&self) -> SprayChunkKey {
        (self.cx, self.cz, self.layer)
    }

    pub fn point_count(&self) -> usize {
        self.strokes.iter().map(SprayStroke::point_count).sum()
    }

    /// La única puerta. Comprueba TODOS los topes de ADR-068 decisión 5 salvo el de alcance,
    /// que necesita saber dónde está el jugador y va en `validate_from`.
    pub fn validate(&self) -> Result<(), SprayReject> {
        if !self.local_pos.iter().all(|v| v.is_finite())
            || !self.size.iter().all(|v| v.is_finite())
            || !self.yaw.is_finite()
        {
            // Antes que cualquier comparación: un NaN hace fallar TODA comparación, así que
            // sin esta guarda un NaN pasaría los rangos de abajo y envenenaría save y render.
            return Err(SprayReject::NonFinite);
        }

        if self.strokes.is_empty() {
            return Err(SprayReject::NoStrokes);
        }
        if self.strokes.len() > MAX_STROKES_PER_SPRAY {
            return Err(SprayReject::TooManyStrokes(self.strokes.len()));
        }
        let points = self.point_count();
        if points > MAX_POINTS_PER_SPRAY {
            return Err(SprayReject::TooManyPoints(points));
        }
        for stroke in &self.strokes {
            if stroke.points.is_empty() {
                return Err(SprayReject::EmptyStroke);
            }
            if stroke.points.len() % 2 != 0 {
                return Err(SprayReject::OddPointBytes(stroke.points.len()));
            }
            if stroke.color >= PALETTE_LEN {
                return Err(SprayReject::BadColor(stroke.color));
            }
        }

        if self
            .size
            .iter()
            .any(|s| *s < MIN_CANVAS_METERS || *s > MAX_CANVAS_METERS)
        {
            return Err(SprayReject::CanvasOutOfRange);
        }

        let [lx, _, lz] = self.local_pos;
        if !(0.0..CHUNK_SIZE).contains(&lx) || !(0.0..CHUNK_SIZE).contains(&lz) {
            return Err(SprayReject::LocalOutOfChunk);
        }

        Ok(())
    }

    /// `validate` más el tope de alcance contra la posición del jugador que la coloca.
    pub fn validate_from(&self, player: Vec3) -> Result<(), SprayReject> {
        self.validate()?;
        let w = self.world_pos();
        if player.distance(Vec3::new(w[0], w[1], w[2])) > MAX_PLACE_DISTANCE {
            return Err(SprayReject::TooFar);
        }
        Ok(())
    }
}

/// Las pintadas del mundo, indexadas por chunk. Índice en memoria; se persiste plano (ver
/// `all`/`from_sprays`), igual que `World::corpses` frente a `SaveFile::corpses`.
#[derive(Debug, Clone, Default)]
pub struct SprayStore {
    by_chunk: HashMap<SprayChunkKey, Vec<Spray>>,
}

impl SprayStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// Inserta una pintada YA validada, desalojando la más antigua de su chunk si estaba
    /// lleno. Devuelve la desalojada, si la hubo, para que el llamante pueda anunciarlo.
    ///
    /// No revalida: el host valida al aceptar la petición y la hidratación de un save lo
    /// hace al cargar. Revalidar aquí escondería un camino sin validar en vez de cerrarlo.
    pub fn insert(&mut self, spray: Spray) -> Option<Spray> {
        let list = self.by_chunk.entry(spray.key()).or_default();
        let evicted = if list.len() >= MAX_SPRAYS_PER_CHUNK {
            // La lista se mantiene ordenada por (tick, id), así que la más antigua va delante.
            Some(list.remove(0))
        } else {
            None
        };
        // Inserción ordenada en vez de push+sort: la lista ya está ordenada y el caso normal
        // (la nueva es la más reciente) es un push al final.
        let at = list.partition_point(|s| (s.tick, s.id) <= (spray.tick, spray.id));
        list.insert(at, spray);
        evicted
    }

    /// Las pintadas de un chunk en ORDEN DE RENDER: de la más antigua a la más nueva, que es
    /// como se dibujan para que la última tape a las anteriores (decisión 4).
    pub fn chunk(&self, key: SprayChunkKey) -> &[Spray] {
        self.by_chunk.get(&key).map_or(&[], |v| v.as_slice())
    }

    pub fn chunk_is_full(&self, key: SprayChunkKey) -> bool {
        self.chunk(key).len() >= MAX_SPRAYS_PER_CHUNK
    }

    pub fn len(&self) -> usize {
        self.by_chunk.values().map(|v| v.len()).sum()
    }

    pub fn is_empty(&self) -> bool {
        self.by_chunk.values().all(|v| v.is_empty())
    }

    /// Vista plana y DETERMINISTA para persistir (mismo criterio que el sort de `build_save`
    /// sobre corpses: un fichero que cambia de orden entre guardados es ruido en el diff).
    pub fn all(&self) -> Vec<Spray> {
        let mut out: Vec<Spray> = self.by_chunk.values().flatten().cloned().collect();
        out.sort_unstable_by_key(|s| s.id);
        out
    }

    /// Reconstruye el índice desde la lista plana de un save, DESCARTANDO lo que no valide.
    /// Un save escrito por una versión con topes más flojos (o tocado a mano) se sanea al
    /// cargar en vez de reventar, misma dirección que el `revive_if_dead_on_load` de la
    /// enmienda de ADR-025: sanear en CARGA no exige migración.
    pub fn from_sprays(sprays: Vec<Spray>) -> (Self, usize) {
        let mut store = Self::new();
        let mut rejected = 0usize;
        for spray in sprays {
            if spray.validate().is_err() {
                rejected += 1;
                continue;
            }
            if store.chunk_is_full(spray.key()) {
                // No desaloja al hidratar: un save por encima del cap se trunca por el final,
                // conservando las MÁS ANTIGUAS es lo contrario de lo que quiere el jugador.
                // Aquí se descartan las más antiguas dejando entrar las nuevas vía insert().
                store.insert(spray);
                rejected += 1;
                continue;
            }
            store.insert(spray);
        }
        (store, rejected)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// `n` PUNTOS (no bytes): el blob sale con el doble de longitud, X e Y intercalados.
    fn stroke(color: u8, n: usize) -> SprayStroke {
        let mut points = Vec::with_capacity(n * 2);
        for i in 0..n {
            points.push((i % 256) as u8);
            points.push((i % 251) as u8);
        }
        SprayStroke {
            color,
            width: 4,
            points,
        }
    }

    fn spray(id: u32, tick: u64) -> Spray {
        Spray {
            id,
            cx: 1,
            cz: -2,
            layer: 0,
            local_pos: [12.5, 1.6, 33.0],
            yaw: 90.0,
            size: [1.0, 1.0],
            author: 7,
            tick,
            strokes: vec![stroke(3, 8)],
        }
    }

    #[test]
    fn chunk_local_round_trips_through_world_space() {
        // El invariante 1 en una línea: guardar local y leer global debe devolver el punto
        // original, también con chunk negativo (donde una división truncada, en vez de por
        // suelo, se equivocaría de chunk).
        for (cx, cz) in [(0, 0), (1, -2), (-3, -7), (12, 40)] {
            let world = [
                cx as f32 * CHUNK_SIZE + 17.25,
                1.8,
                cz as f32 * CHUNK_SIZE + 4.5,
            ];
            assert_eq!(chunk_of(world), (cx, cz), "chunk_of falló en ({cx},{cz})");
            let local = to_chunk_local(world, cx, cz);
            assert!(
                (0.0..CHUNK_SIZE).contains(&local[0]) && (0.0..CHUNK_SIZE).contains(&local[2]),
                "local fuera del chunk en ({cx},{cz}): {local:?}"
            );
            assert_eq!(local[1], world[1], "la Y no es chunk-local");
            assert_eq!(to_world(local, cx, cz), world);
        }
    }

    #[test]
    fn a_well_formed_spray_validates() {
        assert_eq!(spray(1, 10).validate(), Ok(()));
    }

    #[test]
    fn every_cap_of_decision_5_is_enforced() {
        // Un caso por tope. Si alguno deja de rechazar, el cliente modificado que lo cruce
        // llega al save.
        let mut s = spray(1, 10);
        s.strokes.clear();
        assert_eq!(s.validate(), Err(SprayReject::NoStrokes));

        let mut s = spray(1, 10);
        s.strokes = (0..MAX_STROKES_PER_SPRAY + 1)
            .map(|_| stroke(0, 1))
            .collect();
        assert_eq!(
            s.validate(),
            Err(SprayReject::TooManyStrokes(MAX_STROKES_PER_SPRAY + 1))
        );

        let mut s = spray(1, 10);
        s.strokes = vec![stroke(0, MAX_POINTS_PER_SPRAY + 1)];
        assert_eq!(
            s.validate(),
            Err(SprayReject::TooManyPoints(MAX_POINTS_PER_SPRAY + 1))
        );

        let mut s = spray(1, 10);
        s.strokes = vec![stroke(0, 0)];
        assert_eq!(s.validate(), Err(SprayReject::EmptyStroke));

        let mut s = spray(1, 10);
        s.strokes = vec![stroke(0, 4)];
        s.strokes[0].points.push(7); // X sin su Y
        assert_eq!(s.validate(), Err(SprayReject::OddPointBytes(9)));

        let mut s = spray(1, 10);
        s.strokes = vec![stroke(PALETTE_LEN, 4)];
        assert_eq!(s.validate(), Err(SprayReject::BadColor(PALETTE_LEN)));

        let mut s = spray(1, 10);
        s.size = [MAX_CANVAS_METERS + 0.5, 1.0];
        assert_eq!(s.validate(), Err(SprayReject::CanvasOutOfRange));

        let mut s = spray(1, 10);
        s.size = [0.0, 1.0];
        assert_eq!(s.validate(), Err(SprayReject::CanvasOutOfRange));

        let mut s = spray(1, 10);
        s.local_pos = [CHUNK_SIZE, 1.6, 10.0];
        assert_eq!(s.validate(), Err(SprayReject::LocalOutOfChunk));

        let mut s = spray(1, 10);
        s.local_pos = [-0.1, 1.6, 10.0];
        assert_eq!(s.validate(), Err(SprayReject::LocalOutOfChunk));
    }

    #[test]
    fn a_non_finite_field_is_rejected_before_any_range_check() {
        // NaN hace fallar toda comparación, así que sin la guarda temprana un NaN pasaría los
        // rangos y llegaría al save. Este test es la razón de que la guarda vaya la primera.
        for bad in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
            let mut s = spray(1, 10);
            s.local_pos = [bad, 1.6, 10.0];
            assert_eq!(s.validate(), Err(SprayReject::NonFinite), "local_pos {bad}");

            let mut s = spray(1, 10);
            s.size = [1.0, bad];
            assert_eq!(s.validate(), Err(SprayReject::NonFinite), "size {bad}");

            let mut s = spray(1, 10);
            s.yaw = bad;
            assert_eq!(s.validate(), Err(SprayReject::NonFinite), "yaw {bad}");
        }
    }

    #[test]
    fn placing_out_of_arm_reach_is_rejected() {
        let s = spray(1, 10);
        let w = s.world_pos();
        let close = Vec3::new(w[0] + 1.0, w[1], w[2]);
        assert_eq!(s.validate_from(close), Ok(()));

        let far = Vec3::new(w[0] + MAX_PLACE_DISTANCE + 0.5, w[1], w[2]);
        assert_eq!(s.validate_from(far), Err(SprayReject::TooFar));
    }

    #[test]
    fn painting_over_keeps_the_newest_last_in_render_order() {
        // Decisión 4: no hay merge ni borrado; el orden de render es el que decide quién tapa
        // a quién. Se insertan DESORDENADAS a propósito — el relay no garantiza orden.
        let mut store = SprayStore::new();
        for (id, tick) in [(2u32, 20u64), (1, 10), (3, 30)] {
            assert!(store.insert(spray(id, tick)).is_none());
        }
        let key = (1, -2, 0);
        let ids: Vec<u32> = store.chunk(key).iter().map(|s| s.id).collect();
        assert_eq!(ids, vec![1, 2, 3], "la más nueva debe quedar la última");
    }

    #[test]
    fn a_full_chunk_evicts_its_oldest_spray_and_never_grows() {
        let mut store = SprayStore::new();
        let key = (1, -2, 0);
        for i in 0..MAX_SPRAYS_PER_CHUNK as u32 {
            assert!(store.insert(spray(i + 1, i as u64)).is_none());
        }
        assert!(store.chunk_is_full(key));

        let evicted = store.insert(spray(999, 9_999)).expect("debe desalojar");
        assert_eq!(evicted.id, 1, "se desaloja la MÁS ANTIGUA");
        assert_eq!(
            store.chunk(key).len(),
            MAX_SPRAYS_PER_CHUNK,
            "el cap es la frontera del tamaño del save: no puede crecer"
        );
        assert_eq!(store.chunk(key).last().unwrap().id, 999);
    }

    #[test]
    fn the_cap_is_per_chunk_not_global() {
        let mut store = SprayStore::new();
        for i in 0..MAX_SPRAYS_PER_CHUNK as u32 {
            store.insert(spray(i + 1, i as u64));
        }
        let mut other = spray(10_000, 1);
        other.cx = 5;
        assert!(
            store.insert(other).is_none(),
            "un chunk lleno no debe desalojar en OTRO chunk"
        );
        assert_eq!(store.chunk((5, -2, 0)).len(), 1);
        assert_eq!(store.len(), MAX_SPRAYS_PER_CHUNK + 1);
    }

    #[test]
    fn the_layer_is_part_of_the_key() {
        // Mismo XZ, capa distinta = pared distinta. Sin la capa en la clave, pintar en el
        // sótano taparía la pintada de la planta de arriba.
        let mut store = SprayStore::new();
        store.insert(spray(1, 1));
        let mut upstairs = spray(2, 2);
        upstairs.layer = 3;
        store.insert(upstairs);
        assert_eq!(store.chunk((1, -2, 0)).len(), 1);
        assert_eq!(store.chunk((1, -2, 3)).len(), 1);
    }

    #[test]
    fn hydrating_a_save_drops_what_no_longer_validates() {
        let mut poisoned = spray(2, 20);
        poisoned.local_pos = [f32::NAN, 1.6, 10.0];
        let (store, rejected) = SprayStore::from_sprays(vec![spray(1, 10), poisoned, spray(3, 30)]);
        assert_eq!(rejected, 1);
        assert_eq!(store.len(), 2);
        let ids: Vec<u32> = store.chunk((1, -2, 0)).iter().map(|s| s.id).collect();
        assert_eq!(ids, vec![1, 3]);
    }

    #[test]
    fn hydration_truncates_an_oversized_save_keeping_the_newest() {
        // Un save por encima del cap (versión anterior con topes más flojos, o tocado a mano)
        // se sanea al cargar. Se conservan las MÁS NUEVAS: son las que el jugador ve encima.
        let sprays: Vec<Spray> = (0..MAX_SPRAYS_PER_CHUNK as u32 + 10)
            .map(|i| spray(i + 1, i as u64))
            .collect();
        let (store, rejected) = SprayStore::from_sprays(sprays);
        assert_eq!(rejected, 10);
        assert_eq!(store.chunk((1, -2, 0)).len(), MAX_SPRAYS_PER_CHUNK);
        assert_eq!(
            store.chunk((1, -2, 0)).last().unwrap().id,
            MAX_SPRAYS_PER_CHUNK as u32 + 10,
            "la más nueva del save debe sobrevivir"
        );
    }

    #[test]
    fn the_flat_view_for_persistence_is_deterministic() {
        let mut store = SprayStore::new();
        for (id, tick) in [(3u32, 30u64), (1, 10), (2, 20)] {
            let mut s = spray(id, tick);
            s.cx = id as i32; // reparte por chunks para que el orden dependa del sort, no del insert
            store.insert(s);
        }
        let ids: Vec<u32> = store.all().iter().map(|s| s.id).collect();
        assert_eq!(
            ids,
            vec![1, 2, 3],
            "el orden del fichero no puede depender del HashMap"
        );
    }

    #[test]
    fn the_worst_case_spray_stays_within_its_declared_budget() {
        // El presupuesto de ADR-068 §2 se MIDE aquí, no se estima — el borrador del ADR decía
        // ~1,2 KB y este test lo desmintió con 3,1 KB (un `Vec<[u8; 2]>` gastaba una cabecera
        // de array por punto). De ahí el `serde_bytes` del blob. Si los topes suben sin
        // recalcular esto, el presupuesto de wire y save deja de ser cierto en silencio.
        let s = Spray {
            strokes: (0..MAX_STROKES_PER_SPRAY)
                .map(|_| stroke(0, MAX_POINTS_PER_SPRAY / MAX_STROKES_PER_SPRAY))
                .collect(),
            ..spray(1, 1)
        };
        assert_eq!(s.validate(), Ok(()));
        assert_eq!(s.point_count(), MAX_POINTS_PER_SPRAY);

        let bytes = rmp_serde::to_vec_named(&s).expect("una pintada debe serializar");
        // Visible con `--nocapture`: quien afine los topes quiere el número, no solo el veredicto.
        println!("peor caso: wire={} B", bytes.len());
        assert!(
            bytes.len() <= 2048,
            "el peor caso en WIRE mide {} B, por encima del presupuesto declarado",
            bytes.len()
        );

        // El blob debe viajar como `bin`, no como array de enteros: sin esto son ~500 B más.
        assert!(
            bytes.windows(3).any(|w| w[0] == 0xc4 || w[0] == 0xc5),
            "los puntos no se codificaron como bin"
        );

        // El SAVE es JSON, no msgpack, y JSON no tiene tipo binario: el mismo blob sale como
        // lista de enteros y cuesta ~4× lo que en wire. Se fija aquí para que el número exista
        // ANTES de que el commit de persistencia lo herede, no después de ver el fichero.
        let json = serde_json::to_vec(&s).expect("una pintada debe serializar a JSON");
        println!(
            "peor caso: save={} B, chunk saturado={} KB",
            json.len(),
            json.len() * MAX_SPRAYS_PER_CHUNK / 1024
        );
        assert!(
            json.len() <= 8 * 1024,
            "el peor caso en SAVE mide {} B",
            json.len()
        );
        assert!(
            json.len() * MAX_SPRAYS_PER_CHUNK <= 512 * 1024,
            "un chunk saturado costaría {} B de save",
            json.len() * MAX_SPRAYS_PER_CHUNK
        );
    }
}
