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

/// v1 PLACEHOLDER, unmeasured. A member's own forward-cone sighting radius — deliberately its
/// own constant and not a reuse of any `PHANTOM_*` detection range: the robapieles hears and sees
/// light, a child (ADR-094 point 3) only ever gets this one geometric check.
const FACELING_CHILD_DETECT_RADIUS: f32 = 20.0;
const FACELING_CHILD_DETECT_HALF_FOV_DEG: f32 = 60.0;
/// Faster than `FACELING_CHILD_ROAM_SPEED` — this is the hunt, not the wander. Also the
/// `closing_speed` fed to `intercept_point` for `Cut`.
const FACELING_CHILD_CERCO_SPEED: f32 = 2.2;
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
/// A pack at this size does not accept a straggler — keeps ADR-094 point 3's own "packs de 3-4"
/// invariant intact rather than growing a 5-child pack `assign_roles` has no roster for.
const FACELING_CHILD_PACK_MAX: usize = 4;

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
}

pub(super) struct ChildPack {
    pub(super) home_chunk: (i32, i32),
    pub(super) layer: u8,
    /// World-space centre of `home_chunk` — the patrol reference point, fixed at spawn.
    pub(super) anchor: Vec3,
    pub(super) state: ChildState,
    pub(super) mind: PackMind,
    /// ADR-094 point 3: "si el jugador mira a CUALQUIER miembro, se congela el pack ENTERO" —
    /// PACK-level, not per-member: entering needs only one member in ANYONE's tight look-cone,
    /// releasing needs EVERY member out of EVERYONE's wide release-cone. A per-member latch would
    /// let a flanker standing off to the side release on its own while the one being stared at is
    /// still frozen, which is not "cuatro quietos" — it is three creeping away.
    pub(super) frozen: bool,
    pub(super) members: Vec<ChildMover>,
    /// Seconds to the next giggle "beat" (`ChildDriver::update_giggles_for_pack`). Pack-level,
    /// not per-member: the beat fires once and every member queues its own offset off it —
    /// that offset, not this timer, is what spreads or converges the chorus.
    pub(super) giggle_timer: f32,
    /// Bumped every beat, folded into each member's `chorus_delay_fraction` key so two beats
    /// never reuse the same per-member offset (same reason `phantom.rs` keys on `vocal_seq`).
    pub(super) giggle_round: u32,
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

/// `ChildRole::Flank`'s goal point: `side` (persisted per-member, ±1.0) FIXED at role assignment,
/// not recomputed from the target's view cone — ADR-094 point 3 says "dos FLANK toman los lados
/// OPUESTOS (forzados)", literally, not "whichever side reads as hidden". This is what makes the
/// two flankers commit to opposite arcs regardless of the player's own facing.
fn child_flank_position(target: Vec3, target_yaw_deg: f32, side: f32, band: f32) -> Vec3 {
    let view = target_yaw_deg.to_radians();
    let angle = view + side * std::f32::consts::FRAC_PI_2;
    Vec3::new(
        target.x + angle.sin() * band,
        target.y,
        target.z + angle.cos() * band,
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
        _ => &[
            ChildRole::Press,
            ChildRole::Flank,
            ChildRole::Flank,
            ChildRole::Cut,
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
                            role: None,
                            pending_vocal: None,
                            vocal_seq: 0,
                            vocal_kind: 0,
                            vocal_delay: None,
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
            let Some(member) = pack.members.pop() else {
                continue;
            };
            let id = member.id;
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

    pub(super) fn step(&mut self, net: &mut NetworkManager, dt: f32, host_player_pos: Vec3) {
        let players: Vec<(PeerId, Vec3, f32)> = std::iter::once((
            net.local_id,
            host_player_pos,
            0.0, // the host's own yaw is not read here (E2b never freezes/flanks off it — TODO E2c)
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
                self.tick_member(pi, mi, net, dt, &players);
            }
        }
        self.regroup_lone_survivors(net);
        self.seal_vocals(net);
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
                let t = ((nearest - FACELING_CHILD_CERCO_BAND)
                    / (FACELING_CHILD_DETECT_RADIUS - FACELING_CHILD_CERCO_BAND))
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
                }
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

    /// ADR-094 point 3: "si el jugador mira a CUALQUIER miembro, se congela el pack ENTERO ...
    /// miras a otro lado: pasitos". PACK-level, not per-member — see `ChildPack::frozen`'s own
    /// doc for why a per-member latch would be wrong. Entering needs only one member in ANY
    /// player's tight cone; releasing needs EVERY member out of EVERY player's wide cone (the
    /// same enter/release hysteresis pair ADR-094 names explicitly: "la histéresis de cono de
    /// Statue se reutiliza tal cual").
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

        if !self.packs[pi].frozen {
            let any_entered = member_positions.iter().any(|&mpos| {
                players.iter().any(|&(_, ppos, pyaw)| {
                    world_pos_to_layer(ppos.y) == layer && player_is_looking_at(ppos, pyaw, mpos)
                })
            });
            if any_entered {
                self.packs[pi].frozen = true;
                info!(
                    "MPTRACE step=FL_PACK event=faceling_pack_frozen chunk=({},{})",
                    self.packs[pi].home_chunk.0, self.packs[pi].home_chunk.1
                );
            }
        } else {
            let all_released = member_positions.iter().all(|&mpos| {
                players.iter().all(|&(_, ppos, pyaw)| {
                    world_pos_to_layer(ppos.y) != layer
                        || !player_is_looking_at_within(
                            ppos,
                            pyaw,
                            mpos,
                            PHANTOM_STATUE_RELEASE_HALF_FOV,
                        )
                })
            });
            if all_released {
                self.packs[pi].frozen = false;
            }
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
                self.packs[pi].members[mi].vocal_delay = None;
                self.packs[pi].members[mi].pending_vocal = Some(FACELING_CHILD_VOCAL_GIGGLE);
            } else {
                self.packs[pi].members[mi].vocal_delay = Some(left);
            }
        }

        if self.packs[pi].frozen {
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
                let goal = match role {
                    Some(ChildRole::Press) | None => target,
                    Some(ChildRole::Flank) => {
                        let target_yaw = players
                            .iter()
                            .find(|&&(pid, _, _)| Some(pid) == self.packs[pi].mind.target)
                            .map(|&(_, _, yaw)| yaw)
                            .unwrap_or(0.0);
                        child_flank_position(
                            target,
                            target_yaw,
                            self.packs[pi].members[mi].flank_offset,
                            FACELING_CHILD_CERCO_BAND,
                        )
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
                };
                let raw_heading = (goal.x - from.x).atan2(goal.z - from.z);
                let heading = steer_around_walls(&mut self.grid_cache, layer, from, raw_heading);
                let step = FACELING_CHILD_CERCO_SPEED * dt;
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
