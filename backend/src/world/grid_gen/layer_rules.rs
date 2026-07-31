use serde::{Deserialize, Serialize};
use std::path::Path;

/// Declarative ruleset for one macro layer.
///
/// The generation algorithm (`generate_layer`) is one function; adding a new
/// layer personality means adding one row to `LAYER_PROFILES`, not writing code.
///
/// Serializable (ADR-007): `load_profiles` reads the 4 rows from a JSON file,
/// falling back to the compiled defaults below when it is absent/invalid. The
/// `inter_layer_*`, `wall_density` and `corridor_ratio` knobs are config-only
/// for now — NOT yet wired into `generate_layer` (deferred per ADR-007).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct LayerRules {
    /// Cosmetic label. Metadata only: serialized for readability but skipped on
    /// load and re-injected by index, so it never needs to live in the JSON.
    #[serde(skip_deserializing)]
    pub name: &'static str,
    /// Probability of widening a corridor passage to 2-cell width.
    pub wide_chance: f32,
    /// Probability of opening a solid cell adjacent to 2+ floor neighbours.
    pub erode_chance: f32,
    /// Number of open-zone rectangles stamped onto the maze.
    pub num_open_zones: u32,
    /// Base side length of each open zone in cells. Square when
    /// `open_zone_size_x`/`_z` are unset (both `None`).
    pub open_zone_size: u32,
    /// Override for the X side length (cells). `None` (default) = inherit
    /// `open_zone_size`, so a profile that doesn't set this stays exactly
    /// square — zero behaviour change for every profile that doesn't opt in.
    #[serde(default)]
    pub open_zone_size_x: Option<u32>,
    /// Override for the Z side length (cells). Same default rule as
    /// `open_zone_size_x`.
    #[serde(default)]
    pub open_zone_size_z: Option<u32>,
    /// Probability of placing a pillar every 3 cells inside large open zones.
    pub pillar_chance: f32,
    pub num_anomalies: u32,
    pub num_stairs: u32,
    /// Downward connections. Not specified in §3 profile table (§3 gap);
    /// all values are 0 pending explicit confirmation.
    pub num_pits: u32,
    pub num_voids: u32,
    /// Corridor ceiling height in units of 2.5 m.
    pub ceiling_corridor: u8,
    /// Open-zone ceiling height in units of 2.5 m.
    pub ceiling_open: u8,

    // ── ADR-007 config knobs ────────────────────────────────────────────────
    // Present in the JSON but NOT yet wired into generate_layer; values are
    // inert until a follow-up commit reads them. Defaults here are arbitrary
    // (any value is retro-compatible while unwired).
    /// % of chunks with a connection to the layer above (0.0–1.0).
    pub inter_layer_up: f32,
    /// % of chunks with a connection to the layer below (0.0–1.0).
    pub inter_layer_down: f32,
    /// Target wall fraction 0.0–1.0 (intended to drive wide/erode later).
    pub wall_density: f32,
    /// Corridor vs open-zone ratio 0.0–1.0.
    pub corridor_ratio: f32,

    // ── Sesgo de rectitud de la Fase 1 ──────────────────────────────────────
    // Los dos campos llevan `serde(default)` a propósito: la
    // `generation_config.json` ya distribuida no los contiene, y sin default
    // `parse_profiles` fallaría entero y caería a los perfiles compilados —
    // una regresión silenciosa. Con default, un JSON viejo sigue siendo válido
    // y reproduce el comportamiento anterior.
    /// Probabilidad de que el backtracker de Fase 1 continúe en la MISMA
    /// dirección por la que entró al nodo, cuando esa continuación sigue sin
    /// visitar. 0.0 = sorteo uniforme entre vecinos, el comportamiento
    /// histórico: con ese valor `generate_layer` no consume NI UN draw extra
    /// del RNG, así que todo layer que lo deje a 0 genera grid byte-idéntico.
    #[serde(default = "default_straight_bias")]
    pub straight_bias: f32,
    /// Probabilidad de empujar el destino al stack del DFS (sesgo a corredores
    /// largos) en vez de descartar una entrada aleatoria (ramificación). Era el
    /// literal `0.82` incrustado en `generate_layer`; el default reproduce ese
    /// valor exacto, así que parametrizarlo no cambia ningún sorteo.
    #[serde(default = "default_branch_persistence")]
    pub branch_persistence: f32,

    // ── RoomType (Fase 4) ────────────────────────────────────────────────────
    /// Pesos (open, sealed, spine) del sorteo de tipo de zona en Fase 4.
    /// Default `(1.0, 0.0, 0.0)` = SIEMPRE Open, sin draw extra del RNG
    /// compartido — mismo principio que `straight_bias`: todo perfil que no
    /// active los otros dos pesos genera grid byte-idéntico al de antes de
    /// RoomType.
    #[serde(default = "default_room_type_weights")]
    pub room_type_weights: (f32, f32, f32),
}

