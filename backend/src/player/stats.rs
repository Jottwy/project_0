//! Survival stats: hunger, thirst, sanity, and their gameplay consequences.
//! See ARCHITECTURE_V1.md §9 and CLAUDE_CODE_INSTRUCTIONS.md Task 1.6.

use serde::{Deserialize, Serialize};

/// Contextual factors that influence sanity drain for the current tick.
#[derive(Debug, Clone, Copy)]
pub struct StatContext {
    pub entities_visible: u32,
    pub chunk_stabilized: bool,
    pub nearby_players: u32,
    pub light_level: f32, // 0.0 (dark) .. 1.0 (bright)
}

impl Default for StatContext {
    fn default() -> Self {
        // Default: alone, in an unstabilized but well-lit chunk, no entities.
        Self {
            entities_visible: 0,
            chunk_stabilized: false,
            nearby_players: 0,
            light_level: 1.0,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PlayerStats {
    pub health: f32,
    pub hunger: f32,
    pub thirst: f32,
    pub sanity: f32,
    /// ADR-009: server-authoritative stamina (0..100). Drained by running
    /// (applied in the movement step), passively regenerated in `update`.
    pub stamina: f32,

    // Derived modifiers (recomputed each tick, broadcast to Unity for VFX).
    pub speed_modifier: f32,
    pub accuracy_modifier: f32,
    pub hallucination_intensity: f32,

    /// ADR-029 V0 invulnerability amendment: server tick (game_loop.rs's `tick` counter)
    /// until which PvP damage (`PvpDamageGrant`) is blocked for this player. 0 = no active
    /// invulnerability. Set by the "respawn_request" handler to `tick + RESPAWN_INVULN_TICKS`.
    /// NOT relayed over the wire — a remote peer's value is invisible to the host (see the
    /// amendment for the host-vs-victim enforcement split). Never touched by
    /// `report_damage`/NPC damage/fall damage — PvP-only.
    #[serde(default)]
    pub invuln_until_tick: u32,
}

impl Default for PlayerStats {
    fn default() -> Self {
        Self {
            health: 100.0,
            hunger: 100.0,
            thirst: 100.0,
            sanity: 100.0,
            stamina: 100.0,
            speed_modifier: 1.0,
            accuracy_modifier: 1.0,
            hallucination_intensity: 0.0,
            invuln_until_tick: 0,
        }
    }
}

/// Suelo de gracia de hambre/sed al reaparecer. NO es un relleno: solo se aplica a quien llega a
/// la muerte por debajo de él (ver `on_respawn`).
///
/// 25 puntos son ~59 min de sed y ~83 min de hambre con las tasas de 2026-08-17. Suficiente para
/// buscar agua sin que reaparecer sea una fuente: la única forma de tocar el suelo es haber
/// llegado a la muerte con el depósito casi vacío.
pub const RESPAWN_GRACE_HUNGER: f32 = 25.0;
pub const RESPAWN_GRACE_THIRST: f32 = 25.0;

impl PlayerStats {
    /// Stats reset on (re)spawn.
    ///
    /// HAMBRE Y SED SE CONSERVAN A TRAVÉS DE LA MUERTE, con un suelo de gracia. Antes se
    /// rellenaban a 100 y eso hacía que **suicidarse dominara cualquier economía de agua**: morir
    /// inyectaba 100 puntos de sed = 1,67 aguas de almendras, instantáneo y gratis, contra un
    /// mundo que da 4 botellas por partida. Cualquier diseño de escasez medía un balde con
    /// agujero mientras esto siguiera así.
    ///
    /// POR QUÉ CONSERVAR Y NO BAJAR A UN FIJO: un relleno fijo (50, p. ej.) sigue regalando 40
    /// puntos a quien se suicida con sed 10 — el exploit sobrevive, más pequeño. Conservando, la
    /// muerte no produce NADA de agua, y el suelo solo lo alcanza quien ya estaba a punto de
    /// morir de sed, que es el caso que hay que rescatar para no dejarlo en una espiral de
    /// reaparecer-y-volver-a-morir sin margen para buscar.
    ///
    /// Salud vuelve a 100 y cordura a 50 como siempre: eso no es un recurso acumulable del mundo.
    /// El coste de morir sigue siendo el de ADR-028 (el cadáver con tu inventario) más la
    /// recolocación de ADR-025/031.
    ///
    /// (La referencia anterior a `ARCHITECTURE_V1.md §9.2` estaba colgada: ese fichero no existe
    /// en el repo — solo `docs/ARCHITECTURE.md`, que no habla de stats de respawn.)
    pub fn on_respawn(previous: &Self) -> Self {
        Self {
            health: 100.0,
            hunger: previous.hunger.max(RESPAWN_GRACE_HUNGER),
            thirst: previous.thirst.max(RESPAWN_GRACE_THIRST),
            sanity: 50.0,
            ..Default::default()
        }
    }

    pub fn is_dead(&self) -> bool {
        self.health <= 0.0
    }

    /// Apply direct damage to health (from entities, starvation, etc.).
    pub fn take_damage(&mut self, amount: f32) {
        self.health = (self.health - amount).max(0.0);
    }

    /// ADR-030: restore hunger from consuming an item. Clamped — consuming at/near 100 is a
    /// silent no-op past the cap, never an error (see ADR-030 "desperdicio silencioso").
    pub fn restore_hunger(&mut self, amount: f32) {
        self.hunger = (self.hunger + amount).clamp(0.0, 100.0);
    }

    /// ADR-030: restore thirst from consuming an item. Same clamp semantics as `restore_hunger`.
    pub fn restore_thirst(&mut self, amount: f32) {
        self.thirst = (self.thirst + amount).clamp(0.0, 100.0);
    }

    /// ADR-030: restore health from consuming an item (e.g. Antibiotics). Same clamp semantics
    /// as `restore_hunger`; mirrors `take_damage` but in the opposite direction.
    pub fn restore_health(&mut self, amount: f32) {
        self.health = (self.health + amount).clamp(0.0, 100.0);
    }

    /// ADR-030 amendment (Almond Water): restore sanity from consuming an item. Same clamp
    /// semantics as `restore_hunger` — no other consumable restored sanity before this, only
    /// `calculate_sanity_drain`/`update` ever lowered it.
    pub fn restore_sanity(&mut self, amount: f32) {
        self.sanity = (self.sanity + amount).clamp(0.0, 100.0);
    }

    /// Drain stamina (from running). Applied in the movement step where the
    /// move-state is known; `update` handles passive regeneration.
    pub fn use_stamina(&mut self, amount: f32) {
        self.stamina = (self.stamina - amount).clamp(0.0, 100.0);
    }

    /// Advance survival simulation by `dt` seconds.
    pub fn update(&mut self, dt: f32, ctx: &StatContext) {
        // Base decay. Balance (2026-08-17): slowed another 10× (100× vs the original 0.5 / 0.7),
        // porque el objetivo de diseño cambió de "una sesión" a "una jornada administrable": con
        // 0.07/s la ventana en la que beber un agua de almendras era EFICIENTE eran 4 min 46 s
        // (por debajo pierdes la botella contra el clamp a 100 de `restore_thirst`, por encima ya
        // estás en peligro), y racionar algo con una ventana de 5 minutos no es racionar.
        //
        // Depósito lleno: hambre 100/0.005 = 20000 s = 5 h 33 min; sed 100/0.007 = 14285 s =
        // 3 h 58 min. La SED sigue siendo la restricción vinculante (muerte a 4 h 00 min 52 s
        // contando el daño de deshidratación), y eso es deliberado: la única comida alcanzable en
        // juego es el propio agua de almendras, así que un hambre vinculante sería una muerte sin
        // respuesta posible.
        //
        // ESPEJO OBLIGATORIO EN EL CLIENTE: los managers del vendor drenan en LOCAL cuando el IPC
        // está caído (`StatInterpolator` los apaga al conectar y los reenciende al perder la
        // conexión), y sus tasas viven en `_depletionSpeed` de
        // Assets/PolymindGames/STP/Prefabs/Core/STP_Player.prefab. Tocar solo este fichero deja al
        // jugador offline drenando al ritmo viejo. Clavado por `VendorStatDepletionTests`.
        // TODO(balance): playtest values.
        self.hunger -= 0.005 * dt;
        self.thirst -= 0.007 * dt;
        self.sanity -= calculate_sanity_drain(ctx) * dt;

        // Passive stamina regeneration (run-drain is applied in the movement step).
        self.stamina = (self.stamina + 8.0 * dt).clamp(0.0, 100.0);

        // Clamp to valid range.
        self.hunger = self.hunger.clamp(0.0, 100.0);
        self.thirst = self.thirst.clamp(0.0, 100.0);
        self.sanity = self.sanity.clamp(0.0, 100.0);

        // Starvation / dehydration damage. Balance (2026-07-07): softened 5× (was 2.0 / 3.0 per
        // second) so hitting empty is survivable for a while, not a fast death. TODO(balance).
        if self.hunger <= 0.0 {
            self.health -= 0.4 * dt;
        }
        if self.thirst <= 0.0 {
            self.health -= 0.6 * dt;
        }

        self.health = self.health.clamp(0.0, 100.0);

        // Consequences.
        self.speed_modifier = if self.hunger < 20.0 { 0.7 } else { 1.0 };

        self.hallucination_intensity = if self.sanity < 50.0 {
            1.0 - (self.sanity / 50.0)
        } else {
            0.0
        };

        self.accuracy_modifier = if self.sanity < 20.0 { 0.5 } else { 1.0 };
    }
}

/// Sanity drain per second given the surrounding context (capped at 2.0/sec).
pub fn calculate_sanity_drain(ctx: &StatContext) -> f32 {
    let mut drain = 0.1; // base

    if ctx.entities_visible > 0 {
        drain += 0.3 * ctx.entities_visible as f32;
    }
    if !ctx.chunk_stabilized {
        drain += 0.2;
    }
    if ctx.nearby_players == 0 {
        drain += 0.3;
    }
    if ctx.light_level < 0.3 {
        drain += 0.5;
    }

    drain.min(2.0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn restore_hunger_clamps_at_max() {
        let mut s = PlayerStats {
            hunger: 90.0,
            ..Default::default()
        };
        s.restore_hunger(50.0);
        assert_eq!(s.hunger, 100.0);
    }

    #[test]
    fn restore_thirst_clamps_at_max() {
        let mut s = PlayerStats {
            thirst: 95.0,
            ..Default::default()
        };
        s.restore_thirst(50.0);
        assert_eq!(s.thirst, 100.0);
    }

    // ── Respawn: hambre/sed se CONSERVAN (2026-08-17) ──────────────────────────────────────
    // El agujero que cierran estos tres: rellenar a 100 al reaparecer inyectaba 1,67 aguas de
    // almendras por muerte, gratis e instantáneo, contra un mundo que da 4 botellas por partida.
    // Suicidarse dominaba cualquier economía de agua.

    #[test]
    fn respawn_does_not_refill_hunger_and_thirst() {
        let dying = PlayerStats {
            health: 0.0,
            hunger: 62.0,
            thirst: 48.0,
            ..Default::default()
        };
        let fresh = PlayerStats::on_respawn(&dying);

        assert_eq!(
            fresh.hunger, 62.0,
            "morir no debe producir comida: el hambre se conserva tal cual"
        );
        assert_eq!(
            fresh.thirst, 48.0,
            "morir no debe producir agua: la sed se conserva tal cual"
        );
        assert!(
            (fresh.health - 100.0).abs() < 1e-4,
            "la salud sí vuelve a 100 — no es un recurso acumulable del mundo"
        );
        assert_eq!(fresh.sanity, 50.0, "la cordura sigue volviendo a 50");
    }

    #[test]
    fn respawn_applies_the_grace_floor_only_when_below_it() {
        // Muerto de deshidratación: sin suelo, reaparecería en 0 y volvería a morir sin margen
        // para buscar agua — espiral irrecuperable.
        let dehydrated = PlayerStats {
            health: 0.0,
            hunger: 0.0,
            thirst: 0.0,
            ..Default::default()
        };
        let fresh = PlayerStats::on_respawn(&dehydrated);
        assert_eq!(fresh.hunger, RESPAWN_GRACE_HUNGER);
        assert_eq!(fresh.thirst, RESPAWN_GRACE_THIRST);

        // Y por encima del suelo el suelo no toca nada (es un `max`, no un `set`).
        let stabbed = PlayerStats {
            health: 0.0,
            hunger: RESPAWN_GRACE_HUNGER + 1.0,
            thirst: RESPAWN_GRACE_THIRST + 1.0,
            ..Default::default()
        };
        let fresh = PlayerStats::on_respawn(&stabbed);
        assert_eq!(fresh.hunger, RESPAWN_GRACE_HUNGER + 1.0);
        assert_eq!(fresh.thirst, RESPAWN_GRACE_THIRST + 1.0);
    }

    #[test]
    fn repeated_respawns_never_ratchet_above_the_grace_floor() {
        // El bucle que cerraba el exploit: morir en cadena no puede escalar la sed. Tres muertes
        // seguidas partiendo de seco dejan exactamente el suelo, nunca más.
        let mut s = PlayerStats {
            health: 0.0,
            hunger: 0.0,
            thirst: 0.0,
            ..Default::default()
        };
        for _ in 0..3 {
            s = PlayerStats::on_respawn(&s);
            s.health = 0.0; // vuelve a morir sin beber
        }
        assert_eq!(s.thirst, RESPAWN_GRACE_THIRST);
        assert_eq!(s.hunger, RESPAWN_GRACE_HUNGER);
    }

    #[test]
    fn restore_health_clamps_at_max_and_adds_normally_below_it() {
        let mut s = PlayerStats {
            health: 40.0,
            ..Default::default()
        };
        s.restore_health(30.0);
        assert_eq!(s.health, 70.0);

        s.restore_health(55.0); // would overshoot 100 — clamp catches it
        assert_eq!(s.health, 100.0);
    }

    #[test]
    fn restore_sanity_clamps_at_max_and_adds_normally_below_it() {
        let mut s = PlayerStats {
            sanity: 80.0,
            ..Default::default()
        };
        s.restore_sanity(35.0); // would overshoot 100 — clamp catches it
        assert_eq!(s.sanity, 100.0);

        let mut s = PlayerStats {
            sanity: 40.0,
            ..Default::default()
        };
        s.restore_sanity(35.0);
        assert_eq!(s.sanity, 75.0);
    }

    // Balance (2026-08-17): lock in the decay rates, ahora 100× más lentas que las originales
    // (0.5 / 0.7), so a future tweak that silently reverts them fails here. Uses a full-health,
    // well-fed default so no clamp/damage path interferes.
    //
    // La tolerancia de 1e-4 se conserva a propósito aunque los valores sean 10× más pequeños: el
    // error de f32 a esta escala es ~6e-6, y una reversión a las tasas viejas desviaría 0.045 y
    // 0.063, o sea 450× y 630× la tolerancia. Sigue fallando ruidoso, que es para lo que está.
    #[test]
    fn base_decay_rates_are_slowed_100x() {
        let mut s = PlayerStats::default();
        s.update(1.0, &StatContext::default());
        // 100 - 0.005, 100 - 0.007 over one second.
        assert!(
            (s.hunger - 99.995).abs() < 1e-4,
            "hunger decay should be 0.005/s, got {}",
            s.hunger
        );
        assert!(
            (s.thirst - 99.993).abs() < 1e-4,
            "thirst decay should be 0.007/s, got {}",
            s.thirst
        );
    }

    // Balance (2026-07-07): lock in the softened starvation/dehydration damage (5× weaker).
    #[test]
    fn starvation_and_dehydration_damage_are_softened_5x() {
        // Both empty → both damage terms apply: 0.4 + 0.6 = 1.0 HP over one second.
        let mut s = PlayerStats {
            hunger: 0.0,
            thirst: 0.0,
            ..Default::default()
        };
        s.update(1.0, &StatContext::default());
        assert!(
            (s.health - 99.0).abs() < 1e-4,
            "empty hunger+thirst should cost 1.0 HP/s, got {}",
            s.health
        );

        // Hunger-only empty → 0.4 HP/s.
        let mut h = PlayerStats {
            hunger: 0.0,
            ..Default::default()
        };
        h.update(1.0, &StatContext::default());
        assert!(
            (h.health - 99.6).abs() < 1e-4,
            "empty hunger should cost 0.4 HP/s, got {}",
            h.health
        );
    }
}
