//! ADR-094 E1a+E1b — Faceling adultos: Working/Commute/Regard/Enforce, y el carril de daño (el
//! branch en `process_pvp_hit_candidate_host`, `game_loop.rs`) que lo alimenta. Sin niños (E2+),
//! sin que el adulto devuelva el golpe todavía (E1c) y sin modelo (E5, pendiente de los Meshy de
//! Joel) — entra inerte del lado visual, como ADR-093 E0/E1.
//!
//! Reutiliza infraestructura del robapieles a propósito (ADR-094 punto 1: "toda la
//! infraestructura de criatura construida para el robapieles es reutilizable tal cual"), pero
//! como funciones libres y un driver PROPIO — no como variantes nuevas de `PhantomState`/
//! `PhantomMover`. Las dos especies no comparten ni un solo campo de comportamiento (el
//! robapieles tiene hambre, cono de percepción, intercepción; un adulto no percibe nada salvo
//! "hay alguien cerca" y "me han pegado") y forzarlas al mismo enum habría significado un
//! `match` exhaustivo por todo `phantom.rs` decidiendo combinaciones que nunca ocurren.

use super::*;

use std::collections::HashSet;

use crate::world::faceling_spawn;
use crate::world::grid_gen::{is_walkable_grid_gen, steer_around_walls, CELL_SIZE_M, CHUNK_CELLS};

/// Same cadence as `PHANTOM_POPULATION_SYNC_INTERVAL` — no reason to reconcile more often than
/// once a second for a population this cheap to scan.
const FACELING_POPULATION_SYNC_INTERVAL: f32 = 1.0;

/// v1 PLACEHOLDER, unmeasured — ADR-094 point 5 flags density/radii as "por medir con sonda"
/// exactly like ADR-043's own table did before ITS measurement pass. An office chunk is 50 m
/// (`CELL_SIZE_M * CHUNK_CELLS`) on a side, so 70 m from a player already reaches one from an
/// adjacent hallway before they round the corner.
const FACELING_ACTIVATE_RADIUS: f32 = 70.0;
/// Hysteresis gap above `FACELING_ACTIVATE_RADIUS`, same shape as `PHANTOM_DEACTIVATE_RADIUS`.
const FACELING_DEACTIVATE_RADIUS: f32 = 100.0;
/// A floor as well as a ceiling, same reason as `PHANTOM_MIN_SPAWN_DISTANCE`: nothing should
/// pop into a player's face. Smaller than the robapieles' 35 m — an adult is not a threat.
const FACELING_MIN_SPAWN_DISTANCE: f32 = 10.0;
/// Cap on simultaneously-simulated adults. v1 PLACEHOLDER.
const FACELING_ACTIVE_CAP: usize = 32;

/// ADR-094 point 2: "un jugador entra en radio ⇒ TODOS los adultos de la sala paran A LA VEZ".
const FACELING_REGARD_RADIUS: f32 = 12.0;
const FACELING_REGARD_MIN_S: f32 = 2.0;
const FACELING_REGARD_MAX_S: f32 = 4.0;

const FACELING_WALK_SPEED: f32 = 1.0;
const FACELING_ARRIVE_EPS: f32 = 0.4;
const FACELING_COMMUTE_MIN_S: f32 = 20.0;
const FACELING_COMMUTE_MAX_S: f32 = 45.0;
/// A chunk with no walkable cell found this attempt (rare — office floors are mostly open) tries
/// again soon rather than camping `Working` forever on a fluke.
const FACELING_PUESTO_RETRY_S: f32 = 5.0;

/// v1 PLACEHOLDER, unmeasured. An adult is atrezzo first, threat second — low on purpose so a
/// couple of solid hits already ends the encounter one way or the other.
const FACELING_ADULT_MAX_HEALTH: u8 = 30;
/// ADR-094 point 2: "convergen... a paso lento" — brisker than the ambient `Commute` walk (this
/// is the office coming for you), still not a sprint.
const FACELING_ENFORCE_SPEED: f32 = 1.8;
/// ADR-094 point 2: "a los ~45 s sin verlo vuelven a Working". Counts down while the aggressor is
/// out of `FACELING_REGARD_RADIUS` of the converging adult; refreshed to this value every tick it
/// is back in range, so a fight that keeps you close never times out mid-swing.
const FACELING_ENFORCE_COOLOFF_S: f32 = 45.0;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum AdultState {
    Working,
    Commute,
    Regard,
    Enforce,
}

