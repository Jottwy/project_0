//! Authoritative game loop. Runs at 60hz, processes IPC input from Unity,
//! simulates local state (world, entities, stats), manages P2P networking,
//! and streams `WorldState` back at 10hz. See ARCHITECTURE_V1.md §6.1.

use std::collections::{HashMap, HashSet};
use std::time::{Duration, Instant};

use log::{debug, info, warn};
use tokio::sync::{broadcast, mpsc};
use tokio::time::{interval, MissedTickBehavior};

use crate::ipc::{
    ClientMessage, GameEvent, GridChunkData, LocalPlayerState, MovementDelta, PlayerAction,
    PlayerInput, RemotePlayerState, ServerMessage, StatsView, WorldState,
};
use crate::network::protocol::PacketPayload;
use crate::network::sync;
use crate::network::{BoundedDedupeSet, NetworkEvent, NetworkManager, PeerId};
use crate::player::Player;
use crate::utils::{world_to_chunk, ChunkPos, Vec3, CHUNK_SIZE};
use crate::world::collision::{resolve_safe_spawn, Level0Collision};
use crate::world::grid_gen::{
    resolve_move_grid_gen, resolve_move_grid_gen_ex, world_pos_to_layer, GridGenChunkCache,
};
use crate::world::phantom_spawn;
use crate::world::World;

const TICK_HZ: u64 = 60;
const TICK_DURATION: Duration = Duration::from_nanos(1_000_000_000 / TICK_HZ);
/// WorldState to Unity at 10hz.
const WORLD_STATE_EVERY: u64 = 6;
/// ADR-009 §2: authoritative movement delta to Unity at 20hz (60 / 3).
const MOVEMENT_DELTA_EVERY: u64 = 3;
/// Entity AI runs at 10hz.
const ENTITY_TICK_EVERY: u64 = 6;
/// DISABLED 2026-07-07: PvE entity damage (Lurker/Crawler/Shadow) — diagnosed as the cause of
/// silent walking deaths. Entities are invisible (EntityRenderer never wired into the STP scene)
/// and the damage has no client feedback (StatInterpolator applies health via SetHealthSilent;
/// Unity has no "damage_taken" handler). AI (aggro/movement/attack events) keeps running; only
/// the health application is gated. Re-enable ONLY with an explicit decision (renderer +
/// feedback + rebalance) — see STATE.md "Deuda: entidades PvE".
const ENTITY_DAMAGE_ENABLED: bool = false;
/// Ownership + teleportation checked at 1hz.
const SLOW_TICK_EVERY: u64 = 60;
/// Player position broadcast to peers at 10hz.
const NET_BROADCAST_EVERY: u64 = 6;
/// Heartbeat to peers every 1s.
const HEARTBEAT_EVERY: u64 = 60;
/// Chunk state broadcast at 5hz.
const CHUNK_BROADCAST_EVERY: u64 = 12;
/// ADR-029 V0 (invulnerability amendment): ticks a respawned player remains immune to PvP
/// damage. Derived from TICK_HZ so "3 seconds" always means 3 real seconds regardless of
/// tick rate — never a magic number independent of it.
const RESPAWN_INVULN_TICKS: u32 = (TICK_HZ * 3) as u32;
/// ADR-032: host-only world autosave cadence (~3 real minutes). Derived from TICK_HZ so the
/// interval holds regardless of tick rate. 60*180 = 10800 ticks @60Hz.
const AUTOSAVE_EVERY: u64 = TICK_HZ * 180;

const BASE_SPEED: f32 = 5.0;
const SPRINT_MULT: f32 = 1.5;
/// ADR-009 Option B: tolerance added to the speed cap when validating the
/// client-reported velocity before accepting its predicted position.
const SPEED_TOLERANCE: f32 = 0.5;
/// Stamina drained per second while the client reports the run move-state.
const RUN_STAMINA_DRAIN: f32 = 15.0;
/// ADR-014: delay between granting a pickup and removing the item from `stp_items` (the
/// "juicy frame" of the gesture). Host-only. COUPLED to PickupSpeed (x2 in
/// ProxyAnimatorControllerBuilder) → if PickupSpeed changes, re-tune this by hand.
const PICKUP_REMOVE_DELAY: Duration = Duration::from_millis(600);

/// Forces the god-traversal COLLISION BYPASS on (the player's claimed pose is trusted without
/// clamping against the BACKEND world, which doesn't match the rendered ChunkStreamer — the known
/// world-migration debt). NOTE (ADR-016 slice 1): this no longer gates death — that is a SEPARATE
/// flag (`DEV_INVINCIBLE`), so the robapieles can kill the host while the collision bypass stays on.
/// TURNED OFF (2026-07-01): was left `true` and was letting the client's claimed position drift
/// uncorrected into hostile-entity melee range (the "4th damage source" investigation) — server
/// validation is the trade-off accepted over god-mode's phantom-wall-avoidance. If the backend/
/// ChunkStreamer world mismatch causes visible rubber-banding, that is the ALREADY-DOCUMENTED
/// world-authoritative migration debt (see STATE.md), not a new regression — re-enable via
/// `DEV_GOD_TRAVERSAL=1` env for debugging without re-hardcoding this.
const DEV_GOD_TRAVERSAL_HARDCODED: bool = false;

/// ADR-016 slice 2: phantom walk speed (m/s). Calibrated to human walking so the client's
/// velocity-derived locomotion (ADR-013) reads it as walking, not teleporting — the proxy
/// teleport guards only trip on chunk-displacement-scale jumps, far above this. Calibrable.
const PHANTOM_WALK_SPEED: f32 = 3.0;
/// ADR-016 slice 2: on a head-on block the phantom re-orients by a random turn in
/// [90°, 270°] — never straight back into the wall, never a no-op — so it never stalls and
/// wanders the maze (no smart pathing yet).
const PHANTOM_TURN_MIN: f32 = std::f32::consts::FRAC_PI_2;
const PHANTOM_TURN_MAX: f32 = std::f32::consts::PI * 1.5;
/// Initial heading (yaw, radians) of a freshly driven phantom: +X (east of the host spawn).
const PHANTOM_INITIAL_HEADING: f32 = std::f32::consts::FRAC_PI_2;
/// ADR-016 slice 4: how long the phantom holds the FAKED "pickup" animation. Aligned with
/// ADR-011's ~1s trigger-flank window (the real gesture's duration is client-owned, via the
/// Animator exitTime). Movement is paused for this window so it reads like a player that is
/// input-locked while picking up. PURE presentation — no real pickup state is ever touched.
const PHANTOM_PICKUP_GESTURE: Duration = Duration::from_millis(1000);
/// ADR-016 slice 4: cooldown between faked pickups while patrolling (theater — no item needed).
/// Calibrable; small enough to see it recur in play-test.
const PHANTOM_PICKUP_INTERVAL: Duration = Duration::from_secs(6);
/// ADR-016 (tell phase): behavioral tell #2 — the phantom periodically goes UNNATURALLY STILL
/// (a "stare"): it stops dead, holding idle + a fixed facing for `PHANTOM_STARE_DURATION`, on a
/// near-perfect `PHANTOM_STARE_INTERVAL` cadence. Subtle (rare, brief) but learnable — humans
/// don't freeze like a metronome. Calibrable: raise the interval / lower the duration to make it
/// subtler. PURELY BEHAVIORAL — no wire flag; observable only by watching, never by reading packets.
const PHANTOM_STARE_INTERVAL: Duration = Duration::from_secs(30);
const PHANTOM_STARE_DURATION: Duration = Duration::from_millis(2500);
/// ADR-016 — OBSERVATION LEASH (TEMPORARY, play-test only): keep the phantom within this radius
/// (m) of its spawn so it stays in view to observe the cloned name, the stare, and two same-named
/// players. If it drifts past this, its heading is re-aimed at the spawn instead of wandering off.
/// NOT the final behavior: the robapieles must roam freely — free wander returns once the
/// world→backend migration fixes collision (today it phases through visibly-rendered walls).
/// Calibrable.
const PHANTOM_WANDER_RADIUS: f32 = 9.0;

// ADR-016 slice 2 — detection + chase. D1=(a): distance + a forward view cone ONLY, with NO
// geometry line-of-sight. The phantom's collision is against the BACKEND world, not the
// rendered ChunkStreamer, so a geometric raycast would test the WRONG walls; until the
// world→backend migration, distance+angle is the honest prototype. Enter chase within
// DETECT_RADIUS while the target is inside the cone; leave past LOSE_RADIUS (hysteresis so it
// doesn't flicker at the edge). Chase moves faster than the wander walk.
const PHANTOM_DETECT_RADIUS: f32 = 15.0;
const PHANTOM_LOSE_RADIUS: f32 = 25.0;
const PHANTOM_DETECT_HALF_FOV: f32 = std::f32::consts::FRAC_PI_3; // 60° half → 120° cone

// ADR-016 slice 3a — Stalker FSM (Wander / Spotted / Stalk / Sprint). Peek/Search land in 3b.
const PHANTOM_STALK_DISTANCE: f32 = 9.0; // STALK keeps roughly this gap from the player
const PHANTOM_SPRINT_SPEED: f32 = 9.0; // top sprint speed (vs walk 3.0)
const PHANTOM_SPRINT_RAMP: f32 = 1.5; // seconds to ramp WALK → SPRINT
const PHANTOM_SPOTTED_MIN: f32 = 3.0; // SPOTTED stare duration range (s)
const PHANTOM_SPOTTED_MAX: f32 = 8.0;
const PHANTOM_STALK_PATIENCE: f32 = 25.0; // seconds stalking before it lunges
const PHANTOM_WANDER_PAUSE_MIN: f32 = 3.0; // WANDER "looking at a wall" pause range (s)
const PHANTOM_WANDER_PAUSE_MAX: f32 = 12.0;
const PHANTOM_WANDER_PAUSE_CHANCE: f32 = 0.007; // per-tick (≈20% over 3 s at 10 Hz)
const PHANTOM_SPRINT_RANDOM_CHANCE: f32 = 0.008; // per-tick unpredictable lunge

// ADR-016 slice 3b-P1 — STATUE (weeping-angel: freezes while observed) + sound detection.
// All inputs come from data the backend already has (player yaw; target speed derived from the
// position delta — peers send no velocity/move_state). NO wire/IPC change.
const PHANTOM_STATUE_RANGE: f32 = 20.0; // only freezes if the watching player is within this (m)
const PHANTOM_STATUE_LOOK_HALF_FOV: f32 = std::f32::consts::FRAC_PI_6; // 30° half → 60° player cone
const PHANTOM_STATUE_MAX: f32 = 6.0; // max seconds frozen → then it lunges (SPRINT)
const PHANTOM_RUN_SPEED_THRESHOLD: f32 = 4.5; // target speed (m/s) read as "running" (above walk)
const PHANTOM_SOUND_BONUS: f32 = 8.0; // extra detect radius (m) when the player is running
const PHANTOM_SPEED_SANITY_MAX: f32 = 30.0; // ignore deltas above this (teleport/chunk-displace)
const PHANTOM_SPOTTED_SOUND_MIN: f32 = 1.0; // shorter stare when alerted by noise (s)
const PHANTOM_SPOTTED_SOUND_MAX: f32 = 2.0;
// Fluidity (slice 3b-P1 follow-up): ease `heading` toward the player instead of snapping at
// 10 Hz (which reads as lag). rad/s — STALK tracks, SPRINT tracks hard, STATUE turns its head.
// ADR-041 — noise as a stimulus. A gunshot is the loudest thing in the game and until now it did
// not exist for the AI at all.
/// Hard clamp on a reported loudness. The client owns the weapon table, but a forged or buggy value
/// must not turn one shot into a world-wide summons.
const PHANTOM_NOISE_MAX_LOUDNESS: f32 = 600.0;
/// Localization error as a fraction of distance. A rifle at 500 m is HEARD — physically it carries
/// for kilometres and a long corridor acts as a waveguide — but it is not LOCATED. At 50 m this
/// lands almost on top of you; at 500 m it lands ~40 m out and the creature has to search. That
/// gap is the whole design: exact positions at long range would be an aimbot with a delay.
const PHANTOM_NOISE_ERROR_FRAC: f32 = 0.08;
/// Travel speed toward a noise: a fast walk, NOT a sprint. 500 m at 3 m/s is ~2.8 minutes of dread;
/// covering the same ground in 55 s reads as homing.
const PHANTOM_NOISE_TRAVEL_SPEED: f32 = 3.0;
/// A noise goes cold after this long in transit without a fresh one. Without it the phantom would
/// cross the map chasing a shot fired five minutes ago.
const PHANTOM_NOISE_EXPIRY: f32 = 90.0;
/// Patience once it ARRIVES. The 12 s of a normal SEARCH is far too short after a journey of
/// minutes — arriving and immediately shrugging would waste the whole approach.
const PHANTOM_NOISE_SEARCH_PATIENCE: f32 = 30.0;
// ADR-040 perception. Sound stops being a binary "is it sprinting?" and becomes three tiers, which
// is what makes crouching a real choice instead of a cosmetic pose.
/// Below this planar speed you are standing still and make no noise at all.
const PHANTOM_WALK_NOISE_SPEED: f32 = 0.6;
/// How far a STANDING walk carries. A run still carries `DETECT + SOUND_BONUS`.
const PHANTOM_WALK_HEAR_RADIUS: f32 = 9.0;
/// Crouching shrinks the VISUAL radius to this fraction — it does not make you invisible, it makes
/// you something it has to be close to notice. Combined with the existing 120° cone, crouching
/// behind it is genuinely safe; crouching in front of its face is not.
const PHANTOM_CROUCH_SIGHT_FACTOR: f32 = 0.55;
// ADR-040 Fase 4 — SEARCH.
/// How long it hunts around the spot it last saw you before giving up and going back to wandering.
const PHANTOM_SEARCH_MAX: f32 = 12.0;
/// It treats the remembered spot as reached inside this radius.
const PHANTOM_SEARCH_ARRIVE: f32 = 2.0;
/// Speed while investigating: slower than a walk. It is looking, not commuting.
const PHANTOM_SEARCH_SPEED: f32 = 2.2;
// ADR-040 — replan policy. Not tuning knobs pulled from thin air: 0.6 s is ~6 entity ticks, short
// enough that a route stays honest while you move and long enough that the amortized search cost is
// 1.67/s. The drift threshold is under one chunk-cell pair, so the phantom re-routes when you round
// a corner but not when you strafe.
const PHANTOM_REPLAN_INTERVAL: f32 = 0.6;
const PHANTOM_REPLAN_GOAL_DRIFT: f32 = 4.0;
/// ADR-043 — how many steps the replans of the active movers are spread over, capping the searches
/// in any one step at ceil(N / stride). 3 against `PHANTOM_ACTIVE_CAP` = 6 means at most 2.
///
/// Needed because `PHANTOM_REPLAN_INTERVAL` is a fixed 0.6 s: movers that woke on the same tick
/// keep their `nav_age` in phase forever and all come due together, so the cost is a burst, not
/// the average.
const PHANTOM_REPLAN_STRIDE: u64 = 3;
/// A waypoint counts as reached inside this radius. Below ~1 m the phantom can orbit a waypoint it
/// keeps almost-touching; this is under half a cell, so it never skips a corner either.
const PHANTOM_WAYPOINT_ARRIVE: f32 = 1.25;
const PHANTOM_TURN_SPEED_STALK: f32 = 8.0;
const PHANTOM_TURN_SPEED_SPRINT: f32 = 15.0;
const PHANTOM_TURN_SPEED_STATUE: f32 = 3.0;
// ADR-016 slice 1 (phantom damage) — host-only (joiners = Fase 7 debt). Damage flows through the
// PhantomAttack channel, NEVER the pickup path (ADR-016 invariant).
const PHANTOM_ATTACK_DAMAGE: f32 = 35.0; // frontal SPRINT hit (non-lethal; bounces to STALK)
const PHANTOM_KNOCKBACK_RANGE: f32 = 3.0; // STATUE→SPRINT shove only within this (m)
const PHANTOM_KNOCKBACK_FORCE: f32 = 3.0; // shove speed (m/s); client applies via SetVelocity

// ─── ADR-043 — population: which of the world's robapieles are actually simulated ───
/// How often the active population is reconciled with the draw. 1 Hz, not the 10 Hz entity tick:
/// the set of nearby blocks changes at walking pace (a 200 m block takes ~40 s to cross), so ten
/// scans a second would repeat the same answer nine times.
const PHANTOM_POPULATION_SYNC_INTERVAL: f32 = 1.0;
/// A drawn phantom wakes up when a real player is within this of it (m).
const PHANTOM_ACTIVATE_RADIUS: f32 = 150.0;
/// …and only sleeps again past THIS (m). The gap is hysteresis, and it is not decoration: with a
/// single threshold, a player standing near it would spawn and despawn the same creature every
/// second, which on the client is an avatar blinking in and out at the edge of view distance.
const PHANTOM_DEACTIVATE_RADIUS: f32 = 200.0;
/// Hard cap on simultaneously simulated phantoms. Nearest to a player wins when it binds.
///
/// Sized against the chunk cache, not guessed: `GRID_CACHE_MAX_CHUNKS` (256) holds ~16 movers'
/// A\* working sets, so this leaves better than 2× headroom. Raising it past that headroom returns
/// the mutual-eviction thrash the cache cap was raised to remove — the two constants are one
/// decision (see `GRID_CACHE_MAX_CHUNKS`).
const PHANTOM_ACTIVE_CAP: usize = 6;

/// ADR-032: where the host reads/writes its world save. `SAVE_PATH` env overrides; default is
/// `./saves/world_{seed}.json`.
fn resolve_save_path(seed: u64) -> std::path::PathBuf {
    std::env::var("SAVE_PATH")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|_| std::path::PathBuf::from(format!("./saves/world_{seed}.json")))
}

/// Re-siembra los cuatro asignadores de id de proceso desde los rosters recién cargados.
/// Devuelve `(drop, building, carryable, group)` ya almacenados, solo para el log.
///
/// Sin esto, tras cargar una partida los cuatro `AtomicU32` arrancan otra vez en su base y el
/// PRIMER `place` de la sesión reacuña un id que ya existe en el roster. Como `process_stp_demolish`
/// resuelve la pieza por `position(|b| b.id == …)`, demoler la pieza nueva borra la VIEJA. Es
/// pérdida de datos silenciosa, y ocurre hoy con un solo jugador.
///
/// Se siembra desde el máximo DENTRO DEL PROPIO RANGO, no desde `max(roster) + 1` a secas: los
/// rangos están particionados a propósito (`0x4000_0000` drops, `0x6000_0000` construcciones,
/// `0x7000_0000` carryables, y por debajo de `0x4000_0000` acuña el Unity del host). Sembrar desde
/// el máximo global metería el asignador de drops en el rango de construcciones y garantizaría la
/// colisión en vez de evitarla.
fn reseed_stp_id_allocators(net: &NetworkManager) -> (u32, u32, u32, u32) {
    use std::sync::atomic::Ordering;

    /// Primer id libre del rango `[base, end)`, ignorando lo que caiga fuera.
    fn next_free(base: u32, end: u32, ids: impl Iterator<Item = u32>) -> u32 {
        ids.filter(|id| *id >= base && *id < end)
            .max()
            .map(|m| m.saturating_add(1))
            .unwrap_or(base)
            .max(base)
    }

    let drop_id = next_free(
        STP_DROP_ID_BASE,
        STP_BUILDING_ID_BASE,
        net.stp_items.iter().map(|i| i.id),
    );
    let building_id = next_free(
        STP_BUILDING_ID_BASE,
        STP_CARRYABLE_ID_BASE,
        net.stp_buildings.iter().map(|b| b.id),
    );
    let carryable_id = next_free(
        STP_CARRYABLE_ID_BASE,
        u32::MAX,
        net.stp_carryables.iter().map(|c| c.id),
    );
    // `group_id` no vive en un rango alto: 0 significa "pieza suelta", así que el primer grupo
    // válido es 1 y se siembra por encima del mayor grupo cargado.
    let group_id = net
        .stp_buildings
        .iter()
        .map(|b| b.group_id)
        .max()
        .unwrap_or(0)
        .saturating_add(1)
        .max(1);

    NEXT_STP_DROP_ID.store(drop_id, Ordering::Relaxed);
    NEXT_STP_BUILDING_ID.store(building_id, Ordering::Relaxed);
    NEXT_STP_CARRYABLE_ID.store(carryable_id, Ordering::Relaxed);
    NEXT_STP_GROUP_ID.store(group_id, Ordering::Relaxed);

    (drop_id, building_id, carryable_id, group_id)
}

/// ADR-032: apply a loaded save over a freshly-generated host world. Restores corpses/chests, the
/// corpse-id allocator (defensively above both the saved counter and any loaded id), the four
/// host-authoritative STP rosters, and the host player's durable slice.
fn hydrate_from_save(
    world: &mut World,
    player: &mut Player,
    net: &mut NetworkManager,
    save: crate::persistence::save::SaveFile,
) {
    world.corpses.clear();
    for c in save.corpses {
        world.corpses.insert(c.id, c);
    }
    let max_corpse_id = world.corpses.keys().copied().max().unwrap_or(0);
    let next_id = save
        .next_corpse_id
        .max(max_corpse_id.wrapping_add(1))
        .max(1);
    world.set_next_corpse_id(next_id);

    net.stp_items = save.stp_items;
    net.stp_buildings = save.stp_buildings;
    net.stp_carryables = save.stp_carryables;
    net.stp_harvestables = save.stp_harvestables;

    let (drop_id, building_id, carryable_id, group_id) = reseed_stp_id_allocators(net);

    // `occupied_stp_cells` es estado DERIVADO y no se persiste — se reconstruye aquí o queda
    // vacío tras cargar, con lo que el dedup de celda por pose deja de proteger a todas las
    // piezas ya existentes: la primera colocación sobre el socket de una pieza guardada se
    // aceptaría, duplicando la construcción en ese punto. Solo cuentan las de grupo, que son
    // las únicas que el dedup vigila (`group_id != 0`; las sueltas pueden apilarse a propósito).
    let group_cells: Vec<(i32, i32, i32, i32)> = net
        .stp_buildings
        .iter()
        .filter(|b| b.group_id != 0)
        .map(|b| stp_pose_cell(b.position, b.rotation))
        .collect();
    net.occupied_stp_cells.clear();
    net.occupied_stp_cells.extend(group_cells);
    let rederived_cells = net.occupied_stp_cells.len();

    if let Some(p) = save.host_player {
        player.stats = p.stats;
        player.position = p.position;
        player.rotation = p.rotation;
        player.inventory = p.inventory;
        player.equipment = p.equipment;
        player.held_item = p.held_item;
        player.respawn_point = p.respawn_point;
        player.stp_inventory = p.stp_inventory;
    }

    info!(
        "ADR-032: world hydrated from save (corpses={}, buildings={}, items={}, carryables={}, harvestables={}, next_corpse_id={}, next_drop_id=0x{:08x}, next_building_id=0x{:08x}, next_carryable_id=0x{:08x}, next_group_id={}, occupied_cells={})",
        world.corpses.len(),
        net.stp_buildings.len(),
        net.stp_items.len(),
        net.stp_carryables.len(),
        net.stp_harvestables.len(),
        next_id,
        drop_id,
        building_id,
        carryable_id,
        group_id,
        rederived_cells
    );
}

pub async fn run(
    mut from_clients: mpsc::Receiver<ClientMessage>,
    to_clients: broadcast::Sender<ServerMessage>,
    mut net: NetworkManager,
) {
    let mut player = Player::new(net.local_id, &net.local_name);
    let mut world = World::new(net.world_seed);

    // ADR-032: host-only world persistence. Load BEFORE generating/spawning so a persisted seed
    // (and player position) win. Non-host backends never load/save — world state isn't
    // authoritative there (joiners adopt the host's world via WorldSync).
    let save_path = resolve_save_path(net.world_seed);
    let mut session_name = net.local_name.clone();
    let mut loaded_save = if net.is_host {
        crate::persistence::save::load_or_fresh(&save_path)
    } else {
        None
    };
    if let Some(save) = &loaded_save {
        if save.world_seed != net.world_seed {
            warn!(
                "ADR-032: save world_seed {} differs from launch WORLD_SEED {}; adopting saved seed",
                save.world_seed, net.world_seed
            );
        }
        net.world_seed = save.world_seed;
        world = World::new(save.world_seed);
        session_name = save.session_name.clone();
    }

    let dt = 1.0 / TICK_HZ as f32;
    let entity_dt = dt * ENTITY_TICK_EVERY as f32;
    let dev_freeze_survival = env_flag_enabled("DEV_FREEZE_SURVIVAL");
    let dev_god_traversal = DEV_GOD_TRAVERSAL_HARDCODED || env_flag_enabled("DEV_GOD_TRAVERSAL");
    // ADR-016 slice 1: death/respawn is now SEPARATE from the collision bypass. God traversal
    // keeps collision off (world-migration debt) while the player can still die (so the phantom
    // can kill). Set DEV_INVINCIBLE to disable death/respawn for debugging. Default OFF.
    let dev_invincible = env_flag_enabled("DEV_INVINCIBLE");
    // ADR-016 + ADR-043: this is NO LONGER the existence switch for the robapieles. Since ADR-043
    // the world populates itself from the seed and a normal build has creatures in it; what this
    // flag still does — and the only thing it does — is drop ONE extra phantom right next to the
    // host at boot, which is what you want when debugging a specific behaviour instead of walking
    // until you meet one. It is exempt from the population reconciler (`anchor: None`).
    let debug_spawn_phantom = env_flag_enabled("DEBUG_SPAWN_PHANTOM");

    let mut ticker = interval(TICK_DURATION);
    ticker.set_missed_tick_behavior(MissedTickBehavior::Skip);

    let mut tick: u64 = 0;
    let mut received_input = PlayerInput::default();
    let mut has_received_input = false;
    // ADR-009: ack the last input the server actually ACCEPTED (not merely
    // received). u32::MAX = "nothing accepted yet"; input_seq is 0-based, so the
    // first real input (seq 0) is processed correctly.
    let mut last_accepted_input_seq: u32 = u32::MAX;
    // Authoritative velocity echoed in the 20 Hz delta: the accepted client
    // velocity, or zero when the speed cap rejected the move (held in place).
    let mut authoritative_velocity = Vec3::ZERO;
    let mut processed_interactions: HashSet<(u16, u64)> = HashSet::with_capacity(256);
    // Track the last chunk position used for ownership so we only call
    // update_ownership when the player crosses a chunk boundary, not every tick.
    let mut last_ownership_chunk: Option<ChunkPos> = None;
    // ADR-025 respawn-on-demand: edge flag so a dead player (health stays 0 until the client
    // sends respawn_request) emits player_died exactly ONCE, not every tick. Reset on revive.
    let mut death_announced = false;
    // ADR-016 slice 2: host-only driver that walks phantom peers (the robapieles) each
    // entity tick, resolving collision via ADR-017's sim-only chunk cache.
    let mut phantom_driver = PhantomDriver::new(net.world_seed);
    // ADR-032 (snap de sesión restaurada): armed by the hydration branch below. The
    // "session_restored" event CANNOT be emitted at hydration time — Unity's IPC client hasn't
    // connected yet (broadcast to zero receivers = dropped) — so it is deferred until the first
    // PlayerInput proves the client is alive and subscribed. It reuses ONLY the applier's snap
    // mechanism (a position-carrying arming event, same shape as player_respawned) and is a
    // DISTINCT event type on purpose: RespawnRequester listens for player_respawned and would
    // force the native STP respawn chain (SetHealthSilent(0) + RestoreHealth) at boot.
    let mut pending_restore_snap = false;

    // Bootstrap: host/solo creates the authoritative initial structure before
    // loading the surrounding ownership radius. Joiners wait for host WorldSync.
    //
    // `spawn_resolved` tracks whether the player has been placed on a validated
    // safe cell yet. The host resolves immediately after generation; a joiner
    // resolves once it has connected and received the host's world.
    let mut spawn_resolved = false;
    if net.is_host {
        world.generate_initial_structures(player.id);

        if let Some(save) = loaded_save.take() {
            // ADR-032: hydrate persisted state over the freshly-generated deterministic world and
            // KEEP the persisted player position (skip the resolve_safe_spawn override below).
            hydrate_from_save(&mut world, &mut player, &mut net, save);
            spawn_resolved = true;
            pending_restore_snap = true;
            world.update_ownership(player.position, player.id);
        } else {
            world.update_ownership(player.position, player.id);
            let res = resolve_safe_spawn(&mut world, preferred_spawn());
            player.position = res.position;
            spawn_resolved = true;
            // Reload ownership around the validated spawn so the streamed radius is
            // centred on where the player actually stands.
            world.update_ownership(player.position, player.id);
        }

        // ADR-016 (debug-gated): inject one phantom near the host spawn so it appears as a
        // player (host + joiners, via the ADR-015 relay). It walks (slice 2, collision via
        // ADR-017), fakes pickups (slice 4) and impersonates a victim's NAME (identity phase).
        // The victim is a real connected peer (none at startup → host-name fallback, upgraded by
        // rebind_unbound_victims once a joiner connects). Spawns only when DEBUG_SPAWN_PHANTOM is set.
        if debug_spawn_phantom {
            let phantom_pos = [
                player.position.x + 3.0,
                player.position.y,
                player.position.z,
            ];
            let (victim_name, victim_bound) = choose_victim_name_for(&net, 0);
            let phantom_id = net.spawn_phantom(&victim_name, phantom_pos);
            phantom_driver.add(
                phantom_id,
                PHANTOM_INITIAL_HEADING,
                Vec3::from_array(phantom_pos),
                victim_bound,
            );
        }
    }

    info!(
        "Game loop started at {TICK_HZ}hz (tick = {TICK_DURATION:?}), peer_id={}, host={}",
        net.local_id, net.is_host
    );
    info!(
        "MPTRACE step=V26 event=level0_runtime_patch_active version=phase_2_6 build_marker=spawn_safety_layout_polish"
    );
    if dev_freeze_survival {
        info!(
            "DEV_FREEZE_SURVIVAL active: hunger/thirst/sanity decay and player damage are disabled"
        );
    }
    if dev_god_traversal {
        info!(
            "DEV_GOD_TRAVERSAL active: player collision resolution is bypassed (death/respawn now gated separately — see DEV_INVINCIBLE)"
        );
    }
    if dev_invincible {
        info!("DEV_INVINCIBLE active: survival death/respawn is disabled");
    }
    // TEMP god-traversal audit trace: always log exe path + raw env value +
    // effective bypass state so "which backend / which env" is provable.
    info!(
        "MPTRACE step=GODT event=god_traversal_audit exe={} env_value={:?} collision_bypass_enabled={}",
        std::env::current_exe()
            .map(|p| p.display().to_string())
            .unwrap_or_else(|_| "<unknown>".into()),
        std::env::var("DEV_GOD_TRAVERSAL").ok(),
        dev_god_traversal
    );

    loop {
        ticker.tick().await;

        // ─── PHASE 1: RECEIVE (IPC + Network) ───
        while let Ok(msg) = from_clients.try_recv() {
            match msg {
                ClientMessage::Input(input) => {
                    received_input = input;
                    has_received_input = true;
                }
                ClientMessage::Action(action) => {
                    // ADR-032: graceful save-on-quit. Unity's teardown (OnApplicationQuit →
                    // KillBackend) sends this RIGHT BEFORE it force-kills this process, then waits
                    // briefly for us to exit. Persist synchronously NOW (host-only) — don't wait for
                    // the 3-min autosave timer — then exit so Unity's WaitForExit sees a clean exit
                    // and skips the Kill fallback. Idempotent with the timer autosave (atomic write).
                    if action.action_type == "save_and_shutdown" {
                        if net.is_host {
                            match crate::persistence::save::save_world(
                                &save_path,
                                &session_name,
                                &world,
                                &player,
                                &net.stp_items,
                                &net.stp_buildings,
                                &net.stp_carryables,
                                &net.stp_harvestables,
                            ) {
                                Ok(()) => info!(
                                    "ADR-032: save-on-shutdown written to {}",
                                    save_path.display()
                                ),
                                Err(e) => warn!("ADR-032: save-on-shutdown failed: {e}"),
                            }
                        } else {
                            info!("ADR-032: save-on-shutdown on non-host — nothing to persist, exiting");
                        }
                        std::process::exit(0);
                    }
                    info!(
                        "MPTRACE step=PVP event=backend_action_received backend action_received action={}",
                        action.action_type
                    );
                    debug!("action received: {}", action.action_type);
                    handle_action(
                        &action,
                        &mut player,
                        &mut world,
                        &mut net,
                        &to_clients,
                        &mut processed_interactions,
                        tick,
                    )
                    .await;
                }
                ClientMessage::UiEvent(ev) => {
                    debug!("ui event: {}", ev.event_type);
                }
                ClientMessage::RequestChunk { cx, cz, layer } => {
                    // Fase 4.1: grid_gen is the world source of truth. Generate the
                    // requested chunk (with seam stitching) and reply with the 5 m
                    // tile-wall bitmask. net.world_seed is the shared canonical seed
                    // (env WORLD_SEED, propagated via handshake) — identical on every
                    // peer, so the derived chunk is byte-identical across the session.
                    // ADR-033: el perfil de densidad sale del `zone_kind` del chunk
                    // (resolver puro por seed), no de `LAYER_PROFILES[layer]` plano.
                    // El robapieles inyecta ESTE MISMO resolutor en su caché de
                    // colisión, así que render y colisión del fantasma no divergen.
                    // `zone_kind` NO viaja por IPC: se deriva de net.world_seed +
                    // (cx,cz,layer) que el propio mensaje ya trae — sin cambio de
                    // protocolo, como fija el ADR.
                    let rules =
                        crate::world::zone_density::rules_for(net.world_seed, cx, cz, layer);
                    // ADR-034: la variante `_and_rooms` devuelve ADEMÁS los rects
                    // de Fase 4 con su RoomType. Misma generación, un solo pase —
                    // el bitmask sale idéntico al de `chunk_tile_walls`.
                    let (walls, room_zones) = crate::world::grid_gen::chunk_tile_walls_and_rooms(
                        &rules,
                        net.world_seed,
                        cx,
                        cz,
                        layer,
                    );
                    // Broadcast: in this P2P model each player runs its own backend with a
                    // single Unity client, so the only subscriber IS the requester.
                    let _ = to_clients.send(ServerMessage::ChunkData(GridChunkData {
                        cx,
                        cz,
                        layer,
                        walls,
                        room_zones,
                    }));
                }
            }
        }

        // ADR-032 (snap de sesión restaurada): first PlayerInput ⇒ Unity is connected and
        // subscribed — emit the deferred position-arming event exactly once. Carries the
        // CURRENT authoritative position (hydrated; the XZ speed cap has held it against the
        // client's scene-spawn claims). AuthoritativePoseApplier snaps the LOCAL player to it;
        // RespawnRequester ignores this type (no stats/invuln/native-respawn side effects).
        if pending_restore_snap && has_received_input {
            pending_restore_snap = false;
            info!(
                "ADR-032: emitting session_restored snap pos=({:.2},{:.2},{:.2})",
                player.position.x, player.position.y, player.position.z
            );
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "session_restored".into(),
                data: serde_json::json!({ "position": player.position.to_array() }),
            }));
            // TEMP DIAG (TP attribution audit; REMOVE after diagnosis): see
            // Player::last_reposition_tick doc comment.
            player.last_reposition_tick = Some(tick);
            // ADR-032 amendment: restore the real STP inventory in the same deferred window,
            // AFTER the snap event (independent consumers — applier vs InventoryRestorer — so
            // order is cosmetic, but keep it deterministic). Skipped when empty: an empty
            // snapshot is indistinguishable from a pre-amendment save, and clearing the
            // client's fresh-session containers over that ambiguity would destroy STP starter
            // items for no gain (accepted degradation: a genuinely-naked persisted state falls
            // back to whatever STP grants a fresh session).
            if !player.stp_inventory.is_empty() {
                info!(
                    "ADR-032: emitting inventory_restored ({} stacks)",
                    player.stp_inventory.len()
                );
                let items: Vec<serde_json::Value> = player
                    .stp_inventory
                    .iter()
                    .map(|s| serde_json::json!({ "item_id": s.item_id, "quantity": s.quantity }))
                    .collect();
                let _ = to_clients.send(ServerMessage::Event(GameEvent {
                    event_type: "inventory_restored".into(),
                    data: serde_json::json!({ "items": items }),
                }));
            }
        }

        // Process incoming network packets.
        let net_events = net.process_incoming().await;
        for event in net_events {
            handle_network_event(
                event,
                &mut player,
                &mut world,
                &mut net,
                &to_clients,
                &mut processed_interactions,
                tick,
            )
            .await;
        }

        // ADR-016 (identity phase): once a real peer is connected, an unbound phantom adopts
        // that peer's NAME (cloning the victim; keeps its own unique id). Host-only; a cheap
        // no-op once every phantom is bound (or when there is no phantom).
        if net.is_host {
            phantom_driver.rebind_unbound_victims(&mut net);
        }

        // A joiner places its local player only once it has connected and the
        // host's world has arrived — never on the empty/pre-sync world, and
        // never at the unsafe chunk-corner origin. ADR-016: real_peer_count so an
        // injected phantom never satisfies this gate (no-op on a joiner, where the
        // phantom is unmarked, but correct on any backend that injects one).
        if !spawn_resolved && net.real_peer_count() > 0 && !world.chunks.is_empty() {
            let res = resolve_safe_spawn(&mut world, preferred_spawn());
            player.position = res.position;
            spawn_resolved = true;
        }

        // ADR-014: drain reserved pickups whose juicy-frame delay elapsed → remove the item now
        // (despawns for all via the stp_items diff at 10Hz). Tolerant: if the item already left
        // stp_items by another path, just drop the (now-orphan) reservation. Host-only in practice
        // (joiners never reserve), so the empty-map check makes it a no-op there.
        if !net.pending_pickups.is_empty() {
            let now = std::time::Instant::now();
            let due: Vec<u32> = net
                .pending_pickups
                .iter()
                .filter(|(_, (_, remove_at))| *remove_at <= now)
                .map(|(item_id, _)| *item_id)
                .collect();
            for item_id in due {
                net.pending_pickups.remove(&item_id);
                if let Some(pos) = net.stp_items.iter().position(|it| it.id == item_id) {
                    net.stp_items.remove(pos);
                }
            }
        }

        // ─── PHASE 2: SIMULATE ───
        // Only apply once a real input has arrived — the default PlayerInput has
        // position [0,0,0], which would otherwise drag the player to the origin
        // before the client's first packet. Track the accepted seq for the ack.
        if has_received_input {
            // ADR-020: record the client-reported crouch (cosmetic; relayed to peers, not validated).
            player.crouch = received_input.crouch;
            // ADR-021: record the client-reported camera pitch, quantized to 1° (cosmetic;
            // relayed to peers, not validated). yaw is consumed as `look[1]` in apply_movement.
            player.pitch = quantize_pitch(received_input.look[0]);
            // ADR-022: record the client-reported worn clothing IDs (cosmetic; relayed to peers,
            // not validated). Read by the client from its inventory equipment slots.
            player.equipment = received_input.equipment;
            // ADR-023: record the client-reported held item ID (cosmetic; relayed to peers,
            // not validated). Read by the client from its wieldable holster slot.
            player.held_item = received_input.held_item;
            // ADR-024: record the client-reported hit-reaction counter (cosmetic; relayed to
            // peers, not validated). Incremented client-side on each local DamageReceived.
            player.hit_seq = received_input.hit_seq;
            // ADR-042: record the client-reported held-light flag and shot counter (cosmetic;
            // relayed to peers, not validated). The light is "any enabled Light under the active
            // wieldable"; the counter is bumped client-side on each native IFirearmTrigger.Shoot.
            player.light_on = received_input.light_on;
            player.fire_seq = received_input.fire_seq;
            // ADR-025 respawn-on-demand: while DEAD the server FREEZES the authoritative pose —
            // client-reported movement is ignored (same gating family as DEV_FREEZE_SURVIVAL /
            // take_damage). Any local client drift while dead is corrected by the applier's snap
            // on player_respawned. The ack does not advance (nothing was accepted).
            if !player.stats.is_dead() {
                let seq = apply_movement(
                    &mut player,
                    &received_input,
                    dt,
                    &world,
                    tick,
                    dev_god_traversal,
                );
                last_accepted_input_seq = seq;
                authoritative_velocity = Vec3::from_array(received_input.velocity);
            } else {
                authoritative_velocity = Vec3::ZERO;
            }
        }
        // Only refresh chunk ownership when the player crosses a chunk boundary.
        // Calling update_ownership every tick caused ~2 FPS churn from constant
        // chunk rebuild. Startup fires because last_ownership_chunk starts as None.
        {
            let current_chunk = world_to_chunk(player.position);
            if last_ownership_chunk != Some(current_chunk) {
                let reason = if last_ownership_chunk.is_none() {
                    "startup"
                } else {
                    "chunk_changed"
                };
                world.update_ownership(player.position, player.id);
                let loaded_chunks = world.chunks.len();
                info!(
                    "MPTRACE step=STREAM event=ownership_refresh player_chunk=({},{}) loaded_chunks={} reason={}",
                    current_chunk.0, current_chunk.1, loaded_chunks, reason
                );
                last_ownership_chunk = Some(current_chunk);
            }
        }
        if tick.is_multiple_of(60) {
            info!(
                "MPTRACE step=Q event=local_transform_from_ipc self_id={} pos=({:.2},{:.2},{:.2}) rot={:.2}",
                net.local_id,
                player.position.x,
                player.position.y,
                player.position.z,
                player.rotation
            );
        }

        // Entity AI at 10hz.
        if tick.is_multiple_of(ENTITY_TICK_EVERY) {
            let (damage, events) = world.tick_entities(entity_dt, player.position, player.id);
            if ENTITY_DAMAGE_ENABLED && !dev_freeze_survival && damage > 0.0 {
                player.stats.take_damage(damage);
            }
            for ev in events {
                if dev_freeze_survival && ev.event_type == "damage_taken" {
                    continue;
                }
                let _ = to_clients.send(ServerMessage::Event(ev));
            }
            world.tick_respawns(entity_dt);

            // ADR-016 slice 2: advance phantom peers (host-only). Each phantom walks and its
            // move is resolved via ADR-017 sim-only collision, so it respects walls/floor even
            // far from the host (where world.chunks is empty). Same 10 Hz as the pose relay.
            if net.is_host {
                // ADR-043: reconcile which of the world's robapieles are simulated BEFORE stepping
                // them, so one that just woke up gets a full tick instead of standing still for
                // 100 ms at the edge of view — the frame a player is most likely to be looking.
                phantom_driver.sync_population(&mut net, player.position, entity_dt);
                let attacks = phantom_driver.step(
                    &mut net,
                    entity_dt,
                    player.position,
                    player.rotation,
                    player.crouch,
                );
                // ADR-016 slice 1 — apply the phantom attacks to the HOST player. DEUDA: host
                // only; a joiner's health lives in its own backend (Fase 7). The damage path is
                // SEPARATE from the pickup theater (ADR-016 invariant intact).
                //
                // ADR-043: several attackers per tick now. Once the player is down, the remaining
                // attacks of the SAME tick are dropped — a corpse taking two more hits would emit
                // duplicate `phantom_kill`/`phantom_hit` events for a player who is already dead,
                // and the client's death chain is not idempotent.
                for attack in attacks {
                    if player.stats.is_dead() && !dev_invincible {
                        break;
                    }
                    match *attack {
                        PhantomAttack::Kill => {
                            let death_pos = player.position.to_array();
                            if !dev_invincible {
                                player.stats.take_damage(100.0); // → is_dead → death/respawn
                            }
                            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                                event_type: "phantom_kill".into(),
                                data: serde_json::json!({ "pos": death_pos }),
                            }));
                        }
                        PhantomAttack::Hit(dmg) => {
                            if !dev_invincible {
                                player.stats.take_damage(dmg);
                            }
                            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                                event_type: "phantom_hit".into(),
                                data: serde_json::json!({ "damage": dmg }),
                            }));
                        }
                        PhantomAttack::Knockback(dx, dz) => {
                            // Client-only: it applies the impulse (SetVelocity). Mutating
                            // player.position here would be overwritten by the next
                            // client-authoritative input (ADR-009), so the backend only signals.
                            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                                event_type: "phantom_knockback".into(),
                                data: serde_json::json!({ "dx": dx, "dz": dz }),
                            }));
                        }
                    }
                }
            }
        }

        // Ownership is now handled per-chunk-boundary above; only teleportation
        // and other slow-tick work runs here.
        if tick.is_multiple_of(SLOW_TICK_EVERY) && (net.is_host || net.peer_count() == 0) {
            let events = world.tick_teleportation(tick);
            for ev in &events {
                let _ = to_clients.send(ServerMessage::Event(ev.clone()));
            }
            // Broadcast teleport events to peers.
            for ev in &events {
                if let Some(data) = ev.data.as_object() {
                    if let (Some(pos), Some(offset)) =
                        (data.get("chunk_pos"), data.get("new_offset"))
                    {
                        let old_pos = [
                            pos.as_array().and_then(|a| a[0].as_i64()).unwrap_or(0) as i32,
                            pos.as_array().and_then(|a| a[1].as_i64()).unwrap_or(0) as i32,
                        ];
                        let new_pos = [
                            old_pos[0]
                                + offset.as_array().and_then(|a| a[0].as_i64()).unwrap_or(0) as i32,
                            old_pos[1]
                                + offset.as_array().and_then(|a| a[1].as_i64()).unwrap_or(0) as i32,
                        ];
                        sync::broadcast_chunk_teleport(&net, old_pos, new_pos, 0).await;
                    }
                }
            }
        }

        // Stats with real context from the world. ADR-016: real_peer_count so a phantom
        // doesn't inflate the host's sanity context (it still renders in the roster).
        let ctx = world.stat_context_for(player.position, net.real_peer_count() as u32);
        if dev_freeze_survival {
            player.stats.speed_modifier = 1.0;
            player.stats.accuracy_modifier = 1.0;
            player.stats.hallucination_intensity = 0.0;
        } else {
            player.stats.update(dt, &ctx);
        }

        // Death → respawn on a validated safe cell (never the unsafe origin).
        // DEV_INVINCIBLE (not god-traversal anymore): survival death and the resulting respawn
        // are skipped (debug only). This is the path a phantom Kill triggers via take_damage(100).
        // ADR-025 respawn-on-demand (decided 2026-07-02): the server NO LONGER auto-respawns.
        // The player stays dead (health 0, pose frozen — see the is_dead gate in the input
        // block) with the native DeathUI visible, until the client sends "respawn_request"
        // (handled in handle_action, which runs resolve_safe_spawn + emits player_respawned).
        // player_died fires exactly once per death (edge flag), with the REAL death position
        // (previously mislabeled: it carried the post-respawn spawn position).
        if !dev_invincible && player.stats.is_dead() {
            if !death_announced {
                death_announced = true;
                // TEMP DIAG (death-cause dump; REMOVE after diagnosis): stats + candidate cause at
                // the death edge. starving/dehydrated=true → survival drain; both false → a hit
                // (phantom if phantom_active, else entity `damage_taken` / reported local damage).
                info!(
                    "MPTRACE step=DEATH_DIAG event=player_died_cause health={:.2} hunger={:.2} thirst={:.2} sanity={:.2} phantom_active={} starving={} dehydrated={} death_pos=({:.1},{:.1},{:.1})",
                    player.stats.health,
                    player.stats.hunger,
                    player.stats.thirst,
                    player.stats.sanity,
                    debug_spawn_phantom,
                    player.stats.hunger <= 0.0,
                    player.stats.thirst <= 0.0,
                    player.position.x,
                    player.position.y,
                    player.position.z
                );
                info!("Player died — awaiting respawn_request (respawn-on-demand)");
                let _ = to_clients.send(ServerMessage::Event(GameEvent {
                    event_type: "player_died".into(),
                    data: serde_json::json!({ "death_pos": player.position.to_array() }),
                }));
                // TEMP DIAG (TP attribution audit; REMOVE after diagnosis): see
                // Player::last_reposition_tick doc comment.
                player.last_reposition_tick = Some(tick);
            }
        } else {
            death_announced = false; // revived (respawn_request honored) → re-arm the edge
        }

        // Stat warnings at 1hz.
        if tick.is_multiple_of(SLOW_TICK_EVERY) {
            emit_stat_warnings(&player, &to_clients);
        }

        // ─── PHASE 3: NETWORK SEND ───

        // Broadcast player position to peers at 10hz.
        if tick.is_multiple_of(NET_BROADCAST_EVERY) {
            sync::broadcast_player_update(&net, &player).await;
            // Host-as-server relay: the host re-advertises the FULL peer roster (ids +
            // current positions) so every joiner learns about ALL other peers, not just
            // the host. Without this a joiner — which only connects to the host — never
            // sees the other joiners. (A joiner's own roster is just {self, host}, so
            // only the host has anything to relay.)
            if net.is_host {
                sync::broadcast_peer_roster(&net, &player).await;
                // ADR-015: relay each peer's full pose (rotation+animation, not just the
                // position the roster carries) so joiners see other joiners gesture/face
                // correctly. Host-only; no-op below two peers.
                sync::broadcast_peer_poses(&net).await;
                sync::broadcast_stp_items(&net).await;
                sync::broadcast_stp_buildings(&net).await;
                sync::broadcast_stp_carryables(&net).await;
                sync::broadcast_stp_harvestables(&net).await;
                // ADR-028 Fase E: full corpse roster (host-authoritative, self-healing).
                sync::broadcast_corpses(&net, &world).await;
            }
        }

        // Broadcast chunk states at 5hz.
        if tick.is_multiple_of(CHUNK_BROADCAST_EVERY) {
            sync::broadcast_chunk_states(&net, &world, player.position).await;
        }

        // Heartbeat every 1s.
        if tick.is_multiple_of(HEARTBEAT_EVERY) {
            net.retry_pending_connection().await;
            net.send_heartbeats().await;

            // ADR-016: keep injected phantoms alive — refresh their heartbeat before the
            // timeout scan so check_timeouts never reaps them (they get no real packets).
            // Runs at 1s, well under the 5s HEARTBEAT_TIMEOUT. No-op without phantoms.
            net.refresh_phantom_heartbeats();

            // Check timeouts.
            let timeout_events = net.check_timeouts();
            for event in timeout_events {
                handle_network_event(
                    event,
                    &mut player,
                    &mut world,
                    &mut net,
                    &to_clients,
                    &mut processed_interactions,
                    tick,
                )
                .await;
            }
        }

        // Process reliable retransmits.
        if tick.is_multiple_of(ENTITY_TICK_EVERY) {
            net.process_retransmits().await;
        }

        // ─── PHASE 4: SEND ───

        // ADR-009 §2: authoritative movement delta at 20hz for the client
        // reconciler — pose + accepted-input ack, decoupled from the full snapshot.
        if tick.is_multiple_of(MOVEMENT_DELTA_EVERY) {
            let _ = to_clients.send(ServerMessage::DeltaUpdate(MovementDelta {
                tick,
                ack_input_seq: last_accepted_input_seq,
                position: player.position.to_array(),
                velocity: authoritative_velocity.to_array(),
            }));
        }

        // Full WorldState (stats/chunks/entities) to Unity at 10hz.
        if tick.is_multiple_of(WORLD_STATE_EVERY) {
            let snapshot =
                build_world_state(tick, &player, &mut world, &net, last_accepted_input_seq);
            let _ = to_clients.send(ServerMessage::WorldState(snapshot));
        }

        // ADR-032: host-only autosave (~3 min). The single-threaded loop makes tick-boundary
        // serialization an inherently consistent snapshot — no pause/lock needed. Skips tick 0.
        if net.is_host && tick > 0 && tick.is_multiple_of(AUTOSAVE_EVERY) {
            match crate::persistence::save::save_world(
                &save_path,
                &session_name,
                &world,
                &player,
                &net.stp_items,
                &net.stp_buildings,
                &net.stp_carryables,
                &net.stp_harvestables,
            ) {
                Ok(()) => info!("ADR-032: autosave written to {}", save_path.display()),
                Err(e) => warn!("ADR-032: autosave failed: {e}"),
            }
        }

        tick = tick.wrapping_add(1);
    }
}

