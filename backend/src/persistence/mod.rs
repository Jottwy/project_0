//! Persistence domain: JSON save/load and (Phase 5) distributed save merge.

pub mod lock;
pub mod player_save;
pub mod save;

/// ADR-045 Fase 1: cap on a sanitized identity key, generous for a `"uuid:{guid}"` (41 chars) or
/// a `"name:{name}"` fallback while still bounding how long a filename this can produce.
const MAX_KEY_LEN: usize = 128;

/// Whitelist ASCII alphanumeric plus `:_-` (`:` carries the `uuid:`/`name:` namespace prefix
/// ADR-045 requires) — anything else, including path separators and `.`, is dropped outright
/// rather than escaped, so there is nothing left that could reassemble into a traversal sequence.
fn sanitize_component(raw: &str) -> String {
    raw.chars()
        .filter(|c| c.is_ascii_alphanumeric() || matches!(c, ':' | '_' | '-'))
        .collect()
}

/// ADR-045 Fase 1: turns whatever the client claims as its identity into a key safe to use as a
/// filename. A `raw` that sanitizes to empty (absent, or made entirely of characters outside the
/// whitelist) falls back to `"name:{sanitized_player_name}"`; if even the player's name sanitizes
/// to empty, the last resort is `"name:player"`. Always returns something non-empty and
/// filesystem-safe, truncated to `MAX_KEY_LEN`.
///
/// The `"name:"` prefix is reserved EXCLUSIVELY for that server-derived fallback: a `raw` that
/// itself sanitizes to something starting with `"name:"` (a client claiming e.g. `"name:Joel"`
/// verbatim) is treated as if it had arrived empty, same as the absent/whitelist-empty case,
/// instead of being used as-is. Without this, a client-supplied `raw` could collide with the
/// namespace the fallback derives from the SERVER's own knowledge of `player_name` — the
/// difference between "this client's opaque id" and "whoever the server thinks is playing".
pub fn sanitize_player_key(raw: Option<&str>, player_name: &str) -> String {
    let key = match raw.map(sanitize_component) {
        Some(sanitized) if !sanitized.is_empty() && !sanitized.starts_with("name:") => sanitized,
        _ => {
            let sanitized_name = sanitize_component(player_name);
            if sanitized_name.is_empty() {
                "name:player".to_string()
            } else {
                format!("name:{sanitized_name}")
            }
        }
    };
    key.chars().take(MAX_KEY_LEN).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sanitize_player_key_is_deterministic() {
        let a = sanitize_player_key(Some("uuid:1234-5678"), "Player");
        let b = sanitize_player_key(Some("uuid:1234-5678"), "Player");
        assert_eq!(
            a, b,
            "misma raw key + mismo nombre debe devolver la misma clave dos veces"
        );
    }

    #[test]
    fn sanitize_player_key_preserves_a_well_formed_uuid_key() {
        let key = sanitize_player_key(Some("uuid:1234-5678"), "Player");
        assert_eq!(key, "uuid:1234-5678");
    }

    #[test]
    fn sanitize_player_key_falls_back_to_name_when_key_is_absent() {
        let key = sanitize_player_key(None, "Joel");
        assert_eq!(key, "name:Joel");
    }

    #[test]
    fn sanitize_player_key_falls_back_to_name_when_sanitized_result_is_empty() {
        // Only characters outside the whitelist (spaces, slashes) -> sanitizes to empty.
        let key = sanitize_player_key(Some("   /// "), "Joel");
        assert_eq!(key, "name:Joel");
    }

    #[test]
    fn sanitize_player_key_falls_back_to_last_resort_when_even_the_name_is_empty() {
        let key = sanitize_player_key(None, "   ");
        assert_eq!(key, "name:player");
    }

    #[test]
    fn sanitize_player_key_strips_characters_that_could_escape_the_save_directory() {
        let key = sanitize_player_key(Some("../../etc/passwd"), "Joel");
        assert!(
            !key.contains('/') && !key.contains('.') && !key.contains('\\'),
            "no debe sobrevivir ningun separador de ruta ni punto: {key}"
        );
    }

    /// El namespace `"name:"` es del SERVIDOR, no del cliente: un `raw` que ya sanitiza a algo con
    /// ese prefijo no debe poder colisionar con la clave que el propio fallback deriva del nombre
    /// que el servidor conoce de otro jugador.
    #[test]
    fn sanitize_player_key_does_not_let_raw_claim_the_name_namespace() {
        let claimed = sanitize_player_key(Some("name:Joel"), "Attacker");
        let real_fallback = sanitize_player_key(None, "Joel");
        assert_ne!(
            claimed, real_fallback,
            "un raw que se hace pasar por \"name:Joel\" no debe caer en la misma clave que el \
             fallback real de un jugador anónimo llamado Joel"
        );
        // Cae en SU PROPIO fallback (derivado de su propio player_name), no en el raw declarado.
        assert_eq!(claimed, sanitize_player_key(None, "Attacker"));
    }

    #[test]
    fn sanitize_player_key_truncates_to_the_length_cap() {
        let long_raw = format!("uuid:{}", "a".repeat(200));
        let key = sanitize_player_key(Some(&long_raw), "Joel");
        assert_eq!(key.chars().count(), MAX_KEY_LEN);
    }
}