pub(super) struct AdultMover {
    pub(super) id: PeerId,
    pub(super) home_chunk: (i32, i32),
    pub(super) layer: u8,
    pub(super) state: AdultState,
    pub(super) heading: f32,
    pub(super) commute_target: Vec3,
    /// `Regard`: seconds left staring. `Working`: seconds until the next `Commute` roll.
    /// `Enforce`: seconds left since the aggressor was last within regard range (ADR-094's
    /// "~45 s sin verlo").
    pub(super) state_timer: f32,
    pub(super) health: u8,
    /// Set on entering `Enforce`, cleared on leaving it. `None` in every other state — never
    /// stale, because nothing reads it outside the arm that owns it.
    pub(super) enforce_target: Option<PeerId>,
}

pub(super) struct AdultDriver {
    pub(super) grid_cache: GridGenChunkCache,
    pub(super) movers: Vec<AdultMover>,
    pub(super) density_scale: f32,
    population_sync_in: f32,
}

/// World-space bounds of chunk `(cx, cz)`, in the XZ plane. The leash boundary — `Commute` never
/// samples and never steps outside it, which is what makes the office the zone (ADR-094 point 2,
/// rejected alternative D).
pub(super) fn chunk_bounds(chunk: (i32, i32)) -> (f32, f32, f32, f32) {
    let size = CELL_SIZE_M * CHUNK_CELLS as f32;
    (
        chunk.0 as f32 * size,
        (chunk.0 + 1) as f32 * size,
        chunk.1 as f32 * size,
        (chunk.1 + 1) as f32 * size,
    )
}

pub(super) fn pos_in_chunk(pos: Vec3, chunk: (i32, i32)) -> bool {
    let (x0, x1, z0, z1) = chunk_bounds(chunk);
    pos.x >= x0 && pos.x < x1 && pos.z >= z0 && pos.z < z1
}

/// Picks a walkable "puesto" inside `chunk` — plain uniform sampling, not
/// `phantom.rs::pick_lurk_spot`'s deterministic bearings: nothing here needs to be re-derived
/// (unlike a hiding spot, an office desk has no observer to stay consistent for), so a few random
/// tries and a `None` on bad luck (retried next reconcile) is simpler and just as correct.
fn pick_puesto(cache: &mut GridGenChunkCache, chunk: (i32, i32), layer: u8) -> Option<Vec3> {
    let (x0, x1, z0, z1) = chunk_bounds(chunk);
    let y = crate::world::grid_gen::grid_floor_y(layer) + crate::world::collision::PLAYER_BASE_Y;
    for _ in 0..12 {
        let x = x0 + rand::random::<f32>() * (x1 - x0);
        let z = z0 + rand::random::<f32>() * (z1 - z0);
        let candidate = Vec3::new(x, y, z);
        if is_walkable_grid_gen(cache, candidate, layer) {
            return Some(candidate);
        }
    }
    None
}

impl AdultDriver {
    pub(super) fn new(world_seed: u64) -> Self {
        Self {
            grid_cache: GridGenChunkCache::with_rules(
                world_seed,
                crate::world::zone_density::rules_for,
            ),
            movers: Vec::new(),
            density_scale: 1.0,
            population_sync_in: 0.0, // reconcile on the very first entity tick
        }
    }

