//! ADR-115 — las marcas de saqueo: memoria PERSISTENTE de qué punto de loot ya se llevaron.
//!
//! Antes de esto, el saqueo era una **ausencia** en RAM del cliente: `_collectedItems` y
//! `_collectedCarry` viven en `ChunkLootManager` y no se persisten, y un cofre vaciado se borra
//! de `world.corpses`. Como el contenido de un chunk es una función pura de
//! `(world_seed, cx, cz)`, reiniciar volvía a sortearlo entero: el relog era un regenerador
//! universal, instantáneo y gratis.
//!
//! Aquí el saqueo pasa a ser una **presencia**: un registro con sello de tiempo que viaja en el
//! save. Lo que regenera el punto ya no es el reinicio, es la caducidad de su marca.
//!
//! ## Qué NO es
//!
//! No es el reloj de regeneración de los props desmontables (ADR-114 D5, 15 min, `Instant` en
//! memoria) ni el de los materiales de construcción (30 min, cliente). Esos regeneran **el
//! recurso**; esto sólo recuerda **el saqueo**, y ADR-115 D13 deja los dos intactos.
//!
//! ## El reloj
//!
//! `taken_at` va en **segundos de tiempo de mundo** — el mismo contador que alimenta
//! `play_time_seconds`, o sea `base + tick / TICK_HZ`. No reloj de pared: desconectar ocho horas
//! y volver equivaldría a saquear un mundo virgen, que es exactamente el agujero que este módulo
//! cierra. Y no `Time.unscaledTime` del cliente, que arranca en 0 en cada proceso — por eso
//! precisamente el sistema anterior no podía persistir nada.

use std::collections::HashSet;

use serde::{Deserialize, Serialize};

use crate::utils::Vec3;

/// ADR-115 D4 — cuánto dura una marca. **2 h de tiempo de mundo, sin medir**, declarado como
/// placeholder con la misma disciplina que los 15 min de ADR-114 D5.
///
/// Una constante y no una por familia porque el mandato del Paso 5 es *unificar* cadencias.
pub const LOOT_MARK_TTL_SECONDS: u64 = 7200;

/// ADR-115 D7 — techo duro del roster. Sin él, un jugador que recorra diez mil chunks hace crecer
/// el save sin límite; es la misma deuda que hoy arrastra `net.stp_harvestables`, que este ADR no
/// resuelve pero tampoco repite.
pub const MAX_LOOT_MARKS: usize = 4096;

/// El `slot` de una marca de cofre. ADR-115 D2 congela **un cofre por columna**, así que la
/// columna ya identifica el cofre y no hay índice que guardar; el valor existe sólo para que
/// cofres y loot suelto compartan roster, clave y poda en vez de duplicar los tres.
pub const CHEST_SLOT: i32 = -1;

/// Qué canal sorteó el punto. Va en la clave porque los canales sortean por separado y comparten
/// la tripleta `(cx, cz, slot)`: sin esto, llevarse el item del slot 2 taparía el material del
/// slot 2 de la misma columna.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum LootMarkKind {
    Item,
    Carryable,
    Chest,
}

impl LootMarkKind {
    /// Del entero que manda el cliente en `report_loot_taken`. `None` = valor desconocido, que se
    /// descarta en vez de degradar a un canal cualquiera: marcar el canal equivocado esconde loot
    /// que nadie se llevó.
    pub fn from_wire(v: i64) -> Option<Self> {
        match v {
            0 => Some(Self::Item),
            1 => Some(Self::Carryable),
            2 => Some(Self::Chest),
            _ => None,
        }
    }

    pub fn to_wire(self) -> i64 {
        match self {
            Self::Item => 0,
            Self::Carryable => 1,
            Self::Chest => 2,
        }
    }
}

/// ADR-115 D1/D2 — un punto de loot ya saqueado. La clave es **el sorteo, nunca el objeto**: el
/// net id se reasigna en cada sesión y la posición viaja por `f32` de ida y vuelta (por eso
/// `same_chest_spot` necesita medio metro de tolerancia, y una tolerancia lineal no es clave).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct LootMark {
    pub cx: i32,
    pub cz: i32,
    pub slot: i32,
    pub kind: LootMarkKind,
    /// Segundos de tiempo de mundo, ver la cabecera del módulo.
    pub taken_at: u64,
}

