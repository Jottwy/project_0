//! ADR-095 D3 — la bandera de convivencia.
//!
//! WG3 y WG2 comparten proceso hasta el borrado. Lo que decide cuál sirve el mundo se lee UNA VEZ
//! al arrancar y se pasa por parámetro a quien lo necesite: ni un global, ni un `OnceLock`, ni una
//! consulta al entorno desde dentro del bucle. R3 lo pide, y además hace que un test pueda montar
//! una configuración a mano sin tocar el entorno del proceso — que es lo que impide que dos pruebas
//! del mismo binario se pisen.

use std::sync::Arc;

use super::manifest::{self, Wg3Manifest};

/// Variable que enciende WG3. Cualquier valor distinto de `1` lo deja apagado, incluido no estar.
pub const WG3_ENABLED_ENV: &str = "BACKROOMS_WG3";

/// Lo que este proceso sabe de WG3. Barato de clonar: el manifiesto va detrás de un `Arc` porque lo
/// comparten el servidor IPC (para el saludo) y el bucle de juego (para responder chunks).
#[derive(Debug, Clone, Default)]
pub struct Wg3Config {
    enabled: bool,
    manifest: Option<Arc<Wg3Manifest>>,
}

impl Wg3Config {
    /// Configuración apagada. Es el estado por defecto y el de todo backend de test o de CI.
    pub fn disabled() -> Self {
        Self::default()
    }

    /// Para tests: enciende WG3 con un manifiesto ya cargado, sin pasar por el entorno.
    pub fn with_manifest(manifest: Wg3Manifest) -> Self {
        Self {
            enabled: true,
            manifest: Some(Arc::new(manifest)),
        }
    }

    /// Lee el entorno una sola vez.
    ///
    /// **Encendido sin manifiesto = apagado, con un error en el log.** Es la única degradación
    /// aceptable: servir WG3 sin catálogo daría chunks vacíos —un mundo sin suelo por el que se
    /// cae—, y eso es peor que quedarse en WG2. Fallar hacia el mundo que funciona, y decirlo
    /// fuerte, porque un backend que arranca callado en el modo equivocado se depura a ciegas.
    pub fn from_env() -> Self {
        let enabled = std::env::var(WG3_ENABLED_ENV)
            .map(|v| v == "1")
            .unwrap_or(false);
        if !enabled {
            return Self::disabled();
        }

        let Some(path) = manifest::manifest_path_from_env() else {
            log::error!(
                "[wg3] {WG3_ENABLED_ENV}=1 pero sin {} — WG3 queda APAGADO",
                manifest::WG3_MANIFEST_ENV
            );
            return Self::disabled();
        };

        match manifest::load_manifest(&path) {
            Some(m) => {
                log::info!(
                    "[wg3] activo: {} piezas, digest {}",
                    m.pieces.len(),
                    &m.digest[..12.min(m.digest.len())]
                );
                Self {
                    enabled: true,
                    manifest: Some(Arc::new(m)),
                }
            }
            None => {
                log::error!(
                    "[wg3] manifiesto {} no utilizable — WG3 queda APAGADO",
                    path.display()
                );
                Self::disabled()
            }
        }
    }

    /// `true` solo si está encendido Y tiene catálogo. Las dos cosas o ninguna: no existe un estado
    /// intermedio en el que se sirva medio mundo.
    pub fn is_enabled(&self) -> bool {
        self.enabled && self.manifest.is_some()
    }

    pub fn manifest(&self) -> Option<&Wg3Manifest> {
        self.manifest.as_deref()
    }

    /// Digest del catálogo cargado, o vacío. Viaja en el saludo para que el cliente pueda rechazar
    /// una partida en la que su catálogo horneado no es el del servidor — sin esa comparación, la
    /// geometría que se dibuja y la que bloquea son de mundos distintos y nada da error.
    pub fn digest(&self) -> &str {
        self.manifest
            .as_deref()
            .map(|m| m.digest.as_str())
            .unwrap_or("")
    }
}
