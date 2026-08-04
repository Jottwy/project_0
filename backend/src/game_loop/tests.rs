use super::*;
// ADR-016 — la IA del robapieles se mudó a `game_loop::phantom`. Glob explícito (y no solo el
// `use super::*` de arriba) porque el padre solo re-importa la superficie que él consume, y estas
// pruebas ejercitan los internos: `PhantomMover`, `PhantomState`, las constantes de tuning.
use super::phantom::*;

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

/// `invuln_until_tick` es un tick ABSOLUTO y el contador de ticks arranca en 0 en cada
/// proceso. Restaurarlo tal cual concedia invulnerabilidad PvP durante toda la duracion de
/// la sesion que lo guardo (medido en un save real: 21716 ticks ~ 6 min a 60 Hz).
#[tokio::test]
async fn hydrate_clears_the_absolute_invulnerability_tick() {
    use crate::persistence::save::{build_save, SaveMeta};

    let mut world = World::new(42);
    let mut player = Player::new(1, String::from("Host"));
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();

    let mut snapshot_player = Player::new(1, String::from("Host"));
    snapshot_player.stats.health = 73.0;
    snapshot_player.stats.invuln_until_tick = 21_716;

    let save = build_save(
        "test",
        &world,
        &snapshot_player,
        &SaveMeta::default(),
        &[],
        &[],
        &[],
        &[],
    );
    hydrate_from_save(&mut world, &mut player, &mut net, save);

    assert_eq!(
        player.stats.invuln_until_tick, 0,
        "la invulnerabilidad de respawn no puede sobrevivir a un reinicio del backend"
    );
    // Contrapartida: el resto del snapshot de stats SI se restaura — el saneo es quirurgico.
    assert!(
        (player.stats.health - 73.0).abs() < 1e-4,
        "sanear el tick de invulnerabilidad no puede tirar el resto de stats"
    );
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
    let second = apply_corpse_spawn_request(&mut world, &mut processed, 1004, 8, spawn_data(items));
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
    let expected = crate::world::grid_gen::grid_floor_y(0) + crate::world::collision::PLAYER_BASE_Y;
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

    driver.step(&mut net, 0.1, player, 0.0, false, false);

    assert_eq!(
        driver.movers[0].state,
        PhantomState::Spotted,
        "a player in radius + cone must trip WANDER → SPOTTED"
    );
    // Entering SPOTTED arms a randomized stare window in [SPOTTED_MIN, SPOTTED_MAX], SCALED by
    // this creature's temperament — the bound is derived from its own trait rather than from
    // the bare constants, which is the whole point of personalities existing.
    let dur = driver.movers[0].spotted_duration;
    let s = driver.movers[0].traits.spotted_scale;
    assert!(
        dur >= PHANTOM_SPOTTED_MIN * s - 1e-3 && dur <= PHANTOM_SPOTTED_MAX * s + 1e-3,
        "spotted_duration must be seeded in range (scale {s:.2}), got {dur}"
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

    driver.step(&mut net, 0.1, player, 0.0, false, false);

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

    driver.step(&mut net, 0.1, player, 0.0, false, false);

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
    // Patience is now scaled by temperament, so the threshold is pinned to the base constant
    // rather than assumed: this test is about the transition, not about which creature drew a
    // long fuse.
    driver.movers[0].traits.patience_scale = 1.0;
    // ADR-050: and pinned HUNGRY for the same reason the patience scale is pinned. A sated creature
    // does not charge however long its patience ran, so without this the test would be asserting
    // whatever `derive_hunger` happened to draw for this id.
    driver.movers[0].hunger = 0.0;
    driver.movers[0].state = PhantomState::Stalk;
    driver.movers[0].state_timer = PHANTOM_STALK_PATIENCE + 5.0;
    let player = Vec3::new(6.0, 1.8, 0.0);

    driver.step(&mut net, 0.1, player, 0.0, false, false);

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

    // ADR-050 point 15: the theatre needs an AUDIENCE. 20 m is inside `PHANTOM_THEATRE_RANGE`
    // (30) but outside sight (15), and the host sits at 90° to the initial heading anyway, so it
    // is watching without being detected — which is exactly the case the gesture exists for.
    let audience = Vec3::new(spawn_pos[0], spawn_pos[1], spawn_pos[2] + 20.0);

    driver.step(&mut net, 0.1, audience, 0.0, false, false);

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

    driver.step(&mut net, 0.1, player, player_yaw, false, false);

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

    driver.step(&mut net, 0.1, player, player_yaw, false, false);

    assert_eq!(
        driver.movers[0].state,
        PhantomState::Stalk,
        "STATUE must release to STALK when the player looks away"
    );
}

#[tokio::test]
async fn a_wedged_hunter_drops_its_route_and_stops_trusting_the_straight_line() {
    // The unsticking machine itself. STALK and SPRINT used to ignore whether their step landed,
    // so a creature pressed into an inside corner pushed at the same wall at 10 Hz forever —
    // and `segment_is_clear` (a segment test, NO body radius) kept reporting the line to the
    // player as clear, throwing away the one plan that could have routed around it.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].nav_waypoints = vec![Vec3::new(9.0, 1.8, 9.0)];

    // A step that INTENDED 0.9 m and gained nothing along that direction — the signature of a
    // wall-slide, which is what the raw `MoveResult::blocked` flag misses entirely: the
    // resolver happily moves the creature sideways at full speed while it closes no distance.
    let (intended, ground_out) = (0.9f32, 0.0f32);

    // Grazing geometry once is NOT being wedged: a plan survives a single scraped step, which
    // is what rounding any corner produces.
    driver.movers[0].note_step_progress(ground_out, intended);
    assert!(!driver.movers[0].is_wedged());
    assert_eq!(
        driver.movers[0].nav_waypoints.len(),
        1,
        "one scraped step must not cost a good route"
    );

    for _ in 1..PHANTOM_BLOCKED_REPLAN_TICKS {
        driver.movers[0].note_step_progress(ground_out, intended);
    }
    assert!(
        driver.movers[0].is_wedged(),
        "steps that gain nothing mean wedged"
    );
    assert!(
        driver.movers[0].nav_waypoints.is_empty(),
        "a wedged mover must drop the route that is aiming it at the wall"
    );

    // And it re-arms: one step that actually moves clears the whole condition, so the creature
    // goes straight back to the cheap straight-line path the moment it is free.
    driver.movers[0].note_step_progress(intended, intended);
    assert!(!driver.movers[0].is_wedged());
    assert_eq!(driver.movers[0].blocked_ticks, 0);

    // And holding still on purpose (STALK inside its distance band, intended = 0) is never
    // stuck — otherwise the creature would "unstick" itself out of its own designed pause.
    driver.movers[0].blocked_ticks = PHANTOM_BLOCKED_REPLAN_TICKS;
    driver.movers[0].note_step_progress(0.0, 0.0);
    assert!(
        !driver.movers[0].is_wedged(),
        "a deliberate hold is not a wedge"
    );
}

#[tokio::test]
async fn a_sprint_into_a_built_wall_registers_as_blocked() {
    // End-to-end half of the test above, through the REAL path (`resolve_move_grid_gen_ex` →
    // `note_step_blocked`) instead of poking the counter: a player-built piece blocks the cell
    // (ADR-041 overlay), the lunge cannot advance, and the wedge counter climbs.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;

    // Wall the creature in: every neighbouring cell is built on, so no direction advances.
    use crate::network::protocol::StpBuildingInfo;
    let here = Vec3::from_array(net.peers[&pid].position);
    for (id, (dx, dz)) in
        (STP_BUILDING_ID_BASE..).zip([(-2.5f32, 0.0f32), (2.5, 0.0), (0.0, -2.5), (0.0, 2.5)])
    {
        net.stp_buildings.push(StpBuildingInfo {
            id,
            def_id: 1,
            position: [here.x + dx, here.y, here.z + dz],
            rotation: 0.0,
            group_id: 0,
            added: vec![],
        });
    }

    // Player far enough that the lunge always wants to travel, close enough to stay the target.
    // Several ticks, not exactly `PHANTOM_BLOCKED_REPLAN_TICKS`: the creature starts at its own
    // cell's centre and has ~1.5 m of free travel inside it before its 0.5 m body reaches the
    // built neighbour, so the first steps legitimately advance.
    // Fewer than `PHANTOM_SPRINT_GIVEUP_TICKS`, or the lunge would disengage on its own and
    // clear the very counter this asserts on — that give-up is tested separately.
    let player = Vec3::new(here.x + 12.0, 1.8, here.z);
    for _ in 0..20 {
        driver.step(&mut net, 0.1, player, 0.0, false, false);
    }

    assert!(
        driver.movers[0].is_wedged(),
        "a lunge that cannot advance must register as wedged, got {} blocked ticks",
        driver.movers[0].blocked_ticks
    );
}

#[test]
fn traits_are_reproducible_per_creature_and_differ_between_them() {
    // Same promise as the spawn draw: two players meet the SAME character, and one that
    // despawns and comes back is still itself. That is why temperament is DERIVED and never
    // rolled — a roll at spawn would re-cast the creature every time it woke up.
    let a = ((3, -7), 0u8, 0u8);
    let b = ((3, -7), 0u8, 1u8); // same block, second creature in it
    let c = ((4, -7), 0u8, 0u8);

    assert_eq!(
        PhantomTraits::derive(42, Some(a), 0xF000),
        PhantomTraits::derive(42, Some(a), 0xF00A),
        "temperament must follow the ANCHOR, not the id it happens to be given this session"
    );
    assert_ne!(
        PhantomTraits::derive(42, Some(a), 0xF000),
        PhantomTraits::derive(42, Some(b), 0xF000)
    );
    assert_ne!(
        PhantomTraits::derive(42, Some(a), 0xF000),
        PhantomTraits::derive(42, Some(c), 0xF000)
    );
    assert_ne!(
        PhantomTraits::derive(42, Some(a), 0xF000),
        PhantomTraits::derive(7778, Some(a), 0xF000),
        "a different seed is a different world, personalities included"
    );

    // Spread is real, and centred: over many creatures the world must not drift harder or
    // softer than the constants say. Mean within 15 % of 1.0 across the four scale traits.
    let mut n = 0.0f32;
    let (mut sp, mut pa, mut im, mut st) = (0.0f32, 0.0f32, 0.0f32, 0.0f32);
    for bx in -10..10 {
        for bz in -10..10 {
            let t = PhantomTraits::derive(42, Some(((bx, bz), 0, 0)), 0xF000);
            sp += t.spotted_scale;
            pa += t.patience_scale;
            im += t.impulse_scale;
            st += t.statue_scale;
            n += 1.0;
        }
    }
    for (name, mean) in [
        ("spotted", sp / n),
        ("patience", pa / n),
        ("impulse", im / n),
        ("statue", st / n),
    ] {
        assert!(
            (mean - 1.0).abs() < 0.15,
            "{name} temperament is a difficulty knob, not variance: mean {mean:.2}"
        );
    }
}

#[tokio::test]
async fn a_searching_creature_shrieks_without_dropping_its_disguise() {
    // ADR-048's whole reason to exist. The creature is following a noise, closes on somebody it
    // has NOT seen, and vocalises — while `revealed` stays false, because ADR-038 forbids
    // deriving the reveal from anything but Sprint/Statue and the design wants the thing that
    // still looks like a player to be the thing making the sound.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    let here = Vec3::from_array(net.peers[&pid].position);
    driver.movers[0].state = PhantomState::Search;
    driver.movers[0].last_known_player_pos = Some(Vec3::new(here.x + 60.0, 1.8, here.z));
    // Inside the shriek range but well outside the 15 m sight cone behind it.
    let player = Vec3::new(here.x - 16.0, 1.8, here.z);

    driver.step(&mut net, 0.1, player, 0.0, false, false);

    let peer = &net.peers[&pid];
    assert_ne!(peer.vocal_seq, 0, "closing on a player must make a sound");
    assert_eq!(peer.vocal_kind, VOCAL_SEARCH_SHRIEK);
    assert!(
        !peer.revealed,
        "the disguise MUST survive the shriek — that is the point of the field"
    );

    // Cooldown: it does not turn into a siren while it keeps approaching.
    let seq = peer.vocal_seq;
    for _ in 0..10 {
        driver.step(&mut net, 0.1, player, 0.0, false, false);
    }
    assert_eq!(
        net.peers[&pid].vocal_seq, seq,
        "the cooldown must hold it to one cry per approach"
    );
}

#[tokio::test]
async fn a_stalker_breathes_and_the_breath_never_mutes_a_scream() {
    // Voice 3 is ambience, so it takes a SHORT cooldown. The asymmetry is the point: a breath
    // must not sit on the budget and swallow the scream of a lunge two seconds later, but it
    // must still be unable to fire during one.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Stalk;
    driver.movers[0].statue_cooldown = 999.0;
    // STALK rolls for an unpredictable lunge every tick (~2.7 % at the top of the temperament
    // range), and a lunge emits the REVEAL scream instead — a ~1-in-37 flake that passes alone
    // and fails in a full run. Pinned to 0 so this test is about the breath and nothing else.
    driver.movers[0].traits.impulse_scale = 0.0;
    // ADR-050: the breath slot carries the HUNGRY MOAN instead once it drops past
    // `PHANTOM_HUNGER_HUNTING`, so this test pins the band it is about. Mid-band, not sated: at
    // full it would also stop rolling for lunges, which would make the impulse pin above pass for
    // the wrong reason.
    driver.movers[0].hunger = 0.5;
    driver.movers[0].breath_in = 0.05; // due almost immediately
    let here = Vec3::from_array(net.peers[&pid].position);
    let player = Vec3::new(here.x + 9.0, 1.8, here.z);

    driver.step(&mut net, 0.1, player, 90.0, false, false);

    assert_eq!(net.peers[&pid].vocal_kind, VOCAL_STALK_BREATH);
    assert_ne!(net.peers[&pid].vocal_seq, 0);
    assert!(
        !net.peers[&pid].revealed,
        "breathing must never drop the disguise"
    );
    // The ambient cooldown is the short one, so the budget frees up quickly.
    assert!(
        driver.movers[0].vocal_cooldown <= PHANTOM_BREATH_COOLDOWN,
        "a breath must not spend the full dramatic-voice cooldown"
    );
}

#[test]
fn hunters_are_rare_reproducible_and_independent_of_temperament() {
    // ~1 in 8, fixed per creature forever, so the danger of a PLACE is learnable. And drawn from
    // its own bit slice: if being a hunter also dragged the four scales toward one end, "hunter"
    // would just mean "the aggressive tail of the distribution" and the variety would collapse
    // into one axis.
    let mut hunters = 0.0f32;
    let mut n = 0.0f32;
    let (mut hunter_patience, mut normal_patience) = (0.0f32, 0.0f32);
    let (mut hn, mut nn) = (0.0f32, 0.0f32);
    for bx in -16..16 {
        for bz in -16..16 {
            let t = PhantomTraits::derive(42, Some(((bx, bz), 0, 0)), 0xF000);
            n += 1.0;
            if t.is_hunter {
                hunters += 1.0;
                hunter_patience += t.patience_scale;
                hn += 1.0;
            } else {
                normal_patience += t.patience_scale;
                nn += 1.0;
            }
        }
    }
    let rate = hunters / n;
    assert!(
        (0.08..0.18).contains(&rate),
        "hunter rate should sit near 1 in 8, got {rate:.3}"
    );

    // Same creature, same answer — the whole point of deriving instead of rolling.
    let a = PhantomTraits::derive(42, Some(((3, -7), 0, 0)), 0xF000);
    assert_eq!(
        a.is_hunter,
        PhantomTraits::derive(42, Some(((3, -7), 0, 0)), 0xBEEF).is_hunter
    );

    // Independence: a hunter's patience scale is not systematically different.
    let (hp, np) = (hunter_patience / hn.max(1.0), normal_patience / nn.max(1.0));
    assert!(
        (hp - np).abs() < 0.2,
        "hunter-ness leaked into temperament: hunter mean {hp:.2} vs normal {np:.2}"
    );
}

#[tokio::test]
async fn a_distant_shot_is_answered_and_a_close_one_only_grunted() {
    // The mechanic Joel asked for: you fire, and a second later something enormous replies from
    // out there. Close by a grunt reads better — "it is RIGHT THERE" beats "it is somewhere".
    for (dist, want) in [(300.0f32, VOCAL_DISTANT_ANSWER), (10.0, VOCAL_NOISE_GRUNT)] {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let mut driver = PhantomDriver::new(42);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        let here = Vec3::from_array(net.peers[&pid].position);
        net.pending_noises
            .push(([here.x + dist, here.y, here.z], 500.0));

        driver.step(
            &mut net,
            0.1,
            Vec3::new(here.x + dist, 1.8, here.z),
            0.0,
            false,
            false,
        );

        assert_eq!(
            net.peers[&pid].vocal_kind, want,
            "a shot at {dist} m picked the wrong voice"
        );
        assert!(net.peers[&pid].vocal_seq != 0);
    }
}

#[tokio::test]
async fn hearing_a_shot_cancels_the_theatre_and_enrages() {
    // Reported from play-test: "si disparas y estan viniendo, a veces se paran a recoger un
    // objeto y resetean el viaje". The fake-pickup and stare freezes are checked at the TOP of
    // the step loop, so they held in EVERY state — a creature that began a gesture in WANDER
    // kept performing it for a full second after being told to come.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].pickup_until = Some(Instant::now() + Duration::from_secs(30));
    driver.movers[0].stare_until = Some(Instant::now() + Duration::from_secs(30));
    // ADR-050: this test is about RAGE, so the hunger axis is pinned out of it. Both assertions
    // below read `patience()`/`impulse()`, which now compose hunger too — a sated creature would
    // invert both (satiety multiplies patience by 3 and impulse by 0.15) and the test would be
    // measuring the wrong axis.
    driver.movers[0].hunger = 0.0;
    let here = Vec3::from_array(net.peers[&pid].position);
    net.pending_noises
        .push(([here.x + 20.0, here.y, here.z], 500.0));

    driver.hear_noises(&mut net);

    assert!(
        driver.movers[0].pickup_until.is_none(),
        "a hunt cancels the act"
    );
    assert!(driver.movers[0].stare_until.is_none());
    assert_eq!(driver.movers[0].state, PhantomState::Search);
    // …and a shot 20 m away is the CLOSE case: doubly enraged.
    assert!(
        driver.movers[0].enraged_for > PHANTOM_RAGE_SECONDS,
        "a shot fired close must enrage harder, got {}",
        driver.movers[0].enraged_for
    );
    // Rage shortens its patience and sharpens its trigger.
    assert!(driver.movers[0].patience() < PHANTOM_STALK_PATIENCE);
    assert!(driver.movers[0].impulse() > driver.movers[0].traits.impulse_scale);
}

#[tokio::test]
async fn a_kill_leaves_it_sated_and_it_roars_once() {
    // Joel's call: it calms down after a kill BUT roars on finishing. The roar is doing real
    // work — it is the only way the player who just died learns, on respawn, that the thing
    // which killed them is not still coming. Without it, death loops straight into death.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;
    driver.movers[0].enraged_for = 30.0; // it was angry going in
    let here = Vec3::from_array(net.peers[&pid].position);
    let player = Vec3::new(here.x + 1.0, 1.8, here.z);

    // Facing +X, i.e. AWAY from the creature to its west → killed from behind.
    let attacks = driver.step(&mut net, 0.1, player, 90.0, false, false);

    assert!(
        attacks.iter().any(|a| a.kind == PhantomAttackKind::Kill),
        "expected a kill, got {attacks:?}"
    );
    assert_eq!(
        driver.movers[0].state,
        PhantomState::Wander,
        "it stops hunting"
    );
    // ADR-050: eating fills it right up, which is the same job `calm_for` used to do as a 60 s
    // one-shot — now as a point on a cycle that drains back down on its own.
    assert_eq!(driver.movers[0].hunger, 1.0, "eating fills it");
    assert!(driver.movers[0].is_sated(), "it is sated");
    assert_eq!(
        driver.movers[0].enraged_for, 0.0,
        "a kill settles whatever it was angry about"
    );
    assert_eq!(net.peers[&pid].vocal_kind, VOCAL_SATED_ROAR);
    // Satiety makes it markedly less willing to commit again.
    assert!(driver.movers[0].patience() > PHANTOM_STALK_PATIENCE);
    // And ADR-050's hard gate: full, it cannot open a lunge at all, whatever the dice say.
    assert_eq!(
        driver.movers[0].impulse(),
        0.0,
        "a creature that just ate does not roll for lunges"
    );
}

#[tokio::test]
async fn a_real_peer_never_vocalises() {
    // The disguise cuts both ways: the field must not become a way to tell a phantom from a
    // player. A real peer's counter is only ever written from ITS OWN relayed pose, and it has
    // no path that bumps one.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let joiner_id = 1001;
    net.peers.insert(
        joiner_id,
        crate::network::peer::PeerConnection::new(
            joiner_id,
            "Joiner".into(),
            (std::net::Ipv4Addr::LOCALHOST, 40000).into(),
        ),
    );
    let mut driver = PhantomDriver::new(42);

    driver.step(&mut net, 0.1, Vec3::new(0.0, 1.8, 0.0), 0.0, false, false);

    assert_eq!(net.peers[&joiner_id].vocal_seq, 0);
    assert_eq!(net.peers[&joiner_id].vocal_kind, 0);
}

#[test]
fn a_wrapping_vocal_counter_never_lands_on_the_silent_sentinel() {
    // 0 means "has never vocalised" to the client's sentinel, so wrapping onto it would swallow
    // exactly one scream every 255 — the kind of bug that shows up once in a long session and
    // is never reproduced.
    let mut seq: u8 = 254;
    for _ in 0..4 {
        seq = match seq.wrapping_add(1) {
            0 => 1,
            n => n,
        };
        assert_ne!(seq, 0);
    }
    assert_eq!(seq, 3, "255 → 1 → 2 → 3");
}

#[tokio::test]
async fn a_committed_hunter_does_not_flip_between_two_equidistant_players() {
    // Two players at similar range used to make the target flip every tick at 10 Hz: jittering
    // heading, `last_known_player_pos` bouncing between two places, and the A* plan thrown away
    // on each one. It also made "it has chosen YOU" unreadable, which is most of a chase.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let from = Vec3::new(0.0, 1.8, 0.0);
    let host = Vec3::new(10.0, 1.8, 0.0);
    let joiner_id = 1001;
    net.peers.insert(
        joiner_id,
        crate::network::peer::PeerConnection::new(
            joiner_id,
            "Joiner".into(),
            (std::net::Ipv4Addr::LOCALHOST, 40000).into(),
        ),
    );
    // A HAIR closer than the host — under the switch margin, so it must not steal a committed
    // hunter, and must be picked when nothing is committed yet.
    net.peers.get_mut(&joiner_id).unwrap().position = [9.8, 1.8, 0.0];

    let none = HashMap::new();
    let first = choose_target(&net, host, 0.0, false, from, None, &none).unwrap();
    assert_eq!(first.0, joiner_id, "uncommitted, it takes the nearest");

    // Committed to the HOST, the marginally-closer joiner is not enough to pull it away.
    let held = choose_target(&net, host, 0.0, false, from, Some(net.local_id), &none).unwrap();
    assert_eq!(
        held.0, net.local_id,
        "a hair closer must not break commitment"
    );

    // …but a decisively closer player does. Commitment is stickiness, not blindness.
    net.peers.get_mut(&joiner_id).unwrap().position = [2.0, 1.8, 0.0];
    let switched = choose_target(&net, host, 0.0, false, from, Some(net.local_id), &none).unwrap();
    assert_eq!(switched.0, joiner_id, "a clearly closer player must win");
}

#[tokio::test]
async fn creatures_spread_across_players_instead_of_dogpiling_one() {
    // Six creatures all running the same "nearest" rule converge on the same person, so in a
    // two-player game one player gets the whole map's attention and the other gets none.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let from = Vec3::new(0.0, 1.8, 0.0);
    let host = Vec3::new(10.0, 1.8, 0.0);
    let joiner_id = 1001;
    net.peers.insert(
        joiner_id,
        crate::network::peer::PeerConnection::new(
            joiner_id,
            "Joiner".into(),
            (std::net::Ipv4Addr::LOCALHOST, 40000).into(),
        ),
    );
    net.peers.get_mut(&joiner_id).unwrap().position = [14.0, 1.8, 0.0]; // further away

    // Nobody hunting: the nearer host wins on distance alone.
    let alone = choose_target(&net, host, 0.0, false, from, None, &HashMap::new()).unwrap();
    assert_eq!(alone.0, net.local_id);

    // With two others already on the host, a fresh creature goes for the lonely player even
    // though he is 4 m further.
    let crowded = HashMap::from([(net.local_id, 2usize)]);
    let spread = choose_target(&net, host, 0.0, false, from, None, &crowded).unwrap();
    assert_eq!(
        spread.0, joiner_id,
        "crowding must send a new hunter to the player nobody is on"
    );
}

#[tokio::test]
async fn a_strike_reaches_further_than_the_body_can_travel() {
    // "Te pegas a una pared y se queda sin poder hacer nada": the strike used to need the same
    // 1.5 m the 0.5 m BODY had to travel to, so a player flat against geometry could not be
    // reached at all and the creature stood at ~2 m staring. Reach and travel are now separate.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;
    let here = Vec3::from_array(net.peers[&pid].position);
    // Beyond the OLD 1.5 m, inside the new reach — the exact band that used to be a dead zone.
    // The direction is CHOSEN for a clear line rather than assumed: picking east blindly put
    // a seed-42 wall between the two and the test measured geometry, not reach.
    let player = [(2.0f32, 0.0f32), (-2.0, 0.0), (0.0, 2.0), (0.0, -2.0)]
        .into_iter()
        .map(|(dx, dz)| Vec3::new(here.x + dx, 1.8, here.z + dz))
        .find(|p| crate::world::grid_gen::segment_is_clear(&mut driver.grid_cache, 0, here, *p))
        .expect("no open direction at 2 m from a walkable cell");
    // Yaw facing back at the phantom, so it is a Hit and not a Kill — either proves the reach,
    // but pinning it keeps the assert readable.
    let player_yaw = (here.x - player.x)
        .atan2(here.z - player.z)
        .to_degrees()
        .rem_euclid(360.0);

    let attacks = driver.step(&mut net, 0.1, player, player_yaw, false, false);

    assert_eq!(
        attacks.len(),
        1,
        "a player at 2 m with a clear line must be reachable"
    );
}

#[tokio::test]
async fn extra_reach_never_strikes_through_a_wall() {
    // NEGATIVE CONTROL for the reach: widening it must not let the creature hit you through
    // geometry, which is why the strike is gated on a clear segment and not on distance alone.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;
    let here = Vec3::from_array(net.peers[&pid].position);

    // Build a wall in the cell between them, then stand just past it, inside the reach.
    use crate::network::protocol::StpBuildingInfo;
    net.stp_buildings.push(StpBuildingInfo {
        id: STP_BUILDING_ID_BASE,
        def_id: 1,
        position: [here.x + 2.5, here.y, here.z],
        rotation: 0.0,
        group_id: 0,
        added: vec![],
    });
    let player = Vec3::new(here.x + 2.3, 1.8, here.z);

    let attacks = driver.step(&mut net, 0.1, player, 270.0, false, false);

    assert!(
        attacks.is_empty(),
        "reach must not pass through a built wall, got {attacks:?}"
    );
}

#[tokio::test]
async fn a_wedged_lunge_eventually_gives_up_instead_of_grinding_forever() {
    // Backstop for geometry nobody predicted: a lunge must never end up pinned to a wall for
    // good. It re-stalks and comes back from somewhere else.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;
    driver.movers[0].blocked_ticks = PHANTOM_SPRINT_GIVEUP_TICKS;
    let here = Vec3::from_array(net.peers[&pid].position);
    let player = Vec3::new(here.x + 12.0, 1.8, here.z);

    driver.step(&mut net, 0.1, player, 0.0, false, false);

    assert_eq!(
        driver.movers[0].state,
        PhantomState::Stalk,
        "a lunge that cannot make progress must disengage"
    );
}

#[tokio::test]
async fn a_hesitating_lunge_holds_still_before_it_comes() {
    // The beat between "it stops looking like a player" and "it is on you". Reveal and scream
    // both ride SPRINT (ADR-038), so without this they land on the same instant the creature
    // starts closing and there is nothing to read.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;
    driver.movers[0].hesitate_timer = 0.5;
    let here = Vec3::from_array(net.peers[&pid].position);
    let player = Vec3::new(here.x + 10.0, 1.8, here.z);

    driver.step(&mut net, 0.1, player, 0.0, false, false);

    let after = Vec3::from_array(net.peers[&pid].position);
    assert!(
        after.distance_xz(here) < 1e-3,
        "a hesitating lunge must not travel, moved to {after:?}"
    );
    assert!(
        phantom_reveals(driver.movers[0].state),
        "…and it holds its real form while it hesitates"
    );

    // It is a beat, not a stall: once it expires the creature closes.
    for _ in 0..8 {
        driver.step(&mut net, 0.1, player, 0.0, false, false);
    }
    let moved = Vec3::from_array(net.peers[&pid].position);
    assert!(
        moved.distance_xz(here) > 0.5,
        "the hesitation must END and the lunge continue"
    );
}

#[tokio::test]
async fn phantom_stops_hunting_a_dead_player() {
    // The bug this exists for, reported from play-test: kill the player and the creature keeps
    // lunging at the corpse until they respawn. The damage ROUTER already skipped a dead victim,
    // so nothing was ever applied and no log ever complained — the behaviour that produced the
    // blows lived one layer above the guard that dropped them.
    //
    // `sync_population` only retires a phantom in WANDER, so one locked onto a corpse also
    // stayed anchored over it indefinitely. Losing the target is what releases both.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let world = World::new(42);
    let mut driver = PhantomDriver::new(world.seed);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;
    // Point-blank relative to the phantom's actual (snapped) spawn pos — `spawn_phantom` moves
    // it to a walkable cell, so the raw `start` is not where it stands.
    let ppos = net.peers[&pid].position;
    let player = Vec3::new(ppos[0] + 1.0, 1.8, ppos[2]); // ~1 m east, inside the 1.5 m strike
    let player_yaw = 270.0; // faces -X, i.e. looking straight at it

    let attacks = driver.step(&mut net, 0.1, player, player_yaw, false, true);

    assert!(
        attacks.is_empty(),
        "a dead player must not be struck: {attacks:?}"
    );
    assert_ne!(
        driver.movers[0].state,
        PhantomState::Sprint,
        "with nobody alive to chase, the lunge must end"
    );
}

#[tokio::test]
async fn phantom_still_strikes_a_living_player_at_point_blank() {
    // NEGATIVE CONTROL for the test above: same setup, host ALIVE. Without this, deleting the
    // strike entirely would leave `phantom_stops_hunting_a_dead_player` green.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let world = World::new(42);
    let mut driver = PhantomDriver::new(world.seed);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;
    let ppos = net.peers[&pid].position;
    let player = Vec3::new(ppos[0] + 1.0, 1.8, ppos[2]);
    let player_yaw = 270.0;

    let attacks = driver.step(&mut net, 0.1, player, player_yaw, false, false);

    assert_eq!(attacks.len(), 1, "a living player at point blank gets hit");
    assert_eq!(attacks[0].victim, net.local_id);
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

    driver.step(&mut net, 0.1, player, 0.0, false, false);

    assert_eq!(
        driver.movers[0].state,
        PhantomState::Spotted,
        "a running player within sound range must be heard → SPOTTED"
    );
    // The SHORT window, scaled by temperament (see the sight test above). What matters is that
    // sound picks the short band, not that every creature reacts in the same time.
    assert!(
        driver.movers[0].spotted_duration
            <= PHANTOM_SPOTTED_SOUND_MAX * driver.movers[0].traits.spotted_scale + 1e-3,
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

    driver.step(&mut net, 0.1, player, 0.0, true, false); // crouched

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
        driver.step(&mut net, 0.1, player, 0.0, false, false);

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

    let attack = driver.step(&mut net, 0.1, player, player_yaw, false, false);

    assert_eq!(
        attack,
        [PhantomAttack {
            victim: net.local_id,
            kind: PhantomAttackKind::Kill
        }],
        "behind-attack must KILL the local player, got {attack:?}"
    );
}

/// ADR-047 — THE bug Joel reported: a robapieles chasing a JOINER used to damage the HOST.
/// `choose_target` has always been able to pick a remote peer, but the attack carried no
/// victim, so the consumer had nothing to branch on and every blow landed locally.
///
/// The assert is on the VICTIM, not on the kind: the kind was never wrong.
#[tokio::test]
async fn phantom_attacking_a_joiner_names_the_joiner_not_the_host() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);

    // A real joiner, point-blank on the phantom. The host's own player is far away — the
    // configuration that used to send the host's health down for no visible reason.
    let joiner_id: u16 = 2;
    let addr: std::net::SocketAddr = "127.0.0.1:9002".parse().unwrap();
    let mut joiner = crate::network::peer::PeerConnection::new(joiner_id, "Joiner".into(), addr);
    let ppos = net.peers[&pid].position;
    joiner.position = [ppos[0] + 1.0, 1.8, ppos[2]]; // ~1 m: inside the 1.5 m strike
    joiner.rotation = 90.0; // faces +X, AWAY from the phantom → attacked from behind
    net.peers.insert(joiner_id, joiner);

    let world = World::new(42);
    let mut driver = PhantomDriver::new(world.seed);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;

    let host_far = Vec3::new(ppos[0] + 500.0, 1.8, ppos[2]);
    let attacks = driver.step(&mut net, 0.1, host_far, 0.0, false, false);

    assert_eq!(attacks.len(), 1, "expected one strike, got {attacks:?}");
    assert_eq!(
        attacks[0].victim, joiner_id,
        "the blow must name the JOINER it actually hit, not the host ({}); got {attacks:?}",
        net.local_id
    );
    assert_ne!(
        attacks[0].victim, net.local_id,
        "regression: the host is being named as victim for a joiner's beating"
    );
}

