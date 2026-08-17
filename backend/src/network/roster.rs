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
/// y nombres de campo) y a cualquier encapsulado de red por debajo (VPN/túnel), que es
/// exactamente lo que hace que apurar al MTU exacto vuelva a fragmentar.
pub const ROSTER_PAGE_BUDGET_BYTES: usize = 1000;

// TECHO PRÁCTICO, MEDIDO (2026-08-10, loopback, `StpCarryableInfo`): la paginación no vuelve
// infinito el roster, solo mueve el límite ~50×. Rondas necesarias para que el roster llegue
// entero, con el `yield_now` entre páginas ya puesto:
//
// | elementos | páginas | rondas |
// |-----------|---------|--------|
// | 4 000     | 222     | 1      |
// | 20 000    | 1 111   | nunca (20 rondas) |
//
// El monolito moría a ~2 200 elementos (65 507 B) y de forma PERMANENTE; esto entrega 4 000 en
// una sola ronda. Por encima, la ráfaga vuelve a desbordar el buffer de recepción y, con
// reensamblado todo-o-nada, ninguna generación completa — el roster deja de actualizarse
// (aunque el joiner conserva el último completo, en vez de perderlo todo).
//
// Cruzar ese techo pide un rediseño a deltas, que ADR-060 deja explícitamente FUERA. Los
// órdenes de magnitud reales están muy por debajo: el doc-comment de `send_datagram` situaba el
// primer roster en riesgo (`StpBuildingList`) en ~800 piezas.
// (Comentario plano a propósito: documentaba `MEASURED_CONVERGENCE_CEILING_ITEMS`, retirada en
// la auditoría 2026-08-17; la medición sigue valiendo para quien toque la paginación.)

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

/// ADR-071: cada cuánto sale una ronda completa aunque el roster no haya cambiado.
///
/// NO está para detectar cambios — de eso se encarga el hash. Está para REPARAR: el transporte es
/// no-fiable a propósito (ADR-039), así que una página perdida deja su generación incompleta y solo
/// se cura cuando llega otra ronda entera. Sin este latido, un peer que pierde una página del
/// último envío se queda desincronizado para siempre.
///
/// 3 s es el compromiso: con la base seria medida en `systems/perf-baseline.md` baja el gasto en
/// régimen estacionario de 10 rondas/s a 1 cada 3 s (×30), y el peor caso de una página perdida es
/// que un objeto tarde 3 s en aparecerle a alguien — frente a no aparecer nunca.
pub const ROSTER_HEARTBEAT: std::time::Duration = std::time::Duration::from_secs(3);

/// ADR-071: decide si un roster hay que emitirlo esta ronda. Uno por roster, en el host.
///
/// Deja pasar la ronda si el CONTENIDO cambió, si entró un peer nuevo, o si toca latido. En
/// cualquier otro caso corta, y con ello se ahorra tanto el envío como el `paginate` (que es la
/// parte cara: serializa cada elemento del roster para medirlo).
/// ADR-071 (enmienda S1): rondas que se emiten SEGUIDAS tras un cambio, antes de que el gate
/// vuelva a cortar.
///
/// Sin esto el gate rompe la autocuración justo donde más importa. Hoy un roster que cambia se
/// reenvía a 10 Hz, así que una página perdida se repone 100 ms después; con "emite una vez y
/// calla", la reposición pasaba a esperar al latido de 3 s. Peor aún para un roster grande, que
/// necesita VARIAS rondas para converger (la tabla de ADR-060 d): a una ronda cada 3 s, converger
/// pasaba de medio segundo a un minuto.
///
/// Tres rondas ≈ 300 ms cubren la ventana en la que una pérdida es probable —justo después del
/// cambio, cuando el roster acaba de crecer— y siguen dejando el ahorro en un orden de magnitud.
pub const ROSTER_CHANGE_BURST: u8 = 3;

#[derive(Debug, Default)]
pub struct RosterGate {
    last_hash: Option<u64>,
    last_peers: usize,
    last_sent: Option<std::time::Instant>,
    /// Rondas que quedan de la ráfaga post-cambio. Ver `ROSTER_CHANGE_BURST`.
    repeats_left: u8,
}