    /// ADR-043-shaped reconcile, gated to `ZONE_OFFICE` chunks via `faceling_spawn`. Same
    /// retire/wake shape as `PhantomDriver::sync_population`, scoped to CHUNKS instead of 200 m
    /// blocks — see `faceling_spawn`'s module doc for why an office chunk needs no coarser unit.
    pub(super) fn sync_population(
        &mut self,
        net: &mut NetworkManager,
        host_player_pos: Vec3,
        dt: f32,
    ) {
        self.population_sync_in -= dt;
        if self.population_sync_in > 0.0 {
            return;
        }
        self.population_sync_in = FACELING_POPULATION_SYNC_INTERVAL;

        let players: Vec<Vec3> = std::iter::once(host_player_pos)
            .chain(
                net.peers
                    .iter()
                    .filter(|(id, p)| {
                        !net.is_phantom(**id) && !net.is_faceling(**id) && !p.relay_only
                    })
                    .map(|(_, p)| Vec3::from_array(p.position)),
            )
            .collect();

        // ── Put away the ones nobody is near any more ──
        let mut retired: Vec<PeerId> = Vec::new();
        for m in &self.movers {
            let Some(peer) = net.peers.get(&m.id) else {
                continue;
            };
            let here = Vec3::from_array(peer.position);
            let far = players.iter().all(|p| {
                world_pos_to_layer(p.y) != m.layer
                    || p.distance_xz(here) > FACELING_DEACTIVATE_RADIUS
            });
            if far {
                retired.push(m.id);
            }
        }
        for id in &retired {
            net.despawn_faceling(*id);
        }
        self.movers.retain(|m| !retired.contains(&m.id));

        // ── Wake up the ones somebody walked near ──
        if self.movers.len() >= FACELING_ACTIVE_CAP {
            return;
        }
        let taken: HashSet<(i32, i32)> = self.movers.iter().map(|m| m.home_chunk).collect();
        let mut seen_chunks: HashSet<((i32, i32), u8)> = HashSet::new();
        let mut drawn: Vec<[f32; 3]> = Vec::new();

        for p in &players {
            let layer = world_pos_to_layer(p.y);
            let cell = CELL_SIZE_M * CHUNK_CELLS as f32;
            let cx0 = ((p.x - FACELING_ACTIVATE_RADIUS) / cell).floor() as i32;
            let cx1 = ((p.x + FACELING_ACTIVATE_RADIUS) / cell).floor() as i32;
            let cz0 = ((p.z - FACELING_ACTIVATE_RADIUS) / cell).floor() as i32;
            let cz1 = ((p.z + FACELING_ACTIVATE_RADIUS) / cell).floor() as i32;
            for cx in cx0..=cx1 {
                for cz in cz0..=cz1 {
                    if taken.contains(&(cx, cz)) || !seen_chunks.insert(((cx, cz), layer)) {
                        continue;
                    }
                    faceling_spawn::draw_adults_into(
                        net.world_seed,
                        cx,
                        cz,
                        layer,
                        self.density_scale,
                        &mut drawn,
                    );
                    if drawn.is_empty() {
                        continue;
                    }
                    // One office worth of adults wakes or sleeps AS A UNIT (ADR-094: the office is
                    // the zone), so only the CLOSEST drawn spot needs the radius/min-distance
                    // gate — if it qualifies, the whole chunk populates in one pass below.
                    let closest = drawn
                        .iter()
                        .map(|pos| p.distance_xz(Vec3::from_array(*pos)))
                        .fold(f32::INFINITY, f32::min);
                    if closest > FACELING_ACTIVATE_RADIUS {
                        continue;
                    }
                    if players.iter().any(|q| {
                        q.distance_xz(Vec3::from_array(drawn[0])) < FACELING_MIN_SPAWN_DISTANCE
                    }) {
                        continue;
                    }
                    for pos in drawn.iter().copied() {
                        if self.movers.len() >= FACELING_ACTIVE_CAP {
                            break;
                        }
                        let id = net.spawn_faceling("Faceling", pos, 1);
                        let spawn_pos = net
                            .peers
                            .get(&id)
                            .map(|p| Vec3::from_array(p.position))
                            .unwrap_or_else(|| Vec3::from_array(pos));
                        self.movers.push(AdultMover {
                            id,
                            home_chunk: (cx, cz),
                            layer,
                            state: AdultState::Working,
                            heading: rand::random::<f32>() * std::f32::consts::TAU,
                            commute_target: spawn_pos,
                            state_timer: FACELING_COMMUTE_MIN_S
                                + rand::random::<f32>()
                                    * (FACELING_COMMUTE_MAX_S - FACELING_COMMUTE_MIN_S),
                            health: FACELING_ADULT_MAX_HEALTH,
                            enforce_target: None,
                        });
                        info!(
                            "MPTRACE step=FL_POP event=faceling_adult_spawned faceling_id={} chunk=({},{}) layer={}",
                            id, cx, cz, layer
                        );
                    }
                }
            }
        }
    }

    /// One entity tick for every active adult. No perception cone, no sound, no light (ADR-094
    /// point 2: "no perciben crouch, luz ni ruido") — the only two inputs are "is anyone within
    /// `FACELING_REGARD_RADIUS`" (this method) and "did anyone hit me" (`apply_damage`, called
    /// from the game loop's PvP handling, not from here).
    pub(super) fn step(&mut self, net: &mut NetworkManager, dt: f32, host_player_pos: Vec3) {
        let players: Vec<Vec3> = std::iter::once(host_player_pos)
            .chain(
                net.peers
                    .iter()
                    .filter(|(id, p)| {
                        !net.is_phantom(**id) && !net.is_faceling(**id) && !p.relay_only
                    })
                    .map(|(_, p)| Vec3::from_array(p.position)),
            )
            .collect();

        // Pre-pass: which offices have somebody in Regard range THIS tick — computed once so
        // every adult in the same room reacts on the same frame (ADR-094: "TODOS... A LA VEZ"),
        // not one tick apart depending on iteration order.
        let mut regarded: HashSet<(i32, i32)> = HashSet::new();
        for m in &self.movers {
            if regarded.contains(&m.home_chunk) {
                continue;
            }
            let Some(peer) = net.peers.get(&m.id) else {
                continue;
            };
            let here = Vec3::from_array(peer.position);
            if players.iter().any(|p| {
                world_pos_to_layer(p.y) == m.layer && p.distance_xz(here) <= FACELING_REGARD_RADIUS
            }) {
                regarded.insert(m.home_chunk);
            }
        }

        for i in 0..self.movers.len() {
            self.tick_mover(i, net, dt, host_player_pos, &players, &regarded);
        }
    }