/// ADR-047 D7 — `hear_noises` measures distance in XZ, so before the layer test a shot on
/// layer 0 summoned every creature stacked above and below it.
#[tokio::test]
async fn a_noise_does_not_travel_between_layers() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, stand_on(0), 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    let from = Vec3::from_array(net.peers[&pid].position);
    driver.add(pid, 0.0, from, true);

    // Same XZ spot, a different floor, well inside the audible radius.
    net.pending_noises
        .push(([from.x, stand_on(1), from.z], 500.0));
    driver.hear_noises(&mut net);

    assert_eq!(
        driver.movers[0].state,
        PhantomState::Wander,
        "a shot one floor up must not be heard through the ceiling"
    );
    assert!(
        driver.movers[0].last_known_player_pos.is_none(),
        "and it must leave no goal behind either"
    );
}

/// ADR-047 D7 — the sentinel half: the SAME noise on the SAME layer still lands. Without it,
/// a layer test that rejected everything would pass the test above.
#[tokio::test]
async fn a_noise_on_the_same_layer_is_still_heard() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, stand_on(0), 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    let from = Vec3::from_array(net.peers[&pid].position);
    driver.add(pid, 0.0, from, true);

    net.pending_noises
        .push(([from.x + 100.0, from.y, from.z], 500.0));
    driver.hear_noises(&mut net);

    assert_eq!(
        driver.movers[0].state,
        PhantomState::Search,
        "a shot on our own floor must start an investigation"
    );
}