fn default_straight_bias() -> f32 {
    0.0
}

fn default_branch_persistence() -> f32 {
    0.82
}

pub(super) fn default_room_type_weights() -> (f32, f32, f32) {
    (1.0, 0.0, 0.0)
}

/// Layer profiles — §3 of the design document, recalibrated in Fase 2.
///
/// Index = layer index (0 = El Vestíbulo … 3 = El Vacío).
/// For layers beyond index 3, callers should clamp or cycle as appropriate.
///
/// Recalibración Fase 2 (validación visual contra el chunk real de 20×20):
/// los valores originales de §3 (zonas de 9–12 celdas, hasta 11 por chunk)
/// borraban el laberinto en capas 1–3. Regla de calibración: el laberinto
/// SIEMPRE domina; las zonas abiertas son la excepción (respiro y sitio para
/// anomalías), nunca el tejido. El carácter de El Vacío lo dan los Voids,
/// no la ausencia de laberinto. Capa 0 es la referencia y no se tocó.
pub const LAYER_PROFILES: [LayerRules; 4] = [
    // ── Layer 0 — El Vestíbulo ──────────────────────────────────────────────
    LayerRules {
        name: "El Vestibulo",
        wide_chance: 0.10,
        erode_chance: 0.08,
        num_open_zones: 1,
        open_zone_size: 5,
        // 17.5 m: validado en dos experimentos previos (SealedRoom y
        // CorridorSpine, sesión de RoomType) antes de pasar a producción.
        open_zone_size_x: Some(7),
        open_zone_size_z: Some(7),
        pillar_chance: 0.0,
        num_anomalies: 0,
        num_stairs: 2,
        num_pits: 2, // §3 resuelto: descenso progresivo
        num_voids: 0,
        ceiling_corridor: 2, // 5 m
        ceiling_open: 2,     // 5 m
        inter_layer_up: 0.05,
        inter_layer_down: 0.10,
        wall_density: 0.4,
        corridor_ratio: 0.7,
        straight_bias: 0.0,
        branch_persistence: 0.82,
        // Producción, no experimento (esta sesión activa RoomType de verdad):
        // 50% Open (maze tradicional), 30% SealedRoom, 20% CorridorSpine.
        // Rompe a propósito los 4 PHASE1_GOLDENS de layer 0 — ver
        // DECISIONS.md (RoomType en producción) y el commit que los actualiza.
        room_type_weights: (0.5, 0.3, 0.2),
    },
    // ── Layer 1 — Las Salas ─────────────────────────────────────────────────
    LayerRules {
        name: "Las Salas",
        wide_chance: 0.30,
        erode_chance: 0.30,
        num_open_zones: 3,
        open_zone_size: 6,
        open_zone_size_x: None,
        open_zone_size_z: None,
        pillar_chance: 0.5,
        num_anomalies: 2,
        num_stairs: 2,
        num_pits: 2, // §3 resuelto: descenso progresivo
        num_voids: 1,
        ceiling_corridor: 2, // 5 m
        ceiling_open: 4,     // 10 m
        inter_layer_up: 0.10,
        inter_layer_down: 0.10,
        wall_density: 0.6,
        corridor_ratio: 0.5,
        straight_bias: 0.0,
        branch_persistence: 0.82,
        room_type_weights: (1.0, 0.0, 0.0),
    },
    // ── Layer 2 — El Caos ───────────────────────────────────────────────────
    LayerRules {
        name: "El Caos",
        wide_chance: 0.30,
        erode_chance: 0.28,
        num_open_zones: 4,
        open_zone_size: 7,
        open_zone_size_x: None,
        open_zone_size_z: None,
        pillar_chance: 0.6,
        num_anomalies: 6,
        num_stairs: 1,
        num_pits: 2, // §3 resuelto: descenso progresivo
        num_voids: 3,
        ceiling_corridor: 4, // 10 m
        ceiling_open: 6,     // 15 m
        inter_layer_up: 0.10,
        inter_layer_down: 0.15,
        wall_density: 0.8,
        corridor_ratio: 0.4,
        straight_bias: 0.0,
        branch_persistence: 0.82,
        room_type_weights: (1.0, 0.0, 0.0),
    },
    // ── Layer 3 — El Vacío ──────────────────────────────────────────────────
    LayerRules {
        name: "El Vacio",
        wide_chance: 0.20,
        erode_chance: 0.20,
        num_open_zones: 3,
        open_zone_size: 6,
        open_zone_size_x: None,
        open_zone_size_z: None,
        pillar_chance: 0.3,
        num_anomalies: 4,
        num_stairs: 0,
        num_pits: 0, // §3 resuelto: capa más profunda — los Void hacen de "abajo"
        num_voids: 14,
        ceiling_corridor: 2, // 5 m (Void cells override in Phase 6+)
        ceiling_open: 6,     // 15 m
        inter_layer_up: 0.10,
        inter_layer_down: 0.0,
        wall_density: 0.7,
        corridor_ratio: 0.5,
        straight_bias: 0.0,
        branch_persistence: 0.82,
        room_type_weights: (1.0, 0.0, 0.0),
    },
];