    /// ADR-094 E1b: a hit landed on `victim_id`. Applies damage to that ONE adult's health, then
    /// — if it survives — sets EVERY adult sharing its `home_chunk` to `Enforce` against
    /// `attacker_id` ("todos los adultos de la sala convergen sobre el agresor"), including the
    /// one that was actually hit. Returns whether the adult died.
    ///
    /// Called from the SAME branch of `process_pvp_hit_candidate_host` that would otherwise send
    /// a `PvpDamageGrant` over the wire — a faceling has no real backend on the other end, so the
    /// damage is host-local, mirror image of `PhantomAttackGrant`'s own direction.
    pub(super) fn apply_damage(
        &mut self,
        net: &mut NetworkManager,
        victim_id: PeerId,
        attacker_id: PeerId,
        damage: f32,
    ) -> bool {
        let Some(idx) = self.movers.iter().position(|m| m.id == victim_id) else {
            return false;
        };
        let dealt = damage.max(0.0).round() as u8;
        self.movers[idx].health = self.movers[idx].health.saturating_sub(dealt);
        info!(
            "MPTRACE step=FL_DMG event=faceling_adult_damaged faceling_id={} attacker_id={} damage={} health={}",
            victim_id, attacker_id, dealt, self.movers[idx].health
        );
        if self.movers[idx].health == 0 {
            let home = self.movers[idx].home_chunk;
            net.despawn_faceling(victim_id);
            self.movers.remove(idx);
            info!(
                "MPTRACE step=FL_DMG event=faceling_adult_killed faceling_id={} chunk=({},{})",
                victim_id, home.0, home.1
            );
            return true;
        }

        let home = self.movers[idx].home_chunk;
        for m in self.movers.iter_mut().filter(|m| m.home_chunk == home) {
            m.state = AdultState::Enforce;
            m.state_timer = FACELING_ENFORCE_COOLOFF_S;
            m.enforce_target = Some(attacker_id);
        }
        false
    }

