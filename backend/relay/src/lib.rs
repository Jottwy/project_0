//! Relay UDP de respaldo — ADR-117.
//!
//! **Esto no es un servidor de juego.** No simula, no valida reglas, no conoce el mundo y no mira
//! dentro del payload: para el relay, lo que viaja entre dos peers son bytes opacos. La autoridad
//! sigue entera en el backend del host (ADR-015), y ése es exactamente el contrato que hace que
//! añadir un salto de transporte no cambie el modelo de autoridad.
//!
//! Dos módulos y una frontera clara entre ellos:
//!
//! - [`protocol`] — el sobre, sus mensajes y su codificación. Puro: cero E/S, cero tokio, cero
//!   estado. Es lo que comparte con `backrooms_server`, y es donde vive todo lo que se puede
//!   equivocar al leer bytes de la red.
//! - `session` — la tabla de sesiones y peers, la autenticación, la estrella y los timeouts.
//!
//! El binario ([`main`](../src/main.rs)) es socket y bucle: no decide nada por su cuenta.

pub mod protocol;
