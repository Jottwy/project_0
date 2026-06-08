//! Entity definitions and AI state machine.
//! See ARCHITECTURE_V1.md §8 and CLAUDE_CODE_INSTRUCTIONS.md Task 1.5.

use rand::Rng;
use serde::{Deserialize, Serialize};

use crate::network::PeerId;
use crate::utils::{ChunkPos, Vec3, CHUNK_SIZE};

/// Detection radius — entity enters ALERT.
const ALERT_RANGE: f32 = 20.0;
/// Chase radius — entity enters AGGRO.
const AGGRO_RANGE: f32 = 10.0;
/// Entity disengages if target leaves this radius.
const DISENGAGE_RANGE: f32 = 25.0;
/// Melee attack range.
const ATTACK_RANGE: f32 = 2.0;
/// Damage per hit.
const ATTACK_DAMAGE: f32 = 10.0;
/// Seconds between melee hits.
const ATTACK_COOLDOWN: f32 = 1.0;
/// Body stays for 30s after death.
const DESPAWN_TIME: f32 = 30.0;
/// Idle wander direction change interval bounds.
const WANDER_MIN: f32 = 3.0;
const WANDER_MAX: f32 = 8.0;
/// Alert search duration.
const SEARCH_TIME: f32 = 10.0;
/// Max wander distance from patrol center.
const WANDER_RADIUS: f32 = 15.0;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EntityType {
    Lurker,
    Crawler,
    Shadow,
}

impl EntityType {
    pub fn max_health(self) -> u8 {
        match self {
            EntityType::Lurker => 50,
            EntityType::Crawler => 30,
            EntityType::Shadow => 40,
        }
    }