    fn tick_mover(
        &mut self,
        i: usize,
        net: &mut NetworkManager,
        dt: f32,
        host_player_pos: Vec3,
        players: &[Vec3],
        regarded: &HashSet<(i32, i32)>,
    ) {
        let id = self.movers[i].id;
        let Some(peer) = net.peers.get(&id) else {
            return;
        };
        let from = Vec3::from_array(peer.position);
        let layer = self.movers[i].layer;
        let home = self.movers[i].home_chunk;
        let should_regard = regarded.contains(&home);

        if should_regard
            && !matches!(
                self.movers[i].state,
                AdultState::Regard | AdultState::Enforce
            )
        {
            self.movers[i].state = AdultState::Regard;
            self.movers[i].state_timer = FACELING_REGARD_MIN_S
                + rand::random::<f32>() * (FACELING_REGARD_MAX_S - FACELING_REGARD_MIN_S);
        }

        let anim = match self.movers[i].state {
            AdultState::Enforce => {
                let target_pos = self.movers[i].enforce_target.and_then(|tid| {
                    if tid == net.local_id {
                        Some(host_player_pos)
                    } else {
                        net.peers.get(&tid).map(|p| Vec3::from_array(p.position))
                    }
                });
                let in_range = target_pos.is_some_and(|target| {
                    world_pos_to_layer(target.y) == layer
                        && target.distance_xz(from) <= FACELING_REGARD_RADIUS
                });
                // Refreshed while close (ADR-094: "sin verlo" — a fight that stays in your face
                // never times out mid-swing), otherwise ticking down toward the give-up.
                if in_range {
                    self.movers[i].state_timer = FACELING_ENFORCE_COOLOFF_S;
                } else {
                    self.movers[i].state_timer -= dt;
                }
                if self.movers[i].state_timer <= 0.0 {
                    self.movers[i].state = AdultState::Working;
                    self.movers[i].enforce_target = None;
                    self.movers[i].state_timer = FACELING_COMMUTE_MIN_S
                        + rand::random::<f32>() * (FACELING_COMMUTE_MAX_S - FACELING_COMMUTE_MIN_S);
                    "idle"
                } else if let Some(target) =
                    target_pos.filter(|target| world_pos_to_layer(target.y) == layer)
                {
                    let raw_heading = (target.x - from.x).atan2(target.z - from.z);
                    let heading =
                        steer_around_walls(&mut self.grid_cache, layer, from, raw_heading);
                    let step = FACELING_ENFORCE_SPEED * dt;
                    let next = Vec3::new(
                        from.x + heading.sin() * step,
                        from.y,
                        from.z + heading.cos() * step,
                    );
                    // Same leash as `Commute`: the office is the cage even while it is angry.
                    // Hitting the boundary or a wall just holds position — still converging as
                    // far as it is allowed to.
                    if pos_in_chunk(next, home)
                        && is_walkable_grid_gen(&mut self.grid_cache, next, layer)
                    {
                        self.movers[i].heading = heading;
                        if let Some(peer) = net.peers.get_mut(&id) {
                            let yaw = heading.to_degrees().rem_euclid(360.0);
                            peer.update_player_state(next.to_array(), yaw, "walk_slow".into());
                        }
                        return;
                    }
                    "walk_slow"
                } else {
                    // Target disconnected or changed layer: nothing to converge on, just let the
                    // cooloff clock (already ticking above) run out.
                    "idle"
                }
            }
            AdultState::Regard => {
                self.movers[i].state_timer -= dt;
                if self.movers[i].state_timer <= 0.0 {
                    self.movers[i].state = AdultState::Working;
                    self.movers[i].state_timer = FACELING_COMMUTE_MIN_S
                        + rand::random::<f32>() * (FACELING_COMMUTE_MAX_S - FACELING_COMMUTE_MIN_S);
                } else if let Some(target) = players
                    .iter()
                    .filter(|p| world_pos_to_layer(p.y) == layer)
                    .min_by(|a, b| a.distance_xz(from).total_cmp(&b.distance_xz(from)))
                {
                    self.movers[i].heading = (target.x - from.x).atan2(target.z - from.z);
                }
                "idle"
            }
            AdultState::Working => {
                self.movers[i].state_timer -= dt;
                if self.movers[i].state_timer <= 0.0 {
                    match pick_puesto(&mut self.grid_cache, home, layer) {
                        Some(target) => {
                            self.movers[i].commute_target = target;
                            self.movers[i].state = AdultState::Commute;
                        }
                        None => self.movers[i].state_timer = FACELING_PUESTO_RETRY_S,
                    }
                }
                "idle"
            }
            AdultState::Commute => {
                let target = self.movers[i].commute_target;
                if from.distance_xz(target) <= FACELING_ARRIVE_EPS {
                    self.movers[i].state = AdultState::Working;
                    self.movers[i].state_timer = FACELING_COMMUTE_MIN_S
                        + rand::random::<f32>() * (FACELING_COMMUTE_MAX_S - FACELING_COMMUTE_MIN_S);
                    "idle"
                } else {
                    let raw_heading = (target.x - from.x).atan2(target.z - from.z);
                    let heading =
                        steer_around_walls(&mut self.grid_cache, layer, from, raw_heading);
                    let step = FACELING_WALK_SPEED * dt;
                    let next = Vec3::new(
                        from.x + heading.sin() * step,
                        from.y,
                        from.z + heading.cos() * step,
                    );
                    // The leash (ADR-094 rejected alternative D): a step that would leave the home
                    // chunk, or land somewhere grid_gen calls solid, simply does not happen — the
                    // adult stands one tick and re-steers from its current heading next time,
                    // exactly like `steer_around_walls`' own whisker deflection already does for
                    // the robapieles.
                    if pos_in_chunk(next, home)
                        && is_walkable_grid_gen(&mut self.grid_cache, next, layer)
                    {
                        self.movers[i].heading = heading;
                        if let Some(peer) = net.peers.get_mut(&id) {
                            let yaw = heading.to_degrees().rem_euclid(360.0);
                            peer.update_player_state(next.to_array(), yaw, "walk_slow".into());
                        }
                        return;
                    }
                    "walk_slow"
                }
            }
        };

        if let Some(peer) = net.peers.get_mut(&id) {
            let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
            peer.update_player_state(from.to_array(), yaw, anim.into());
        }
    }
}