fn env_flag_enabled(name: &str) -> bool {
    std::env::var(name)
        .map(|value| {
            let value = value.trim();
            value == "1"
                || value.eq_ignore_ascii_case("true")
                || value.eq_ignore_ascii_case("yes")
                || value.eq_ignore_ascii_case("on")
        })
        .unwrap_or(false)
}

/// ADR-043 — a tuning knob read from the env, falling back to `default` when unset OR unparseable.
///
/// Fails SOFT and loud, unlike the fail-loud config elsewhere: this is a load-test lever, and a
/// typo in a shell variable must not stop the game from starting. The warning is what makes the
/// difference between a lever that silently did nothing and one you can see did nothing.
fn env_tuning<T: std::str::FromStr + std::fmt::Display>(name: &str, default: T) -> T {
    match std::env::var(name) {
        Err(_) => default,
        Ok(raw) => match raw.trim().parse::<T>() {
            Ok(v) => {
                info!("MPTRACE step=CFG event=tuning_override name={name} value={v}");
                v
            }
            Err(_) => {
                warn!(
                    "MPTRACE step=CFG event=tuning_unparseable name={name} raw={raw} using_default={default}"
                );
                default
            }
        },
    }
}

async fn handle_network_event(
    event: NetworkEvent,
    player: &mut Player,
    world: &mut World,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
    processed_interactions: &mut HashSet<(u16, u64)>,
    tick: u64,
) {
    match event {
        NetworkEvent::PeerConnected { id, name } => {
            info!("Peer connected id={} name={}", id, name);
            info!(
                "MPTRACE step=G event=peer_registry_after_connected self_id={} sender_id=<event> assigned_id=<event> peer_id={} endpoint=<registered> peer_count={} remote_players_count=<n/a> remote_players_ids={:?}",
                net.local_id,
                id,
                net.peer_count(),
                net.peer_ids()
            );
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "player_joined".into(),
                data: serde_json::json!({ "player_id": id, "name": name }),
            }));

            // If we're the host, send world sync to the new peer.
            if net.is_host {
                sync::send_world_sync(net, id, world, player).await;
            } else if id == 1 {
                world.reset_for_remote_world(net.world_seed, 1);
                info!(
                    "MPTRACE step=Z event=apply_world_snapshot self_id={} revision={} chunks=0 entities=0 items=0 reason=joiner_reset_for_host_seed seed={}",
                    net.local_id,
                    world.revision,
                    world.seed
                );
            }

            // Keep the local player identity aligned with the peer id assigned by the host.
            if !net.is_host && player.id != net.local_id {
                player.id = net.local_id;
            }
        }

        NetworkEvent::PeerDisconnected { id, reason } => {
            info!("Peer disconnected: id={}, reason={}", id, reason);
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "player_left".into(),
                data: serde_json::json!({ "player_id": id, "name": "", "reason": reason }),
            }));
        }

        NetworkEvent::RemotePlayerUpdate {
            id,
            position,
            rotation,
            animation,
            crouch,
            pitch,
            equipment,
            held_item,
            hit_seq,
            dead,
            revealed,
            light_on,
            fire_seq,
        } => {
            debug!(
                "Remote player received: id={}, pos=({:.2}, {:.2}, {:.2}), rot={:.1}, anim={}, crouch={}, pitch={}, equipment={:?}, held_item={}, hit_seq={}, dead={}, revealed={}, light_on={}, fire_seq={}",
                id, position[0], position[1], position[2], rotation, animation, crouch, pitch, equipment, held_item, hit_seq, dead, revealed, light_on, fire_seq
            );
            // Player state is tracked in PeerConnection; WorldState builder reads it.
        }

        NetworkEvent::WorldSyncReceived {
            world_seed,
            world_revision,
            chunks,
        } => {
            info!(
                "Received world sync with seed={}, revision={}, {} chunks",
                world_seed,
                world_revision,
                chunks.len()
            );
            world.apply_world_sync(world_seed, world_revision, &chunks, net.local_id);
        }

        NetworkEvent::StpPickupRequest {
            item_id,
            requester_id,
        } => {
            if net.is_host {
                process_stp_pickup(item_id, requester_id, net, to_clients).await;
            }
        }

        NetworkEvent::StpPickupGranted {
            item_id,
            def_id,
            count,
        } => {
            // ADR-011 follow-up: dedup retransmitted reliable grants. StpPickupGranted is reliable
            // and may arrive multiple times; without this each copy re-stamps last_pickup_at →
            // a duplicated "pickup" window on observers. Same form as processed_stp_drops/places/etc.
            if item_id != 0 && !net.processed_stp_pickup_grants.insert(item_id) {
                info!(
                    "MPTRACE step=SP event=stp_pickup_grant_duplicate item_id={} ignored=true",
                    item_id
                );
                return;
            }
            // ADR-011: our own local player picked up (joiner path: host granted) → stamp window.
            net.last_pickup_at = Some(std::time::Instant::now());
            // We are the recoger: surface the grant to our Unity, which credits the
            // local STP inventory (StpPickupController). The item already vanished via
            // the host's stp_items removal.
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "stp_pickup_granted".into(),
                data: serde_json::json!({
                    "item_id": item_id,
                    "def_id": def_id,
                    "count": count,
                }),
            }));
        }

        NetworkEvent::StpDropRequest {
            drop_id,
            def_id,
            count,
            position,
            rotation,
        } => {
            if net.is_host {
                process_stp_drop(drop_id, def_id, count, position, rotation, net);
            }
        }

        NetworkEvent::StpPlaceRequest {
            place_id,
            def_id,
            position,
            rotation,
            group_id,
            is_group,
        } => {
            if net.is_host {
                process_stp_place(
                    place_id, def_id, position, rotation, group_id, is_group, net,
                );
                // Phase B3: relay immediately so the placer's replicated copy (with its group)
                // arrives within ~RTT, closing the round-trip gap when chaining pieces.
                sync::broadcast_stp_buildings(net).await;
            }
        }

        NetworkEvent::StpBuildAddRequest {
            add_id,
            building_id,
            material_id,
        } => {
            if net.is_host {
                process_stp_build_add(add_id, building_id, material_id, net);
            }
        }

        // ADR-037: a joiner cancelled an unbuilt piece. Relay immediately after retiring it —
        // the player who pressed the key is staring at the spot, so the round-trip gap matters
        // more here than anywhere else (same reasoning as StpPlaceRequest above).
        NetworkEvent::StpDemolishRequest {
            demolish_id,
            building_id,
        } => {
            if net.is_host {
                process_stp_demolish(demolish_id, building_id, net);
                sync::broadcast_stp_buildings(net).await;
            }
        }

        NetworkEvent::StpCarryablePickupRequest {
            carryable_id,
            requester_id,
        } => {
            if net.is_host {
                process_stp_carryable_pickup(carryable_id, requester_id, net, to_clients).await;
            }
        }

        NetworkEvent::StpCarryablePickupGranted {
            carryable_id,
            def_id,
        } => {
            // We are the recoger: surface the grant to our Unity, which carries the
            // carryable in hand (StpCarryablePickupController). It already vanished for
            // everyone via the host's stp_carryables removal.
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "stp_carryable_pickup_granted".into(),
                data: serde_json::json!({
                    "carryable_id": carryable_id,
                    "def_id": def_id,
                }),
            }));
        }

        NetworkEvent::StpCarryableDropRequest {
            drop_id,
            def_id,
            position,
            rotation,
        } => {
            if net.is_host {
                process_stp_carryable_drop(drop_id, def_id, position, rotation, net);
            }
        }

        NetworkEvent::StpHarvestHitRequest {
            hit_id,
            harvestable_id,
            amount,
        } => {
            if net.is_host {
                process_stp_harvest_hit(hit_id, harvestable_id, amount, net);
            }
        }

        NetworkEvent::WorldInteractRequest {
            requester_id,
            request_id,
            target_id,
            target_kind,
            interaction_type,
            player_position,
        } => {
            if !net.is_host {
                return;
            }

            process_authoritative_interaction(
                requester_id,
                request_id,
                target_id,
                &target_kind,
                &interaction_type,
                Vec3::from_array(player_position),
                world,
                net,
                player,
                processed_interactions,
            )
            .await;
        }

        NetworkEvent::ChunkTransferReceived { from, data } => {
            info!(
                "Received chunk transfer [{}, {}] from peer {}",
                data.pos[0], data.pos[1], from
            );
            world.apply_chunk_transfer(&data, net.local_id);

            // ACK the transfer.
            let ack = crate::network::protocol::PacketPayload::ChunkTransferAck { pos: data.pos };
            net.send_reliable(from, &ack).await;
        }

        NetworkEvent::ChunkTransferAckReceived { from, pos } => {
            debug!(
                "Chunk transfer ACK from {} for [{}, {}]",
                from, pos[0], pos[1]
            );
        }

        NetworkEvent::ChunkTeleportReceived {
            old_pos,
            new_pos: _,
            new_seed,
        } => {
            world.apply_remote_teleport(old_pos, new_seed);
        }

        NetworkEvent::AnchorBroadcastReceived {
            chunk_pos,
            durability: _,
            installed_by: _,
        } => {
            world.set_chunk_anchored(chunk_pos);
        }

        NetworkEvent::StabilizerBroadcastReceived {
            chunk_pos,
            tier: _,
            remaining_hours: _,
        } => {
            world.set_chunk_stabilized(chunk_pos);
        }

        NetworkEvent::HandshakeReceived { .. } => {
            // Handled internally by NetworkManager.
        }

        // ─── ADR-028 Fase E: corpse relay (host-authoritative) ───
        NetworkEvent::CorpseSpawnRequest {
            request_id,
            requester_id,
            owner_name,
            position,
            equipment,
            held_item,
            items,
        } => {
            if !net.is_host {
                return; // only the host owns corpse authority
            }
            // ADR-028 post-E3: defense against old/malicious clients — a v9 joiner (or a forged
            // request) could still ship an empty snapshot; the immortal-empty-corpse rule is
            // backend-authoritative, so enforce it here too, not just at the joiner's forward.
            if crate::world::corpse::corpse_loot_is_empty(&items) {
                info!(
                    "MPTRACE step=CORPSE event=corpse_spawn_skipped reason=empty_loot requester_id={} request_id={}",
                    requester_id, request_id
                );
                return;
            }
            match apply_corpse_spawn_request(
                world,
                &mut net.processed_corpse_requests,
                requester_id,
                request_id,
                CorpseSpawnData {
                    owner_name,
                    position,
                    equipment,
                    held_item,
                    items,
                },
            ) {
                Some(corpse_id) => info!(
                    "MPTRACE step=CORPSE event=corpse_spawned corpse_id={} owner_id={} pos=({:.2},{:.2},{:.2}) relayed=true request_id={}",
                    corpse_id, requester_id, position[0], position[1], position[2], request_id
                ),
                None => info!(
                    "MPTRACE step=CORPSE event=corpse_spawn_duplicate requester_id={} request_id={} ignored=true",
                    requester_id, request_id
                ),
            }
        }

        NetworkEvent::CorpseTakeRequest {
            request_id,
            requester_id,
            corpse_id,
            item_index,
            quantity,
            requester_pos,
        } => {
            if !net.is_host {
                return;
            }
            match apply_corpse_take_request(
                world,
                &mut net.processed_corpse_requests,
                requester_id,
                request_id,
                CorpseTakeData {
                    corpse_id,
                    item_index,
                    quantity,
                    requester_pos,
                },
            ) {
                Some(result) => {
                    if let PacketPayload::CorpseTakeResult {
                        accepted, item_id, quantity, corpse_empty, ref reason, ..
                    } = result
                    {
                        info!(
                            "MPTRACE step=CORPSE event=corpse_take_relayed corpse_id={} item_index={} requester_id={} accepted={} item_id={} quantity={} corpse_empty={} reason={}",
                            corpse_id, item_index, requester_id, accepted, item_id, quantity, corpse_empty,
                            if reason.is_empty() { "-" } else { reason }
                        );
                    }
                    net.send_reliable(requester_id, &result).await;
                }
                None => info!(
                    "MPTRACE step=CORPSE event=corpse_take_duplicate requester_id={} request_id={} ignored=true",
                    requester_id, request_id
                ),
            }
        }

        NetworkEvent::CorpseTakeResult {
            request_id,
            accepted,
            corpse_id,
            item_index,
            item_id,
            quantity,
            corpse_empty,
            reason,
        } => {
            // We are the requester: surface the host's verdict to OUR Unity through the SAME
            // IPC events Fase D already consumes (CorpseLootSync's confirm/rollback works
            // unchanged). Deduped: the verdict is reliable and may retransmit — a duplicated
            // corpse_item_taken would double-shift the client's index mirror.
            if !net.processed_corpse_results.insert(request_id) {
                info!(
                    "MPTRACE step=CORPSE event=corpse_take_result_duplicate request_id={} ignored=true",
                    request_id
                );
                return;
            }
            if accepted {
                let _ = to_clients.send(ServerMessage::Event(GameEvent {
                    event_type: "corpse_item_taken".into(),
                    data: serde_json::json!({
                        "corpse_id": corpse_id,
                        "item_index": item_index,
                        "item_id": item_id,
                        "quantity": quantity,
                        "corpse_empty": corpse_empty,
                    }),
                }));
            } else {
                let _ = to_clients.send(ServerMessage::Event(GameEvent {
                    event_type: "corpse_take_rejected".into(),
                    data: serde_json::json!({
                        "corpse_id": corpse_id,
                        "item_index": item_index,
                        "reason": reason,
                    }),
                }));
            }
        }

        NetworkEvent::CorpseListReceived { corpses } => {
            // Joiner: mirror the host's authoritative roster verbatim (same trust as
            // StpItemList). The host never receives this (it doesn't broadcast to itself),
            // but guard anyway — its own map is the source of truth.
            if !net.is_host {
                world.corpses = corpses.into_iter().map(|c| (c.id, c)).collect();
            }
        }

        // ─── ADR-029 V0: PvP relay (host-authoritative validation, victim-applied damage) ───
        NetworkEvent::PvpHitCandidate {
            request_id,
            attacker_id,
            victim_id,
            weapon_id,
            damage,
            origin: _,
            direction,
            client_tick: _,
            hit_position: _,
        } => {
            if !net.is_host {
                return; // only the host validates PvP candidates
            }
            process_pvp_hit_candidate_host(
                PvpCandidateFields {
                    request_id,
                    attacker_id,
                    victim_id,
                    weapon_id,
                    damage,
                    direction,
                },
                player,
                net,
                to_clients,
                tick,
            )
            .await;
        }

        NetworkEvent::PvpDamageGrant {
            request_id,
            attacker_id,
            victim_id,
            weapon_id,
            damage,
            reason: _,
        } => {
            // We are the victim's own backend (the host addressed this packet to us because
            // OUR local player is the victim) — apply the damage here, never elsewhere.
            if victim_id != net.local_id as u32 {
                info!(
                    "MPTRACE step=PVP event=pvp_damage_grant_victim_mismatch self_id={} expected_victim={} got_victim={} request_id={}",
                    net.local_id, net.local_id, victim_id, request_id
                );
                return;
            }
            match apply_pvp_damage_grant(
                &mut player.stats,
                &mut net.processed_pvp_grants,
                attacker_id,
                request_id,
                damage,
                tick,
            ) {
                Ok(health) => {
                    info!(
                        "MPTRACE step=PVP event=pvp_damage_applied request_id={} attacker_id={} weapon_id={} damage={:.1} health={:.2}",
                        request_id, attacker_id, weapon_id, damage, health
                    );
                    let _ = to_clients.send(ServerMessage::Event(GameEvent {
                        event_type: "pvp_damage_taken".into(),
                        data: serde_json::json!({
                            "attacker_id": attacker_id,
                            "weapon_id": weapon_id,
                            "damage": damage,
                            "health": health,
                        }),
                    }));
                }
                Err(reason) => {
                    info!(
                        "MPTRACE step=PVP event=pvp_damage_grant_blocked reason={} request_id={} attacker_id={}",
                        reason, request_id, attacker_id
                    );
                }
            }
        }

        NetworkEvent::PvpHitRejected {
            request_id,
            attacker_id,
            victim_id: _,
            reason,
        } => {
            // We are the shooter's own backend (the host addressed this packet to us because
            // OUR local player fired the rejected shot) — surface it to our own Unity.
            if attacker_id != net.local_id as u32 {
                info!(
                    "MPTRACE step=PVP event=pvp_hit_rejected_attacker_mismatch self_id={} expected_attacker={} got_attacker={} request_id={}",
                    net.local_id, net.local_id, attacker_id, request_id
                );
                return;
            }
            info!(
                "MPTRACE step=PVP event=pvp_hit_rejected_relayed request_id={} reason={}",
                request_id, reason
            );
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "pvp_hit_rejected".into(),
                data: serde_json::json!({ "request_id": request_id, "reason": reason }),
            }));
        }
    }
}

/// ADR-028 Fase E: the loot-snapshot half of a CorpseSpawnRequest (everything except the
/// dedupe key), grouped so the handler keeps a readable arity.
struct CorpseSpawnData {
    owner_name: String,
    position: [f32; 3],
    equipment: [i32; 4],
    held_item: i32,
    items: Vec<crate::world::corpse::CorpseStack>,
}

/// ADR-028 Fase E: the take half of a CorpseTakeRequest (everything except the dedupe key).
struct CorpseTakeData {
    corpse_id: u32,
    item_index: u32,
    quantity: u16,
    requester_pos: [f32; 3],
}

/// ADR-028 Fase E: host-side handling of a (possibly retransmitted) corpse spawn request.
/// Returns the new corpse id, or `None` when the (requester, request_id) pair was already
/// processed — a reliable retransmit must spawn EXACTLY one corpse (the known open
/// infinite-retransmit bug makes this dedupe load-bearing, not defensive).
fn apply_corpse_spawn_request(
    world: &mut World,
    processed: &mut HashSet<(u16, u64)>,
    requester_id: u16,
    request_id: u64,
    spawn: CorpseSpawnData,
) -> Option<u32> {
    if !processed.insert((requester_id, request_id)) {
        return None;
    }
    Some(world.spawn_corpse(
        requester_id,
        spawn.owner_name,
        Vec3::from_array(spawn.position),
        spawn.equipment,
        spawn.held_item,
        spawn.items,
    ))
}

/// ADR-028 Fase E: host-side handling of a (possibly retransmitted) corpse take request.
/// Returns the ready-to-send verdict payload, or `None` when deduped (retransmit: the
/// original verdict is already in the reliable-send pipeline; sending a second would
/// double-fire the requester's IPC event).
fn apply_corpse_take_request(
    world: &mut World,
    processed: &mut HashSet<(u16, u64)>,
    requester_id: u16,
    request_id: u64,
    take: CorpseTakeData,
) -> Option<PacketPayload> {
    if !processed.insert((requester_id, request_id)) {
        return None;
    }
    match world.take_corpse_item(
        take.corpse_id,
        take.item_index as usize,
        take.quantity,
        Vec3::from_array(take.requester_pos),
        crate::world::corpse::CORPSE_LOOT_MAX_DISTANCE,
    ) {
        Ok(taken) => Some(PacketPayload::CorpseTakeResult {
            request_id,
            accepted: true,
            corpse_id: take.corpse_id,
            item_index: take.item_index,
            item_id: taken.item_id,
            quantity: taken.quantity,
            corpse_empty: !world.corpses.contains_key(&take.corpse_id),
            reason: String::new(),
        }),
        Err(reason) => Some(PacketPayload::CorpseTakeResult {
            request_id,
            accepted: false,
            corpse_id: take.corpse_id,
            item_index: take.item_index,
            item_id: 0,
            quantity: 0,
            corpse_empty: false,
            reason,
        }),
    }
}

fn preferred_spawn() -> Vec3 {
    // Centre of the starter chunk (0,0). The resolver snaps this to the nearest
    // validated safe cell; Y is recomputed from the chunk floor.
    Vec3::new(CHUNK_SIZE * 0.5, 1.8, CHUNK_SIZE * 0.5)
}

/// ADR-031: the STP "Sleeping Bag" BuildingPieceDefinition id. Placing this piece (via the existing
/// `stp_place` action) sets the player's respawn point. TODO(config): hardcoded like the ADR-029 PvP
/// weapon allowlist; move to a config surface when one exists.
const BED_DEF_ID: i32 = -4996552;

/// ADR-031: resolve where a respawn lands. If the player has a bed (`respawn_point`), stream its
/// chunk in (resolve_safe_spawn only reads loaded chunks) and prefer it; else use the fixed starter
/// spawn. If the resolver falls back to the starter cluster despite a bed (its chunk is non-flat /
/// has no safe cell), "trust the bed": spawn at the bed's exact position if a capsule fits there,
/// instead of teleporting to the fixed (0,0) origin. Extracted from the `respawn_request` handler so
/// the selection logic is unit-testable without the async loop.
fn resolve_respawn(
    world: &mut World,
    respawn_point: Option<Vec3>,
    player_id: PeerId,
) -> crate::world::collision::SpawnResolution {
    let preferred = respawn_point.unwrap_or_else(preferred_spawn);
    if respawn_point.is_some() {
        world.update_ownership(preferred, player_id);
    }
    let mut res = resolve_safe_spawn(world, preferred);
    if res.method == crate::world::collision::SpawnMethod::Repaired {
        if let Some(bed) = respawn_point {
            if let Some(bed_res) = crate::world::collision::try_bed_spawn(world, bed) {
                info!(
                    "MPTRACE step=BED event=trust_bed_spawn pos=({:.2},{:.2},{:.2})",
                    bed_res.position.x, bed_res.position.y, bed_res.position.z
                );
                res = bed_res;
            }
        }
    }
    res
}

/// ADR-009 Option B: apply the client's authoritative-pose input and return the
/// accepted `input_seq` (`None` when the speed cap rejected the move). The legacy
/// direction-integration path was removed with the `input_seq` gate — `input_seq`
/// is now 0-based and every input is a prediction packet.
fn apply_movement(
    player: &mut Player,
    input: &PlayerInput,
    dt: f32,
    world: &World,
    tick: u64,
    god_traversal: bool,
) -> u32 {
    player.rotation = input.look[1].rem_euclid(360.0); // yaw is INPUT (ADR-009 §8)
    apply_client_authoritative_move(player, input, dt, world, tick, god_traversal)
}

/// ADR-021: clamp the client-reported camera pitch to [−90, 90]° and quantize to 1°
/// (i8). Cosmetic/host-relay; the 1° step is the wire resolution for the peer broadcast.
fn quantize_pitch(deg: f32) -> i8 {
    deg.clamp(-90.0, 90.0).round() as i8
}

