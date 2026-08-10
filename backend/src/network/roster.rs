//! ADR-060 commit (d): paginación de los rosters completos.
//!
//! Los cinco rosters host-autoritativos (`StpItemList`, `StpBuildingList`, `StpCarryableList`,
//! `StpHarvestableList`, `CorpseList`) se emitían como UN datagrama con la lista entera, a 10 Hz
//! y sin fiabilidad. Al superar el límite del datagrama UDP IPv4 (65 507 B) el `send_to` falla
//! con `WSAEMSGSIZE` y la replicación de ese roster se detiene PARA SIEMPRE: no hay reintento
//! que arreglarlo, la lista solo crece, y el único rastro es el warn 1/s de `send_datagram`. El
//! doc-comment de esa función ya predecía este final para `StpBuildingList` (~800 piezas).
//!
//! Aquí viven las dos mitades del arreglo, genéricas sobre el tipo de elemento para que los cinco
//! rosters compartan mecanismo en vez de copiarlo cinco veces:
//!   * `paginate` — trocea por PRESUPUESTO DE BYTES REALES (mide cada elemento serializado), no
//!     por número de elementos: `StpBuildingInfo` lleva un `Vec` de progreso de construcción y
//!     un `CorpseView` lleva su inventario, así que dos elementos del mismo tipo pueden diferir
//!     en un orden de magnitud.
//!   * `RosterAssembler` — reensambla en el receptor y solo entrega la lista cuando está
//!     COMPLETA, conservando la semántica de hoy (reemplazo verbatim del roster entero).

use serde::Serialize;

/// Presupuesto de payload por página, en bytes. Muy por debajo de un MTU típico (1500) para
/// dejar sitio a la cabecera de 12 B, al envoltorio MessagePack del payload (nombre de variante
/// + nombres de campo) y a cualquier encapsulado de red por debajo (VPN/túnel), que es
/// exactamente lo que hace que apurar al MTU exacto vuelva a fragmentar.
pub const ROSTER_PAGE_BUDGET_BYTES: usize = 1000;

/// TECHO PRÁCTICO, MEDIDO (2026-08-10, loopback, `StpCarryableInfo`): la paginación no vuelve
/// infinito el roster, solo mueve el límite ~50×. Rondas necesarias para que el roster llegue
/// entero, con el `yield_now` entre páginas ya puesto:
///
/// | elementos | páginas | rondas |
/// |-----------|---------|--------|
/// | 4 000     | 222     | 1      |
/// | 20 000    | 1 111   | nunca (20 rondas) |
///
/// El monolito moría a ~2 200 elementos (65 507 B) y de forma PERMANENTE; esto entrega 4 000 en
/// una sola ronda. Por encima, la ráfaga vuelve a desbordar el buffer de recepción y, con
/// reensamblado todo-o-nada, ninguna generación completa — el roster deja de actualizarse
/// (aunque el joiner conserva el último completo, en vez de perderlo todo).
///
/// Cruzar ese techo pide un rediseño a deltas, que ADR-060 deja explícitamente FUERA. Los
/// órdenes de magnitud reales están muy por debajo: el doc-comment de `send_datagram` situaba el
/// primer roster en riesgo (`StpBuildingList`) en ~800 piezas.
pub const MEASURED_CONVERGENCE_CEILING_ITEMS: usize = 4000;

/// Trocea `items` en páginas cuyo contenido serializado no supera `budget` bytes.
///
/// Un elemento que por sí solo excede el presupuesto viaja SOLO en su página: partir un elemento
/// por la mitad no tiene sentido (el receptor no puede reensamblar medio `CorpseView`), y una
/// página de un elemento sigue siendo mucho menor que el roster entero. Solo si UN elemento
/// superase los 65 507 B volvería el fallo original, lo que exigiría un inventario patológico.
///
/// Una lista vacía devuelve UNA página vacía, no cero páginas: enviar el roster vacío es cómo el
/// host dice "ya no queda nada", y suprimirlo dejaría los objetos retirados vivos para siempre en
/// los joiners.
pub fn paginate<T: Serialize + Clone>(items: &[T], budget: usize) -> Vec<Vec<T>> {
    if items.is_empty() {
        return vec![Vec::new()];
    }

    let mut pages: Vec<Vec<T>> = Vec::new();
    let mut current: Vec<T> = Vec::new();
    let mut current_bytes = 0usize;

    for item in items {
        let size = rmp_serde::to_vec_named(item).map(|v| v.len()).unwrap_or(0);
        if !current.is_empty() && current_bytes + size > budget {
            pages.push(std::mem::take(&mut current));
            current_bytes = 0;
        }
        current.push(item.clone());
        current_bytes += size;
    }
    if !current.is_empty() {
        pages.push(current);
    }
    pages
}