// ─── ADR-094 E2a — Faceling niños: población y `PackRoam` ambiental ───
//
// Sin detección, sin `PackMind` compartido, sin cerco, sin congelación, sin robo (E2b/E2c) y sin
// voz (banco vacío de todos modos — se cablea junto al timing del coro en E2b/E2c, no antes).
// Entra silencioso y sin combate, igual que ADR-094 E1a para los adultos: se ve el pack rondando
// su territorio, nada más todavía.
//
// `ChildDriver` es un tipo PROPIO, no una extensión de `AdultDriver`: un pack no es "un adulto
// más" — la unidad de simulación es el PACK (`ChildPack`, con sus 3-4 `ChildMover`), no la
// criatura individual, y esa diferencia se nota ya en la población (`sync_population` recluta y
// retira PACKS enteros) y se notará más en E2b (una sola percepción escribe en el `PackMind` y
// los cuatro actúan sobre él el mismo tick).

/// v1 PLACEHOLDER, unmeasured. "Packs anclados a un chunk de oficina y rondando su perímetro (~2
/// chunks)" — ADR-094 punto 5. 2 chunks de 50 m ⇒ 100 m desde el ancla.
pub(super) const FACELING_CHILD_PATROL_RADIUS_M: f32 = 100.0;
/// Radio de activación de un pack: igual criterio que `FACELING_ACTIVATE_RADIUS`, pero medido
/// contra el ANCLA (centro del chunk), no contra un miembro individual — la población recluta o
/// retira el pack como unidad.
const FACELING_CHILD_ACTIVATE_RADIUS: f32 = 90.0;
const FACELING_CHILD_DEACTIVATE_RADIUS: f32 = 130.0;
const FACELING_CHILD_MIN_SPAWN_DISTANCE: f32 = 15.0;
/// Cap en PACKS simultáneamente simulados (no en niños individuales). v1 PLACEHOLDER.
const FACELING_CHILD_PACK_ACTIVE_CAP: usize = 8;

const FACELING_CHILD_MAX_HEALTH: u8 = 15;
const FACELING_CHILD_ROAM_SPEED: f32 = 1.6;
const FACELING_CHILD_ARRIVE_EPS: f32 = 0.4;
const FACELING_CHILD_ROAM_MIN_S: f32 = 8.0;
const FACELING_CHILD_ROAM_MAX_S: f32 = 20.0;
const FACELING_CHILD_ROAM_RETRY_S: f32 = 3.0;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum ChildState {
    /// Ambient wander within `FACELING_CHILD_PATROL_RADIUS_M` of the pack's anchor. The only
    /// state that exists yet — everything else is E2b/E2c.
    PackRoam,
}

pub(super) struct ChildMover {
    pub(super) id: PeerId,
    pub(super) heading: f32,
    pub(super) roam_target: Vec3,
    /// Seconds until the next `PackRoam` target roll.
    pub(super) state_timer: f32,
    pub(super) health: u8,
}

/// The pack's shared knowledge — empty in E2a on purpose. `PackMind` is what makes the pack a
/// hive rather than four independent movers: E2b adds `target`/`last_known_pos`/`last_known_vel`,
/// written by whichever member perceives something and read by all four the SAME tick (ADR-094
/// point 3: "no hay «avisar»; eso es exactamente lo inquietante").
pub(super) struct PackMind {}

pub(super) struct ChildPack {
    pub(super) home_chunk: (i32, i32),
    pub(super) layer: u8,
    /// World-space centre of `home_chunk` — the patrol reference point, fixed at spawn.
    pub(super) anchor: Vec3,
    pub(super) state: ChildState,
    pub(super) mind: PackMind,
    pub(super) members: Vec<ChildMover>,
}

pub(super) struct ChildDriver {
    pub(super) grid_cache: GridGenChunkCache,
    pub(super) packs: Vec<ChildPack>,
    pub(super) density_scale: f32,
    population_sync_in: f32,
}

/// Picks a walkable point within `radius` of `center` — the circular counterpart of
/// `pick_puesto`'s chunk-box sampling, for a pack whose patrol area is a radius around an anchor
/// rather than a single chunk's bounds.
fn pick_roam_point(
    cache: &mut GridGenChunkCache,
    center: Vec3,
    radius: f32,
    layer: u8,
) -> Option<Vec3> {
    for _ in 0..12 {
        let angle = rand::random::<f32>() * std::f32::consts::TAU;
        let r = rand::random::<f32>() * radius;
        let candidate = Vec3::new(
            center.x + angle.sin() * r,
            center.y,
            center.z + angle.cos() * r,
        );
        if is_walkable_grid_gen(cache, candidate, layer) {
            return Some(candidate);
        }
    }
    None
}

impl ChildDriver {
    pub(super) fn new(world_seed: u64) -> Self {
        Self {
            grid_cache: GridGenChunkCache::with_rules(
                world_seed,
                crate::world::zone_density::rules_for,
            ),
            packs: Vec::new(),
            density_scale: 1.0,
            population_sync_in: 0.0,
        }
    }

