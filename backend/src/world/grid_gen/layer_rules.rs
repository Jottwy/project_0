/// Declarative ruleset for one macro layer.
///
/// The generation algorithm (`generate_layer`) is one function; adding a new
/// layer personality means adding one row to `LAYER_PROFILES`, not writing code.
pub struct LayerRules {
    pub name: &'static str,
    /// Probability of widening a corridor passage to 2-cell width.
    pub wide_chance: f32,
    /// Probability of opening a solid cell adjacent to 2+ floor neighbours.
    pub erode_chance: f32,
    /// Number of open-zone rectangles stamped onto the maze.
    pub num_open_zones: u32,
    /// Base side length of each open zone in cells.
    pub open_zone_size: u32,
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
        pillar_chance: 0.0,
        num_anomalies: 0,
        num_stairs: 2,
        num_pits: 2, // §3 resuelto: descenso progresivo
        num_voids: 0,
        ceiling_corridor: 2, // 5 m
        ceiling_open: 2,     // 5 m
    },
    // ── Layer 1 — Las Salas ─────────────────────────────────────────────────
    LayerRules {
        name: "Las Salas",
        wide_chance: 0.30,
        erode_chance: 0.30,
        num_open_zones: 3,
        open_zone_size: 6,
        pillar_chance: 0.5,
        num_anomalies: 2,
        num_stairs: 2,
        num_pits: 2, // §3 resuelto: descenso progresivo
        num_voids: 1,
        ceiling_corridor: 2, // 5 m
        ceiling_open: 4,     // 10 m
    },
    // ── Layer 2 — El Caos ───────────────────────────────────────────────────
    LayerRules {
        name: "El Caos",
        wide_chance: 0.30,
        erode_chance: 0.28,
        num_open_zones: 4,
        open_zone_size: 7,
        pillar_chance: 0.6,
        num_anomalies: 6,
        num_stairs: 1,
        num_pits: 2, // §3 resuelto: descenso progresivo
        num_voids: 3,
        ceiling_corridor: 4, // 10 m
        ceiling_open: 6,     // 15 m
    },
    // ── Layer 3 — El Vacío ──────────────────────────────────────────────────
    LayerRules {
        name: "El Vacio",
        wide_chance: 0.20,
        erode_chance: 0.20,
        num_open_zones: 3,
        open_zone_size: 6,
        pillar_chance: 0.3,
        num_anomalies: 4,
        num_stairs: 0,
        num_pits: 0, // §3 resuelto: capa más profunda — los Void hacen de "abajo"
        num_voids: 14,
        ceiling_corridor: 2, // 5 m (Void cells override in Phase 6+)
        ceiling_open: 6,     // 15 m
    },
];