/// ADR-047 D5 — the contradiction between ADR-041 (a 500 m gunshot worth a long journey) and
/// ADR-043 (only creatures within 150 m of a player exist at all). Before this, a distant shot
/// reached nobody: not because it was inaudible, but because there was nothing there yet.
#[tokio::test]
async fn a_distant_shot_wakes_a_sleeper_that_no_player_is_near() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut driver = PhantomDriver::new(42);
    // Nobody has been anywhere: no phantom is simulated.
    assert!(driver.movers.is_empty());

    // A rifle, far beyond PHANTOM_ACTIVATE_RADIUS (150 m).
    net.pending_noises
        .push(([400.0, stand_on(0), 400.0], 500.0));
    driver.wake_for_noises(&mut net);

    assert!(
        !driver.movers.is_empty(),
        "a 500 m shot must be able to wake somebody near where it was fired"
    );
    assert!(
        driver.movers.len() <= PHANTOM_NOISE_ACTIVATE_MAX,
        "one shot must not summon a crowd: {} woken, cap is {PHANTOM_NOISE_ACTIVATE_MAX}",
        driver.movers.len()
    );
    // The queue is NOT consumed here — `hear_noises` owns the drain, and the one just woken
    // has to still find the noise waiting for it on this same tick.
    assert_eq!(
        net.pending_noises.len(),
        1,
        "wake_for_noises must peek, never drain"
    );
}

