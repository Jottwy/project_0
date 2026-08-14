//! Authoritative game loop. Runs at 60hz, processes IPC input from Unity,
//! simulates local state (world, entities, stats), manages P2P networking,
//! and streams `WorldState` back at 10hz. See ARCHITECTURE_V1.md §6.1.

use std::collections::{HashMap, HashSet};
use std::time::{Duration, Instant};

use log::{debug, error, info, warn};
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

/// ADR-016 — the robapieles AI. It lives in its own (private) module because it is a whole system
/// and not a helper of this loop: `PhantomDriver` touches no local of `run`, takes everything it
/// needs as an argument and returns `&[PhantomAttack]`. What follows is the ENTIRE surface the loop
/// consumes from it.
mod phantom;
use phantom::{
    choose_victim_name_for, phantom_attack_kind_name, sanitize_noise, PhantomAttackKind,
    PhantomDriver, PHANTOM_INITIAL_HEADING,
};

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
///
/// This invisibility is also load-bearing elsewhere: the entity-AI gate below (search
/// `net.is_host` near `ENTITY_TICK_EVERY`) freezes legacy PvE entities for a joiner in chunks far
/// from the host, and that's only harmless BECAUSE they're invisible and damage-free. If this
/// flag flips or `EntityRenderer` gets wired in, re-check that gate before shipping either.
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
/// ADR-045 Fase 2 fix: how long `apply_movement` is suppressed after `session_restored` is
/// emitted. Mirrors the client's `AuthoritativePoseApplier.SnapWindow` (0.35s) — the round trip
/// the event needs to reach the client and for the client to actually apply it, during which its
/// reported position is still the pre-restore one and must not be trusted. A dedicated constant,
/// NOT the `TP_WATCH_WINDOW_TICKS` local inside `apply_client_authoritative_move` (that one is
/// explicitly marked TEMP DIAG / removable and shared with player_died/player_respawned, which
/// this fix does not touch).
const RESTORE_SNAP_SUPPRESS_TICKS: u64 = (0.35 * TICK_HZ as f64) as u64;

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

/// ADR-032: where the host reads/writes its world save. `SAVE_PATH` env overrides; default is
/// `./saves/world_{seed}.json`.
fn resolve_save_path(seed: u64) -> std::path::PathBuf {
    std::env::var("SAVE_PATH")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|_| std::path::PathBuf::from(format!("./saves/world_{seed}.json")))
}

/// P0-3: `true` when a loaded save's `world_seed` disagrees with the launch-time WORLD_SEED.
/// Extracted so the caller's fatal-exit decision is testable without actually exiting the
/// process — same reason `resolve_phantom_density_scale` above is its own function.
fn save_world_seed_conflicts(
    save: &crate::persistence::save::SaveFile,
    launch_world_seed: u64,
) -> bool {
    save.world_seed != launch_world_seed
}

/// P0-2: same precedent as `world_seed`'s adoption below — a loaded save wins over the
/// launch-time env, with a warn when they differ. Pure so it's testable without driving the
/// whole loop (see the tick-gate note on `broadcast_chunk_states` from P0-1 for why that matters
/// here: nothing in `run()` exercises this in isolation otherwise).
fn resolve_phantom_density_scale(
    launch_value: f32,
    loaded_save: Option<&crate::persistence::save::SaveFile>,
) -> f32 {
    let Some(save) = loaded_save else {
        return launch_value;
    };
    if save.phantom_density_scale != launch_value {
        warn!(
            "P0-2: save phantom_density_scale {} differs from launch PHANTOM_DENSITY_SCALE {}; adopting saved value",
            save.phantom_density_scale, launch_value
        );
    }
    save.phantom_density_scale
}

