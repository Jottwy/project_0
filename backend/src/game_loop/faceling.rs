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
// ADR-094 E2b: the pieces `phantom.rs` built for the robapieles that are PURE free functions
// (owe nothing to `PhantomMover`'s own shape) — `player_is_looking_at[_within]` (Statue's cone,
// reused verbatim per the ADR), `intercept_point` (ADR-088), the two `PHANTOM_STATUE_*` cones.
// `resolve_flank_goal` is NOT in this list on purpose: it is a `PhantomDriver` method tied to
// `self.movers[i]`, so this module writes its own `child_flank_position` instead of importing it.
use super::phantom::*;

use std::collections::HashSet;

use crate::world::faceling_spawn;
use crate::world::grid_gen::{
    is_walkable_grid_gen, segment_is_clear, steer_around_walls, CELL_SIZE_M, CHUNK_CELLS,
};

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

/// THE ANTI-WEDGE WATCHDOG. Seconds of consecutively REFUSED steps after which a mover gives up on
/// its current destination and picks a new one.
///
/// Both walking states had the same deadlock, and it is worth naming precisely because it is easy
/// to reintroduce: the timer that says "pick somewhere else" only ever ran once the walk had
/// already succeeded. `Commute` never decremented `state_timer` at all (only `Working` did, and
/// you reach `Working` by ARRIVING), and `PackRoam` decremented it only inside its
/// `distance <= ARRIVE_EPS` branch. So a mover whose every step was refused — target behind a
/// wall, wedged in a corner, leash boundary in the way — stood there re-steering into the same
/// blocked heading forever, animated as walking and never moving. That is the "se quedan pillados
/// en paredes" from the 2026-08-24 play-test.
///
/// Long enough not to cut short a legitimate detour (`steer_around_walls` deflects within a tick
/// or two, and a long way round still shortens the distance eventually), short enough that the
/// stall never reads as a freeze.
pub(super) const FACELING_WEDGED_GIVE_UP_S: f32 = 3.0;
/// How much closer a mover has to get to count as progress. Above the per-tick step at the slowest
/// speed (1.0 m/s × 0.1 s = 0.1 m) would call a legitimate crawl "stuck", so this sits under it.
const FACELING_PROGRESS_EPS: f32 = 0.05;

/// Watches whether a mover is actually getting closer to where it is going.
///
/// Measures PROGRESS, not refused steps, and the difference is the whole reason this exists: the
/// first version of the watchdog counted steps the leash/walkability guard rejected, and it never
/// fired, because `steer_around_walls` deflects sideways — the mover keeps taking perfectly valid
/// steps along a wall it can never get round, so "was the step accepted" is `true` forever while
/// the distance to the target does not move at all.
#[derive(Debug, Clone, Copy)]
pub(super) struct ProgressWatch {
    /// The destination `best` is measured against. Comparing it each tick makes the watch reset
    /// itself whenever the caller picks a new destination, so no assignment site has to remember
    /// to — which is exactly the kind of bookkeeping that rots.
    ref_target: Vec3,
    best: f32,
    stalled_for: f32,
}

impl ProgressWatch {
    pub(super) fn new() -> Self {
        Self {
            ref_target: Vec3::ZERO,
            best: f32::MAX,
            stalled_for: 0.0,
        }
    }

    /// Records this tick and returns whether the mover has stopped making headway long enough to
    /// give up on `target`. Resets itself on a give-up, so the caller only has to re-plan.
    fn note(&mut self, from: Vec3, target: Vec3, dt: f32) -> bool {
        if self.ref_target.distance_xz(target) > FACELING_PROGRESS_EPS || self.best == f32::MAX {
            self.ref_target = target;
            self.best = from.distance_xz(target);
            self.stalled_for = 0.0;
            return false;
        }
        let d = from.distance_xz(target);
        if d < self.best - FACELING_PROGRESS_EPS {
            self.best = d;
            self.stalled_for = 0.0;
            return false;
        }
        self.stalled_for += dt;
        if self.stalled_for >= FACELING_WEDGED_GIVE_UP_S {
            *self = Self::new();
            return true;
        }
        false
    }
}

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

/// E1c — the blow itself. Same reach as `PHANTOM_ATTACK_REACH`: both are human-scale bodies at
/// arm's length, and a different number here would be a number with no reason behind it.
const FACELING_ADULT_ATTACK_REACH: f32 = 2.4;
/// Deliberately far below the robapieles' 35. ADR-094 point 2 makes the adult "atrezzo primero,
/// amenaza segundo" — at this rate a full-health player has some twenty seconds of being cornered
/// before it matters, which is long enough to read as a warning rather than an execution. The
/// office kills by NUMBERS and by not letting you leave, never by one adult connecting.
const FACELING_ADULT_ATTACK_DAMAGE: f32 = 12.0;
/// Same recovery as `PHANTOM_STRIKE_RECOVERY`, for the same reason as the reach.
const FACELING_ADULT_STRIKE_RECOVERY: f32 = 2.5;

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
    /// E1c — seconds left before this adult can swing again. Same field and same name as
    /// `PhantomMover::strike_recover`; ticked down for EVERY adult in every state, not just in
    /// `Enforce`, so a fight that drops out of range and comes back cannot bank a free hit.
    pub(super) strike_recover: f32,
    /// Anti-wedge watchdog for `Commute` — see `ProgressWatch`.
    pub(super) progress: ProgressWatch,
}