impl LootMark {
    fn key(&self) -> (i32, i32, i32, LootMarkKind) {
        (self.cx, self.cz, self.slot, self.kind)
    }
}

/// El roster de marcas. `Vec` y no `HashMap` porque tiene techo (`MAX_LOOT_MARKS`), porque el
/// orden estable hace que el JSON del save no baile entre guardados, y porque las dos consultas
/// que existen —la puerta de siembra de cofres y la hidratación— son raras, no de tick.
#[derive(Debug, Clone, Default)]
pub struct LootMarkStore {
    marks: Vec<LootMark>,
}

/// Qué se llevó una poda, sólo para el log y los tests.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct PruneOutcome {
    /// Marcas caducadas por TTL.
    pub expired: usize,
    /// Marcas descartadas por techo (las MÁS ANTIGUAS primero, ADR-115 D7).
    pub over_cap: usize,
}

impl PruneOutcome {
    pub fn is_empty(&self) -> bool {
        self.expired == 0 && self.over_cap == 0
    }
}

impl LootMarkStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// Hidratación desde el save. Sanea lo que carga en vez de confiar: descarta duplicados de
    /// clave **quedándose con el sello más antiguo** (misma regla que `mark`, ver D11) y aplica el
    /// techo. Un fichero editado a mano o un save de una versión con otro techo no debe poder
    /// meter un roster que la poda de tick tenga que arreglar más tarde.
    pub fn from_marks(marks: Vec<LootMark>) -> Self {
        let mut store = Self { marks };
        store.dedupe_keeping_oldest();
        store.enforce_cap();
        store
    }

    pub fn all(&self) -> &[LootMark] {
        &self.marks
    }

    pub fn len(&self) -> usize {
        self.marks.len()
    }

    pub fn is_empty(&self) -> bool {
        self.marks.is_empty()
    }

    pub fn contains(&self, kind: LootMarkKind, cx: i32, cz: i32, slot: i32) -> bool {
        self.marks.iter().any(|m| m.key() == (cx, cz, slot, kind))
    }

    /// ADR-115 D11 — **idempotente**: volver a marcar un punto ya marcado NO re-estampa
    /// `taken_at`. Sin esto, un informe repetido (y el cliente reintenta) renovaría la caducidad
    /// indefinidamente y el punto no volvería jamás. Es el fallo silencioso más probable del
    /// diseño entero, y por eso vive aquí y no en quien llama.
    ///
    /// Devuelve `true` sólo si la marca es nueva.
    pub fn mark(&mut self, kind: LootMarkKind, cx: i32, cz: i32, slot: i32, now: u64) -> bool {
        if self.contains(kind, cx, cz, slot) {
            return false;
        }
        self.marks.push(LootMark {
            cx,
            cz,
            slot,
            kind,
            taken_at: now,
        });
        true
    }

    /// ADR-115 D7 — caducidad por barrido más techo duro. Barrido y no un temporizador por marca,
    /// igual que `regenerate_harvestables`: así las marcas que llegan hidratadas del save caducan
    /// gratis, sin que nadie tenga que darlas de alta.
    pub fn prune(&mut self, now: u64) -> PruneOutcome {
        let before = self.marks.len();
        self.marks
            .retain(|m| now.saturating_sub(m.taken_at) < LOOT_MARK_TTL_SECONDS);
        let expired = before - self.marks.len();
        let over_cap = self.enforce_cap();
        PruneOutcome { expired, over_cap }
    }

    /// Las más antiguas primero: son las más próximas a caducar de todas formas, así que el techo
    /// adelanta la decisión que el TTL iba a tomar igualmente.
    fn enforce_cap(&mut self) -> usize {
        if self.marks.len() <= MAX_LOOT_MARKS {
            return 0;
        }
        let excess = self.marks.len() - MAX_LOOT_MARKS;
        // Estable: dos marcas del mismo segundo conservan el orden de inserción, así que la poda
        // es reproducible y el save no baila.
        self.marks.sort_by_key(|m| m.taken_at);
        self.marks.drain(0..excess);
        excess
    }

    fn dedupe_keeping_oldest(&mut self) {
        if self.marks.len() < 2 {
            return;
        }
        self.marks.sort_by_key(|m| m.taken_at);
        let mut seen: HashSet<(i32, i32, i32, LootMarkKind)> =
            HashSet::with_capacity(self.marks.len());
        self.marks.retain(|m| seen.insert(m.key()));
    }
}