/// ADR-047 D5 — the global cap still binds. Without this, the per-noise cap alone would let a
/// burst of shots walk the population straight past `active_cap`.
#[tokio::test]
async fn waking_by_noise_still_respects_the_global_cap() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut driver = PhantomDriver::new(42);
    driver.active_cap = 1;

    for i in 0..4 {
        net.pending_noises
            .push(([400.0 + i as f32 * 50.0, stand_on(0), 400.0], 500.0));
    }
    driver.wake_for_noises(&mut net);

    assert!(
        driver.movers.len() <= 1,
        "active_cap=1 but {} are awake",
        driver.movers.len()
    );
}

#[tokio::test]
async fn phantom_sprint_hits_from_front() {
    // Point-blank SPRINT while the player IS looking → non-lethal Hit.
    //
    // This test used to assert the bounce to STALK on the SAME tick as the blow. That was the
    // flicker: `revealed` is derived from the state (ADR-038), so a lunge that ended the
    // instant it connected dropped the disguise and put it back on around one frame of contact.
    // The lunge now holds for `PHANTOM_STRIKE_RECOVERY` and the bounce is asserted below, in
    // `a_strike_does_not_end_the_lunge_on_the_same_tick`.
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

    let attack = driver.step(&mut net, 0.1, player, player_yaw, false, false);

    assert!(
        matches!(attack, [PhantomAttack { victim, kind: PhantomAttackKind::Hit(d) }]
            if *victim == net.local_id && (d - PHANTOM_ATTACK_DAMAGE).abs() < 1e-3),
        "frontal attack must HIT the local player for {PHANTOM_ATTACK_DAMAGE}, got {attack:?}"
    );
    assert_eq!(
        driver.movers[0].state,
        PhantomState::Sprint,
        "the lunge stays committed through its own strike"
    );
}