    /// Same ADR-043-shaped reconcile as `AdultDriver::sync_population`, but the unit is the PACK:
    /// `faceling_spawn::draw_child_pack_into` returns either nothing or a whole 3-4-member roster
    /// for a chunk, and retire/wake acts on that roster as one thing, never a partial pack.
    pub(super) fn sync_population(
        &mut self,
        net: &mut NetworkManager,
        host_player_pos: Vec3,
        dt: f32,
    ) {
        self.population_sync_in -= dt;
        if self.population_sync_in > 0.0 {
            return;
        }
        self.population_sync_in = FACELING_POPULATION_SYNC_INTERVAL;

        let players: Vec<Vec3> = std::iter::once(host_player_pos)
            .chain(
                net.peers
                    .iter()
                    .filter(|(id, p)| {
                        !net.is_phantom(**id) && !net.is_faceling(**id) && !p.relay_only
                    })
                    .map(|(_, p)| Vec3::from_array(p.position)),
            )
            .collect();

        // ── Put away the packs nobody is near any more ──
        let mut retired_packs: Vec<usize> = Vec::new();
        for (pi, pack) in self.packs.iter().enumerate() {
            let far = players.iter().all(|p| {
                world_pos_to_layer(p.y) != pack.layer
                    || p.distance_xz(pack.anchor) > FACELING_CHILD_DEACTIVATE_RADIUS
            });
            if far {
                retired_packs.push(pi);
            }
        }
        for &pi in retired_packs.iter().rev() {
            for m in &self.packs[pi].members {
                net.despawn_faceling(m.id);
            }
            self.packs.remove(pi);
        }

        // ── Wake up packs somebody walked near ──
        if self.packs.len() >= FACELING_CHILD_PACK_ACTIVE_CAP {
            return;
        }
        let taken: HashSet<(i32, i32)> = self.packs.iter().map(|p| p.home_chunk).collect();
        let mut seen_chunks: HashSet<((i32, i32), u8)> = HashSet::new();
        let mut drawn: Vec<[f32; 3]> = Vec::new();

        for p in &players {
            let layer = world_pos_to_layer(p.y);
            let cell = CELL_SIZE_M * CHUNK_CELLS as f32;
            let cx0 = ((p.x - FACELING_CHILD_ACTIVATE_RADIUS) / cell).floor() as i32;
            let cx1 = ((p.x + FACELING_CHILD_ACTIVATE_RADIUS) / cell).floor() as i32;
            let cz0 = ((p.z - FACELING_CHILD_ACTIVATE_RADIUS) / cell).floor() as i32;
            let cz1 = ((p.z + FACELING_CHILD_ACTIVATE_RADIUS) / cell).floor() as i32;
            for cx in cx0..=cx1 {
                for cz in cz0..=cz1 {
                    if taken.contains(&(cx, cz)) || !seen_chunks.insert(((cx, cz), layer)) {
                        continue;
                    }
                    faceling_spawn::draw_child_pack_into(
                        net.world_seed,
                        cx,
                        cz,
                        layer,
                        self.density_scale,
                        &mut drawn,
                    );
                    if drawn.is_empty() {
                        continue;
                    }
                    let anchor = {
                        let (x0, x1, z0, z1) = chunk_bounds((cx, cz));
                        Vec3::new(
                            (x0 + x1) / 2.0,
                            crate::world::grid_gen::grid_floor_y(layer)
                                + crate::world::collision::PLAYER_BASE_Y,
                            (z0 + z1) / 2.0,
                        )
                    };
                    if p.distance_xz(anchor) > FACELING_CHILD_ACTIVATE_RADIUS {
                        continue;
                    }
                    // Measured against the actual drawn spot, not the anchor — same reasoning as
                    // `AdultDriver::sync_population`: the anchor is just the leash centre, and a
                    // member can land anywhere in the chunk, including right next to a player who
                    // is standing nowhere near the geometric centre.
                    if players.iter().any(|q| {
                        q.distance_xz(Vec3::from_array(drawn[0]))
                            < FACELING_CHILD_MIN_SPAWN_DISTANCE
                    }) {
                        continue;
                    }
                    let mut members = Vec::with_capacity(drawn.len());
                    for pos in drawn.iter().copied() {
                        let id = net.spawn_faceling("Faceling_Child", pos, 2);
                        let spawn_pos = net
                            .peers
                            .get(&id)
                            .map(|pp| Vec3::from_array(pp.position))
                            .unwrap_or_else(|| Vec3::from_array(pos));
                        members.push(ChildMover {
                            id,
                            heading: rand::random::<f32>() * std::f32::consts::TAU,
                            roam_target: spawn_pos,
                            state_timer: FACELING_CHILD_ROAM_MIN_S
                                + rand::random::<f32>()
                                    * (FACELING_CHILD_ROAM_MAX_S - FACELING_CHILD_ROAM_MIN_S),
                            health: FACELING_CHILD_MAX_HEALTH,
                        });
                    }
                    info!(
                        "MPTRACE step=FL_POP event=faceling_pack_spawned chunk=({},{}) layer={} size={}",
                        cx, cz, layer, members.len()
                    );
                    self.packs.push(ChildPack {
                        home_chunk: (cx, cz),
                        layer,
                        anchor,
                        state: ChildState::PackRoam,
                        mind: PackMind {},
                        members,
                    });
                    if self.packs.len() >= FACELING_CHILD_PACK_ACTIVE_CAP {
                        return;
                    }
                }
            }
        }
    }