/// La columna de un punto del mundo. `CHUNK_SIZE` (50 m) es el MISMO número en los tres sitios
/// que lo usan —`Wg3ChunkStreamer.ChunkSize`, `ChunkLootManager.Side` y esto—, y de ahí que el
/// backend pueda calcular por su cuenta la columna de un cofre sin preguntarle al cliente.
pub fn chunk_of(pos: Vec3) -> (i32, i32) {
    let side = crate::utils::CHUNK_SIZE;
    ((pos.x / side).floor() as i32, (pos.z / side).floor() as i32)
}

/// ADR-115 — marca los cofres que han DESAPARECIDO desde la ronda anterior.
///
/// Por barrido del roster y no en el camino de la toma, por la misma razón que ADR-114 D5 detecta
/// el agotamiento mirando el roster: hay más de un camino por el que un cofre se vacía (la toma
/// local del host y la petición de un joiner son dos funciones distintas) y este sitio los cubre
/// todos, incluidos los cofres que llegaron hidratados del save.
///
/// Un cofre sólo puede irse de `world.corpses` vaciándose: el mapa nunca se poda por otra razón
/// (`visible_corpse_views` filtra para ancho de banda, no borra). En la PRIMERA ronda `seen` está
/// vacío, así que nada se considera desaparecido y no hay marcas falsas al arrancar.
///
/// Devuelve cuántas marcas nuevas puso.
pub fn reconcile_chest_marks<I>(
    live_chest_chunks: I,
    seen: &mut HashSet<(i32, i32)>,
    store: &mut LootMarkStore,
    now: u64,
) -> usize
where
    I: IntoIterator<Item = (i32, i32)>,
{
    let current: HashSet<(i32, i32)> = live_chest_chunks.into_iter().collect();
    let mut marked = 0;
    for (cx, cz) in seen.difference(&current) {
        if store.mark(LootMarkKind::Chest, *cx, *cz, CHEST_SLOT, now) {
            marked += 1;
        }
    }
    *seen = current;
    marked
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mark_at(store: &mut LootMarkStore, slot: i32, now: u64) {
        store.mark(LootMarkKind::Item, 0, 0, slot, now);
    }

    #[test]
    fn una_marca_nueva_se_registra_y_se_consulta() {
        let mut store = LootMarkStore::new();
        assert!(store.mark(LootMarkKind::Item, 3, -7, 2, 100));
        assert!(store.contains(LootMarkKind::Item, 3, -7, 2));
        assert_eq!(store.len(), 1);
    }

    #[test]
    fn el_canal_forma_parte_de_la_clave() {
        let mut store = LootMarkStore::new();
        store.mark(LootMarkKind::Item, 3, -7, 2, 100);
        assert!(
            !store.contains(LootMarkKind::Carryable, 3, -7, 2),
            "el material del mismo slot NO puede quedar tapado por el item"
        );
        assert!(store.mark(LootMarkKind::Carryable, 3, -7, 2, 100));
        assert_eq!(store.len(), 2);
    }

    /// ADR-115 D11. El fallo que evita: informe repetido = caducidad que se renueva sola y un
    /// punto que no vuelve jamás.
    #[test]
    fn re_marcar_no_re_estampa_el_reloj() {
        let mut store = LootMarkStore::new();
        store.mark(LootMarkKind::Item, 1, 1, 0, 100);
        assert!(!store.mark(LootMarkKind::Item, 1, 1, 0, 5_000));
        assert_eq!(store.all()[0].taken_at, 100, "gana el PRIMER sello");

        // Y la caducidad se mide desde ese primero: a 100 + TTL ya no está.
        let out = store.prune(100 + LOOT_MARK_TTL_SECONDS);
        assert_eq!(out.expired, 1);
        assert!(store.is_empty());
    }

    #[test]
    fn caduca_a_las_dos_horas_de_mundo_y_ni_un_segundo_antes() {
        let mut store = LootMarkStore::new();
        mark_at(&mut store, 0, 1_000);

        let out = store.prune(1_000 + LOOT_MARK_TTL_SECONDS - 1);
        assert!(out.is_empty(), "un segundo antes sigue saqueado");
        assert_eq!(store.len(), 1);

        let out = store.prune(1_000 + LOOT_MARK_TTL_SECONDS);
        assert_eq!(out.expired, 1);
        assert!(store.is_empty());
    }

    #[test]
    fn el_techo_descarta_las_mas_antiguas_primero() {
        let mut store = LootMarkStore::new();
        for i in 0..(MAX_LOOT_MARKS as i32 + 10) {
            // Sello creciente con el slot: la marca i es más vieja que la i+1.
            mark_at(&mut store, i, 1_000 + i as u64);
        }
        let out = store.prune(1_000);
        assert_eq!(out.over_cap, 10);
        assert_eq!(store.len(), MAX_LOOT_MARKS);
        assert!(
            !store.contains(LootMarkKind::Item, 0, 0, 9),
            "las 10 más antiguas son las que se van"
        );
        assert!(store.contains(LootMarkKind::Item, 0, 0, 10));
    }

    #[test]
    fn hidratar_sanea_duplicados_quedandose_con_el_sello_viejo() {
        let store = LootMarkStore::from_marks(vec![
            LootMark {
                cx: 0,
                cz: 0,
                slot: 1,
                kind: LootMarkKind::Item,
                taken_at: 900,
            },
            LootMark {
                cx: 0,
                cz: 0,
                slot: 1,
                kind: LootMarkKind::Item,
                taken_at: 100,
            },
        ]);
        assert_eq!(store.len(), 1);
        assert_eq!(store.all()[0].taken_at, 100);
    }

    #[test]
    fn un_reloj_que_va_hacia_atras_no_revive_marcas() {
        let mut store = LootMarkStore::new();
        mark_at(&mut store, 0, 5_000);
        // `now` anterior al sello: `saturating_sub` da 0, la marca sigue viva y nada revienta.
        let out = store.prune(10);
        assert!(out.is_empty());
        assert_eq!(store.len(), 1);
    }

    #[test]
    fn la_columna_de_un_cofre_sale_de_los_50_m_de_chunk() {
        assert_eq!(chunk_of(Vec3::new(0.0, 0.0, 0.0)), (0, 0));
        assert_eq!(chunk_of(Vec3::new(49.9, 3.0, 49.9)), (0, 0));
        assert_eq!(chunk_of(Vec3::new(50.1, 3.0, -0.1)), (1, -1));
        assert_eq!(chunk_of(Vec3::new(-50.0, 3.0, -50.0)), (-1, -1));
    }

    #[test]
    fn la_primera_ronda_no_marca_nada() {
        let mut store = LootMarkStore::new();
        let mut seen = HashSet::new();
        let marked = reconcile_chest_marks([(1, 2), (3, 4)], &mut seen, &mut store, 100);
        assert_eq!(marked, 0, "arrancar no es vaciar cofres");
        assert_eq!(seen.len(), 2);
    }

    #[test]
    fn un_cofre_que_desaparece_deja_marca() {
        let mut store = LootMarkStore::new();
        let mut seen = HashSet::new();
        reconcile_chest_marks([(1, 2), (3, 4)], &mut seen, &mut store, 100);

        let marked = reconcile_chest_marks([(3, 4)], &mut seen, &mut store, 250);
        assert_eq!(marked, 1);
        assert!(store.contains(LootMarkKind::Chest, 1, 2, CHEST_SLOT));
        assert_eq!(store.all()[0].taken_at, 250);

        // Y no se vuelve a marcar en las rondas siguientes (ya no está en `seen`).
        let marked = reconcile_chest_marks([(3, 4)], &mut seen, &mut store, 300);
        assert_eq!(marked, 0);
        assert_eq!(store.len(), 1);
    }

    #[test]
    fn el_canal_del_cable_no_admite_valores_inventados() {
        assert_eq!(LootMarkKind::from_wire(0), Some(LootMarkKind::Item));
        assert_eq!(LootMarkKind::from_wire(1), Some(LootMarkKind::Carryable));
        assert_eq!(LootMarkKind::from_wire(2), Some(LootMarkKind::Chest));
        assert_eq!(LootMarkKind::from_wire(7), None);
        for k in [
            LootMarkKind::Item,
            LootMarkKind::Carryable,
            LootMarkKind::Chest,
        ] {
            assert_eq!(LootMarkKind::from_wire(k.to_wire()), Some(k));
        }
    }
}