/// Load the 4 layer profiles from a JSON file (ADR-007). Falls back to the
/// compiled [`LAYER_PROFILES`] when the file is absent or invalid, so the
/// default behaviour is reproduced with no config present (retro-compatible).
/// Names are re-injected from the defaults by index — the JSON does not
/// configure them.
pub fn load_profiles(path: &Path) -> [LayerRules; 4] {
    match std::fs::read_to_string(path) {
        Ok(text) => parse_profiles(&text).unwrap_or(LAYER_PROFILES),
        Err(_) => LAYER_PROFILES,
    }
}

/// Parse exactly 4 profiles from a JSON array, re-injecting the static names.
/// Returns `None` (→ caller uses defaults) on malformed JSON or wrong count.
fn parse_profiles(text: &str) -> Option<[LayerRules; 4]> {
    let parsed: Vec<LayerRules> = serde_json::from_str(text).ok()?;
    if parsed.len() != 4 {
        return None;
    }
    let mut out = LAYER_PROFILES;
    for (i, mut row) in parsed.into_iter().enumerate() {
        row.name = LAYER_PROFILES[i].name; // metadata, not configured by JSON
        out[i] = row;
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_round_trip_through_json_and_reinject_names() {
        let json = serde_json::to_string(&LAYER_PROFILES.to_vec()).unwrap();
        let got = parse_profiles(&json).expect("exactly 4 profiles");
        assert_eq!(got, LAYER_PROFILES);
    }

    #[test]
    fn missing_file_falls_back_to_defaults() {
        let got = load_profiles(Path::new("definitely/missing/generation_config.json"));
        assert_eq!(got, LAYER_PROFILES);
    }

    /// La `generation_config.json` ya distribuida no lleva los campos de sesgo
    /// de rectitud. Debe seguir parseando —vía `serde(default)`— y reproducir
    /// los valores históricos, en vez de invalidar el fichero entero y caer en
    /// silencio a los perfiles compilados.
    #[test]
    fn json_without_straightness_fields_still_parses_with_historic_defaults() {
        let json = r#"[
            {"wide_chance":0.10,"erode_chance":0.08,"num_open_zones":1,"open_zone_size":5,
             "pillar_chance":0.0,"num_anomalies":0,"num_stairs":2,"num_pits":2,"num_voids":0,
             "ceiling_corridor":2,"ceiling_open":2,"inter_layer_up":0.05,"inter_layer_down":0.10,
             "wall_density":0.4,"corridor_ratio":0.7}
        ]"#;
        // Un solo perfil ⇒ `parse_profiles` lo rechaza por conteo, así que se
        // deserializa la fila suelta para aislar el contrato de serde.
        let row: LayerRules = serde_json::from_str::<Vec<LayerRules>>(json).unwrap()[0].clone();
        assert_eq!(row.straight_bias, 0.0);
        assert_eq!(row.branch_persistence, 0.82);
    }

    #[test]
    fn malformed_or_wrong_count_falls_back() {
        assert!(parse_profiles("[]").is_none());
        assert!(parse_profiles("not json at all").is_none());
    }
}