    /// One entity tick for every active pack. E2a only ever runs `PackRoam` — each member wanders
    /// independently within `FACELING_CHILD_PATROL_RADIUS_M` of the shared anchor, so the group
    /// stays loosely together without an explicit cohesion rule.
    pub(super) fn step(&mut self, net: &mut NetworkManager, dt: f32) {
        for pi in 0..self.packs.len() {
            for mi in 0..self.packs[pi].members.len() {
                self.tick_member(pi, mi, net, dt);
            }
        }
    }

    fn tick_member(&mut self, pi: usize, mi: usize, net: &mut NetworkManager, dt: f32) {
        let id = self.packs[pi].members[mi].id;
        let Some(peer) = net.peers.get(&id) else {
            return;
        };
        let from = Vec3::from_array(peer.position);
        let layer = self.packs[pi].layer;
        let anchor = self.packs[pi].anchor;

        match self.packs[pi].state {
            ChildState::PackRoam => {
                let target = self.packs[pi].members[mi].roam_target;
                if from.distance_xz(target) <= FACELING_CHILD_ARRIVE_EPS {
                    self.packs[pi].members[mi].state_timer -= dt;
                    if self.packs[pi].members[mi].state_timer <= 0.0 {
                        match pick_roam_point(
                            &mut self.grid_cache,
                            anchor,
                            FACELING_CHILD_PATROL_RADIUS_M,
                            layer,
                        ) {
                            Some(next_target) => {
                                self.packs[pi].members[mi].roam_target = next_target;
                                self.packs[pi].members[mi].state_timer = FACELING_CHILD_ROAM_MIN_S
                                    + rand::random::<f32>()
                                        * (FACELING_CHILD_ROAM_MAX_S - FACELING_CHILD_ROAM_MIN_S);
                            }
                            None => {
                                self.packs[pi].members[mi].state_timer = FACELING_CHILD_ROAM_RETRY_S
                            }
                        }
                    }
                    if let Some(peer) = net.peers.get_mut(&id) {
                        let yaw = self.packs[pi].members[mi]
                            .heading
                            .to_degrees()
                            .rem_euclid(360.0);
                        peer.update_player_state(from.to_array(), yaw, "idle".into());
                    }
                    return;
                }
                let raw_heading = (target.x - from.x).atan2(target.z - from.z);
                let heading = steer_around_walls(&mut self.grid_cache, layer, from, raw_heading);
                let step = FACELING_CHILD_ROAM_SPEED * dt;
                let next = Vec3::new(
                    from.x + heading.sin() * step,
                    from.y,
                    from.z + heading.cos() * step,
                );
                // Same leash SHAPE as the adults' `Commute`, radius instead of chunk-box: a step
                // that would leave the patrol circle, or land somewhere solid, just does not
                // happen — hold and re-steer next tick.
                if next.distance_xz(anchor) <= FACELING_CHILD_PATROL_RADIUS_M
                    && is_walkable_grid_gen(&mut self.grid_cache, next, layer)
                {
                    self.packs[pi].members[mi].heading = heading;
                    if let Some(peer) = net.peers.get_mut(&id) {
                        let yaw = heading.to_degrees().rem_euclid(360.0);
                        peer.update_player_state(next.to_array(), yaw, "walk_slow".into());
                    }
                    return;
                }
                if let Some(peer) = net.peers.get_mut(&id) {
                    let yaw = self.packs[pi].members[mi]
                        .heading
                        .to_degrees()
                        .rem_euclid(360.0);
                    peer.update_player_state(from.to_array(), yaw, "walk_slow".into());
                }
            }
        }
    }
}