/// Metadatos a persistir en ESTE guardado: la fecha de creación original más el tiempo jugado
/// acumulado, al que se suma lo que lleva corriendo la sesión actual. `tick` arranca en 0 en
/// cada lanzamiento del proceso, así que es exactamente la duración de esta sesión.
fn save_meta_now(
    base: &crate::persistence::save::SaveMeta,
    tick: u64,
) -> crate::persistence::save::SaveMeta {
    crate::persistence::save::SaveMeta {
        created_at: base.created_at.clone(),
        play_time_seconds: base.play_time_seconds.saturating_add(tick / TICK_HZ),
    }
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

/// ADR-032 / ADR-045 Fase 2: apply a `PlayerSnapshot` onto a live `Player`. Extracted so it is the
/// SAME call whether the snapshot came from the world save's embedded `host_player` (ADR-032) or
/// from a per-player file (ADR-045 Fase 2) — calling it a second time with the player-file
/// snapshot after `hydrate_from_save` already called it with `host_player` is what makes "the
/// player file wins when it exists" true for free: it is simply the last `apply` winning, no
/// priority logic needed anywhere.
fn apply_player_snapshot(player: &mut Player, snap: crate::persistence::save::PlayerSnapshot) {
    player.stats = snap.stats;
    // `invuln_until_tick` is an ABSOLUTE tick of the game loop's own counter, which restarts at 0
    // on every process launch. Restoring it as-is would grant PvP invulnerability for as many
    // ticks as the session that saved it had been running (measured on a real save: 21716 ticks ≈
    // 6 min at 60 Hz; hours in a long session). Sanitized to 0: ADR-029's invulnerability protects
    // the instant of a respawn, it does not survive a backend restart by design.
    player.stats.invuln_until_tick = 0;
    player.position = snap.position;
    player.rotation = snap.rotation;
    player.inventory = snap.inventory;
    player.equipment = snap.equipment;
    player.held_item = snap.held_item;
    player.respawn_point = snap.respawn_point;
    player.pending_respawn_point = snap.pending_respawn_point;
    player.stp_inventory = snap.stp_inventory;
    player.inventory_v2 = snap.inventory_v2;
}

/// A snapshot saved while dead must NOT hydrate as-is. The death belongs to a session that no
/// longer exists: the client booting against this save has no DeathUI up, and the re-announced
/// `player_died` races a rig that hasn't spawned yet — the edge is lost and the player loads
/// frozen (the ADR-025 dead gate holds their pose) with no button and no way out. Loading a dead
/// save therefore IS the respawn: the same stats reset + bed/starter placement the
/// `respawn_request` handler runs, minus its events (nobody is dead-awaiting at boot; the
/// `session_restored` snap already carries the resolved position) and minus the PvP
/// invulnerability window (tick-relative, meaningless across a restart — see
/// `apply_player_snapshot`). Done at LOAD, not at save time, so saves that already contain a
/// dead player are healed too.
fn revive_if_dead_on_load(player: &mut Player, world: &mut World) {
    if !player.stats.is_dead() {
        return;
    }
    player.stats = crate::player::stats::PlayerStats::on_respawn();
    let res = resolve_respawn(world, player.respawn_point, player.id);
    player.position = res.position;
    world.update_ownership(player.position, player.id);
    info!(
        "MPTRACE step=RESPAWN event=dead_save_revived_on_load pos=({:.2},{:.2},{:.2})",
        player.position.x, player.position.y, player.position.z
    );
}

/// ADR-045 Fase 2 fix: whether `apply_movement` should be skipped this tick because a
/// `session_restored` snap is still in flight to the client. Extracted as a pure function so the
/// tick-boundary arithmetic (`tick < until`, not `<=`) is unit-testable without spinning up the
/// game loop.
fn movement_suppressed(tick: u64, suppressed_until: Option<u64>) -> bool {
    suppressed_until.is_some_and(|until| tick < until)
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

    // ADR-068: el índice por chunk se reconstruye aquí, descartando lo que ya no valide.
    let (sprays, dropped_sprays) = crate::world::spray::SprayStore::from_sprays(save.sprays);
    net.sprays = sprays;
    // Y el acuñador se re-siembra POR LA MISMA RAZÓN que los cuatro de abajo: sin esto, tras
    // cargar, la primera pintada de la sesión reacuña un id que ya existe en el almacén.
    let spray_id = net
        .sprays
        .all()
        .iter()
        .map(|s| s.id)
        .max()
        .unwrap_or(0)
        .saturating_add(1)
        .max(1);
    NEXT_SPRAY_ID.store(spray_id, std::sync::atomic::Ordering::Relaxed);
    if dropped_sprays > 0 {
        warn!("ADR-068: {dropped_sprays} pintadas del save descartadas (no validan o pasan del cap por chunk)");
    }

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
        apply_player_snapshot(player, p);
        revive_if_dead_on_load(player, world);
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
    // ADR-046 — the voice channel, separate from `to_clients` for the reason spelled out in
    // `ipc::server::run`: that one drops its oldest messages on overflow, events included.
    to_clients_voice: broadcast::Sender<ServerMessage>,
    mut net: NetworkManager,
    // ADR-045 fix: fires whenever THIS backend's own local Unity IPC connection ends — a real
    // quit, but ALSO a transient drop (e.g. an editor recompile bouncing the socket) that isn't
    // one. `save_and_shutdown` depends on Unity's `NetworkInitializer` successfully reaching
    // `IPCClient` before the socket closes — two independent `OnApplicationQuit` handlers on two
    // MonoBehaviours with no execution order between them, and `IPCClient`'s own teardown can
    // close the socket (and null its singleton) before `NetworkInitializer` gets a chance to ask
    // for a save. This is the backend's OWN, Unity-independent fallback: it always knows when its
    // local client is gone, without caring why. Firing on a transient drop too is accepted, not
    // overlooked — the write it triggers is the same idempotent atomic save the timer autosave
    // already performs at an arbitrary cadence, just early.
    mut local_disconnect_rx: mpsc::Receiver<()>,
) {
    let mut player = Player::new(net.local_id, &net.local_name);
    let mut world = World::new(net.world_seed);

    // ADR-032: host-only world persistence. Load BEFORE generating/spawning so a persisted seed
    // (and player position) win. Non-host backends never load/save — world state isn't
    // authoritative there (joiners adopt the host's world via WorldSync).
    let save_path = resolve_save_path(net.world_seed);

    // P0-3: exclusive lock on the world save, held for the whole process — host-only, same
    // reason loading/saving already are (a joiner never touches persistence, see the checkpoint
    // entry for why that's load-bearing here). Acquired BEFORE `load_or_fresh` so this backend
    // never even reads a save another live host might be mid-write on.
    let mut world_lock = if net.is_host {
        Some(
            crate::persistence::lock::open_for_locking(&save_path).unwrap_or_else(|e| {
                eprintln!(
                    "FATAL: cannot open world lock file for {} ({e}). Refusing to start.",
                    save_path.display()
                );
                error!(
                    "P0-3: could not open world lock file for {}: {e}",
                    save_path.display()
                );
                std::process::exit(1);
            }),
        )
    } else {
        None
    };
    let _world_lock_guard = world_lock
        .as_mut()
        .map(|lock| crate::persistence::lock::acquire_or_exit(lock, &save_path));

    // ADR-045 Fase 2: per-player save file. Unlike `world_lock` above this cannot be resolved at
    // this point — it needs `identity_key` (arrives async via the `set_identity` IPC action) AND
    // `world_seed` (already known for a host; a joiner only has it after the HandshakeAck) — so
    // resolution happens later, inside the tick loop, the first tick both are available.
    //
    // `_player_lock_guard` is `'static`: a plain `RwLockWriteGuard<'a, File>` borrows from an
    // `RwLock<File>` local, and the borrow checker cannot see that the tick loop's runtime guard
    // (`player_file_resolve_attempted`) makes the acquisition run at most once — it has to assume
    // ANY iteration could reassign that local, which would invalidate a guard already borrowed
    // from a PRIOR iteration. `Box::leak` sidesteps this honestly rather than fighting it: this
    // lock is genuinely meant to live for the rest of the process (exactly like `world_lock`,
    // which sidesteps the same problem by being acquired before any loop exists at all), so
    // leaking the tiny `RwLock<File>` once is the correct shape for that intent, not a workaround.
    //
    // Two-piece state, deliberately NOT collapsed to one flag:
    //  - `player_file_resolve_attempted`: tried exactly once — never retried, success or not (a
    //    contested lock is a standing fact about this identity_key+world_seed combo, not a
    //    transient one worth polling 60x/second forever).
    //  - `player_save_path`: `Some` ONLY on a successful lock acquisition — this, not the attempt
    //    flag, is what autosave/save_and_shutdown gate on, so a failed/contested resolution can
    //    never write to a path it does not hold the lock for.
    let mut player_file_resolve_attempted = false;
    let mut player_save_path: Option<std::path::PathBuf> = None;
    let mut _player_lock_guard: Option<fd_lock::RwLockWriteGuard<'static, std::fs::File>> = None;

    let mut session_name = net.local_name.clone();
    let mut loaded_save = if net.is_host {
        crate::persistence::save::load_or_fresh(&save_path)
    } else {
        None
    };
    if let Some(save) = &loaded_save {
        // P0-3: a mismatch used to be a warn + silent adopt. That degradation is exactly what
        // this task exists to close — two things disagreeing about which world this is must
        // refuse to start, not quietly merge into whichever one the code happened to load.
        if save_world_seed_conflicts(save, net.world_seed) {
            eprintln!(
                "FATAL: save at {} has world_seed={} but this launch requested WORLD_SEED={}. \
                 Refusing to start — set WORLD_SEED={} or use a different SAVE_PATH.",
                save_path.display(),
                save.world_seed,
                net.world_seed,
                save.world_seed
            );
            error!(
                "P0-3: save world_seed {} != launch WORLD_SEED {}; refusing to start",
                save.world_seed, net.world_seed
            );
            std::process::exit(1);
        }
        net.world_seed = save.world_seed;
        world = World::new(save.world_seed);
        session_name = save.session_name.clone();
    }
    net.phantom_density_scale =
        resolve_phantom_density_scale(net.phantom_density_scale, loaded_save.as_ref());
    // Continuidad de metadatos: sin esto `created_at` se re-estampa en cada guardado y
    // `play_time_seconds` se escribe siempre 0. Se captura ANTES de que `hydrate_from_save`
    // consuma el `SaveFile`.
    let save_meta_base = loaded_save
        .as_ref()
        .map(crate::persistence::save::SaveMeta::from_loaded)
        .unwrap_or_default();

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
    // P0-2: the value this backend would use if it hosts, resolved above (env, save-overridden).
    phantom_driver.density_scale = net.phantom_density_scale;
    // ADR-032 (snap de sesión restaurada): armed by the hydration branch below. The
    // "session_restored" event CANNOT be emitted at hydration time — Unity's IPC client hasn't
    // connected yet (broadcast to zero receivers = dropped) — so it is deferred until the first
    // PlayerInput proves the client is alive and subscribed. It reuses ONLY the applier's snap
    // mechanism (a position-carrying arming event, same shape as player_respawned) and is a
    // DISTINCT event type on purpose: RespawnRequester listens for player_respawned and would
    // force the native STP respawn chain (SetHealthSilent(0) + RestoreHealth) at boot.
    let mut pending_restore_snap = false;
    // ADR-045 Fase 2 fix: armed to `tick + RESTORE_SNAP_SUPPRESS_TICKS` at the SAME instant a
    // snapshot is hydrated (every site that sets `pending_restore_snap = true` also sets this,
    // in the same statement group) — NOT at emission time. The risk starts the moment the
    // position is overwritten in RAM: `apply_movement` can run LATER in that SAME tick (the
    // resolution block that hydrates sits above the movement-apply site in `run()`'s body), so
    // arming only when `session_restored` goes out (one tick later, at the earliest) would miss
    // that first, same-tick clobber — the event would then carry the ALREADY-clobbered position.
    // Until `tick` reaches this value, `apply_movement` is skipped: the client hasn't received
    // (or hasn't yet applied) the snap, so its reported position is still the pre-restore one.
    let mut movement_suppressed_until: Option<u64> = None;

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
            // ADR-045 Fase 2 fix: see the doc comment on `movement_suppressed_until` above —
            // `tick` is 0 here (before the loop's first iteration), so this protects ticks
            // 0..RESTORE_SNAP_SUPPRESS_TICKS in case input is already queued by the time the
            // loop starts.
            movement_suppressed_until = Some(tick + RESTORE_SNAP_SUPPRESS_TICKS);
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

    // ADR-046 — voice ingress counters. Throttled to one line every 2 s for the same reason
    // the WorldState trace is (`ipc/server.rs`): stdout is PIPED to Unity, so a per-frame log
    // at 25 Hz would back-pressure the very path it is measuring.
    let mut voice_frames_in: u64 = 0;
    let mut voice_bytes_in: u64 = 0;
    let mut next_voice_log = Instant::now();

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
                                &save_meta_now(&save_meta_base, tick),
                                &net.stp_items,
                                &net.stp_buildings,
                                &net.stp_carryables,
                                &net.stp_harvestables,
                                net.phantom_density_scale,
                                &net.sprays.all(),
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
                        // ADR-045 Fase 2: same graceful save-on-quit, extended to the per-player
                        // file — host AND joiner, whichever this backend is. No `net.is_host`
                        // gate, mirroring the autosave above.
                        if let (Some(path), Some(key)) = (&player_save_path, &player.identity_key) {
                            match crate::persistence::player_save::save_player(path, key, &player) {
                                Ok(()) => info!(
                                    "ADR-045: player save-on-shutdown written to {}",
                                    path.display()
                                ),
                                Err(e) => warn!("ADR-045: player save-on-shutdown failed: {e}"),
                            }
                        }
                        // ADR-056: tell the peers before vanishing. Last thing before the exit,
                        // after both saves, so a slow or failing send can never cost us the
                        // persistence this path exists for. Peers that miss it still notice on
                        // the 5 s heartbeat timeout — this only makes the common case immediate.
                        sync::broadcast_goodbye(&net, "clean_shutdown").await;
                        std::process::exit(0);
                    }
                    // ADR-045 Fase 1: the client's own identity key, session-transient (never
                    // part of PlayerSnapshot — it SELECTS which player file to load/write, see
                    // the per-tick resolution below). Resolved eagerly, sanitized on receipt —
                    // trust-the-client with a filesystem-safety net, same posture as every other
                    // client-reported action.
                    if action.action_type == "set_identity" {
                        let raw_key = json_str(&action.data, "key");
                        let key = crate::persistence::sanitize_player_key(raw_key, &player.name);
                        info!("ADR-045: identity resolved to key={key}");
                        player.identity_key = Some(key);
                        continue;
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
                    // ADR-068: las pintadas viajan CON el chunk. No se derivan del seed como
                    // `walls`/`room_zones` — son estado de jugador que el host posee —, así que
                    // esto es lo único de este mensaje que no es función pura de la coordenada.
                    let sprays = net.sprays.chunk((cx, cz, layer)).to_vec();
                    // Y si somos joiner, hay que PREGUNTAR: nuestro almacén arranca vacío al
                    // entrar, y sin esto un jugador que se une a un mundo pintado ve paredes
                    // limpias hasta que alguien pinte de nuevo delante de él. Una vez por chunk
                    // — Unity vuelve a pedir el mismo chunk cada vez que entra en streaming.
                    if !net.is_host && net.requested_spray_chunks.insert((cx, cz, layer)) {
                        let payload = crate::network::protocol::PacketPayload::SprayChunkRequest {
                            cx,
                            cz,
                            layer,
                        };
                        net.send_reliable(1, &payload).await;
                    }
                    let _ = to_clients.send(ServerMessage::ChunkData(GridChunkData {
                        cx,
                        cz,
                        layer,
                        walls,
                        room_zones,
                        sprays,
                    }));
                }
                ClientMessage::SprayPlace(req) => {
                    // ADR-068: el host valida y acuña; el jugador que pinta no decide nada.
                    process_spray_place(req, &player, &mut net, tick, &to_clients).await;
                }
                ClientMessage::Voice { seq, data } => {
                    // ADR-046 Fase 2 — our own microphone, on its way out.
                    voice_frames_in = voice_frames_in.wrapping_add(1);
                    voice_bytes_in = voice_bytes_in.wrapping_add(data.len() as u64);

                    // The dead do not speak. The client also stops capturing, but that half is
                    // the one a patched client can delete.
                    let mut sent_to = 0usize;
                    if !player.stats.is_dead() && !data.is_empty() {
                        // ADR-053: our own voice is stealable too — and on a solo session it is the
                        // ONLY voice there is, so without this the mimicry would never fire.
                        if net.is_host {
                            net.voice_echo.insert(net.local_id, data.clone());
                        }
                        let me = [player.position.x, player.position.y, player.position.z];
                        let payload = PacketPayload::VoiceFrame {
                            seq,
                            data: data.clone(),
                        };
                        if net.is_host {
                            // We ARE the relay: one copy per listener in earshot of us.
                            let dests: Vec<u16> = net
                                .peers
                                .values()
                                .filter(|p| !net.is_phantom(p.id) && !p.dead)
                                .filter(|p| {
                                    crate::network::sync::within_voice_range(me, p.position)
                                })
                                .map(|p| p.id)
                                .collect();
                            for dest in dests {
                                net.send_unreliable_to(dest, &payload).await;
                                sent_to += 1;
                            }
                        } else {
                            // A joiner's only real link is the host, which owns the decision of
                            // who else hears this. Sending to `peers` at large would spray
                            // datagrams at addresses a joiner cannot reach anyway.
                            //
                            // The pre-check is NOT the security filter (that one lives on the
                            // host) — it just stops us paying upstream for a frame nobody is
                            // close enough to receive. The roster carries peer POSITION, so we
                            // can answer "is anyone near me?" without asking.
                            let anyone_near = net.peers.values().any(|p| {
                                !net.is_phantom(p.id)
                                    && !p.dead
                                    && crate::network::sync::within_voice_range(me, p.position)
                            });
                            if anyone_near {
                                net.send_unreliable_to(1, &payload).await; // 1 = host
                                sent_to = 1;
                            }
                        }
                    }

                    let now = Instant::now();
                    if now >= next_voice_log {
                        next_voice_log = now + Duration::from_secs(2);
                        info!(
                            "MPTRACE step=V event=voice_frame_out seq={seq} bytes={} dests={sent_to} frames_total={voice_frames_in} bytes_total={voice_bytes_in}",
                            data.len()
                        );
                    }
                }
            }
        }

        // ADR-045 fix: the local Unity IPC client disconnected — save what `save_and_shutdown`
        // would have saved, without depending on Unity having sent it. See the doc comment on
        // `local_disconnect_rx` above. Mirrors `save_and_shutdown`'s own gates AND order exactly
        // (world save host-only, then player save whenever a path is resolved) but does NOT
        // `std::process::exit` — a lost local connection is not by itself a reason for this
        // backend to end its own process; Unity's `KillBackend` already force-kills it on its own
        // timeout regardless, and this is a safety net for that window, not a second shutdown
        // path. Placed AFTER the `ClientMessage` loop above on purpose: if Unity's own
        // `save_and_shutdown` request is queued in the SAME tick as the disconnect that follows
        // it (the common case — `IPCClient.Shutdown()` closes the socket shortly after sending),
        // that arm's `std::process::exit(0)` already ended the process by the time control would
        // reach here — this block never runs for that tick, so the two paths cannot both fire for
        // the same clean shutdown. It can still fire on a TRANSIENT local socket drop that is not
        // a real quit (e.g. an editor recompile bounces the connection) — harmless: same atomic
        // write the timer autosave already performs at an arbitrary cadence, just early.
        while local_disconnect_rx.try_recv().is_ok() {
            if net.is_host {
                match crate::persistence::save::save_world(
                    &save_path,
                    &session_name,
                    &world,
                    &player,
                    &save_meta_now(&save_meta_base, tick),
                    &net.stp_items,
                    &net.stp_buildings,
                    &net.stp_carryables,
                    &net.stp_harvestables,
                    net.phantom_density_scale,
                    &net.sprays.all(),
                ) {
                    Ok(()) => info!(
                        "ADR-032: world save on local IPC disconnect written to {}",
                        save_path.display()
                    ),
                    Err(e) => warn!("ADR-032: world save on local IPC disconnect failed: {e}"),
                }
            }
            if let (Some(path), Some(key)) = (&player_save_path, &player.identity_key) {
                match crate::persistence::player_save::save_player(path, key, &player) {
                    Ok(()) => info!(
                        "ADR-045: player save on local IPC disconnect written to {}",
                        path.display()
                    ),
                    Err(e) => warn!("ADR-045: player save on local IPC disconnect failed: {e}"),
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
            // ADR-045 Fase 3: inventory_v2 (container/slot/props) takes priority when the
            // client has ever sent the richer report_inventory shape; a save written under
            // Fases 1+2 only (or by a pre-Fase-3 client that never sends it) falls back to the
            // flat stp_inventory restore, UNCHANGED from before this fase existed — the two
            // are never merged, and neither being non-empty implies the other is.
            if !player.inventory_v2.is_empty() {
                info!(
                    "ADR-045: emitting inventory_restored v2 ({} stacks)",
                    player.inventory_v2.len()
                );
                let items: Vec<serde_json::Value> = player
                    .inventory_v2
                    .iter()
                    .map(|s| {
                        let props: Vec<serde_json::Value> = s
                            .props
                            .iter()
                            .map(|p| serde_json::json!({ "id": p.id, "value": p.value }))
                            .collect();
                        serde_json::json!({
                            "item_id": s.item_id,
                            "quantity": s.quantity,
                            "container": s.container,
                            "slot": s.slot,
                            "props": props,
                        })
                    })
                    .collect();
                let _ = to_clients.send(ServerMessage::Event(GameEvent {
                    event_type: "inventory_restored".into(),
                    data: serde_json::json!({ "items": items }),
                }));
            } else if !player.stp_inventory.is_empty() {
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
                &to_clients_voice,
                &mut processed_interactions,
                tick,
                player_save_path.as_deref(),
            )
            .await;
        }

        // ADR-045 Fase 2: resolve the per-player save file, exactly once, the first tick both
        // ingredients are available — `net.world_seed_known` (always true for a host; a joiner
        // gets it from the HandshakeAck this same `process_incoming()` may just have processed)
        // and `player.identity_key` (set by the `set_identity` handler above, whichever tick it
        // arrives on). `player_file_resolve_attempted` guards this from ever running twice,
        // success or not — see the doc comment on the locals above for why that is a SEPARATE
        // flag from `player_save_path`.
        if !player_file_resolve_attempted && net.world_seed_known {
            if let Some(key) = player.identity_key.clone() {
                player_file_resolve_attempted = true;
                let path =
                    crate::persistence::player_save::resolve_player_save_path(net.world_seed, &key);
                match crate::persistence::lock::open_for_locking(&path) {
                    Ok(lock) => {
                        // Leaked on purpose — see the doc comment on `_player_lock_guard` above.
                        let leaked: &'static mut fd_lock::RwLock<std::fs::File> =
                            Box::leak(Box::new(lock));
                        match crate::persistence::lock::try_acquire(leaked) {
                            Ok(guard) => {
                                _player_lock_guard = Some(guard);
                                if let Some(file) =
                                    crate::persistence::player_save::load_or_fresh(&path)
                                {
                                    apply_player_snapshot(&mut player, file.snapshot);
                                    revive_if_dead_on_load(&mut player, &mut world);
                                    pending_restore_snap = true;
                                    // ADR-045 Fase 2 fix: see the doc comment on
                                    // `movement_suppressed_until` above — armed HERE, not at
                                    // emission, because `apply_movement` can still run later in
                                    // this SAME tick.
                                    movement_suppressed_until =
                                        Some(tick + RESTORE_SNAP_SUPPRESS_TICKS);
                                    info!("ADR-045: player file hydrated from {}", path.display());
                                } else {
                                    info!(
                                        "ADR-045: no existing player file at {} — starting fresh",
                                        path.display()
                                    );
                                }
                                player_save_path = Some(path);
                            }
                            Err(e) => warn!(
                                "ADR-045: lock on {} held by another process ({e}) — this \
                                 session will not persist a player file this run (same \
                                 identity_key + world_seed already in use elsewhere). Playing \
                                 without save.",
                                path.display()
                            ),
                        }
                    }
                    Err(e) => warn!(
                        "ADR-045: could not open player lock file for {} ({e}) — playing \
                         without save.",
                        path.display()
                    ),
                }
            }
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
        //
        // ADR-060: el mundo ya NO llega en un paquete, sino a goteo — `!world.chunks.is_empty()`
        // se dispararía con el PRIMER chunk y resolvería el spawn sobre un mundo a medias
        // (`resolve_safe_spawn` buscaría celda segura entre los chunks que hubieran llegado).
        // La condición es la completitud del goteo: `WorldSyncEnd` recibido Y todos sus chunks
        // aplicados. El monolito deprecado la marca completa de una vez (`note_monolith`).
        if !spawn_resolved && net.real_peer_count() > 0 && net.world_sync_progress.is_complete() {
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
            // ADR-044: record the client-reported sustained-state bits (bit 0 aiming, bit 1
            // reloading) and the melee-swing counter. Cosmetic; relayed to peers, not validated,
            // and never an input to the hit validation of ADR-029.
            player.buttons = received_input.buttons;
            player.melee_seq = received_input.melee_seq;
            // ADR-049: record the client-reported carry state. THIS BLOCK IS PLAIN ASSIGNMENTS, NOT A
            // STRUCT LITERAL — omitting a line here compiles clean, passes the tests, and silently
            // relays 0 forever. Covered by the `peer.carry_def` test in network/mod.rs.
            player.carry_def = received_input.carry_def;
            player.carry_count = received_input.carry_count;
            // ADR-025 respawn-on-demand: while DEAD the server FREEZES the authoritative pose —
            // client-reported movement is ignored (same gating family as DEV_FREEZE_SURVIVAL /
            // take_damage). Any local client drift while dead is corrected by the applier's snap
            // on player_respawned. The ack does not advance (nothing was accepted).
            //
            // ADR-045 Fase 2 fix: same freeze, same "nothing accepted" semantics, while a
            // session_restored snap is in flight to the client (see `movement_suppressed_until`
            // above) — trusting the client's reported position during that window would
            // overwrite the just-restored one with wherever the client was BEFORE it received
            // the snap.
            if !player.stats.is_dead() && !movement_suppressed(tick, movement_suppressed_until) {
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
            // ADR-009 §4: the host owns all authoritative world state; a joiner only predicts
            // movement. Before this guard, `tick_entities`/`tick_respawns` ran unconditionally on
            // BOTH — the joiner simulated its own AI locally, the host's `broadcast_chunk_states`
            // (host-only since `sync.rs:473-477`, same bug on the send side) overwrote it via
            // `chunk.entities.clear()` + rebuild every 5hz, and the joiner's `tick_respawns`
            // minted ids from a `NEXT_ENTITY_ID` counter that restarts at 1 in every process —
            // guaranteed collisions with the host's ids. Net effect: 0.5s of visible AI drift
            // between overwrites (jitter) plus a live id-collision generator. Host-only closes
            // both. Cost accepted: legacy PvE entities freeze in chunks far from the host for the
            // joiner — currently invisible and harmless, see `ENTITY_DAMAGE_ENABLED` above; if
            // either flag there changes, re-read this comment before assuming AI still "just
            // works" for a joiner.
            if net.is_host {
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
                world.tick_respawns(entity_dt, net.local_id);
            }

            // ADR-016 slice 2: advance phantom peers (host-only). Each phantom walks and its
            // move is resolved via ADR-017 sim-only collision, so it respects walls/floor even
            // far from the host (where world.chunks is empty). Same 10 Hz as the pose relay.
            if net.is_host {
                // ADR-043: reconcile which of the world's robapieles are simulated BEFORE stepping
                // them, so one that just woke up gets a full tick instead of standing still for
                // 100 ms at the edge of view — the frame a player is most likely to be looking.
                phantom_driver.sync_population(&mut net, player.position, entity_dt);
                // ADR-047 D5: a noise reported this tick may wake sleepers near its SOURCE, which
                // is what makes ADR-041's long-distance travel reachable at all. Must run every
                // tick (not on the 1 Hz reconcile) because `step` drains the queue immediately.
                phantom_driver.wake_for_noises(&mut net);

                // ADR-070: advance the falling items. Runs here, in the host-only entity block,
                // because it is the same class of work as stepping a phantom and it feeds the same
                // 10 Hz roster relay — one simulated tick per replicated position.
                //
                // It BORROWS the phantom's grid cache instead of keeping its own, and that is the
                // point rather than a saving: the ADR-070 amendment records that the cache has to be
                // built with the same `rules_fn` as the creature's (ADR-033) or objects would fall
                // against a different world than the one that walks. Sharing the cache makes that
                // impossible to get wrong, and the chunks are already warm.
                if !net.settling_items.is_empty() {
                    let asleep = settle_items_tick(
                        &mut net.settling_items,
                        &mut net.stp_items,
                        &mut phantom_driver.grid_cache,
                        entity_dt,
                        SETTLE_SUBSTEPS,
                    );
                    for id in asleep {
                        mark_item_settled(&mut net, id);
                    }
                }
                // Copied out rather than borrowed so the driver is free again immediately — the
                // voice echoes below need it mutably, and an attack is two `Copy` words.
                let attacks: Vec<_> = phantom_driver
                    .step(
                        &mut net,
                        entity_dt,
                        player.position,
                        player.rotation,
                        player.crouch,
                        player.stats.is_dead(),
                        player.held_item,
                    )
                    .to_vec();

                // ADR-053 — IT SAYS YOUR OWN WORDS BACK AT YOU. The driver decides who and when;
                // this is the half that owns the sockets. `send_unreliable_as` stamps the frame
                // with the PHANTOM's id, which is the whole trick: the client already plays a
                // peer's voice at that peer's position, so the words come out of the figure down
                // the corridor with no client code at all.
                for (phantom_id, victim_id) in std::mem::take(&mut phantom_driver.voice_echoes) {
                    let Some(data) = net.voice_echo.get(&victim_id).cloned() else {
                        continue;
                    };
                    // Its OWN counter — see `voice_echo_seq`. Borrowing the attack-request id meant
                    // every echo of a solo session shipped with seq 0 and the client's jitter
                    // buffer dropped all but the first as duplicates.
                    net.voice_echo_seq = net.voice_echo_seq.wrapping_add(1);
                    let payload = PacketPayload::VoiceFrame {
                        seq: net.voice_echo_seq,
                        data,
                    };
                    for dest in crate::network::sync::voice_destinations(&net, phantom_id) {
                        net.send_unreliable_as(phantom_id, dest, &payload).await;
                    }
                    // …and the host's own player, who is not a peer and therefore not in that list.
                    if let Some(ph) = net.peers.get(&phantom_id) {
                        let me = [player.position.x, player.position.y, player.position.z];
                        if crate::network::sync::within_voice_range(me, ph.position)
                            && !player.stats.is_dead()
                        {
                            if let PacketPayload::VoiceFrame { seq, data } = &payload {
                                let _ = to_clients.send(ServerMessage::PeerVoice(
                                    crate::ipc::PeerVoice {
                                        peer_id: phantom_id,
                                        seq: *seq,
                                        data: data.clone(),
                                    },
                                ));
                            }
                        }
                    }
                }
                // ADR-047 — this loop ROUTES; it no longer assumes the victim. Each attack names
                // whose health it is for, and the only branch that applies damage is the one where
                // that victim IS this backend's own local player. ADR-025 makes the split
                // mandatory, not stylistic: a joiner's health lives in the joiner's own backend
                // and the host physically does not have it.
                //
                // The damage path stays SEPARATE from the pickup theater (ADR-016 invariant).
                //
                // ADR-043: several attackers per tick. Once a player is down, further attacks
                // AGAINST THAT SAME PLAYER in the tick are dropped — a corpse taking two more hits
                // would emit duplicate `phantom_kill`/`phantom_hit` for someone already dead, and
                // the client's death chain is not idempotent. ADR-047 scopes that skip to the
                // victim: the old code `break`-ed the WHOLE loop on the host's death, so a dead
                // host would have swallowed a blow aimed at a live joiner.
                for attack in attacks {
                    let victim_is_local = attack.victim == net.local_id;

                    if !victim_is_local {
                        // ADR-047 — the victim's health is owned by another backend (ADR-025), so
                        // the DECISION travels and the mutation stays home. Same authority split
                        // as ADR-029's PvP grant.
                        //
                        // The peer is checked HERE and not left to `send_reliable`, which returns
                        // silently when the peer is gone (network/mod.rs) — a blow swallowed
                        // without a word is exactly the failure mode that hid the original bug for
                        // so long. And it is DISCARDED, never redirected to the local player: that
                        // redirection IS the bug, and re-adding it as a "fallback" would rename it.
                        if !net.peers.contains_key(&attack.victim) {
                            warn!(
                                "MPTRACE step=PH_ATTACK event=phantom_attack_undeliverable victim_id={} kind={} reason=victim_has_no_channel",
                                attack.victim,
                                phantom_attack_kind_name(attack.kind)
                            );
                            continue;
                        }
                        // A peer already down does not get hit again: `dead` is relayed
                        // (ADR-028 post-E3), so the host can pre-filter without asking.
                        if net.peers.get(&attack.victim).is_some_and(|p| p.dead) {
                            info!(
                                "MPTRACE step=PH_ATTACK event=phantom_attack_skipped victim_id={} reason=victim_dead",
                                attack.victim
                            );
                            continue;
                        }

                        let request_id = net.next_phantom_attack_request_id;
                        net.next_phantom_attack_request_id =
                            net.next_phantom_attack_request_id.wrapping_add(1);
                        let (kind_code, damage, impulse) = match attack.kind {
                            PhantomAttackKind::Hit(dmg) => (0u8, dmg, [0.0, 0.0]),
                            PhantomAttackKind::Kill => (1u8, 0.0, [0.0, 0.0]),
                            PhantomAttackKind::Knockback(dx, dz) => (2u8, 0.0, [dx, dz]),
                            // ADR-050 point 9. The grab window rides `damage`, which is the only
                            // spare f32 in the payload — no layout change, per ADR-047's spare-kind
                            // clause. It is NOT damage and the victim side must not apply it as
                            // such; kind 3 has its own arm there.
                            PhantomAttackKind::GrabStart(window) => (3u8, window, [0.0, 0.0]),
                            PhantomAttackKind::GrabRelease => (4u8, 0.0, [0.0, 0.0]),
                        };
                        let grant = PacketPayload::PhantomAttackGrant {
                            request_id,
                            victim_id: attack.victim as u32,
                            kind: kind_code,
                            damage,
                            impulse,
                        };
                        info!(
                            "MPTRACE step=PH_ATTACK event=phantom_attack_granted victim_id={} kind={} request_id={}",
                            attack.victim,
                            phantom_attack_kind_name(attack.kind),
                            request_id
                        );
                        net.send_reliable(attack.victim, &grant).await;
                        continue;
                    }

                    if player.stats.is_dead() && !dev_invincible {
                        continue; // scoped to THIS victim (see above), not a whole-loop break
                    }

                    match attack.kind {
                        PhantomAttackKind::Kill => {
                            let death_pos = player.position.to_array();
                            if !dev_invincible {
                                player.stats.take_damage(100.0); // → is_dead → death/respawn
                            }
                            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                                event_type: "phantom_kill".into(),
                                data: serde_json::json!({ "pos": death_pos }),
                            }));
                        }
                        PhantomAttackKind::Hit(dmg) => {
                            if !dev_invincible {
                                player.stats.take_damage(dmg);
                            }
                            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                                event_type: "phantom_hit".into(),
                                data: serde_json::json!({ "damage": dmg }),
                            }));
                        }
                        PhantomAttackKind::Knockback(dx, dz) => {
                            // Client-only: it applies the impulse (SetVelocity). Mutating
                            // player.position here would be overwritten by the next
                            // client-authoritative input (ADR-009), so the backend only signals.
                            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                                event_type: "phantom_knockback".into(),
                                data: serde_json::json!({ "dx": dx, "dz": dz }),
                            }));
                        }
                        // ADR-050 point 9: NO health is touched by either of these. The grab is a
                        // window, not a blow — the kill that may follow arrives as a separate
                        // `Kill` from `tick_grab` once the timer expires.
                        PhantomAttackKind::GrabStart(window) => {
                            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                                event_type: "phantom_grab_start".into(),
                                data: serde_json::json!({ "window": window }),
                            }));
                        }
                        PhantomAttackKind::GrabRelease => {
                            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                                event_type: "phantom_grab_release".into(),
                                data: serde_json::json!({}),
                            }));
                        }
                    }
                }
            }
        }

        // Ownership is now handled per-chunk-boundary above; only teleportation
        // and other slow-tick work runs here.
        if tick.is_multiple_of(SLOW_TICK_EVERY) && (net.is_host || net.peer_count() == 0) {
            let outcomes = world.tick_teleportation(tick);
            for o in &outcomes {
                let _ = to_clients.send(ServerMessage::Event(o.event.clone()));
            }
            // Broadcast teleport events to peers. The seed comes straight from the
            // outcome: peers regenerate the chunk from it, so sending anything else
            // diverges their content from the owner's while the owner is still alive.
            for o in &outcomes {
                sync::broadcast_chunk_teleport(&net, o.old_pos, o.new_pos, o.new_seed).await;
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
                // ADR-071: `&mut` now, because each of these owns a send gate that it updates when
                // it decides a round goes out. They still run at 10 Hz — what changed is that a
                // roster nobody touched since the last round returns immediately.
                sync::broadcast_stp_items(&mut net).await;
                sync::broadcast_stp_buildings(&mut net).await;
                sync::broadcast_stp_carryables(&mut net).await;
                sync::broadcast_stp_harvestables(&mut net).await;
                // ADR-028 Fase E: full corpse roster (host-authoritative, self-healing).
                sync::broadcast_corpses(&mut net, &world).await;
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
                    &to_clients_voice,
                    &mut processed_interactions,
                    tick,
                    player_save_path.as_deref(),
                )
                .await;
            }
        }

        // Process reliable retransmits. ADR-062: agotar los reintentos desconecta al peer, así
        // que esto emite eventos por el mismo camino que el escaneo de timeouts.
        if tick.is_multiple_of(ENTITY_TICK_EVERY) {
            let retransmit_events = net.process_retransmits().await;
            for event in retransmit_events {
                handle_network_event(
                    event,
                    &mut player,
                    &mut world,
                    &mut net,
                    &to_clients,
                    &to_clients_voice,
                    &mut processed_interactions,
                    tick,
                    player_save_path.as_deref(),
                )
                .await;
            }
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
                &save_meta_now(&save_meta_base, tick),
                &net.stp_items,
                &net.stp_buildings,
                &net.stp_carryables,
                &net.stp_harvestables,
                net.phantom_density_scale,
                &net.sprays.all(),
            ) {
                Ok(()) => info!("ADR-032: autosave written to {}", save_path.display()),
                Err(e) => warn!("ADR-032: autosave failed: {e}"),
            }
        }

        // ADR-045 Fase 2: per-player autosave, same cadence as the world autosave above but
        // WITHOUT the `net.is_host` gate — unlike the world save, a joiner owns and writes its
        // own player file too. Gated on `player_save_path` alone: `None` covers both "not
        // resolved yet" and "resolution failed/lost the lock" (see the locals' doc comment) —
        // either way, nothing to write to.
        if tick > 0 && tick.is_multiple_of(AUTOSAVE_EVERY) {
            if let (Some(path), Some(key)) = (&player_save_path, &player.identity_key) {
                match crate::persistence::player_save::save_player(path, key, &player) {
                    Ok(()) => info!("ADR-045: player autosave written to {}", path.display()),
                    Err(e) => warn!("ADR-045: player autosave failed: {e}"),
                }
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

// El octavo parámetro es el canal de voz de ADR-046, que existe precisamente para NO ir
// mezclado con `to_clients`. Agruparlos en un struct desharía esa separación en la firma justo
// donde importa que se lea. Mismo criterio que los `too_many_arguments` ya presentes en
// `persistence/save.rs` y `grid_gen/generator.rs`.
#[allow(clippy::too_many_arguments)]
async fn handle_network_event(
    event: NetworkEvent,
    player: &mut Player,
    world: &mut World,
    net: &mut NetworkManager,
    to_clients: &broadcast::Sender<ServerMessage>,
    // ADR-046 — voice out to Unity. Deliberately NOT `to_clients`: that channel evicts its
    // OLDEST messages on overflow, `player_died` among them.
    to_clients_voice: &broadcast::Sender<ServerMessage>,
    processed_interactions: &mut HashSet<(u16, u64)>,
    tick: u64,
    // ADR-056: needed by the host-departure arm below, which persists the player file before
    // announcing the end of the session. `None` means no file was ever resolved (no identity, or
    // the lock was contested) — exactly the same gate every other save site in this loop uses.
    player_save_path: Option<&std::path::Path>,
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

            // ADR-056: the host leaving ends the session — there is no host migration, so
            // whatever is left of the mesh is a world that cannot advance (chunk displacement is
            // gated on `is_host || peer_count() == 0`, and with the mesh still alive peer_count
            // never reaches 0). Compared against `host_peer_id` rather than the literal `1` the
            // request paths use, so this does not become the sixteenth hardcoded host id.
            if net.host_peer_id == Some(id) {
                info!("ADR-056: host {id} left ({reason}) — ending the session");

                // Persist before announcing. Same gates and same order as `save_and_shutdown`:
                // if Unity tears down promptly on the event below, this is the write that makes
                // the difference between keeping this session's progress and losing it back to
                // the last ~3-minute autosave.
                if let (Some(path), Some(key)) = (player_save_path, &player.identity_key) {
                    match crate::persistence::player_save::save_player(path, key, player) {
                        Ok(()) => info!(
                            "ADR-056: player saved on host departure to {}",
                            path.display()
                        ),
                        Err(e) => warn!("ADR-056: player save on host departure failed: {e}"),
                    }
                }

                // Unity owns the teardown: it answers this by calling `NetworkInitializer
                // .Shutdown()`, which comes back as `save_and_shutdown` and exits this process.
                // Deliberately NOT `std::process::exit` here — two owners of the shutdown is the
                // race the ADR-045 amendment already had to untangle once.
                let _ = to_clients.send(ServerMessage::Event(GameEvent {
                    event_type: "session_ended".into(),
                    data: serde_json::json!({ "reason": reason }),
                }));
            }
        }

        NetworkEvent::ConnectRejected { reason } => {
            // Corrección adosada a ADR-060: reutiliza el `session_ended` que Unity YA maneja
            // (ADR-056) en vez de una UI nueva — nunca llegamos a unirnos a ningún mundo, así
            // que no hay nada que persistir aquí, a diferencia del brazo `PeerDisconnected`
            // de arriba.
            info!("Connect rejected by host: {reason}");
            let _ = to_clients.send(ServerMessage::Event(GameEvent {
                event_type: "session_ended".into(),
                data: serde_json::json!({ "reason": reason }),
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
            buttons,
            melee_seq,
            vocal_seq,
            vocal_kind,
            carry_def,
            carry_count,
        } => {
            debug!(
                "Remote player received: id={}, pos=({:.2}, {:.2}, {:.2}), rot={:.1}, anim={}, crouch={}, pitch={}, equipment={:?}, held_item={}, hit_seq={}, dead={}, revealed={}, light_on={}, fire_seq={}, buttons={:#06b}, melee_seq={}, vocal_seq={}, vocal_kind={}, carry={}x{}",
                id, position[0], position[1], position[2], rotation, animation, crouch, pitch, equipment, held_item, hit_seq, dead, revealed, light_on, fire_seq, buttons, melee_seq, vocal_seq, vocal_kind, carry_count, carry_def
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
            // ADR-060: el monolito deprecado aplica el mundo entero — completo por
            // construcción, para que el gate de spawn abra igual que con un goteo.
            net.world_sync_progress.note_monolith(world_revision);
        }

        // ADR-060: un chunk del goteo. Upsert inmediato (mismo camino que ChunkTransfer);
        // la completitud se cuenta por clave (pos, layer) — los duplicados por retransmisión
        // colapsan en el set y un rezagado de una revision vieja se ignora.
        NetworkEvent::WorldSyncChunkReceived {
            world_revision,
            data,
        } => {
            let (pos, layer) = (data.pos, data.layer);
            world.apply_chunk_transfer(&data, net.local_id);
            net.world_sync_progress
                .note_chunk(world_revision, pos, layer);
        }

        NetworkEvent::WorldSyncEndReceived {
            world_revision,
            chunk_count,
        } => {
            net.world_sync_progress
                .note_end(world_revision, chunk_count);
            info!(
                "MPTRACE step=Z event=apply_world_drip_end self_id={} revision={} chunk_count={} complete={} chunks_in_world={}",
                net.local_id,
                world_revision,
                chunk_count,
                net.world_sync_progress.is_complete(),
                world.chunks.len()
            );
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
            velocity,
        } => {
            if net.is_host {
                process_stp_drop(drop_id, def_id, count, position, rotation, velocity, net);
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

        // ADR-068: un joiner pide pintar. El host aplica EXACTAMENTE las mismas reglas que al
        // jugador local — mismo `accept_spray` — midiendo el alcance contra la posición que el
        // host ya conoce de ESE peer, no contra la que el paquete pudiera reclamar.
        NetworkEvent::SprayPlaceRequest {
            place_id,
            layer,
            world_pos,
            yaw,
            size,
            strokes,
            requester_id,
        } => {
            if net.is_host {
                let Some(painter) = net.peers.get(&requester_id).map(|p| p.position) else {
                    info!(
                        "MPTRACE step=SPRAY event=spray_place_unknown_peer requester={requester_id} ignored=true"
                    );
                    return;
                };
                let req = crate::ipc::SprayPlaceRequest {
                    place_id,
                    layer,
                    world_pos,
                    yaw,
                    size,
                    strokes,
                };
                let painter_pos = Vec3::new(painter[0], painter[1], painter[2]);
                if let Some(spray) =
                    accept_spray(req, requester_id, painter_pos, net, tick, to_clients)
                {
                    broadcast_spray(&spray, net).await;
                }
            }
        }

        // ADR-068: el host aceptó una pintada (de quien sea) y este peer debe verla. Se revalida
        // aunque venga del host: es lo que separa el almacén local de cualquier paquete
        // malformado, y el coste es despreciable frente a lo que cuesta rasterizarla.
        NetworkEvent::SprayPlacedReceived { spray } => {
            if spray.validate().is_ok() {
                net.sprays.insert(spray.clone());
                // Y AL CLIENTE. Guardarla sin reenviarla dejaba al joiner con el almacén al día
                // y la pared en blanco: su `GridChunkData` ya viajó, así que ésta es la única
                // vía por la que una pintada ajena llega a su pantalla sin recargar el chunk.
                let _ = to_clients.send(ServerMessage::SprayPlaced(spray));
            } else {
                info!("MPTRACE step=SPRAY event=spray_relay_rejected ignored=true");
            }
        }

        // ADR-068: un joiner acaba de cargar un chunk y pregunta qué hay pintado en él. Su propio
        // backend no puede derivarlo — a diferencia de la geometría, una pintada no es función del
        // seed. Solo el host contesta, y contesta con una pintada POR PAQUETE.
        NetworkEvent::SprayChunkRequest {
            cx,
            cz,
            layer,
            requester_id,
        } => {
            if net.is_host {
                let sprays = net.sprays.chunk((cx, cz, layer)).to_vec();
                info!(
                    "MPTRACE step=SPRAY event=spray_chunk_served chunk=({cx},{cz},{layer}) to={requester_id} count={}",
                    sprays.len()
                );
                for spray in &sprays {
                    let payload = crate::network::protocol::PacketPayload::SprayPlaced {
                        spray: spray.clone(),
                    };
                    net.send_reliable(requester_id, &payload).await;
                }
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

            // ACK the transfer. SOLO para el handoff (0x30): quien cede la propiedad de un chunk
            // sí quiere saber que llegó. El broadcast periódico (0x11) cae en el brazo de abajo y
            // NO se confirma — ver `NetworkEvent::ChunkStateReceived`.
            let ack = crate::network::protocol::PacketPayload::ChunkTransferAck { pos: data.pos };
            net.send_reliable(from, &ack).await;
        }

        // Mismo apply que el handoff, SIN ack: es un broadcast unreliable que el dueño repite cada
        // tick, así que confirmarlo no aporta nada (nadie lee el ack) y a ~820 chunks/s llenaba la
        // ventana fiable del receptor, tirando sus propias acciones de gameplay.
        NetworkEvent::ChunkStateReceived { from, data } => {
            world.apply_chunk_transfer(&data, net.local_id);
            // Throttled a UNA línea por segundo, no por chunk: a ~820/s el `info!` por chunk que
            // traía el brazo del handoff era en sí mismo parte del problema. Pero SIN traza no hay
            // forma de ver en un log de producción si el broadcast sigue llegando — que es justo lo
            // que hubo que comprobar al separar los dos caminos, para no "arreglar" el spam a base
            // de dejar de aplicar el estado.
            if net.should_log_chunk_state() {
                info!(
                    "MPTRACE step=CS event=chunk_state_applied self_id={} from_peer={} chunk=({},{}) layer={} acked=false",
                    net.local_id, from, data.pos[0], data.pos[1], data.layer
                );
            }
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

        // ─── ADR-047: the robapieles reaches across backends ───
        NetworkEvent::PhantomAttackGrant {
            request_id,
            victim_id,
            kind,
            damage,
            impulse,
        } => {
            // We are the victim's own backend (the host addressed this to us because OUR local
            // player is the one that got hit) — apply it here, never anywhere else. Same shape as
            // the PvP mismatch guard above.
            if victim_id != net.local_id as u32 {
                warn!(
                    "MPTRACE step=PH_ATTACK event=phantom_attack_grant_victim_mismatch self_id={} got_victim={} request_id={}",
                    net.local_id, victim_id, request_id
                );
                return;
            }
            if let Err(reason) = accept_phantom_attack_grant(
                &player.stats,
                &mut net.processed_phantom_grants,
                request_id,
                tick,
            ) {
                info!(
                    "MPTRACE step=PH_ATTACK event=phantom_attack_grant_blocked reason={reason} request_id={request_id}"
                );
                return;
            }

            // The SAME three IPC events the host emits for its own player, so the client side
            // (`PhantomAttackHandler`) needs no changes at all to work inside a joiner process.
            match kind {
                1 => {
                    let death_pos = player.position.to_array();
                    player.stats.take_damage(100.0);
                    info!(
                        "MPTRACE step=PH_ATTACK event=phantom_attack_applied kind=kill request_id={request_id}"
                    );
                    let _ = to_clients.send(ServerMessage::Event(GameEvent {
                        event_type: "phantom_kill".into(),
                        data: serde_json::json!({ "pos": death_pos }),
                    }));
                }
                2 => {
                    info!(
                        "MPTRACE step=PH_ATTACK event=phantom_attack_applied kind=knockback request_id={request_id}"
                    );
                    let _ = to_clients.send(ServerMessage::Event(GameEvent {
                        event_type: "phantom_knockback".into(),
                        data: serde_json::json!({ "dx": impulse[0], "dz": impulse[1] }),
                    }));
                }
                // ADR-050 point 9. These MUST be explicit arms rather than falling through to the
                // `_` below, which treats an unknown kind as a hit: the grab window rides `damage`,
                // so kind 3 would land 2.5 points of damage on the victim instead of opening the
                // escape window. Neither touches health.
                3 => {
                    info!(
                        "MPTRACE step=PH_ATTACK event=phantom_attack_applied kind=grab_start window={damage:.1} request_id={request_id}"
                    );
                    let _ = to_clients.send(ServerMessage::Event(GameEvent {
                        event_type: "phantom_grab_start".into(),
                        data: serde_json::json!({ "window": damage }),
                    }));
                }
                4 => {
                    info!(
                        "MPTRACE step=PH_ATTACK event=phantom_attack_applied kind=grab_release request_id={request_id}"
                    );
                    let _ = to_clients.send(ServerMessage::Event(GameEvent {
                        event_type: "phantom_grab_release".into(),
                        data: serde_json::json!({}),
                    }));
                }
                _ => {
                    // Unknown kinds decode as a plain hit rather than being dropped: a future
                    // sender adding one should still land damage on an older victim backend.
                    if !damage.is_finite() || damage <= 0.0 {
                        warn!(
                            "MPTRACE step=PH_ATTACK event=phantom_attack_grant_blocked reason=invalid_damage request_id={request_id}"
                        );
                        return;
                    }
                    player.stats.take_damage(damage);
                    info!(
                        "MPTRACE step=PH_ATTACK event=phantom_attack_applied kind=hit damage={damage:.1} health={:.2} request_id={request_id}",
                        player.stats.health
                    );
                    let _ = to_clients.send(ServerMessage::Event(GameEvent {
                        event_type: "phantom_hit".into(),
                        data: serde_json::json!({ "damage": damage }),
                    }));
                }
            }
        }

        NetworkEvent::StruggleReported { victim } => {
            // Same host-only gate as the noise below, and for the same reason: nothing on a joiner
            // simulates a phantom, so nothing there can release one.
            if !net.is_host {
                warn!(
                    "MPTRACE step=PH_GRAB event=struggle_report_not_host note=dropped_no_simulator"
                );
                return;
            }
            // Nothing to sanitise: the payload is empty and the victim is the sender, which the
            // transport authenticated by address. A struggle from somebody nobody is holding drains
            // into an empty set on the next tick and does nothing.
            info!("MPTRACE step=PH_GRAB event=struggle_reported_p2p victim_id={victim}");
            net.pending_struggles.insert(victim);
        }

        NetworkEvent::NoiseReported { position, loudness } => {
            // Only the host simulates phantoms, so only the host has anything to do with a noise.
            if !net.is_host {
                warn!(
                    "MPTRACE step=PH_NOISE event=noise_report_not_host note=dropped_no_simulator"
                );
                return;
            }
            // Re-sanitised through the SAME gate as the local IPC action — a peer is never
            // trusted, and two parallel validations would drift.
            match sanitize_noise(position, loudness) {
                Some((pos, clamped)) => {
                    info!(
                        "MPTRACE step=PH_NOISE event=noise_reported_p2p pos=({:.1},{:.1},{:.1}) loudness={:.0}",
                        pos[0], pos[1], pos[2], clamped
                    );
                    net.pending_noises.push((pos, clamped));
                }
                None => {
                    warn!("MPTRACE step=PH_NOISE event=noise_report_rejected reason=malformed")
                }
            }
        }

        NetworkEvent::VoiceReceived { speaker, seq, data } => {
            // ADR-046. Two jobs, and only the host has the first one.
            //
            // A dead speaker is dropped at the door. The client already stops capturing on
            // death, but that is the half a patched client can remove; this is the half it
            // cannot.
            if net.peers.get(&speaker).map(|p| p.dead).unwrap_or(false) {
                return;
            }

            if net.is_host {
                // ADR-053: keep the last scrap so a robapieles can give it back later. Only the
                // host, because only the host simulates them.
                if !data.is_empty() {
                    net.voice_echo.insert(speaker, data.clone());
                }
                // 1) Relay to every other peer within earshot OF THE SPEAKER, stamped with the
                //    speaker's id — the same ADR-015 mechanism the pose relay uses, so the
                //    receiving side needs no special case at all.
                let payload = PacketPayload::VoiceFrame {
                    seq,
                    data: data.clone(),
                };
                for dest in crate::network::sync::voice_destinations(net, speaker) {
                    net.send_unreliable_as(speaker, dest, &payload).await;
                }
                // 2) And decide whether the HOST'S OWN player hears it. On a joiner this
                //    question is already answered — the host would not have sent the frame —
                //    but on the host nobody has asked it yet.
                let me = [player.position.x, player.position.y, player.position.z];
                let heard = net
                    .peers
                    .get(&speaker)
                    .map(|p| crate::network::sync::within_voice_range(me, p.position))
                    .unwrap_or(false);
                if !heard || player.stats.is_dead() {
                    return;
                }
            } else if player.stats.is_dead() {
                // The dead do not listen either. The host cannot enforce this half for us: it
                // knows our `dead` flag from the pose relay, but our own is the fresher truth.
                return;
            }

            let _ = to_clients_voice.send(ServerMessage::PeerVoice(crate::ipc::PeerVoice {
                peer_id: speaker,
                seq,
                data,
            }));
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
/// `stp_place` action) arms the player's PENDING respawn point; ADR-069 promotes it to the real one
/// once the piece is actually built. TODO(config): hardcoded like the ADR-029 PvP weapon allowlist;
/// move to a config surface when one exists.
const BED_DEF_ID: i32 = -4996552;

/// How close a reported bed position has to be to a stored respawn point for the two to be
/// considered the same bed. Every bed↔point pairing in this file goes through POSITION, never
/// `building_id` (ADR-069 decision 4: the id is host-minted and a joiner's own backend never
/// learns which one its placement got). Used by the ADR-037 demolish cleanup and by the ADR-069
/// `bed_constructed` promotion.
const BED_MATCH_RADIUS_M: f32 = 0.5;

/// ADR-069: promote a pending bed respawn point into the real one, if `position` is the bed this
/// player planted. Returns whether it promoted.
///
/// Stores the PENDING position, not the reported one, even though the two are within
/// `BED_MATCH_RADIUS_M` of each other: the report can come from any client that watched the bed
/// finish, and the point this player respawns at should be the one their own placement claimed.
///
/// A report that matches nothing is the normal case, not an error — every client emits one per
/// bed it sees complete, so most of them land on backends whose player planted nothing.
fn promote_pending_respawn(player: &mut Player, position: Vec3) -> bool {
    match player.pending_respawn_point {
        Some(pending) if pending.distance(position) < BED_MATCH_RADIUS_M => {
            player.respawn_point = Some(pending);
            player.pending_respawn_point = None;
            true
        }
        _ => false,
    }
}

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
            match coords.and_then(|p| sanitize_noise(p, loudness)) {
                Some((p, loudness)) => {
                    if net.is_host {
                        info!(
                            "MPTRACE step=PH_NOISE event=noise_reported pos=({:.1},{:.1},{:.1}) loudness={:.0}",
                            p[0], p[1], p[2], loudness
                        );
                        net.pending_noises.push((p, loudness));
                    } else {
                        // ADR-047 — we are a joiner: nothing here simulates a robapieles, so
                        // pushing to `pending_noises` would be a write nobody ever reads (it was
                        // also a monotonic leak: `hear_noises` only ever runs on the host). Forward
                        // it to the host, the only backend that can act on it. UNRELIABLE by
                        // design — see the payload's doc.
                        info!(
                            "MPTRACE step=PH_NOISE event=noise_forwarded_to_host pos=({:.1},{:.1},{:.1}) loudness={:.0}",
                            p[0], p[1], p[2], loudness
                        );
                        let report = PacketPayload::NoiseReport {
                            position: p,
                            loudness,
                        };
                        net.send_unreliable_to(1, &report).await; // 1 = host, as every other joiner→host send here
                    }
                }
                None => debug!("report_noise ignored: malformed position or loudness"),
            }
        }
        // ADR-050 point 9: the victim reports STRUGGLING out of a grab. Carries no payload at all —
        // the identity is "whoever's backend this is", so there is nothing to forge. A struggle
        // from a player nobody is holding drains into an empty set and does nothing.
        //
        // Molded on `report_noise` above, including the host/joiner split: only the host simulates
        // phantoms, so a joiner forwards it instead of writing to a queue nobody reads. RELIABLE,
        // unlike the noise: a dropped noise is a missed stimulus, a dropped struggle is a death the
        // player earned their way out of.
        "report_struggle" => {
            if net.is_host {
                info!(
                    "MPTRACE step=PH_GRAB event=struggle_reported victim_id={}",
                    net.local_id
                );
                net.pending_struggles.insert(net.local_id);
            } else {
                info!("MPTRACE step=PH_GRAB event=struggle_forwarded_to_host");
                let report = PacketPayload::StruggleReport;
                net.send_reliable(1, &report).await; // 1 = host
            }
        }
        "report_inventory" => {
            let mut items = parse_loot_stacks(&action.data);
            crate::world::corpse::sanitize_loot_stacks(&mut items);
            debug!("report_inventory: {} stacks", items.len());
            player.stp_inventory = items;

            // ADR-045 Fase 3: same report, richer companion parse. Empty on a pre-Fase-3
            // client (no container/slot on any entry) — that is fine, the emission site falls
            // back to the flat restore above when this stays empty.
            let mut items_v2 = parse_inventory_v2_stacks(&action.data);
            sanitize_inventory_v2_stacks(&mut items_v2);
            if !items_v2.is_empty() {
                debug!("report_inventory: {} v2 stacks", items_v2.len());
            }
            player.inventory_v2 = items_v2;
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
            // ADR-070 × canal full-replace: el spec de Unity no lleva `settling` (decodifica al
            // default, false), así que este reemplazo dejaba un item EN PLENA CAÍDA marcado como
            // posado a su altura de medio aire. El cliente clava el transform en ese flanco — los
            // items posados no re-siguen la posición, ese es el contrato pre-070 — y el item se
            // quedaba FLOTANDO para siempre, mientras la simulación (que vive aparte, en
            // `settling_items`, y sobrevive al reemplazo) lo seguía bajando hasta un suelo que ya
            // nadie mira. Disparo real: soltar algo y cruzar un límite de chunk en los ~3 s de
            // caída. La lista de simulación es la autoridad; se restaura la marca desde ella.
            restore_settling_flags(&mut net.stp_items, &net.settling_items);
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
            // ADR-070: the throw impulse. Absent (an older client, or a drop path that has none)
            // decodes to zero, which is a straight fall from the hand — still a fall, never an error.
            let velocity: [f32; 3] = serde_json::from_value(
                action
                    .data
                    .get("velocity")
                    .cloned()
                    .unwrap_or(serde_json::Value::Null),
            )
            .unwrap_or([0.0, 0.0, 0.0]);
            if net.is_host {
                process_stp_drop(drop_id, def_id, count, position, rotation, velocity, net);
            } else {
                let payload = crate::network::protocol::PacketPayload::StpDropRequest {
                    drop_id,
                    def_id,
                    count,
                    position,
                    rotation,
                    velocity,
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
            // ADR-031 + ADR-069: placing a Sleeping Bag arms this player's PENDING respawn point
            // ("last placed wins"). It is deliberately NOT the real point: `stp_place` fires on the
            // BLUEPRINT, so writing `respawn_point` here handed out a respawn for free, without
            // spending a single material. The promotion happens in `bed_constructed` below.
            //
            // Runs on the placer's own backend (host OR joiner) since the stp_place action arrives here
            // regardless of who relays the building; trust-the-client for the position (same level as
            // report_death_loot — the server validates it when resolving the spawn).
            if def_id == BED_DEF_ID {
                player.pending_respawn_point = Some(Vec3::from_array(position));
                info!(
                    "MPTRACE step=BED event=pending_respawn_point_set pos=({:.2},{:.2},{:.2})",
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
        // ADR-069: a bed FINISHED being built somewhere in the world. IPC-only, never relayed —
        // the client emits this for EVERY bed it sees complete (its own or somebody else's,
        // including beds that arrive already built on world load), and each backend decides for
        // itself whether that position is the blueprint its own player planted. The whole filter
        // is `promote_pending_respawn`: no cross-player trust is possible, because the pending
        // point only exists in the placer's own process.
        "bed_constructed" => {
            let position: [f32; 3] = serde_json::from_value(
                action
                    .data
                    .get("position")
                    .cloned()
                    .unwrap_or(serde_json::Value::Null),
            )
            .unwrap_or([0.0, 0.0, 0.0]);
            if promote_pending_respawn(player, Vec3::from_array(position)) {
                info!(
                    "MPTRACE step=BED event=respawn_point_set pos=({:.2},{:.2},{:.2})",
                    position[0], position[1], position[2]
                );
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
                    let bed_pos = Vec3::from_array(building.position);
                    if let Some(point) = player.respawn_point {
                        if point.distance(bed_pos) < BED_MATCH_RADIUS_M {
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
                    // ADR-069 decision 6: the pending point needs the SAME cleanup. Cancelling a
                    // bed blueprint must not leave a pending respawn armed on a spot where there
                    // is no longer anything to build.
                    if let Some(pending) = player.pending_respawn_point {
                        if pending.distance(bed_pos) < BED_MATCH_RADIUS_M {
                            player.pending_respawn_point = None;
                            info!(
                                "MPTRACE step=BED event=pending_respawn_point_cleared building_id={} pos=({:.2},{:.2},{:.2})",
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

/// ADR-047 — the victim backend's own veto over a robapieles' blow, extracted so it can be tested
/// without standing up a game loop. Sibling of `apply_pvp_damage_grant`, deliberately NOT a reuse
/// of it: ADR-016 keeps the phantom on its own layer, and the two dedupe keys differ (the host is
/// the sole minter of these ids, so a bare `request_id` is unique; PvP needs the attacker too).
///
/// The re-checks are not paranoia about the host — they are the only place these can happen at
/// all. `invuln_until_tick` is never relayed, so the host could not have consulted ours.
fn accept_phantom_attack_grant(
    stats: &crate::player::stats::PlayerStats,
    dedupe: &mut BoundedDedupeSet<u64>,
    request_id: u64,
    tick: u64,
) -> Result<(), &'static str> {
    // Dedupe FIRST: a reliable retransmit must never double a blow.
    if !dedupe.insert(request_id) {
        return Err("duplicate");
    }
    if stats.invuln_until_tick > tick as u32 {
        return Err("victim_invulnerable");
    }
    if stats.is_dead() {
        return Err("victim_dead");
    }
    Ok(())
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
    velocity: [f32; 3],
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
        settling: true,
    });
    // ADR-070 decision 5: the cost of this feature is capped by DESIGN, not by trusting that
    // nobody empties a backpack on the floor. At the cap the oldest faller is put to sleep where
    // it is — a slightly abrupt landing for one object, against an unbounded per-tick cost.
    if net.settling_items.len() >= MAX_SETTLING_ITEMS {
        let evicted = net.settling_items.remove(0);
        mark_item_settled(net, evicted.id);
        info!(
            "MPTRACE step=SD event=settle_cap_evicted id={} cap={}",
            evicted.id, MAX_SETTLING_ITEMS
        );
    }
    net.settling_items.push(crate::network::SettlingItem {
        id,
        velocity: Vec3::from_array(velocity),
        quiet_ticks: 0,
        age_ticks: 0,
    });
    info!(
        "MPTRACE step=SD event=stp_drop_spawned id={} drop_id={} def_id={} count={} pos=({:.2},{:.2},{:.2}) vel=({:.2},{:.2},{:.2})",
        id, drop_id, def_id, count, position[0], position[1], position[2],
        velocity[0], velocity[1], velocity[2]
    );
}

/// ADR-070: how many items may be falling at once. See decision 5 — a bound, not a guess.
const MAX_SETTLING_ITEMS: usize = 32;
/// Metres per second squared. Slightly below real gravity: a dropped object that hangs a few extra
/// frames reads as weight, and the exact value is cosmetic (nothing gameplay-facing depends on it).
const SETTLE_GRAVITY: f32 = 9.0;
/// Fraction of the vertical speed kept on a bounce. Low on purpose — loot that bounces around a
/// room like a rubber ball is worse than loot that lands.
const SETTLE_BOUNCE: f32 = 0.25;
/// Fraction of the horizontal speed kept per second of contact with the floor.
const SETTLE_FRICTION: f32 = 0.08;
/// Below this speed (m/s) an item counts as quiet for the tick.
const SETTLE_SLEEP_SPEED: f32 = 0.35;
/// Consecutive quiet substeps needed to fall asleep (~0.33 s).
const SETTLE_SLEEP_TICKS: u8 = 20;
/// ADR-070: substeps per entity tick. The roster relay runs at 10 Hz, but integrating gravity at
/// 10 Hz would make the arc a sequence of ~1 m jumps — physically wrong even before the client
/// interpolates it. Substepping integrates at an effective 60 Hz and still emits one position per
/// relay, so the arc is right and the wire is untouched. Cheap by construction: at most
/// MAX_SETTLING_ITEMS × this many iterations, and zero when nothing is falling.
const SETTLE_SUBSTEPS: u32 = 6;
/// Hard budget in ticks (~3 s at 60 Hz). Decision 2: an item that cannot settle on its own is put
/// to sleep anyway.
const SETTLE_MAX_TICKS: u16 = 180;
/// How far above the floor a resting item's origin sits, so the model does not sink into it.
const SETTLE_REST_OFFSET_M: f32 = 0.12;

/// ADR-070: re-marca como `settling` todo item del roster que siga teniendo entrada de simulación.
/// Existe por el canal full-replace (`set_stp_items`): el spec del cliente no transporta la marca,
/// así que un reemplazo del roster la borraba de los items en plena caída y el cliente los dejaba
/// flotando a media altura. La lista de simulación sobrevive al reemplazo y es la autoridad sobre
/// qué está cayendo — el roster solo refleja.
fn restore_settling_flags(
    stp_items: &mut [crate::network::protocol::StpItemInfo],
    settling: &[crate::network::SettlingItem],
) {
    for s in settling {
        if let Some(item) = stp_items.iter_mut().find(|i| i.id == s.id) {
            item.settling = true;
        }
    }
}

/// Clear the `settling` flag of one item in the replicated roster. Separate from removing the
/// simulation entry because the two live in different lists, and the flag is the half the clients
/// actually see (it is their cue to stop interpolating and pin the transform).
fn mark_item_settled(net: &mut NetworkManager, id: u32) {
    if let Some(item) = net.stp_items.iter_mut().find(|i| i.id == id) {
        item.settling = false;
    }
}

/// ADR-070: advance every falling item by one tick. Host-only.
///
/// Vertical is integrated here; horizontal is handed to `resolve_move_grid_gen`, the SAME engine
/// the phantom walks on (ADR-070 amendment: the draft named `resolve_move_simulated`, which is
/// dead outside tests AND pins Y to the floor, so it could not have produced a fall at all).
/// That split is exactly what the grid_gen resolver is built for — it slides X/Z against walls and
/// preserves the Y it was handed.
///
/// Returns the ids that fell asleep this tick, so the caller can clear their `settling` flag
/// without holding two mutable borrows of `net` at once.
fn settle_items_tick(
    items: &mut Vec<crate::network::SettlingItem>,
    positions: &mut [crate::network::protocol::StpItemInfo],
    cache: &mut crate::world::grid_gen::GridGenChunkCache,
    dt: f32,
    substeps: u32,
) -> Vec<u32> {
    let mut asleep = Vec::new();
    let sub_dt = dt / substeps.max(1) as f32;
    items.retain_mut(|s| {
        // Look the item up ONCE per tick and run every substep against that borrow. Doing the
        // lookup inside the substep loop instead would multiply a linear scan of the whole roster
        // by the substep count, on a list that grows with everything ever dropped in the world —
        // the cost would scale with the map, not with what is falling.
        let Some(item) = positions.iter_mut().find(|i| i.id == s.id) else {
            // The item was picked up mid-fall. Nothing to simulate, nothing to report.
            return false;
        };

        for _ in 0..substeps.max(1) {
            s.age_ticks = s.age_ticks.saturating_add(1);
            s.velocity.y -= SETTLE_GRAVITY * sub_dt;

            let from = Vec3::from_array(item.position);
            let layer = crate::world::grid_gen::world_pos_to_layer(from.y);
            let floor_y = crate::world::grid_gen::grid_floor_y(layer) + SETTLE_REST_OFFSET_M;

            // Horizontal first, against the live geometry. The resolver preserves the Y it is
            // given, so this is a pure X/Z slide and the vertical below is unaffected by it.
            let desired = Vec3::new(
                from.x + s.velocity.x * sub_dt,
                from.y,
                from.z + s.velocity.z * sub_dt,
            );
            let mut resolved =
                crate::world::grid_gen::resolve_move_grid_gen(cache, layer, from, desired);
            // Hitting a wall kills the horizontal speed rather than sliding along it forever: a
            // thrown object that hits a wall should drop, not skate.
            if (resolved.x - desired.x).abs() > 1e-3 {
                s.velocity.x = 0.0;
            }
            if (resolved.z - desired.z).abs() > 1e-3 {
                s.velocity.z = 0.0;
            }

            resolved.y = from.y + s.velocity.y * sub_dt;
            let grounded = resolved.y <= floor_y;
            if grounded {
                resolved.y = floor_y;
                if s.velocity.y < 0.0 {
                    s.velocity.y = -s.velocity.y * SETTLE_BOUNCE;
                    // A bounce too small to see is not a bounce; killing it here is what lets the
                    // quiet counter actually reach its threshold instead of chattering.
                    if s.velocity.y < SETTLE_SLEEP_SPEED {
                        s.velocity.y = 0.0;
                    }
                }
                s.velocity.x *= SETTLE_FRICTION.powf(sub_dt);
                s.velocity.z *= SETTLE_FRICTION.powf(sub_dt);
            }

            item.position = resolved.to_array();

            if s.velocity.length() < SETTLE_SLEEP_SPEED && grounded {
                s.quiet_ticks = s.quiet_ticks.saturating_add(1);
            } else {
                s.quiet_ticks = 0;
            }

            if s.quiet_ticks >= SETTLE_SLEEP_TICKS || s.age_ticks >= SETTLE_MAX_TICKS {
                asleep.push(s.id);
                return false;
            }
        }
        true
    });
    asleep
}

/// Monotonic id source for host-spawned STP building pieces. Lives in its own high range
/// so building ids never collide with item ids (the two lists are independent, but a
/// distinct range keeps logs unambiguous).
static NEXT_STP_BUILDING_ID: std::sync::atomic::AtomicU32 =
    std::sync::atomic::AtomicU32::new(STP_BUILDING_ID_BASE);

fn next_stp_building_id() -> u32 {
    NEXT_STP_BUILDING_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// ADR-068: monotonic id source for host-minted sprays. Starts at 1 so `0` stays available as
/// "no spray". Host-only by construction — `process_spray_place` is the single call site and it
/// returns early on a joiner, the same host-only régime ADR-063 requires of every runtime minter.
static NEXT_SPRAY_ID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(1);

fn next_spray_id() -> u32 {
    NEXT_SPRAY_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

/// ADR-068: the host accepts (or refuses) one painted spray.
///
/// Everything the client sent is a REQUEST. The host derives the chunk itself, converts the
/// position to chunk-local, validates every cap of decision 5, and only then mints an id — so a
/// modified client cannot inflate the save, paint through a wall from across the level, or plant
/// a NaN. A refusal is logged with WHICH cap it crossed: a silent reject over a legitimate
/// client is undebuggable.
async fn process_spray_place(
    req: crate::ipc::SprayPlaceRequest,
    player: &Player,
    net: &mut NetworkManager,
    tick: u64,
    to_clients: &broadcast::Sender<ServerMessage>,
) {
    if !net.is_host {
        // El joiner no ancla, no valida y no numera: solo pide. Fiable porque una pintada
        // perdida no se auto-cura — nadie la reintenta y el jugador se queda mirando una pared
        // que para los demás sí está pintada.
        let payload = crate::network::protocol::PacketPayload::SprayPlaceRequest {
            place_id: req.place_id,
            layer: req.layer,
            world_pos: req.world_pos,
            yaw: req.yaw,
            size: req.size,
            strokes: req.strokes,
        };
        net.send_reliable(1, &payload).await;
        info!(
            "MPTRACE step=SPRAY event=spray_place_forwarded_to_host place_id={}",
            req.place_id
        );
        return;
    }

    if let Some(spray) = accept_spray(req, net.local_id, player.position, net, tick, to_clients) {
        broadcast_spray(&spray, net).await;
    }
}

/// ADR-068: envía a TODOS los peers una pintada ya aceptada. Uno por paquete y no como roster
/// (a diferencia de `StpBuildingList`): una pintada son ~1,9 KB y un puñado no cabría en el
/// datagrama que ADR-060 (d) ya tuvo que paginar para elementos mucho más ligeros.
async fn broadcast_spray(spray: &crate::world::spray::Spray, net: &mut NetworkManager) {
    let payload = crate::network::protocol::PacketPayload::SprayPlaced {
        spray: spray.clone(),
    };
    for peer_id in net.peer_ids() {
        net.send_reliable(peer_id, &payload).await;
    }
}

/// El núcleo host-only: valida y acuña. Lo comparten la entrada IPC (el jugador local pinta) y
/// la entrada P2P (un joiner lo pide), para que NO existan dos juegos de reglas — el camino del
/// joiner es exactamente el mismo, solo cambia de quién es la posición contra la que se mide el
/// alcance. Devuelve la pintada aceptada para que el llamante la difunda.
fn accept_spray(
    req: crate::ipc::SprayPlaceRequest,
    author: u16,
    painter_pos: Vec3,
    net: &mut NetworkManager,
    tick: u64,
    to_clients: &broadcast::Sender<ServerMessage>,
) -> Option<crate::world::spray::Spray> {
    if req.place_id != 0 && !net.processed_spray_places.insert(req.place_id) {
        info!(
            "MPTRACE step=SPRAY event=spray_place_duplicate place_id={} ignored=true",
            req.place_id
        );
        return None;
    }

    // El chunk lo deriva el HOST, no el cliente: un cliente que redondee distinto anclaría la
    // pintada al chunk equivocado, y el anclaje es justo lo que ADR-068 no puede permitirse mal.
    let (cx, cz) = crate::world::spray::chunk_of(req.world_pos);
    let spray = crate::world::spray::Spray {
        id: next_spray_id(),
        cx,
        cz,
        layer: req.layer,
        local_pos: crate::world::spray::to_chunk_local(req.world_pos, cx, cz),
        yaw: req.yaw,
        size: req.size,
        author,
        tick,
        strokes: req.strokes,
    };

    if let Err(reason) = spray.validate_from(painter_pos) {
        info!(
            "MPTRACE step=SPRAY event=spray_place_rejected place_id={} author={} reason={} pos=({:.2},{:.2},{:.2})",
            req.place_id, author, reason, req.world_pos[0], req.world_pos[1], req.world_pos[2]
        );
        return None;
    }

    let id = spray.id;
    let points = spray.point_count();
    let evicted = net.sprays.insert(spray.clone());
    if let Some(old) = &evicted {
        info!(
            "MPTRACE step=SPRAY event=spray_evicted id={} chunk=({},{},{}) reason=chunk_full",
            old.id, old.cx, old.cz, old.layer
        );
    }

    info!(
        "MPTRACE step=SPRAY event=spray_placed id={} place_id={} author={} chunk=({},{},{}) strokes={} points={} evicted={}",
        id,
        req.place_id,
        author,
        cx,
        cz,
        req.layer,
        spray.strokes.len(),
        points,
        evicted.is_some()
    );

    // Eco inmediato: el que pinta está mirando la pared, así que el round-trip se nota más aquí
    // que en ningún otro sitio (mismo razonamiento que el relay inmediato de `stp_place`).
    let _ = to_clients.send(ServerMessage::SprayPlaced(spray.clone()));
    Some(spray)
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
            match world.spawn_dropped_item(requester_pos, item, 1, net.local_id) {
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

/// ADR-028: parse the client-reported death-loot snapshot from `report_death_loot`
/// action data: `{ equipment: [i32;4], held_item: i32, items: [{item_id, quantity}] }`.
/// Malformed or missing fields degrade to empty (never poison the corpse with junk);
/// out-of-range stacks are skipped. Length/zero-quantity hygiene is enforced again by
/// `spawn_corpse` (single choke point).
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
                    // ADR-072: las propiedades son OPCIONALES en el mensaje. Un cliente que no las
                    // mande (o un item que no tenga) da un vector vacío, que es exactamente el
                    // comportamiento anterior a este ADR — por eso no hace falta versionar el
                    // parseo, solo el schema.
                    Some(crate::world::corpse::CorpseStack {
                        item_id,
                        quantity,
                        props: parse_item_props(entry),
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

/// ADR-045 Fase 3: richer companion of `parse_loot_stacks` — reads the SAME `report_inventory`
/// `items` array, but only keeps entries that carry `container`+`slot` (a Fase-3-aware client).
/// A pre-Fase-3 client's plain `{item_id, quantity}` entries have neither key, so `?` short-
/// circuits them out here; they still populate `stp_inventory` via `parse_loot_stacks`,
/// unchanged. `props` is optional even on a v2 entry (an item with no instance properties).
fn parse_inventory_v2_stacks(data: &serde_json::Value) -> Vec<crate::player::InventoryStackV2> {
    data.get("items")
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|entry| {
                    let item_id = json_i32(entry, "item_id")?;
                    let quantity = json_i32(entry, "quantity")?;
                    let container =
                        json_u32(entry, "container").and_then(|v| u8::try_from(v).ok())?;
                    let slot = json_u32(entry, "slot").and_then(|v| u8::try_from(v).ok())?;
                    let props = parse_item_props(entry);
                    Some(crate::player::InventoryStackV2 {
                        item_id,
                        quantity,
                        container,
                        slot,
                        props,
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

/// Las propiedades de instancia de UN stack, en la forma `props:[{id,value}]`.
///
/// Una sola función para los dos consumidores: el inventario de ADR-045 y, desde ADR-072, el
/// botín del cadáver. Comparten la forma exacta porque comparten el tipo (`ItemPropertyValue`),
/// y dos copias de este bucle es garantizar que un día uno acepte lo que el otro descarta.
///
/// Ausente, malformado o con una entrada sin `id`/`value` → esa entrada se cae, no es un error:
/// mismo contrato de degradación que el resto del parseo del snapshot de muerte.
fn parse_item_props(entry: &serde_json::Value) -> Vec<crate::player::session::ItemPropertyValue> {
    entry
        .get("props")
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|p| {
                    let id = json_i32(p, "id")?;
                    let value = p.get("value").and_then(|v| v.as_f64())?;
                    // Un NaN/∞ envenenaría el desgaste con un valor que no se puede comparar ni
                    // guardar en JSON. Mismo criterio que `sanitize_reported_damage`.
                    if !value.is_finite() {
                        return None;
                    }
                    Some(crate::player::session::ItemPropertyValue { id, value })
                })
                .collect()
        })
        .unwrap_or_default()
}

/// ADR-045 Fase 3: same hygiene as `sanitize_loot_stacks` (zero-quantity drop, cap truncate),
/// kept as its own function because the two stacks share only their JSON key names, not a
/// backing type — `InventoryStackV2` is not `CorpseStack`.
fn sanitize_inventory_v2_stacks(items: &mut Vec<crate::player::InventoryStackV2>) {
    items.retain(|s| s.quantity > 0);
    items.truncate(crate::world::corpse::MAX_CORPSE_STACKS);
    // ADR-072: mismo tope de propiedades que el botín, y por la misma razón — esto también llega
    // del cliente y también acaba en memoria del servidor (el save del host). Era un hueco de
    // ADR-045: el botín truncaba y este camino no.
    for stack in items.iter_mut() {
        if stack.props.len() > crate::world::corpse::MAX_PROPS_PER_STACK {
            log::warn!(
                "inventory v2 stack item_id={} reportó {} propiedades (tope {}) — recortado",
                stack.item_id,
                stack.props.len(),
                crate::world::corpse::MAX_PROPS_PER_STACK
            );
            stack
                .props
                .truncate(crate::world::corpse::MAX_PROPS_PER_STACK);
        }
    }
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
            vocal_seq: p.vocal_seq,
            vocal_kind: p.vocal_kind,
            light_on: p.light_on,
            fire_seq: p.fire_seq,
            buttons: p.buttons,
            melee_seq: p.melee_seq,
            carry_def: p.carry_def,
            carry_count: p.carry_count,
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

#[cfg(test)]
mod tests;
