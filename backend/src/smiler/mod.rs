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
//! - **Sin `game_loop` y sin el wire de producción.** [`sandbox::run`] es el único llamador, y es
//!   un modo aparte del mismo binario (`SMILER_SANDBOX=1`, ver su doc comment) que habla por un
//!   socket de depuración propio — nunca por `PacketPayload` ni `WIRE_SCHEMA_VERSION`. Conectar
//!   esto al bucle de juego real o al wire de producción sigue siendo trabajo de una fase
//!   posterior, con su propio ADR (regla dura 7: cambiar el wire exige ADR antes de tocar código).
//!
//! No hay ADR propio todavía porque el sandbox no toca ningún protocolo existente: las seis
//! preguntas abiertas de `docs/SMILER-DESIGN.md` (luz, conductos, VFX, puertas, ataque, plan de
//! fases) siguen sin resolver y no las resuelve este código — sólo hace observable en vivo que el
//! modelo de propagación es viable, antes de comprometer ninguna de esas decisiones.

pub mod flow;
pub mod sandbox;