    pub fn type_name(self) -> &'static str {
        match self {
            EntityType::Lurker => "lurker",
            EntityType::Crawler => "crawler",
            EntityType::Shadow => "shadow",
        }
    }

    pub fn move_speed(self) -> f32 {
        match self {
            EntityType::Lurker => 2.0,
            EntityType::Crawler => 5.0,
            EntityType::Shadow => 3.0,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EntityState {
    Idle,
    Alert {
        last_known_pos: Vec3,
        search_timer: f32,
    },
    Aggro {
        target: PeerId,
        attack_cooldown: f32,
    },
    Dead {
        despawn_timer: f32,
    },
}

impl EntityState {
    pub fn state_name(&self) -> &'static str {
        match self {
            EntityState::Idle => "idle",
            EntityState::Alert { .. } => "alert",
            EntityState::Aggro { .. } => "aggro",
            EntityState::Dead { .. } => "dead",
        }
    }
}

/// Result of an entity update tick — the game loop handles these.
pub enum EntityEvent {
    None,
    AttackPlayer { target: PeerId, damage: f32 },
    Despawned,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Entity {
    pub id: u32,
    pub entity_type: EntityType,
    pub position: Vec3,
    pub rotation: f32,
    pub health: u8,
    pub state: EntityState,
    pub patrol_center: Vec3,
    pub respawn_timer: Option<f32>,
    pub wander_dir: Vec3,
    pub wander_timer: f32,
}

impl Entity {
    pub fn new(id: u32, entity_type: EntityType, position: Vec3) -> Self {
        Self {
            id,
            entity_type,
            position,
            rotation: 0.0,
            health: entity_type.max_health(),
            state: EntityState::Idle,
            patrol_center: position,
            respawn_timer: None,
            wander_dir: Vec3::ZERO,
            wander_timer: 0.0,
        }
    }

    pub fn health_pct(&self) -> f32 {
        self.health as f32 / self.entity_type.max_health() as f32
    }

    pub fn is_alive(&self) -> bool {
        !matches!(self.state, EntityState::Dead { .. })
    }

    pub fn take_damage(&mut self, amount: u8) -> bool {
        if !self.is_alive() {
            return false;
        }
        self.health = self.health.saturating_sub(amount);
        if self.health == 0 {
            self.state = EntityState::Dead {
                despawn_timer: DESPAWN_TIME,
            };
            true
        } else {
            false
        }
    }

    /// Advance the entity AI by `dt` seconds.
    pub fn update(
        &mut self,
        dt: f32,
        player_pos: Vec3,
        player_id: PeerId,
        chunk_pos: ChunkPos,
        rng: &mut impl Rng,
    ) -> EntityEvent {
        // Work with a snapshot of the state to avoid borrow conflicts.
        let current_state = std::mem::replace(&mut self.state, EntityState::Idle);
        let (new_state, event) =
            self.step(current_state, dt, player_pos, player_id, chunk_pos, rng);
        self.state = new_state;
        event
    }

    fn step(
        &mut self,
        state: EntityState,
        dt: f32,
        player_pos: Vec3,
        player_id: PeerId,
        chunk_pos: ChunkPos,
        rng: &mut impl Rng,
    ) -> (EntityState, EntityEvent) {
        match state {
            EntityState::Dead { mut despawn_timer } => {
                despawn_timer -= dt;
                if despawn_timer <= 0.0 {
                    return (
                        EntityState::Dead { despawn_timer: 0.0 },
                        EntityEvent::Despawned,
                    );
                }
                (EntityState::Dead { despawn_timer }, EntityEvent::None)
            }
            EntityState::Idle => {
                self.tick_wander(dt, chunk_pos, rng);
                let dist = self.position.distance_xz(player_pos);
                if dist < AGGRO_RANGE {
                    (
                        EntityState::Aggro {
                            target: player_id,
                            attack_cooldown: 0.0,
                        },
                        EntityEvent::None,
                    )
                } else if dist < ALERT_RANGE {
                    (
                        EntityState::Alert {
                            last_known_pos: player_pos,
                            search_timer: SEARCH_TIME,
                        },
                        EntityEvent::None,
                    )
                } else {
                    (EntityState::Idle, EntityEvent::None)
                }
            }
            EntityState::Alert {
                mut last_known_pos,
                mut search_timer,
            } => {
                let dist_to_player = self.position.distance_xz(player_pos);
                if dist_to_player < AGGRO_RANGE {
                    return (
                        EntityState::Aggro {
                            target: player_id,
                            attack_cooldown: 0.0,
                        },
                        EntityEvent::None,
                    );
                }
                if dist_to_player < ALERT_RANGE {
                    last_known_pos = player_pos;
                    search_timer = SEARCH_TIME;
                }
                let toward = last_known_pos.sub(self.position).normalized();
                let speed = self.entity_type.move_speed();
                self.position = self.position.add(toward.scale(speed * dt));
                self.face_direction(toward);
                clamp_to_chunk(&mut self.position, chunk_pos);
                search_timer -= dt;
                if search_timer <= 0.0 {
                    (EntityState::Idle, EntityEvent::None)
                } else {
                    (
                        EntityState::Alert {
                            last_known_pos,
                            search_timer,
                        },
                        EntityEvent::None,
                    )
                }
            }
            EntityState::Aggro {
                target,
                mut attack_cooldown,
            } => {
                let dist = self.position.distance_xz(player_pos);
                if dist > DISENGAGE_RANGE {
                    return (EntityState::Idle, EntityEvent::None);
                }
                let toward = player_pos.sub(self.position).normalized();
                if dist > ATTACK_RANGE {
                    let speed = self.entity_type.move_speed() * 1.3;
                    self.position = self.position.add(toward.scale(speed * dt));
                    clamp_to_chunk(&mut self.position, chunk_pos);
                }
                self.face_direction(toward);
                attack_cooldown -= dt;
                if dist <= ATTACK_RANGE && attack_cooldown <= 0.0 {
                    attack_cooldown = ATTACK_COOLDOWN;
                    (
                        EntityState::Aggro {
                            target,
                            attack_cooldown,
                        },
                        EntityEvent::AttackPlayer {
                            target: player_id,
                            damage: ATTACK_DAMAGE,
                        },
                    )
                } else {
                    (
                        EntityState::Aggro {
                            target,
                            attack_cooldown,
                        },
                        EntityEvent::None,
                    )
                }
            }
        }
    }

    fn tick_wander(&mut self, dt: f32, chunk_pos: ChunkPos, rng: &mut impl Rng) {
        self.wander_timer -= dt;
        if self.wander_timer <= 0.0 {
            let angle = rng.gen_range(0.0..std::f32::consts::TAU);
            self.wander_dir = Vec3::new(angle.cos(), 0.0, angle.sin());
            self.wander_timer = rng.gen_range(WANDER_MIN..WANDER_MAX);
        }
        let speed = self.entity_type.move_speed() * 0.4;
        self.position = self.position.add(self.wander_dir.scale(speed * dt));
        self.face_direction(self.wander_dir);

        // Stay near patrol center
        let drift = self.position.distance_xz(self.patrol_center);
        if drift > WANDER_RADIUS {
            let back = self.patrol_center.sub(self.position).normalized();
            self.wander_dir = back;
        }
        clamp_to_chunk(&mut self.position, chunk_pos);
    }

    fn face_direction(&mut self, dir: Vec3) {
        if dir.x.abs() > f32::EPSILON || dir.z.abs() > f32::EPSILON {
            self.rotation = dir.x.atan2(dir.z).to_degrees();
        }
    }
}

fn clamp_to_chunk(pos: &mut Vec3, chunk_pos: ChunkPos) {
    let min_x = chunk_pos.0 as f32 * CHUNK_SIZE;
    let min_z = chunk_pos.1 as f32 * CHUNK_SIZE;
    pos.x = pos.x.clamp(min_x + 1.0, min_x + CHUNK_SIZE - 1.0);
    pos.z = pos.z.clamp(min_z + 1.0, min_z + CHUNK_SIZE - 1.0);
}

#[cfg(test)]
mod tests {
    use super::*;
    use rand::rngs::StdRng;
    use rand::SeedableRng;

    fn make_entity(pos: Vec3) -> Entity {
        Entity::new(1, EntityType::Lurker, pos)
    }

    #[test]
    fn idle_entity_transitions_to_alert() {
        let mut e = make_entity(Vec3::new(25.0, 0.0, 25.0));
        let mut rng = StdRng::seed_from_u64(0);
        let player = Vec3::new(30.0, 0.0, 25.0); // 5 units away — within ALERT_RANGE(20)
        e.update(0.1, player, 1, (0, 0), &mut rng);
        assert!(matches!(e.state, EntityState::Aggro { .. })); // within AGGRO_RANGE(10)
    }

    #[test]
    fn idle_entity_enters_alert_at_15_units() {
        let mut e = make_entity(Vec3::new(25.0, 0.0, 25.0));
        let mut rng = StdRng::seed_from_u64(0);
        let player = Vec3::new(40.0, 0.0, 25.0); // 15 units — ALERT not AGGRO
        e.update(0.1, player, 1, (0, 0), &mut rng);
        assert!(matches!(e.state, EntityState::Alert { .. }));
    }

    #[test]
    fn aggro_entity_attacks_at_close_range() {
        let mut e = make_entity(Vec3::new(25.0, 0.0, 25.0));
        e.state = EntityState::Aggro {
            target: 1,
            attack_cooldown: 0.0,
        };
        let mut rng = StdRng::seed_from_u64(0);
        let player = Vec3::new(26.0, 0.0, 25.0); // 1 unit — within ATTACK_RANGE(2)
        let event = e.update(0.1, player, 1, (0, 0), &mut rng);
        assert!(matches!(event, EntityEvent::AttackPlayer { .. }));
    }

    #[test]
    fn entity_disengages_when_player_far() {
        let mut e = make_entity(Vec3::new(25.0, 0.0, 25.0));
        e.state = EntityState::Aggro {
            target: 1,
            attack_cooldown: 0.0,
        };
        let mut rng = StdRng::seed_from_u64(0);
        let player = Vec3::new(60.0, 0.0, 25.0); // 35 units — > DISENGAGE_RANGE(25)
        e.update(0.1, player, 1, (0, 0), &mut rng);
        assert!(matches!(e.state, EntityState::Idle));
    }

    #[test]
    fn dead_entity_despawns() {
        let mut e = make_entity(Vec3::new(25.0, 0.0, 25.0));
        e.state = EntityState::Dead { despawn_timer: 0.5 };
        let mut rng = StdRng::seed_from_u64(0);
        let player = Vec3::new(100.0, 0.0, 100.0);
        let event = e.update(1.0, player, 1, (0, 0), &mut rng);
        assert!(matches!(event, EntityEvent::Despawned));
    }

    #[test]
    fn take_damage_kills_at_zero_hp() {
        let mut e = make_entity(Vec3::new(25.0, 0.0, 25.0));
        assert_eq!(e.health, 50); // Lurker has 50 HP
        e.take_damage(30);
        assert_eq!(e.health, 20);
        assert!(e.is_alive());
        e.take_damage(20);
        assert_eq!(e.health, 0);
        assert!(!e.is_alive());
        assert!(matches!(e.state, EntityState::Dead { .. }));
    }
}