pub(super) struct AdultDriver {
    pub(super) grid_cache: GridGenChunkCache,
    pub(super) movers: Vec<AdultMover>,
    pub(super) density_scale: f32,
    population_sync_in: f32,
    /// E1c — blows staged this tick, drained by `step`'s caller. Reuses `PhantomAttack` rather
    /// than minting a parallel type: `game_loop.rs`'s routing loop never reads WHO struck (ADR-016
    /// §1 keeps the creature's id off the wire entirely), so it already works for any attacker.
    pub(super) attacks: Vec<PhantomAttack>,
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
            attacks: Vec::new(),
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
                            strike_recover: 0.0,
                            progress: ProgressWatch::new(),
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
    /// E1c: returns the blows staged this tick, exactly like `PhantomDriver::step` — the caller's
    /// existing routing loop takes it from there.
    pub(super) fn step(
        &mut self,
        net: &mut NetworkManager,
        dt: f32,
        host_player_pos: Vec3,
    ) -> &[PhantomAttack] {
        self.attacks.clear();
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
            // Every adult, every state: see `AdultMover::strike_recover`.
            self.movers[i].strike_recover = (self.movers[i].strike_recover - dt).max(0.0);
            self.tick_mover(i, net, dt, host_player_pos, &players, &regarded);
        }
        &self.attacks
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
                // E1c — THE BLOW, checked before the convergence arm below so that arriving and
                // swinging cannot cost a tick each. No cone test either way: unlike the
                // robapieles (whose front/back split picks between a hit and a grab), an adult
                // has exactly one thing it does, and ADR-094 point 2 gives it no perception to
                // branch on ("no perciben crouch, luz ni ruido").
                //
                // `segment_is_clear` IS required though, exactly as `phantom.rs` requires it
                // (ADR-082): reach alone would let an adult pinned against a partition wall bleed
                // whoever walks down the corridor on the other side, 12 a swing, unseeable and
                // unanswerable. Its leash makes that worse, not better — it cannot step around.
                let strike_target = match self.movers[i].strike_recover <= 0.0 {
                    false => None,
                    true => target_pos.filter(|target| {
                        world_pos_to_layer(target.y) == layer
                            && target.distance_xz(from) <= FACELING_ADULT_ATTACK_REACH
                            && segment_is_clear(&mut self.grid_cache, layer, from, *target)
                    }),
                };
                if let (Some(target), Some(victim)) = (strike_target, self.movers[i].enforce_target)
                {
                    // Face what it hits. The convergence arm below is what normally keeps
                    // `heading` pointed at the target, and this path returns before reaching it —
                    // left alone the adult lands the blow side-on or with its back turned, which
                    // reads as damage out of nowhere.
                    let heading = (target.x - from.x).atan2(target.z - from.z);
                    self.movers[i].heading = heading;
                    self.movers[i].strike_recover = FACELING_ADULT_STRIKE_RECOVERY;
                    self.attacks.push(PhantomAttack {
                        victim,
                        kind: PhantomAttackKind::Hit(FACELING_ADULT_ATTACK_DAMAGE),
                    });
                    info!(
                        "MPTRACE step=FL_ATK event=faceling_adult_struck faceling_id={} victim_id={} damage={}",
                        id, victim, FACELING_ADULT_ATTACK_DAMAGE
                    );
                    // Same "pickup" gesture the robapieles swings with — the proxy's only
                    // wired one-shot arm animation (ADR-011's single transient channel).
                    if let Some(peer) = net.peers.get_mut(&id) {
                        let yaw = heading.to_degrees().rem_euclid(360.0);
                        peer.update_player_state(from.to_array(), yaw, "pickup".into());
                    }
                    return;
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
                } else if self.movers[i].progress.note(from, target, dt) {
                    // Not getting any closer for `FACELING_WEDGED_GIVE_UP_S` — give up on this
                    // puesto. `Commute` had NO way out on its own: `state_timer` is only
                    // decremented by `Working`, and the only route to `Working` was arriving.
                    // Back with a short clock so a fresh destination is drawn almost immediately
                    // instead of waiting out a full commute interval doing nothing.
                    self.movers[i].state = AdultState::Working;
                    self.movers[i].state_timer = FACELING_PUESTO_RETRY_S;
                    info!(
                        "MPTRACE step=FL_NAV event=faceling_adult_unwedged faceling_id={} chunk=({},{})",
                        id, home.0, home.1
                    );
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
                    // the robapieles. The watchdog above is what stops that from being forever.
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
/// The idle wander. Slowest of the three — a child that is not hunting you is not in a hurry, and
/// the contrast is what makes the other two register as gear changes.
const FACELING_CHILD_ROAM_SPEED: f32 = 1.2;
const FACELING_CHILD_ARRIVE_EPS: f32 = 0.4;
const FACELING_CHILD_ROAM_MIN_S: f32 = 8.0;
const FACELING_CHILD_ROAM_MAX_S: f32 = 20.0;
const FACELING_CHILD_ROAM_RETRY_S: f32 = 3.0;

/// v1 PLACEHOLDER, unmeasured. A member's own forward-cone sighting radius — deliberately its
/// own constant and not a reuse of any `PHANTOM_*` detection range: the robapieles hears and sees
/// light, a child (ADR-094 point 3) only ever gets this one geometric check.
const FACELING_CHILD_DETECT_RADIUS: f32 = 20.0;
const FACELING_CHILD_DETECT_HALF_FOV_DEG: f32 = 60.0;
/// THE GEAR CHANGE (play-test, Joel 2026-08-24): a child in the cerco moves at one of two speeds
/// depending on whether you can see it coming.
///
/// SEEN — you are facing its general direction, but not squarely enough to trigger the freeze.
/// A contained advance: it is closing, and you can watch it close.
const FACELING_CHILD_CERCO_SPEED_SEEN: f32 = 1.8;
/// UNSEEN — your back is turned. THIS is the scare: turn away for two seconds and they have
/// crossed half the room. Deliberately BELOW a running player (~5 m/s): they never out-sprint you
/// in a straight line, they only take everything you give them by stopping, turning or hesitating.
/// "Corriendo pero a paso de niño, no una velocidad de locos."
const FACELING_CHILD_CERCO_SPEED_UNSEEN: f32 = 4.2;
/// What `intercept_point` assumes when projecting where to cut you off. The UNSEEN figure on
/// purpose: a `Cut` that plans at walking pace aims at a point you have already passed.
const FACELING_CHILD_CERCO_SPEED: f32 = FACELING_CHILD_CERCO_SPEED_UNSEEN;
/// Enmienda 4 — how far off the target's facing a `Flank` aims. ADR-094 point 3 says the flankers
/// take "los lados FUERA DEL CONO del objetivo", and 90° (the original `FRAC_PI_2`) is not outside
/// anything — it is the boundary, so a flanker sat there stays in the corner of your eye and you
/// can watch the whole pack at once. Past 90° puts them genuinely behind your shoulders, which is
/// what forces you to choose who to keep an eye on.
const FACELING_CHILD_FLANK_ANGLE_DEG: f32 = 125.0;

/// How far `Flank`/`Cut` try to stand from the target while converging — the radius of the ring
/// the cerco reads as, not a hard stop distance (ADR-094 point 4's strike range is a separate,
/// tighter constant added in E2c).
const FACELING_CHILD_CERCO_BAND: f32 = 6.0;
/// ADR-094 point 3 doesn't specify a give-up window for the cerco itself (only Enforce's ~45 s is
/// named, for the adults) — this is a v1 PLACEHOLDER, deliberately shorter: a pack that lost the
/// scent should let go sooner than an office defending itself.
const FACELING_CHILD_GIVE_UP_S: f32 = 20.0;

/// ADR-094 point 3/6 — the child pack's OWN vocal kind space, read by the CHILD's own
/// `ProxyVocalHook` instance/bank array (`FacelingChildAvatarBuilder`'s own wiring), independent
/// of `phantom.rs`'s `VOCAL_*` constants even though both ride the same generic
/// `peer.vocal_seq`/`vocal_kind` wire fields (ADR-094 pays for that bump once, for both species).
const FACELING_CHILD_VOCAL_GIGGLE: u8 = 0;
const FACELING_CHILD_VOCAL_SCREAM: u8 = 1;
/// The lone survivor's regroup call ("grita para reagruparse con otro pack"), emitted from
/// `ChildState::Flee`. Client bank ships wired but unauthored, same convention as `phantom.rs`'s
/// own silent banks.
const FACELING_CHILD_VOCAL_CALL: u8 = 2;
/// Enmienda 3 — the close-quarters whisper/chant. Takes over from the giggle once the ring is
/// shut: the giggles are what you hear while they are still coming, this is what you hear when
/// they are already on you. Different sound, different distance, same counter.
const FACELING_CHILD_VOCAL_WHISPER: u8 = 3;

/// How often the pack schedules one giggle "beat" (`ChildDriver::update_giggles_for_pack`).
const FACELING_CHILD_GIGGLE_INTERVAL_S: f32 = 5.0;
/// Widest per-member offset off the beat: `PackRoam`, or `PackStalk` at the edge of detection —
/// spread out, reads as ambient, not a chorus.
const FACELING_CHILD_GIGGLE_SPREAD_MAX_S: f32 = 2.2;
/// Narrowest offset, reached once the nearest member has closed to `FACELING_CHILD_CERCO_BAND` —
/// ADR-094 point 3: "risa a coro = el cerco está cerrado".
const FACELING_CHILD_GIGGLE_SPREAD_MIN_S: f32 = 0.15;

/// v1 PLACEHOLDER. Faster than the cerco: this is panic, not hunting.
const FACELING_CHILD_FLEE_SPEED: f32 = 3.0;
/// How often a lone survivor screams for another pack while fleeing.
const FACELING_CHILD_CALL_INTERVAL_S: f32 = 4.0;
/// How close a lone survivor has to get to another pack of its own layer to join it. Generous on
/// purpose — a straggler that never finds anybody is a straggler that stays a permanent free kill,
/// which is the opposite of what "reagruparse" is for.
const FACELING_CHILD_REGROUP_RADIUS: f32 = 25.0;
/// A pack at this size does not accept a straggler. Five since the 2026-08-24 play-test (ADR-094
/// Enmienda 2): `assign_roles` has a roster for it, and beyond five the extra members fall through
/// to `Press | None`, which is a silent degradation rather than a decision.
const FACELING_CHILD_PACK_MAX: usize = 8;

/// E2c — the `Press` role's blow. Shorter than the adults' 2.4 m: a child's arms are shorter, and
/// the tighter reach is also what forces the cerco to actually CLOSE before anything lands.
const FACELING_CHILD_ATTACK_REACH: f32 = 1.8;
/// Longer than the adults' 2.5 s. The knockdown is a much heavier event than a plain hit (ADR-076
/// takes control away for `PHANTOM_KNOCKDOWN_SECONDS`), so it has to be rare enough that a cerco
/// reads as one nasty moment and a scramble out, not as a stun-lock with no way back.
const FACELING_CHILD_STRIKE_RECOVERY: f32 = 6.0;
/// ADR-094 point 4: the connecting `Press` blow "hace knockdown (pieza de ADR-076)". Seconds and
/// impulse handed to `PhantomAttackKind::Knockdown` — matched to the robapieles' own ambush
/// knockdown so the client's existing handler reads them the same way.
const FACELING_CHILD_KNOCKDOWN_SECONDS: f32 = 2.0;
const FACELING_CHILD_KNOCKDOWN_FORCE: f32 = 6.0;

/// Enmienda 3 — EL EMPUJÓN. Any child that is not the `Press` shoves you when it gets this close:
/// no damage, no knockdown, just a push. It is the "te molestan" layer — the pack becomes a
/// physical nuisance long before it becomes lethal, and being jostled from three sides while you
/// try to aim is what the cerco should FEEL like.
const FACELING_CHILD_SHOVE_REACH: f32 = 2.2;
/// Per-member. Short on purpose: with four or five of them the shoves overlap into constant
/// harassment, which is the point, while any ONE child still reads as occasional.
const FACELING_CHILD_SHOVE_RECOVERY: f32 = 2.5;
/// Deliberately mild. `PHANTOM_KNOCKBACK_FORCE` is 3.0 for a grown creature; this has to nudge
/// and annoy, never launch — the moment a shove throws you across the room it stops being
/// harassment and becomes the knockdown, which already exists and belongs to the `Press`.
const FACELING_CHILD_SHOVE_FORCE: f32 = 1.6;

/// Enmienda 3 — EL SCREAMER. The jump-scare: one child, right behind you, screaming point-blank
/// with a hard shove and real damage.
///
/// Gated hard, because a scare that repeats is not a scare. It needs ALL of: your back turned, a
/// closed cerco, point-blank range, and a pack-wide cooldown — not per member, or five children
/// would take turns and it would become a mechanic instead of a moment.
const FACELING_CHILD_SCREAMER_REACH: f32 = 2.0;
const FACELING_CHILD_SCREAMER_DAMAGE: f32 = 10.0;
const FACELING_CHILD_SCREAMER_FORCE: f32 = 5.0;
/// Pack-wide, and long. This is the beat the whole encounter builds toward; twice in ten seconds
/// would spend it.
const FACELING_CHILD_SCREAMER_COOLDOWN_S: f32 = 25.0;

/// How near the target a member has to be to count toward a CLOSED cerco. Tighter than
/// `FACELING_CHILD_CERCO_BAND * 1.5` would be: this radius is what switches the freeze off (see
/// `cerco_is_closed`), so it has to mean "they are on top of you", not "they arrived".
const FACELING_CHILD_CERCO_CLOSED_RADIUS: f32 = 7.0;
/// How many members inside that radius close the cerco. Three, so a pack thinned by a death gets
/// measurably less lethal (a pair can never close one) — "el peligro es la geometría del cerco".
const FACELING_CHILD_CERCO_CLOSED_MIN: usize = 3;

/// ADR-094 points 3 and 4, the one predicate both halves of the pay-off share: is the ring
/// actually shut around `target`?
///
/// Load-bearing in TWO places, deliberately the same test in both — `update_freeze_for_pack`
/// (a closed cerco cancels the stare's protection) and the `Press` blow ("o sobre un jugador
/// cercado"). Two separate thresholds would let the freeze break at a distance the blow could not
/// yet reach, which reads to the player as "looking at them stopped working for no reason".
fn cerco_is_closed(member_positions: &[Vec3], target: Vec3) -> bool {
    member_positions
        .iter()
        .filter(|p| p.distance_xz(target) <= FACELING_CHILD_CERCO_CLOSED_RADIUS)
        .count()
        >= FACELING_CHILD_CERCO_CLOSED_MIN
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum ChildState {
    /// Ambient wander within `FACELING_CHILD_PATROL_RADIUS_M` of the pack's anchor.
    PackRoam,
    /// The cerco: `PackMind.target` is set, roles are assigned, everyone converges. Entered the
    /// instant ANY member detects a player; left when `PackMind.lost_for` exceeds
    /// `FACELING_CHILD_GIVE_UP_S`.
    PackStalk,
    /// ADR-094 point 3, "cobardía individual": a pack thinned down to ONE runs for its own
    /// territory and calls for another pack. A `Flee` pack never cercos again on its own — it is
    /// a straggler until it merges (`ChildDriver::regroup_lone_survivors`), which is the whole
    /// point: "el peligro es la geometría del cerco, nunca el niño suelto".
    Flee,
}

pub(super) struct ChildMover {
    pub(super) id: PeerId,
    pub(super) heading: f32,
    pub(super) roam_target: Vec3,
    /// Seconds until the next `PackRoam` target roll. Unused in `PackStalk`.
    pub(super) state_timer: f32,
    pub(super) health: u8,
    /// `None` in `PackRoam` — nothing to hold a side for. Assigned on entering `PackStalk`
    /// (`assign_roles`) and re-rolled whenever the roster changes (a member dies).
    pub(super) role: Option<ChildRole>,
    /// Enmienda 4 — PER-MEMBER freeze. ADR-094 point 3 wrote this at PACK level ("se congela el
    /// pack ENTERO") and the code carried a comment defending it: a per-member latch, it said,
    /// would let a flanker off to the side keep creeping while the one you are staring at holds
    /// still — "no es cuatro quietos, es tres arrastrándose".
    ///
    /// That description was right and the conclusion was wrong. Three creeping away is EXACTLY the
    /// encounter this wants (play-test, Joel 2026-08-24): freezing the whole pack meant you could
    /// hold five children still by looking at one of them, and then take them apart one at a time.
    /// The stare now buys you the child you are looking AT, and costs you the ones you are not.
    pub(super) frozen: bool,
    /// ADR-094 point 3 ("las risas son telemetría") + point 6's vocal banks. Same shape as
    /// `phantom.rs::PhantomMover`'s own `pending_vocal`/`vocal_seq`/`vocal_kind` (staged here,
    /// sealed into `peer.vocal_*` once per tick by `ChildDriver::seal_vocals` — see that fn for
    /// why staging beats writing at the decision site).
    pub(super) pending_vocal: Option<u8>,
    pub(super) vocal_seq: u8,
    pub(super) vocal_kind: u8,
    /// Countdown to this member's own queued giggle, in flight from
    /// `ChildDriver::update_giggles_for_pack`. `None` when nothing is queued.
    pub(super) vocal_delay: Option<f32>,
    /// E2c — seconds before this child may swing again. Only `Press` ever swings, but the field
    /// lives on every member because roles are re-dealt on every death: a `Flank` promoted to
    /// `Press` mid-fight must inherit a cooldown, not a clean slate.
    pub(super) strike_recover: f32,
    /// Anti-wedge watchdog for `PackRoam` — see `ProgressWatch`.
    pub(super) progress: ProgressWatch,
    /// ADR-094 punto 4 — the loot this child is carrying, `(def_id, count)`. `None` until a
    /// `StealReport` comes back naming something real; a child NEVER carries on the strength of
    /// its own blow, because the victim is the one who decides whether anything was taken.
    ///
    /// Mirrored into `peer.carry_def`/`carry_count` by `seal_vocals` so the theft is VISIBLE —
    /// "se le VE llevándose lo tuyo, que es la mitad de la rabia".
    pub(super) loot: Option<(i32, u16)>,
    /// `ChildRole::Flank`'s persisted side, same shape and same reason as
    /// `phantom.rs::PhantomMover::flank_offset`: a member sitting dead-centre in the target's
    /// view must not shuffle between two equally good exits every tick.
    pub(super) flank_offset: f32,
}

/// The pack's shared knowledge. What makes the pack a hive rather than four independent movers:
/// written by whichever member perceives something (`detect_for_pack`), read by all four THE
/// SAME tick (ADR-094 point 3: "no hay «avisar»; eso es exactamente lo inquietante" — there is no
/// per-member latency between one spotting the player and all four reacting).
pub(super) struct PackMind {
    pub(super) target: Option<PeerId>,
    pub(super) last_known_pos: Option<Vec3>,
    /// Planar velocity (x,z) of `target` as of the tick it was last actually seen — the input
    /// `CUT` needs to intercept the RETREAT (ADR-094: "sobre la dirección DE VUELTA del
    /// jugador"), not the approach.
    pub(super) last_known_vel: (f32, f32),
    /// Seconds since ANY member last had `target` in `FACELING_CHILD_DETECT_RADIUS`. The pack
    /// gives up the cerco (back to `PackRoam`) past `FACELING_CHILD_GIVE_UP_S`, same shape as the
    /// robapieles' own Search timeout.
    pub(super) lost_for: f32,
}

impl PackMind {
    pub(super) fn empty() -> Self {
        Self {
            target: None,
            last_known_pos: None,
            last_known_vel: (0.0, 0.0),
            lost_for: 0.0,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum ChildRole {
    /// Harasses head-on; the one whose connecting hit knocks down and steals (E2c/E2d, not yet
    /// wired).
    Press,
    /// Orbits to a side out of the target's view cone — `resolve_child_flank_goal`, the
    /// non-`&mut self` twin of `phantom.rs::resolve_flank_goal` (that one is a `PhantomDriver`
    /// method, tied to `PhantomMover`; this reimplements the same geometry against a plain
    /// `flank_offset: &mut f32` so it owes nothing to the robapieles' own mover shape).
    Flank,
    /// Goes to `intercept_point` on the target's RETREAT direction — the piece ADR-088 built for
    /// the robapieles' own chase, reused verbatim (it only ever needed a cache/layer/positions/
    /// velocity, never `PhantomMover` itself).
    Cut,
    /// Enmienda 5 — THE RING. Does not come for you at all: it orbits at
    /// `FACELING_CHILD_RING_BAND` and keeps working around to whichever side you are NOT facing.
    ///
    /// This is the role that makes a big pack feel like a pack instead of a queue. `Press`,
    /// `Flank` and `Cut` all converge, so past four or five bodies they arrive together and you
    /// fight a crowd in front of you. A `Ring` deliberately stays out of reach and spends the whole
    /// encounter behind your shoulder, so the danger stops being "what is in front of me" and
    /// becomes "how many are back there now".
    Ring,
}

pub(super) struct ChildPack {
    pub(super) home_chunk: (i32, i32),
    pub(super) layer: u8,
    /// World-space centre of `home_chunk` — the patrol reference point, fixed at spawn.
    pub(super) anchor: Vec3,
    pub(super) state: ChildState,
    pub(super) mind: PackMind,
    /// Whether the CERCO has been closed at least once this stalk — kept only for the log line
    /// that reports it; the live gate is `cerco_is_closed` against the target's current position.
    pub(super) frozen: bool,
    pub(super) members: Vec<ChildMover>,
    /// Seconds to the next giggle "beat" (`ChildDriver::update_giggles_for_pack`). Pack-level,
    /// not per-member: the beat fires once and every member queues its own offset off it —
    /// that offset, not this timer, is what spreads or converges the chorus.
    pub(super) giggle_timer: f32,
    /// Bumped every beat, folded into each member's `chorus_delay_fraction` key so two beats
    /// never reuse the same per-member offset (same reason `phantom.rs` keys on `vocal_seq`).
    pub(super) giggle_round: u32,
    /// Enmienda 3 — seconds until this pack may land another screamer. PACK-level, not per
    /// member: with five children a per-member cooldown would just mean they queue up and the
    /// jump-scare becomes a rotation.
    pub(super) screamer_cooldown: f32,
}

pub(super) struct ChildDriver {
    pub(super) grid_cache: GridGenChunkCache,
    pub(super) packs: Vec<ChildPack>,
    pub(super) density_scale: f32,
    population_sync_in: f32,
    /// E2c — same channel and same reasoning as `AdultDriver::attacks`.
    pub(super) attacks: Vec<PhantomAttack>,
    /// ADR-094 punto 4 — `(thief, victim)` pairs whose blow connected this tick and who are not
    /// already carrying. Drained by `step`'s caller, which owns the sockets: the driver decides
    /// THAT a theft happens, the game loop asks the victim WHAT is lost.
    pub(super) thefts: Vec<(PeerId, PeerId)>,
    /// ADR-094 punto 4 — loot dropped back into the world this tick, `(def_id, count, position)`.
    /// The non-destruction invariant runs through here: a thief that dies, escapes home or is
    /// deactivated all end up pushing to this one list, and the caller mints the world item.
    pub(super) dropped_loot: Vec<(i32, u16, Vec3)>,
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

/// Is `target` within this member's own forward cone? ADR-094 point 3's ONLY detection input —
/// no cone/sound/light stack like the robapieles, no crouch/light awareness like the adults have
/// NEITHER of (this is its own third thing). Unity yaw convention throughout this module: 0 = +Z,
/// forward = `(sin, cos)`.
fn child_can_see(from: Vec3, heading: f32, target: Vec3) -> bool {
    if from.distance_xz(target) > FACELING_CHILD_DETECT_RADIUS {
        return false;
    }
    let dx = target.x - from.x;
    let dz = target.z - from.z;
    let len_sq = dx * dx + dz * dz;
    if len_sq < 1e-6 {
        return true; // standing on top of them
    }
    let len = len_sq.sqrt();
    let dot = (heading.sin() * dx + heading.cos() * dz) / len;
    dot >= FACELING_CHILD_DETECT_HALF_FOV_DEG.to_radians().cos()
}

/// Unit push direction from `from` toward `tpos`, falling back to `heading` when the two are
/// effectively on the same spot.
///
/// The fallback is the whole reason this exists. Normalising by a distance clamped to 0.0001 does
/// not blow up, it does something worse and quieter: it returns (0,0), so a child standing ON the
/// player emits a shove with NO impulse — it spends its cooldown, logs a shove, and moves nobody.
/// Caught by the shove test, which produced three of those from the three members piled on the
/// target. Point-blank is exactly when a push should be hardest, so it gets the direction the
/// child is facing instead.
fn push_direction(from: Vec3, tpos: Vec3, heading: f32) -> (f32, f32) {
    let dx = tpos.x - from.x;
    let dz = tpos.z - from.z;
    let len_sq = dx * dx + dz * dz;
    if len_sq < 1e-4 {
        return (heading.sin(), heading.cos());
    }
    let len = len_sq.sqrt();
    (dx / len, dz / len)
}

/// Which gear this child is in — the whole "corren cuando estás de espaldas" mechanic, in one
/// function.
///
/// Uses the RELEASE cone (the wide one), not the tight freeze cone, and the gap between the two is
/// the point: there is a band where you are facing them enough to keep them at a walk but not
/// enough to stop them dead. Turn a little further and they freeze; turn away and they run. The
/// player never sees a number, they just learn that looking is what slows them down.
///
/// Same-layer only, like every other perception check here.
fn child_gear_speed(from: Vec3, players: &[(PeerId, Vec3, f32)], layer: u8) -> f32 {
    let watched = players.iter().any(|&(_, ppos, pyaw)| {
        world_pos_to_layer(ppos.y) == layer
            && player_is_looking_at_within(ppos, pyaw, from, PHANTOM_STATUE_RELEASE_HALF_FOV)
    });
    match watched {
        true => FACELING_CHILD_CERCO_SPEED_SEEN,
        false => FACELING_CHILD_CERCO_SPEED_UNSEEN,
    }
}

/// `ChildRole::Flank`'s goal point: `side` (persisted per-member, ±1.0) FIXED at role assignment,
/// not recomputed from the target's view cone — ADR-094 point 3 says "dos FLANK toman los lados
/// OPUESTOS (forzados)", literally, not "whichever side reads as hidden". This is what makes the
/// two flankers commit to opposite arcs regardless of the player's own facing.
fn child_flank_position(target: Vec3, target_yaw_deg: f32, side: f32, band: f32) -> Vec3 {
    let view = target_yaw_deg.to_radians();
    let angle = view + side * FACELING_CHILD_FLANK_ANGLE_DEG.to_radians();
    Vec3::new(
        target.x + angle.sin() * band,
        target.y,
        target.z + angle.cos() * band,
    )
}

/// Enmienda 6 — THE BOLT. How fast a child runs once it has your property.
///
/// Below a sprinting player (~5 m/s) on purpose, and it is the most load-bearing number in the
/// theft: catchable, but only if you commit to the chase RIGHT NOW and stop worrying about the
/// four still around you. Any faster and the item is simply gone; any slower and the escape is not
/// a chase, it is a formality.
const FACELING_CHILD_BOLT_SPEED: f32 = 4.6;
/// How hard a bolting thief bends its run away from whoever is chasing it. It is heading for the
/// nest, and the nest may well be past you — without this it would sprint straight into your arms,
/// which reads as stupid rather than frightened.
const FACELING_CHILD_BOLT_EVADE_RADIUS: f32 = 9.0;

/// Enmienda 6 — where the OTHER children stand while a packmate runs with your things: between
/// you and it. Close enough to be in the way, far enough not to just be a wall you shoot through.
pub(super) const FACELING_CHILD_BLOCK_BAND: f32 = 3.5;

/// A point on the line from `player` to `thief`, `FACELING_CHILD_BLOCK_BAND` out from the player.
///
/// This is the whole "los otros te siguen molestando dificultando perseguir al otro": the rest of
/// the pack stops trying to surround you and starts trying to be IN FRONT OF YOU, on the one line
/// you need to run down. They are not faster than you; they only have to make you go round.
pub(super) fn child_block_position(player: Vec3, thief: Vec3) -> Vec3 {
    let dx = thief.x - player.x;
    let dz = thief.z - player.z;
    let len_sq = dx * dx + dz * dz;
    if len_sq < 1e-4 {
        return player;
    }
    let len = len_sq.sqrt();
    Vec3::new(
        player.x + dx / len * FACELING_CHILD_BLOCK_BAND,
        player.y,
        player.z + dz / len * FACELING_CHILD_BLOCK_BAND,
    )
}

/// Enmienda 5 — how far out a `Ring` orbits. Beyond the strike and shove reaches on purpose: this
/// role is not supposed to be able to touch you, it is supposed to be BEHIND you.
pub(super) const FACELING_CHILD_RING_BAND: f32 = 10.0;
/// How far past the target's shoulder a `Ring` aims. Further round than a `Flank`'s 125°: a
/// flanker still wants an angle it can close from, a ring wants your back.
const FACELING_CHILD_RING_ANGLE_DEG: f32 = 155.0;

/// Where a `Ring` wants to stand: behind the shoulder you are NOT turning toward.
///
/// Recomputed against your CURRENT facing every tick, which is what makes it feel like it is
/// working around you rather than standing on a fixed mark — turn toward it and its goal slides
/// round to the other shoulder, so it is always heading for the side you just stopped watching.
pub(super) fn child_ring_position(target: Vec3, target_yaw_deg: f32, side: f32) -> Vec3 {
    let view = target_yaw_deg.to_radians();
    let angle = view + side * FACELING_CHILD_RING_ANGLE_DEG.to_radians();
    Vec3::new(
        target.x + angle.sin() * FACELING_CHILD_RING_BAND,
        target.y,
        target.z + angle.cos() * FACELING_CHILD_RING_BAND,
    )
}

/// Enmienda 5 — a per-child constant in 0..1 that keeps them from moving like one animal.
///
/// Deterministic from the id, same trick and same reason as the robapieles' `derive_hunger`: a
/// play-test has to be repeatable, and `rand` here would mean the same pack never behaves the same
/// way twice. Low is timid (hangs back, slower), high is pushy (closes tighter, faster).
///
/// The variation is deliberately small. Enough that five children stop arriving in formation like
/// a marching band, not so much that one of them reads as a different creature.
fn child_nerve(id: PeerId) -> f32 {
    let mut z = (id as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15);
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^= z >> 31;
    (z >> 11) as f32 / (1u64 << 53) as f32
}

/// Enmienda 4 — how far a child will steer off its goal to avoid standing on a packmate.
///
/// Without this every role computes its point independently and nothing stops two of them
/// occupying it: the pack collapses into a clump you can see, and shoot, all at once. A clump is
/// also strictly worse at its own job — a cerco is only a cerco if there is angle between them.
const FACELING_CHILD_SEPARATION_RADIUS: f32 = 3.2;
/// How hard the push-apart pulls versus the goal. Below 1.0 so converging always wins in the end:
/// they spread out on the way in, they do not orbit forever refusing to close.
const FACELING_CHILD_SEPARATION_WEIGHT: f32 = 0.95;

/// The offset that keeps packmates off each other, as a world-space nudge to add to a goal.
///
/// Falls back to a deterministic sidestep when two are exactly superimposed — a zero-length
/// repulsion would leave them welded together forever, and `mi` picks the direction so the two
/// members of a pair push apart instead of both choosing the same way out.
pub(super) fn separation_offset(from: Vec3, mi: usize, others: &[Vec3]) -> (f32, f32) {
    let mut ax = 0.0;
    let mut az = 0.0;
    for other in others {
        let dx = from.x - other.x;
        let dz = from.z - other.z;
        let d_sq = dx * dx + dz * dz;
        if d_sq >= FACELING_CHILD_SEPARATION_RADIUS * FACELING_CHILD_SEPARATION_RADIUS {
            continue;
        }
        if d_sq < 1e-4 {
            let bearing = (mi as f32) * 2.399_963; // golden angle: neighbours never agree
            ax += bearing.sin();
            az += bearing.cos();
            continue;
        }
        let d = d_sq.sqrt();
        // Linear falloff: full strength when touching, nothing at the radius.
        let push = (FACELING_CHILD_SEPARATION_RADIUS - d) / FACELING_CHILD_SEPARATION_RADIUS;
        ax += dx / d * push;
        az += dz / d * push;
    }
    (
        ax * FACELING_CHILD_SEPARATION_RADIUS * FACELING_CHILD_SEPARATION_WEIGHT,
        az * FACELING_CHILD_SEPARATION_RADIUS * FACELING_CHILD_SEPARATION_WEIGHT,
    )
}

/// Assigns roles by member index, matching ADR-094 point 3's roster for 4 (`Press`, two `Flank`
/// with opposite `flank_offset`, `Cut`) and degrading gracefully for a pack already thinned by a
/// death (3 drops the second `Flank`; 2 keeps `Press`+`Cut`, the two roles that do not depend on
/// a partner; 1 never reaches here — `ChildDriver::step`'s lone-survivor check routes it to
/// `ChildState::Flee` before this runs, once E2c wires it in).
///
/// Re-run every time the roster changes (a member dies), not just once at cerco start — "roles
/// reasignados al morir un miembro" is ADR-094's own words for it.
pub(super) fn assign_roles(members: &mut [ChildMover]) {
    let roles: &[ChildRole] = match members.len() {
        0 => &[],
        1 => &[ChildRole::Press],
        2 => &[ChildRole::Press, ChildRole::Cut],
        3 => &[ChildRole::Press, ChildRole::Flank, ChildRole::Cut],
        4 => &[
            ChildRole::Press,
            ChildRole::Flank,
            ChildRole::Flank,
            ChildRole::Cut,
        ],
        // Five (play-test, Joel 2026-08-24): the extra child DOUBLES THE FRONT rather than adding
        // a third flank or a second cut. The geometry that already works is left untouched — two
        // flanks on opposite sides, one cut on the retreat — and the new pressure lands where the
        // player is already looking. Two coming straight at you reads as "they are committing",
        // where a third flanker would just be one more figure in the corner of the eye.
        5 => &[
            ChildRole::Press,
            ChildRole::Flank,
            ChildRole::Flank,
            ChildRole::Cut,
            ChildRole::Press,
        ],
        // Enmienda 5 — SIX AND UP GO TO THE RING, not to the front. Everything above already
        // converges, so a sixth converger just joins the crowd you are looking at. The extra
        // bodies orbit instead: they never close, they just keep taking the angle you are not
        // watching, which is what turns "a group of enemies" into "how many are behind me".
        6 => &[
            ChildRole::Press,
            ChildRole::Flank,
            ChildRole::Flank,
            ChildRole::Cut,
            ChildRole::Press,
            ChildRole::Ring,
        ],
        7 => &[
            ChildRole::Press,
            ChildRole::Flank,
            ChildRole::Flank,
            ChildRole::Cut,
            ChildRole::Press,
            ChildRole::Ring,
            ChildRole::Ring,
        ],
        _ => &[
            ChildRole::Press,
            ChildRole::Flank,
            ChildRole::Flank,
            ChildRole::Cut,
            ChildRole::Press,
            ChildRole::Ring,
            ChildRole::Ring,
            ChildRole::Ring,
        ],
    };
    let mut next_flank_side = -1.0;
    for (i, m) in members.iter_mut().enumerate() {
        m.role = roles.get(i).copied();
        if m.role == Some(ChildRole::Flank) {
            m.flank_offset = next_flank_side;
            next_flank_side = 1.0; // the second Flank, if any, takes the opposite side
        } else {
            m.flank_offset = 0.0;
        }
    }
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
            attacks: Vec::new(),
            thefts: Vec::new(),
            dropped_loot: Vec::new(),
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
            // ADR-094 punto 4, tercera salida del invariante: "desactivación por lejanía ⇒ suelta
            // donde estaba". Staged BEFORE the despawn loop, which is what erases the positions.
            for mi in 0..self.packs[pi].members.len() {
                let at = net
                    .peers
                    .get(&self.packs[pi].members[mi].id)
                    .map(|p| Vec3::from_array(p.position))
                    .unwrap_or(self.packs[pi].anchor);
                self.stage_loot_drop(pi, mi, at);
            }
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
                            role: None,
                            frozen: false,
                            pending_vocal: None,
                            vocal_seq: 0,
                            vocal_kind: 0,
                            vocal_delay: None,
                            strike_recover: 0.0,
                            progress: ProgressWatch::new(),
                            loot: None,
                            flank_offset: 0.0,
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
                        mind: PackMind::empty(),
                        frozen: false,
                        members,
                        giggle_timer: FACELING_CHILD_GIGGLE_INTERVAL_S,
                        giggle_round: 0,
                        screamer_cooldown: 0.0,
                    });
                    if self.packs.len() >= FACELING_CHILD_PACK_ACTIVE_CAP {
                        return;
                    }
                }
            }
        }
    }

    /// One entity tick for every active pack. `detect_for_pack` runs first and writes the WHOLE
    /// pack's `PackMind` before any member moves, which is the actual mechanism behind ADR-094's
    /// "no hay «avisar»" — every member's movement this tick already sees whatever any member
    /// perceived THIS tick, never last tick's picture.
    /// ADR-094 E2b+ — a hit landed on `victim_id`, one of the children. Twin of
    /// `AdultDriver::apply_damage` and reached from the SAME branch of
    /// `process_pvp_hit_candidate_host`, but the reactions are the pack's, not one mover's:
    ///
    /// * SURVIVES → the whole pack turns on `attacker_id` in the same tick, with no line of sight
    ///   required by anybody. That IS the hive (point 3, "conocimiento instantáneo"): hurting one
    ///   child tells four where you are.
    /// * DIES → every survivor screams AT ONCE ("todos A LA VEZ cuando un miembro muere"), roles
    ///   are re-dealt over the thinned roster ("roles reasignados al morir un miembro"), and a
    ///   pack down to its last child drops to `Flee` instead of pressing a cerco it cannot form.
    ///
    /// Returns whether the child died.
    /// `host_player_pos` is NOT redundant with `net.peers`: the host's own player is the one
    /// participant that never has a `PeerConnection`, so resolving the attacker's position from
    /// the roster alone would leave `last_known_pos` empty for exactly the attacker the host is
    /// most likely to be — and a pack that cannot place you is a pack that never retaliates.
    /// `AdultDriver` dodges this by storing only the id and resolving late; the pack mind stores
    /// the POSITION, so it has to be resolved here.
    pub(super) fn apply_damage(
        &mut self,
        net: &mut NetworkManager,
        victim_id: PeerId,
        attacker_id: PeerId,
        damage: f32,
        host_player_pos: Vec3,
    ) -> bool {
        let Some((pi, mi)) = self.packs.iter().enumerate().find_map(|(pi, pack)| {
            pack.members
                .iter()
                .position(|m| m.id == victim_id)
                .map(|mi| (pi, mi))
        }) else {
            return false;
        };

        let dealt = damage.max(0.0).round() as u8;
        let member = &mut self.packs[pi].members[mi];
        member.health = member.health.saturating_sub(dealt);
        let health_left = member.health;
        info!(
            "MPTRACE step=FL_DMG event=faceling_child_damaged faceling_id={} attacker_id={} damage={} health={}",
            victim_id, attacker_id, dealt, health_left
        );

        if health_left > 0 {
            // The hive learns instantly, and being hurt is perception too — no cone check, no
            // line of sight, and deliberately no `Flee`: a pack that still has numbers answers.
            if self.packs[pi].state != ChildState::Flee {
                let attacker_pos = match attacker_id == net.local_id {
                    true => Some(host_player_pos),
                    false => net
                        .peers
                        .get(&attacker_id)
                        .map(|p| Vec3::from_array(p.position)),
                };
                let mind = &mut self.packs[pi].mind;
                mind.target = Some(attacker_id);
                if let Some(pos) = attacker_pos {
                    mind.last_known_pos = Some(pos);
                }
                mind.lost_for = 0.0;
                if self.packs[pi].state != ChildState::PackStalk
                    && self.packs[pi].mind.last_known_pos.is_some()
                {
                    self.packs[pi].state = ChildState::PackStalk;
                    assign_roles(&mut self.packs[pi].members);
                    for m in &mut self.packs[pi].members {
                        m.pending_vocal = Some(FACELING_CHILD_VOCAL_SCREAM);
                    }
                    info!(
                        "MPTRACE step=FL_PACK event=faceling_pack_cerco_started chunk=({},{}) target={} cause=retaliation",
                        self.packs[pi].home_chunk.0, self.packs[pi].home_chunk.1, attacker_id
                    );
                }
            }
            return false;
        }

        // ADR-094 punto 4, primera salida del invariante: "muerto el ladrón ⇒ el host acuña un
        // item de mundo en el sitio". Leída ANTES del despawn, que es lo que borra el peer.
        let died_at = net
            .peers
            .get(&victim_id)
            .map(|p| Vec3::from_array(p.position))
            .unwrap_or(host_player_pos);
        self.stage_loot_drop(pi, mi, died_at);

        net.despawn_faceling(victim_id);
        self.packs[pi].members.remove(mi);
        let home = self.packs[pi].home_chunk;
        info!(
            "MPTRACE step=FL_DMG event=faceling_child_killed faceling_id={} chunk=({},{}) left={}",
            victim_id,
            home.0,
            home.1,
            self.packs[pi].members.len()
        );

        // "Grito ... todos A LA VEZ cuando un miembro muere" — no chorus spread here on purpose:
        // the spread is what makes the giggles read as several children in several places, and
        // this one has to read as one voice, which is what makes it land as a reaction.
        for m in &mut self.packs[pi].members {
            m.pending_vocal = Some(FACELING_CHILD_VOCAL_SCREAM);
            m.vocal_delay = None;
        }

        match self.packs[pi].members.len() {
            0 => {
                self.packs.remove(pi);
            }
            1 => {
                self.packs[pi].state = ChildState::Flee;
                self.packs[pi].frozen = false;
                self.packs[pi].mind = PackMind::empty();
                let survivor = &mut self.packs[pi].members[0];
                survivor.role = None;
                survivor.flank_offset = 0.0;
                survivor.state_timer = FACELING_CHILD_CALL_INTERVAL_S;
                info!(
                    "MPTRACE step=FL_PACK event=faceling_pack_lone_survivor chunk=({},{}) faceling_id={}",
                    home.0, home.1, survivor.id
                );
            }
            _ => assign_roles(&mut self.packs[pi].members),
        }
        true
    }

    /// ADR-094 point 3 — "un pack reducido a 1 huye a territorio y grita para reagruparse con otro
    /// pack". The merge half of that sentence: a `Flee` straggler that has reached another pack of
    /// its own layer joins it outright.
    ///
    /// Runs as its OWN pass after every pack has ticked, never inside the pack loop: moving a
    /// member between two packs mid-iteration is exactly the kind of aliasing that turns into a
    /// silently skipped pack.
    fn regroup_lone_survivors(&mut self, net: &NetworkManager) {
        let mut merges: Vec<(usize, usize)> = Vec::new(); // (straggler pack, host pack)
        for (si, straggler) in self.packs.iter().enumerate() {
            if straggler.state != ChildState::Flee || straggler.members.len() != 1 {
                continue;
            }
            let Some(spos) = net
                .peers
                .get(&straggler.members[0].id)
                .map(|p| Vec3::from_array(p.position))
            else {
                continue;
            };
            let found = self.packs.iter().enumerate().find(|(hi, host)| {
                *hi != si
                    && host.layer == straggler.layer
                    && host.state != ChildState::Flee
                    && host.members.len() < FACELING_CHILD_PACK_MAX
                    && host
                        .members
                        .iter()
                        .filter_map(|m| net.peers.get(&m.id))
                        .any(|p| {
                            Vec3::from_array(p.position).distance_xz(spos)
                                <= FACELING_CHILD_REGROUP_RADIUS
                        })
            });
            if let Some((hi, _)) = found {
                merges.push((si, hi));
            }
        }

        // Highest index first: removing a pack shifts everything after it, and a host index
        // recorded before the removal would then point at the wrong pack.
        merges.sort_unstable_by_key(|(si, _)| std::cmp::Reverse(*si));
        let mut merged: HashSet<usize> = HashSet::new();
        for (si, hi) in merges {
            // One straggler can be claimed by one host only, and a host that was ITSELF removed
            // as a straggler this pass is no longer a place to merge into.
            if merged.contains(&si) || merged.contains(&hi) {
                continue;
            }
            merged.insert(si);
            let mut pack = self.packs.remove(si);
            let hi = if hi > si { hi - 1 } else { hi };
            let Some(mut member) = pack.members.pop() else {
                continue;
            };
            let id = member.id;
            // Re-home it onto the NEW anchor before it joins. Its `roam_target` was sampled around
            // the pack it came from, and `PackRoam` only ever re-rolls a target after ARRIVING at
            // the current one — so a stale target outside the new patrol circle is a permanent
            // trap: every step gets refused by the leash guard, it never arrives, and it never
            // re-rolls. Also clears the `Flee` call timer, which now means "seconds to next roam
            // roll" in the state it is walking into.
            member.roam_target = self.packs[hi].anchor;
            member.state_timer = FACELING_CHILD_ROAM_MIN_S
                + rand::random::<f32>() * (FACELING_CHILD_ROAM_MAX_S - FACELING_CHILD_ROAM_MIN_S);
            self.packs[hi].members.push(member);
            if self.packs[hi].state == ChildState::PackStalk {
                assign_roles(&mut self.packs[hi].members);
            }
            info!(
                "MPTRACE step=FL_PACK event=faceling_pack_regrouped faceling_id={} into_chunk=({},{}) size={}",
                id,
                self.packs[hi].home_chunk.0,
                self.packs[hi].home_chunk.1,
                self.packs[hi].members.len()
            );
        }
    }

    /// E2c: returns the `Press` blows staged this tick — same channel as `AdultDriver::step`'s.
    pub(super) fn step(
        &mut self,
        net: &mut NetworkManager,
        dt: f32,
        host_player_pos: Vec3,
        host_player_rot: f32,
    ) -> &[PhantomAttack] {
        self.attacks.clear();
        self.thefts.clear();
        let players: Vec<(PeerId, Vec3, f32)> = std::iter::once((
            net.local_id,
            host_player_pos,
            // E2c: the host's REAL yaw. E2b hardcoded 0.0 here with a TODO, which quietly meant
            // the freeze and the flank angles never worked against the host at all — and in a
            // solo session the host is the only player there is, so the pack's headline mechanic
            // was dead exactly where it gets played.
            host_player_rot,
        ))
        .chain(net.peers.iter().filter_map(|(id, p)| {
            if net.is_phantom(*id) || net.is_faceling(*id) || p.relay_only {
                None
            } else {
                Some((*id, Vec3::from_array(p.position), p.rotation))
            }
        }))
        .collect();

        for pi in 0..self.packs.len() {
            self.detect_for_pack(pi, net, dt, &players);
            self.update_freeze_for_pack(pi, net, &players);
            self.update_giggles_for_pack(pi, net, dt);
            for mi in 0..self.packs[pi].members.len() {
                // Every member, every state: see `ChildMover::strike_recover`.
                self.packs[pi].members[mi].strike_recover =
                    (self.packs[pi].members[mi].strike_recover - dt).max(0.0);
                self.tick_member(pi, mi, net, dt, &players);
            }
        }
        self.regroup_lone_survivors(net);
        self.seal_vocals(net);
        &self.attacks
    }

    /// ADR-094 point 3 — schedules one giggle "beat" per pack every
    /// `FACELING_CHILD_GIGGLE_INTERVAL_S` and gives each member its own offset off that beat
    /// (`chorus_delay_fraction`, same determinism reasoning as `phantom.rs`'s own chorus: a
    /// play-test has to be repeatable). The offset's CEILING is what does the storytelling: wide
    /// in `PackRoam` or at the edge of `PackStalk` detection, narrowing toward simultaneous as the
    /// nearest member closes on the target — "risa a coro = el cerco está cerrado".
    fn update_giggles_for_pack(&mut self, pi: usize, net: &NetworkManager, dt: f32) {
        // A child running for its life is not giggling — `Flee` has its own voice
        // (`FACELING_CHILD_VOCAL_CALL`, driven per-member from `tick_member`).
        if self.packs[pi].state == ChildState::Flee {
            return;
        }
        self.packs[pi].screamer_cooldown = (self.packs[pi].screamer_cooldown - dt).max(0.0);

        self.packs[pi].giggle_timer -= dt;
        if self.packs[pi].giggle_timer > 0.0 {
            return;
        }
        self.packs[pi].giggle_timer = FACELING_CHILD_GIGGLE_INTERVAL_S;

        let spread = match (self.packs[pi].state, self.packs[pi].mind.last_known_pos) {
            (ChildState::PackStalk, Some(target)) => {
                let nearest = self.packs[pi]
                    .members
                    .iter()
                    .filter_map(|m| net.peers.get(&m.id))
                    .map(|p| Vec3::from_array(p.position).distance_xz(target))
                    .fold(f32::MAX, f32::min);
                // Normalised against `FACELING_CHILD_CERCO_CLOSED_RADIUS`, the radius that
                // actually ends the stare's protection — NOT against `CERCO_BAND`, which is only
                // where the flankers aim. The whole job of this sound is to hit one voice at the
                // exact moment you stop being safe; measuring it from a different distance than
                // `cerco_is_closed` uses would put the tell a metre off the thing it announces.
                let t = ((nearest - FACELING_CHILD_CERCO_CLOSED_RADIUS)
                    / (FACELING_CHILD_DETECT_RADIUS - FACELING_CHILD_CERCO_CLOSED_RADIUS))
                    .clamp(0.0, 1.0);
                FACELING_CHILD_GIGGLE_SPREAD_MIN_S
                    + t * (FACELING_CHILD_GIGGLE_SPREAD_MAX_S - FACELING_CHILD_GIGGLE_SPREAD_MIN_S)
            }
            _ => FACELING_CHILD_GIGGLE_SPREAD_MAX_S,
        };

        let round = self.packs[pi].giggle_round;
        self.packs[pi].giggle_round = round.wrapping_add(1);
        let (cx, cz) = self.packs[pi].home_chunk;
        for (mi, m) in self.packs[pi].members.iter_mut().enumerate() {
            if m.vocal_delay.is_some() {
                continue; // this member already has a giggle in flight; do not stack
            }
            let key =
                ((cx as u64) << 40) ^ ((cz as u64) << 16) ^ ((mi as u64) << 8) ^ (round as u64);
            m.vocal_delay = Some(chorus_delay_fraction(key) * spread);
        }
    }

    /// Enmienda 3 — which voice this beat is. The giggle is what you hear while they are STILL
    /// COMING; once the ring is shut and they are on top of you it turns into the whisper.
    ///
    /// Same counter, same beat, same spread — only the bank changes. That is what makes the
    /// transition land as one continuous thing getting closer rather than two separate sounds:
    /// the rhythm you have been listening to for the last minute does not break, it just drops to
    /// a whisper next to your ear.
    fn pack_beat_vocal(
        &self,
        pi: usize,
        net: &NetworkManager,
        players: &[(PeerId, Vec3, f32)],
    ) -> u8 {
        if self.packs[pi].state != ChildState::PackStalk {
            return FACELING_CHILD_VOCAL_GIGGLE;
        }
        let Some(target) = self.packs[pi].mind.target.and_then(|tid| {
            players
                .iter()
                .find(|&&(pid, _, _)| pid == tid)
                .map(|&(_, ppos, _)| ppos)
        }) else {
            return FACELING_CHILD_VOCAL_GIGGLE;
        };
        let member_positions: Vec<Vec3> = self.packs[pi]
            .members
            .iter()
            .filter_map(|m| net.peers.get(&m.id))
            .map(|p| Vec3::from_array(p.position))
            .collect();
        match cerco_is_closed(&member_positions, target) {
            true => FACELING_CHILD_VOCAL_WHISPER,
            false => FACELING_CHILD_VOCAL_GIGGLE,
        }
    }

    /// Applies every member's queued `pending_vocal`/`vocal_delay` into `peer.vocal_seq`/
    /// `vocal_kind` — mirror of `phantom.rs::seal_cosmetics`'s own vocal half, run once per tick
    /// after every state/movement arm has had its say, for the same reason: many early exits
    /// inside `tick_member` would otherwise miss a write staged at the decision site.
    fn seal_vocals(&mut self, net: &mut NetworkManager) {
        for pack in &mut self.packs {
            for m in &mut pack.members {
                if let Some(kind) = m.pending_vocal.take() {
                    // Wrapping, never landing back on 0 — the client's `ProxyVocalHook` treats 0
                    // as "never vocalised" (`.claude/rules/pose-relay-proxy-hook-csharp.md`).
                    m.vocal_seq = match m.vocal_seq.wrapping_add(1) {
                        0 => 1,
                        n => n,
                    };
                    m.vocal_kind = kind;
                }
                if let Some(peer) = net.peers.get_mut(&m.id) {
                    peer.vocal_seq = m.vocal_seq;
                    peer.vocal_kind = m.vocal_kind;
                    // ADR-094 punto 4 — the loot, sealed HERE for the same reason the voice is:
                    // `update_player_state` is deliberately left alone (see
                    // `.claude/rules/pose-relay-wire-rust.md` step 6), so every carry write has to
                    // happen in one pass after the movement arms, or an early exit loses it.
                    let (def, count) = m.loot.unwrap_or((0, 0));
                    peer.carry_def = def;
                    peer.carry_count = count.min(u8::MAX as u16) as u8;
                }
            }
        }
    }

    /// ADR-094 punto 4 — the victim answered: this is what the thief actually got. Called from the
    /// game loop's `StealReport` arm.
    ///
    /// Returns false when the thief is already gone (died or was deactivated between the blow and
    /// the answer). That is NOT a silent drop: the caller mints the world item at the victim's own
    /// position instead, because the item has already left the victim's bag and the invariant says
    /// it must exist somewhere.
    pub(super) fn grant_loot(&mut self, thief_id: PeerId, def_id: i32, count: u16) -> bool {
        for pack in &mut self.packs {
            for m in &mut pack.members {
                if m.id == thief_id {
                    m.loot = Some((def_id, count));
                    return true;
                }
            }
        }
        false
    }

    /// Stages a carrying child's loot for the world, at `at`. The single funnel for all three
    /// exits ADR-094 punto 4 names — died, escaped home, deactivated — so the invariant cannot be
    /// half-implemented: any path that removes a child has to come through here or the item is
    /// destroyed, which the ADR forbids in capitals.
    fn stage_loot_drop(&mut self, pi: usize, mi: usize, at: Vec3) {
        if let Some((def, count)) = self.packs[pi].members[mi].loot.take() {
            if count > 0 {
                self.dropped_loot.push((def, count, at));
            }
        }
    }

    /// Updates `PackMind` from every member's perception THIS tick, then the state transitions
    /// that ride on it: `PackRoam` → `PackStalk` the instant anybody is spotted, `PackStalk` →
    /// `PackRoam` after `FACELING_CHILD_GIVE_UP_S` with nobody in range of anybody.
    fn detect_for_pack(
        &mut self,
        pi: usize,
        net: &NetworkManager,
        dt: f32,
        players: &[(PeerId, Vec3, f32)],
    ) {
        // A straggler does not hunt. ADR-094 point 3 makes the lone child harmless BY DESIGN
        // ("el peligro es la geometría del cerco, nunca el niño suelto") — letting a `Flee` pack
        // fall back into `PackStalk` on sight would quietly turn every survivor into a solo
        // stalker, which is the exact fantasy the rule exists to prevent.
        if self.packs[pi].state == ChildState::Flee {
            return;
        }
        let layer = self.packs[pi].layer;
        let mut spotted: Option<(PeerId, Vec3)> = None;
        'search: for m in &self.packs[pi].members {
            let Some(peer) = net.peers.get(&m.id) else {
                continue;
            };
            let from = Vec3::from_array(peer.position);
            for &(pid, ppos, _) in players {
                if world_pos_to_layer(ppos.y) == layer && child_can_see(from, m.heading, ppos) {
                    spotted = Some((pid, ppos));
                    break 'search;
                }
            }
        }

        if let Some((pid, ppos)) = spotted {
            let mind = &mut self.packs[pi].mind;
            let vel = match (mind.target, mind.last_known_pos) {
                (Some(prev_id), Some(prev_pos)) if prev_id == pid && dt > 0.0 => {
                    ((ppos.x - prev_pos.x) / dt, (ppos.z - prev_pos.z) / dt)
                }
                _ => (0.0, 0.0),
            };
            mind.target = Some(pid);
            mind.last_known_pos = Some(ppos);
            mind.last_known_vel = vel;
            mind.lost_for = 0.0;
            if self.packs[pi].state != ChildState::PackStalk {
                self.packs[pi].state = ChildState::PackStalk;
                assign_roles(&mut self.packs[pi].members);
                // ADR-094 point 3: "Grito: al cargar" — the whole pack, the instant the cerco
                // opens. Overwrites whatever giggle a member might have had queued this same
                // tick; a scream always wins (mirrors `phantom.rs`'s one-slot `pending_vocal`).
                for m in &mut self.packs[pi].members {
                    m.pending_vocal = Some(FACELING_CHILD_VOCAL_SCREAM);
                }
                info!(
                    "MPTRACE step=FL_PACK event=faceling_pack_cerco_started chunk=({},{}) target={}",
                    self.packs[pi].home_chunk.0, self.packs[pi].home_chunk.1, pid
                );
            }
        } else if self.packs[pi].state == ChildState::PackStalk {
            self.packs[pi].mind.lost_for += dt;
            if self.packs[pi].mind.lost_for > FACELING_CHILD_GIVE_UP_S {
                self.packs[pi].state = ChildState::PackRoam;
                self.packs[pi].mind = PackMind::empty();
                self.packs[pi].frozen = false;
                for m in &mut self.packs[pi].members {
                    m.role = None;
                    m.flank_offset = 0.0;
                }
            }
        }
    }