/// ADR-009 Option B authoritative-move validation. The client owns prediction and
/// its position, so the reported pose is ALWAYS applied — it is never discarded.
///   * speed cap  — REMOVED. It used to `return None` when the finite-difference
///     velocity exceeded the sprint cap, holding the player at the last
///     accepted pose; on the remote avatar that surfaced as teleport
///     JUMPS between accepted poses. Velocity now only drives stamina.
///   * collision  — still clamps the claimed position against static level geometry
///     (slides, never freezes).
///
/// No server-side physics integration is performed. Always returns the accepted
/// `input_seq` (`Some`).
fn apply_client_authoritative_move(
    player: &mut Player,
    input: &PlayerInput,
    dt: f32,
    world: &World,
    _tick: u64,
    god_traversal: bool,
) -> u32 {
    // Run-drain stamina from the reported move-state (2 == run).
    if input.move_state == 2 {
        player.stats.use_stamina(RUN_STAMINA_DRAIN * dt);
    }

    let claimed = Vec3::from_array(input.position);
    // Velocity is intentionally NOT used to gate the pose (see doc above): a velocity
    // cap that rejected the whole pose caused visible teleport jumps. Coop trusts the
    // client position; wall collision below is the only spatial constraint.

    if god_traversal {
        player.position = claimed;
        return input.input_seq;
    }

    // Collision: verify the claimed position doesn't intersect static geometry.
    // resolve_move slides/clamps against the level; the resolved point is the
    // authoritative pose echoed back to the client.
    let resolved = Level0Collision::resolve_move(world, player.position, claimed);

    // TEMP DIAG (rubber-banding trigger-rate audit; REMOVE after diagnosis): count resolve_move
    // invocations/sec and how many resolve as Blocked (pressed against a wall), to test whether
    // this path's call rate could plausibly correlate with the ~5s DEATH_DIAG death cadence, or
    // whether it's orders of magnitude more frequent (call-rate is gated by TICK_HZ=60 once
    // has_received_input is true, i.e. every tick — NOT by the 30Hz client send rate) and thus
    // unrelated to the death cause. Fully-qualified CollisionResultKind path (no new `use`) so
    // removing this block leaves no orphaned import.
    RESOLVE_MOVE_CALLS_DIAG.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    if matches!(
        resolved.kind,
        crate::world::collision::CollisionResultKind::Blocked
    ) {
        RESOLVE_MOVE_BLOCKED_DIAG.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    }
    if _tick.is_multiple_of(60) {
        let calls = RESOLVE_MOVE_CALLS_DIAG.swap(0, std::sync::atomic::Ordering::Relaxed);
        let blocked = RESOLVE_MOVE_BLOCKED_DIAG.swap(0, std::sync::atomic::Ordering::Relaxed);
        info!(
            "MPTRACE step=RESOLVE_DIAG event=resolve_move_rate calls_per_sec={} blocked_per_sec={}",
            calls, blocked
        );
    }

    // TEMP DIAG (TP-source audit; REMOVE after diagnosis): the clamp displaced the CLAIMED pose by
    // a large XZ amount → server-side rubber-banding against the (mismatched) backend world. This
    // pose only reaches the client through delta_update inside a death window, so a client-side TP
    // outside a window can NOT come from here — this log proves/disproves correlation. Throttled to
    // 1/s (a sustained wall-press displaces every tick).
    {
        // TEMP DIAG (TP attribution audit; REMOVE after diagnosis): explicit `in_window` marker —
        // previously this had to be inferred indirectly from the comment above. Mirrors the
        // client's AuthoritativePoseApplier.SnapWindow (0.35s) as a tick count off TICK_HZ, using
        // Player::last_reposition_tick (sealed at session_restored/player_died/player_respawned).
        // Approximate by construction: the backend doesn't know the client's actual timer state,
        // only when IT sent the arming event — good enough to cross-reference against reported TPs.
        const TP_WATCH_WINDOW_TICKS: u64 = (0.35 * TICK_HZ as f64) as u64;
        let in_window = player
            .last_reposition_tick
            .is_some_and(|t| _tick.saturating_sub(t) <= TP_WATCH_WINDOW_TICKS);

        let dx = resolved.position.x - claimed.x;
        let dz = resolved.position.z - claimed.z;
        if (dx * dx + dz * dz).sqrt() > 2.0 && _tick.is_multiple_of(60) {
            info!(
                "MPTRACE step=TP_WATCH TP_SOURCE=resolve_move_clamp in_window={} claimed=({:.1},{:.1},{:.1}) resolved=({:.1},{:.1},{:.1}) kind={:?}",
                in_window,
                claimed.x, claimed.y, claimed.z,
                resolved.position.x, resolved.position.y, resolved.position.z,
                resolved.kind
            );
        }
    }

    player.position = resolved.position;
    input.input_seq
}

// TEMP DIAG (rubber-banding trigger-rate audit; REMOVE after diagnosis): see apply_client_authoritative_move.
static RESOLVE_MOVE_CALLS_DIAG: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
static RESOLVE_MOVE_BLOCKED_DIAG: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(0);

