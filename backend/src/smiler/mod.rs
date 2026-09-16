//! SmilerSandbox — Fase 1 de `docs/SMILER-DESIGN.md` §22: PROTOTIPO AISLADO.
//!
//! Demuestra una única cosa: que una masa gaseosa puede propagarse por el mundo de WG3
//! **respetando su ráster de colisión** (`world::wg3::raster`) — rodea macizos, sube por huecos
//! (pozos, atrios, escaleras), cruza una rendija mucho más despacio que un vano ancho, y nunca
//! aparece dentro de materia sólida. Nada más.
//!
//! **Fuera de alcance a propósito** (Joel, 2026-09-16: «Implementa únicamente FASE 1»):
//! - Sin máquina de estados de IA (`Latent/Presence/Propagation/...` de §8): [`flow::SmilerFlow::step`]
//!   toma un `target: Option<Vec3>` fijo como único sesgo de intención, no una FSM.
//! - Sin luz, sin `LightProtectionZone`, sin batería, sin manivela (§12-14).
//! - Sin VFX, sin partículas, sin sonrisa: [`flow::SmilerNode`] es un proxy de depuración
//!   (esferas grises en el sentido del plan), nunca gameplay ni wire.
//! - Sin sonido, sin ataque, sin persecución de un jugador real.
//! - **Sin wire y sin `game_loop`.** Este módulo no tiene todavía ningún llamador: es un
//!   prototipo aislado que sólo se ejercita desde sus propios tests (`cargo test`). Conectarlo al
//!   bucle de juego, al relay de poses o a cualquier entidad servida es trabajo de una fase
//!   posterior, con su propio ADR (regla dura 7: cambiar el wire exige ADR antes de tocar código).
//!
//! No hay ADR propio todavía porque no hace falta uno para un módulo que nadie llama: las seis
//! preguntas abiertas de `docs/SMILER-DESIGN.md` (luz, conductos, VFX, puertas, ataque, plan de
//! fases) siguen sin resolver y no las resuelve este código — sólo valida que el modelo de
//! propagación en sí es viable antes de comprometer ninguna de esas decisiones.

pub mod flow;