    /// Enmienda 4 — PER-MEMBER freeze. "Miras a uno: ese se queda quieto. Los otros siguen."
    ///
    /// ADR-094 point 3 specified this pack-wide, and in play-test that turned the stare into an
    /// off switch for the whole encounter: look at any one child and all five hold still while you
    /// pick them off. Now each child latches on its own, with the same enter/release hysteresis
    /// pair the ADR names ("la histéresis de cono de Statue se reutiliza tal cual") — tight cone to
    /// freeze, wide cone to release. You can always stop the one you are looking at, and never all
    /// of them, which is what makes the pack keep working the angles behind you.
    fn update_freeze_for_pack(
        &mut self,
        pi: usize,
        net: &NetworkManager,
        players: &[(PeerId, Vec3, f32)],
    ) {
        if self.packs[pi].state != ChildState::PackStalk {
            return;
        }
        let layer = self.packs[pi].layer;
        let member_positions: Vec<Vec3> = self.packs[pi]
            .members
            .iter()
            .filter_map(|m| net.peers.get(&m.id))
            .map(|p| Vec3::from_array(p.position))
            .collect();

        // ADR-094 point 4, decided in play-test (Joel, 2026-08-24): ONCE THE RING IS SHUT,
        // LOOKING AT THEM STOPS SAVING YOU. Without this the stare is an absolute defence, the
        // pack can never cash in a cerco it has already won, and point 4's "o sobre un jugador
        // cercado" is unreachable text — the freeze would have covered every case that clause
        // exists for. This is the beat the converging giggles are the tell FOR: they arrive at a
        // single voice exactly when the protection ends.
        //
        // Measured against the target's LIVE position, never `mind.last_known_pos`. With the
        // memory the ring stays "shut" around wherever you WERE for the whole 20 s of
        // `FACELING_CHILD_GIVE_UP_S` — so after every escape the pack would refuse to freeze,
        // from any distance, for twenty seconds. The stare has to fail because they are actually
        // on you, not because they once were.
        let live_target = self.packs[pi].mind.target.and_then(|tid| {
            players
                .iter()
                .find(|&&(pid, _, _)| pid == tid)
                .map(|&(_, ppos, _)| ppos)
        });
        if let Some(target) = live_target {
            if cerco_is_closed(&member_positions, target) {
                if !self.packs[pi].frozen {
                    self.packs[pi].frozen = true;
                    info!(
                        "MPTRACE step=FL_PACK event=faceling_pack_cerco_closed chunk=({},{})",
                        self.packs[pi].home_chunk.0, self.packs[pi].home_chunk.1
                    );
                }
                // Ring shut: nobody freezes, not even the one being stared at.
                for m in &mut self.packs[pi].members {
                    m.frozen = false;
                }
                return;
            }
            self.packs[pi].frozen = false;
        }

        for mi in 0..self.packs[pi].members.len() {
            let Some(mpos) = member_positions.get(mi).copied() else {
                continue;
            };
            let was = self.packs[pi].members[mi].frozen;
            let now = match was {
                // Tight cone to latch on...
                false => players.iter().any(|&(_, ppos, pyaw)| {
                    world_pos_to_layer(ppos.y) == layer && player_is_looking_at(ppos, pyaw, mpos)
                }),
                // ...wide cone to let go, so a child at the edge of vision does not flicker
                // between statue and stride every tick.
                true => players.iter().any(|&(_, ppos, pyaw)| {
                    world_pos_to_layer(ppos.y) == layer
                        && player_is_looking_at_within(
                            ppos,
                            pyaw,
                            mpos,
                            PHANTOM_STATUE_RELEASE_HALF_FOV,
                        )
                }),
            };
            self.packs[pi].members[mi].frozen = now;
        }
    }