async fn handle_action(
    action: &PlayerAction,
    player: &mut Player,
    world: &mut World,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
    processed_interactions: &mut HashSet<(u16, u64)>,
    tick: u64,
) {
    match action.action_type.as_str() {
        // ADR-025 Slice B: the client reports REAL local damage (falls, hazards — its
        // HealthManager.DamageReceived) so the authoritative health tracks it. Death is then
        // owned by the existing is_dead() → respawn → "player_died" path in run(). Gated by
        // DEV_FREEZE_SURVIVAL like every other player-damage source; sanitized (NaN/negative/
        // huge → clamped) so a malformed report can never poison the authoritative health.
        "report_damage" => {
            let raw = action.data.get("amount").and_then(|v| v.as_f64());
            let amount = sanitize_reported_damage(raw);
            let cause = json_str(&action.data, "cause").unwrap_or("unknown");
            if amount > 0.0 && !env_flag_enabled("DEV_FREEZE_SURVIVAL") {
                player.stats.take_damage(amount);
                info!(
                    "MPTRACE step=DMG event=report_damage_applied amount={:.1} cause={} health={:.2}",
                    amount, cause, player.stats.health
                );
            }
        }
        // ADR-032 amendment: the client reports its CURRENT real STP inventory (InventoryReporter,
        // debounced on-change). Trust-the-client — same level as report_death_loot: no
        // authoritative inventory exists to verify against (decided in ADR-030). Hygiene shared
        // with corpse/chest spawns (sanitize_loot_stacks: quantity<=0 dropped, truncated to
        // MAX_CORPSE_STACKS=64 — first 64 kept). Only mirrored into RAM here; persistence picks
        // it up via PlayerSnapshot on the next save.
        // ADR-041: the client reports a NOISE at a position with a loudness in metres (the weapon
        // table lives in the client on purpose — keeping it in Rust would duplicate data that
        // belongs to Unity's weapon definitions and drift the moment a weapon is added). This
        // mutates NOTHING: not health, not inventory, not the world. It is a perception stimulus,
        // so the worst a forged one can do is walk the phantom to a spot — the same trust level
        // ADR-009 Option B already accepts for X/Z.
        "report_noise" => {
            let pos = action.data.get("position").and_then(|v| v.as_array());
            let coords: Option<[f32; 3]> = pos.and_then(|a| {
                if a.len() != 3 {
                    return None;
                }
                let mut out = [0.0f32; 3];
                for (i, v) in a.iter().enumerate() {
                    out[i] = v.as_f64()? as f32;
                }
                out.iter().all(|c| c.is_finite()).then_some(out)
            });
            let loudness = action
                .data
                .get("loudness")
                .and_then(|v| v.as_f64())
                .unwrap_or(0.0) as f32;
            match coords {
                // Clamped, not trusted: a garbage loudness must not turn one shot into a
                // world-wide summons.
                Some(p) if loudness.is_finite() && loudness > 0.0 => {
                    let loudness = loudness.min(PHANTOM_NOISE_MAX_LOUDNESS);
                    info!(
                        "MPTRACE step=PH_NOISE event=noise_reported pos=({:.1},{:.1},{:.1}) loudness={:.0}",
                        p[0], p[1], p[2], loudness
                    );
                    net.pending_noises.push((p, loudness));
                }
                _ => debug!("report_noise ignored: malformed position or loudness"),
            }
        }
        "report_inventory" => {
            let mut items = parse_loot_stacks(&action.data);
            crate::world::corpse::sanitize_loot_stacks(&mut items);
            debug!("report_inventory: {} stacks", items.len());
            player.stp_inventory = items;
        }
        // ADR-030: the client reports eating/drinking an item (STP's own local Hunger/Thirst
        // managers are disabled by StatInterpolator, ADR-009 L2, so without this the survival
        // stats can only ever go DOWN between respawns). Trust-the-client for possession (no
        // authoritative inventory exists to verify against — same level as report_death_loot);
        // the backend is the sole authority on HOW MUCH an item restores (consumable_spec's
        // fixed table), never a client-reported amount. No dedupe: local action, ordered TCP IPC.
        "consume_item" => {
            let item_id = action
                .data
                .get("item_id")
                .and_then(|v| v.as_i64())
                .unwrap_or(0) as i32;
            if player.stats.is_dead() {
                info!(
                    "MPTRACE step=CONSUME event=consume_item_ignored reason=dead item_id={}",
                    item_id
                );
            } else if let Some(spec) = consumable_spec(item_id) {
                player.stats.restore_hunger(spec.hunger_restore);
                player.stats.restore_thirst(spec.thirst_restore);
                player.stats.restore_health(spec.health_restore);
                info!(
                    "MPTRACE step=CONSUME event=consume_item_applied item_id={} hunger={:.2} thirst={:.2} health={:.2}",
                    item_id, player.stats.hunger, player.stats.thirst, player.stats.health
                );
            } else {
                info!(
                    "MPTRACE step=CONSUME event=consume_item_rejected reason=unknown_item item_id={}",
                    item_id
                );
            }
        }
        // ADR-025 respawn-on-demand: the client's native Respawn button asks the server to
        // respawn. Honored ONLY while actually dead (sanitized like report_damage: a spammed or
        // abusive request while alive is a logged no-op). This is the resolve+reposition that the
        // death block used to run automatically; player_respawned carries the resolved position
        // and arms the client's AuthoritativePoseApplier snap.
        "respawn_request" => {
            if player.stats.is_dead() {
                player.stats = crate::player::stats::PlayerStats::on_respawn();
                // ADR-029 V0 invulnerability amendment: a fresh respawn is immune to PvP
                // damage for RESPAWN_INVULN_TICKS (tick-based, no spatial safe zone).
                player.stats.invuln_until_tick = (tick as u32).wrapping_add(RESPAWN_INVULN_TICKS);
                // ADR-028: this death's corpse (if any) is sealed; re-arm the dedupe
                // so the NEXT death can report its own loot snapshot.
                player.death_loot_reported = false;
                // ADR-031: respawn at the player's bed if they placed one, else the fixed starter
                // spawn (extracted to resolve_respawn so the selection logic is unit-testable).
                let res = resolve_respawn(world, player.respawn_point, player.id);
                player.position = res.position;
                info!(
                    "MPTRACE step=RESPAWN event=respawn_request_honored pos=({:.2},{:.2},{:.2})",
                    player.position.x, player.position.y, player.position.z
                );
                let _ = to_clients.send(ServerMessage::Event(GameEvent {
                    event_type: "player_respawned".into(),
                    data: serde_json::json!({ "position": player.position.to_array() }),
                }));
                // TEMP DIAG (TP attribution audit; REMOVE after diagnosis): see
                // Player::last_reposition_tick doc comment.
                player.last_reposition_tick = Some(tick);
                world.update_ownership(player.position, player.id);
            } else {
                info!(
                    "MPTRACE step=RESPAWN event=respawn_request_ignored reason=not_dead health={:.2}",
                    player.stats.health
                );
            }
        }
        // ADR-028 Fase A: the client reports its death-loot snapshot (full STP inventory +
        // equipment + held item, raw STP item ids) at the death edge. Trust-the-client, same
        // level as position/equipment/held_item — the server has no authoritative inventory
        // to verify against (the legacy Rust Inventory is disconnected from the real game).
        // Gated on is_dead (a report while alive is a logged no-op — ordered IPC guarantees
        // the killing report_damage arrives first) and deduped per death (the client's event
        // fast-path + derived-edge fallback may both fire; only the first creates a corpse).
        // The corpse anchors at player.position, frozen by the ADR-025 death gate until
        // respawn_request.
        "report_death_loot" => {
            if !player.stats.is_dead() {
                info!(
                    "MPTRACE step=CORPSE event=death_loot_ignored reason=not_dead health={:.2}",
                    player.stats.health
                );
            } else if player.death_loot_reported {
                info!("MPTRACE step=CORPSE event=death_loot_ignored reason=already_reported");
            } else if net.is_host {
                let (equipment, held_item, items) = parse_death_loot(&action.data);
                // ADR-028 post-E3: a corpse born empty would be immortal (despawn-on-empty
                // only runs after a take) — dying naked leaves no physical trace.
                if crate::world::corpse::corpse_loot_is_empty(&items) {
                    player.death_loot_reported = true;
                    info!("MPTRACE step=CORPSE event=corpse_spawn_skipped reason=empty_loot");
                    return;
                }
                let stack_count = items.len();
                let corpse_id = world.spawn_corpse(
                    player.id,
                    player.name.clone(),
                    player.position,
                    equipment,
                    held_item,
                    items,
                );
                player.death_loot_reported = true;
                info!(
                    "MPTRACE step=CORPSE event=corpse_spawned corpse_id={} owner_id={} pos=({:.2},{:.2},{:.2}) stacks={} held_item={}",
                    corpse_id,
                    player.id,
                    player.position.x,
                    player.position.y,
                    player.position.z,
                    stack_count,
                    held_item
                );
            } else {
                // ADR-028 Fase E: joiners do NOT spawn locally — corpse ids are host-assigned
                // (global uniqueness) and the roster mirror brings the corpse back within one
                // 10 Hz broadcast tick. Reliable + host-side (requester, request_id) dedupe.
                let (equipment, held_item, items) = parse_death_loot(&action.data);
                // ADR-028 post-E3: empty snapshot → no corpse anywhere; skip the hop too.
                if crate::world::corpse::corpse_loot_is_empty(&items) {
                    player.death_loot_reported = true;
                    info!("MPTRACE step=CORPSE event=corpse_spawn_skipped reason=empty_loot");
                    return;
                }
                let stack_count = items.len();
                let request_id = net.next_corpse_request_id;
                net.next_corpse_request_id += 1;
                let payload = PacketPayload::CorpseSpawnRequest {
                    request_id,
                    requester_id: net.local_id,
                    owner_name: player.name.clone(),
                    position: player.position.to_array(),
                    equipment,
                    held_item,
                    items,
                };
                player.death_loot_reported = true;
                info!(
                    "MPTRACE step=CORPSE event=corpse_spawn_forwarded_to_host request_id={} stacks={} held_item={}",
                    request_id, stack_count, held_item
                );
                net.send_reliable(1, &payload).await;
            }
        }
        // ADR-028 amendment (world chests): the HOST's Unity seeds N supply chests at session
        // start — walkable positions raycast against the RENDERED world (the backend can't
        // validate them, two-worlds debt) and loot picked client-side from the richer chest
        // pools (trust-the-client, same level as report_death_loot). A chest is a corpse-entry
        // with is_chest=true: all relay/loot/despawn machinery is reused untouched. Host-only:
        // joiners receive chests through the CorpseList mirror and must never seed. Dedupe by
        // (player, request_id) via the SAME processed_interactions set world_interact uses —
        // guards a client re-send after reconnect. Empty loot → skipped (post-E3 rule: an
        // empty container would be immortal).
        "spawn_world_chest" => {
            let request_id = json_u64(&action.data, "request_id").unwrap_or(0);
            let position = json_vec3(&action.data, "position")
                .map(Vec3::from_array)
                .unwrap_or(player.position);
            let (_, _, items) = parse_death_loot(&action.data);
            match handle_spawn_world_chest(
                world,
                net.is_host,
                player.id,
                request_id,
                position,
                items,
                processed_interactions,
            ) {
                Ok(chest_id) => info!(
                    "MPTRACE step=CHEST event=chest_seeded chest_id={} pos=({:.2},{:.2},{:.2}) request_id={}",
                    chest_id, position.x, position.y, position.z, request_id
                ),
                Err(reason) => info!(
                    "MPTRACE step=CHEST event=chest_seed_ignored reason={reason} request_id={request_id}"
                ),
            }
        }
        // ADR-028 Fase A: loot one stack from a corpse. The server only keeps the container
        // accounting (the granted stack's destination — the looter's STP inventory — lives
        // client-side, same trust principle as pickup). Success is confirmed by the
        // "corpse_item_taken" event; the WorldState reflects the new contents next tick and
        // the corpse despawns by absence once empty. No reservation: the single-threaded
        // loop serializes competing takes (the loser gets a logged rejection).
        "take_corpse_item" => {
            let corpse_id = json_u32(&action.data, "corpse_id").unwrap_or(0);
            let item_index = json_u32(&action.data, "item_index").unwrap_or(u32::MAX) as usize;
            let quantity = json_u32(&action.data, "quantity")
                .map(|q| q.min(u16::MAX as u32) as u16)
                .unwrap_or(0);
            // ADR-028 Fase E: a joiner forwards to the host (authority) instead of mutating
            // its local mirror (which the 10 Hz roster broadcast overwrites anyway). The
            // verdict comes back as a reliable CorpseTakeResult → the same IPC events.
            if !net.is_host {
                let request_id = net.next_corpse_request_id;
                net.next_corpse_request_id += 1;
                let payload = PacketPayload::CorpseTakeRequest {
                    request_id,
                    requester_id: net.local_id,
                    corpse_id,
                    item_index: item_index as u32,
                    quantity,
                    requester_pos: player.position.to_array(),
                };
                info!(
                    "MPTRACE step=CORPSE event=corpse_take_forwarded_to_host request_id={} corpse_id={} item_index={} quantity={}",
                    request_id, corpse_id, item_index, quantity
                );
                net.send_reliable(1, &payload).await;
                return;
            }
            match world.take_corpse_item(
                corpse_id,
                item_index,
                quantity,
                player.position,
                crate::world::corpse::CORPSE_LOOT_MAX_DISTANCE,
            ) {
                Ok(taken) => {
                    let corpse_empty = !world.corpses.contains_key(&corpse_id);
                    info!(
                        "MPTRACE step=CORPSE event=corpse_item_taken corpse_id={} item_index={} item_id={} quantity={} corpse_empty={}",
                        corpse_id, item_index, taken.item_id, taken.quantity, corpse_empty
                    );
                    let _ = to_clients.send(ServerMessage::Event(GameEvent {
                        event_type: "corpse_item_taken".into(),
                        data: serde_json::json!({
                            "corpse_id": corpse_id,
                            "item_index": item_index,
                            "item_id": taken.item_id,
                            "quantity": taken.quantity,
                            "corpse_empty": corpse_empty,
                        }),
                    }));
                }
                Err(reason) => {
                    info!(
                        "MPTRACE step=CORPSE event=corpse_take_rejected corpse_id={} item_index={} reason={}",
                        corpse_id, item_index, reason
                    );
                    // Fase D fix #1: the client applies the take LOCALLY the instant the drag/
                    // Take-All gesture completes (StorageStationUI's native transfer has no
                    // request-gate — see CorpseLootSync class doc) and rolls it back on rejection.
                    // Without this broadcast the client would never learn to roll back, and the
                    // "server manda, cliente refleja" invariant would silently break on rejection.
                    let _ = to_clients.send(ServerMessage::Event(GameEvent {
                        event_type: "corpse_take_rejected".into(),
                        data: serde_json::json!({
                            "corpse_id": corpse_id,
                            "item_index": item_index,
                            "reason": reason,
                        }),
                    }));
                }
            }
        }
        "world_interact" => {
            let target_id = json_u32(&action.data, "target_id").unwrap_or(0);
            let request_id = json_u64(&action.data, "request_id").unwrap_or(0);
            let target_kind = json_str(&action.data, "target_kind").unwrap_or("item");
            let interaction_type = json_str(&action.data, "interaction_type").unwrap_or("pickup");
            info!(
                "MPTRACE step=AC event=interact_request_from_ipc self_id={} target_id={} kind={} type={} request_id={}",
                net.local_id,
                target_id,
                target_kind,
                interaction_type,
                request_id
            );

            // pickup needs an existing target_id; drop has none (it creates an item).
            if request_id == 0 {
                info!(
                    "MPTRACE step=AF event=host_validate_interaction result=rejected reason=invalid_request_id target_id={} requester_id={}",
                    target_id,
                    net.local_id
                );
                return;
            }
            if interaction_type != "drop" && target_id == 0 {
                info!(
                    "MPTRACE step=AF event=host_validate_interaction result=rejected reason=invalid_target_id target_id={} requester_id={}",
                    target_id,
                    net.local_id
                );
                return;
            }

            if net.is_host {
                process_authoritative_interaction(
                    net.local_id,
                    request_id,
                    target_id,
                    target_kind,
                    interaction_type,
                    player.position,
                    world,
                    net,
                    player,
                    processed_interactions,
                )
                .await;
            } else {
                let payload = crate::network::protocol::PacketPayload::Interact {
                    requester_id: net.local_id,
                    request_id,
                    target_id,
                    target_kind: target_kind.into(),
                    interaction_type: interaction_type.into(),
                    player_position: player.position.to_array(),
                };
                info!(
                    "MPTRACE step=AD event=send_interact_request_to_host self_id={} host_id=1 target_id={} request_id={}",
                    net.local_id,
                    target_id,
                    request_id
                );
                net.send_reliable(1, &payload).await;
            }
        }
        "attack" => {
            let player_pos = player.position;
            let attack_range = 3.0f32;
            let damage = 10u8;
            for chunk in world.chunks.values_mut() {
                for entity in chunk.entities.iter_mut() {
                    if entity.is_alive() && entity.position.distance_xz(player_pos) <= attack_range
                    {
                        let killed = entity.take_damage(damage);
                        if killed {
                            let cable_count: u16 = rand::random::<u16>() % 6 + 5;
                            player
                                .inventory
                                .add(crate::player::inventory::Item::Cable, cable_count);
                            info!("Entity {} killed, dropped {} cable", entity.id, cable_count);
                        }
                        break;
                    }
                }
            }
        }
        "interact" | "pickup" => {
            debug!("legacy local pickup ignored; use world_interact with target_id");
        }
        // Phase 1: the host Unity registers the authoritative STP item list. Gated to
        // the host (joiners' lists come only via the relayed StpItemList packet, so a
        // joiner sending this is ignored and cannot diverge the world).
        "set_stp_items" => {
            if !net.is_host {
                return;
            }
            let items: Vec<crate::network::protocol::StpItemInfo> = action
                .data
                .get("items")
                .cloned()
                .and_then(|v| serde_json::from_value(v).ok())
                .unwrap_or_default();
            info!(
                "MPTRACE step=SI event=host_set_stp_items self_id={} count={}",
                net.local_id,
                items.len()
            );
            net.stp_items = items;
            // ADR-014 invariant: drop reservations for items no longer present, so pending_pickups
            // never points at a vanished item.
            let present: std::collections::HashSet<u32> =
                net.stp_items.iter().map(|it| it.id).collect();
            net.pending_pickups
                .retain(|item_id, _| present.contains(item_id));
        }
        // Phase 2: a client asks to pick up an STP item. Host-authoritative: the host
        // validates and removes it (vanishes for all via the stp_items relay) and grants
        // it to the requester; a joiner forwards the request to the host.
        "stp_pickup" => {
            let item_id = json_u32(&action.data, "item_id").unwrap_or(0);
            if item_id == 0 {
                return;
            }
            if net.is_host {
                process_stp_pickup(item_id, net.local_id, net, to_clients).await;
            } else {
                let payload = crate::network::protocol::PacketPayload::StpPickupRequest {
                    item_id,
                    requester_id: net.local_id,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase 3: a client dropped an STP item from its inventory. Client-authoritative over
        // its own inventory; the host assigns a fresh net id and adds it to stp_items, which
        // the Phase 1 relay propagates so everyone spawns the same pickup (with the Phase 2 gate).
        "stp_drop" => {
            let drop_id = action
                .data
                .get("drop_id")
                .and_then(|v| v.as_u64())
                .unwrap_or(0);
            let def_id = action
                .data
                .get("def_id")
                .and_then(|v| v.as_i64())
                .unwrap_or(0) as i32;
            let count = action
                .data
                .get("count")
                .and_then(|v| v.as_u64())
                .unwrap_or(1)
                .max(1) as u16;
            let position: [f32; 3] = serde_json::from_value(
                action
                    .data
                    .get("position")
                    .cloned()
                    .unwrap_or(serde_json::Value::Null),
            )
            .unwrap_or([0.0, 0.0, 0.0]);
            let rotation = action
                .data
                .get("rotation")
                .and_then(|v| v.as_f64())
                .unwrap_or(0.0) as f32;
            if net.is_host {
                process_stp_drop(drop_id, def_id, count, position, rotation, net);
            } else {
                let payload = crate::network::protocol::PacketPayload::StpDropRequest {
                    drop_id,
                    def_id,
                    count,
                    position,
                    rotation,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase B1: a client placed an STP building piece. The host assigns a fresh net id
        // and adds it to stp_buildings, which the Phase B1 relay propagates so everyone
        // spawns the same piece (StpBuildingReplicator). A joiner forwards to the host.
        "stp_place" => {
            let place_id = action
                .data
                .get("place_id")
                .and_then(|v| v.as_u64())
                .unwrap_or(0);
            let def_id = action
                .data
                .get("def_id")
                .and_then(|v| v.as_i64())
                .unwrap_or(0) as i32;
            let position: [f32; 3] = serde_json::from_value(
                action
                    .data
                    .get("position")
                    .cloned()
                    .unwrap_or(serde_json::Value::Null),
            )
            .unwrap_or([0.0, 0.0, 0.0]);
            let rotation = action
                .data
                .get("rotation")
                .and_then(|v| v.as_f64())
                .unwrap_or(0.0) as f32;
            let group_id = action
                .data
                .get("group_id")
                .and_then(|v| v.as_u64())
                .unwrap_or(0) as u32;
            let is_group = action
                .data
                .get("is_group")
                .and_then(|v| v.as_bool())
                .unwrap_or(false);
            // ADR-031: placing a Sleeping Bag sets THIS player's respawn point ("last placed wins").
            // Runs on the placer's own backend (host OR joiner) since the stp_place action arrives here
            // regardless of who relays the building; trust-the-client for the position (same level as
            // report_death_loot — the server validates it when resolving the spawn).
            if def_id == BED_DEF_ID {
                player.respawn_point = Some(Vec3::from_array(position));
                info!(
                    "MPTRACE step=BED event=respawn_point_set pos=({:.2},{:.2},{:.2})",
                    position[0], position[1], position[2]
                );
            }
            if net.is_host {
                process_stp_place(
                    place_id, def_id, position, rotation, group_id, is_group, net,
                );
                // Phase B3: relay immediately so the placer's replicated copy (with its group)
                // arrives within ~RTT, closing the round-trip gap when chaining pieces.
                sync::broadcast_stp_buildings(net).await;
            } else {
                let payload = crate::network::protocol::PacketPayload::StpPlaceRequest {
                    place_id,
                    def_id,
                    position,
                    rotation,
                    group_id,
                    is_group,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase B2: a client added one unit of build material to a piece. The host advances
        // the piece's authoritative progress (added[material]) and the relay propagates it so
        // every client derives the same construction state. A joiner forwards to the host.
        "stp_build_add" => {
            let add_id = action
                .data
                .get("add_id")
                .and_then(|v| v.as_u64())
                .unwrap_or(0);
            let building_id = action
                .data
                .get("building_id")
                .and_then(|v| v.as_u64())
                .unwrap_or(0) as u32;
            let material_id = action
                .data
                .get("material_id")
                .and_then(|v| v.as_i64())
                .unwrap_or(0) as i32;
            if building_id == 0 {
                return;
            }
            if net.is_host {
                process_stp_build_add(add_id, building_id, material_id, net);
            } else {
                let payload = crate::network::protocol::PacketPayload::StpBuildAddRequest {
                    add_id,
                    building_id,
                    material_id,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // ADR-037: a client cancelled a placed-but-unbuilt piece. The host retires it from
        // stp_buildings and the relay makes every client's replicator drop its copy. A joiner
        // forwards to the host and never mutates the roster itself.
        "stp_demolish" => {
            let demolish_id = action
                .data
                .get("demolish_id")
                .and_then(|v| v.as_u64())
                .unwrap_or(0);
            let building_id = action
                .data
                .get("building_id")
                .and_then(|v| v.as_u64())
                .unwrap_or(0) as u32;
            if building_id == 0 {
                return;
            }

            // ADR-031 follow-up, finally closed: a Sleeping Bag placement set THIS player's
            // respawn point, so cancelling that same bag has to clear it. Read the entry BEFORE
            // the host removes it below — and match on position, because "last placed wins"
            // means the point may well belong to a different, still-standing bed.
            //
            // Runs on the canceller's own backend (host OR joiner), mirroring where `stp_place`
            // sets it. Known gap, documented in ADR-037: ANOTHER player whose respawn point was
            // this bed keeps a stale one — their Player lives in their own backend and the host
            // does not own it. They respawn at a safe cell where the bed used to be, which is a
            // stale point, not an invalid state.
            if let Some(building) = net.stp_buildings.iter().find(|b| b.id == building_id) {
                if building.def_id == BED_DEF_ID {
                    if let Some(point) = player.respawn_point {
                        const BED_MATCH_RADIUS_M: f32 = 0.5;
                        if point.distance(Vec3::from_array(building.position)) < BED_MATCH_RADIUS_M
                        {
                            player.respawn_point = None;
                            info!(
                                "MPTRACE step=BED event=respawn_point_cleared building_id={} pos=({:.2},{:.2},{:.2})",
                                building_id,
                                building.position[0],
                                building.position[1],
                                building.position[2]
                            );
                        }
                    }
                }
            }

            if net.is_host {
                process_stp_demolish(demolish_id, building_id, net);
                sync::broadcast_stp_buildings(net).await;
            } else {
                let payload = crate::network::protocol::PacketPayload::StpDemolishRequest {
                    demolish_id,
                    building_id,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase B2.5: the host Unity registers the authoritative carryable list (host-only;
        // a joiner sending this is ignored so it can't diverge the world).
        "set_stp_carryables" => {
            if !net.is_host {
                return;
            }
            let carryables: Vec<crate::network::protocol::StpCarryableInfo> = action
                .data
                .get("carryables")
                .cloned()
                .and_then(|v| serde_json::from_value(v).ok())
                .unwrap_or_default();
            info!(
                "MPTRACE step=CY event=host_set_stp_carryables self_id={} count={}",
                net.local_id,
                carryables.len()
            );
            net.stp_carryables = carryables;
        }
        // Phase B2.5: a client asks to pick up a world carryable. Host-authoritative: the
        // host removes it (vanishes for all via the relay) and grants it to the requester,
        // who carries it in hand. A joiner forwards the request to the host.
        "stp_carryable_pickup" => {
            let carryable_id = json_u32(&action.data, "carryable_id").unwrap_or(0);
            if carryable_id == 0 {
                return;
            }
            if net.is_host {
                process_stp_carryable_pickup(carryable_id, net.local_id, net, to_clients).await;
            } else {
                let payload = crate::network::protocol::PacketPayload::StpCarryablePickupRequest {
                    carryable_id,
                    requester_id: net.local_id,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase B2.5: a client dropped a carryable in the world. The host assigns a fresh id
        // and adds it to stp_carryables, which the relay propagates so everyone spawns it.
        "stp_carryable_drop" => {
            let drop_id = action
                .data
                .get("drop_id")
                .and_then(|v| v.as_u64())
                .unwrap_or(0);
            let def_id = action
                .data
                .get("def_id")
                .and_then(|v| v.as_i64())
                .unwrap_or(0) as i32;
            let position: [f32; 3] = serde_json::from_value(
                action
                    .data
                    .get("position")
                    .cloned()
                    .unwrap_or(serde_json::Value::Null),
            )
            .unwrap_or([0.0, 0.0, 0.0]);
            let rotation = action
                .data
                .get("rotation")
                .and_then(|v| v.as_f64())
                .unwrap_or(0.0) as f32;
            if net.is_host {
                process_stp_carryable_drop(drop_id, def_id, position, rotation, net);
            } else {
                let payload = crate::network::protocol::PacketPayload::StpCarryableDropRequest {
                    drop_id,
                    def_id,
                    position,
                    rotation,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // Phase B2.6: the host Unity registers the authoritative scene-harvestable list
        // (host-only; remaining starts full). A joiner sending this is ignored.
        "set_stp_harvestables" => {
            if !net.is_host {
                return;
            }
            #[derive(serde::Deserialize)]
            struct HarvestableSpec {
                id: u32,
                position: [f32; 3],
            }
            let specs: Vec<HarvestableSpec> = action
                .data
                .get("harvestables")
                .cloned()
                .and_then(|v| serde_json::from_value(v).ok())
                .unwrap_or_default();
            net.stp_harvestables = specs
                .into_iter()
                .map(|s| crate::network::protocol::StpHarvestableInfo {
                    id: s.id,
                    position: s.position,
                    remaining: 1.0,
                })
                .collect();
            info!(
                "MPTRACE step=HV event=host_set_stp_harvestables self_id={} count={}",
                net.local_id,
                net.stp_harvestables.len()
            );
        }
        // Phase B2.6: a client reports a harvest hit. Host-authoritative: the host reduces the
        // harvestable's `remaining` and the relay propagates it. A joiner forwards to the host.
        "stp_harvest_hit" => {
            let hit_id = action
                .data
                .get("hit_id")
                .and_then(|v| v.as_u64())
                .unwrap_or(0);
            let harvestable_id = action
                .data
                .get("harvestable_id")
                .and_then(|v| v.as_u64())
                .unwrap_or(0) as u32;
            let amount = action
                .data
                .get("amount")
                .and_then(|v| v.as_f64())
                .unwrap_or(0.0) as f32;
            if harvestable_id == 0 {
                return;
            }
            if net.is_host {
                process_stp_harvest_hit(hit_id, harvestable_id, amount, net);
            } else {
                let payload = crate::network::protocol::PacketPayload::StpHarvestHitRequest {
                    hit_id,
                    harvestable_id,
                    amount,
                };
                net.send_reliable(1, &payload).await;
            }
        }
        // ADR-029 V0 Fase 1+3: Unity reports a candidate PvP hit against a remote-player
        // proxy. Unity NEVER applies damage — this backend either validates it directly (if
        // it is the host) or forwards it to the host for validation (if it is a joiner).
        // `attacker_id` is ALWAYS this backend's own `net.local_id`, never the IPC-reported
        // value — same principle as `world_interact`'s `requester_id` (this backend is the
        // only trustworthy source of "who is shooting" for itself).
        "pvp_hit_candidate" => {
            let request_id = json_u64(&action.data, "request_id").unwrap_or(0);
            if request_id == 0 {
                info!("MPTRACE step=PVP event=pvp_hit_candidate_ignored reason=invalid_request_id");
                return;
            }
            let victim_id = json_u32(&action.data, "victim_id").unwrap_or(0);
            let weapon_id = json_i32(&action.data, "weapon_id").unwrap_or(0);
            let damage = action
                .data
                .get("damage")
                .and_then(|v| v.as_f64())
                .unwrap_or(0.0) as f32;
            let origin = json_vec3(&action.data, "origin").unwrap_or([0.0, 0.0, 0.0]);
            let direction = json_vec3(&action.data, "direction").unwrap_or([0.0, 0.0, 0.0]);
            let hit_position = json_vec3(&action.data, "hit_position");
            let client_tick = json_u32(&action.data, "client_tick");
            let attacker_id = net.local_id as u32;

            info!(
                "MPTRACE step=PVP event=pvp_hit_candidate_received self_id={} request_id={} attacker_id={} victim_id={} weapon_id={} damage={:.1}",
                net.local_id, request_id, attacker_id, victim_id, weapon_id, damage
            );

            if net.is_host {
                process_pvp_hit_candidate_host(
                    PvpCandidateFields {
                        request_id,
                        attacker_id,
                        victim_id,
                        weapon_id,
                        damage,
                        direction,
                    },
                    player,
                    net,
                    to_clients,
                    tick,
                )
                .await;
            } else {
                let payload = PacketPayload::PvpHitCandidate {
                    request_id,
                    attacker_id,
                    victim_id,
                    weapon_id,
                    damage,
                    origin,
                    direction,
                    client_tick,
                    hit_position,
                };
                info!(
                    "MPTRACE step=PVP event=pvp_hit_candidate_forwarded_to_host request_id={} victim_id={} weapon_id={}",
                    request_id, victim_id, weapon_id
                );
                net.send_reliable(1, &payload).await;
            }
        }
        _ => {}
    }
}

// ─── ADR-029 V0: PvP hit candidate → host validation → victim-applied damage ───
//
// Authority split (see ADR-029 "Decision de autoridad" + the invulnerability amendment):
// Unity never applies PvP damage. The HOST validates a candidate (11-step order below,
// short-circuiting at the first failure) and either grants it to the victim's own backend
// or rejects it back to the shooter's own backend. The VICTIM's own backend is the only
// place `PlayerStats::take_damage` is ever called for PvP — even a host-granted hit is
// re-checked there (dedupe + invulnerability), because the host cannot see a remote peer's
// `invuln_until_tick` (not relayed over the wire — see the amendment).

/// Everything needed to validate/dispatch ONE candidate hit, gathered from `handle_action`
/// (local shot) or a `NetworkEvent::PvpHitCandidate` (a remote peer's shot, forwarded to us
/// because we are the host). `attacker_id` is always a value the CALLER already trusts (this
/// backend's own `net.local_id` for a local shot, or the sender's self-reported id for a
/// forwarded one — same trust level as the rest of the P2P relay, e.g. `CorpseSpawnRequest`).
struct PvpCandidateFields {
    request_id: u64,
    attacker_id: u32,
    victim_id: u32,
    weapon_id: i32,
    damage: f32,
    direction: [f32; 3],
}

/// Hardcoded per-weapon caps (max damage per hit, max effective range in meters). The ids are
/// the REAL STP `DataIdReference` ids of the weapon `ItemDefinition` assets, confirmed by
/// reading the assets in Fase 2. Any id NOT in this list of 7 (and `0`) rejects
/// `invalid_weapon`.
struct PvpWeaponSpec {
    max_damage: f32,
    max_range: f32,
}

/// `weapon_id == 0` is STP's own "no item" sentinel and is ALWAYS rejected (`invalid_weapon`),
/// regardless of this table, per ADR-029's validation list item 7.
///
// TODO(balance): placeholder values, pendiente pasada de balance dedicada — NO son valores finales.
fn pvp_weapon_spec(weapon_id: i32) -> Option<PvpWeaponSpec> {
    match weapon_id {
        0 => None,
        // ── Firearms ──
        9692212 => Some(PvpWeaponSpec {
            // STP_Marlin 336
            max_damage: 45.0,
            max_range: 100.0,
        }),
        -7892144 => Some(PvpWeaponSpec {
            // STP_Wooden Bow
            max_damage: 35.0,
            max_range: 60.0,
        }),
        // ── Melee ──
        -1198406010 => Some(PvpWeaponSpec {
            // STP_Bone Club
            max_damage: 15.0,
            max_range: 2.5,
        }),
        2211292 => Some(PvpWeaponSpec {
            // STP_Hunting Axe
            max_damage: 25.0,
            max_range: 2.5,
        }),
        -9575342 => Some(PvpWeaponSpec {
            // STP_Hunting Knife
            max_damage: 12.0,
            max_range: 2.0,
        }),
        -1159981804 => Some(PvpWeaponSpec {
            // STP_Steel Pickaxe
            max_damage: 20.0,
            max_range: 2.5,
        }),
        5085425 => Some(PvpWeaponSpec {
            // STP_Stone Spear
            max_damage: 18.0,
            max_range: 3.0,
        }),
        -52379 => Some(PvpWeaponSpec {
            // STP_Wooden Spear
            max_damage: 16.0,
            max_range: 3.0,
        }),
        _ => None,
    }
}

/// ADR-030: fixed per-item restoration applied by `"consume_item"`. The ids are the REAL STP
/// `DataIdReference` ids of the item `ItemDefinition` assets (confirmed by reading the assets'
/// `ConsumeData`). Values are the MIDPOINT of that asset's own authored `_hungerChange`/
/// `_thirstChange`/`_healthChange` range, simplified to a single fixed number (the server has
/// no client-reported roll to apply — trust-the-client stops at "this item was consumed", per
/// ADR-030). Any id NOT in this table rejects `unknown_item`.
///
// TODO(balance): fixed value = midpoint of the asset's authored range, not a re-balanced number.
struct ConsumableSpec {
    hunger_restore: f32,
    thirst_restore: f32,
    health_restore: f32,
}

fn consumable_spec(item_id: i32) -> Option<ConsumableSpec> {
    match item_id {
        -5498592 => Some(ConsumableSpec {
            // STP_Apple: hunger 10..25, thirst 5..10
            hunger_restore: 17.5,
            thirst_restore: 7.5,
            health_restore: 0.0,
        }),
        1045632 => Some(ConsumableSpec {
            // STP_Cooked Meat: hunger 40..50
            hunger_restore: 45.0,
            thirst_restore: 0.0,
            health_restore: 0.0,
        }),
        -7862085 => Some(ConsumableSpec {
            // STP_Energy Bar: hunger 25..30
            hunger_restore: 27.5,
            thirst_restore: 0.0,
            health_restore: 0.0,
        }),
        6285896 => Some(ConsumableSpec {
            // STP_Large Food Can: hunger 50..65, thirst 10..15
            hunger_restore: 57.5,
            thirst_restore: 12.5,
            health_restore: 0.0,
        }),
        -7580928 => Some(ConsumableSpec {
            // STP_Small Food Can: hunger 30..40, thirst 5..10
            hunger_restore: 35.0,
            thirst_restore: 7.5,
            health_restore: 0.0,
        }),
        7983286 => Some(ConsumableSpec {
            // STP_Water Bottle: thirst 40..50
            hunger_restore: 0.0,
            thirst_restore: 45.0,
            health_restore: 0.0,
        }),
        -7174886 => Some(ConsumableSpec {
            // STP_Antibiotics: health 50..60
            hunger_restore: 0.0,
            thirst_restore: 0.0,
            health_restore: 55.0,
        }),
        _ => None,
    }
}

/// PeerId is u16; the wire/ADR fields are u32. A value that doesn't fit u16 can never be a
/// real peer id (host=1, joiners ∈[1000,60999], phantoms ≥0xF000 per network/mod.rs), so it
/// safely resolves to "unknown" rather than panicking or silently truncating.
fn peer_id_from_u32(id: u32) -> Option<PeerId> {
    u16::try_from(id).ok()
}

/// Pure input to `validate_pvp_hit` — everything pre-resolved from `Player`/`NetworkManager`
/// so the 11-step validation order is unit-testable without a live UDP socket or async runtime.
struct PvpValidationInput {
    is_host: bool,
    request_id: u64,
    attacker_id: u32,
    victim_id: u32,
    attacker_known: bool,
    victim_known: bool,
    victim_dead: bool,
    /// Only meaningful when the victim is THIS backend's own local player — see the
    /// invulnerability amendment for why a remote victim's value can't be checked here.
    victim_invuln: bool,
    weapon_id: i32,
    damage: f32,
    direction: [f32; 3],
    attacker_pos: Vec3,
    victim_pos: Vec3,
}

enum PvpVerdict {
    Accepted { clamped_damage: f32 },
    Rejected(&'static str),
}

/// The 11-step validation order from ADR-029, short-circuiting at the first failure. Step
/// 11 (line of sight) is a gated, degradable stub in V0 (see `process_pvp_hit_candidate_host`
/// for the flag check) — it never rejects here, so it isn't represented as a branch.
fn validate_pvp_hit(
    input: &PvpValidationInput,
    dedupe: &mut BoundedDedupeSet<(u32, u64)>,
) -> PvpVerdict {
    if !input.is_host {
        return PvpVerdict::Rejected("not_authority");
    }
    if input.victim_id == input.attacker_id {
        return PvpVerdict::Rejected("self_hit");
    }
    if !input.attacker_known {
        return PvpVerdict::Rejected("attacker_missing");
    }
    if !input.victim_known {
        return PvpVerdict::Rejected("victim_missing");
    }
    if !dedupe.insert((input.attacker_id, input.request_id)) {
        return PvpVerdict::Rejected("duplicate");
    }
    if input.victim_dead {
        return PvpVerdict::Rejected("victim_dead");
    }
    if input.victim_invuln {
        return PvpVerdict::Rejected("victim_invulnerable");
    }
    let Some(spec) = pvp_weapon_spec(input.weapon_id) else {
        return PvpVerdict::Rejected("invalid_weapon");
    };
    if !input.damage.is_finite() || input.damage <= 0.0 {
        return PvpVerdict::Rejected("invalid_damage");
    }
    // "El host clamp/reject" (ADR-029): an over-cap hit still lands, clamped, rather than
    // being thrown away outright — matches `PvpDamageGrant.damage`'s doc ("ya validado/
    // clampado por host").
    let clamped_damage = input.damage.min(spec.max_damage);
    let dir_len = Vec3::from_array(input.direction).length();
    if !dir_len.is_finite() || dir_len < 1e-4 {
        return PvpVerdict::Rejected("invalid_direction");
    }
    let dist = input.attacker_pos.distance(input.victim_pos);
    if !dist.is_finite() || dist > spec.max_range {
        return PvpVerdict::Rejected("too_far");
    }
    PvpVerdict::Accepted { clamped_damage }
}

/// The ONLY place PvP damage is actually applied (victim-applied damage, ADR-029's core
/// authority split). Runs on whichever backend owns `stats` — the host, when the victim is
/// its own local player (no network hop), or a joiner, from `NetworkEvent::PvpDamageGrant`.
/// Defensive dedupe guards a retransmitted grant from ever applying damage twice; the
/// invulnerability re-check is the REAL enforcement point for a victim the host could not
/// check itself (a remote peer's `invuln_until_tick` isn't relayed over the wire).
fn apply_pvp_damage_grant(
    stats: &mut crate::player::stats::PlayerStats,
    dedupe: &mut BoundedDedupeSet<(u32, u64)>,
    attacker_id: u32,
    request_id: u64,
    damage: f32,
    tick: u64,
) -> Result<f32, &'static str> {
    if !dedupe.insert((attacker_id, request_id)) {
        return Err("duplicate");
    }
    if stats.invuln_until_tick > tick as u32 {
        return Err("victim_invulnerable");
    }
    stats.take_damage(damage);
    Ok(stats.health)
}

/// Host-side entry point for a PvP hit candidate, whether it came directly from OUR own
/// attached Unity (`attacker_id == net.local_id`) or was forwarded here via a
/// `PvpHitCandidate` P2P packet from a remote peer's backend. Resolves attacker/victim
/// position + known-ness + dead/invuln state into a `PvpValidationInput`, runs the
/// validation order, and dispatches the grant/reject to whichever backend needs it —
/// applying directly (no network hop) when the affected party is this same backend's own
/// local player.
async fn process_pvp_hit_candidate_host(
    candidate: PvpCandidateFields,
    player: &mut Player,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
    tick: u64,
) {
    // Step 11 (line of sight) is degradable in V0: gated by a flag, never actually rejects
    // (no raycast-vs-backend-collision implemented yet — see ADR-029 §11's own escape
    // hatch: "documentar line_of_sight_failed... pero no saltarse distancia/dedupe/dano").
    if env_flag_enabled("PVP_LOS_CHECK_ENABLED") {
        info!(
            "MPTRACE step=PVP event=pvp_los_check_stub reason=not_implemented request_id={}",
            candidate.request_id
        );
    }

    let local_id_u32 = net.local_id as u32;
    let attacker_is_local = candidate.attacker_id == local_id_u32;
    let victim_is_local = candidate.victim_id == local_id_u32;

    let attacker_known = attacker_is_local
        || peer_id_from_u32(candidate.attacker_id)
            .map(|id| net.peers.contains_key(&id))
            .unwrap_or(false);
    let victim_known = victim_is_local
        || peer_id_from_u32(candidate.victim_id)
            .map(|id| net.peers.contains_key(&id))
            .unwrap_or(false);

    let victim_dead = if victim_is_local {
        player.stats.is_dead()
    } else {
        peer_id_from_u32(candidate.victim_id)
            .and_then(|id| net.peers.get(&id))
            .map(|p| p.dead)
            .unwrap_or(false)
    };
    // See the invulnerability amendment: only checkable host-side when the victim IS this
    // backend's own local player. A remote peer's real value is re-checked defensively on
    // ITS OWN backend in `apply_pvp_damage_grant`.
    let victim_invuln = victim_is_local && player.stats.invuln_until_tick > tick as u32;

    let attacker_pos = if attacker_is_local {
        player.position
    } else {
        peer_id_from_u32(candidate.attacker_id)
            .and_then(|id| net.peers.get(&id))
            .map(|p| Vec3::from_array(p.position))
            .unwrap_or(Vec3::ZERO)
    };
    let victim_pos = if victim_is_local {
        player.position
    } else {
        peer_id_from_u32(candidate.victim_id)
            .and_then(|id| net.peers.get(&id))
            .map(|p| Vec3::from_array(p.position))
            .unwrap_or(Vec3::ZERO)
    };

    let input = PvpValidationInput {
        is_host: net.is_host,
        request_id: candidate.request_id,
        attacker_id: candidate.attacker_id,
        victim_id: candidate.victim_id,
        attacker_known,
        victim_known,
        victim_dead,
        victim_invuln,
        weapon_id: candidate.weapon_id,
        damage: candidate.damage,
        direction: candidate.direction,
        attacker_pos,
        victim_pos,
    };

    match validate_pvp_hit(&input, &mut net.processed_pvp_hits) {
        PvpVerdict::Accepted { clamped_damage } => {
            info!(
                "MPTRACE step=PVP event=pvp_hit_validated request_id={} attacker_id={} victim_id={} weapon_id={} damage={:.1}",
                candidate.request_id, candidate.attacker_id, candidate.victim_id, candidate.weapon_id, clamped_damage
            );

            if victim_is_local {
                // No network hop: the host IS the victim's own backend.
                match apply_pvp_damage_grant(
                    &mut player.stats,
                    &mut net.processed_pvp_grants,
                    candidate.attacker_id,
                    candidate.request_id,
                    clamped_damage,
                    tick,
                ) {
                    Ok(health) => {
                        let _ = to_clients.send(ServerMessage::Event(GameEvent {
                            event_type: "pvp_damage_taken".into(),
                            data: serde_json::json!({
                                "attacker_id": candidate.attacker_id,
                                "weapon_id": candidate.weapon_id,
                                "damage": clamped_damage,
                                "health": health,
                            }),
                        }));
                    }
                    Err(reason) => {
                        info!(
                            "MPTRACE step=PVP event=pvp_damage_grant_blocked_local reason={} request_id={}",
                            reason, candidate.request_id
                        );
                    }
                }
            } else if let Some(victim_peer) = peer_id_from_u32(candidate.victim_id) {
                let grant = PacketPayload::PvpDamageGrant {
                    request_id: candidate.request_id,
                    attacker_id: candidate.attacker_id,
                    victim_id: candidate.victim_id,
                    weapon_id: candidate.weapon_id,
                    damage: clamped_damage,
                    reason: "validated".into(),
                };
                net.send_reliable(victim_peer, &grant).await;
            }

            if attacker_is_local {
                let _ = to_clients.send(ServerMessage::Event(GameEvent {
                    event_type: "pvp_hit_confirmed".into(),
                    data: serde_json::json!({
                        "request_id": candidate.request_id,
                        "victim_id": candidate.victim_id,
                        "damage": clamped_damage,
                    }),
                }));
            }
            // NOTE (deviation, see final report): ADR-029's wire list defines NO P2P packet
            // for "confirmed" — only `PvpHitRejected` for rejections. A REMOTE shooter (not
            // the host) gets no explicit confirm in V0; the victim's health dropping in the
            // next WorldState/pose relay is its only feedback. Not inventing a new packet
            // here (out of this task's declared scope).
        }
        PvpVerdict::Rejected(reason) => {
            info!(
                "MPTRACE step=PVP event=pvp_hit_rejected request_id={} attacker_id={} victim_id={} reason={}",
                candidate.request_id, candidate.attacker_id, candidate.victim_id, reason
            );
            if attacker_is_local {
                let _ = to_clients.send(ServerMessage::Event(GameEvent {
                    event_type: "pvp_hit_rejected".into(),
                    data: serde_json::json!({ "request_id": candidate.request_id, "reason": reason }),
                }));
            } else if let Some(attacker_peer) = peer_id_from_u32(candidate.attacker_id) {
                let payload = PacketPayload::PvpHitRejected {
                    request_id: candidate.request_id,
                    attacker_id: candidate.attacker_id,
                    victim_id: candidate.victim_id,
                    reason: reason.into(),
                };
                net.send_reliable(attacker_peer, &payload).await;
            }
        }
    }
}

/// Monotonic id source for host-spawned dropped STP items. Starts high so it never
/// collides with the low, host-Unity-assigned ids of the Phase 1 spawn ring.
/// Bases de los tres rangos de id que acuña el BACKEND. Están particionados a propósito para
/// no colisionar con los ids que acuña el Unity del host, que van por DEBAJO de la primera.
/// `reseed_stp_id_allocators` los usa para re-sembrar dentro del rango correcto tras cargar.
const STP_DROP_ID_BASE: u32 = 0x4000_0000;
const STP_BUILDING_ID_BASE: u32 = 0x6000_0000;
const STP_CARRYABLE_ID_BASE: u32 = 0x7000_0000;

static NEXT_STP_DROP_ID: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(STP_DROP_ID_BASE);

fn next_stp_drop_id() -> u32 {
    NEXT_STP_DROP_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// Phase 3: host adds a dropped STP item to the authoritative `stp_items` list. The 10 Hz
/// relay (broadcast_stp_items) propagates it to all peers, where StpItemReplicator spawns it.
/// Deduped by the client-generated `drop_id`: a repeated request (watcher race OR reliable
/// retransmit when the host is slow) is ignored, so one logical drop spawns exactly one item.
fn process_stp_drop(
    drop_id: u64,
    def_id: i32,
    count: u16,
    position: [f32; 3],
    rotation: f32,
    net: &mut NetworkManager,
) {
    if drop_id != 0 && !net.processed_stp_drops.insert(drop_id) {
        info!(
            "MPTRACE step=SD event=stp_drop_duplicate drop_id={} ignored=true",
            drop_id
        );
        return;
    }

    let id = next_stp_drop_id();
    net.stp_items.push(crate::network::protocol::StpItemInfo {
        id,
        def_id,
        count,
        position,
        rotation,
    });
    info!(
        "MPTRACE step=SD event=stp_drop_spawned id={} drop_id={} def_id={} count={} pos=({:.2},{:.2},{:.2})",
        id, drop_id, def_id, count, position[0], position[1], position[2]
    );
}

/// Monotonic id source for host-spawned STP building pieces. Lives in its own high range
/// so building ids never collide with item ids (the two lists are independent, but a
/// distinct range keeps logs unambiguous).
static NEXT_STP_BUILDING_ID: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(STP_BUILDING_ID_BASE);

fn next_stp_building_id() -> u32 {
    NEXT_STP_BUILDING_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// Phase B3: monotonic id source for host-minted STP building GROUPS. Starts at 1 so
/// `group_id == 0` always means "standalone / no group".
static NEXT_STP_GROUP_ID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(1);

fn next_stp_group_id() -> u32 {
    NEXT_STP_GROUP_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// Phase B3: quantize a placed pose to a dedup cell. Two clients targeting the same socket
/// instantiate the same prefab on the same authoritative anchor and apply the same authored
/// offset, so they compute an identical snapped pose → identical cell. Distinct sockets are
/// far apart (≫ Q_POS), so there are no false collisions.
fn stp_pose_cell(position: [f32; 3], rotation: f32) -> (i32, i32, i32, i32) {
    const Q_POS: f32 = 0.25; // meters
    const Q_YAW: f32 = 1.0; // degrees
    (
        (position[0] / Q_POS).round() as i32,
        (position[1] / Q_POS).round() as i32,
        (position[2] / Q_POS).round() as i32,
        (rotation / Q_YAW).round() as i32,
    )
}

/// Phase B1: host adds a placed STP building piece to the authoritative `stp_buildings`
/// list. The 10 Hz relay (broadcast_stp_buildings) propagates it to all peers, where
/// StpBuildingReplicator spawns it. Deduped by the client-generated `place_id`: a repeated
/// request (reliable retransmit when the host is slow) is ignored, so one logical placement
/// spawns exactly one piece.
fn process_stp_place(
    place_id: u64,
    def_id: i32,
    position: [f32; 3],
    rotation: f32,
    req_group_id: u32,
    is_group: bool,
    net: &mut NetworkManager,
) {
    if place_id != 0 && !net.processed_stp_places.insert(place_id) {
        info!(
            "MPTRACE step=BP event=stp_place_duplicate place_id={} ignored=true",
            place_id
        );
        return;
    }

    // Phase B3: concurrency dedup by quantized pose-cell — only for group pieces (free
    // pieces may legitimately stack). The first placement at a cell wins; a second one
    // targeting the same socket (distinct place_id) is rejected, so no duplicate / no
    // doubly-occupied socket.
    if is_group {
        let cell = stp_pose_cell(position, rotation);
        if !net.occupied_stp_cells.insert(cell) {
            info!(
                "MPTRACE step=BP event=stp_place_cell_taken place_id={} cell=({},{},{},{}) rejected=true",
                place_id, cell.0, cell.1, cell.2, cell.3
            );
            return;
        }
    }

    // Phase B3: resolve the group. Free piece → 0; existing group → echo it; new group
    // (group_id == 0 with is_group) → mint a fresh host-authoritative id.
    let group_id = if !is_group {
        0
    } else if req_group_id != 0 {
        req_group_id
    } else {
        next_stp_group_id()
    };

    let id = next_stp_building_id();
    net.stp_buildings
        .push(crate::network::protocol::StpBuildingInfo {
            id,
            def_id,
            position,
            rotation,
            group_id,
            added: Vec::new(),
        });
    info!(
        "MPTRACE step=BP event=stp_place_spawned id={} place_id={} def_id={} group_id={} pos=({:.2},{:.2},{:.2})",
        id, place_id, def_id, group_id, position[0], position[1], position[2]
    );
}

/// Phase B2: host advances the authoritative construction progress of a building piece.
/// Accumulates one unit of `material_id` into the piece's `added` list. Deduped by the
/// client-generated `add_id` (reliable retransmit safe). The 10 Hz relay propagates the
/// updated `stp_buildings`, where StpBuildingReplicator derives the per-client progress.
fn process_stp_build_add(
    add_id: u64,
    building_id: u32,
    material_id: i32,
    net: &mut NetworkManager,
) {
    if add_id != 0 && !net.processed_stp_build_adds.insert(add_id) {
        info!(
            "MPTRACE step=BM event=stp_build_add_duplicate add_id={} ignored=true",
            add_id
        );
        return;
    }

    let building = match net.stp_buildings.iter_mut().find(|b| b.id == building_id) {
        Some(b) => b,
        None => {
            info!(
                "MPTRACE step=BM event=stp_build_add_no_building building_id={} add_id={} ignored=true",
                building_id, add_id
            );
            return;
        }
    };

    match building
        .added
        .iter_mut()
        .find(|p| p.material_id == material_id)
    {
        Some(p) => p.count = p.count.saturating_add(1),
        None => building
            .added
            .push(crate::network::protocol::StpBuildProgress {
                material_id,
                count: 1,
            }),
    }

    info!(
        "MPTRACE step=BM event=stp_build_add building_id={} material_id={} add_id={}",
        building_id, material_id, add_id
    );
}

/// ADR-037: host retires a placed-but-unbuilt piece from the authoritative `stp_buildings`
/// list. The 10 Hz relay stops emitting it and the stale-sweep that `StpBuildingReplicator`
/// already runs destroys the copy on every client — so this is the whole demolish path on
/// the backend, and not one line of destruction code is needed on the client.
///
/// Deduped by the client-generated `demolish_id`, mirroring `process_stp_place` /
/// `process_stp_build_add`.
fn process_stp_demolish(demolish_id: u64, building_id: u32, net: &mut NetworkManager) {
    if demolish_id != 0 && !net.processed_stp_demolishes.insert(demolish_id) {
        info!(
            "MPTRACE step=BD event=stp_demolish_duplicate demolish_id={} ignored=true",
            demolish_id
        );
        return;
    }

    let index = match net.stp_buildings.iter().position(|b| b.id == building_id) {
        Some(i) => i,
        None => {
            // Not an error: two clients can cancel the same piece in the same window, and the
            // loser simply finds it already gone.
            info!(
                "MPTRACE step=BD event=stp_demolish_no_building building_id={} demolish_id={} ignored=true",
                building_id, demolish_id
            );
            return;
        }
    };

    let removed = net.stp_buildings.remove(index);

    // Release the pose cell that `process_stp_place` claimed. Only group pieces ever claimed
    // one (`is_group` gates the insert there), and `group_id != 0` is exactly that condition
    // at placement time — so the flag itself never has to be stored. Skipping this would brick
    // the slot: placing, cancelling and re-placing on the same socket would be impossible for
    // the rest of the session, with a silent `stp_place_cell_taken` as the only trace.
    if removed.group_id != 0 {
        let cell = stp_pose_cell(removed.position, removed.rotation);
        net.occupied_stp_cells.remove(&cell);
    }

    // NOTE: `processed_stp_places` is deliberately NOT cleaned here. It is retransmit dedup,
    // not a live-piece census — freeing the id would let a late duplicate of the original
    // request resurrect the piece that was just cancelled.

    info!(
        "MPTRACE step=BD event=stp_demolish_removed id={} demolish_id={} def_id={} group_id={} pos=({:.2},{:.2},{:.2})",
        building_id,
        demolish_id,
        removed.def_id,
        removed.group_id,
        removed.position[0],
        removed.position[1],
        removed.position[2]
    );
}

/// Monotonic id source for host-spawned STP carryables. Lives in its own high range so
/// carryable ids never collide with item/building ids.
static NEXT_STP_CARRYABLE_ID: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(STP_CARRYABLE_ID_BASE);

fn next_stp_carryable_id() -> u32 {
    NEXT_STP_CARRYABLE_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// Phase B2.5: host adds a dropped carryable to the authoritative `stp_carryables` list.
/// The 10 Hz relay (broadcast_stp_carryables) propagates it to all peers, where
/// StpCarryableReplicator spawns it. Deduped by the client-generated `drop_id`.
fn process_stp_carryable_drop(
    drop_id: u64,
    def_id: i32,
    position: [f32; 3],
    rotation: f32,
    net: &mut NetworkManager,
) {
    if drop_id != 0 && !net.processed_stp_carryable_drops.insert(drop_id) {
        info!(
            "MPTRACE step=CY event=stp_carryable_drop_duplicate drop_id={} ignored=true",
            drop_id
        );
        return;
    }

    let id = next_stp_carryable_id();
    net.stp_carryables
        .push(crate::network::protocol::StpCarryableInfo {
            id,
            def_id,
            position,
            rotation,
        });
    info!(
        "MPTRACE step=CY event=stp_carryable_drop_spawned id={} drop_id={} def_id={} pos=({:.2},{:.2},{:.2})",
        id, drop_id, def_id, position[0], position[1], position[2]
    );
}

/// Phase B2.5: host-authoritative carryable pickup. If the carryable still exists, remove it
/// (which despawns it for everyone via the relay) and grant it to the requester — directly to
/// the host's own Unity, or reliably to a joiner. A second request for an already-taken
/// carryable finds nothing and is rejected (race-safe; the removal is the dedup).
async fn process_stp_carryable_pickup(
    carryable_id: u32,
    requester_id: crate::network::PeerId,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
) {
    let pos = match net.stp_carryables.iter().position(|c| c.id == carryable_id) {
        Some(p) => p,
        None => {
            info!(
                "MPTRACE step=CY event=stp_carryable_pickup_rejected carryable_id={} requester_id={} reason=not_found",
                carryable_id, requester_id
            );
            return;
        }
    };

    let carryable = net.stp_carryables.remove(pos);
    info!(
        "MPTRACE step=CY event=stp_carryable_pickup_granted carryable_id={} requester_id={} def_id={}",
        carryable_id, requester_id, carryable.def_id
    );

    if requester_id == net.local_id {
        let _ = to_clients.send(ServerMessage::Event(GameEvent {
            event_type: "stp_carryable_pickup_granted".into(),
            data: serde_json::json!({
                "carryable_id": carryable_id,
                "def_id": carryable.def_id,
            }),
        }));
    } else {
        let payload = crate::network::protocol::PacketPayload::StpCarryablePickupGranted {
            carryable_id,
            def_id: carryable.def_id,
        };
        net.send_reliable(requester_id, &payload).await;
    }
}

/// Phase B2.6: host reduces a scene harvestable's authoritative `remaining` by `amount`.
/// Deduped by the client-generated `hit_id` so a reliable retransmit (or two clients hitting
/// the same tree) never double-counts. The 10 Hz relay propagates the updated health, and the
/// host Unity spawns the resource carryables when it crosses to depleted (B2.6 → B2.5).
fn process_stp_harvest_hit(
    hit_id: u64,
    harvestable_id: u32,
    amount: f32,
    net: &mut NetworkManager,
) {
    if hit_id != 0 && !net.processed_stp_harvest_hits.insert(hit_id) {
        info!(
            "MPTRACE step=HV event=stp_harvest_hit_duplicate hit_id={} ignored=true",
            hit_id
        );
        return;
    }

    let harvestable = match net
        .stp_harvestables
        .iter_mut()
        .find(|h| h.id == harvestable_id)
    {
        Some(h) => h,
        None => {
            info!(
                "MPTRACE step=HV event=stp_harvest_hit_no_target harvestable_id={} hit_id={} ignored=true",
                harvestable_id, hit_id
            );
            return;
        }
    };

    harvestable.remaining = (harvestable.remaining - amount.abs()).max(0.0);
    info!(
        "MPTRACE step=HV event=stp_harvest_hit harvestable_id={} amount={:.3} remaining={:.3} hit_id={}",
        harvestable_id, amount, harvestable.remaining, hit_id
    );
}

/// Phase 2: host-authoritative STP pickup. If the item still exists, remove it (which
/// despawns it for everyone via the stp_items relay) and grant it to the requester —
/// directly to the host's own Unity, or reliably to a joiner. A second request for an
/// already-taken item finds nothing and is rejected (race-safe; the removal is the dedup).
async fn process_stp_pickup(
    item_id: u32,
    requester_id: crate::network::PeerId,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
) {
    // ADR-014: the reservation is the dedup now that the removal is deferred. A request for an
    // item already being channeled (reserved) is rejected just like a not-found one.
    if net.pending_pickups.contains_key(&item_id) {
        info!(
            "MPTRACE step=SP event=stp_pickup_rejected item_id={} requester_id={} reason=reserved",
            item_id, requester_id
        );
        return;
    }

    // The item must still exist. We do NOT remove it here (ADR-014 deferred removal): copy the
    // fields the grant needs, then reserve it; the per-tick drain removes it at remove_at.
    let (def_id, count) = match net.stp_items.iter().find(|it| it.id == item_id) {
        Some(it) => (it.def_id, it.count),
        None => {
            info!(
                "MPTRACE step=SP event=stp_pickup_rejected item_id={} requester_id={} reason=not_found",
                item_id, requester_id
            );
            return;
        }
    };

    info!(
        "MPTRACE step=SP event=stp_pickup_granted item_id={} requester_id={} def_id={} count={}",
        item_id, requester_id, def_id, count
    );

    // ADR-014: reserve — keep the item visible in stp_items until remove_at; concurrent requests
    // are rejected by the contains_key check above. The drain removes it at the juicy frame.
    net.pending_pickups.insert(
        item_id,
        (
            requester_id,
            std::time::Instant::now() + PICKUP_REMOVE_DELAY,
        ),
    );

    if requester_id == net.local_id {
        // ADR-011: our own local player picked up (host path) → stamp the pickup-anim window.
        net.last_pickup_at = Some(std::time::Instant::now());
        let _ = to_clients.send(ServerMessage::Event(GameEvent {
            event_type: "stp_pickup_granted".into(),
            data: serde_json::json!({
                "item_id": item_id,
                "def_id": def_id,
                "count": count,
            }),
        }));
    } else {
        let payload = crate::network::protocol::PacketPayload::StpPickupGranted {
            item_id,
            def_id,
            count,
        };
        net.send_reliable(requester_id, &payload).await;
    }
}

// TODO(refactor): group into a params struct; deferred to keep this diff to a lint fix.
#[allow(clippy::too_many_arguments)]
async fn process_authoritative_interaction(
    requester_id: u16,
    request_id: u64,
    target_id: u32,
    target_kind: &str,
    interaction_type: &str,
    requester_pos: Vec3,
    world: &mut World,
    net: &mut NetworkManager,
    player: &Player,
    processed_interactions: &mut HashSet<(u16, u64)>,
) {
    if requester_id != net.local_id && !net.peers.contains_key(&requester_id) {
        info!(
            "MPTRACE step=AF event=host_validate_interaction result=rejected reason=unknown_requester target_id={} requester_id={} request_id={}",
            target_id,
            requester_id,
            request_id
        );
        return;
    }

    if !processed_interactions.insert((requester_id, request_id)) {
        info!(
            "MPTRACE step=AF event=host_validate_interaction result=rejected reason=duplicate_request target_id={} requester_id={} request_id={}",
            target_id,
            requester_id,
            request_id
        );
        return;
    }

    match interaction_type {
        "pickup" => {
            if target_kind != "item" {
                info!(
                    "MPTRACE step=AF event=host_validate_interaction result=rejected reason=unsupported kind={} type={} target_id={} requester_id={}",
                    target_kind,
                    interaction_type,
                    target_id,
                    requester_id
                );
                return;
            }

            match world.interact_with_item(target_id, requester_pos, 5.0) {
                Ok((item_type, quantity)) => {
                    info!(
                        "MPTRACE step=AF event=host_validate_interaction result=accepted reason=ok target_id={} requester_id={} item_type={} quantity={}",
                        target_id,
                        requester_id,
                        item_type,
                        quantity
                    );
                    info!(
                        "MPTRACE step=AG event=world_object_state_changed revision={} target_id={} active=false kind=item requester_id={}",
                        world.revision,
                        target_id,
                        requester_id
                    );
                    info!(
                        "MPTRACE step=AH event=worldsync_after_interaction revision={} items={} entities={}",
                        world.revision,
                        world.visible_item_views().len(),
                        world.visible_entity_views().len()
                    );
                    sync::broadcast_world_sync(net, world, player).await;
                }
                Err(reason) => {
                    info!(
                        "MPTRACE step=AF event=host_validate_interaction result=rejected reason={} target_id={} requester_id={}",
                        reason,
                        target_id,
                        requester_id
                    );
                }
            }
        }
        // Drop: the client carries the item type name in `target_kind`. The host spawns
        // the item into the world at the requester's position and propagates it via the
        // SAME broadcast_world_sync the pickup uses, so A, B and C all see it appear.
        // NOTE: inventory ownership is NOT validated here — the backend does not track
        // per-peer inventories yet (the client removes from its own UI). Deferred.
        "drop" => {
            let item = item_from_type_name(target_kind);
            match world.spawn_dropped_item(requester_pos, item, 1) {
                Some(item_id) => {
                    info!(
                        "MPTRACE step=AF event=host_validate_interaction result=accepted reason=drop requester_id={} item_type={} item_id={} pos=({:.2},{:.2},{:.2})",
                        requester_id,
                        target_kind,
                        item_id,
                        requester_pos.x,
                        requester_pos.y,
                        requester_pos.z
                    );
                    info!(
                        "MPTRACE step=AH event=worldsync_after_interaction revision={} items={} entities={}",
                        world.revision,
                        world.visible_item_views().len(),
                        world.visible_entity_views().len()
                    );
                    sync::broadcast_world_sync(net, world, player).await;
                }
                None => {
                    info!(
                        "MPTRACE step=AF event=host_validate_interaction result=rejected reason=drop_chunk_not_loaded requester_id={} pos=({:.2},{:.2},{:.2})",
                        requester_id,
                        requester_pos.x,
                        requester_pos.y,
                        requester_pos.z
                    );
                }
            }
        }
        _ => {
            info!(
                "MPTRACE step=AF event=host_validate_interaction result=rejected reason=unsupported kind={} type={} target_id={} requester_id={}",
                target_kind,
                interaction_type,
                target_id,
                requester_id
            );
        }
    }
}

/// Map the wire item-type name (shared with `Item::type_name`) to an `Item` for drops.
fn item_from_type_name(name: &str) -> crate::player::inventory::Item {
    use crate::player::inventory::Item;
    match name {
        "circuit" => Item::Circuit,
        "battery" => Item::Battery,
        "cable" => Item::Cable,
        "food" => Item::Food,
        "water" => Item::Water,
        "medicine" => Item::Medicine,
        "tool" => Item::Tool,
        _ => Item::Metal,
    }
}

fn json_u64(value: &serde_json::Value, key: &str) -> Option<u64> {
    value.get(key).and_then(|v| v.as_u64())
}

fn json_u32(value: &serde_json::Value, key: &str) -> Option<u32> {
    json_u64(value, key).and_then(|v| u32::try_from(v).ok())
}

/// ADR-028: raw STP item ids (`DataIdReference` hashes) may be NEGATIVE — the u32/u64
/// helpers above would drop them. i64 → i32 with range check.
fn json_i32(value: &serde_json::Value, key: &str) -> Option<i32> {
    value
        .get(key)
        .and_then(|v| v.as_i64())
        .and_then(|v| i32::try_from(v).ok())
}

fn json_vec3(value: &serde_json::Value, key: &str) -> Option<[f32; 3]> {
    let arr = value.get(key)?.as_array()?;
    if arr.len() < 3 {
        return None;
    }
    Some([
        arr[0].as_f64()? as f32,
        arr[1].as_f64()? as f32,
        arr[2].as_f64()? as f32,
    ])
}

/// ADR-028: parse the client-reported death-loot snapshot from `report_death_loot`
/// action data: `{ equipment: [i32;4], held_item: i32, items: [{item_id, quantity}] }`.
/// Malformed or missing fields degrade to empty (never poison the corpse with junk);
/// out-of-range stacks are skipped. Length/zero-quantity hygiene is enforced again by
/// `spawn_corpse` (single choke point).
/// ADR-028 amendment (world chests): the pure gate+seed step behind the "spawn_world_chest"
/// action, extracted so host-gate/dedupe/empty-loot rules are unit-testable without a live
/// NetworkManager. Dedupe rides the SAME `processed_interactions` set `world_interact` uses,
/// keyed (player, request_id).
fn handle_spawn_world_chest(
    world: &mut World,
    is_host: bool,
    player_id: PeerId,
    request_id: u64,
    position: Vec3,
    items: Vec<crate::world::corpse::CorpseStack>,
    processed_interactions: &mut HashSet<(u16, u64)>,
) -> Result<u32, &'static str> {
    if !is_host {
        return Err("not_host");
    }
    if !processed_interactions.insert((player_id, request_id)) {
        return Err("duplicate");
    }
    // Dedup que sobrevive al REINICIO, no solo al retransmit. `StpChestSpawner` acuña sus
    // request_id como `RequestIdBase + contador de instancia`, así que la secuencia es IDÉNTICA
    // en cada lanzamiento, mientras `processed_interactions` nace vacío con cada `run()`. El
    // dedup por request_id de arriba no puede ver eso: cada arranque volvía a sembrar los 16
    // cofres sobre los que ya estaban en el save, inflando el mundo sin techo.
    //
    // Se deduplica contra los cofres YA CARGADOS, que es el estado que sí sobrevive al
    // reinicio. Solo contra cofres: un cadáver de jugador en el mismo sitio no debe bloquear
    // la siembra.
    //
    // Va DESPUÉS de `empty_loot` a propósito: esa es una regla de validez de la petición en sí
    // (ADR-028 post-E3, un cofre vacío sería inmortal) y no depende del estado del mundo, así
    // que debe seguir contestando lo mismo que antes de existir esta guardia.
    if crate::world::corpse::corpse_loot_is_empty(&items) {
        return Err("empty_loot");
    }
    if world
        .corpses
        .values()
        .any(|c| c.is_chest && same_chest_spot(c.position, position))
    {
        return Err("chest_already_seeded");
    }
    Ok(world.spawn_chest(position, items))
}

/// Dos siembras del mismo cofre de mundo. Con tolerancia y no igualdad exacta porque la
/// posición hace un viaje de ida y vuelta por f32 a través del wire y del save.
fn same_chest_spot(a: Vec3, b: Vec3) -> bool {
    const EPS: f32 = 0.5; // metros
    (a.x - b.x).abs() < EPS && (a.y - b.y).abs() < EPS && (a.z - b.z).abs() < EPS
}

fn parse_death_loot(
    data: &serde_json::Value,
) -> ([i32; 4], i32, Vec<crate::world::corpse::CorpseStack>) {
    let mut equipment = [0i32; 4];
    if let Some(arr) = data.get("equipment").and_then(|v| v.as_array()) {
        for (slot, value) in arr.iter().take(4).enumerate() {
            equipment[slot] = value
                .as_i64()
                .and_then(|v| i32::try_from(v).ok())
                .unwrap_or(0);
        }
    }

    let held_item = json_i32(data, "held_item").unwrap_or(0);

    let items = parse_loot_stacks(data);

    (equipment, held_item, items)
}

/// Parse the `items:[{item_id,quantity}]` array shared by `report_death_loot` (ADR-028) and
/// `report_inventory` (ADR-032 amendment). Extracted from `parse_death_loot` verbatim.
fn parse_loot_stacks(data: &serde_json::Value) -> Vec<crate::world::corpse::CorpseStack> {
    data.get("items")
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|entry| {
                    let item_id = json_i32(entry, "item_id")?;
                    let quantity =
                        json_u32(entry, "quantity").map(|q| q.min(u16::MAX as u32) as u16)?;
                    Some(crate::world::corpse::CorpseStack { item_id, quantity })
                })
                .collect()
        })
        .unwrap_or_default()
}

/// ADR-025 Slice B: sanitize a client-reported damage amount. Missing/NaN/∞/negative → 0
/// (never poisons the authoritative health); capped at 100 (one report can at most kill).
fn sanitize_reported_damage(raw: Option<f64>) -> f32 {
    match raw {
        Some(v) if v.is_finite() => (v as f32).clamp(0.0, 100.0),
        _ => 0.0,
    }
}

fn json_str<'a>(value: &'a serde_json::Value, key: &str) -> Option<&'a str> {
    value.get(key).and_then(|v| v.as_str())
}

fn emit_stat_warnings(player: &Player, to_clients: &broadcast::Sender<ServerMessage>) {
    let warnings = [
        ("hunger", player.stats.hunger),
        ("thirst", player.stats.thirst),
        ("sanity", player.stats.sanity),
    ];
    for (stat, value) in warnings {
        if value < 20.0 {
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "stat_warning".into(),
                data: serde_json::json!({ "stat": stat, "value": value }),
            }));
        }
    }
}

fn build_world_state(
    tick: u64,
    player: &Player,
    world: &mut World,
    net: &NetworkManager,
    ack_input_seq: u32,
) -> WorldState {
    let mut remote_players = Vec::with_capacity(net.peer_count());
    for p in net.peers.values() {
        remote_players.push(RemotePlayerState {
            id: p.id,
            name: p.name.clone(),
            position: p.position,
            rotation: p.rotation,
            animation: p.animation.clone(),
            crouch: p.crouch,
            pitch: p.pitch,
            equipment: p.equipment,
            held_item: p.held_item,
            hit_seq: p.hit_seq,
            dead: p.dead,
            revealed: p.revealed,
            light_on: p.light_on,
            fire_seq: p.fire_seq,
        });
    }

    if tick.is_multiple_of(30) {
        let remote_ids: Vec<u16> = remote_players.iter().map(|p| p.id).collect();
        info!(
            "WorldState remote_players={} ids={:?}",
            remote_players.len(),
            remote_ids
        );
        info!(
            "MPTRACE step=H event=build_world_state self_id={} sender_id=<none> assigned_id=<none> peer_id=<none> endpoint=<none> peer_count={} remote_players_count={} remote_players_ids={:?}",
            net.local_id,
            net.peer_count(),
            remote_players.len(),
            remote_ids
        );
        for remote in &remote_players {
            info!(
                "MPTRACE step=T event=worldstate_remote_transform self_id={} remote_id={} pos=({:.2},{:.2},{:.2}) rot={:.2}",
                net.local_id,
                remote.id,
                remote.position[0],
                remote.position[1],
                remote.position[2],
                remote.rotation
            );
        }
    }

    WorldState {
        tick,
        world_seed: world.seed,
        world_revision: world.revision,
        local_player: LocalPlayerState {
            position: player.position.to_array(),
            rotation: player.rotation,
            stats: StatsView {
                health: player.stats.health,
                hunger: player.stats.hunger,
                thirst: player.stats.thirst,
                sanity: player.stats.sanity,
                stamina: player.stats.stamina,
            },
            speed_modifier: player.stats.speed_modifier,
            inventory_changed: false,
            ack_input_seq,
        },
        remote_players,
        visible_chunks: world.visible_chunk_views(),
        visible_entities: world.visible_entity_views(),
        visible_items: world.visible_item_views(),
        vertical_debug_markers: world.vertical_debug_marker_views(),
        stp_items: net.stp_items.clone(),
        stp_buildings: net.stp_buildings.clone(),
        stp_carryables: net.stp_carryables.clone(),
        stp_harvestables: net.stp_harvestables.clone(),
        visible_corpses: world.visible_corpse_views(player.position),
    }
}

// ─── ADR-016: phantom driver — movement (2) + faked pickup (4) + victim identity + tell ───

/// ADR-016 (identity phase): pick the name the phantom (robapieles) impersonates. The victim is
/// the first REAL (non-phantom) connected peer; its name is cloned (the phantom keeps its OWN
/// unique id — the id mismatch is the intended subtle tell #1). Returns `(name, bound)`: `bound`
/// is true when a real victim was found, false → host-name fallback (solo), which
/// `rebind_unbound_victims` later upgrades to a real peer once one connects.
///
/// ADR-043 — `slot` spreads the victims across the real peers instead of every creature wearing
/// the same face. It used to be a bare `.find()` over `peers`, which was harmless with one phantom
/// and self-defeating with a populated world: two of them in sight at once, both called the same
/// thing, and the whole point of ADR-016's disguise evaporates. With more phantoms than players
/// names still repeat — unavoidable, and fine, because they are spread across the map.
///
/// The list comes from `real_peer_names` (id-ordered) rather than from iterating `peers` directly,
/// because `HashMap` order is arbitrary and would re-cast everyone from tick to tick.
fn choose_victim_name_for(net: &NetworkManager, slot: usize) -> (String, bool) {
    let names = net.real_peer_names();
    match names.is_empty() {
        true => (net.local_name.clone(), false),
        false => (names[slot % names.len()].clone(), true),
    }
}

/// ADR-016 — nearest REAL target (non-phantom) to `from` in XZ: the host's own local player
/// (`host_player_pos`/`host_player_rot`, keyed by `net.local_id`) plus every real peer. Returns
/// `(id, position, distance, yaw_deg)` of the closest. The `id` lets the caller look up the
/// target's derived speed (sound detection, slice 3b-P1) and the `yaw` lets it test whether the
/// player is looking back (STATUE). The host player is always a candidate → `Some` in practice.
/// ADR-040 perception: is this target crouching? Remote peers carry it in `PeerConnection.crouch`
/// (relayed since ADR-020, so no wire work was needed for stealth); the host's own player is not a
/// peer, so its value is handed into `step`.
fn target_is_crouched(net: &NetworkManager, tid: PeerId, host_crouch: bool) -> bool {
    if tid == net.local_id {
        return host_crouch;
    }
    net.peers.get(&tid).is_some_and(|p| p.crouch)
}

/// ADR-041 — displace a heard position by an error that grows with distance, DETERMINISTICALLY.
///
/// Deterministic and not per-tick random on purpose: a wandering estimate would make the phantom
/// zigzag toward a point that keeps moving, which looks like a bug rather than like uncertainty.
/// The same shot always resolves to the same spot for the same phantom.
fn blur_noise(source: Vec3, dist: f32, id: PeerId) -> Vec3 {
    let radius = dist * PHANTOM_NOISE_ERROR_FRAC;
    if radius <= 0.01 {
        return source;
    }
    let mut h = (source.x as i32 as u32).wrapping_mul(0x9E37_79B9)
        ^ (source.z as i32 as u32).wrapping_mul(0x85EB_CA6B)
        ^ (id as u32).wrapping_mul(0xC2B2_AE35);
    h ^= h >> 15;
    h = h.wrapping_mul(0x2545_F491);
    h ^= h >> 13;
    let angle = (h as f32 / u32::MAX as f32) * std::f32::consts::TAU;
    Vec3::new(
        source.x + angle.cos() * radius,
        source.y,
        source.z + angle.sin() * radius,
    )
}

fn nearest_real_target(
    net: &NetworkManager,
    host_player_pos: Vec3,
    host_player_rot: f32,
    from: Vec3,
) -> Option<(PeerId, Vec3, f32, f32)> {
    let mut best = Some((
        net.local_id,
        host_player_pos,
        from.distance_xz(host_player_pos),
        host_player_rot,
    ));
    for p in net.peers.values() {
        if net.phantom_ids.contains(&p.id) {
            continue;
        }
        let pos = Vec3::from_array(p.position);
        let d = from.distance_xz(pos);
        if best.is_none_or(|(_, _, bd, _)| d < bd) {
            best = Some((p.id, pos, d, p.rotation));
        }
    }
    best
}

/// ADR-016 slice 3b-P1 (STATUE): is the PHANTOM inside the player's forward HORIZONTAL cone —
/// i.e. is the player looking at it? `player_yaw` is degrees (Unity yaw, 0 = +Z). Pitch is not
/// available per-peer (and is discarded for the host), so this is the horizontal cone only:
/// looking up/down does not count. No geometry occlusion (consistent with D1=(a)).
fn player_is_looking_at(player_pos: Vec3, player_yaw: f32, phantom_pos: Vec3) -> bool {
    let dx = phantom_pos.x - player_pos.x;
    let dz = phantom_pos.z - player_pos.z;
    let len = (dx * dx + dz * dz).sqrt();
    if len < f32::EPSILON {
        return true; // on top of each other → counts as looked-at
    }
    let yaw = player_yaw.to_radians();
    // Player forward unit dir (Unity yaw): (sin, cos). dot with the unit to-phantom vector.
    let dot = (yaw.sin() * dx + yaw.cos() * dz) / len;
    dot >= PHANTOM_STATUE_LOOK_HALF_FOV.cos()
}

/// ADR-016 (fluidity): angularly ease `current` heading toward `target` (both yaw radians) by
/// factor `t` in [0,1], via normalize-lerp of the unit direction vectors (nlerp — naturally takes
/// the shorter arc, no angle-wrap special-casing). Returns the blended yaw. Smooths the phantom's
/// turn-to-face so it tracks the player without a 10 Hz snap.
fn lerp_heading(current: f32, target: f32, t: f32) -> f32 {
    let t = t.clamp(0.0, 1.0);
    // dir = (sin(yaw), cos(yaw)) — the Unity yaw convention used throughout the driver.
    let mx = current.sin() * (1.0 - t) + target.sin() * t;
    let mz = current.cos() * (1.0 - t) + target.cos() * t;
    let len = (mx * mx + mz * mz).sqrt();
    if len > 0.001 {
        mx.atan2(mz).rem_euclid(std::f32::consts::TAU)
    } else {
        current // ~diametrically opposite at t≈0.5 → keep current; next tick resolves it
    }
}

/// ADR-016 slice 2: is `target` inside the phantom's forward view cone (heading ± HALF_FOV)?
/// Distance is checked by the caller; this is angle ONLY, with no geometry occlusion (D1=(a)).
fn in_view_cone(heading: f32, from: Vec3, target: Vec3) -> bool {
    let tx = target.x - from.x;
    let tz = target.z - from.z;
    let len = (tx * tx + tz * tz).sqrt();
    if len < f32::EPSILON {
        return true; // target is on top of the phantom → counts as seen
    }
    // Heading unit dir: yaw 0 = +Z, so dir = (sin, _, cos). dot with the unit to-target vector.
    let dot = (heading.sin() * tx + heading.cos() * tz) / len;
    dot >= PHANTOM_DETECT_HALF_FOV.cos()
}

/// ADR-016 slice 3a — the robapieles' behavioral FSM. Drives how it relates to the nearest real
/// player. PEEK/SEARCH (corner-peeking, last-known-position hunting) arrive in slice 3b; until
/// then their would-be transitions fall back to `Wander`. PURELY BEHAVIORAL — no wire flag; the
/// state is observable only by watching, never by reading packets.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum PhantomState {
    /// Erratic patrol: walks its heading, pauses to "look at walls", fakes pickups, does the
    /// metronomic stare tell, and stays near spawn via the observation leash.
    Wander,
    /// Saw the player: freezes and stares straight at them for a randomized window, then STALK.
    Spotted,
    /// Shadows the player at a held distance; its patience runs out into a SPRINT.
    Stalk,
    /// Weeping-angel freeze (slice 3b-P1): the player is looking at it, so it goes dead still
    /// until they look away (→ STALK) or it tires of the game (→ SPRINT). Entered from STALK;
    /// never interrupts a committed SPRINT.
    Statue,
    /// Lunges straight at the player with a ramped speed; "attacks" (anim-only) at point blank.
    Sprint,
    /// ADR-040 Fase 4 — lost you, and goes to look where it last saw you instead of forgetting on
    /// the spot. This is the counterweight to the pathfinding: without it, a creature that always
    /// routes optimally toward your exact position is a homing missile. With it, hiding works, and
    /// the tension moves from "can it reach me" to "does it know where I went".
    Search,
}

/// ADR-038: the two states where the stolen skin stops holding — the phantom shows its real form.
/// SINGLE SOURCE OF TRUTH for the reveal: anyone adding a `PhantomState` has to decide here, and
/// the decision is covered by `phantom_reveals_only_in_sprint_and_statue`. Purely cosmetic — the
/// flag rides the pose relay and never gates damage, detection or collision.
fn phantom_reveals(state: PhantomState) -> bool {
    matches!(state, PhantomState::Sprint | PhantomState::Statue)
}

/// ADR-016 slice 1 (phantom damage) — what `PhantomDriver::step` produced this tick for the HOST
/// player. Returned to the game loop, which owns `player`/`stats`. DEUDA: host-only — a joiner's
/// health lives in its own backend (P2P multi-backend), so this never affects joiners
/// (cross-backend damage authority is Fase 7). The damage path is SEPARATE from the pickup
/// theater (ADR-016 invariant intact).
#[derive(Clone, Copy, PartialEq, Debug)]
enum PhantomAttack {
    // ADR-043: there is no `None` variant. "Nothing happened this tick" is an EMPTY list now that
    // `step` returns one entry per attacker. Keeping the variant would leave something nothing
    // constructs any more — a trap, because `attack == None` would silently never fire.
    /// Frontal point-blank hit: non-lethal `damage` to health; the phantom bounces back to STALK.
    Hit(f32),
    /// Point-blank from BEHIND: lethal (the loop applies 100 dmg → the existing death/respawn).
    Kill,
    /// A shove the CLIENT applies (dx, dz m/s via the motor's SetVelocity). The backend never
    /// mutates the player pose for this — it's client-authoritative and would be overwritten by
    /// the next input (ADR-009), so the backend only signals the direction/force.
    Knockback(f32, f32),
}

/// Host-only driver for phantom peers (the robapieles). Each phantom wanders: it steps along
/// its heading and, on a head-on block, turns to a new heading — so it bumps walls and turns
/// instead of clipping through them. Every step is resolved through `resolve_move_grid_gen` +
/// `GridGenChunkCache`, so the phantom collides against the SAME grid_gen world Unity renders
/// (not world::generator), generated on-demand even far from the host. Periodically the phantom
/// also FAKES a pickup (slice 4): it freezes and
/// flips its animation field to "pickup" for ~1s, then resumes. It does a behavioral TELL #2
/// (an unnatural, near-metronomic "stare"), and clones a real peer's NAME (victim identity;
/// keeps its own id). Slice 2: it also DETECTS the nearest real player (distance + forward cone,
/// no geometry LOS — D1=(a)) and CHASES them at CHASE_SPEED, dropping the wander/stare/pickup
/// theater until it loses them past LOSE_RADIUS.
///
/// SAFETY INVARIANT (ADR-016 slice 4): the faked pickup is PURE THEATER — it touches ONLY the
/// phantom's `animation` field (via `update_player_state`). It NEVER calls `process_stp_pickup`,
/// NEVER inserts into `pending_pickups`, NEVER touches `stp_items`, NEVER credits an inventory,
/// NEVER emits a `stp_pickup_granted`. The phantom has no effect on real game state beyond its
/// own presentation field. (Calling the real pickup path would delete/reserve a real world item.)
///
/// The `GridGenChunkCache` lives here (a host-only game-loop local), not in World/NetworkManager:
/// it is sim-only host state, the least invasive home. The phantom POSE itself lives in its
/// `PeerConnection` (the render source). (world::collision's `SimChunkCache` is unchanged but no
/// longer has a production consumer; it remains for tests / future world::generator entities.)
struct PhantomDriver {
    grid_cache: GridGenChunkCache,
    movers: Vec<PhantomMover>,
    /// Last-tick XZ position of each real target (host + peers), keyed by id. Used to derive each
    /// target's speed (sound detection, slice 3b-P1) — peers never send velocity/move_state, so a
    /// position delta is the only uniform "is it running?" signal. Rebuilt each tick from the
    /// current targets, so disconnected ids drop out automatically.
    prev_target_pos: HashMap<PeerId, Vec3>,
    /// ADR-040 — A* buffers, allocated ONCE and reused by every search, so pathfinding performs no
    /// heap work inside the tick. Lives on the driver (not per-mover) because only one search runs
    /// at a time.
    nav_scratch: crate::world::grid_gen::NavScratch,
    /// Reusable cell-path buffer handed to `find_path`, for the same reason.
    nav_cells: Vec<crate::world::grid_gen::CellCoord>,
    /// Cumulative A\* searches. Proves the replan policy holds the cost bound; ADR-043 promotes it
    /// out of `#[cfg(test)]` because the same number is what the host's step instrumentation logs,
    /// and a load test needs to see it in a real session, not only in a unit test.
    nav_replans: u64,
    /// ADR-043 — steps taken, the phase source for the replan stagger. Wrapping: it is only ever
    /// read modulo the stride.
    step_counter: u64,
    /// ADR-043 — WORST step duration (µs) since the last report, reset on every report.
    ///
    /// The peak and not a sample, because the thing worth knowing is whether a step ever blew the
    /// budget, and `MissedTickBehavior::Skip` guarantees that an overrun is invisible otherwise: it
    /// drops ticks silently, `dt` stays a hardcoded 0.1 s, and the only symptom is the AI running
    /// in slow motion — which in play-test looks like a design choice, not like a fault.
    step_peak_us: u64,
    /// ADR-043 — every attack produced this tick, one entry per mover that landed one. A single
    /// return value made the LAST attacker of the tick win and silently dropped the rest, which
    /// with a populated world is two creatures reaching you together and only one of them
    /// counting. Owned by the driver and cleared per step so the fan-out costs no allocation.
    attacks: Vec<PhantomAttack>,
    /// ADR-043 — seconds until the active population is reconciled against the draw again.
    population_sync_in: f32,
    /// ADR-043 — monotonic counter handing each new mover its `victim_slot`. Monotonic and not
    /// `movers.len()` on purpose: indices shift when a creature deactivates, and reusing one would
    /// silently re-cast a survivor as somebody else the next time a neighbour despawned.
    next_victim_slot: usize,
    /// ADR-043 — density multiplier applied to the draw (`PHANTOM_DENSITY_SCALE`). Held here so
    /// the pure draw stays a pure function of its arguments.
    density_scale: f32,
    /// ADR-043 — max simultaneously simulated phantoms (`PHANTOM_ACTIVE_CAP`, env-overridable).
    active_cap: usize,
    /// Size of the building roster the blocked-cell overlay was last built from.
    built_count: usize,
    /// Seconds until the overlay is rebuilt regardless of size. A place + a demolish in the same
    /// window leave the count unchanged, so size alone would miss it.
    built_resync_in: f32,
}

/// ADR-043 — identity of a drawn phantom: `(block, layer, index within the block)`.
///
/// The index is what lets a block hold more than one. Without it, `PHANTOM_DENSITY_SCALE` above 1.0
/// would be a no-op — measured on the deployed binary: scale 8 with a cap of 24 woke exactly ONE
/// creature, because the block, not the cap, was the limit.
type PhantomAnchor = ((i32, i32), u8, u8);

/// ADR-043 — a drawn phantom the population reconciler is considering waking up this scan.
struct PhantomCandidate {
    /// Distance from the nearest real player, in XZ. Sorted on, so the cap keeps the creatures
    /// somebody is most likely to actually meet.
    distance: f32,
    /// `(block, layer)` it was drawn from — becomes the mover's `anchor`.
    anchor: PhantomAnchor,
    /// Raw drawn position, before `spawn_phantom` snaps it to a walkable cell.
    position: [f32; 3],
}

/// Per-phantom state: which peer, its heading (yaw, radians), the faked-pickup gesture (slice 4),
/// the "stare" tell (tell phase), and the victim-name binding (identity phase).
struct PhantomMover {
    id: PeerId,
    /// ADR-043 — the `(block, layer)` this creature was drawn from, or `None` for one placed by
    /// hand (`DEBUG_SPAWN_PHANTOM`). The population reconciler uses it as the identity of a drawn
    /// phantom: it is what stops the same block spawning a second copy on the next scan, and
    /// `None` is what keeps a hand-placed one exempt from being reconciled away.
    ///
    /// Deliberately NOT the same thing as `spawn_pos`, which re-anchors as the creature travels
    /// (ADR-041). The anchor is where the world says it lives; `spawn_pos` is where it currently
    /// considers home.
    anchor: Option<PhantomAnchor>,
    /// Which real player this one impersonates, as an index into `NetworkManager::real_peer_names`.
    /// Assigned once at spawn and never reshuffled.
    ///
    /// Before ADR-043 the victim was resolved with a single `.find()` over `peers` and handed to
    /// EVERY mover, so a populated world was N avatars all wearing the same name — the disguise
    /// ADR-016 exists to protect, defeating itself the moment two were in sight.
    victim_slot: usize,
    heading: f32,
    /// Spawn position; the observation leash (PHANTOM_WANDER_RADIUS) re-aims the heading here
    /// when the phantom drifts too far, so it stays in view during play-test.
    spawn_pos: Vec3,
    /// `Some(t)` while faking a pickup: movement is paused and `animation` is held at
    /// "pickup" until `t`. `None` while walking. PURE presentation — never real pickup state.
    pickup_until: Option<Instant>,
    /// Earliest instant the next faked pickup may begin.
    next_pickup_at: Instant,
    /// `Some(t)` while doing the "stare" tell: frozen, unnaturally still ("idle") until `t`.
    /// `None` while walking. The tell is its near-metronomic regularity.
    stare_until: Option<Instant>,
    /// Earliest instant the next stare tell may begin.
    next_stare_at: Instant,
    /// `true` once the name was bound to a real victim (so it is not rebound). `false` = still
    /// on the host-name fallback; adopts a real peer's name when one connects.
    victim_bound: bool,
    /// ADR-016 slice 3a — current FSM state. Replaces the slice-2 `chasing` bool.
    state: PhantomState,
    /// Seconds spent in the current `state` (reset to 0 on every transition). Drives the SPOTTED
    /// stare length, the STALK patience, and the SPRINT speed ramp.
    state_timer: f32,
    /// Randomized SPOTTED stare length (s), drawn in [SPOTTED_MIN, SPOTTED_MAX] on entry.
    spotted_duration: f32,
    /// Last position the player was seen at while STALK/SPRINT — recorded now so slice 3b's
    /// SEARCH/PEEK need no further step() rework.
    last_known_player_pos: Option<Vec3>,
    /// WANDER organic pause: remaining time (s) it stands still "looking at a wall". `is_paused`
    /// gates it.
    wander_pause_timer: f32,
    is_paused: bool,
    /// Smoothed turn target (yaw radians): STALK/SPRINT/STATUE ease `heading` toward this instead
    /// of snapping each tick (fluidity), so it tracks the player without 10 Hz rotational jerk.
    heading_target: f32,
    /// ADR-040 — string-pulled route the phantom is currently walking, in world space. Empty means
    /// "no plan": the steering falls back to heading straight at the target, which is exactly the
    /// pre-ADR-040 behaviour, so a failed or stale plan degrades to the old code instead of freezing.
    nav_waypoints: Vec<Vec3>,
    /// Index of the waypoint being walked toward.
    nav_cursor: usize,
    /// Goal the current plan was built for. A plan is stale once the target has drifted away from
    /// it, which is what stops the phantom from running at where you WERE.
    nav_goal: Option<Vec3>,
    /// Seconds since the current plan was built.
    nav_age: f32,
    /// ADR-041 — how long this SEARCH may run. Normally `PHANTOM_SEARCH_MAX`; a noise
    /// investigation gets a longer window, because arriving after minutes of travel and shrugging
    /// immediately would waste the entire approach.
    search_patience: f32,
    /// Travel speed for the current SEARCH. A noise investigation walks; a normal search ambles.
    search_speed: f32,
    /// Seconds left before the noise being investigated goes cold. `None` = not a noise search.
    noise_expiry: Option<f32>,
}

impl PhantomDriver {
    fn new(world_seed: u64) -> Self {
        Self {
            // ADR-033: el fantasma colisiona contra la MISMA densidad por zona que
            // se renderiza. Es el consumidor que hereda el cambio sin divergencia
            // (a diferencia del jugador real, que sigue contra world::generator —
            // deuda aceptada y documentada en el ADR).
            grid_cache: GridGenChunkCache::with_rules(
                world_seed,
                crate::world::zone_density::rules_for,
            ),
            movers: Vec::new(),
            prev_target_pos: HashMap::new(),
            nav_scratch: crate::world::grid_gen::NavScratch::new(),
            nav_cells: Vec::new(),
            nav_replans: 0,
            step_counter: 0,
            step_peak_us: 0,
            attacks: Vec::new(),
            population_sync_in: 0.0, // reconcile on the very first entity tick
            next_victim_slot: 0,
            // ADR-043 — the load-test levers. Read ONCE at construction, not per tick: a value
            // that could change mid-session would make the world's population depend on when you
            // looked, and the draw's whole promise is that it does not.
            density_scale: env_tuning("PHANTOM_DENSITY_SCALE", 1.0f32).max(0.0),
            active_cap: env_tuning("PHANTOM_ACTIVE_CAP", PHANTOM_ACTIVE_CAP),
            built_count: usize::MAX, // forces a build on the first step
            built_resync_in: 0.0,
        }
    }

    /// Player-built pieces are NOT in the generator's output, so without this the phantom walks
    /// straight through a wall you built to protect yourself — the one failure that reads as the
    /// game being broken rather than as the creature being scary.
    ///
    /// Approximation, deliberately: each piece blocks the 2.5 m cell it stands in. The backend
    /// knows a piece's `def_id`, position and rotation but NOT its size — footprints live in
    /// Unity's building definitions — so anything exact would need a new wire field and an ADR.
    /// One cell is right for walls and floors, which is what people actually build to hide behind.
    ///
    /// Feeding the CACHE means navigation inherits it for free: the phantom plans around your wall
    /// instead of planning through it and then bumping.
    fn sync_built_cells(&mut self, net: &NetworkManager, dt: f32) {
        self.built_resync_in -= dt;
        if net.stp_buildings.len() == self.built_count && self.built_resync_in > 0.0 {
            return;
        }
        self.built_count = net.stp_buildings.len();
        self.built_resync_in = 2.0;
        let cells: std::collections::HashSet<(i32, i32)> = net
            .stp_buildings
            .iter()
            .map(|b| crate::world::grid_gen::cell_of(Vec3::from_array(b.position)))
            .collect();
        self.grid_cache.set_blocked_cells(cells);
    }

    /// ADR-043 — reconcile the SIMULATED population against the world's draw, once a second.
    ///
    /// The world holds infinitely many robapieles and simulates a handful: the ones a real player
    /// could plausibly meet. Everything else is a hash that has not been asked yet. Without this
    /// gate the host would pay full AI — pathfinding included — for creatures nobody can see, and
    /// the step budget goes with it.
    ///
    /// Deactivation is deliberately NOT the mirror image of activation:
    /// - the radii differ (hysteresis), so a player loitering at the boundary does not make an
    ///   avatar blink in and out on every client;
    /// - only a phantom in WANDER may be put away. One that is stalking, searching or charging has
    ///   left its anchor by definition, and deleting it would teleport it back there the moment it
    ///   re-activated — which reads as a bug, not as having lost it. Escaping already has its own
    ///   designed mechanic: SEARCH and its 12 s surrender (ADR-040).
    fn sync_population(&mut self, net: &mut NetworkManager, host_player_pos: Vec3, dt: f32) {
        self.population_sync_in -= dt;
        if self.population_sync_in > 0.0 {
            return;
        }
        self.population_sync_in = PHANTOM_POPULATION_SYNC_INTERVAL;

        // Every REAL player: the host's own local player (which is not a peer) plus real peers.
        // Collected up front so no borrow of `net` survives into the mutation below.
        let players: Vec<Vec3> = std::iter::once(host_player_pos)
            .chain(
                net.peers
                    .iter()
                    .filter(|(id, _)| !net.is_phantom(**id))
                    .map(|(_, p)| Vec3::from_array(p.position)),
            )
            .collect();

        // ── Put away the ones nobody is near any more ──
        let mut retired: Vec<PeerId> = Vec::new();
        for m in &self.movers {
            if m.anchor.is_none() || m.state != PhantomState::Wander {
                continue; // hand-placed, or busy with a player — see the doc comment
            }
            let Some(peer) = net.peers.get(&m.id) else {
                continue;
            };
            let here = Vec3::from_array(peer.position);
            let layer = world_pos_to_layer(here.y);
            let far = players.iter().all(|p| {
                world_pos_to_layer(p.y) != layer || p.distance_xz(here) > PHANTOM_DEACTIVATE_RADIUS
            });
            if far {
                retired.push(m.id);
            }
        }
        for id in &retired {
            net.despawn_phantom(*id);
        }
        self.movers.retain(|m| !retired.contains(&m.id));

        // ── Wake up the ones somebody walked near ──
        if self.movers.len() >= self.active_cap {
            return;
        }
        let taken: std::collections::HashSet<PhantomAnchor> =
            self.movers.iter().filter_map(|m| m.anchor).collect();
        let mut seen_blocks: std::collections::HashSet<((i32, i32), u8)> =
            std::collections::HashSet::new();
        let mut candidates: Vec<PhantomCandidate> = Vec::new();
        let mut drawn: Vec<[f32; 3]> = Vec::new();

        for p in &players {
            // ADR-043 D-ACTIVACIÓN: the player's OWN layer only. Distance in XZ alone would wake a
            // creature standing on layer 1 directly under your feet — paying AI for something that
            // can neither reach you nor be seen by you.
            let layer = world_pos_to_layer(p.y);
            let (bx0, bz0) = phantom_spawn::block_of(
                p.x - PHANTOM_ACTIVATE_RADIUS,
                p.z - PHANTOM_ACTIVATE_RADIUS,
            );
            let (bx1, bz1) = phantom_spawn::block_of(
                p.x + PHANTOM_ACTIVATE_RADIUS,
                p.z + PHANTOM_ACTIVATE_RADIUS,
            );
            for bx in bx0..=bx1 {
                for bz in bz0..=bz1 {
                    if !seen_blocks.insert(((bx, bz), layer)) {
                        continue; // already offered by another player this scan
                    }
                    phantom_spawn::draw_into(
                        net.world_seed,
                        (bx, bz),
                        layer,
                        self.density_scale,
                        &mut drawn,
                    );
                    for (index, pos) in drawn.iter().copied().enumerate() {
                        let key = ((bx, bz), layer, index as u8);
                        if taken.contains(&key) {
                            continue; // this one is already awake
                        }
                        // The block is a coarse filter; the radius is the real test, and it is
                        // measured against the drawn spot rather than the block, or a corner of a
                        // 200 m block would count as "near" from 280 m away.
                        let d = p.distance_xz(Vec3::from_array(pos));
                        if d <= PHANTOM_ACTIVATE_RADIUS {
                            candidates.push(PhantomCandidate {
                                distance: d,
                                anchor: key,
                                position: pos,
                            });
                        }
                    }
                }
            }
        }

        // Nearest first, so when the cap binds it keeps the creatures a player is most likely to
        // actually run into rather than whichever block the scan happened to reach first.
        candidates.sort_by(|a, b| a.distance.total_cmp(&b.distance));
        for cand in candidates {
            if self.movers.len() >= self.active_cap {
                break;
            }
            let (victim_name, victim_bound) = choose_victim_name_for(net, self.next_victim_slot);
            let id = net.spawn_phantom(&victim_name, cand.position);
            // The SNAPPED position is the anchor for the leash, not the raw draw: `spawn_phantom`
            // may have moved it up to 7.5 m to find a walkable cell, and leashing it to a spot
            // inside a wall would have it pressing against that wall forever.
            let spawn_pos = net
                .peers
                .get(&id)
                .map(|p| Vec3::from_array(p.position))
                .unwrap_or_else(|| Vec3::from_array(cand.position));
            self.add_anchored(
                id,
                PHANTOM_INITIAL_HEADING,
                spawn_pos,
                victim_bound,
                Some(cand.anchor),
            );
        }
    }

    fn add(&mut self, id: PeerId, heading: f32, spawn_pos: Vec3, victim_bound: bool) {
        self.add_anchored(id, heading, spawn_pos, victim_bound, None);
    }

    /// `add`, plus the `(block, layer)` the world drew this one from (ADR-043). The un-anchored
    /// `add` remains for the hand-placed debug phantom, which must stay exempt from the population
    /// reconciler — it was put somewhere on purpose and nothing should tidy it away.
    fn add_anchored(
        &mut self,
        id: PeerId,
        heading: f32,
        spawn_pos: Vec3,
        victim_bound: bool,
        anchor: Option<PhantomAnchor>,
    ) {
        let now = Instant::now();
        let victim_slot = self.next_victim_slot;
        self.next_victim_slot = self.next_victim_slot.wrapping_add(1);
        self.movers.push(PhantomMover {
            id,
            anchor,
            victim_slot,
            heading,
            spawn_pos,
            pickup_until: None,
            next_pickup_at: now + PHANTOM_PICKUP_INTERVAL,
            stare_until: None,
            next_stare_at: now + PHANTOM_STARE_INTERVAL,
            victim_bound,
            state: PhantomState::Wander,
            state_timer: 0.0,
            spotted_duration: 0.0,
            last_known_player_pos: None,
            wander_pause_timer: 0.0,
            is_paused: false,
            heading_target: heading,
            nav_waypoints: Vec::new(),
            nav_cursor: 0,
            nav_goal: None,
            nav_age: 0.0,
            search_patience: PHANTOM_SEARCH_MAX,
            search_speed: PHANTOM_SEARCH_SPEED,
            noise_expiry: None,
        });
    }

    /// ADR-041 — drain the noises reported this tick and turn the ones a phantom could hear into an
    /// investigation. Loudness IS the radius: the client decides how far its weapon carries.
    fn hear_noises(&mut self, net: &mut NetworkManager) {
        if net.pending_noises.is_empty() {
            return;
        }
        let noises = std::mem::take(&mut net.pending_noises);
        for (raw, loudness) in noises {
            let source = Vec3::from_array(raw);
            for i in 0..self.movers.len() {
                let id = self.movers[i].id;
                let from = match net.peers.get(&id) {
                    Some(p) => Vec3::from_array(p.position),
                    None => continue,
                };
                let dist = from.distance_xz(source);
                if dist > loudness {
                    continue;
                }
                // A committed lunge or a freeze is NOT interrupted by a noise somewhere else. It
                // already has a target in front of it; being distractible there would read as
                // stupidity, not as curiosity.
                if matches!(
                    self.movers[i].state,
                    PhantomState::Sprint | PhantomState::Statue
                ) {
                    continue;
                }
                let goal = blur_noise(source, dist, id);
                self.movers[i].last_known_player_pos = Some(goal);
                self.movers[i].state = PhantomState::Search;
                self.movers[i].state_timer = 0.0;
                self.movers[i].search_patience = PHANTOM_NOISE_SEARCH_PATIENCE;
                self.movers[i].search_speed = PHANTOM_NOISE_TRAVEL_SPEED;
                self.movers[i].noise_expiry = Some(PHANTOM_NOISE_EXPIRY);
                // The plan is deliberately NOT thrown away. A second shot is new information about
                // the same hunt, not a new hunt: the creature should keep walking and re-aim, and
                // the replan policy already rebuilds the route on its own when the goal drifts more
                // than PHANTOM_REPLAN_GOAL_DRIFT. Clearing here made every shot in a burst restart
                // the approach from scratch, which is what "it resets instead of tracking" looked
                // like in play-test.
                info!(
                    "MPTRACE step=PH_NOISE event=phantom_investigates phantom_id={} dist={:.0} goal=({:.1},{:.1}) error={:.1}",
                    id,
                    dist,
                    goal.x,
                    goal.z,
                    dist * PHANTOM_NOISE_ERROR_FRAC
                );
            }
        }
    }

    /// ADR-040 — the heading to walk this tick: the bearing to the next waypoint of a string-pulled
    /// route, replanned on a policy, falling back to the straight bearing when there is no plan.
    ///
    /// The fallback matters as much as the pathfinding: a failed search, a target standing in a
    /// cell grid_gen calls solid, or a goal outside the window all degrade to EXACTLY the
    /// pre-ADR-040 behaviour rather than freezing the creature. The worst case is the old code.
    ///
    /// At most ONE search per call, so the per-step cost stays the bounded thing it was measured to
    /// be. A plan is rebuilt when it is older than `PHANTOM_REPLAN_INTERVAL`, when the target has
    /// drifted `PHANTOM_REPLAN_GOAL_DRIFT` from the goal it was built for, or when it ran out.
    fn steer_heading(&mut self, i: usize, layer: u8, from: Vec3, target: Vec3, dt: f32) -> f32 {
        use crate::world::grid_gen::{cell_of, find_path, segment_is_clear, string_pull};

        let straight = |to: Vec3| {
            (to.x - from.x)
                .atan2(to.z - from.z)
                .rem_euclid(std::f32::consts::TAU)
        };

        self.movers[i].nav_age += dt;

        // LINE OF TRAVEL FIRST. A pathfinder must never come BETWEEN the creature and a player it
        // can already walk straight to. Two ways that bites, both seen in play-test: a player
        // pressed against a wall quantizes into a cell grid_gen calls solid, so the search returns
        // best effort and the route stops a cell short — the phantom parks at ~2 m and stares,
        // and the point-blank strike (dist < 1.5) never fires; and a route's last waypoint is a
        // cell CENTRE, which can sit up to half a cell diagonal away from where you actually are.
        // Checking the straight line is cheap and settles both: if it is clear, take it.
        if segment_is_clear(&mut self.grid_cache, layer, from, target) {
            self.movers[i].nav_waypoints.clear();
            self.movers[i].nav_cursor = 0;
            self.movers[i].nav_goal = None;
            return straight(target);
        }

        // ADR-043 — REPLAN STAGGER, the lever ADR-040 wrote down for the day there were several.
        // `PHANTOM_REPLAN_INTERVAL` is 0.6 s = 6 steps, so movers that woke on the same tick keep
        // their `nav_age` in phase and every one of them comes due on the SAME step: the cost is
        // not the average, it is N searches in one 100 ms slot. Offsetting the permission by the
        // mover index spreads them over `PHANTOM_REPLAN_STRIDE` steps, capping the burst at
        // ceil(N / stride). A mover denied its turn keeps its previous route, and one with no route
        // at all falls back to the straight bearing — i.e. exactly the pre-ADR-040 behaviour, for
        // at most 0.2 s.
        let may_replan = (self.step_counter + i as u64).is_multiple_of(PHANTOM_REPLAN_STRIDE);
        let stale = may_replan
            && (self.movers[i].nav_waypoints.is_empty()
                || self.movers[i].nav_cursor >= self.movers[i].nav_waypoints.len()
                || self.movers[i].nav_age >= PHANTOM_REPLAN_INTERVAL
                || self.movers[i]
                    .nav_goal
                    .is_none_or(|g| g.distance_xz(target) > PHANTOM_REPLAN_GOAL_DRIFT));

        if stale {
            // ADR-041 — LONG-RANGE TRAVEL. The A* window is ±24 cells (±60 m); a noise 500 m away
            // is far outside it, and widening the window would cost 160k cells per search instead
            // of 2.4k. Instead the search is aimed at a SUB-GOAL on the bearing, just inside the
            // window: the same machinery, the same cost, and the journey keeps real local obstacle
            // avoidance the whole way instead of degrading to a blind straight line.
            // Stay inside the window edge (2 cells of margin).
            let reach = (crate::world::grid_gen::NAV_WINDOW_CELLS - 2) as f32
                * crate::world::grid_gen::CELL_SIZE_M;
            let far = from.distance_xz(target);
            let nav_target = if far > reach {
                let dx = (target.x - from.x) / far;
                let dz = (target.z - from.z) / far;
                Vec3::new(from.x + dx * reach, from.y, from.z + dz * reach)
            } else {
                target
            };
            let start = cell_of(from);
            let goal = cell_of(nav_target);
            find_path(
                &mut self.grid_cache,
                layer,
                start,
                goal,
                &mut self.nav_scratch,
                &mut self.nav_cells,
            );
            let cells = std::mem::take(&mut self.nav_cells);
            string_pull(
                &mut self.grid_cache,
                layer,
                from.y,
                &cells,
                &mut self.movers[i].nav_waypoints,
            );
            self.nav_cells = cells; // give the buffer back; no per-search allocation
            self.movers[i].nav_cursor = 0;
            self.movers[i].nav_goal = Some(target);
            self.movers[i].nav_age = 0.0;
            self.nav_replans += 1;
        }

        // Consume waypoints already reached. Done AFTER a possible replan so a fresh plan whose
        // first waypoint is underfoot does not cost a wasted tick.
        while self.movers[i].nav_cursor < self.movers[i].nav_waypoints.len() {
            let wp = self.movers[i].nav_waypoints[self.movers[i].nav_cursor];
            if from.distance_xz(wp) <= PHANTOM_WAYPOINT_ARRIVE {
                self.movers[i].nav_cursor += 1;
            } else {
                break;
            }
        }

        match self.movers[i].nav_waypoints.get(self.movers[i].nav_cursor) {
            Some(wp) => straight(*wp),
            None => straight(target), // no plan (or plan exhausted): the old straight bearing
        }
    }

    /// ADR-016 (identity phase): once a real (non-phantom) peer is connected, any phantom still
    /// on its fallback name adopts that peer's NAME — cloning the victim's identity while keeping
    /// its OWN unique id (never the victim's id, which would collide the client's `_active[id]`).
    /// The rename rides the existing roster/PeerList + ADR-015 relay (no schema). One-shot per
    /// phantom; cheap no-op once all are bound or no real peer exists.
    /// ADR-043 — each unbound phantom takes the victim of ITS OWN `victim_slot`, not one name
    /// resolved once and stamped on all of them. That old shape was invisible with a single debug
    /// phantom and self-defeating with a populated world: several creatures in view, all wearing
    /// the same name, which is precisely the disguise giving itself away.
    fn rebind_unbound_victims(&mut self, net: &mut NetworkManager) {
        if self.movers.iter().all(|m| m.victim_bound) {
            return;
        }
        let names = net.real_peer_names();
        if names.is_empty() {
            return;
        }
        for m in self.movers.iter_mut().filter(|m| !m.victim_bound) {
            let victim_name = names[m.victim_slot % names.len()].clone();
            if let Some(peer) = net.peers.get_mut(&m.id) {
                peer.name = victim_name.clone();
            }
            m.victim_bound = true;
            info!(
                "MPTRACE step=PH5 event=phantom_victim_bound phantom_id={} victim_name={}",
                m.id, victim_name
            );
        }
    }

    /// Advance every phantom one step at `dt` (entity-tick delta). Reads the phantom's
    /// current pose from its PeerConnection, resolves a walk-step through sim-only collision,
    /// and writes the resolved pose (grounded Y from the resolver + facing) back. A fully
    /// blocked step re-orients the heading so the phantom never stalls at a wall.
    fn step(
        &mut self,
        net: &mut NetworkManager,
        dt: f32,
        host_player_pos: Vec3,
        host_player_rot: f32,
        // ADR-040 perception: the HOST player's crouch. Remote peers carry their own in
        // `PeerConnection.crouch` (ADR-020 already relays it), but the host's local player is not a
        // peer, so it has to be handed in. Passed explicitly rather than stashed on the driver so
        // the input to a tick stays visible in the signature.
        host_player_crouch: bool,
    ) -> &[PhantomAttack] {
        let now = Instant::now();
        self.step_counter = self.step_counter.wrapping_add(1);

        // ADR-041: stimuli first — a shot reported this tick redirects the phantom before the FSM
        // gets to act on stale intentions.
        self.hear_noises(net);
        // Your walls exist for the AI too — collision AND pathfinding, via the same cache.
        self.sync_built_cells(net, dt);

        // ADR-016 slice 3b-P1 — derive each real target's XZ speed from its last-tick position
        // (peers send no velocity/move_state, so this is the uniform "is it running?" signal for
        // sound detection). Teleport / chunk-displacement deltas are clamped out. Computed once
        // per tick; the map is rebuilt from the current targets so stale ids drop out.
        let mut cur_positions: Vec<(PeerId, Vec3)> = Vec::with_capacity(net.peers.len() + 1);
        cur_positions.push((net.local_id, host_player_pos));
        for p in net.peers.values() {
            if !net.phantom_ids.contains(&p.id) {
                cur_positions.push((p.id, Vec3::from_array(p.position)));
            }
        }
        let mut target_speeds: HashMap<PeerId, f32> = HashMap::with_capacity(cur_positions.len());
        for (tid, cur) in &cur_positions {
            let speed = match self.prev_target_pos.get(tid) {
                Some(prev) => {
                    let d = ((cur.x - prev.x).powi(2) + (cur.z - prev.z).powi(2)).sqrt() / dt;
                    if d > PHANTOM_SPEED_SANITY_MAX {
                        0.0 // teleport / chunk displacement — not a footstep
                    } else {
                        d
                    }
                }
                None => 0.0,
            };
            target_speeds.insert(*tid, speed);
        }
        self.prev_target_pos = cur_positions.into_iter().collect();

        // ADR-016 slice 1 / ADR-043 — every attack produced this tick, in mover order; the game
        // loop applies them to the host. A single slot used to mean the last attacker won, so two
        // creatures reaching you together counted as one — invisible with the one debug phantom,
        // wrong the moment the world is populated.
        self.attacks.clear();
        for i in 0..self.movers.len() {
            let id = self.movers[i].id;
            let from = match net.peers.get(&id) {
                Some(p) => Vec3::from_array(p.position),
                None => continue, // phantom no longer present
            };
            // The grid_gen layer to collide against, derived from the phantom's own Y (works on
            // every layer without a hardcoded layer).
            let current_layer = world_pos_to_layer(from.y);

            // Nearest REAL player (the host's own local player + any real peer). D1=(a): distance
            // + a forward view cone only, NO geometry line-of-sight (collision = grid_gen world).
            let target = nearest_real_target(net, host_player_pos, host_player_rot, from);
            self.movers[i].state_timer += dt;

            // ── Gesture freeze (ANY state): the faked-pickup imitation and the SPRINT "attack"
            // are PURE THEATER — only the `animation` field. While active, freeze in place holding
            // "pickup" (the trigger flank the proxy edge-detects, ADR-011). NOTHING real is
            // touched: no process_stp_pickup, no pending_pickups, no stp_items, no grant. ──
            if self.movers[i]
                .pickup_until
                .is_some_and(|until| now >= until)
            {
                self.movers[i].pickup_until = None;
            }
            if self.movers[i].pickup_until.is_some() {
                let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                if let Some(peer) = net.peers.get_mut(&id) {
                    peer.update_player_state(from.to_array(), yaw, "pickup".into());
                }
                continue;
            }

            // ADR-016 slice 3a — Stalker FSM. `state` is Copy, so the match holds no borrow.
            let state = self.movers[i].state;
            match state {
                // ── WANDER: erratic patrol + detection. Keeps the slice-4 fake-pickup imitation,
                // the metronomic stare tell, organic "look at a wall" pauses, and the play-test
                // observation leash. Detecting the player (radius + cone) → SPOTTED. ──
                PhantomState::Wander => {
                    // Detection: normally distance + forward cone. A RUNNING target (speed from
                    // delta) is HEARD — detected from farther (DETECT + SOUND_BONUS) AND from any
                    // direction (no cone). Sound-only detection reacts faster (a shorter stare).
                    let (detected, by_sound) = match target {
                        Some((tid, tpos, dist, _)) => {
                            let crouched = target_is_crouched(net, tid, host_player_crouch);
                            // SIGHT: the cone is unchanged (behind it is behind it), but a
                            // crouching target has to be much closer before it registers.
                            let sight_radius = if crouched {
                                PHANTOM_DETECT_RADIUS * PHANTOM_CROUCH_SIGHT_FACTOR
                            } else {
                                PHANTOM_DETECT_RADIUS
                            };
                            let normal = dist <= sight_radius
                                && in_view_cone(self.movers[i].heading, from, tpos);
                            // SOUND: three tiers, and crouching mutes them all. This is the channel
                            // that ignores the cone, so silencing it is what makes sneaking up
                            // BEHIND the creature actually work.
                            let speed = target_speeds.get(&tid).copied().unwrap_or(0.0);
                            let hear_radius = if crouched || speed < PHANTOM_WALK_NOISE_SPEED {
                                0.0
                            } else if speed > PHANTOM_RUN_SPEED_THRESHOLD {
                                PHANTOM_DETECT_RADIUS + PHANTOM_SOUND_BONUS
                            } else {
                                PHANTOM_WALK_HEAR_RADIUS
                            };
                            let sound = hear_radius > 0.0 && dist <= hear_radius;
                            (normal || sound, sound && !normal)
                        }
                        None => (false, false),
                    };
                    if detected {
                        self.movers[i].state = PhantomState::Spotted;
                        self.movers[i].state_timer = 0.0;
                        let (lo, hi) = if by_sound {
                            (PHANTOM_SPOTTED_SOUND_MIN, PHANTOM_SPOTTED_SOUND_MAX)
                        } else {
                            (PHANTOM_SPOTTED_MIN, PHANTOM_SPOTTED_MAX)
                        };
                        self.movers[i].spotted_duration = lo + rand::random::<f32>() * (hi - lo);
                        self.movers[i].is_paused = false;
                        info!(
                            "MPTRACE step=PH_SPOTTED event=phantom_spotted phantom_id={} dur={:.1} by_sound={}",
                            id, self.movers[i].spotted_duration, by_sound
                        );
                        continue;
                    }

                    // Slice 4: start a faked-pickup gesture when the cooldown elapsed. Stamp the
                    // "pickup" flank now and freeze; the top-of-loop gesture freeze holds the pose
                    // for the rest of the window. (Anim-only — ADR-016 invariant.)
                    if now >= self.movers[i].next_pickup_at {
                        self.movers[i].pickup_until = Some(now + PHANTOM_PICKUP_GESTURE);
                        self.movers[i].next_pickup_at = now + PHANTOM_PICKUP_INTERVAL;
                        info!(
                            "MPTRACE step=PH4 event=phantom_fake_pickup phantom_id={} note=animation_field_only_no_real_pickup",
                            id
                        );
                        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                        if let Some(peer) = net.peers.get_mut(&id) {
                            peer.update_player_state(from.to_array(), yaw, "pickup".into());
                        }
                        continue;
                    }

                    // Tell #2: metronomic unnatural stillness (the tell is its regularity).
                    if self.movers[i].stare_until.is_some_and(|until| now >= until) {
                        self.movers[i].stare_until = None;
                    }
                    if self.movers[i].stare_until.is_none() && now >= self.movers[i].next_stare_at {
                        self.movers[i].stare_until = Some(now + PHANTOM_STARE_DURATION);
                        self.movers[i].next_stare_at = now + PHANTOM_STARE_INTERVAL;
                        info!(
                            "MPTRACE step=PH6 event=phantom_tell_stare phantom_id={} note=behavioral_tell_unnatural_stillness",
                            id
                        );
                    }
                    if self.movers[i].stare_until.is_some() {
                        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                        if let Some(peer) = net.peers.get_mut(&id) {
                            peer.update_player_state(from.to_array(), yaw, "idle".into());
                        }
                        continue;
                    }

                    // Organic pause: stand still "looking at a wall". Count down if paused; else a
                    // small per-tick chance to start one. On resume, turn to a new heading.
                    if self.movers[i].is_paused {
                        self.movers[i].wander_pause_timer -= dt;
                        if self.movers[i].wander_pause_timer <= 0.0 {
                            self.movers[i].is_paused = false;
                            let turn = PHANTOM_TURN_MIN
                                + rand::random::<f32>() * (PHANTOM_TURN_MAX - PHANTOM_TURN_MIN);
                            self.movers[i].heading =
                                (self.movers[i].heading + turn).rem_euclid(std::f32::consts::TAU);
                        } else {
                            let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                            if let Some(peer) = net.peers.get_mut(&id) {
                                peer.update_player_state(from.to_array(), yaw, "idle".into());
                            }
                            continue;
                        }
                    } else if rand::random::<f32>() < PHANTOM_WANDER_PAUSE_CHANCE {
                        self.movers[i].is_paused = true;
                        self.movers[i].wander_pause_timer = PHANTOM_WANDER_PAUSE_MIN
                            + rand::random::<f32>()
                                * (PHANTOM_WANDER_PAUSE_MAX - PHANTOM_WANDER_PAUSE_MIN);
                        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                        if let Some(peer) = net.peers.get_mut(&id) {
                            peer.update_player_state(from.to_array(), yaw, "idle".into());
                        }
                        continue;
                    }

                    // Observation leash (WANDER-only, play-test crutch): re-aim toward spawn if it
                    // drifted past the radius so it stays in view. STALK/SPRINT ignore it.
                    if from.distance_xz(self.movers[i].spawn_pos) > PHANTOM_WANDER_RADIUS {
                        let dx = self.movers[i].spawn_pos.x - from.x;
                        let dz = self.movers[i].spawn_pos.z - from.z;
                        if dx * dx + dz * dz > f32::EPSILON {
                            self.movers[i].heading = dx.atan2(dz).rem_euclid(std::f32::consts::TAU);
                        }
                    }

                    // Walk the heading; a full block (neither axis advanced) re-orients so it
                    // never stalls at a wall. A slide keeps the heading and hugs the wall.
                    let heading = self.movers[i].heading;
                    let dir = Vec3::new(heading.sin(), 0.0, heading.cos());
                    let desired = Vec3::new(
                        from.x + dir.x * PHANTOM_WALK_SPEED * dt,
                        from.y,
                        from.z + dir.z * PHANTOM_WALK_SPEED * dt,
                    );
                    let resolved =
                        resolve_move_grid_gen(&mut self.grid_cache, current_layer, from, desired);
                    let blocked =
                        (resolved.x - from.x).abs() < 1e-4 && (resolved.z - from.z).abs() < 1e-4;
                    if blocked {
                        let turn = PHANTOM_TURN_MIN
                            + rand::random::<f32>() * (PHANTOM_TURN_MAX - PHANTOM_TURN_MIN);
                        self.movers[i].heading = (heading + turn).rem_euclid(std::f32::consts::TAU);
                    }
                    let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(resolved.to_array(), yaw, "idle".into());
                    }
                    if net.session_start.elapsed().as_millis() % 1000 < 120 {
                        info!(
                            "MPTRACE step=PH2 event=phantom_move phantom_id={} pos=({:.2},{:.2},{:.2}) yaw={:.1} blocked={} grid_chunks={}",
                            id, resolved.x, resolved.y, resolved.z, yaw, blocked, self.grid_cache.len()
                        );
                    }
                }

                // ── SPOTTED: frozen stare. Faces the player; exits to STALK once the stare window
                // elapses (deterministic), may unpredictably SPRINT mid-stare; loses the player
                // (past DETECT_RADIUS*1.5) → WANDER. ──
                PhantomState::Spotted => {
                    let still = match target {
                        Some((_, _, dist, _)) => dist <= PHANTOM_DETECT_RADIUS * 1.5,
                        None => false,
                    };
                    if !still {
                        self.movers[i].state = PhantomState::Wander;
                        self.movers[i].state_timer = 0.0;
                        continue;
                    }
                    let (_, tpos, _dist, _) = target.unwrap(); // still ⇒ Some
                    let heading = (tpos.x - from.x)
                        .atan2(tpos.z - from.z)
                        .rem_euclid(std::f32::consts::TAU);
                    self.movers[i].heading = heading;

                    // Stare done → STALK (checked before the random lunge so it's deterministic
                    // once the window passes).
                    if self.movers[i].state_timer >= self.movers[i].spotted_duration {
                        self.movers[i].state = PhantomState::Stalk;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_STALK event=phantom_stalk phantom_id={}",
                            id
                        );
                        continue;
                    }
                    // Unpredictable lunge mid-stare (scarier when imprevisible).
                    if rand::random::<f32>() < PHANTOM_SPRINT_RANDOM_CHANCE {
                        self.movers[i].state = PhantomState::Sprint;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_SPRINT event=phantom_sprint phantom_id={} note=from_spotted_random",
                            id
                        );
                        continue;
                    }
                    let yaw = heading.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(from.to_array(), yaw, "idle".into());
                    }
                }

                // ── STALK: shadow the player at a held gap. Patience (or an unpredictable roll) →
                // SPRINT; lost past LOSE_RADIUS → WANDER (slice 3b: SEARCH the last-known pos). ──
                PhantomState::Stalk => {
                    let dist_opt = target.map(|(_, _, d, _)| d);
                    if dist_opt.is_none_or(|d| d > PHANTOM_LOSE_RADIUS) {
                        // ADR-040 Fase 4: it does not forget on the spot any more — it goes to look
                        // where it last saw you. Only with no memory at all does it resume wandering.
                        self.movers[i].state = if self.movers[i].last_known_player_pos.is_some() {
                            PhantomState::Search
                        } else {
                            PhantomState::Wander
                        };
                        self.movers[i].state_timer = 0.0;
                        continue;
                    }
                    let (_, tpos, dist, tyaw) = target.unwrap();
                    self.movers[i].last_known_player_pos = Some(tpos);

                    // STATUE (weeping angel): the player is looking at it (horizontal cone) and is
                    // close → freeze. Entered only from STALK; a committed SPRINT is never frozen.
                    if dist < PHANTOM_STATUE_RANGE && player_is_looking_at(tpos, tyaw, from) {
                        self.movers[i].state = PhantomState::Statue;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_STATUE event=phantom_statue phantom_id={} dist={:.2}",
                            id, dist
                        );
                        continue;
                    }

                    if self.movers[i].state_timer > PHANTOM_STALK_PATIENCE
                        || rand::random::<f32>() < PHANTOM_SPRINT_RANDOM_CHANCE * 2.0
                    {
                        self.movers[i].state = PhantomState::Sprint;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_SPRINT event=phantom_sprint phantom_id={} dist={:.2}",
                            id, dist
                        );
                        continue;
                    }

                    // ADR-040: navigated heading. Where this used to point straight at the player —
                    // and grind into whatever wall was in between — it now follows a string-pulled
                    // route. With no plan it falls back to the straight bearing, i.e. the old code.
                    let to_player = self.steer_heading(i, current_layer, from, tpos, dt);
                    // Ease toward the player instead of snapping (a 10 Hz snap reads as lag);
                    // movement follows the smoothed heading → a curved, less robotic track.
                    self.movers[i].heading_target = to_player;
                    let t = (PHANTOM_TURN_SPEED_STALK * dt).min(1.0);
                    self.movers[i].heading =
                        lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
                    let heading = self.movers[i].heading;
                    // Maintain STALK_DISTANCE: close in if too far, ease back if too near (it
                    // backs away while still facing you — unsettling), else hold.
                    let (move_dir, speed) = if dist > PHANTOM_STALK_DISTANCE + 2.0 {
                        (heading, PHANTOM_WALK_SPEED)
                    } else if dist < PHANTOM_STALK_DISTANCE {
                        (
                            (heading + std::f32::consts::PI).rem_euclid(std::f32::consts::TAU),
                            PHANTOM_WALK_SPEED * 0.6,
                        )
                    } else {
                        (heading, 0.0)
                    };
                    let dir = Vec3::new(move_dir.sin(), 0.0, move_dir.cos());
                    let desired = Vec3::new(
                        from.x + dir.x * speed * dt,
                        from.y,
                        from.z + dir.z * speed * dt,
                    );
                    let resolved =
                        resolve_move_grid_gen(&mut self.grid_cache, current_layer, from, desired);
                    let yaw = heading.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(resolved.to_array(), yaw, "idle".into());
                    }
                    if net.session_start.elapsed().as_millis() % 1000 < 120 {
                        info!(
                            "MPTRACE step=PH_STALK event=phantom_stalk_move phantom_id={} pos=({:.2},{:.2},{:.2}) dist={:.2}",
                            id, resolved.x, resolved.y, resolved.z, dist
                        );
                    }
                }

                // ── STATUE: weeping-angel freeze. Dead still while the player keeps looking. They
                // look away → STALK (resumes the hunt, never back to WANDER); tires after
                // STATUE_MAX → SPRINT; loses the player past LOSE_RADIUS → WANDER. Never reached
                // mid-SPRINT (a committed lunge is not frozen). ──
                PhantomState::Statue => {
                    let lost = match target {
                        Some((_, _, dist, _)) => dist > PHANTOM_LOSE_RADIUS,
                        None => true,
                    };
                    if lost {
                        self.movers[i].state = PhantomState::Wander;
                        self.movers[i].state_timer = 0.0;
                        continue;
                    }
                    let (_, tpos, dist, tyaw) = target.unwrap();
                    self.movers[i].last_known_player_pos = Some(tpos);

                    // Tired of the game → lunge (checked before the look test so the timeout always
                    // wins once it elapses). If point-blank, also SHOVE the player — the client
                    // applies the impulse (SetVelocity); the backend only signals the direction.
                    if self.movers[i].state_timer >= PHANTOM_STATUE_MAX {
                        let dx = tpos.x - from.x;
                        let dz = tpos.z - from.z;
                        let len = (dx * dx + dz * dz).sqrt();
                        if dist < PHANTOM_KNOCKBACK_RANGE && len > 0.001 {
                            self.attacks.push(PhantomAttack::Knockback(
                                dx / len * PHANTOM_KNOCKBACK_FORCE,
                                dz / len * PHANTOM_KNOCKBACK_FORCE,
                            ));
                        }
                        self.movers[i].state = PhantomState::Sprint;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_SPRINT event=phantom_sprint phantom_id={} note=from_statue_timeout knockback={}",
                            id,
                            dist < PHANTOM_KNOCKBACK_RANGE
                        );
                        continue;
                    }
                    // Player looked away → resume stalking.
                    if !player_is_looking_at(tpos, tyaw, from) {
                        self.movers[i].state = PhantomState::Stalk;
                        self.movers[i].state_timer = 0.0;
                        info!(
                            "MPTRACE step=PH_STALK event=phantom_statue_release phantom_id={}",
                            id
                        );
                        continue;
                    }
                    // Frozen in place, but it SLOWLY turns its head toward you (creepier than a
                    // fixed facing): position held, only the heading eases toward the player.
                    let to_player = (tpos.x - from.x)
                        .atan2(tpos.z - from.z)
                        .rem_euclid(std::f32::consts::TAU);
                    self.movers[i].heading_target = to_player;
                    let t = (PHANTOM_TURN_SPEED_STATUE * dt).min(1.0);
                    self.movers[i].heading =
                        lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
                    let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(from.to_array(), yaw, "idle".into());
                    }
                }

                // ── SPRINT: ramp WALK→SPRINT straight at the player. Point-blank → anim-only
                // "attack" (ADR-016 invariant) then STALK; lost past LOSE_RADIUS*1.2 → WANDER
                // (slice 3b: SEARCH — it doesn't give up easily mid-lunge). ──
                PhantomState::Sprint => {
                    let dist_opt = target.map(|(_, _, d, _)| d);
                    if dist_opt.is_none_or(|d| d > PHANTOM_LOSE_RADIUS * 1.2) {
                        // ADR-040 Fase 4 — same as STALK: a lost lunge becomes a search, not
                        // amnesia. This is what makes breaking line of sight a tactic rather than
                        // an off switch.
                        self.movers[i].state = if self.movers[i].last_known_player_pos.is_some() {
                            PhantomState::Search
                        } else {
                            PhantomState::Wander
                        };
                        self.movers[i].state_timer = 0.0;
                        continue;
                    }
                    let (_, tpos, dist, tyaw) = target.unwrap();
                    self.movers[i].last_known_player_pos = Some(tpos);
                    // ADR-040: navigated heading (see STALK). The lunge routes around geometry
                    // instead of pinning itself to a wall between it and you.
                    let to_player = self.steer_heading(i, current_layer, from, tpos, dt);
                    // Aggressive turn smoothing (faster than STALK) — tracks hard but never snaps.
                    self.movers[i].heading_target = to_player;
                    let t = (PHANTOM_TURN_SPEED_SPRINT * dt).min(1.0);
                    self.movers[i].heading =
                        lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
                    let heading = self.movers[i].heading;

                    // Point-blank "attack". The pickup gesture is the VISUAL only (ADR-016
                    // invariant — the DAMAGE rides the separate PhantomAttack channel, never the
                    // pickup path). Front (player looking) = non-lethal hit; behind = kill.
                    if dist < 1.5 {
                        self.movers[i].pickup_until = Some(now + PHANTOM_PICKUP_GESTURE);
                        self.movers[i].state = PhantomState::Stalk; // bounce off after the strike
                        self.movers[i].state_timer = 0.0;
                        if player_is_looking_at(tpos, tyaw, from) {
                            self.attacks.push(PhantomAttack::Hit(PHANTOM_ATTACK_DAMAGE));
                            info!(
                                "MPTRACE step=PH_SPRINT event=phantom_hit phantom_id={} dmg={:.0}",
                                id, PHANTOM_ATTACK_DAMAGE
                            );
                        } else {
                            self.attacks.push(PhantomAttack::Kill);
                            info!(
                                "MPTRACE step=PH_SPRINT event=phantom_kill phantom_id={} note=from_behind",
                                id
                            );
                        }
                        let yaw = self.movers[i].heading.to_degrees().rem_euclid(360.0);
                        if let Some(peer) = net.peers.get_mut(&id) {
                            peer.update_player_state(from.to_array(), yaw, "pickup".into());
                        }
                        continue;
                    }

                    let ramp = (self.movers[i].state_timer / PHANTOM_SPRINT_RAMP).clamp(0.0, 1.0);
                    let speed =
                        PHANTOM_WALK_SPEED + (PHANTOM_SPRINT_SPEED - PHANTOM_WALK_SPEED) * ramp;
                    let dir = Vec3::new(heading.sin(), 0.0, heading.cos());
                    let desired = Vec3::new(
                        from.x + dir.x * speed * dt,
                        from.y,
                        from.z + dir.z * speed * dt,
                    );
                    let resolved =
                        resolve_move_grid_gen(&mut self.grid_cache, current_layer, from, desired);
                    let yaw = heading.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(resolved.to_array(), yaw, "idle".into());
                    }
                    if net.session_start.elapsed().as_millis() % 1000 < 120 {
                        info!(
                            "MPTRACE step=PH_SPRINT event=phantom_sprint_move phantom_id={} pos=({:.2},{:.2},{:.2}) speed={:.1} dist={:.2}",
                            id, resolved.x, resolved.y, resolved.z, speed, dist
                        );
                    }
                }

                // ── SEARCH (ADR-040 Fase 4): it lost you and walks, navigating, to the last place
                // it saw you. Slower than a walk — it is looking, not commuting. Re-acquiring you
                // resumes the hunt; running out of patience returns it to WANDER and it FORGETS,
                // which is what makes hiding a real escape and not just a delay. ──
                PhantomState::Search => {
                    // Re-acquire on sight (crouching still shrinks the radius — stealth applies
                    // while it hunts, not only while it patrols).
                    if let Some((tid, tpos, dist, _)) = target {
                        let crouched = target_is_crouched(net, tid, host_player_crouch);
                        let sight = if crouched {
                            PHANTOM_DETECT_RADIUS * PHANTOM_CROUCH_SIGHT_FACTOR
                        } else {
                            PHANTOM_DETECT_RADIUS
                        };
                        if dist <= sight && in_view_cone(self.movers[i].heading, from, tpos) {
                            self.movers[i].last_known_player_pos = Some(tpos);
                            self.movers[i].state = PhantomState::Stalk;
                            self.movers[i].state_timer = 0.0;
                            info!(
                                "MPTRACE step=PH_SEARCH event=phantom_reacquired phantom_id={} dist={:.2}",
                                id, dist
                            );
                            continue;
                        }
                    }

                    let goal = match self.movers[i].last_known_player_pos {
                        Some(g) => g,
                        None => {
                            self.movers[i].state = PhantomState::Wander;
                            self.movers[i].state_timer = 0.0;
                            continue;
                        }
                    };

                    // ADR-041: a noise in transit goes cold on its own clock, independent of the
                    // arrival patience — otherwise a phantom that never reaches the spot would
                    // walk toward a five-minute-old shot forever.
                    let noise_cold = match self.movers[i].noise_expiry.as_mut() {
                        Some(left) => {
                            *left -= dt;
                            *left <= 0.0
                        }
                        None => false,
                    };

                    // Swept the spot, out of patience, or the trail went cold → give up and forget.
                    if noise_cold
                        || self.movers[i].state_timer > self.movers[i].search_patience
                        || from.distance_xz(goal) <= PHANTOM_SEARCH_ARRIVE
                    {
                        info!(
                            "MPTRACE step=PH_SEARCH event=phantom_gives_up phantom_id={} searched_for={:.1}s cold={}",
                            id, self.movers[i].state_timer, noise_cold
                        );
                        self.movers[i].last_known_player_pos = None;
                        self.movers[i].state = PhantomState::Wander;
                        self.movers[i].state_timer = 0.0;
                        self.movers[i].search_patience = PHANTOM_SEARCH_MAX;
                        self.movers[i].search_speed = PHANTOM_SEARCH_SPEED;
                        self.movers[i].noise_expiry = None;
                        // ADR-041: RE-ANCHOR the observation leash. It only acts in WANDER, so it
                        // never fought the journey — but without this the phantom would finish a
                        // 500 m trip and immediately start walking all the way back to its spawn.
                        self.movers[i].spawn_pos = from;
                        continue;
                    }

                    let heading = self.steer_heading(i, current_layer, from, goal, dt);
                    self.movers[i].heading_target = heading;
                    let t = (PHANTOM_TURN_SPEED_STALK * dt).min(1.0);
                    self.movers[i].heading =
                        lerp_heading(self.movers[i].heading, self.movers[i].heading_target, t);
                    let h = self.movers[i].heading;
                    let dir = Vec3::new(h.sin(), 0.0, h.cos());
                    let speed = self.movers[i].search_speed;
                    let desired = Vec3::new(
                        from.x + dir.x * speed * dt,
                        from.y,
                        from.z + dir.z * speed * dt,
                    );
                    let moved = resolve_move_grid_gen_ex(
                        &mut self.grid_cache,
                        current_layer,
                        from,
                        desired,
                    );
                    if moved.blocked {
                        // Wedged against geometry: drop the plan so the next tick re-routes instead
                        // of pressing into the same wall (the defect STALK/SPRINT used to have).
                        self.movers[i].nav_waypoints.clear();
                    }
                    let yaw = h.to_degrees().rem_euclid(360.0);
                    if let Some(peer) = net.peers.get_mut(&id) {
                        peer.update_player_state(moved.pos.to_array(), yaw, "idle".into());
                    }
                }
            }
        }

        // ADR-038 — seal the cosmetic real-form flag from the POST-tick state, in ONE place. The
        // FSM above has several early `continue`s (lost target, point-blank strike, gesture
        // freeze), so a per-branch seal would silently miss paths and leave a stale disguise on
        // exactly the frames that matter. Derived level, not a latch: the flag falls back to false
        // on its own when the phantom returns to WANDER/STALK, so the disguise recomposes without
        // any reset logic. Written HERE and not in `update_player_state` on purpose — that method
        // stays untouched so the other five pose fields keep inheriting their defaults for the
        // phantom (`.claude/rules/pose-relay-wire-rust.md`, step 6).
        for m in &self.movers {
            if let Some(peer) = net.peers.get_mut(&m.id) {
                peer.revealed = phantom_reveals(m.state);
            }
        }

        // ADR-043 — the measurement the whole populated world rests on. Before this, ADR-040's
        // "2 ms per step" budget, ADR-041's 23.5 µs per chunk and the cost of an A* search were all
        // documented numbers with NO code behind them: no constant, no assert, no benchmark in the
        // tree. "How many creatures does the world hold" was therefore unanswerable except by
        // opinion. These five counters make it a reading.
        //
        // `step_us` is the PEAK since the last report, not this step: the throttle samples ~1 step
        // in 10, so an instantaneous value would miss precisely the spike worth seeing.
        self.step_peak_us = self.step_peak_us.max(now.elapsed().as_micros() as u64);
        if net.session_start.elapsed().as_millis() % 1000 < 120 {
            info!(
                "MPTRACE step=PH_BUDGET event=phantom_step_budget movers={} step_peak_us={} searches={} chunk_regens={} cached_chunks={} body_valve={}",
                self.movers.len(),
                self.step_peak_us,
                self.nav_replans,
                self.grid_cache.generated_count(),
                self.grid_cache.len(),
                self.grid_cache.degraded_body_check_count()
            );
            self.step_peak_us = 0;
        }

        &self.attacks
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Sin re-siembra, tras cargar una partida los cuatro asignadores arrancan en su base y el
    /// primer `place` reacuña un id que YA existe en el roster. Como `process_stp_demolish`
    /// resuelve por `position(|b| b.id == …)`, demoler la pieza nueva borra la VIEJA.
    ///
    /// Se asserta sobre el valor DEVUELTO y no leyendo los `AtomicU32` después: son estáticos de
    /// proceso y los tests corren en hilos del MISMO proceso, así que leerlos seria una carrera.
    /// El valor devuelto es funcion pura del roster.
    #[tokio::test]
    async fn id_allocators_reseed_inside_their_own_range() {
        use crate::network::protocol::{StpBuildingInfo, StpCarryableInfo, StpItemInfo};
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();

        net.stp_items.push(StpItemInfo {
            id: STP_DROP_ID_BASE + 7,
            def_id: 1,
            count: 1,
            position: [0.0; 3],
            rotation: 0.0,
        });
        net.stp_buildings.push(StpBuildingInfo {
            id: STP_BUILDING_ID_BASE + 3,
            def_id: 1,
            position: [0.0; 3],
            rotation: 0.0,
            group_id: 9,
            added: vec![],
        });
        net.stp_carryables.push(StpCarryableInfo {
            id: STP_CARRYABLE_ID_BASE + 11,
            def_id: 1,
            position: [0.0; 3],
            rotation: 0.0,
        });

        let (drop_id, building_id, carryable_id, group_id) = reseed_stp_id_allocators(&net);

        assert_eq!(drop_id, STP_DROP_ID_BASE + 8);
        assert_eq!(building_id, STP_BUILDING_ID_BASE + 4);
        assert_eq!(carryable_id, STP_CARRYABLE_ID_BASE + 12);
        assert_eq!(group_id, 10);
    }

    /// Los 16 cofres de mundo se re-sembraban en CADA arranque: StpChestSpawner acuña sus
    /// request_id como `RequestIdBase + contador de instancia`, secuencia identica en cada
    /// lanzamiento, contra un `processed_interactions` que nace vacio con cada `run()`. El dedup
    /// por request_id no puede ver un reinicio; el dedup por posicion contra los cofres cargados,
    /// si.
    #[test]
    fn world_chest_is_not_reseeded_over_one_already_loaded() {
        let mut world = World::new(42);
        let mut processed: HashSet<(u16, u64)> = HashSet::new();
        let spot = Vec3::new(10.0, 0.0, 20.0);
        let loot = vec![crate::world::corpse::CorpseStack {
            item_id: 1,
            quantity: 3,
        }];

        // Arranque 1: se siembra.
        let first = handle_spawn_world_chest(
            &mut world,
            true,
            1,
            5000,
            spot,
            loot.clone(),
            &mut processed,
        );
        assert!(first.is_ok(), "la primera siembra debe entrar: {first:?}");

        // Arranque 2: MISMO request_id (el contador reinicia) y dedup vacio, como en un
        // relanzamiento real del backend.
        let mut fresh_dedupe: HashSet<(u16, u64)> = HashSet::new();
        let second = handle_spawn_world_chest(
            &mut world,
            true,
            1,
            5000,
            spot,
            loot.clone(),
            &mut fresh_dedupe,
        );
        assert_eq!(second, Err("chest_already_seeded"));
        assert_eq!(
            world.corpses.values().filter(|c| c.is_chest).count(),
            1,
            "un reinicio no puede duplicar los cofres del mundo"
        );

        // Y un cofre en OTRO sitio sigue entrando: el dedup es por posicion, no un cierre global.
        let elsewhere = handle_spawn_world_chest(
            &mut world,
            true,
            1,
            5001,
            Vec3::new(200.0, 0.0, 200.0),
            loot,
            &mut fresh_dedupe,
        );
        assert!(elsewhere.is_ok(), "otro cofre lejos debe poder sembrarse");
    }

    /// `occupied_stp_cells` es estado DERIVADO y no se persiste. Si no se reconstruye al cargar,
    /// el dedup de celda por pose arranca vacio y la primera colocacion sobre el socket de una
    /// pieza YA GUARDADA se acepta — duplicando la construccion en ese punto.
    #[tokio::test]
    async fn hydrate_rederives_the_occupied_cell_set_for_group_pieces_only() {
        use crate::network::protocol::StpBuildingInfo;
        use crate::persistence::save::SaveFile;

        let mut world = World::new(42);
        let mut player = Player::new(1, String::from("Host"));
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();

        let grouped = StpBuildingInfo {
            id: STP_BUILDING_ID_BASE,
            def_id: 1,
            position: [4.0, 0.0, 8.0],
            rotation: 90.0,
            group_id: 3, // pieza de grupo -> ocupa celda
            added: vec![],
        };
        let free = StpBuildingInfo {
            id: STP_BUILDING_ID_BASE + 1,
            def_id: 2,
            position: [40.0, 0.0, 80.0],
            rotation: 0.0,
            group_id: 0, // pieza suelta -> las sueltas pueden apilarse, no ocupan celda
            added: vec![],
        };
        let expected_cell = stp_pose_cell(grouped.position, grouped.rotation);
        let free_cell = stp_pose_cell(free.position, free.rotation);

        let mut save = SaveFile::new(String::from("test"), 42u64);
        save.stp_buildings = vec![grouped, free];
        hydrate_from_save(&mut world, &mut player, &mut net, save);

        assert!(
            net.occupied_stp_cells.contains(&expected_cell),
            "la celda de una pieza de grupo guardada debe quedar ocupada tras cargar"
        );
        assert!(
            !net.occupied_stp_cells.contains(&free_cell),
            "una pieza suelta no ocupa celda: apilarlas es legitimo"
        );
        assert_eq!(net.occupied_stp_cells.len(), 1);
    }

    /// El matiz que hace que la receta ingenua `max(roster) + 1` sea INCORRECTA: los rangos estan
    /// particionados, asi que el asignador de drops no puede mirar los ids de construcciones —
    /// sembraria dentro del rango ajeno y garantizaria la colision en vez de evitarla.
    #[tokio::test]
    async fn drop_allocator_ignores_ids_from_the_building_range() {
        use crate::network::protocol::StpItemInfo;
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        // Un id fuera de rango en la lista de items (p.ej. un roster corrupto o migrado).
        net.stp_items.push(StpItemInfo {
            id: STP_BUILDING_ID_BASE + 500,
            def_id: 1,
            count: 1,
            position: [0.0; 3],
            rotation: 0.0,
        });

        let (drop_id, ..) = reseed_stp_id_allocators(&net);

        assert_eq!(
            drop_id, STP_DROP_ID_BASE,
            "un id ajeno al rango no puede arrastrar al asignador de drops fuera del suyo"
        );
    }

    // ADR-038: the reveal is derived from the FSM state, so THIS is the decision worth freezing —
    // which states break the disguise. A future state added to PhantomState without a verdict here
    // (or a careless edit to the matches!) fails this test instead of silently unmasking the
    // robapieles while it stalks, which would kill the whole premise of ADR-016.
    #[test]
    fn phantom_reveals_only_in_sprint_and_statue() {
        assert!(phantom_reveals(PhantomState::Sprint));
        assert!(phantom_reveals(PhantomState::Statue));
        assert!(!phantom_reveals(PhantomState::Wander));
        assert!(!phantom_reveals(PhantomState::Spotted));
        assert!(!phantom_reveals(PhantomState::Stalk));
        // SEARCH does NOT reveal: it has lost you, so it puts the skin back on and goes looking.
        // A revealed creature wandering around searching would give away its own game.
        assert!(!phantom_reveals(PhantomState::Search));
    }

    #[test]
    fn sanitize_reported_damage_rejects_garbage_and_clamps() {
        // ADR-025 Slice B: a malformed client report must never poison authoritative health.
        assert_eq!(sanitize_reported_damage(None), 0.0);
        assert_eq!(sanitize_reported_damage(Some(f64::NAN)), 0.0);
        assert_eq!(sanitize_reported_damage(Some(f64::INFINITY)), 0.0);
        assert_eq!(sanitize_reported_damage(Some(-25.0)), 0.0);
        assert_eq!(sanitize_reported_damage(Some(35.5)), 35.5);
        assert_eq!(sanitize_reported_damage(Some(9999.0)), 100.0);
    }

    // ADR-028 Fase E: THE dedupe-under-retransmission test (explicitly required — the reliable
    // channel has a known open infinite-retransmit bug, STATE.md, so the same request WILL
    // arrive multiple times in production, not just in theory).
    #[test]
    fn corpse_spawn_request_dedupes_under_retransmit() {
        let mut world = World::new(42);
        let mut processed: HashSet<(u16, u64)> = HashSet::new();
        let items = vec![crate::world::corpse::CorpseStack {
            item_id: -12345,
            quantity: 3,
        }];

        let spawn_data = |items: Vec<crate::world::corpse::CorpseStack>| CorpseSpawnData {
            owner_name: "Joel".into(),
            position: [-22.0, 1.8, 9.0],
            equipment: [0, -1, -2, -3],
            held_item: -99,
            items,
        };

        let first = apply_corpse_spawn_request(
            &mut world,
            &mut processed,
            1004,
            7,
            spawn_data(items.clone()),
        );
        assert!(first.is_some(), "first request must spawn");
        assert_eq!(world.corpses.len(), 1);

        // Reliable retransmit: same (requester, request_id) → EXACTLY one corpse, no duplicate.
        for _ in 0..3 {
            let dup = apply_corpse_spawn_request(
                &mut world,
                &mut processed,
                1004,
                7,
                spawn_data(items.clone()),
            );
            assert!(dup.is_none(), "retransmit must be deduped");
        }
        assert_eq!(
            world.corpses.len(),
            1,
            "retransmits must never duplicate the corpse"
        );

        // A DIFFERENT request id from the same peer is a new death → spawns.
        let second =
            apply_corpse_spawn_request(&mut world, &mut processed, 1004, 8, spawn_data(items));
        assert!(second.is_some());
        assert_eq!(world.corpses.len(), 2);
        assert_ne!(
            first.unwrap(),
            second.unwrap(),
            "host-assigned ids stay unique"
        );
    }

    #[test]
    fn corpse_take_request_dedupes_validates_and_reports_verdict() {
        let mut world = World::new(42);
        let mut processed: HashSet<(u16, u64)> = HashSet::new();
        let pos = [10.0f32, 1.8, 20.0];
        let corpse_id = world.spawn_corpse(
            1004,
            "Joel".into(),
            Vec3::from_array(pos),
            [0; 4],
            0,
            vec![crate::world::corpse::CorpseStack {
                item_id: -55,
                quantity: 2,
            }],
        );

        let take_data = |corpse_id: u32, quantity: u16, requester_pos: [f32; 3]| CorpseTakeData {
            corpse_id,
            item_index: 0,
            quantity,
            requester_pos,
        };

        // Accepted take, then retransmit of the SAME request → deduped (no double removal).
        let verdict = apply_corpse_take_request(
            &mut world,
            &mut processed,
            1004,
            21,
            take_data(corpse_id, 1, pos),
        );
        match verdict {
            Some(PacketPayload::CorpseTakeResult {
                accepted,
                item_id,
                quantity,
                corpse_empty,
                ..
            }) => {
                assert!(accepted);
                assert_eq!(item_id, -55);
                assert_eq!(quantity, 1);
                assert!(!corpse_empty);
            }
            other => panic!("expected verdict, got {other:?}"),
        }
        let dup = apply_corpse_take_request(
            &mut world,
            &mut processed,
            1004,
            21,
            take_data(corpse_id, 1, pos),
        );
        assert!(dup.is_none(), "retransmitted take must be deduped");
        assert_eq!(
            world.corpses[&corpse_id].items[0].quantity, 1,
            "retransmit must not remove a second unit"
        );

        // Rejected take (too far) still produces a verdict so the requester can roll back.
        let far = [9999.0f32, 0.0, 9999.0];
        let rejected = apply_corpse_take_request(
            &mut world,
            &mut processed,
            1004,
            22,
            take_data(corpse_id, 1, far),
        );
        match rejected {
            Some(PacketPayload::CorpseTakeResult {
                accepted,
                ref reason,
                ..
            }) => {
                assert!(!accepted);
                assert!(reason.starts_with("too_far"), "reason was: {reason}");
            }
            other => panic!("expected verdict, got {other:?}"),
        }

        // Depleting take reports corpse_empty=true and removes the entry.
        let deplete = apply_corpse_take_request(
            &mut world,
            &mut processed,
            1004,
            23,
            take_data(corpse_id, 9, pos),
        );
        match deplete {
            Some(PacketPayload::CorpseTakeResult {
                accepted,
                quantity,
                corpse_empty,
                ..
            }) => {
                assert!(accepted);
                assert_eq!(quantity, 1);
                assert!(corpse_empty);
            }
            other => panic!("expected verdict, got {other:?}"),
        }
        assert!(world.corpses.is_empty());
    }

    #[test]
    fn parse_death_loot_reads_negative_ids_and_degrades_malformed_to_empty() {
        // ADR-028: raw STP DataIdReference ids may be negative — they must parse.
        let data = serde_json::json!({
            "equipment": [101, 0, -303, 404],
            "held_item": -12345,
            "items": [
                { "item_id": -12345, "quantity": 3 },
                { "item_id": 99, "quantity": 1 },
                { "item_id": 7 },                       // missing quantity → skipped
                { "quantity": 5 },                      // missing item_id → skipped
                { "item_id": 5, "quantity": 700000 },   // clamps to u16::MAX
            ],
        });
        let (equipment, held_item, items) = parse_death_loot(&data);
        assert_eq!(equipment, [101, 0, -303, 404]);
        assert_eq!(held_item, -12345);
        assert_eq!(items.len(), 3);
        assert_eq!(items[0].item_id, -12345);
        assert_eq!(items[0].quantity, 3);
        assert_eq!(items[2].quantity, u16::MAX);

        // Malformed/missing payload degrades to naked-and-empty, never an error.
        let (equipment, held_item, items) = parse_death_loot(&serde_json::json!({}));
        assert_eq!(equipment, [0; 4]);
        assert_eq!(held_item, 0);
        assert!(items.is_empty());

        // Short equipment array fills what it has; extra entries beyond 4 are ignored.
        let (equipment, _, _) = parse_death_loot(&serde_json::json!({ "equipment": [1, 2] }));
        assert_eq!(equipment, [1, 2, 0, 0]);
    }

    // ADR-032 amendment: a valid report_inventory mirrors the client's real STP inventory into
    // player.stp_inventory, with the shared corpse hygiene applied (quantity<=0 dropped,
    // truncated to MAX_CORPSE_STACKS — the FIRST 64 valid stacks survive, the rest discarded).
    #[tokio::test]
    async fn report_inventory_updates_player_stp_inventory_with_hygiene() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut world = World::new(42);
        let mut player = Player::new(1, "Host");
        let (tx, _rx) = broadcast::channel(16);
        let mut processed: HashSet<(u16, u64)> = HashSet::new();

        // 1 zero-quantity (dropped) + 70 valid (truncated to 64, first-come order).
        let mut items = vec![serde_json::json!({ "item_id": -999, "quantity": 0 })];
        for i in 0..70 {
            items.push(serde_json::json!({ "item_id": 1000 + i, "quantity": 2 }));
        }
        let action = crate::ipc::PlayerAction {
            action_type: "report_inventory".into(),
            data: serde_json::json!({ "items": items }),
        };
        handle_action(
            &action,
            &mut player,
            &mut world,
            &mut net,
            &tx,
            &mut processed,
            0,
        )
        .await;

        assert_eq!(
            player.stp_inventory.len(),
            crate::world::corpse::MAX_CORPSE_STACKS
        );
        assert!(player.stp_inventory.iter().all(|s| s.quantity > 0));
        // First valid stack survives; the zero-quantity one never entered.
        assert_eq!(player.stp_inventory[0].item_id, 1000);
        // Truncation keeps the first 64 valid stacks: 1000..1063 — 1064+ discarded.
        assert_eq!(player.stp_inventory.last().unwrap().item_id, 1063);

        // A follow-up report REPLACES the snapshot (latest wins), never appends.
        let action = crate::ipc::PlayerAction {
            action_type: "report_inventory".into(),
            data: serde_json::json!({ "items": [{ "item_id": 42, "quantity": 3 }] }),
        };
        handle_action(
            &action,
            &mut player,
            &mut world,
            &mut net,
            &tx,
            &mut processed,
            0,
        )
        .await;
        assert_eq!(player.stp_inventory.len(), 1);
        assert_eq!(player.stp_inventory[0].item_id, 42);
        assert_eq!(player.stp_inventory[0].quantity, 3);
    }

    /// ADR-016: the phantom is a PEER, so its relayed Y must use the same player-pivot convention
    /// every real peer uses (`floor + PLAYER_BASE_Y`). The client subtracts `PlayerBaseY` from EVERY
    /// remote pose to place a feet-pivoted avatar, and it cannot special-case the phantom (it must
    /// not know). Pinning the phantom to the bare floor sank it 1.8 m — visible from the waist up,
    /// found in the 2026-08-01 play-test. This freezes the convention on the spawn path.
    #[tokio::test]
    async fn phantom_spawns_at_the_player_pivot_height() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let pid = net.spawn_phantom("Robapieles_Test", [25.0, 1.8, 25.0]);

        let y = net.peers[&pid].position[1];
        let expected =
            crate::world::grid_gen::grid_floor_y(0) + crate::world::collision::PLAYER_BASE_Y;
        assert!(
            (y - expected).abs() < 1e-4,
            "phantom spawn Y was {y}, must be floor+PLAYER_BASE_Y = {expected}"
        );
        // Raising the pose must NOT change which grid_gen layer it collides against (ADR-018).
        assert_eq!(crate::world::grid_gen::world_pos_to_layer(y), 0);
    }

    /// ADR-040 Fase 3 — THE behavioural test: with a wall between it and you, the phantom must aim
    /// somewhere OTHER than straight at you. Before this phase both STALK and SPRINT pointed at the
    /// player unconditionally and ground into the geometry; this asserts the heading actually
    /// bends. Deterministic: the blocked pair is discovered in the real seed-42 world, so it also
    /// proves the navigation works against generated geometry rather than a hand-made fixture.
    #[tokio::test]
    async fn phantom_steers_around_geometry_instead_of_into_it() {
        use crate::world::grid_gen::{
            cell_center, find_path, segment_is_clear, GridGenChunkCache, NavScratch,
        };

        let mut probe = GridGenChunkCache::with_rules(42, crate::world::zone_density::rules_for);
        let mut scratch = NavScratch::new();
        let mut cells = Vec::new();

        // Find two walkable cells whose straight line is blocked but which ARE connected.
        let mut found: Option<(Vec3, Vec3)> = None;
        'outer: for ax in 1..18i32 {
            for az in 1..18i32 {
                let a = cell_center((ax, az), 0.0);
                if !crate::world::grid_gen::is_walkable_grid_gen(&mut probe, a, 0) {
                    continue;
                }
                for bx in (ax + 2)..20i32 {
                    for bz in (az + 2)..20i32 {
                        let b = cell_center((bx, bz), 0.0);
                        if !crate::world::grid_gen::is_walkable_grid_gen(&mut probe, b, 0) {
                            continue;
                        }
                        if segment_is_clear(&mut probe, 0, a, b) {
                            continue; // line of travel is open — not the case we want
                        }
                        find_path(&mut probe, 0, (ax, az), (bx, bz), &mut scratch, &mut cells);
                        if !cells.is_empty() && *cells.last().unwrap() == (bx, bz) {
                            found = Some((a, b));
                            break 'outer;
                        }
                    }
                }
            }
        }
        let (from, target) =
            found.expect("seed 42 must contain a blocked-but-connected pair near the origin");

        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let pid = net.spawn_phantom("Robapieles_Test", from.to_array());
        let mut driver = PhantomDriver::new(42);
        driver.add(pid, 0.0, from, true);

        let navigated = driver.steer_heading(0, 0, from, target, 0.1);
        let straight = (target.x - from.x)
            .atan2(target.z - from.z)
            .rem_euclid(std::f32::consts::TAU);

        let delta = (navigated - straight)
            .abs()
            .min(std::f32::consts::TAU - (navigated - straight).abs());
        assert!(
            delta > 0.05,
            "with a wall in the way the phantom must not aim straight at the player: \
             navigated={navigated:.3} straight={straight:.3} (from {from:?} to {target:?})"
        );
        assert!(
            !driver.movers[0].nav_waypoints.is_empty(),
            "a route must have been planned"
        );
    }

    /// A pathfinder must never come BETWEEN the creature and a player it can already reach in a
    /// straight line. Play-test symptom this pins: pressed against a wall, the player's cell can
    /// quantize into one grid_gen calls solid, the search returns best effort, the route ends a
    /// cell short, and the phantom parks ~2 m away staring — never triggering its point-blank
    /// strike at dist < 1.5.
    #[tokio::test]
    async fn clear_line_of_travel_beats_the_plan() {
        use crate::world::grid_gen::{is_walkable_grid_gen, segment_is_clear, GridGenChunkCache};

        // Find an open pair with a CLEAR line in the real world.
        let mut probe = GridGenChunkCache::with_rules(42, crate::world::zone_density::rules_for);
        let mut pair: Option<(Vec3, Vec3)> = None;
        'outer: for ax in 1..19i32 {
            for az in 1..19i32 {
                let a = crate::world::grid_gen::cell_center((ax, az), 0.0);
                if !is_walkable_grid_gen(&mut probe, a, 0) {
                    continue;
                }
                let b = Vec3::new(a.x + 2.0, a.y, a.z);
                if is_walkable_grid_gen(&mut probe, b, 0) && segment_is_clear(&mut probe, 0, a, b) {
                    pair = Some((a, b));
                    break 'outer;
                }
            }
        }
        let (from, target) = pair.expect("seed 42 must have an open pair near the origin");

        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let pid = net.spawn_phantom("Robapieles_Test", from.to_array());
        let mut driver = PhantomDriver::new(42);
        driver.add(pid, 0.0, from, true);
        // Poison it with a stale plan: the shortcut must throw it away, not follow it.
        driver.movers[0].nav_waypoints = vec![Vec3::new(from.x - 20.0, from.y, from.z)];
        driver.movers[0].nav_goal = Some(Vec3::new(from.x - 20.0, from.y, from.z));

        let h = driver.steer_heading(0, 0, from, target, 0.1);
        let straight = (target.x - from.x)
            .atan2(target.z - from.z)
            .rem_euclid(std::f32::consts::TAU);
        assert!(
            (h - straight).abs() < 1e-3,
            "with a clear line the heading must be the straight bearing: {h} vs {straight}"
        );
        assert!(
            driver.movers[0].nav_waypoints.is_empty(),
            "the stale plan must be dropped, not walked"
        );
    }

    /// The cost bound is only honest if the replan policy actually throttles. One search per steer
    /// call is by construction; this pins the POLICY: a static target must not be replanned every
    /// tick just because time passed.
    #[tokio::test]
    async fn replan_policy_throttles_a_static_target() {
        let start = [25.0, 1.8, 25.0];
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let mut driver = PhantomDriver::new(42);
        let from = Vec3::from_array(net.peers[&pid].position);
        driver.add(pid, 0.0, from, true);

        let target = Vec3::new(from.x + 6.0, from.y, from.z + 6.0);
        for _ in 0..10 {
            driver.steer_heading(0, 0, from, target, 0.1); // 10 ticks = 1.0 s
        }
        // 1.0 s at a 0.6 s interval is at most two windows; allow one extra for the initial plan.
        assert!(
            driver.nav_replans <= 3,
            "replan policy did not throttle: {} searches in 1 s",
            driver.nav_replans
        );
        assert!(
            driver.nav_replans >= 1,
            "it must have planned at least once"
        );
    }

    #[tokio::test]
    async fn replan_stagger_spreads_the_searches_of_a_populated_world() {
        // ADR-043 — the lever ADR-040 wrote down. `PHANTOM_REPLAN_INTERVAL` is a fixed 0.6 s, so
        // movers that woke on the same tick keep their `nav_age` in phase and every one of them
        // comes due on the SAME step: the cost is a burst of N searches in one 100 ms slot, not the
        // average. The stagger caps that burst at ceil(N / stride).
        //
        // Asserting on the WORST step, not the total: throttling the average while still bursting
        // is exactly the failure this exists to prevent.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut driver = population_driver(42, 8);
        let here = Vec3::new(0.0, stand_on(0), 0.0);
        driver.sync_population(&mut net, here, 0.1);
        let n = driver.movers.len();
        assert!(n >= 2, "need a crowd to stagger, got {n}");

        // Force every one of them to want a route to a goal it cannot walk straight to, so the
        // only thing standing between them and a search is the stagger.
        let mut worst = 0u64;
        for _ in 0..30 {
            driver.step_counter = driver.step_counter.wrapping_add(1);
            let before = driver.nav_replans;
            for i in 0..driver.movers.len() {
                let from = Vec3::from_array(net.peers[&driver.movers[i].id].position);
                let goal = Vec3::new(from.x + 45.0, from.y, from.z + 45.0);
                driver.movers[i].nav_age = PHANTOM_REPLAN_INTERVAL; // due right now
                driver.steer_heading(i, 0, from, goal, 0.1);
            }
            worst = worst.max(driver.nav_replans - before);
        }

        // Asserted against N, NOT against `ceil(N / PHANTOM_REPLAN_STRIDE)`: deriving the bound
        // from the same constant the code uses makes the test move with the mutation and pass
        // whatever the stride is (verified — with the stride at 1 the ceil form still passed). The
        // property that actually matters is that the burst is strictly smaller than "all of them
        // at once", and that is what a stride of 1 breaks.
        assert!(
            worst < n as u64,
            "{n} movers all replanned in the same step ({worst}); the stagger is not spreading them"
        );
        let allowed = n.div_ceil(PHANTOM_REPLAN_STRIDE as usize) as u64;
        assert!(
            worst <= allowed,
            "{n} movers burst {worst} searches in one step; the stride allows {allowed}"
        );
        assert!(driver.nav_replans > 0, "nothing replanned at all");
    }

    #[tokio::test]
    async fn phantom_driver_walks_via_grid_cache_far_from_host() {
        // Far from the host: the phantom must resolve collision against grid_gen via the
        // on-demand GridGenChunkCache (the host player is parked very far so it never chases).
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [5000.0, 1.8, 5000.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42); // seed source only; the phantom no longer reads world.chunks
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);

        // 200 ticks (20 s sim): a WANDER pause can freeze it for up to 12 s, so this window
        // guarantees at least one walk step → the grid_gen cache is exercised deterministically.
        for _ in 0..200 {
            driver.step(
                &mut net,
                0.1,
                Vec3::new(100_000.0, 1.8, 100_000.0),
                0.0,
                false,
            );
        }

        // The driver exercised the grid_gen cache (proves on-demand generation far from host).
        assert!(
            !driver.grid_cache.is_empty(),
            "driver must generate grid_gen chunks far from the host"
        );
        // The phantom stayed grounded with a finite pose (never NaN, never an unloaded snap).
        let p = net.peers[&pid].position;
        assert!(
            p[0].is_finite() && p[1].is_finite() && p[2].is_finite(),
            "phantom pose must be finite"
        );
        assert!(
            p[1] > 0.0,
            "phantom must be grounded on a real floor, got y={}",
            p[1]
        );
    }

    #[tokio::test]
    async fn phantom_transitions_wander_to_spotted_in_radius() {
        // ADR-016 slice 3a: a real player within DETECT_RADIUS and inside the forward cone trips
        // WANDER → SPOTTED in a single step (detection is checked first in WANDER → deterministic
        // regardless of the sim collision at the origin).
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        // PHANTOM_INITIAL_HEADING faces +X (dir = (sin, _, cos) at FRAC_PI_2 = (1, 0, 0)).
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        let player = Vec3::new(6.0, 1.8, 0.0); // 6 m ahead (+X): inside radius and cone

        driver.step(&mut net, 0.1, player, 0.0, false);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Spotted,
            "a player in radius + cone must trip WANDER → SPOTTED"
        );
        // Entering SPOTTED arms a randomized stare window in [SPOTTED_MIN, SPOTTED_MAX].
        let dur = driver.movers[0].spotted_duration;
        assert!(
            (PHANTOM_SPOTTED_MIN..=PHANTOM_SPOTTED_MAX).contains(&dur),
            "spotted_duration must be seeded in range, got {dur}"
        );
    }

    #[tokio::test]
    async fn phantom_stays_wander_when_player_beyond_radius() {
        // A player well past DETECT_RADIUS → no detection (stays in WANDER).
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        let player = Vec3::new(100.0, 1.8, 0.0); // far beyond the detect/lose radius

        driver.step(&mut net, 0.1, player, 0.0, false);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Wander,
            "must not engage a player beyond the detect radius"
        );
    }

    #[tokio::test]
    async fn phantom_spotted_to_stalk_after_duration() {
        // ADR-016 slice 3a: once the SPOTTED stare window elapses, the phantom advances to STALK.
        // The duration check precedes the random lunge, so an elapsed window is deterministic.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        // Force a SPOTTED already past its (tiny) stare window, player still in range.
        driver.movers[0].state = PhantomState::Spotted;
        driver.movers[0].spotted_duration = 0.5;
        driver.movers[0].state_timer = 10.0;
        let player = Vec3::new(6.0, 1.8, 0.0); // inside DETECT_RADIUS*1.5

        driver.step(&mut net, 0.1, player, 0.0, false);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Stalk,
            "an elapsed SPOTTED stare must advance to STALK"
        );
    }

    #[tokio::test]
    async fn phantom_sprints_after_patience_exceeded() {
        // ADR-016 slice 3a: a phantom that has STALKed past PHANTOM_STALK_PATIENCE lunges into
        // SPRINT. The patience check precedes the random roll, so this is deterministic.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        // Force a STALK whose patience has already run out, player inside LOSE_RADIUS.
        driver.movers[0].state = PhantomState::Stalk;
        driver.movers[0].state_timer = PHANTOM_STALK_PATIENCE + 5.0;
        let player = Vec3::new(6.0, 1.8, 0.0);

        driver.step(&mut net, 0.1, player, 0.0, false);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Sprint,
            "patience exhausted while stalking must trigger SPRINT"
        );
    }

    #[tokio::test]
    async fn phantom_fake_pickup_touches_only_animation_not_real_state() {
        // SAFETY INVARIANT (ADR-016 slice 4): a faked pickup must flip ONLY the phantom's
        // animation field — never the real pickup state. Seed a real item to prove it survives.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        net.stp_items.push(crate::network::protocol::StpItemInfo {
            id: 7,
            def_id: 1,
            count: 1,
            position: [10.5, 1.8, 10.0],
            rotation: 0.0,
        });
        let pid = net.spawn_phantom("Robapieles_Test", [10.0, 1.8, 10.0]);
        let spawn_pos = net.peers[&pid].position; // actual (grid_gen-snapped) spawn position
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(
            pid,
            PHANTOM_INITIAL_HEADING,
            Vec3::from_array(spawn_pos),
            true,
        );
        // Force the gesture to be due now (instead of after the cooldown).
        driver.movers[0].next_pickup_at = Instant::now();

        driver.step(
            &mut net,
            0.1,
            Vec3::new(100_000.0, 1.8, 100_000.0),
            0.0,
            false,
        );

        // It IS faking the gesture: the presentation flank is "pickup"…
        assert_eq!(
            net.peers[&pid].animation, "pickup",
            "faked pickup must set the animation flank"
        );
        // …and the phantom stayed put during the gesture (movement paused).
        assert_eq!(net.peers[&pid].position, spawn_pos);
        // INVARIANT: nothing real changed — the item still exists, no reservation, no grant.
        assert_eq!(net.stp_items.len(), 1, "phantom must NOT remove real items");
        assert!(net.stp_items.iter().any(|it| it.id == 7));
        assert!(
            net.pending_pickups.is_empty(),
            "phantom must NOT reserve pickups"
        );
        assert!(
            net.processed_stp_pickup_grants.is_empty(),
            "phantom must NOT process any pickup grant"
        );
    }

    // ─── ADR-043: population — which of the world's robapieles are actually simulated ───

    /// A driver whose knobs are fixed in code, so a stray `PHANTOM_*` in the developer's shell
    /// cannot quietly change what these tests are asserting.
    fn population_driver(seed: u64, cap: usize) -> PhantomDriver {
        let mut d = PhantomDriver::new(seed);
        d.density_scale = 1.0;
        d.active_cap = cap;
        d
    }

    /// Standing height on `layer`, in the player-pivot convention every peer pose uses.
    fn stand_on(layer: u8) -> f32 {
        crate::world::grid_gen::grid_floor_y(layer) + crate::world::collision::PLAYER_BASE_Y
    }

    #[tokio::test]
    async fn population_wakes_phantoms_near_a_player_and_none_when_alone_far_away() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut driver = population_driver(42, 8);

        driver.sync_population(&mut net, Vec3::new(0.0, stand_on(0), 0.0), 0.1);
        let near_spawn = driver.movers.len();
        assert!(
            near_spawn > 0,
            "a player standing in the world must have neighbours"
        );
        assert!(
            driver.movers.iter().all(|m| m.anchor.is_some()),
            "drawn phantoms must record the block they came from"
        );
        // Every awake one is genuinely within the activation radius — the block is only a coarse
        // filter, and a 200 m block reaches well past 150 m from its far corner.
        for m in &driver.movers {
            let here = Vec3::from_array(net.peers[&m.id].position);
            assert!(
                here.distance_xz(Vec3::new(0.0, 0.0, 0.0)) <= PHANTOM_ACTIVATE_RADIUS + 10.0,
                "woke one at {here:?}, outside the activation radius"
            );
        }
    }

    #[tokio::test]
    async fn population_ignores_blocks_on_another_layer() {
        // ADR-043 D-ACTIVACIÓN. Layers 1-3 draw empty today, so a player standing on one must wake
        // nothing at all — and crucially, must not wake the LAYER 0 creatures underneath it just
        // because they are close in XZ. That is the failure the layer filter exists to prevent.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut driver = population_driver(42, 8);

        driver.sync_population(&mut net, Vec3::new(0.0, stand_on(1), 0.0), 0.1);

        assert!(
            driver.movers.is_empty(),
            "a player on layer 1 woke {} phantoms",
            driver.movers.len()
        );
        // Control: the very same XZ on layer 0 does wake some, so the assert above is the layer
        // filter working and not simply an empty neighbourhood.
        let mut driver0 = population_driver(42, 8);
        driver0.sync_population(&mut net, Vec3::new(0.0, stand_on(0), 0.0), 0.1);
        assert!(!driver0.movers.is_empty(), "control: layer 0 must populate");
    }

    #[tokio::test]
    async fn population_never_exceeds_the_active_cap() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut driver = population_driver(42, 1);

        for _ in 0..5 {
            driver.sync_population(&mut net, Vec3::new(0.0, stand_on(0), 0.0), 1.0);
        }

        assert!(
            driver.movers.len() <= 1,
            "cap of 1 held {} movers",
            driver.movers.len()
        );
    }

    #[tokio::test]
    async fn a_settled_block_is_not_spawned_twice() {
        // The anchor is the identity of a drawn phantom. Without it every scan would re-draw the
        // same block and stack duplicates on the same spot until the cap stopped it.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut driver = population_driver(42, 8);
        let here = Vec3::new(0.0, stand_on(0), 0.0);

        driver.sync_population(&mut net, here, 0.1);
        let first = driver.movers.len();
        for _ in 0..4 {
            driver.sync_population(&mut net, here, 1.0);
        }

        assert_eq!(
            driver.movers.len(),
            first,
            "repeat scans duplicated the population"
        );
        let anchors: std::collections::HashSet<_> =
            driver.movers.iter().filter_map(|m| m.anchor).collect();
        assert_eq!(anchors.len(), first, "two movers share one anchor block");
    }

    #[tokio::test]
    async fn walking_away_puts_a_wanderer_away_but_never_a_pursuer() {
        // ADR-043 D5, and the reason deactivation is not the mirror of activation: a phantom that
        // is chasing has LEFT its anchor, so despawning it would teleport it home the moment it
        // woke again — read as a bug, not as having escaped. Losing it already has its own
        // designed mechanic (SEARCH + the 12 s surrender, ADR-040).
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut driver = population_driver(42, 8);
        let here = Vec3::new(0.0, stand_on(0), 0.0);
        driver.sync_population(&mut net, here, 0.1);
        assert!(driver.movers.len() >= 2, "need at least two to compare");

        // One keeps wandering, one is on your heels.
        driver.movers[0].state = PhantomState::Wander;
        driver.movers[1].state = PhantomState::Stalk;
        let wanderer = driver.movers[0].id;
        let pursuer = driver.movers[1].id;

        // The player leaves — far past the deactivation radius.
        let far = Vec3::new(5_000.0, stand_on(0), 5_000.0);
        driver.sync_population(&mut net, far, 1.0);

        assert!(
            !driver.movers.iter().any(|m| m.id == wanderer),
            "the wanderer should have been put away"
        );
        assert!(
            !net.peers.contains_key(&wanderer) && !net.is_phantom(wanderer),
            "despawn must clear BOTH peers and phantom_ids, or the id leaks"
        );
        assert!(
            driver.movers.iter().any(|m| m.id == pursuer),
            "a pursuing phantom must survive the player walking away"
        );
    }

    #[tokio::test]
    async fn hysteresis_stops_a_phantom_blinking_at_the_boundary() {
        // Between the two radii nothing may change. With a single threshold, a player loitering
        // there would spawn and despawn the same creature every second — on the client, an avatar
        // flickering in and out at the edge of view distance.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut driver = population_driver(42, 8);
        driver.sync_population(&mut net, Vec3::new(0.0, stand_on(0), 0.0), 0.1);
        assert!(!driver.movers.is_empty());

        // Measure the band against ONE specific creature: standing `band` metres from the origin
        // says nothing about a phantom that was drawn 120 m the other way.
        let watched = driver.movers[0].id;
        let its_pos = Vec3::from_array(net.peers[&watched].position);
        let band = (PHANTOM_ACTIVATE_RADIUS + PHANTOM_DEACTIVATE_RADIUS) * 0.5;
        let loiter = Vec3::new(its_pos.x + band, stand_on(0), its_pos.z);
        assert!(
            loiter.distance_xz(its_pos) > PHANTOM_ACTIVATE_RADIUS
                && loiter.distance_xz(its_pos) < PHANTOM_DEACTIVATE_RADIUS,
            "test setup is not inside the dead band"
        );

        driver.sync_population(&mut net, loiter, 1.0);

        assert!(
            driver.movers.iter().any(|m| m.id == watched),
            "a phantom was retired from inside the hysteresis band"
        );
    }

    #[tokio::test]
    async fn phantoms_spread_their_victims_across_real_peers() {
        // ADR-043 fixes the one-name-for-everyone bug: harmless with a single debug phantom, and
        // the disguise defeating itself the moment two of them are in view at once.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        for (id, name) in [(2u16, "Joel"), (3, "Ana"), (4, "Iker")] {
            let addr: std::net::SocketAddr = format!("127.0.0.1:{}", 9000 + id).parse().unwrap();
            net.peers.insert(
                id,
                crate::network::peer::PeerConnection::new(id, name.into(), addr),
            );
        }
        let mut driver = PhantomDriver::new(42);
        let start = [0.0, 1.8, 0.0];
        for _ in 0..3 {
            let slot = driver.next_victim_slot;
            let (name, bound) = choose_victim_name_for(&net, slot);
            let id = net.spawn_phantom(&name, start);
            driver.add(id, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), bound);
        }

        let worn: std::collections::HashSet<String> = driver
            .movers
            .iter()
            .map(|m| net.peers[&m.id].name.clone())
            .collect();
        assert_eq!(
            worn.len(),
            3,
            "three phantoms, three real victims, but they wore {worn:?}"
        );
    }

    #[tokio::test]
    async fn phantom_clones_victim_name_but_keeps_its_own_id() {
        // ADR-016 identity phase: the phantom impersonates a real peer's NAME but never its id.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();

        // No real peers yet → spawn falls back to the host name, unbound.
        let (name0, bound0) = choose_victim_name_for(&net, 0);
        assert_eq!(name0, net.local_name, "solo fallback is the host name");
        assert!(!bound0, "fallback spawn must be unbound");
        let pid = net.spawn_phantom(&name0, [0.0, 1.8, 0.0]);
        let mut driver = PhantomDriver::new(42);
        driver.add(
            pid,
            PHANTOM_INITIAL_HEADING,
            Vec3::new(0.0, 1.8, 0.0),
            bound0,
        );

        // A real victim connects with a name.
        let victim_id = 2;
        let addr = "127.0.0.1:9999".parse().unwrap();
        net.peers.insert(
            victim_id,
            crate::network::peer::PeerConnection::new(victim_id, "Joel".into(), addr),
        );

        driver.rebind_unbound_victims(&mut net);

        // The phantom now wears the victim's NAME…
        assert_eq!(
            net.peers[&pid].name, "Joel",
            "phantom must clone the victim name"
        );
        // …but keeps its OWN unique phantom id (never the victim's id — the subtle tell).
        assert_ne!(pid, victim_id);
        assert!(net.is_phantom(pid));
        assert!(!net.is_phantom(victim_id));
        // The real victim is untouched.
        assert_eq!(net.peers[&victim_id].name, "Joel");

        // Idempotent: a second rebind does not steal a new victim (bound stays put).
        driver.rebind_unbound_victims(&mut net);
        assert_eq!(net.peers[&pid].name, "Joel");
    }

    #[tokio::test]
    async fn phantom_statue_freezes_when_player_looks() {
        // ADR-016 slice 3b-P1: a STALKing phantom freezes (STATUE) when the player looks at it
        // (within range + horizontal cone). Deterministic — no rand on this path.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].state = PhantomState::Stalk;
        let player = Vec3::new(6.0, 1.8, 0.0); // close, inside STATUE_RANGE
        let player_yaw = 270.0; // faces -X, i.e. toward the phantom near the origin

        driver.step(&mut net, 0.1, player, player_yaw, false);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Statue,
            "a watched STALKer must freeze into STATUE"
        );
    }

    #[tokio::test]
    async fn phantom_statue_releases_to_stalk_when_player_looks_away() {
        // STATUE resumes STALK (not WANDER) the moment the player looks away.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].state = PhantomState::Statue;
        let player = Vec3::new(6.0, 1.8, 0.0); // close, inside LOSE_RADIUS
        let player_yaw = 90.0; // faces +X, AWAY from the phantom

        driver.step(&mut net, 0.1, player, player_yaw, false);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Stalk,
            "STATUE must release to STALK when the player looks away"
        );
    }

    #[tokio::test]
    async fn phantom_sound_detection_hears_running_player_outside_cone() {
        // ADR-016 slice 3b-P1: a RUNNING player beyond the normal cone/radius (but within
        // DETECT + SOUND_BONUS) is HEARD → SPOTTED with a short (sound) stare. Speed is derived
        // from the per-tick position delta, so we pre-seed last tick's position.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        // Heading +X; the player is BEHIND (-X) at ~18 m: outside the cone AND beyond
        // DETECT_RADIUS (15), but inside DETECT + SOUND_BONUS (23).
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        // Seed last-tick position 1 m back → 10 m/s this tick (> RUN_THRESHOLD, < sanity cap).
        driver
            .prev_target_pos
            .insert(net.local_id, Vec3::new(-19.0, 1.8, 0.0));
        let player = Vec3::new(-18.0, 1.8, 0.0);

        driver.step(&mut net, 0.1, player, 0.0, false);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Spotted,
            "a running player within sound range must be heard → SPOTTED"
        );
        assert!(
            driver.movers[0].spotted_duration <= PHANTOM_SPOTTED_SOUND_MAX,
            "sound-triggered stare must use the short window, got {}",
            driver.movers[0].spotted_duration
        );
    }

    /// ADR-040 perception — the stealth payoff. EXACT same setup as the test above (running player
    /// behind it, inside sound range) with one difference: crouched. Sound is the only channel that
    /// ignores the view cone, so muting it is precisely what makes sneaking up BEHIND it work.
    #[tokio::test]
    async fn crouching_mutes_the_sound_channel() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let mut driver = PhantomDriver::new(42);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver
            .prev_target_pos
            .insert(net.local_id, Vec3::new(-19.0, 1.8, 0.0));
        let player = Vec3::new(-18.0, 1.8, 0.0);

        driver.step(&mut net, 0.1, player, 0.0, true); // crouched

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Wander,
            "a CROUCHING player behind it must not be heard, however fast they move"
        );
    }

    /// The middle tier. Walking is audible, but only close — between the silence of crouching and
    /// the long reach of a sprint. Without this the stealth model is a binary and posture stops
    /// mattering.
    #[tokio::test]
    async fn walking_is_heard_only_close_by() {
        // Same geometry, walking speed (2 m/s), at two distances: inside and outside WALK_HEAR.
        for (dist, expect_heard) in [(6.0_f32, true), (14.0_f32, false)] {
            let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
            let start = [0.0, 1.8, 0.0];
            let pid = net.spawn_phantom("Robapieles_Test", start);
            let mut driver = PhantomDriver::new(42);
            driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
            // Behind it (-X) so the view cone can never be the thing that detects.
            let player = Vec3::new(-dist, 1.8, 0.0);
            driver
                .prev_target_pos
                .insert(net.local_id, Vec3::new(-dist - 0.2, 1.8, 0.0)); // 2 m/s
            driver.step(&mut net, 0.1, player, 0.0, false);

            let heard = driver.movers[0].state != PhantomState::Wander;
            assert_eq!(
                heard, expect_heard,
                "walking at {dist} m: expected heard={expect_heard} (WALK_HEAR_RADIUS is {PHANTOM_WALK_HEAR_RADIUS})"
            );
        }
    }

    /// ADR-041 — a shot within earshot must start an investigation, with the LONG patience: it is
    /// about to walk for minutes, and arriving only to shrug after 12 s would waste the approach.
    #[tokio::test]
    async fn a_noise_within_earshot_starts_an_investigation() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [25.0, 1.8, 25.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let mut driver = PhantomDriver::new(42);
        let from = Vec3::from_array(net.peers[&pid].position);
        driver.add(pid, 0.0, from, true);

        // 400 m away, rifle loudness 500 → heard.
        net.pending_noises
            .push(([from.x + 400.0, from.y, from.z], 500.0));
        driver.step(
            &mut net,
            0.1,
            Vec3::new(from.x + 400.0, 1.8, from.z),
            0.0,
            false,
        );

        assert_eq!(driver.movers[0].state, PhantomState::Search);
        assert_eq!(
            driver.movers[0].search_patience,
            PHANTOM_NOISE_SEARCH_PATIENCE
        );
        assert!(
            driver.movers[0].noise_expiry.is_some(),
            "it must be able to go cold"
        );
        assert!(driver.movers[0].last_known_player_pos.is_some());
    }

    /// Beyond the weapon's loudness there is simply no stimulus. Loudness IS the radius.
    #[tokio::test]
    async fn a_noise_beyond_earshot_is_ignored() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [25.0, 1.8, 25.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let mut driver = PhantomDriver::new(42);
        let from = Vec3::from_array(net.peers[&pid].position);
        driver.add(pid, 0.0, from, true);

        net.pending_noises
            .push(([from.x + 400.0, from.y, from.z], 60.0)); // quiet weapon, 400 m away
        driver.step(
            &mut net,
            0.1,
            Vec3::new(from.x + 5000.0, 1.8, from.z),
            0.0,
            false,
        );

        assert_eq!(driver.movers[0].state, PhantomState::Wander);
    }

    /// A committed lunge is not distractible. Turning away from the player in front of it to chase
    /// a noise elsewhere would read as stupidity, not curiosity.
    #[tokio::test]
    async fn a_noise_does_not_interrupt_a_committed_sprint() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [25.0, 1.8, 25.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let mut driver = PhantomDriver::new(42);
        let from = Vec3::from_array(net.peers[&pid].position);
        driver.add(pid, 0.0, from, true);
        driver.movers[0].state = PhantomState::Sprint;

        net.pending_noises
            .push(([from.x + 100.0, from.y, from.z], 500.0));
        driver.hear_noises(&mut net);

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Sprint,
            "a noise must not pull it off a committed attack"
        );
    }

    /// The localization error is what separates "heard you" from "knows where you are". It must
    /// scale with distance and be DETERMINISTIC — a per-tick random estimate would make the phantom
    /// zigzag toward a target that keeps moving, which reads as a bug rather than as uncertainty.
    #[test]
    fn noise_localization_error_scales_with_distance_and_is_stable() {
        let src = Vec3::new(500.0, 0.0, 500.0);
        for dist in [10.0_f32, 100.0, 500.0] {
            let a = blur_noise(src, dist, 0xF000);
            let b = blur_noise(src, dist, 0xF000);
            assert_eq!(a, b, "the same shot must always resolve to the same spot");
            let err = ((a.x - src.x).powi(2) + (a.z - src.z).powi(2)).sqrt();
            let expected = dist * PHANTOM_NOISE_ERROR_FRAC;
            assert!(
                (err - expected).abs() < 0.01,
                "at {dist} m the error must be {expected:.2} m, got {err:.2}"
            );
        }
    }

    /// ADR-040 Fase 4 — losing you must lead to a SEARCH of the last known spot, not to instant
    /// amnesia. This is the counterweight that stops the new pathfinding from turning the creature
    /// into a homing missile.
    #[tokio::test]
    async fn losing_the_target_starts_a_search_not_amnesia() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [25.0, 1.8, 25.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let mut driver = PhantomDriver::new(42);
        let from = Vec3::from_array(net.peers[&pid].position);
        driver.add(pid, 0.0, from, true);
        driver.movers[0].state = PhantomState::Stalk;
        driver.movers[0].last_known_player_pos = Some(Vec3::new(from.x + 5.0, from.y, from.z));

        // Player far beyond LOSE_RADIUS.
        driver.step(
            &mut net,
            0.1,
            Vec3::new(from.x + 500.0, 1.8, from.z),
            0.0,
            false,
        );

        assert_eq!(
            driver.movers[0].state,
            PhantomState::Search,
            "with a remembered position it must go looking, not resume wandering"
        );
    }

    /// …and the search must END. A creature that hunts the same spot forever is as broken as one
    /// that forgets instantly: forgetting is what makes hiding an escape rather than a delay.
    #[tokio::test]
    async fn search_gives_up_and_forgets_after_its_patience() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [25.0, 1.8, 25.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let mut driver = PhantomDriver::new(42);
        let from = Vec3::from_array(net.peers[&pid].position);
        driver.add(pid, 0.0, from, true);
        driver.movers[0].state = PhantomState::Search;
        // A goal far away so it cannot "arrive" — only patience can end this.
        driver.movers[0].last_known_player_pos =
            Some(Vec3::new(from.x + 200.0, from.y, from.z + 200.0));
        driver.movers[0].state_timer = PHANTOM_SEARCH_MAX + 1.0;

        driver.step(
            &mut net,
            0.1,
            Vec3::new(from.x + 500.0, 1.8, from.z),
            0.0,
            false,
        );

        assert_eq!(driver.movers[0].state, PhantomState::Wander);
        assert!(
            driver.movers[0].last_known_player_pos.is_none(),
            "giving up must also FORGET, or the next search resumes a stale hunt"
        );
    }

    #[test]
    fn lerp_heading_eases_toward_target_via_shorter_arc() {
        use std::f32::consts::{FRAC_PI_2, TAU};
        // t = 1 → exactly the target; t = 0 → unchanged.
        assert!((lerp_heading(0.0, FRAC_PI_2, 1.0) - FRAC_PI_2).abs() < 1e-3);
        assert!((lerp_heading(1.0, 2.0, 0.0) - 1.0).abs() < 1e-3);
        // A partial ease lands strictly between current and target.
        let mid = lerp_heading(0.0, FRAC_PI_2, 0.5);
        assert!(
            mid > 0.01 && mid < FRAC_PI_2 - 0.01,
            "partial ease, got {mid}"
        );
        // Shorter arc: 350° → 10° must cross 0, not swing the long way through 180°.
        let h = lerp_heading(350f32.to_radians(), 10f32.to_radians(), 0.5);
        let dist_to_zero = h.min(TAU - h);
        assert!(
            dist_to_zero < 0.2,
            "must take the shorter arc through 0, got {h}"
        );
    }

    #[tokio::test]
    async fn phantom_sprint_kills_from_behind() {
        // ADR-016 slice 1: a point-blank SPRINT while the player is NOT looking (phantom behind)
        // → lethal Kill. Deterministic.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].state = PhantomState::Sprint;
        // Place the player point-blank relative to the phantom's actual (snapped) spawn pos.
        let ppos = net.peers[&pid].position;
        let player = Vec3::new(ppos[0] + 1.0, 1.8, ppos[2]); // ~1 m east: point-blank
        let player_yaw = 90.0; // faces +X, AWAY from the phantom (to its west) → not looking

        let attack = driver.step(&mut net, 0.1, player, player_yaw, false);

        assert_eq!(
            attack,
            [PhantomAttack::Kill],
            "behind-attack must KILL, got {attack:?}"
        );
    }

    #[tokio::test]
    async fn phantom_sprint_hits_from_front() {
        // Point-blank SPRINT while the player IS looking → non-lethal Hit, bounces to STALK.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].state = PhantomState::Sprint;
        let ppos = net.peers[&pid].position;
        let player = Vec3::new(ppos[0] + 1.0, 1.8, ppos[2]); // ~1 m east: point-blank
        let player_yaw = 270.0; // faces -X, TOWARD the phantom → looking

        let attack = driver.step(&mut net, 0.1, player, player_yaw, false);

        assert!(
            matches!(attack, [PhantomAttack::Hit(d)] if (d - PHANTOM_ATTACK_DAMAGE).abs() < 1e-3),
            "frontal attack must HIT for {PHANTOM_ATTACK_DAMAGE}, got {attack:?}"
        );
        assert_eq!(
            driver.movers[0].state,
            PhantomState::Stalk,
            "must bounce to STALK after a hit"
        );
    }

    #[tokio::test]
    async fn phantom_statue_timeout_knocks_back_point_blank() {
        // STATUE that times out while the player is point-blank → SPRINT + a Knockback signal.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].state = PhantomState::Statue;
        driver.movers[0].state_timer = PHANTOM_STATUE_MAX + 1.0;
        let ppos = net.peers[&pid].position;
        let player = Vec3::new(ppos[0] + 2.0, 1.8, ppos[2]); // within PHANTOM_KNOCKBACK_RANGE (3 m)

        let attack = driver.step(&mut net, 0.1, player, 0.0, false);

        assert!(
            matches!(attack, [PhantomAttack::Knockback(_, _)]),
            "point-blank STATUE timeout must shove, got {attack:?}"
        );
        assert_eq!(driver.movers[0].state, PhantomState::Sprint);
    }

    #[tokio::test]
    async fn phantom_idle_step_returns_no_attack() {
        // A plain WANDER step far from any player produces no attack.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);

        let attack = driver.step(
            &mut net,
            0.1,
            Vec3::new(100_000.0, 1.8, 100_000.0),
            0.0,
            false,
        );

        assert!(
            attack.is_empty(),
            "idle step must attack nobody, got {attack:?}"
        );
    }

    #[tokio::test]
    async fn phantom_step_reports_every_attacker_not_just_the_last() {
        // ADR-043 — the fan-out this whole slice exists for. TWO phantoms strike the same player
        // in the SAME step; before the fan-out the second overwrote the first and one of the two
        // creatures hit you for free. Mutation check: reverting `step` to a single slot makes this
        // assert see 1 instead of 2.
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let world = World::new(42);
        let mut driver = PhantomDriver::new(world.seed);

        // Both phantoms are seeded on the same cell, so both end up point-blank on the player.
        let start = [0.0, 1.8, 0.0];
        let a = net.spawn_phantom("Robapieles_A", start);
        let b = net.spawn_phantom("Robapieles_B", start);
        for id in [a, b] {
            driver.add(id, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        }
        for m in driver.movers.iter_mut() {
            m.state = PhantomState::Sprint;
        }

        // Player ~1 m east of where the snap actually put them, facing them → frontal Hit each.
        let ppos = net.peers[&a].position;
        let player = Vec3::new(ppos[0] + 1.0, 1.8, ppos[2]);

        let attacks = driver.step(&mut net, 0.1, player, 270.0, false);

        assert_eq!(
            attacks.len(),
            2,
            "both attackers of the tick must be reported, got {attacks:?}"
        );
        assert!(
            attacks.iter().all(
                |a| matches!(a, PhantomAttack::Hit(d) if (d - PHANTOM_ATTACK_DAMAGE).abs() < 1e-3)
            ),
            "both must be frontal hits, got {attacks:?}"
        );
    }

    // ─── ADR-029 V0: PvP validation order + victim-applied damage ───

    fn base_pvp_input(request_id: u64) -> PvpValidationInput {
        PvpValidationInput {
            is_host: true,
            request_id,
            attacker_id: 1,
            victim_id: 1004,
            attacker_known: true,
            victim_known: true,
            victim_dead: false,
            victim_invuln: false,
            weapon_id: 9692212, // STP_Marlin 336 (firearm): max_damage=45, max_range=100
            damage: 20.0,
            direction: [0.0, 0.0, 1.0],
            attacker_pos: Vec3::new(0.0, 1.8, 0.0),
            victim_pos: Vec3::new(0.0, 1.8, 10.0),
        }
    }

    #[test]
    fn validate_pvp_hit_accepts_valid_candidate() {
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        match validate_pvp_hit(&base_pvp_input(1), &mut dedupe) {
            PvpVerdict::Accepted { clamped_damage } => assert_eq!(clamped_damage, 20.0),
            PvpVerdict::Rejected(reason) => panic!("expected accept, got {reason}"),
        }
    }

    #[test]
    fn validate_pvp_hit_duplicate_request_rejected_and_never_grants_twice() {
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let input = base_pvp_input(2);
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Accepted { .. }
        ));
        // Reliable retransmit of the SAME (attacker_id, request_id) → rejected, not a second grant.
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Rejected("duplicate")
        ));
    }

    #[test]
    fn validate_pvp_hit_rejects_self_hit() {
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let mut input = base_pvp_input(3);
        input.victim_id = input.attacker_id;
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Rejected("self_hit")
        ));
    }

    #[test]
    fn validate_pvp_hit_rejects_attacker_missing() {
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let mut input = base_pvp_input(4);
        input.attacker_known = false;
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Rejected("attacker_missing")
        ));
    }

    #[test]
    fn validate_pvp_hit_rejects_victim_missing() {
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let mut input = base_pvp_input(5);
        input.victim_known = false;
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Rejected("victim_missing")
        ));
    }

    #[test]
    fn validate_pvp_hit_rejects_victim_dead() {
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let mut input = base_pvp_input(6);
        input.victim_dead = true;
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Rejected("victim_dead")
        ));
    }

    #[test]
    fn validate_pvp_hit_rejects_victim_invulnerable() {
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let mut input = base_pvp_input(7);
        input.victim_invuln = true;
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Rejected("victim_invulnerable")
        ));
    }

    #[test]
    fn validate_pvp_hit_rejects_invalid_weapon() {
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let mut input = base_pvp_input(8);
        input.weapon_id = 0; // STP's "no item" sentinel — always rejected
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Rejected("invalid_weapon")
        ));

        let mut dedupe2 = BoundedDedupeSet::with_capacity(64);
        let mut input2 = base_pvp_input(9);
        input2.weapon_id = 999_999; // not one of the 7 real STP weapon ids
        assert!(matches!(
            validate_pvp_hit(&input2, &mut dedupe2),
            PvpVerdict::Rejected("invalid_weapon")
        ));
    }

    #[test]
    fn validate_pvp_hit_rejects_invalid_damage_and_clamps_overcap() {
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let mut input = base_pvp_input(10);
        input.damage = 0.0;
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Rejected("invalid_damage")
        ));

        let mut dedupe2 = BoundedDedupeSet::with_capacity(64);
        let mut input2 = base_pvp_input(11);
        input2.damage = f32::NAN;
        assert!(matches!(
            validate_pvp_hit(&input2, &mut dedupe2),
            PvpVerdict::Rejected("invalid_damage")
        ));

        // Over the weapon's cap → clamped, NOT rejected — ADR-029: "el host clamp/reject"
        // (PvpDamageGrant.damage docs: "ya validado/clampado por host"). Checked for both a
        // firearm (Marlin 336, cap 45) and a melee (Hunting Axe, cap 25) so both categories
        // of the real allowlist are represented.
        let mut dedupe3 = BoundedDedupeSet::with_capacity(64);
        let mut input3 = base_pvp_input(12); // Marlin 336 (firearm), max_damage=45
        input3.damage = 9999.0;
        match validate_pvp_hit(&input3, &mut dedupe3) {
            PvpVerdict::Accepted { clamped_damage } => assert_eq!(clamped_damage, 45.0),
            PvpVerdict::Rejected(reason) => panic!("expected clamp, got rejected: {reason}"),
        }

        let mut dedupe4 = BoundedDedupeSet::with_capacity(64);
        let mut input4 = base_pvp_input(13);
        input4.weapon_id = 2211292; // STP_Hunting Axe (melee), max_damage=25
        input4.victim_pos = Vec3::new(0.0, 1.8, 2.0); // within the axe's 2.5 m range
        input4.damage = 9999.0;
        match validate_pvp_hit(&input4, &mut dedupe4) {
            PvpVerdict::Accepted { clamped_damage } => assert_eq!(clamped_damage, 25.0),
            PvpVerdict::Rejected(reason) => panic!("expected clamp, got rejected: {reason}"),
        }
    }

    #[test]
    fn validate_pvp_hit_rejects_too_far() {
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let mut input = base_pvp_input(13);
        input.victim_pos = Vec3::new(0.0, 1.8, 500.0); // beyond the Marlin 336's 100 m range
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Rejected("too_far")
        ));
    }

    #[test]
    fn validate_pvp_hit_too_far_uses_3d_distance_with_real_y() {
        // ADR-026 (enmienda 2026-07-06): with the client's real Y now relayed (no longer
        // flattened), too_far stays 3D on purpose — a melee attacker at the same XZ but a
        // layer above (ΔY=4 m > the axe's 2.5 m range) must be rejected, where a 2D check
        // would have accepted a hit through the ceiling.
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let mut input = base_pvp_input(40);
        input.weapon_id = 2211292; // STP_Hunting Axe (melee), max_range=2.5
        input.attacker_pos = Vec3::new(0.0, 1.8, 0.0);
        input.victim_pos = Vec3::new(0.0, 5.8, 0.0); // same XZ, 4 m above (other layer)
        assert!(matches!(
            validate_pvp_hit(&input, &mut dedupe),
            PvpVerdict::Rejected("too_far")
        ));

        // A modest real-jump ΔY within range still lands: 3D distance ≈ 1.9 m ≤ 2.5 m.
        let mut dedupe2 = BoundedDedupeSet::with_capacity(64);
        let mut input2 = base_pvp_input(41);
        input2.weapon_id = 2211292;
        input2.attacker_pos = Vec3::new(0.0, 2.9, 0.0); // attacker mid-jump (+1.1 m)
        input2.victim_pos = Vec3::new(1.5, 1.8, 0.0);
        assert!(matches!(
            validate_pvp_hit(&input2, &mut dedupe2),
            PvpVerdict::Accepted { .. }
        ));
    }

    #[test]
    fn validate_pvp_hit_accepts_all_seven_real_weapon_ids() {
        // Each of the 7 real STP weapon ids must clear the invalid_weapon gate (the rest of
        // the flow is covered by the other tests — here we only assert the allowlist knows
        // them). victim_pos is kept point-blank so a short-range melee never trips too_far.
        let real_ids: [i32; 7] = [
            9692212,     // STP_Marlin 336
            -7892144,    // STP_Wooden Bow
            -1198406010, // STP_Bone Club
            2211292,     // STP_Hunting Axe
            -9575342,    // STP_Hunting Knife
            -1159981804, // STP_Steel Pickaxe
            5085425,     // STP_Stone Spear
        ];
        // -52379 (STP_Wooden Spear) is the 8th; kept separate only to name every id explicitly.
        let all_ids: Vec<i32> = real_ids
            .iter()
            .copied()
            .chain(std::iter::once(-52379))
            .collect();

        for (i, id) in all_ids.iter().enumerate() {
            let mut dedupe = BoundedDedupeSet::with_capacity(8);
            let mut input = base_pvp_input(i as u64);
            input.weapon_id = *id;
            input.victim_pos = Vec3::new(0.0, 1.8, 1.5); // within every weapon's min range
            input.damage = 5.0; // under every weapon's cap → no clamp noise
            match validate_pvp_hit(&input, &mut dedupe) {
                PvpVerdict::Accepted { .. } => {}
                PvpVerdict::Rejected(reason) => {
                    panic!("real weapon id {id} was rejected: {reason}")
                }
            }
        }
    }

    // ADR-028 amendment (world chests): host gate, request_id dedupe (reused
    // processed_interactions set), and the post-E3 empty-loot rule, in one pass.
    #[test]
    fn spawn_world_chest_gates_dedupes_and_seeds() {
        use crate::world::corpse::CorpseStack;

        let mut world = World::new(42);
        let mut processed = HashSet::new();
        let pos = Vec3::new(10.0, 1.8, 20.0);
        let loot = || {
            vec![CorpseStack {
                item_id: -5498592,
                quantity: 2,
            }]
        };

        // Non-host never seeds (joiners mirror via CorpseList instead).
        assert_eq!(
            handle_spawn_world_chest(&mut world, false, 1, 1, pos, loot(), &mut processed),
            Err("not_host")
        );
        assert!(world.corpses.is_empty());

        // Host seeds once; the entry is flagged as a chest.
        let id = handle_spawn_world_chest(&mut world, true, 1, 1, pos, loot(), &mut processed)
            .expect("first seed must succeed");
        assert!(world.corpses[&id].is_chest);

        // Same (player, request_id) re-sent → duplicate, nothing new seeded.
        assert_eq!(
            handle_spawn_world_chest(&mut world, true, 1, 1, pos, loot(), &mut processed),
            Err("duplicate")
        );
        assert_eq!(world.corpses.len(), 1);

        // Fresh request_id but empty loot → skipped (immortal-empty-container rule).
        assert_eq!(
            handle_spawn_world_chest(&mut world, true, 1, 2, pos, vec![], &mut processed),
            Err("empty_loot")
        );
        assert_eq!(world.corpses.len(), 1);
    }

    #[test]
    fn consumable_spec_resolves_all_seven_real_item_ids() {
        // Each of the 7 real STP consumable ids must resolve to a spec (ADR-030 allowlist).
        let real_ids: [i32; 7] = [
            -5498592, // STP_Apple
            1045632,  // STP_Cooked Meat
            -7862085, // STP_Energy Bar
            6285896,  // STP_Large Food Can
            -7580928, // STP_Small Food Can
            7983286,  // STP_Water Bottle
            -7174886, // STP_Antibiotics
        ];
        for id in real_ids {
            assert!(
                consumable_spec(id).is_some(),
                "real consumable id {id} was not found in the allowlist"
            );
        }
    }

    #[test]
    fn consumable_spec_rejects_unknown_id() {
        assert!(consumable_spec(0).is_none());
        assert!(consumable_spec(9692212).is_none()); // a real weapon id, not a consumable
        assert!(consumable_spec(123456789).is_none());
    }

    #[test]
    fn apply_pvp_damage_grant_applies_once_and_dedupes_retransmit() {
        let mut stats = crate::player::stats::PlayerStats::default();
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let health_before = stats.health;

        let result = apply_pvp_damage_grant(&mut stats, &mut dedupe, 1, 100, 30.0, 0);
        assert_eq!(result, Ok(health_before - 30.0));

        // Retransmitted grant (same attacker_id + request_id) → deduped, health unchanged.
        let dup = apply_pvp_damage_grant(&mut stats, &mut dedupe, 1, 100, 30.0, 0);
        assert_eq!(dup, Err("duplicate"));
        assert_eq!(stats.health, health_before - 30.0);
    }

    #[test]
    fn apply_pvp_damage_grant_blocks_while_invulnerable_then_applies_after() {
        let mut stats = crate::player::stats::PlayerStats {
            invuln_until_tick: 500,
            ..Default::default()
        };
        let mut dedupe = BoundedDedupeSet::with_capacity(64);
        let health_before = stats.health;

        let blocked = apply_pvp_damage_grant(&mut stats, &mut dedupe, 1, 200, 30.0, 100);
        assert_eq!(blocked, Err("victim_invulnerable"));
        assert_eq!(
            stats.health, health_before,
            "a blocked grant must not touch health"
        );

        // Past the invuln window (tick >= invuln_until_tick), a fresh request_id applies.
        let applied = apply_pvp_damage_grant(&mut stats, &mut dedupe, 1, 201, 30.0, 600);
        assert_eq!(applied, Ok(health_before - 30.0));
    }

    // ── ADR-031 bed respawn ──

    // A clean, flat, all-walkable chunk at `pos` so resolve_safe_spawn accepts a cell there.
    fn insert_clean_flat_chunk(world: &mut crate::world::World, pos: (i32, i32)) {
        use crate::world::chunk::{CELL_WALKABLE, EDGE_KIND_OPEN, FLOOR_FLAT, LAYOUT_GRID_SIZE};
        let mut chunk = crate::world::generator::generate_chunk_layer(1, pos, 0);
        let g = LAYOUT_GRID_SIZE as usize;
        chunk.layout.cells = vec![CELL_WALKABLE; g * g];
        chunk.layout.edges_v = vec![EDGE_KIND_OPEN; (g + 1) * g];
        chunk.layout.edges_h = vec![EDGE_KIND_OPEN; g * (g + 1)];
        chunk.layout.floor_profile = FLOOR_FLAT;
        chunk.layout.vertical_flags = 0;
        let key = chunk.key();
        world.chunks.insert(key, chunk);
    }

    #[test]
    fn resolve_respawn_without_bed_uses_fixed_starter() {
        let mut world = crate::world::World::new(1);
        let res = resolve_respawn(&mut world, None, 1);
        assert_eq!(
            res.chunk,
            (0, 0),
            "no bed → the fixed starter spawn (chunk 0,0)"
        );
    }

    #[test]
    fn resolve_respawn_prefers_a_placed_bed() {
        // A bed far from the origin, on a clean flat chunk: respawn must land at the bed, not (0,0).
        let mut world = crate::world::World::new(1);
        insert_clean_flat_chunk(&mut world, (10, 10));
        let bed = Vec3::new(10.0 * CHUNK_SIZE + 25.0, 1.8, 10.0 * CHUNK_SIZE + 25.0);
        let res = resolve_respawn(&mut world, Some(bed), 1);
        assert_eq!(
            res.chunk,
            (10, 10),
            "a bed must pull the respawn to the bed's chunk, not (0,0)"
        );
        assert!(
            (res.position.x - bed.x).abs() < CHUNK_SIZE
                && (res.position.z - bed.z).abs() < CHUNK_SIZE,
            "respawn should land in the bed's chunk near the bed, got {:?}",
            res.position
        );
    }

    // NOTE: the trust-the-bed FALLBACK (resolve→Repaired then bed used) is covered deterministically
    // by collision::tests::try_bed_spawn_recovers_where_resolve_safe_spawn_would_repair. It cannot be
    // forced through resolve_respawn here because update_ownership generates procedural neighbours that
    // may themselves offer a safe cell (avoiding the Repaired fallback) — non-deterministic.

    #[test]
    fn respawn_point_last_placed_wins() {
        // ADR-031 "last placed wins": each Sleeping Bag placement overwrites the single slot.
        let mut p = Player::new(1, "t");
        p.respawn_point = Some(Vec3::new(10.0, 1.8, 10.0));
        p.respawn_point = Some(Vec3::new(500.0, 1.8, 500.0));
        assert_eq!(p.respawn_point, Some(Vec3::new(500.0, 1.8, 500.0)));
    }

    #[test]
    fn bounded_dedupe_set_evicts_oldest_past_capacity() {
        let mut dedupe: BoundedDedupeSet<(u32, u64)> = BoundedDedupeSet::with_capacity(2);
        assert!(dedupe.insert((1, 1)));
        assert!(dedupe.insert((1, 2)));
        // Still within the 2-entry window — both remain deduped (no eviction yet).
        assert!(!dedupe.insert((1, 1)));
        assert!(!dedupe.insert((1, 2)));

        // A third entry exceeds capacity → evicts the OLDEST, (1,1). (1,2)/(1,3) stay.
        assert!(dedupe.insert((1, 3)));
        assert!(
            dedupe.insert((1, 1)),
            "evicted entry must be insertable again"
        );
        assert!(
            !dedupe.insert((1, 3)),
            "not-yet-evicted entry must stay deduped"
        );
    }

    // ── ADR-037: stp_demolish ───────────────────────────────────────────────────

    /// The headline behaviour AND the trap: freeing the pose cell. Without the release, placing,
    /// cancelling and re-placing on the same socket is impossible for the rest of the session and
    /// the only trace is a silent `stp_place_cell_taken`.
    #[tokio::test]
    async fn stp_demolish_retires_the_piece_and_frees_its_pose_cell() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let position = [10.0, 0.0, 20.0];
        let rotation = 90.0;

        process_stp_place(1, 111, position, rotation, 0, true, &mut net);
        assert_eq!(net.stp_buildings.len(), 1);
        let id = net.stp_buildings[0].id;
        assert!(
            net.occupied_stp_cells
                .contains(&stp_pose_cell(position, rotation)),
            "a group piece must claim its pose cell on placement"
        );

        process_stp_demolish(500, id, &mut net);

        assert!(net.stp_buildings.is_empty(), "the piece must be retired");
        assert!(
            !net.occupied_stp_cells
                .contains(&stp_pose_cell(position, rotation)),
            "the pose cell must be released, or the slot is bricked for the session"
        );

        // The real proof: the same socket accepts a new piece again.
        process_stp_place(2, 111, position, rotation, 0, true, &mut net);
        assert_eq!(
            net.stp_buildings.len(),
            1,
            "re-placing on the freed cell must be accepted"
        );
    }

    /// The reliable channel has a known open infinite-retransmit bug (STATE.md), so the same
    /// request WILL arrive twice in production. A second delivery must not eat a second piece.
    #[tokio::test]
    async fn stp_demolish_dedupes_under_retransmit() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        process_stp_place(1, 111, [0.0, 0.0, 0.0], 0.0, 0, false, &mut net);
        process_stp_place(2, 111, [50.0, 0.0, 50.0], 0.0, 0, false, &mut net);
        let first = net.stp_buildings[0].id;

        process_stp_demolish(900, first, &mut net);
        assert_eq!(net.stp_buildings.len(), 1);

        // Same demolish_id again: must be dropped before it can touch the survivor.
        process_stp_demolish(900, net.stp_buildings[0].id, &mut net);
        assert_eq!(
            net.stp_buildings.len(),
            1,
            "a retransmitted demolish must not retire a second piece"
        );
    }

    #[tokio::test]
    async fn stp_demolish_of_unknown_building_is_ignored() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        process_stp_place(1, 111, [0.0, 0.0, 0.0], 0.0, 0, false, &mut net);

        // Two clients cancelling the same piece in one window: the loser finds it already gone.
        process_stp_demolish(901, 0xDEAD_BEEF, &mut net);

        assert_eq!(
            net.stp_buildings.len(),
            1,
            "an unknown building id must be a no-op, not a panic or a wrong removal"
        );
    }

    /// A free piece never claimed a cell (`is_group` gates the insert in process_stp_place), so
    /// demolishing one must not reach into `occupied_stp_cells` and unblock a cell a DIFFERENT,
    /// still-standing group piece is holding at the same quantized pose.
    #[tokio::test]
    async fn stp_demolish_of_a_standalone_piece_leaves_pose_cells_alone() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let position = [30.0, 0.0, 30.0];

        process_stp_place(1, 111, position, 0.0, 0, true, &mut net); // group piece: claims the cell
        process_stp_place(2, 222, position, 0.0, 0, false, &mut net); // free piece: claims nothing
        let free_id = net.stp_buildings[1].id;

        process_stp_demolish(902, free_id, &mut net);

        assert_eq!(net.stp_buildings.len(), 1);
        assert!(
            net.occupied_stp_cells
                .contains(&stp_pose_cell(position, 0.0)),
            "the group piece still standing there must keep its cell"
        );
    }

    /// ADR-031's follow-up, closed by ADR-037: cancelling the bed that set the respawn point must
    /// clear it. Goes through handle_action because that is where `player` is in scope.
    #[tokio::test]
    async fn stp_demolish_of_the_bed_clears_the_respawn_point() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut world = World::new(42);
        let mut player = Player::new(1, "Host");
        let (tx, _rx) = broadcast::channel(16);
        let mut processed: HashSet<(u16, u64)> = HashSet::new();

        let bed_position = [12.0, 0.0, 34.0];
        process_stp_place(1, BED_DEF_ID, bed_position, 0.0, 0, false, &mut net);
        let bed_id = net.stp_buildings[0].id;
        player.respawn_point = Some(Vec3::from_array(bed_position));

        let action = crate::ipc::PlayerAction {
            action_type: "stp_demolish".into(),
            data: serde_json::json!({ "demolish_id": 903, "building_id": bed_id }),
        };
        handle_action(
            &action,
            &mut player,
            &mut world,
            &mut net,
            &tx,
            &mut processed,
            0,
        )
        .await;

        assert!(
            player.respawn_point.is_none(),
            "cancelling the bed the respawn point came from must clear it"
        );
        assert!(net.stp_buildings.is_empty());
    }

    /// "Last placed wins" (ADR-031) means the point can belong to a DIFFERENT bed that is still
    /// standing. Cancelling an unrelated one must leave it alone.
    #[tokio::test]
    async fn stp_demolish_of_another_bed_keeps_the_respawn_point() {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let mut world = World::new(42);
        let mut player = Player::new(1, "Host");
        let (tx, _rx) = broadcast::channel(16);
        let mut processed: HashSet<(u16, u64)> = HashSet::new();

        let live_bed = [12.0, 0.0, 34.0];
        let doomed_bed = [80.0, 0.0, 90.0];
        process_stp_place(1, BED_DEF_ID, doomed_bed, 0.0, 0, false, &mut net);
        let doomed_id = net.stp_buildings[0].id;
        player.respawn_point = Some(Vec3::from_array(live_bed));

        let action = crate::ipc::PlayerAction {
            action_type: "stp_demolish".into(),
            data: serde_json::json!({ "demolish_id": 904, "building_id": doomed_id }),
        };
        handle_action(
            &action,
            &mut player,
            &mut world,
            &mut net,
            &tx,
            &mut processed,
            0,
        )
        .await;

        assert_eq!(
            player.respawn_point,
            Some(Vec3::from_array(live_bed)),
            "a bed that is not the one the point came from must not clear it"
        );
    }
}
