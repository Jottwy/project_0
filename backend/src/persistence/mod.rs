//! Persistence domain: JSON save/load and (Phase 5) distributed save merge.

pub mod lock;
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
pub fn sanitize_player_key(raw: Option<&str>, player_name: &str) -> String {
    let key = match raw.map(sanitize_component) {
        Some(sanitized) if !sanitized.is_empty() => sanitized,
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

    #[test]
    fn sanitize_player_key_truncates_to_the_length_cap() {
        let long_raw = format!("uuid:{}", "a".repeat(200));
        let key = sanitize_player_key(Some(&long_raw), "Joel");
        assert_eq!(key.chars().count(), MAX_KEY_LEN);
    }
}