/// Reensamblado receptor de un roster paginado, por GENERACIÓN.
///
/// El emisor estampa todas las páginas de una misma ronda con la misma `generation` (su
/// `timestamp()` en ms, resuelto una vez por ronda). El receptor acumula las páginas de una
/// generación y solo entrega la lista al completarse; una página de OTRA generación descarta lo
/// acumulado y empieza de nuevo.
///
/// **Adopción incondicional, no "la mayor gana".** Cualquier generación distinta a la actual
/// resetea, sin comparar cuál es más nueva. Dos razones: el `timestamp` es `u32` de milisegundos
/// y envuelve (~49 días), y con `>` una envoltura congelaría el roster hasta el fin de la sesión;
/// y una página reordenada de la ronda anterior, con este esquema, solo cuesta perder la ronda en
/// curso — que se rehace 100 ms después. Perder una ronda es invisible; congelar un roster no.
///
/// Esa autocuración a 10 Hz es también lo que hace innecesaria la fiabilidad aquí: una página
/// perdida deja su generación incompleta, nunca se aplica, y la siguiente ronda la sustituye
/// entera. Exactamente la propiedad que ADR-039 invocó para dejar estos cinco fuera de
/// `is_reliable`, y que este cambio conserva.
#[derive(Debug)]
pub struct RosterAssembler<T> {
    generation: u32,
    page_count: u16,
    pages: Vec<Option<Vec<T>>>,
}

/// `Default` a mano y no `derive`: el derive añadiría un `T: Default` que ninguno de los cinco
/// tipos de roster cumple (ni tiene por qué — un `CorpseData` por defecto no significa nada).
/// El estado inicial no contiene ningún `T`, así que el bound sobra.
impl<T> Default for RosterAssembler<T> {
    fn default() -> Self {
        Self {
            generation: 0,
            page_count: 0,
            pages: Vec::new(),
        }
    }
}

