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
        }
    }
}

impl PlayerStats {
    /// Stats reset on (re)spawn — see ARCHITECTURE_V1.md §9.2.
    pub fn on_respawn() -> Self {
        Self {
            health: 100.0,
            hunger: 100.0,
            thirst: 100.0,
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

    /// Drain stamina (from running). Applied in the movement step where the
    /// move-state is known; `update` handles passive regeneration.
    pub fn use_stamina(&mut self, amount: f32) {
        self.stamina = (self.stamina - amount).clamp(0.0, 100.0);
    }

    /// Advance survival simulation by `dt` seconds.
    pub fn update(&mut self, dt: f32, ctx: &StatContext) {
        // Base decay.
        self.hunger -= 0.5 * dt;
        self.thirst -= 0.7 * dt;
        self.sanity -= calculate_sanity_drain(ctx) * dt;

        // Passive stamina regeneration (run-drain is applied in the movement step).
        self.stamina = (self.stamina + 8.0 * dt).clamp(0.0, 100.0);

        // Clamp to valid range.
        self.hunger = self.hunger.clamp(0.0, 100.0);
        self.thirst = self.thirst.clamp(0.0, 100.0);
        self.sanity = self.sanity.clamp(0.0, 100.0);

        // Starvation / dehydration damage.
        if self.hunger <= 0.0 {
            self.health -= 2.0 * dt;
        }
        if self.thirst <= 0.0 {
            self.health -= 3.0 * dt;
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