#[tokio::test]
async fn a_strike_does_not_end_the_lunge_on_the_same_tick() {
    // ADR-038 derives `revealed` from the STATE, so anything that makes the state flap makes
    // the real form flap with it — disguise off, scream, disguise back on, around a single
    // frame of contact. The fix is in the FSM and NOT a latch on the flag: ADR-038 point 2 is
    // explicit that `revealed` is a derived level, and its rejected alternative (C) is a latch.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;
    let ppos = net.peers[&pid].position;
    let player = Vec3::new(ppos[0] + 1.0, 1.8, ppos[2]);
    let player_yaw = 270.0; // looking at it → a Hit, not a Kill

    let first = driver
        .step(&mut net, 0.1, player, player_yaw, false, false)
        .len();
    assert_eq!(first, 1, "the blow still lands");

    // The 1 s gesture freeze runs on `Instant`, so inside a test (microseconds of real time) it
    // never expires and would swallow every remaining tick. Ending it by hand is what a second
    // of wall-clock does in production; it is also why the bounce below is a LEVEL and not the
    // edge of the timer.
    driver.movers[0].pickup_until = None;

    // Through the whole commitment the creature stays revealed and never strikes twice.
    let mut extra = 0;
    let ticks = (PHANTOM_STRIKE_RECOVERY / 0.1).floor() as i32 - 2;
    for _ in 0..ticks {
        extra += driver
            .step(&mut net, 0.1, player, player_yaw, false, false)
            .len();
        assert!(
            phantom_reveals(driver.movers[0].state),
            "the real form must not flicker back mid-commitment"
        );
    }
    assert_eq!(extra, 0, "no second blow inside the recovery window");

    // …AND IT KEEPS COMING. The lunge used to bounce back to STALK a couple of seconds after
    // each blow, which is what "ataca, no ataca" looked like from the outside. A committed hunt
    // now ends only when the PLAYER ends it — outrun it, or break its line of sight.
    for _ in 0..40 {
        driver.step(&mut net, 0.1, player, player_yaw, false, false);
    }
    assert_eq!(
        driver.movers[0].state,
        PhantomState::Sprint,
        "a hunt with a clear line to a reachable player must NOT let go on its own"
    );
}