impl RosterGate {
    /// `hash` es del roster serializado (ver `content_hash`). `peers` es el número de peers vivos.
    ///
    /// Actualiza el estado SOLO cuando devuelve true: si corta, el hash anterior sigue siendo el
    /// último realmente emitido, que es lo que hay que comparar. Guardarlo al cortar haría que un
    /// cambio seguido de una vuelta al valor anterior se tragara la emisión intermedia.
    pub fn should_send(
        &mut self,
        hash: u64,
        peers: usize,
        now: std::time::Instant,
        heartbeat: std::time::Duration,
    ) -> bool {
        let changed = self.last_hash != Some(hash);
        // ADR-071 decisión 4: quien acaba de entrar no tiene nada. Esperar al latido le daría un
        // mundo vacío durante segundos.
        let joined = peers > self.last_peers;
        let stale = self
            .last_sent
            .is_none_or(|t| now.duration_since(t) >= heartbeat);

        // Un cambio (o un peer nuevo) rearma la ráfaga: esta ronda y las siguientes salen a 10 Hz
        // como antes de ADR-071, que es lo que repone una página perdida en 100 ms en vez de en 3 s.
        if changed || joined {
            self.repeats_left = ROSTER_CHANGE_BURST;
        }

        if changed || joined || stale || self.repeats_left > 0 {
            self.last_hash = Some(hash);
            self.last_peers = peers;
            self.last_sent = Some(now);
            self.repeats_left = self.repeats_left.saturating_sub(1);
            return true;
        }
        // Una salida SÍ se registra aunque no emitamos: si no, `peers > last_peers` volvería a
        // dispararse cuando el siguiente entre y el conteo apenas recupere el valor viejo.
        self.last_peers = peers;
        false
    }
}