impl<T> RosterAssembler<T> {
    /// Acepta una página. Devuelve `Some(roster completo)` solo en la página que completa la
    /// generación — el llamador reemplaza su lista con eso y no toca nada en el resto de casos.
    pub fn accept(
        &mut self,
        generation: u32,
        page: u16,
        page_count: u16,
        items: Vec<T>,
    ) -> Option<Vec<T>> {
        if page_count == 0 || page >= page_count {
            return None; // emisor incoherente (o corrupción): se ignora, la ronda siguiente cura
        }

        if generation != self.generation || self.page_count != page_count {
            self.generation = generation;
            self.page_count = page_count;
            self.pages = (0..page_count).map(|_| None).collect();
        }

        self.pages[page as usize] = Some(items);

        if self.pages.iter().any(|p| p.is_none()) {
            return None;
        }
        // Completa: se vacía el buffer al entregar, para que una retransmisión de la última
        // página no vuelva a entregar el mismo roster.
        let complete = std::mem::take(&mut self.pages)
            .into_iter()
            .flatten()
            .flatten()
            .collect();
        self.page_count = 0;
        Some(complete)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::network::protocol::StpCarryableInfo;

    fn carryable(id: u32) -> StpCarryableInfo {
        StpCarryableInfo {
            id,
            def_id: 7,
            position: [1.0, 2.0, 3.0],
            rotation: 90.0,
        }
    }

    #[test]
    fn an_empty_roster_still_produces_one_page() {
        // Es como el host dice "ya no queda nada". Sin pagina, un objeto retirado seguiria vivo
        // en el joiner para siempre.
        let pages = paginate::<StpCarryableInfo>(&[], ROSTER_PAGE_BUDGET_BYTES);
        assert_eq!(pages.len(), 1);
        assert!(pages[0].is_empty());
    }

    #[test]
    fn a_roster_that_fits_travels_in_a_single_page() {
        let pages = paginate(&[carryable(1), carryable(2)], ROSTER_PAGE_BUDGET_BYTES);
        assert_eq!(pages.len(), 1, "dos elementos caben de sobra en 1000 B");
    }

    #[test]
    fn every_page_of_a_big_roster_stays_within_the_budget() {
        // EL invariante del commit: ninguna pagina puede acercarse al techo de 65 507 B. 4000
        // carryables son ~200 KB de roster, muy por encima del limite del datagrama.
        let items: Vec<StpCarryableInfo> = (0..4000).map(carryable).collect();
        let pages = paginate(&items, ROSTER_PAGE_BUDGET_BYTES);

        assert!(pages.len() > 1, "un roster de 4000 tiene que trocearse");
        for (i, page) in pages.iter().enumerate() {
            let encoded = rmp_serde::to_vec_named(page).unwrap().len();
            assert!(
                encoded < 1400,
                "pagina {i} ocupa {encoded} B: se acerca al MTU y volveria a fragmentar"
            );
        }
        let total: usize = pages.iter().map(|p| p.len()).sum();
        assert_eq!(total, items.len(), "no se pierde ni se duplica ningun item");
    }

    #[test]
    fn an_oversized_item_travels_alone_instead_of_being_dropped() {
        // Presupuesto absurdo: cada elemento excede por si solo. Ninguno puede desaparecer.
        let items: Vec<StpCarryableInfo> = (0..3).map(carryable).collect();
        let pages = paginate(&items, 1);
        assert_eq!(pages.len(), 3);
        assert!(pages.iter().all(|p| p.len() == 1));
    }

    #[test]
    fn the_assembler_only_delivers_once_every_page_is_in() {
        let mut asm: RosterAssembler<StpCarryableInfo> = RosterAssembler::default();
        assert!(asm.accept(100, 0, 3, vec![carryable(1)]).is_none());
        assert!(asm.accept(100, 2, 3, vec![carryable(3)]).is_none());
        let done = asm.accept(100, 1, 3, vec![carryable(2)]);
        let done = done.expect("con las tres paginas dentro, se entrega");
        assert_eq!(
            done.iter().map(|c| c.id).collect::<Vec<_>>(),
            vec![1, 2, 3],
            "y en el orden del emisor, no en el de llegada"
        );
    }

    #[test]
    fn a_lost_page_never_delivers_a_truncated_roster() {
        // El modo de fallo que importa: entregar media lista BORRARIA la mitad de los objetos
        // del joiner, que es peor que no entregar nada (lo de hoy se cura en 100 ms).
        let mut asm: RosterAssembler<StpCarryableInfo> = RosterAssembler::default();
        assert!(asm.accept(100, 0, 2, vec![carryable(1)]).is_none());
        assert!(
            asm.accept(101, 0, 2, vec![carryable(9)]).is_none(),
            "la ronda siguiente empieza limpia, sin arrastrar la pagina huerfana"
        );
        let done = asm.accept(101, 1, 2, vec![carryable(10)]).unwrap();
        assert_eq!(
            done.iter().map(|c| c.id).collect::<Vec<_>>(),
            vec![9, 10],
            "el roster entregado es el de la ronda nueva, sin restos de la anterior"
        );
    }

    #[test]
    fn a_reordered_page_from_the_previous_round_costs_a_round_not_the_roster() {
        // Adopcion incondicional: una pagina vieja resetea. Lo que NO puede pasar es que el
        // roster quede congelado — la ronda siguiente completa igual.
        let mut asm: RosterAssembler<StpCarryableInfo> = RosterAssembler::default();
        assert!(asm.accept(200, 0, 2, vec![carryable(1)]).is_none());
        assert!(asm.accept(199, 1, 2, vec![carryable(99)]).is_none());
        assert!(asm.accept(201, 0, 2, vec![carryable(5)]).is_none());
        assert!(asm.accept(201, 1, 2, vec![carryable(6)]).is_some());
    }

    #[test]
    fn the_generation_counter_wrapping_does_not_freeze_the_roster() {
        // `timestamp()` es u32 de ms y envuelve a los ~49 dias. Con "la mayor gana", a partir de
        // ahi ninguna generacion nueva se aceptaria nunca mas.
        let mut asm: RosterAssembler<StpCarryableInfo> = RosterAssembler::default();
        assert!(asm.accept(u32::MAX, 0, 1, vec![carryable(1)]).is_some());
        assert!(
            asm.accept(0, 0, 1, vec![carryable(2)]).is_some(),
            "tras la envoltura el roster tiene que seguir aplicandose"
        );
    }

    #[test]
    fn a_retransmitted_last_page_does_not_deliver_the_roster_twice() {
        let mut asm: RosterAssembler<StpCarryableInfo> = RosterAssembler::default();
        assert!(asm.accept(100, 0, 2, vec![carryable(1)]).is_none());
        assert!(asm.accept(100, 1, 2, vec![carryable(2)]).is_some());
        assert!(
            asm.accept(100, 1, 2, vec![carryable(2)]).is_none(),
            "el buffer se vacia al entregar"
        );
    }

    #[test]
    fn an_incoherent_page_index_is_ignored_instead_of_panicking() {
        // `pages[page]` con page >= page_count seria un panic en el hilo de red por un datagrama
        // de fuera. Se descarta y la ronda siguiente cura.
        let mut asm: RosterAssembler<StpCarryableInfo> = RosterAssembler::default();
        assert!(asm.accept(100, 5, 2, vec![carryable(1)]).is_none());
        assert!(asm.accept(100, 0, 0, vec![carryable(1)]).is_none());
    }
}