    fn tick_member(
        &mut self,
        pi: usize,
        mi: usize,
        net: &mut NetworkManager,
        dt: f32,
        players: &[(PeerId, Vec3, f32)],
    ) {
        let id = self.packs[pi].members[mi].id;
        let Some(peer) = net.peers.get(&id) else {
            return;
        };
        let from = Vec3::from_array(peer.position);
        let layer = self.packs[pi].layer;
        let anchor = self.packs[pi].anchor;

        // Ages this member's queued giggle regardless of movement/freeze state below — a frozen
        // pack staring at you is exactly where the giggle should still land, not less creepy.
        if let Some(left) = self.packs[pi].members[mi].vocal_delay {
            let left = left - dt;
            if left <= 0.0 {
                // Enmienda 3: the bank is chosen HERE, when the beat actually fires, not when it
                // was queued — so a ring that shuts during the delay turns that queued giggle into
                // the whisper it should have been.
                let kind = self.pack_beat_vocal(pi, net, players);
                self.packs[pi].members[mi].vocal_delay = None;
                self.packs[pi].members[mi].pending_vocal = Some(kind);
            } else {
                self.packs[pi].members[mi].vocal_delay = Some(left);
            }
        }

        // ── Enmienda 6 — THE THIEF BOLTS ──────────────────────────────────────────────────────
        //
        // Checked BEFORE the freeze on purpose, and that is the point Joel asked for: a child
        // holding your property DOES NOT STOP WHEN YOU LOOK AT IT. Everything else in the pack
        // obeys the stare, so the one that ignores it is instantly, visually, the one to chase —
        // the mechanic identifies the thief for you without a marker, an outline or a nametag.
        //
        // It also outranks the cerco arms below: a thief has no role any more, it has an errand.
        if self.packs[pi].members[mi].loot.is_some() {
            // Arrived: drop it and go back to being an ordinary child.
            if from.distance_xz(anchor) <= FACELING_CHILD_ARRIVE_EPS {
                self.stage_loot_drop(pi, mi, from);
                if let Some(peer) = net.peers.get_mut(&id) {
                    let yaw = self.packs[pi].members[mi]
                        .heading
                        .to_degrees()
                        .rem_euclid(360.0);
                    peer.update_player_state(from.to_array(), yaw, "idle".into());
                }
                return;
            }

            // Head for the nest, bending away from anyone close enough to grab it. The nest is
            // often PAST the player, and a thief that sprints straight through the person chasing
            // it reads as stupid rather than frightened.
            let mut hx = anchor.x - from.x;
            let mut hz = anchor.z - from.z;
            let to_nest = (hx * hx + hz * hz).sqrt().max(0.0001);
            hx /= to_nest;
            hz /= to_nest;
            for &(_, ppos, _) in players {
                if world_pos_to_layer(ppos.y) != layer {
                    continue;
                }
                let dx = from.x - ppos.x;
                let dz = from.z - ppos.z;
                let d = (dx * dx + dz * dz).sqrt();
                if !(1e-4..FACELING_CHILD_BOLT_EVADE_RADIUS).contains(&d) {
                    continue;
                }
                // Strongest when they are on top of it, gone at the radius.
                let w = (FACELING_CHILD_BOLT_EVADE_RADIUS - d) / FACELING_CHILD_BOLT_EVADE_RADIUS;
                hx += dx / d * w;
                hz += dz / d * w;
            }

            let raw_heading = hx.atan2(hz);
            let heading = steer_around_walls(&mut self.grid_cache, layer, from, raw_heading);
            let step = FACELING_CHILD_BOLT_SPEED * dt;
            let next = Vec3::new(
                from.x + heading.sin() * step,
                from.y,
                from.z + heading.cos() * step,
            );
            // No patrol leash while bolting: it is running TO the anchor, so it can only end up
            // further inside its own territory.
            if is_walkable_grid_gen(&mut self.grid_cache, next, layer) {
                self.packs[pi].members[mi].heading = heading;
                if let Some(peer) = net.peers.get_mut(&id) {
                    let yaw = heading.to_degrees().rem_euclid(360.0);
                    peer.update_player_state(next.to_array(), yaw, "walk_slow".into());
                }
                return;
            }
            // Wedged mid-escape: the same watchdog everything else uses, so a thief cannot end up
            // pinned against a corner holding your item forever.
            if self.packs[pi].members[mi].progress.note(from, anchor, dt) {
                self.stage_loot_drop(pi, mi, from);
                info!("MPTRACE step=FL_STEAL event=steal_thief_wedged_dropped faceling_id={id}");
            }
            if let Some(peer) = net.peers.get_mut(&id) {
                let yaw = heading.to_degrees().rem_euclid(360.0);
                peer.update_player_state(from.to_array(), yaw, "walk_slow".into());
            }
            return;
        }

        // Enmienda 4: THIS member's own latch, not the pack's. The one you are staring at holds
        // still; the rest of the ring keeps working.
        if self.packs[pi].members[mi].frozen {
            if let Some(peer) = net.peers.get_mut(&id) {
                let yaw = self.packs[pi].members[mi]
                    .heading
                    .to_degrees()
                    .rem_euclid(360.0);
                peer.update_player_state(from.to_array(), yaw, "idle".into());
            }
            return;
        }

        match self.packs[pi].state {
            // ADR-094 point 3, "huye a territorio y grita para reagruparse con otro pack". Runs
            // for the anchor and keeps calling once it is there — the call is what makes the
            // merge happen, so it must not stop at the finish line.
            ChildState::Flee => {
                self.packs[pi].members[mi].state_timer -= dt;
                if self.packs[pi].members[mi].state_timer <= 0.0 {
                    self.packs[pi].members[mi].state_timer = FACELING_CHILD_CALL_INTERVAL_S;
                    self.packs[pi].members[mi].pending_vocal = Some(FACELING_CHILD_VOCAL_CALL);
                }

                if from.distance_xz(anchor) <= FACELING_CHILD_ARRIVE_EPS {
                    if let Some(peer) = net.peers.get_mut(&id) {
                        let yaw = self.packs[pi].members[mi]
                            .heading
                            .to_degrees()
                            .rem_euclid(360.0);
                        peer.update_player_state(from.to_array(), yaw, "idle".into());
                    }
                    return;
                }

                let raw_heading = (anchor.x - from.x).atan2(anchor.z - from.z);
                let heading = steer_around_walls(&mut self.grid_cache, layer, from, raw_heading);
                let step = FACELING_CHILD_FLEE_SPEED * dt;
                let next = Vec3::new(
                    from.x + heading.sin() * step,
                    from.y,
                    from.z + heading.cos() * step,
                );
                // No patrol leash here, unlike `PackRoam`: this is a run TOWARD the anchor, so it
                // can only ever end up further inside the territory, never out of it.
                let (pos, anim) = match is_walkable_grid_gen(&mut self.grid_cache, next, layer) {
                    true => {
                        self.packs[pi].members[mi].heading = heading;
                        (next, "walk_slow")
                    }
                    false => (from, "idle"),
                };
                if let Some(peer) = net.peers.get_mut(&id) {
                    let yaw = heading.to_degrees().rem_euclid(360.0);
                    peer.update_player_state(pos.to_array(), yaw, anim.into());
                }
            }
            ChildState::PackStalk => {
                let Some(target) = self.packs[pi].mind.last_known_pos else {
                    return; // detect_for_pack already reset the state if this were stale
                };
                let role = self.packs[pi].members[mi].role;

                // Enmienda 3 — THE SCREAMER and THE SHOVE, both checked before the `Press` blow
                // because both are cheaper events that should get their chance first: a pack that
                // only ever knocks you down has one note, and these two are what fill the minute
                // before it.
                //
                // Resolved against the target's LIVE pose, same rule as everything else that
                // touches the player (never `last_known_pos`).
                let live_target = self.packs[pi].mind.target.and_then(|tid| {
                    players
                        .iter()
                        .find(|&&(pid, _, _)| pid == tid)
                        .map(|&(pid, ppos, pyaw)| (pid, ppos, pyaw))
                });
                if let Some((victim, tpos, tyaw)) = live_target {
                    let dist = tpos.distance_xz(from);
                    let same_layer = world_pos_to_layer(tpos.y) == layer;
                    let facing_away = !player_is_looking_at(tpos, tyaw, from);
                    let clear =
                        same_layer && segment_is_clear(&mut self.grid_cache, layer, from, tpos);
                    let dx = tpos.x - from.x;
                    let dz = tpos.z - from.z;

                    let member_positions: Vec<Vec3> = self.packs[pi]
                        .members
                        .iter()
                        .filter_map(|m| net.peers.get(&m.id))
                        .map(|p| Vec3::from_array(p.position))
                        .collect();
                    let ring_shut = cerco_is_closed(&member_positions, tpos);

                    // ── THE SCREAMER ──
                    // Everything has to line up at once: your back turned, the ring shut, point
                    // blank, and the pack off cooldown. Rare by construction — a jump-scare you
                    // can predict is just a mechanic.
                    if clear
                        && facing_away
                        && ring_shut
                        && dist <= FACELING_CHILD_SCREAMER_REACH
                        && self.packs[pi].screamer_cooldown <= 0.0
                        && self.packs[pi].members[mi].strike_recover <= 0.0
                    {
                        self.packs[pi].screamer_cooldown = FACELING_CHILD_SCREAMER_COOLDOWN_S;
                        self.packs[pi].members[mi].strike_recover = FACELING_CHILD_STRIKE_RECOVERY;
                        self.packs[pi].members[mi].heading = dx.atan2(dz);
                        // TWO attacks, deliberately: `PhantomAttackKind` has no variant that
                        // carries damage AND an impulse, and inventing one would mean a new wire
                        // kind plus a client handler for a combination the client can already
                        // express as two events it has handled since ADR-047.
                        let (ux, uz) =
                            push_direction(from, tpos, self.packs[pi].members[mi].heading);
                        self.attacks.push(PhantomAttack {
                            victim,
                            kind: PhantomAttackKind::Knockback(
                                ux * FACELING_CHILD_SCREAMER_FORCE,
                                uz * FACELING_CHILD_SCREAMER_FORCE,
                            ),
                        });
                        self.attacks.push(PhantomAttack {
                            victim,
                            kind: PhantomAttackKind::Hit(FACELING_CHILD_SCREAMER_DAMAGE),
                        });
                        // Only THIS child screams. The pack-wide scream belongs to the cerco
                        // opening and to a death; here the whole point is that it comes from one
                        // mouth, right behind your ear.
                        self.packs[pi].members[mi].pending_vocal =
                            Some(FACELING_CHILD_VOCAL_SCREAM);
                        self.packs[pi].members[mi].vocal_delay = None;
                        info!(
                            "MPTRACE step=FL_ATK event=faceling_child_screamer faceling_id={} victim_id={} dist={:.2}",
                            id, victim, dist
                        );
                        if let Some(peer) = net.peers.get_mut(&id) {
                            let yaw = self.packs[pi].members[mi]
                                .heading
                                .to_degrees()
                                .rem_euclid(360.0);
                            peer.update_player_state(from.to_array(), yaw, "pickup".into());
                        }
                        return;
                    }

                    // ── THE SHOVE ──
                    // Everyone EXCEPT the `Press`, whose job is the knockdown. No damage, no
                    // stagger state, just a push — the harassment layer.
                    if clear
                        && role != Some(ChildRole::Press)
                        && dist <= FACELING_CHILD_SHOVE_REACH
                        && self.packs[pi].members[mi].strike_recover <= 0.0
                    {
                        self.packs[pi].members[mi].strike_recover = FACELING_CHILD_SHOVE_RECOVERY;
                        self.packs[pi].members[mi].heading = dx.atan2(dz);
                        let (ux, uz) =
                            push_direction(from, tpos, self.packs[pi].members[mi].heading);
                        self.attacks.push(PhantomAttack {
                            victim,
                            kind: PhantomAttackKind::Knockback(
                                ux * FACELING_CHILD_SHOVE_FORCE,
                                uz * FACELING_CHILD_SHOVE_FORCE,
                            ),
                        });
                        info!(
                            "MPTRACE step=FL_ATK event=faceling_child_shove faceling_id={} victim_id={} dist={:.2}",
                            id, victim, dist
                        );
                        if let Some(peer) = net.peers.get_mut(&id) {
                            let yaw = self.packs[pi].members[mi]
                                .heading
                                .to_degrees()
                                .rem_euclid(360.0);
                            peer.update_player_state(from.to_array(), yaw, "pickup".into());
                        }
                        return;
                    }
                }

                // E2c — THE PRESS BLOW. ADR-094 point 4: "el golpe del rol PRESS conectando por
                // la espalda o sobre un jugador cercado hace knockdown". Both halves of that "or"
                // are checked below; the theft the same sentence asks for needs `0x55/0x56` and
                // lands separately.
                //
                // Aimed at the target's LIVE position from `players`, never at
                // `mind.last_known_pos`: a stale memory is the right thing to walk toward and the
                // wrong thing to swing at — it would let a pack that lost you knock you down
                // through a wall it remembers you behind.
                if role == Some(ChildRole::Press)
                    && self.packs[pi].members[mi].strike_recover <= 0.0
                {
                    let live = self.packs[pi].mind.target.and_then(|tid| {
                        players
                            .iter()
                            .find(|&&(pid, _, _)| pid == tid)
                            .map(|&(pid, ppos, pyaw)| (pid, ppos, pyaw))
                    });
                    if let Some((victim, tpos, tyaw)) = live {
                        // Reach AND a clear line, same as the adult's and for the same reason
                        // (ADR-082): without the segment test the pack knocks you down through
                        // the partition it is standing behind.
                        if world_pos_to_layer(tpos.y) == layer
                            && tpos.distance_xz(from) <= FACELING_CHILD_ATTACK_REACH
                            && segment_is_clear(&mut self.grid_cache, layer, from, tpos)
                        {
                            // The SAME predicate that switches the freeze off — see
                            // `cerco_is_closed`'s own doc for why the two must not drift apart.
                            let member_positions: Vec<Vec3> = self.packs[pi]
                                .members
                                .iter()
                                .filter_map(|m| net.peers.get(&m.id))
                                .map(|p| Vec3::from_array(p.position))
                                .collect();
                            let surrounded = cerco_is_closed(&member_positions, tpos);
                            let from_behind = !player_is_looking_at(tpos, tyaw, from);

                            if surrounded || from_behind {
                                // ADR-094 punto 4: the connecting blow knocks down AND steals. Only
                                // if this child's hands are empty — one child, one item. Without
                                // that guard a pack that keeps you down farms your whole bag while
                                // still only ever SHOWING one thing carried, and the carry is the
                                // point ("se le VE llevándose lo tuyo").
                                if self.packs[pi].members[mi].loot.is_none() {
                                    self.thefts.push((id, victim));
                                }
                                let dx = tpos.x - from.x;
                                let dz = tpos.z - from.z;
                                // Face the victim — same reason as the adult's: this path returns
                                // before the movement arm that normally maintains `heading`.
                                self.packs[pi].members[mi].heading = dx.atan2(dz);
                                self.packs[pi].members[mi].strike_recover =
                                    FACELING_CHILD_STRIKE_RECOVERY;
                                let (ux, uz) =
                                    push_direction(from, tpos, self.packs[pi].members[mi].heading);
                                self.attacks.push(PhantomAttack {
                                    victim,
                                    kind: PhantomAttackKind::Knockdown(
                                        FACELING_CHILD_KNOCKDOWN_SECONDS,
                                        ux * FACELING_CHILD_KNOCKDOWN_FORCE,
                                        uz * FACELING_CHILD_KNOCKDOWN_FORCE,
                                    ),
                                });
                                // The whole pack, at once — this is the moment the cerco pays off.
                                for m in &mut self.packs[pi].members {
                                    m.pending_vocal = Some(FACELING_CHILD_VOCAL_SCREAM);
                                    m.vocal_delay = None;
                                }
                                info!(
                                    "MPTRACE step=FL_ATK event=faceling_child_knockdown faceling_id={} victim_id={} surrounded={} from_behind={}",
                                    id, victim, surrounded, from_behind
                                );
                                if let Some(peer) = net.peers.get_mut(&id) {
                                    let yaw = self.packs[pi].members[mi]
                                        .heading
                                        .to_degrees()
                                        .rem_euclid(360.0);
                                    peer.update_player_state(from.to_array(), yaw, "pickup".into());
                                }
                                return;
                            }
                        }
                    }
                }

                // Enmienda 5 — this child's own temperament, 0..1. Shifts how tight it is willing
                // to stand and how fast it moves, by a little. The point is only that five of them
                // stop arriving in formation like a marching band.
                let nerve = child_nerve(id);
                let target_yaw = players
                    .iter()
                    .find(|&&(pid, _, _)| Some(pid) == self.packs[pi].mind.target)
                    .map(|&(_, _, yaw)| yaw)
                    .unwrap_or(0.0);

                // Enmienda 6 — WHILE A PACKMATE RUNS WITH YOUR THINGS, THE REST GET IN THE WAY.
                // Overrides every role: surrounding you stops being the plan the moment there is
                // something to protect, and the pack's whole job becomes making you go round.
                // They are not faster than you — they only have to cost you the seconds the thief
                // needs.
                let fleeing_mate = self.packs[pi]
                    .members
                    .iter()
                    .enumerate()
                    .filter(|(other, m)| *other != mi && m.loot.is_some())
                    .filter_map(|(_, m)| net.peers.get(&m.id))
                    .map(|p| Vec3::from_array(p.position))
                    .next();

                let goal = match (fleeing_mate, role) {
                    (Some(thief_pos), _) => child_block_position(target, thief_pos),
                    (None, role) => match role {
                        Some(ChildRole::Press) | None => target,
                        Some(ChildRole::Flank) => {
                            // Timid ones hang a metre or so further out than pushy ones.
                            let band = FACELING_CHILD_CERCO_BAND * (1.15 - nerve * 0.3);
                            child_flank_position(
                                target,
                                target_yaw,
                                self.packs[pi].members[mi].flank_offset,
                                band,
                            )
                        }
                        // Enmienda 5 — never closes, just keeps taking the shoulder you are not
                        // watching. `flank_offset` is 0.0 for this role out of `assign_roles`, so the
                        // side comes off the member index: adjacent Rings work opposite shoulders
                        // instead of stacking on one.
                        Some(ChildRole::Ring) => {
                            let side = match mi.is_multiple_of(2) {
                                true => 1.0,
                                false => -1.0,
                            };
                            child_ring_position(target, target_yaw, side)
                        }
                        Some(ChildRole::Cut) => {
                            let vel = self.packs[pi].mind.last_known_vel;
                            let retreat_vel = (-vel.0, -vel.1);
                            intercept_point(
                                &mut self.grid_cache,
                                layer,
                                from,
                                target,
                                retreat_vel,
                                FACELING_CHILD_CERCO_SPEED,
                            )
                            .unwrap_or(target)
                        }
                    },
                };
                // Enmienda 4 — KEEP A GAP. Every role computes its point independently, so nothing
                // stopped two of them landing on the same one: the pack collapsed into a clump you
                // could see and shoot all at once, which is also strictly worse at its own job —
                // a cerco is only a cerco if there is angle between them.
                let others: Vec<Vec3> = self.packs[pi]
                    .members
                    .iter()
                    .enumerate()
                    .filter(|(other, _)| *other != mi)
                    .filter_map(|(_, m)| net.peers.get(&m.id))
                    .map(|p| Vec3::from_array(p.position))
                    .collect();
                let (sx, sz) = separation_offset(from, mi, &others);
                let goal = Vec3::new(goal.x + sx, goal.y, goal.z + sz);

                let raw_heading = (goal.x - from.x).atan2(goal.z - from.z);
                let heading = steer_around_walls(&mut self.grid_cache, layer, from, raw_heading);
                // ±10% by temperament, so a pack does not advance as one rigid line.
                let step = child_gear_speed(from, players, layer) * (0.9 + nerve * 0.2) * dt;
                let next = Vec3::new(
                    from.x + heading.sin() * step,
                    from.y,
                    from.z + heading.cos() * step,
                );
                if is_walkable_grid_gen(&mut self.grid_cache, next, layer) {
                    self.packs[pi].members[mi].heading = heading;
                    if let Some(peer) = net.peers.get_mut(&id) {
                        let yaw = heading.to_degrees().rem_euclid(360.0);
                        peer.update_player_state(next.to_array(), yaw, "walk_slow".into());
                    }
                    return;
                }
                if let Some(peer) = net.peers.get_mut(&id) {
                    let yaw = heading.to_degrees().rem_euclid(360.0);
                    peer.update_player_state(from.to_array(), yaw, "walk_slow".into());
                }
            }
            ChildState::PackRoam => {
                // (ADR-094 punto 4's "ladrón que escapa ⇒ lleva el botín al nido" now lives in the
                // bolt arm at the top of this function — since Enmienda 6 a thief runs for the
                // nest the instant it has the loot, without waiting for the cerco to end.)
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
                // Not getting closer for `FACELING_WEDGED_GIVE_UP_S`? Draw somewhere else.
                // `PackRoam`'s own re-roll lives behind the ARRIVE check above, so without this a
                // child that can never arrive never re-rolls — and "never arrives" covers walking
                // the length of a wall forever, not just standing against it.
                if self.packs[pi].members[mi].progress.note(from, target, dt) {
                    match pick_roam_point(
                        &mut self.grid_cache,
                        anchor,
                        FACELING_CHILD_PATROL_RADIUS_M,
                        layer,
                    ) {
                        Some(next_target) => {
                            self.packs[pi].members[mi].roam_target = next_target;
                            info!(
                                "MPTRACE step=FL_NAV event=faceling_child_unwedged faceling_id={} chunk=({},{})",
                                id, self.packs[pi].home_chunk.0, self.packs[pi].home_chunk.1
                            );
                        }
                        // Nothing walkable sampled this attempt: aim at the anchor, which is by
                        // construction a place the pack was able to stand.
                        None => self.packs[pi].members[mi].roam_target = anchor,
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

                let anim = "walk_slow";
                if let Some(peer) = net.peers.get_mut(&id) {
                    let yaw = self.packs[pi].members[mi]
                        .heading
                        .to_degrees()
                        .rem_euclid(360.0);
                    peer.update_player_state(from.to_array(), yaw, anim.into());
                }
            }
        }
    }
}