#[tokio::test]
async fn breaking_the_line_of_sight_ends_a_committed_hunt() {
    // The other half of the rule above, and the reason hiding is worth anything: a lunge that
    // cannot see you gives up after PHANTOM_SPRINT_BLIND_SECONDS. Without this test the change
    // above would be indistinguishable from "the creature never stops", which is a worse game.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].state = PhantomState::Sprint;
    let here = Vec3::from_array(net.peers[&pid].position);

    // Wall the player off: a built piece between them blocks the segment (ADR-041 overlay), so
    // the creature is inside LOSE_RADIUS but blind — exactly "he found somewhere to hide".
    use crate::network::protocol::StpBuildingInfo;
    for (i, d) in [2.5f32, 5.0].iter().enumerate() {
        net.stp_buildings.push(StpBuildingInfo {
            id: STP_BUILDING_ID_BASE + i as u32,
            def_id: 1,
            position: [here.x + d, here.y, here.z],
            rotation: 0.0,
            group_id: 0,
            added: vec![],
        });
    }
    let player = Vec3::new(here.x + 9.0, 1.8, here.z);

    let ticks = ((PHANTOM_SPRINT_BLIND_SECONDS / 0.1) as i32) + 5;
    for _ in 0..ticks {
        driver.step(&mut net, 0.1, player, 0.0, false, false);
    }

    assert_ne!(
        driver.movers[0].state,
        PhantomState::Sprint,
        "a hunt that has lost its line must break off, or hiding means nothing"
    );
}