/// ADR-071 decisión 2: la huella de un roster. Sobre el serializado y no sobre un contador que
/// haya que acordarse de incrementar — olvidar un sitio de mutación produciría un cambio que no se
/// propaga NUNCA, sin error ni log, que es el peor fallo que este sistema puede tener.
///
/// El coste no es una clase de trabajo nueva: `paginate` ya serializa cada elemento en cada ronda
/// para medirlo. Cuando el gate corta, esto sustituye a aquello y sale ganando.
pub fn content_hash<T: Serialize>(items: &[T]) -> u64 {
    use std::hash::{Hash, Hasher};
    let mut h = std::collections::hash_map::DefaultHasher::new();
    match rmp_serde::to_vec_named(items) {
        Ok(bytes) => bytes.hash(&mut h),
        // Un roster que no serializa no se puede enviar igualmente; devolver un hash distinto cada
        // vez haría que el gate lo dejara pasar siempre, que es la degradación correcta (se
        // comporta como antes de ADR-071).
        Err(_) => items.len().hash(&mut h),
    }
    h.finish()
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

/// E1 fase 2 (ADR-074): reensamblado POR CELDA, el receptor del troceo espacial.
///
/// Sustituye al todo-o-nada global de `RosterAssembler` cuando el emisor manda subconjuntos. La
/// diferencia que decide la fase no es el ahorro de tráfico: hoy **una sola página perdida
/// invalida el roster ENTERO de esa ronda**, y aquí invalida solo su celda — las demás se aplican
/// igual y la incompleta conserva lo que ya tenía hasta la ronda siguiente. La granularidad es un
/// modo de fallo más pequeño.
///
/// La regla de aplicación vive entera en `accept_scope_end`, y es la que cierra la ambigüedad que
/// el troceo introduce ("¿esta celda está vacía o solo lejos?"): **no se infiere, el host manda la
/// lista de celdas en scope**.
#[derive(Debug)]
pub struct CellRosterAssembler<T> {
    /// Celdas ya completas de la generación en curso, esperando su cierre.
    staged: std::collections::HashMap<[i32; 2], Vec<T>>,
    /// Celdas a medias: páginas recibidas de una generación que aún no está completa.
    partial: std::collections::HashMap<[i32; 2], PartialCell<T>>,
    /// Lo último aplicado, por celda. Es de aquí de donde sale el roster plano que ve el resto
    /// del código, y lo que conserva una celda cuya ronda llegó rota.
    applied: std::collections::HashMap<[i32; 2], Vec<T>>,
}

#[derive(Debug)]
struct PartialCell<T> {
    generation: u32,
    page_count: u16,
    pages: Vec<Option<Vec<T>>>,
}

/// `Default` a mano y no `derive`, por lo mismo que `RosterAssembler`: el derive exigiría
/// `T: Default` y ninguno de los cinco tipos de roster lo cumple ni tiene por qué.
impl<T> Default for CellRosterAssembler<T> {
    fn default() -> Self {
        Self {
            staged: std::collections::HashMap::new(),
            partial: std::collections::HashMap::new(),
            applied: std::collections::HashMap::new(),
        }
    }
}

impl<T> CellRosterAssembler<T> {
    /// Acepta una página de una celda. No entrega nada: el roster no cambia hasta que llega el
    /// cierre de la ronda, porque hasta entonces no se sabe qué celdas debían venir.
    pub fn accept_page(
        &mut self,
        cell: [i32; 2],
        generation: u32,
        page: u16,
        page_count: u16,
        items: Vec<T>,
    ) {
        if page_count == 0 || page >= page_count {
            return; // emisor incoherente o corrupción: se ignora, la ronda siguiente cura
        }

        let entry = self.partial.entry(cell).or_insert_with(|| PartialCell {
            generation,
            page_count,
            pages: (0..page_count).map(|_| None).collect(),
        });
        // Una generación distinta (o un `page_count` distinto) descarta lo acumulado y empieza de
        // nuevo — adopción incondicional, igual que `RosterAssembler`, y por la misma razón: el
        // `timestamp` de la generación es u32 de ms y envuelve, así que "la mayor gana" congelaría
        // el roster para siempre a los ~49 días.
        if entry.generation != generation || entry.page_count != page_count {
            entry.generation = generation;
            entry.page_count = page_count;
            entry.pages = (0..page_count).map(|_| None).collect();
        }
        entry.pages[page as usize] = Some(items);

        if entry.pages.iter().all(|p| p.is_some()) {
            let complete: Vec<T> = std::mem::take(&mut entry.pages)
                .into_iter()
                .flatten()
                .flatten()
                .collect();
            self.partial.remove(&cell);
            self.staged.insert(cell, complete);
        }
    }

    /// Cierra la ronda y devuelve el roster COMPLETO y plano que el consumidor debe adoptar.
    ///
    /// La regla de tres líneas de la enmienda de ADR-074, y no hay una cuarta:
    /// - celda en scope de la que llegó contenido completo → se reemplaza;
    /// - celda en scope de la que no llegó NADA → está vacía (el host no tenía nada que mandar);
    /// - celda fuera del scope → se descarta (está lejos; si el jugador vuelve, se la remandan).
    ///
    /// Una celda en scope que llegó A MEDIAS no entra en ninguna de las tres: conserva lo que ya
    /// tenía. Se distingue de la vacía porque de aquella no llegó ninguna página y de esta sí
    /// llegaron algunas — si el host tiene contenido manda al menos una.
    pub fn accept_scope_end(&mut self, generation: u32, cells_in_scope: &[[i32; 2]]) -> Vec<T>
    where
        T: Clone,
    {
        let scope: std::collections::HashSet<[i32; 2]> = cells_in_scope.iter().copied().collect();

        for cell in &scope {
            if let Some(complete) = self.staged.remove(cell) {
                self.applied.insert(*cell, complete);
            } else if self
                .partial
                .get(cell)
                .is_none_or(|p| p.generation != generation)
            {
                // Ni completa ni a medias EN ESTA generación: el host no mandó nada de aquí.
                self.applied.insert(*cell, Vec::new());
            }
            // else: a medias en esta generación → se conserva lo aplicado, y la ronda siguiente
            // (o el heartbeat de ADR-071) la repone.
        }

        // Fuera de scope: se olvida. Es lo que impide que un jugador que cruza el mapa acumule el
        // mundo entero en memoria.
        self.applied.retain(|cell, _| scope.contains(cell));
        self.staged.retain(|cell, _| scope.contains(cell));
        self.partial.retain(|cell, _| scope.contains(cell));

        self.applied
            .values()
            .flat_map(|v| v.iter().cloned())
            .collect()
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

    /// SONDA DE MEDICIÓN, no un test — no afirma nada, imprime. `#[ignore]` a propósito: existe
    /// para responder "¿cuántos jugadores aguanta esto?" con números en vez de con intuición.
    ///
    /// ```text
    /// cargo test roster_relay_cost -- --ignored --nocapture
    /// ```
    ///
    /// Qué mide y por qué ESTO y no otra cosa: el host difunde cada roster ENTERO a 10 Hz a todos
    /// los peers, sin filtro de distancia. Ese es el único coste del proyecto que crece con el
    /// tamaño del mundo Y con el número de jugadores a la vez, así que es el que fija el techo de
    /// la palabra "MMO". El resto del loop crece con una cosa o con la otra, no con el producto.
    #[test]
    #[ignore = "sonda de medición: imprime, no afirma"]
    fn roster_relay_cost() {
        use crate::network::protocol::{StpBuildingInfo, StpItemInfo};
        use std::time::Instant;

        fn building(id: u32) -> StpBuildingInfo {
            StpBuildingInfo {
                id,
                def_id: -4996552,
                position: [12.5, 1.8, -40.0],
                rotation: 90.0,
                group_id: id / 8,
                owner_id: 0,
                added: vec![crate::network::protocol::StpBuildProgress {
                    material_id: -1234,
                    count: 4,
                }],
            }
        }
        fn item(id: u32) -> StpItemInfo {
            StpItemInfo {
                id,
                def_id: -52379,
                count: 3,
                position: [12.5, 1.8, -40.0],
                rotation: 90.0,
                settling: false,
            }
        }

        const RELAY_HZ: f64 = 10.0;
        println!("\n=== ADR-070 follow-up / sonda de coste del relay de rosters ===");
        println!("presupuesto de página: {ROSTER_PAGE_BUDGET_BYTES} B | relay: {RELAY_HZ} Hz\n");

        println!("--- tamaño serializado por elemento ---");
        let b1 = rmp_serde::to_vec_named(&building(1)).unwrap().len();
        let i1 = rmp_serde::to_vec_named(&item(1)).unwrap().len();
        let c1 = rmp_serde::to_vec_named(&carryable(1)).unwrap().len();
        println!("  StpBuildingInfo (1 material): {b1} B");
        println!("  StpItemInfo:                  {i1} B");
        println!("  StpCarryableInfo:             {c1} B\n");

        // Escenarios: "una base modesta", "una base seria", "el techo medido de ADR-060 (d)".
        for (label, n_build, n_items) in [
            ("mundo temprano", 100usize, 40usize),
            ("una base seria", 1_000, 300),
            ("mundo maduro", 4_000, 1_000),
        ] {
            let buildings: Vec<_> = (0..n_build as u32).map(building).collect();
            let items: Vec<_> = (0..n_items as u32).map(item).collect();

            let t = Instant::now();
            let bp = paginate(&buildings, ROSTER_PAGE_BUDGET_BYTES);
            let ip = paginate(&items, ROSTER_PAGE_BUDGET_BYTES);
            let paginate_us = t.elapsed().as_micros();

            let bytes: usize = bp
                .iter()
                .map(|p| rmp_serde::to_vec_named(p).unwrap().len())
                .sum::<usize>()
                + ip.iter()
                    .map(|p| rmp_serde::to_vec_named(p).unwrap().len())
                    .sum::<usize>();
            let pages = bp.len() + ip.len();
            let per_peer_kbps = bytes as f64 * RELAY_HZ / 1024.0;

            println!("--- {label}: {n_build} piezas + {n_items} items ---");
            println!("  páginas por ronda:      {pages}");
            println!("  bytes por ronda:        {bytes} B");
            println!(
                "  paginate() por ronda:   {paginate_us} µs  (×{RELAY_HZ}/s = {:.1} ms/s de CPU)",
                paginate_us as f64 * RELAY_HZ / 1000.0
            );
            println!("  POR PEER:               {per_peer_kbps:.0} KB/s");
            for peers in [4usize, 16, 32] {
                println!(
                    "    salida del host con {peers:>2} peers: {:.1} MB/s ({} datagramas/s)",
                    per_peer_kbps * peers as f64 / 1024.0,
                    pages * peers * RELAY_HZ as usize
                );
            }

            // El MISMO roster viaja además por IPC al cliente LOCAL, entero y clonado, a 10 Hz
            // (`build_world_state`: `stp_buildings: net.stp_buildings.clone()`). No es red, pero sí
            // es memoria y CPU por tick, y es lo que el replicador de Unity vuelve a recorrer entero
            // en CADA frame. Se mide aparte porque se cura aparte.
            let t = Instant::now();
            let cloned = buildings.clone();
            let clone_us = t.elapsed().as_micros();
            let ipc_bytes = rmp_serde::to_vec_named(&cloned).unwrap().len()
                + rmp_serde::to_vec_named(&items).unwrap().len();
            println!(
                "  IPC al cliente local:   {ipc_bytes} B/ronda = {:.0} KB/s (clone {clone_us} µs/ronda)",
                ipc_bytes as f64 * RELAY_HZ / 1024.0
            );

            // ADR-071 S2: el MISMO escenario, pasado por el gate. Se simulan 60 s de juego (600
            // rondas a 10 Hz) con un jugador construyendo ACTIVAMENTE — una pieza nueva cada 5 s.
            // Se cuenta cuántas rondas sobreviven al gate; el ahorro es la diferencia.
            //
            // `now` es sintético y avanza a mano: el latido mide tiempo real y un bucle cerrado
            // recorrería los 60 s simulados en microsegundos, con lo que jamás vencería y el
            // resultado saldría mejor de lo que es.
            let mut gate = RosterGate::default();
            let mut live = buildings.clone();
            let t0 = Instant::now();
            let mut sent_rounds = 0usize;
            const ROUNDS: usize = 600;
            for round in 0..ROUNDS {
                if round % 50 == 0 && round > 0 {
                    live.push(building(90_000 + round as u32)); // una pieza nueva cada 5 s
                }
                let now = t0 + std::time::Duration::from_millis(round as u64 * 100);
                if gate.should_send(content_hash(&live), 1, now, ROSTER_HEARTBEAT) {
                    sent_rounds += 1;
                }
            }
            let pct = sent_rounds as f64 * 100.0 / ROUNDS as f64;
            println!(
                "  ADR-071 (60 s, construyendo): {sent_rounds}/{ROUNDS} rondas emiten ({pct:.1} %) → \
                 {:.0} KB/s por peer (antes {per_peer_kbps:.0})",
                per_peer_kbps * pct / 100.0
            );
            println!();
        }
    }

    // ── ADR-071: un roster que no ha cambiado no se envía ──

    fn gate_send(gate: &mut RosterGate, items: &[StpCarryableInfo], peers: usize) -> bool {
        gate.should_send(
            content_hash(items),
            peers,
            std::time::Instant::now(),
            ROSTER_HEARTBEAT,
        )
    }

    /// El comportamiento entero del gate en una secuencia, que es como se lee mejor: la primera
    /// ronda sale, la ráfaga post-cambio sale, y solo DESPUÉS empieza a callar.
    #[test]
    fn an_unchanged_roster_stops_being_sent_after_its_burst() {
        let items = vec![carryable(1), carryable(2)];
        let mut gate = RosterGate::default();

        assert!(
            gate_send(&mut gate, &items, 1),
            "la primera ronda siempre sale: el gate no ha emitido nada todavía"
        );
        for i in 1..ROSTER_CHANGE_BURST {
            assert!(
                gate_send(&mut gate, &items, 1),
                "ronda {i} de la ráfaga post-cambio tiene que salir (autocuración a 10 Hz)"
            );
        }
        assert!(
            !gate_send(&mut gate, &items, 1),
            "agotada la ráfaga y sin cambios, el gate TIENE que cortar — es toda la cura"
        );
        assert!(
            !gate_send(&mut gate, &items, 1),
            "y seguir cortando mientras nadie toque el roster"
        );
    }

    /// Control positivo del anterior: en cuanto el contenido cambia de verdad, vuelve a salir. Sin
    /// esto, un gate que cortara SIEMPRE pasaría el test de arriba y rompería el juego entero.
    #[test]
    fn a_changed_roster_is_sent_again_immediately() {
        let mut items = vec![carryable(1)];
        let mut gate = RosterGate::default();
        for _ in 0..ROSTER_CHANGE_BURST {
            gate_send(&mut gate, &items, 1);
        }
        assert!(!gate_send(&mut gate, &items, 1), "preparación: ya calla");

        items.push(carryable(2)); // alguien soltó algo
        assert!(
            gate_send(&mut gate, &items, 1),
            "un roster que cambió sale en la ronda siguiente, sin esperar al latido"
        );
    }

    /// Un cambio que NO altera la longitud tiene que detectarse igual. Es el caso que se le
    /// escaparía a cualquier atajo del tipo "comparo cuántos elementos hay", y ADR-070 lo hizo
    /// cotidiano: un objeto asentándose cambia de posición en cada ronda sin cambiar la cuenta.
    #[test]
    fn a_moved_item_counts_as_a_change_even_though_the_count_is_identical() {
        let mut items = vec![carryable(1), carryable(2)];
        let mut gate = RosterGate::default();
        for _ in 0..ROSTER_CHANGE_BURST {
            gate_send(&mut gate, &items, 1);
        }
        assert!(!gate_send(&mut gate, &items, 1), "preparación: ya calla");

        items[1].position[1] += 0.5; // cae medio metro
        assert_eq!(
            items.len(),
            2,
            "la longitud NO cambia: ese es el punto del test"
        );
        assert!(
            gate_send(&mut gate, &items, 1),
            "el hash es del contenido, no de la cuenta"
        );
    }

    /// Decisión 4. Quien acaba de entrar no tiene roster ninguno; si el gate le hace esperar al
    /// latido, ve un mundo vacío durante segundos y cree que el juego está roto.
    #[test]
    fn a_new_peer_forces_a_send_even_with_nothing_changed() {
        let items = vec![carryable(1)];
        let mut gate = RosterGate::default();
        for _ in 0..ROSTER_CHANGE_BURST {
            gate_send(&mut gate, &items, 1);
        }
        assert!(!gate_send(&mut gate, &items, 1), "preparación: ya calla");

        assert!(
            gate_send(&mut gate, &items, 2),
            "un peer nuevo tiene que forzar el envío del roster entero"
        );
    }

    /// Y la contrapartida: que alguien SE VAYA no fuerza nada. Sin registrar la bajada, el conteo
    /// se quedaría alto y la siguiente entrada no se detectaría como tal.
    #[test]
    fn a_leaving_peer_neither_forces_a_send_nor_hides_the_next_join() {
        let items = vec![carryable(1)];
        let mut gate = RosterGate::default();
        for _ in 0..ROSTER_CHANGE_BURST {
            gate_send(&mut gate, &items, 2);
        }
        assert!(!gate_send(&mut gate, &items, 2), "preparación: ya calla");

        assert!(
            !gate_send(&mut gate, &items, 1),
            "que uno se vaya no obliga a reenviar nada a los que quedan"
        );
        assert!(
            gate_send(&mut gate, &items, 2),
            "pero la siguiente entrada SÍ, aunque el conteo solo esté recuperando su valor viejo"
        );
    }

    /// Decisión 3: el latido no está para detectar cambios, está para reparar páginas perdidas.
    /// Con `heartbeat = 0` se comprueba que esa vía existe y es independiente del contenido.
    #[test]
    fn the_heartbeat_sends_a_round_even_with_nothing_changed() {
        let items = vec![carryable(1)];
        let mut gate = RosterGate::default();
        let now = std::time::Instant::now();
        let hash = content_hash(&items);
        for _ in 0..ROSTER_CHANGE_BURST {
            gate.should_send(hash, 1, now, ROSTER_HEARTBEAT);
        }
        assert!(
            !gate.should_send(hash, 1, now, ROSTER_HEARTBEAT),
            "preparación: ya calla"
        );

        assert!(
            gate.should_send(hash, 1, now, std::time::Duration::ZERO),
            "vencido el latido, la ronda sale aunque el roster sea idéntico"
        );
    }

    // ─── E1 fase 2 (ADR-074): reensamblado por celda ───

    const C1: [i32; 2] = [1, 1];
    const C2: [i32; 2] = [2, 2];
    const C3: [i32; 2] = [3, 3];

    fn ids(v: &[StpCarryableInfo]) -> Vec<u32> {
        let mut out: Vec<u32> = v.iter().map(|c| c.id).collect();
        out.sort_unstable();
        out
    }

    /// El caso normal: dos celdas, ambas en scope, ambas completas.
    #[test]
    fn cells_in_scope_replace_their_own_contents() {
        let mut asm: CellRosterAssembler<StpCarryableInfo> = CellRosterAssembler::default();
        asm.accept_page(C1, 100, 0, 1, vec![carryable(1)]);
        asm.accept_page(C2, 100, 0, 1, vec![carryable(2)]);
        let roster = asm.accept_scope_end(100, &[C1, C2]);
        assert_eq!(ids(&roster), vec![1, 2]);
    }

    /// LA regla que la enmienda cierra, primera mitad: una celda EN SCOPE de la que no llegó nada
    /// está VACÍA. Sin esto, un objeto recogido por otro jugador seguiría en el mundo del cliente
    /// para siempre.
    #[test]
    fn a_cell_in_scope_with_nothing_received_is_emptied() {
        let mut asm: CellRosterAssembler<StpCarryableInfo> = CellRosterAssembler::default();
        asm.accept_page(C1, 100, 0, 1, vec![carryable(1)]);
        assert_eq!(ids(&asm.accept_scope_end(100, &[C1])), vec![1]);

        // Ronda siguiente: C1 sigue en scope pero el host ya no manda nada de ella.
        let roster = asm.accept_scope_end(101, &[C1]);
        assert!(
            roster.is_empty(),
            "en scope y sin datos significa vacía: el host no tenía nada que mandar"
        );
    }

    /// Segunda mitad de la regla: una celda FUERA de scope se descarta. Es lo que impide que un
    /// jugador que cruza el mapa acumule el mundo entero en memoria.
    #[test]
    fn a_cell_out_of_scope_is_dropped_not_kept() {
        let mut asm: CellRosterAssembler<StpCarryableInfo> = CellRosterAssembler::default();
        asm.accept_page(C1, 100, 0, 1, vec![carryable(1)]);
        asm.accept_page(C2, 100, 0, 1, vec![carryable(2)]);
        assert_eq!(ids(&asm.accept_scope_end(100, &[C1, C2])), vec![1, 2]);

        // El jugador se aleja: C1 sale del scope. La ronda emite C2 igualmente — ver
        // `a_round_the_gate_cut_must_not_arrive_as_a_scope_end` para por qué esto NO es opcional.
        asm.accept_page(C2, 101, 0, 1, vec![carryable(2)]);
        let roster = asm.accept_scope_end(101, &[C2]);
        assert_eq!(
            ids(&roster),
            vec![2],
            "lo que queda lejos se olvida; si vuelve, se lo remandan"
        );
    }

    /// **La interacción con ADR-071 que este test existe para fijar, y que se descubrió aquí:**
    /// el gate de emisión corta las rondas en las que un roster no ha cambiado. Si en una de esas
    /// rondas cortadas llegara un cierre de scope, la regla "en scope y sin datos = vacía"
    /// borraría del cliente un mundo que está perfectamente vivo.
    ///
    /// La regla que lo cierra: **el cierre pertenece a la ronda EMITIDA.** Si el gate corta, no
    /// hay páginas y tampoco cierre, y el cliente conserva lo que tiene. Este test no puede
    /// comprobar el emisor desde aquí, así que fija la otra mitad: sin `accept_scope_end` no se
    /// toca nada, por muchas rondas que pasen.
    #[test]
    fn a_round_the_gate_cut_must_not_arrive_as_a_scope_end() {
        let mut asm: CellRosterAssembler<StpCarryableInfo> = CellRosterAssembler::default();
        asm.accept_page(C1, 100, 0, 1, vec![carryable(1)]);
        assert_eq!(ids(&asm.accept_scope_end(100, &[C1])), vec![1]);

        // Diez rondas en las que el gate cortó: ni páginas ni cierre. El roster sigue intacto,
        // que es justo lo que ADR-071 promete ("solo deja de re-enviar lo que todos ya tienen").
        let still: Vec<u32> = asm.applied.values().flatten().map(|c| c.id).collect();
        assert_eq!(
            still,
            vec![1],
            "sin cierre no se aplica nada: una ronda que el gate corta no puede vaciar nada"
        );
    }

    /// **El argumento que justifica la fase entera**: una página perdida ya no invalida el roster
    /// completo, solo su celda. Antes de esto, perder una página de cualquier sitio dejaba al
    /// jugador sin NINGUNA actualización esa ronda.
    #[test]
    fn a_lost_page_only_invalidates_its_own_cell() {
        let mut asm: CellRosterAssembler<StpCarryableInfo> = CellRosterAssembler::default();
        // Ronda 100: las dos celdas llegan enteras.
        asm.accept_page(C1, 100, 0, 1, vec![carryable(1)]);
        asm.accept_page(C2, 100, 0, 1, vec![carryable(2)]);
        assert_eq!(ids(&asm.accept_scope_end(100, &[C1, C2])), vec![1, 2]);

        // Ronda 101: C1 cambia y llega entera; de C2 se pierde una de sus dos páginas.
        asm.accept_page(C1, 101, 0, 1, vec![carryable(11)]);
        asm.accept_page(C2, 101, 0, 2, vec![carryable(22)]); // falta la página 1 de 2
        let roster = asm.accept_scope_end(101, &[C1, C2]);
        assert_eq!(
            ids(&roster),
            vec![2, 11],
            "C1 se actualiza pese al fallo de C2, y C2 conserva su contenido anterior en vez de \
             vaciarse — eso es lo que la granularidad compra"
        );
    }

    /// Y la contrapartida del anterior: la celda rota se repone en la ronda siguiente, no se queda
    /// congelada. Sin esto, "conservar lo anterior" sería una fuga permanente.
    #[test]
    fn a_broken_cell_recovers_on_the_next_round() {
        let mut asm: CellRosterAssembler<StpCarryableInfo> = CellRosterAssembler::default();
        asm.accept_page(C1, 100, 0, 1, vec![carryable(1)]);
        asm.accept_scope_end(100, &[C1]);

        asm.accept_page(C1, 101, 0, 2, vec![carryable(9)]); // incompleta
        assert_eq!(ids(&asm.accept_scope_end(101, &[C1])), vec![1], "conserva");

        asm.accept_page(C1, 102, 0, 1, vec![carryable(9)]); // ronda entera
        assert_eq!(ids(&asm.accept_scope_end(102, &[C1])), vec![9], "repuesta");
    }

    /// Una celda que entra en scope por primera vez y de la que no hay nada NO puede arrastrar
    /// basura de otra: cada celda es independiente.
    #[test]
    fn a_new_empty_cell_does_not_inherit_anything() {
        let mut asm: CellRosterAssembler<StpCarryableInfo> = CellRosterAssembler::default();
        asm.accept_page(C1, 100, 0, 1, vec![carryable(1)]);
        let roster = asm.accept_scope_end(100, &[C1, C3]);
        assert_eq!(ids(&roster), vec![1], "C3 entra vacía, sin heredar de C1");
    }

    /// Igual que el assembler global: un índice de página incoherente se ignora en vez de romper
    /// el hilo de red con un panic por un datagrama de fuera.
    #[test]
    fn an_incoherent_cell_page_is_ignored_instead_of_panicking() {
        let mut asm: CellRosterAssembler<StpCarryableInfo> = CellRosterAssembler::default();
        asm.accept_page(C1, 100, 5, 2, vec![carryable(1)]);
        asm.accept_page(C1, 100, 0, 0, vec![carryable(1)]);
        assert!(asm.accept_scope_end(100, &[C1]).is_empty());
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