#[tokio::test]
async fn statue_uses_a_wider_cone_to_release_than_to_freeze() {
    // Hysteresis. With one hard edge, a player standing on the boundary toggled STATUE↔STALK
    // every tick at 10 Hz, and every toggle was a full reveal + scream.
    let phantom = Vec3::new(0.0, 1.8, 0.0);
    let player = Vec3::new(0.0, 1.8, -10.0); // 10 m south, so yaw 0 (+Z) looks straight at it

    // A yaw between the two cones: outside the 30° that freezes, inside the 45° that holds.
    let between = (PHANTOM_STATUE_LOOK_HALF_FOV.to_degrees()
        + PHANTOM_STATUE_RELEASE_HALF_FOV.to_degrees())
        / 2.0;

    assert!(
        !player_is_looking_at(player, between, phantom),
        "must be too far off-axis to START a freeze"
    );
    assert!(
        player_is_looking_at_within(player, between, phantom, PHANTOM_STATUE_RELEASE_HALF_FOV),
        "…yet still count as watching, so an existing freeze HOLDS"
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
    // ADR-050: pinned hungry. A sated creature bored of the statue game still SHOVES you but goes
    // back to shadowing instead of charging, so without this the tail assertion would be measuring
    // whichever band `derive_hunger` drew.
    driver.movers[0].hunger = 0.0;
    let ppos = net.peers[&pid].position;
    let player = Vec3::new(ppos[0] + 2.0, 1.8, ppos[2]); // within PHANTOM_KNOCKBACK_RANGE (3 m)

    let attack = driver.step(&mut net, 0.1, player, 0.0, false, false);

    assert!(
        matches!(attack, [PhantomAttack { victim, kind: PhantomAttackKind::Knockback(_, _) }]
            if *victim == net.local_id),
        "point-blank STATUE timeout must shove the local player, got {attack:?}"
    );
    assert_eq!(driver.movers[0].state, PhantomState::Sprint);
}

#[tokio::test]
async fn a_sated_creature_never_charges_however_long_its_patience_ran() {
    // ADR-050 point 4 — THE GATE, and the single most important assertion of the redesign. This is
    // the exact setup of `phantom_sprints_after_patience_exceeded`, which lunges, with the ONE
    // difference that this creature has just eaten. "It hangs around a bit and then attacks" was
    // the reported feel, and the cause was that nothing but a dice roll gated the charge.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let world = World::new(42);
    let mut driver = PhantomDriver::new(world.seed);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].traits.patience_scale = 1.0;
    driver.movers[0].traits.impulse_scale = 1.7; // the twitchiest temperament in the range
    driver.movers[0].hunger = 1.0; // just fed
    driver.movers[0].statue_cooldown = 999.0; // keep it out of STATUE so this is about the charge
    driver.movers[0].state = PhantomState::Stalk;
    driver.movers[0].state_timer = PHANTOM_STALK_PATIENCE * 10.0;
    let here = Vec3::from_array(net.peers[&pid].position);
    let player = Vec3::new(here.x + 6.0, 1.8, here.z);

    // Many ticks, so the per-tick roll gets every chance it would ever get.
    for _ in 0..200 {
        driver.step(&mut net, 0.1, player, 0.0, false, false);
        assert_ne!(
            driver.movers[0].state,
            PhantomState::Sprint,
            "a sated creature must never charge, whatever the dice or the clock say"
        );
    }
    assert!(!net.peers[&pid].revealed, "and it never drops the disguise");
}

#[tokio::test]
async fn hunger_is_reproducible_per_creature_and_spread_across_the_population() {
    // Same discipline as `traits_are_reproducible_per_creature_and_differ_between_them`: derived
    // from the anchor, never rolled, so two players meet the same creature at the same point of its
    // cycle and one that despawns and returns is still itself.
    let anchor = Some(((3i32, -7i32), 0u8, 0u8));
    assert_eq!(
        derive_hunger(42, anchor, 0xF001),
        derive_hunger(42, anchor, 0xF999),
        "hunger must come from the ANCHOR, not from whichever peer id it got this time"
    );
    assert_ne!(
        derive_hunger(42, anchor, 0xF001),
        derive_hunger(7778, anchor, 0xF001),
        "a different seed is a different world"
    );

    // And the population must hold animals at every point of the cycle AT ONCE. Seeding them near
    // one value would give the world synchronised feeding hours, where everything everywhere turns
    // dangerous together.
    let mut sated = 0;
    let mut hungry = 0;
    for bx in 0..20i32 {
        for bz in 0..20i32 {
            let h = derive_hunger(42, Some(((bx, bz), 0, 0)), 0xF000);
            assert!((0.0..=1.0).contains(&h), "hunger out of range: {h}");
            if h > PHANTOM_HUNGER_SATED {
                sated += 1;
            }
            if h < PHANTOM_HUNGER_HUNTING {
                hungry += 1;
            }
        }
    }
    assert!(
        sated > 40 && hungry > 40,
        "the draw must populate both ends of the cycle, got {sated} sated / {hungry} hungry of 400"
    );
}

#[tokio::test]
async fn hunger_drains_even_while_the_fsm_is_skipped() {
    // The gesture freeze returns `None` from `resolve_mover_tick` and skips the whole FSM, so a
    // timer ticked inside a state arm would stall across exactly that window. Hunger is ticked in
    // the preamble with the other timers for that reason.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].hunger = 1.0;
    // Frozen mid-gesture for the whole run, and far from any player so nothing cancels it.
    driver.movers[0].pickup_until = Some(Instant::now() + Duration::from_secs(60));
    let away = Vec3::new(100_000.0, 1.8, 100_000.0);

    for _ in 0..100 {
        driver.step(&mut net, 0.1, away, 0.0, false, false);
    }

    let expected = 1.0 - 10.0 / PHANTOM_HUNGER_DRAIN_SECONDS;
    assert!(
        (driver.movers[0].hunger - expected).abs() < 1e-3,
        "10 s of gesture freeze must still cost 10 s of hunger, got {}",
        driver.movers[0].hunger
    );
    assert!(
        driver.movers[0].pickup_until.is_some(),
        "and the freeze itself is untouched — nobody was near enough to cancel it"
    );
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

    let attacks = driver.step(&mut net, 0.1, player, 270.0, false, false);

    assert_eq!(
        attacks.len(),
        2,
        "both attackers of the tick must be reported, got {attacks:?}"
    );
    assert!(
        attacks.iter().all(
            |a| matches!(a, PhantomAttack { kind: PhantomAttackKind::Hit(d), .. }
                if (d - PHANTOM_ATTACK_DAMAGE).abs() < 1e-3)
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

/// ADR-047 — the victim backend's veto. A retransmitted grant is the realistic case: 0x4D is
/// reliable, so the same blow WILL arrive twice whenever an ACK is lost.
#[test]
fn a_retransmitted_phantom_grant_lands_once() {
    let stats = crate::player::stats::PlayerStats::default();
    let mut dedupe = BoundedDedupeSet::with_capacity(64);

    assert_eq!(
        accept_phantom_attack_grant(&stats, &mut dedupe, 77, 0),
        Ok(())
    );
    assert_eq!(
        accept_phantom_attack_grant(&stats, &mut dedupe, 77, 0),
        Err("duplicate"),
        "a reliable retransmit must not land a second time"
    );
    // A DIFFERENT blow from the same phantom in the same second still counts — deduping by
    // anything coarser than the request id would silently eat real hits.
    assert_eq!(
        accept_phantom_attack_grant(&stats, &mut dedupe, 78, 0),
        Ok(())
    );
}

/// ADR-047 — respawn invulnerability is re-checked on the VICTIM's backend because that is the
/// only backend that has it: `invuln_until_tick` is never relayed, so the host cannot consult
/// a joiner's. Without this a joiner could be killed inside its own spawn protection.
#[test]
fn a_phantom_grant_cannot_pierce_respawn_invulnerability() {
    let stats = crate::player::stats::PlayerStats {
        invuln_until_tick: 500,
        ..Default::default()
    };
    let mut dedupe = BoundedDedupeSet::with_capacity(64);

    assert_eq!(
        accept_phantom_attack_grant(&stats, &mut dedupe, 1, 100),
        Err("victim_invulnerable")
    );
    // …and it lands once the window has passed. Note the DIFFERENT request_id: the rejected
    // one was already consumed by the dedupe, which is deliberate — the host retries nothing.
    assert_eq!(
        accept_phantom_attack_grant(&stats, &mut dedupe, 2, 600),
        Ok(())
    );
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
        (res.position.x - bed.x).abs() < CHUNK_SIZE && (res.position.z - bed.z).abs() < CHUNK_SIZE,
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
