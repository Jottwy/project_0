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
        settling: false,
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
        props: Vec::new(),
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
        1.0,
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

/// ADR-045 Fase 2: "el fichero de jugador gana sobre `host_player`" no es una regla de
/// prioridad en ningun sitio del codigo — es simplemente que `apply_player_snapshot` se llama
/// una segunda vez (desde el bloque por-tick de `run()`) despues de que `hydrate_from_save` ya
/// la llamo una primera con el snapshot embebido del save de mundo. El ultimo `apply` gana.
#[test]
fn apply_player_snapshot_prefers_the_last_applied_snapshot() {
    use crate::persistence::save::PlayerSnapshot;

    let mut host_embedded_like = Player::new(1, "Host");
    host_embedded_like.stats.health = 40.0;
    host_embedded_like.position = Vec3::new(1.0, 1.8, 1.0);

    let mut player_file_like = Player::new(1, "Host");
    player_file_like.stats.health = 90.0;
    player_file_like.position = Vec3::new(9.0, 1.8, 9.0);

    let mut player = Player::new(1, "Host");
    apply_player_snapshot(
        &mut player,
        PlayerSnapshot::from_player(&host_embedded_like),
    );
    apply_player_snapshot(&mut player, PlayerSnapshot::from_player(&player_file_like));

    assert!(
        (player.stats.health - 90.0).abs() < 1e-4,
        "el segundo apply (fichero de jugador) debe ganar sobre el primero (host_player)"
    );
    assert_eq!(player.position, Vec3::new(9.0, 1.8, 9.0));
}

#[test]
fn movement_suppressed_none_never_suppresses() {
    assert!(!movement_suppressed(0, None));
    assert!(!movement_suppressed(1_000_000, None));
}

/// Fija el orden dentro del tick: el punto de fallo real del bug era armar la ventana en el
/// tick de EMISION de `session_restored` en vez del tick de HIDRATACION — `apply_movement`
/// corre DESPUES del bloque de hidratacion dentro del MISMO tick en `run()`, asi que armar un
/// tick tarde deja pasar el primer clobber. Este test fija la aritmetica de fronteras exacta
/// que `run()` depende: suprimido en el tick de hidratacion (el que importa), suprimido en el
/// de emision (un tick despues), y liberado justo al llegar a `until`, no antes ni despues.
#[test]
fn movement_suppressed_protects_from_the_hydration_tick_through_the_window() {
    let hydrate_tick = 500u64;
    let until = hydrate_tick + RESTORE_SNAP_SUPPRESS_TICKS;

    assert!(
        movement_suppressed(hydrate_tick, Some(until)),
        "el tick de hidratacion, MISMO tick que el clobber que este fix existe para evitar"
    );
    assert!(
        movement_suppressed(hydrate_tick + 1, Some(until)),
        "el tick en que session_restored se emite de verdad (uno despues de hidratar)"
    );
    assert!(movement_suppressed(until - 1, Some(until)));
    assert!(
        !movement_suppressed(until, Some(until)),
        "al llegar a `until` el cliente ya tuvo toda la ventana para recibir + aplicar el snap"
    );
    assert!(!movement_suppressed(until + 100, Some(until)));
}

/// Extremo a extremo con las funciones REALES de produccion (no una reimplementacion en el
/// test): hidrata una posicion, confirma que un input de cliente con la posicion RECLAMADA
/// PREVIA a la restauracion no la pisa mientras la ventana esta armada, y que — control
/// negativo — la MISMA llamada SI la pisa en cuanto la ventana expira. Sin el control negativo
/// este test pasaria igual aunque `apply_movement` nunca tocara `position` por cualquier otro
/// motivo. `god_traversal=true` evita necesitar geometria de colision real — el fix no toca ese
/// camino, `apply_client_authoritative_move` lo ejecuta igual con o sin colision.
#[test]
fn restore_snap_window_blocks_the_stale_client_position_then_admits_it_after_expiry() {
    let world = World::new(42);
    let hydrate_tick = 200u64;
    let suppressed_until = Some(hydrate_tick + RESTORE_SNAP_SUPPRESS_TICKS);
    let dt = 1.0 / 60.0;

    let mut player = Player::new(1, "Joiner");
    let hydrated_position = Vec3::new(10.0, 1.8, 10.0);
    player.position = hydrated_position; // lo que apply_player_snapshot acaba de fijar

    let stale_client_input = PlayerInput {
        position: [999.0, 1.8, 999.0],
        input_seq: 1,
        ..Default::default()
    };

    // Mismo tick que la hidratacion — el clobber exacto que el fix evita.
    if !movement_suppressed(hydrate_tick, suppressed_until) {
        apply_movement(
            &mut player,
            &stale_client_input,
            dt,
            &world,
            hydrate_tick,
            true,
        );
    }
    assert_eq!(
        player.position, hydrated_position,
        "no debe pisarse durante la ventana de supresion"
    );

    // Mitad de la ventana.
    let mid_tick = hydrate_tick + 10;
    if !movement_suppressed(mid_tick, suppressed_until) {
        apply_movement(&mut player, &stale_client_input, dt, &world, mid_tick, true);
    }
    assert_eq!(
        player.position, hydrated_position,
        "sigue suprimido a mitad de ventana"
    );

    // Control negativo: ventana expirada, el cliente vuelve a ganar.
    let after_tick = hydrate_tick + RESTORE_SNAP_SUPPRESS_TICKS;
    if !movement_suppressed(after_tick, suppressed_until) {
        apply_movement(
            &mut player,
            &stale_client_input,
            dt,
            &world,
            after_tick,
            true,
        );
    }
    assert_eq!(
        player.position,
        Vec3::new(999.0, 1.8, 999.0),
        "tras la ventana, apply_movement debe volver a aplicar la posicion del cliente"
    );
}

/// Contrato del drain de stamina de ADR-009, por el lado del servidor. El bug real vivia en el
/// CLIENTE (`PlayerPoseTransmitter` clasificaba `move_state` solo como 0/1 y nunca 2, asi que
/// la barra jamas bajaba estando conectado), pero la mitad servidor no tenia guarda ninguna:
/// nada impedia que un refactor futuro cambiara el valor sobre el que se gatea y volviera a
/// dejar el drain muerto sin que fallara un test. Esto lo fija: 2 drena, 1 y 0 no.
/// `god_traversal=true` por el mismo motivo que el test de arriba — el drain corre igual con o
/// sin geometria de colision y asi no hay que fabricar un mundo con celdas libres.
#[test]
fn only_the_run_move_state_drains_stamina() {
    let world = World::new(42);
    let dt = 1.0 / 60.0;

    // Control negativo primero: caminar (1) e idle (0) NO pueden tocar la stamina.
    for state in [0u8, 1u8] {
        let mut player = Player::new(1, "Runner");
        let full = player.stats.stamina;
        let input = PlayerInput {
            position: [5.0, 1.8, 5.0],
            move_state: state,
            input_seq: 1,
            ..Default::default()
        };
        apply_movement(&mut player, &input, dt, &world, 1, true);
        assert_eq!(
            player.stats.stamina, full,
            "move_state={state} no debe drenar stamina"
        );
    }

    // Correr (2) drena exactamente RUN_STAMINA_DRAIN por segundo.
    let mut player = Player::new(1, "Runner");
    let full = player.stats.stamina;
    let input = PlayerInput {
        position: [5.0, 1.8, 5.0],
        move_state: 2,
        input_seq: 1,
        ..Default::default()
    };
    apply_movement(&mut player, &input, dt, &world, 1, true);
    let drained = full - player.stats.stamina;
    assert!(
        (drained - RUN_STAMINA_DRAIN * dt).abs() < 1e-4,
        "un tick corriendo debe drenar RUN_STAMINA_DRAIN*dt ({}), drenó {}",
        RUN_STAMINA_DRAIN * dt,
        drained
    );
}

// P0-2: `resolve_phantom_density_scale` es la misma regla de precedencia que world_seed
// (game_loop.rs:270-280) extraída a función pura, precisamente para poder testearla sin
// levantar el loop entero — mismo motivo que llevó a mover el gate de `broadcast_chunk_states`
// DENTRO de la función en P0-1.

#[test]
fn resolve_phantom_density_scale_keeps_launch_value_without_a_save() {
    assert_eq!(resolve_phantom_density_scale(3.0, None), 3.0);
}

#[test]
fn resolve_phantom_density_scale_the_save_wins_when_it_differs_from_launch_env() {
    let mut save = crate::persistence::save::SaveFile::new("s", 42);
    save.phantom_density_scale = 5.0;
    // El env de lanzamiento (3.0) NUNCA debe pisar lo persistido — env divergente al cargar no
    // pisa los params, misma regla que world_seed.
    assert_eq!(resolve_phantom_density_scale(3.0, Some(&save)), 5.0);
}

#[test]
fn resolve_phantom_density_scale_agrees_when_save_and_launch_match() {
    let mut save = crate::persistence::save::SaveFile::new("s", 42);
    save.phantom_density_scale = 1.0;
    assert_eq!(resolve_phantom_density_scale(1.0, Some(&save)), 1.0);
}

// P0-3: antes, un `world_seed` divergente entre el save y el env de lanzamiento era un warn +
// adopción silenciosa del valor del save — la misma clase de degradación silenciosa que este
// commit existe para cerrar. Ahora es un `save_world_seed_conflicts` que el call site en `run()`
// convierte en salida fatal; aquí se testea solo la decisión, sin matar el proceso de test.

#[test]
fn save_world_seed_conflicts_when_save_and_launch_disagree() {
    let save = crate::persistence::save::SaveFile::new("s", 42);
    assert!(save_world_seed_conflicts(&save, 99));
}

#[test]
fn save_world_seed_does_not_conflict_when_they_agree() {
    let save = crate::persistence::save::SaveFile::new("s", 42);
    assert!(!save_world_seed_conflicts(&save, 42));
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
        settling: false,
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
    // ADR-051 point 1 — STATUE NO LONGER REVEALS, and this reversal is the play-test's doing.
    // It is entered by LOOKING at the creature from close by, with hunger playing no part, so a
    // sated one — the kind built to follow you and copy you — tore out of its skin because you
    // turned to look at it, and put it back on when you turned away. It now stares WEARING the
    // face, which is worse.
    assert!(!phantom_reveals(PhantomState::Statue));
    // …and the warning does not reveal either: what makes that beat work is that the thing
    // screaming at you still looks like one of your own (ADR-051 point 2).
    assert!(!phantom_reveals(PhantomState::Unmasking));
    // The unmasked hunt does, and never re-dresses until it loses you (point 5).
    assert!(phantom_reveals(PhantomState::Hunting));
    assert!(!phantom_reveals(PhantomState::Wander));
    assert!(!phantom_reveals(PhantomState::Spotted));
    assert!(!phantom_reveals(PhantomState::Stalk));
    // SEARCH does NOT reveal: it has lost you, so it puts the skin back on and goes looking.
    // A revealed creature wandering around searching would give away its own game.
    assert!(!phantom_reveals(PhantomState::Search));
    // ADR-050 point 7 — FLEE does NOT reveal either, and this one is a decision rather than an
    // omission. A peer that bolts when a gun goes off next to it is precisely what a real player
    // would do, so keeping the skin on means you never quite know whether you startled a teammate
    // or the thing wearing his face. That ambiguity is what ADR-016 exists to protect.
    assert!(!phantom_reveals(PhantomState::Flee));
    // ADR-050 point 10 — GRAB does reveal, and the contrast with FLEE is the point. There is no
    // ambiguity left to protect when it is holding you at arm's length.
    assert!(phantom_reveals(PhantomState::Grab));

    // And the guard that makes this test worth having: `phantom_reveals` is a `matches!`, so a
    // variant added later would default to "does not reveal" and every assertion above would still
    // pass without ever mentioning it. Enumerating exhaustively here is what forces the decision.
    for state in [
        PhantomState::Wander,
        PhantomState::Spotted,
        PhantomState::Stalk,
        PhantomState::Statue,
        PhantomState::Sprint,
        PhantomState::Search,
        PhantomState::Flee,
        PhantomState::Grab,
        PhantomState::Unmasking,
        PhantomState::Hunting,
    ] {
        // Exhaustive `match` with no wildcard: adding a variant stops compiling right here.
        let expected = match state {
            PhantomState::Sprint | PhantomState::Grab | PhantomState::Hunting => true,
            PhantomState::Wander
            | PhantomState::Spotted
            | PhantomState::Stalk
            | PhantomState::Statue
            | PhantomState::Search
            | PhantomState::Flee
            | PhantomState::Unmasking => false,
        };
        assert_eq!(
            phantom_reveals(state),
            expected,
            "{state:?} disagrees with the declared reveal set"
        );
    }
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
        props: Vec::new(),
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
            props: Vec::new(),
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

/// ADR-072: el snapshot de muerte trae el desgaste, y este es el punto de entrada donde se perdía.
/// Cubre también lo que NO debe entrar: un cliente viejo que no manda `props`, y una propiedad con
/// un valor que no es finito (envenenaría el desgaste con algo que ni se compara ni se guarda).
#[test]
fn parse_death_loot_reads_instance_properties_and_rejects_the_unusable() {
    let data = serde_json::json!({
        "items": [
            {
                "item_id": -8792658,
                "quantity": 1,
                "props": [
                    { "id": -8792658, "value": 0.4237 },
                    { "id": 6313314, "value": 1.0 },
                ],
            },
            // Sin la clave: un cliente anterior a este ADR. Vector vacío, no error.
            { "item_id": 99, "quantity": 2 },
            {
                "item_id": 7,
                "quantity": 1,
                "props": [
                    { "id": 1 },                    // sin valor → se cae
                    { "value": 0.5 },               // sin id → se cae
                    { "id": 2, "value": "medio" },  // valor no numérico → se cae
                ],
            },
        ],
    });

    let (_, _, items) = parse_death_loot(&data);
    assert_eq!(items.len(), 3);

    assert_eq!(items[0].props.len(), 2, "las dos propiedades buenas entran");
    assert_eq!(items[0].props[0].id, -8792658);
    assert!((items[0].props[0].value - 0.4237).abs() < 1e-9);

    assert!(
        items[1].props.is_empty(),
        "sin la clave `props` se degrada al comportamiento de siempre"
    );
    assert!(
        items[2].props.is_empty(),
        "las entradas incompletas o con valor no numérico se caen una a una"
    );
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

// ADR-045 Fase 3: a Fase-3-aware client's report_inventory ALSO populates inventory_v2, in
// the SAME action — no new IPC action name. container/slot/props round-trip; a legacy entry
// mixed into the same array (no container/slot) is skipped from v2 but still lands in
// stp_inventory (both parses read the same array independently).
#[tokio::test]
async fn report_inventory_with_container_and_slot_also_populates_inventory_v2() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut world = World::new(42);
    let mut player = Player::new(1, "Host");
    let (tx, _rx) = broadcast::channel(16);
    let mut processed: HashSet<(u16, u64)> = HashSet::new();

    let action = crate::ipc::PlayerAction {
        action_type: "report_inventory".into(),
        data: serde_json::json!({ "items": [
            {
                "item_id": -52379, "quantity": 2, "container": 1, "slot": 5,
                "props": [{ "id": 10, "value": 0.75 }],
            },
            { "item_id": 999, "quantity": 1 }, // legacy shape, no container/slot
        ] }),
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

    // Legacy parse sees BOTH entries (container/slot/props are extra keys it ignores).
    assert_eq!(player.stp_inventory.len(), 2);
    // v2 parse keeps only the entry that actually carries container+slot.
    assert_eq!(player.inventory_v2.len(), 1);
    let stack = &player.inventory_v2[0];
    assert_eq!(stack.item_id, -52379);
    assert_eq!(stack.quantity, 2);
    assert_eq!(stack.container, 1);
    assert_eq!(stack.slot, 5);
    assert_eq!(stack.props.len(), 1);
    assert_eq!(stack.props[0].id, 10);
    assert!((stack.props[0].value - 0.75).abs() < 1e-9);
}

// ADR-045 Fase 3, requisito explícito de Joel: un cliente pre-Fase-3 (o uno Fase-3 que aún
// no reportó nada v2) deja inventory_v2 vacío y NO rompe report_inventory — mismo camino que
// siempre existió.
#[tokio::test]
async fn report_inventory_legacy_only_leaves_inventory_v2_empty() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut world = World::new(42);
    let mut player = Player::new(1, "Host");
    let (tx, _rx) = broadcast::channel(16);
    let mut processed: HashSet<(u16, u64)> = HashSet::new();

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
    assert!(
        player.inventory_v2.is_empty(),
        "legacy-shaped report must not fabricate v2 entries"
    );
}

// ADR-045 Fase 3: sanitize_inventory_v2_stacks drops zero-quantity entries and truncates to
// MAX_CORPSE_STACKS — same hygiene contract as sanitize_loot_stacks, own function because the
// backing type differs.
#[test]
fn sanitize_inventory_v2_stacks_drops_zeroes_then_truncates_to_cap() {
    let mut items = vec![crate::player::InventoryStackV2 {
        item_id: -1,
        quantity: 0,
        container: 0,
        slot: 0,
        props: vec![],
    }];
    for i in 0..(crate::world::corpse::MAX_CORPSE_STACKS as i32 + 6) {
        items.push(crate::player::InventoryStackV2 {
            item_id: i,
            quantity: 1,
            container: 0,
            slot: i as u8,
            props: vec![],
        });
    }
    sanitize_inventory_v2_stacks(&mut items);
    assert_eq!(items.len(), crate::world::corpse::MAX_CORPSE_STACKS);
    assert!(items.iter().all(|s| s.quantity > 0));
    assert_eq!(
        items[0].item_id, 0,
        "zero-qty stack must not consume a cap slot"
    );
}

// ADR-045 Fase 3: malformed/missing payload degrades to empty, never a panic — same contract
// parse_death_loot/parse_loot_stacks already have.
#[test]
fn parse_inventory_v2_stacks_degrades_to_empty_on_malformed_payload() {
    assert!(parse_inventory_v2_stacks(&serde_json::json!({})).is_empty());
    assert!(parse_inventory_v2_stacks(&serde_json::json!({ "items": "not an array" })).is_empty());
    // Missing "slot" on an otherwise-complete v2 entry disqualifies it (not a partial stack).
    let items = parse_inventory_v2_stacks(&serde_json::json!({
        "items": [{ "item_id": 1, "quantity": 1, "container": 0 }]
    }));
    assert!(items.is_empty());
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
            0,
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

    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);

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

    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);

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

    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);

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

    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);

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
        settling: false,
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

    driver.step(&mut net, 0.1, audience, 0.0, false, false, 0);

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
    // ADR-053: the calibration knobs get the same shielding as the population ones — a stray
    // `PHANTOM_HUNGER_SATED` in the developer's shell must not quietly change what a test asserts.
    d.hunger_drain_seconds = PHANTOM_HUNGER_DRAIN_SECONDS;
    d.sated_threshold = PHANTOM_HUNGER_SATED;
    d.unmask_seconds = PHANTOM_UNMASK_SECONDS;
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

    driver.step(&mut net, 0.1, player, player_yaw, false, false, 0);

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

    driver.step(&mut net, 0.1, player, player_yaw, false, false, 0);

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
        driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
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

    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);

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
        driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
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

    driver.step(&mut net, 0.1, player, 90.0, false, false, 0);

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
            0,
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

    // Facing +X, i.e. AWAY from the creature to its west → taken from behind.
    // ADR-050 point 9: that opens a GRAB, and the kill lands when its window runs out. Run the
    // window through so this test still measures what it is about — what a kill leaves behind.
    let mut killed = false;
    for _ in 0..((PHANTOM_GRAB_SECONDS / 0.1) as i32 + 3) {
        killed = driver
            .step(&mut net, 0.1, player, 90.0, false, false, 0)
            .iter()
            .any(|a| a.kind == PhantomAttackKind::Kill);
        if killed {
            break;
        }
    }
    assert!(killed, "expected the grab to become a kill");
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

    driver.step(
        &mut net,
        0.1,
        Vec3::new(0.0, 1.8, 0.0),
        0.0,
        false,
        false,
        0,
    );

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

    let attacks = driver.step(&mut net, 0.1, player, player_yaw, false, false, 0);

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

    let attacks = driver.step(&mut net, 0.1, player, 270.0, false, false, 0);

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

    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);

    assert_eq!(
        driver.movers[0].state,
        PhantomState::Hunting,
        "a lunge that cannot make progress must disengage"
    );
    // ADR-051 point 5: disengaging is NOT re-dressing. It backs off from the wall it could not get
    // through and keeps hunting you with the skin off — the flicker this replaced was the creature
    // dropping into a clothed `Stalk` after every failed lunge.
    assert!(
        phantom_reveals(driver.movers[0].state),
        "a failed lunge must not hand the disguise back"
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

    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);

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
        driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
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

    let attacks = driver.step(&mut net, 0.1, player, player_yaw, false, true, 0);

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

    let attacks = driver.step(&mut net, 0.1, player, player_yaw, false, false, 0);

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

    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);

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

    driver.step(&mut net, 0.1, player, 0.0, true, false, 0); // crouched

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
        driver.step(&mut net, 0.1, player, 0.0, false, false, 0);

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
        0,
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
        0,
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
        0,
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
        0,
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
async fn phantom_sprint_grabs_from_behind() {
    // ADR-016 slice 1 + ADR-050 point 9: a point-blank SPRINT while the player is NOT looking
    // (phantom behind) used to be an instant lethal `Kill`. It is now a `GrabStart` — the death
    // still comes, but from `tick_grab` once its window expires, and the victim is alive until
    // then. See `a_grab_that_runs_out_of_time_kills_and_feeds` for the other half.
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

    let attack = driver.step(&mut net, 0.1, player, player_yaw, false, false, 0);

    assert_eq!(
        attack,
        [PhantomAttack {
            victim: net.local_id,
            kind: PhantomAttackKind::GrabStart(PHANTOM_GRAB_SECONDS)
        }],
        "behind-attack must GRAB the local player, got {attack:?}"
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
    let attacks = driver.step(&mut net, 0.1, host_far, 0.0, false, false, 0);

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

    let attack = driver.step(&mut net, 0.1, player, player_yaw, false, false, 0);

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
        .step(&mut net, 0.1, player, player_yaw, false, false, 0)
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
            .step(&mut net, 0.1, player, player_yaw, false, false, 0)
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
        driver.step(&mut net, 0.1, player, player_yaw, false, false, 0);
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
        driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
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

    let attack = driver.step(&mut net, 0.1, player, 0.0, false, false, 0);

    assert!(
        matches!(attack, [PhantomAttack { victim, kind: PhantomAttackKind::Knockback(_, _) }]
            if *victim == net.local_id),
        "point-blank STATUE timeout must shove the local player, got {attack:?}"
    );
    // ADR-051 points 2-3: a hungry creature bored of the statue game does NOT charge straight away
    // any more — it comes apart first, still wearing the face, screaming. The charge is what
    // happens at the end of that beat.
    assert_eq!(driver.movers[0].state, PhantomState::Unmasking);
    assert!(
        !net.peers[&pid].revealed,
        "the warning must still look like a player, or it stops being a warning"
    );
    assert_eq!(net.peers[&pid].vocal_kind, VOCAL_UNMASK_SCREAM);

    // …and at the end of it the skin tears and it comes.
    for _ in 0..((PHANTOM_UNMASK_SECONDS / 0.1) as i32 + 2) {
        driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
    }
    assert_eq!(driver.movers[0].state, PhantomState::Sprint);
    assert!(
        net.peers[&pid].revealed,
        "the tear is the false→true edge the client decorates"
    );
    // REGRESSION — the tear used to be SILENT. `enter_sprint` emits VOCAL_REVEAL, but this state
    // screams on entry and the shared vocal budget (6 s) outlasts the unmask beat (1.6 s), so the
    // scream at the climax of the sequence was swallowed every single time.
    assert_eq!(
        net.peers[&pid].vocal_kind, VOCAL_REVEAL,
        "the skin breaking has to be HEARD, not just seen"
    );
}

/// Sets up a creature holding the host player, i.e. the tick right after a blow from behind.
/// Returns the driver and net with the grab already open.
async fn grabbed_setup() -> (NetworkManager, PhantomDriver, PeerId, Vec3) {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].hunger = 0.0;
    driver.movers[0].state = PhantomState::Sprint;
    driver.movers[0].hesitate_timer = 0.0;
    let here = Vec3::from_array(net.peers[&pid].position);
    // Point-blank and facing +X, i.e. AWAY from the creature to its west → taken from behind.
    let player = Vec3::new(here.x + 1.0, 1.8, here.z);
    let attacks = driver.step(&mut net, 0.1, player, 90.0, false, false, 0);
    assert!(
        attacks
            .iter()
            .any(|a| matches!(a.kind, PhantomAttackKind::GrabStart(_))),
        "expected a grab, got {attacks:?}"
    );
    (net, driver, pid, player)
}

#[tokio::test]
async fn a_blow_from_behind_grabs_instead_of_killing_outright() {
    // ADR-050 point 9 — the whole reason the grab exists. This used to be an instant 100 damage
    // applied in the same tick it was decided, with the client's animation running afterwards as a
    // 0.9 s epilogue that read no input: there was no instant at which you were held and alive, so
    // there was nothing to escape from.
    let (net, driver, pid, _) = grabbed_setup().await;
    assert_eq!(driver.movers[0].state, PhantomState::Grab);
    assert_eq!(driver.movers[0].grab_victim, Some(net.local_id));
    assert!(driver.movers[0].grab_timer > 0.0, "the clock is running");
    // ADR-050 point 10: unlike FLEE, this one reveals — it is holding you at arm's length.
    assert!(
        net.peers[&pid].revealed,
        "a creature holding you has nothing left to hide"
    );
}

#[tokio::test]
async fn a_grab_that_runs_out_of_time_kills_and_feeds() {
    // The death did not disappear, it moved: `tick_grab` owns it now.
    let (mut net, mut driver, pid, player) = grabbed_setup().await;

    // Stop ON the kill: in production the victim is dead and respawns elsewhere, but this test's
    // player is a fixed point 1 m away, so extra ticks would just have it notice them again.
    let mut killed = false;
    for _ in 0..((PHANTOM_GRAB_SECONDS / 0.1) as i32 + 3) {
        killed = driver
            .step(&mut net, 0.1, player, 90.0, false, false, 0)
            .iter()
            .any(|a| a.kind == PhantomAttackKind::Kill);
        if killed {
            break;
        }
    }
    assert!(killed, "the grab must become a kill when nobody breaks it");
    assert_eq!(driver.movers[0].state, PhantomState::Wander);
    assert_eq!(driver.movers[0].hunger, 1.0, "and it feeds");
    assert_eq!(driver.movers[0].grab_victim, None);
    assert_eq!(
        net.peers[&pid].vocal_kind, VOCAL_SATED_ROAR,
        "the roar still rides the kill — it is how the respawning victim learns it is not coming"
    );
}

#[tokio::test]
async fn struggling_free_costs_the_creature_its_meal() {
    // The other end of point 9. Breaking out must not feed it: you won the exchange, not the
    // fight, and it is still hungry and still there.
    let (mut net, mut driver, _, player) = grabbed_setup().await;
    let victim = net.local_id;

    net.pending_struggles.insert(victim);
    let attacks = driver.step(&mut net, 0.1, player, 90.0, false, false, 0);

    assert!(
        attacks
            .iter()
            .any(|a| a.kind == PhantomAttackKind::GrabRelease),
        "expected a release, got {attacks:?}"
    );
    // ADR-051 point 5: shaking it off does not hand the skin back either. You are still being
    // hunted, now by something you can see.
    assert_eq!(driver.movers[0].state, PhantomState::Hunting);
    assert!(phantom_reveals(driver.movers[0].state));
    assert_eq!(
        driver.movers[0].hunger, 0.0,
        "shaking it off must NOT feed it"
    );
    assert!(
        driver.movers[0].strike_recover > 0.0,
        "and it owes a recovery, or the failed grab becomes another one immediately"
    );
    assert!(
        net.pending_struggles.is_empty(),
        "the report is consumed, not left to release the next grab too"
    );

    // Run it out: with the window gone, no kill can arrive from this grab any more.
    let mut killed = false;
    for _ in 0..40 {
        killed |= driver
            .step(&mut net, 0.1, player, 90.0, false, false, 0)
            .iter()
            .any(|a| a.kind == PhantomAttackKind::Kill);
    }
    assert!(!killed, "a broken grab must never still land its kill");
}

#[tokio::test]
async fn one_players_struggle_does_not_free_another() {
    // `pending_struggles` is keyed BY VICTIM rather than being a flag, and this is why: with two
    // creatures holding two players, a flag would let either report release both.
    let (mut net, mut driver, _, player) = grabbed_setup().await;

    // Somebody else entirely reports a struggle.
    net.pending_struggles.insert(4242);
    let attacks = driver.step(&mut net, 0.1, player, 90.0, false, false, 0);

    assert!(
        !attacks
            .iter()
            .any(|a| a.kind == PhantomAttackKind::GrabRelease),
        "a stranger's struggle must not open this grab"
    );
    assert_eq!(
        driver.movers[0].state,
        PhantomState::Grab,
        "it still has you"
    );
}

#[tokio::test]
async fn a_gunshot_across_the_map_does_not_make_it_let_go() {
    // ADR-050 lists `hear_noises` as one of the four sites the compiler cannot catch. A creature
    // that dropped the player it is killing because somebody fired somewhere else would be
    // abandoning the one thing it committed to.
    let (mut net, mut driver, pid, _) = grabbed_setup().await;
    let here = Vec3::from_array(net.peers[&pid].position);

    net.pending_noises
        .push(([here.x + 300.0, here.y, here.z], 500.0));
    driver.hear_noises(&mut net);

    assert_eq!(
        driver.movers[0].state,
        PhantomState::Grab,
        "a noise must not interrupt a grab"
    );
    assert!(driver.movers[0].grab_victim.is_some());
}

#[tokio::test]
async fn the_same_shot_scares_a_full_creature_and_summons_a_hungry_one() {
    // ADR-050 point 6 — the clearest reading of the hunger model the player ever gets, and it costs
    // nothing: no UI, no wire, just which way the thing runs.
    async fn shot_near(hunger: f32) -> (PhantomState, Option<Vec3>) {
        let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
        let start = [0.0, 1.8, 0.0];
        let pid = net.spawn_phantom("Robapieles_Test", start);
        let mut driver = PhantomDriver::new(42);
        driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
        driver.movers[0].hunger = hunger;
        let here = Vec3::from_array(net.peers[&pid].position);
        // 20 m away: inside PHANTOM_RAGE_MAX_DISTANCE (70), so both branches are reachable and the
        // only thing deciding between them is how full the creature is.
        net.pending_noises
            .push(([here.x + 20.0, here.y, here.z], 500.0));
        driver.hear_noises(&mut net);
        (driver.movers[0].state, driver.movers[0].flee_goal)
    }

    let (sated_state, flee_goal) = shot_near(1.0).await;
    assert_eq!(
        sated_state,
        PhantomState::Flee,
        "a creature that has just eaten bolts from a gunshot"
    );
    // And it runs AWAY: the noise came from +X, so the goal must be on the -X side.
    let goal = flee_goal.expect("a fleeing creature needs somewhere to run");
    assert!(
        goal.x < 0.0,
        "it must flee AWAY from the shot, goal was {goal:?}"
    );

    let (hungry_state, _) = shot_near(0.0).await;
    assert_eq!(
        hungry_state,
        PhantomState::Search,
        "a hungry one comes to look instead"
    );
}

#[tokio::test]
async fn a_burst_of_fire_does_not_restart_the_same_scare_forever() {
    // ADR-050 names this as one of the four sites the compiler cannot catch. `hear_noises` skips
    // states that must not be distracted, and FLEE has to be on that list: a burst is MANY noises,
    // so without it every shot after the first would re-arm the timer and re-aim the goal at a
    // creature already running, and it would flee forever and never settle.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].hunger = 1.0;
    let here = Vec3::from_array(net.peers[&pid].position);

    net.pending_noises
        .push(([here.x + 20.0, here.y, here.z], 500.0));
    driver.hear_noises(&mut net);
    assert_eq!(driver.movers[0].state, PhantomState::Flee);
    let first_goal = driver.movers[0].flee_goal.unwrap();

    // Keep firing while it runs. Half the scare's worth of ticks, with a fresh shot on each.
    for _ in 0..30 {
        let p = Vec3::from_array(net.peers[&pid].position);
        net.pending_noises.push(([p.x + 20.0, p.y, p.z], 500.0));
        driver.hear_noises(&mut net);
        driver.step(
            &mut net,
            0.1,
            Vec3::new(1e5, 1.8, 1e5),
            0.0,
            false,
            false,
            0,
        );
    }
    assert_eq!(
        driver.movers[0].flee_goal,
        Some(first_goal),
        "later shots must not re-aim a scare already in progress"
    );
    // 30 ticks of 0.1 accumulate to 2.9999993, not 3.0 — the point is that the clock was never
    // rewound, not its exact value.
    assert!(
        driver.movers[0].state_timer > 2.9,
        "…nor rewind its clock: the timer must have kept running, got {}",
        driver.movers[0].state_timer
    );

    // And it does settle, rather than fleeing forever.
    for _ in 0..40 {
        driver.step(
            &mut net,
            0.1,
            Vec3::new(1e5, 1.8, 1e5),
            0.0,
            false,
            false,
            0,
        );
    }
    assert_eq!(
        driver.movers[0].state,
        PhantomState::Wander,
        "the scare has to end on its own"
    );
    assert_eq!(
        driver.movers[0].flee_goal, None,
        "and clear up after itself"
    );
}

#[tokio::test]
async fn a_sated_creature_wears_your_pose_a_beat_late() {
    // ADR-050 point 8. The delay IS the effect: a perfect mirror reads as a network bug, something
    // that crouches a beat after you do reads as being imitated. Rides `seal_cosmetics`, the site
    // already established for the driver to write cosmetics, so there is no wire and no client code.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].hunger = 1.0; // sated: it follows and copies
    driver.movers[0].state = PhantomState::Stalk;
    driver.movers[0].statue_cooldown = 999.0; // keep it out of STATUE; this is about the pose
    let here = Vec3::from_array(net.peers[&pid].position);
    let player = Vec3::new(here.x + 10.0, 1.8, here.z);

    // The host's own local player is the target. Crouching is relayed for peers but handed in for
    // the host, so this drives it through the parameter.
    driver.step(&mut net, 0.1, player, 0.0, true, false, 0);
    assert!(
        !net.peers[&pid].crouch,
        "it must NOT mirror instantly — the lag is the whole effect"
    );

    // Past the delay, it is wearing the pose.
    for _ in 0..12 {
        driver.step(&mut net, 0.1, player, 0.0, true, false, 0);
    }
    assert!(
        net.peers[&pid].crouch,
        "after PHANTOM_MIMIC_DELAY it copies you"
    );

    // Stand up: it follows you back up, also a beat late.
    for _ in 0..12 {
        driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
    }
    assert!(!net.peers[&pid].crouch, "and it copies you standing too");
}

#[tokio::test]
async fn a_hungry_creature_does_not_imitate_anyone() {
    // The counterpart of the test above, and the reason it is a separate one: imitation is a
    // SATED-band behaviour. A hungry creature that copied your pose would be spending the disguise
    // on the band where it is about to stop mattering.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].hunger = 0.0;
    driver.movers[0].state = PhantomState::Stalk;
    driver.movers[0].statue_cooldown = 999.0;
    let here = Vec3::from_array(net.peers[&pid].position);
    let player = Vec3::new(here.x + 10.0, 1.8, here.z);

    for _ in 0..30 {
        driver.step(&mut net, 0.1, player, 0.0, true, false, 0);
    }
    assert!(
        !net.peers[&pid].crouch,
        "a hungry creature keeps its own posture"
    );
    assert_eq!(
        net.peers[&pid].held_item, 0,
        "and its own empty hands (ADR-016's default for the phantom)"
    );
}

#[tokio::test]
async fn a_charge_blows_and_recovers_without_ever_ending_the_hunt() {
    // ADR-050 point 5. The chase gets a pulse — flat out, blown, heavy walk, flat out again — and
    // the LAST assertion is the load-bearing one: running out of breath must never be an exit from
    // SPRINT. A lunge that ends on a timer is exactly what the 2026-08-03 pass removed, and this is
    // the obvious way to reintroduce it by accident.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    // Entered through the REAL door, not by assigning the state: `enter_sprint` emits the reveal
    // scream, and that scream holding the shared vocal budget is exactly what used to swallow the
    // gasp below. A test that skipped it passed while the game was silent.
    driver.movers[0].enter_sprint();
    driver.movers[0].hesitate_timer = 0.0;
    // A TREADMILL: the player is re-placed 12 m ahead of the creature every tick, which is what a
    // sustained chase looks like from the stamina's point of view. The alternatives both measure
    // the wrong thing — a static target is caught in two seconds (so the test becomes about the
    // strike and the wedge detector), and a player fleeing in a straight line walks through walls
    // the creature has to go around, so it drops out of LOSE_RADIUS on geometry rather than on
    // speed. 12 m is clear of the 2.4 m strike reach and well inside the 30 m leash.
    let here = Vec3::from_array(net.peers[&pid].position);
    let mut player = Vec3::new(here.x + 12.0, 1.8, here.z);
    // Losing the line through generated geometry is a DIFFERENT exit with its own test
    // (`breaking_the_line_of_sight_ends_a_committed_hunt`). Pinned out so this one is about the
    // stamina and nothing else.
    macro_rules! chase_tick {
        () => {{
            driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
            driver.movers[0].sprint_blind_for = 0.0;
            let p = Vec3::from_array(net.peers[&pid].position);
            player = Vec3::new(p.x + 12.0, 1.8, p.z);
        }};
    }

    // Burst: 5,5 s at 10 Hz, i.e. just past `PHANTOM_SPRINT_BURST_SECONDS`.
    for _ in 0..55 {
        chase_tick!();
    }
    assert_eq!(driver.movers[0].state, PhantomState::Sprint);
    assert!(
        driver.movers[0].winded_for > 0.0,
        "5 s of flat-out running must blow it, stamina was {}",
        driver.movers[0].stamina
    );
    // REGRESSION — this was inaudible in practice: the burst (5 s) is shorter than the shared vocal
    // budget (6 s), so the REVEAL scream that opens every lunge was still holding the slot when the
    // creature blew, and the gasp got dropped nearly every time.
    assert_eq!(
        net.peers[&pid].vocal_kind, VOCAL_WINDED,
        "and the player has to HEAR the charge fail, or the window is invisible"
    );

    // Recovery: it keeps coming, and comes back to a full burst on the far side.
    for _ in 0..40 {
        chase_tick!();
        assert_eq!(
            driver.movers[0].state,
            PhantomState::Sprint,
            "being winded must NEVER end the hunt — only the player can"
        );
    }
    assert_eq!(driver.movers[0].winded_for, 0.0, "it got its breath back");
    assert!(
        driver.movers[0].stamina > 0.0,
        "and the next burst is armed"
    );
    assert!(
        net.peers[&pid].revealed,
        "still revealed the whole way through: it never stopped charging"
    );
}

#[tokio::test]
async fn a_sated_creature_stares_at_you_without_ever_breaking_its_skin() {
    // ADR-051 points 1 and 4 — THE PLAY-TEST BUG. Reported as "a veces me está copiando y se rompe
    // la piel". `Statue` used to reveal, and `Statue` is entered by LOOKING at the creature from
    // close by with hunger playing no part, so a sated one tore out of its skin because you turned
    // your head, and dressed again when you turned away. The disguise was falling to a camera
    // angle. Now: stare at a full one as long as you like — it stares back, wearing your friend's
    // face, and nothing comes off.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].hunger = 1.0; // just fed: it is here to follow and copy, not to eat
    driver.movers[0].state = PhantomState::Stalk;
    let here = Vec3::from_array(net.peers[&pid].position);
    // 6 m and looking straight at it — the exact setup that triggers STATUE.
    let player = Vec3::new(here.x + 6.0, 1.8, here.z);

    let mut saw_statue = false;
    for _ in 0..200 {
        driver.step(&mut net, 0.1, player, 270.0, false, false, 0);
        saw_statue |= driver.movers[0].state == PhantomState::Statue;
        assert!(
            !net.peers[&pid].revealed,
            "a sated creature must NEVER break its skin, it was in {:?}",
            driver.movers[0].state
        );
        assert_ne!(driver.movers[0].state, PhantomState::Unmasking);
    }
    assert!(
        saw_statue,
        "the test is vacuous unless it actually entered STATUE"
    );
}

#[tokio::test]
async fn the_skin_only_breaks_after_the_warning_and_never_grows_back_mid_hunt() {
    // ADR-051 points 2, 3 and 5 — the sequence Joel described: it stares, it goes still and
    // screams WITH THE FACE ON, and only then does the skin tear and it comes for you. And once
    // torn, a failed lunge does not hand it back.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].hunger = 0.0; // starving: this one has decided
    driver.movers[0].state = PhantomState::Statue;
    driver.movers[0].state_timer = PHANTOM_STATUE_MAX + 1.0;
    let here = Vec3::from_array(net.peers[&pid].position);
    let player = Vec3::new(here.x + 8.0, 1.8, here.z);

    // The warning: still dressed, still screaming.
    driver.step(&mut net, 0.1, player, 270.0, false, false, 0);
    assert_eq!(driver.movers[0].state, PhantomState::Unmasking);
    assert!(!net.peers[&pid].revealed, "the warning wears the face");

    // Through the beat: it must NOT reveal early, or the warning stops being one.
    let beat = (PHANTOM_UNMASK_SECONDS / 0.1) as i32 - 2;
    for _ in 0..beat {
        driver.step(&mut net, 0.1, player, 270.0, false, false, 0);
        assert!(!net.peers[&pid].revealed, "revealed BEFORE the tear");
    }

    // The tear.
    for _ in 0..4 {
        driver.step(&mut net, 0.1, player, 270.0, false, false, 0);
    }
    assert!(net.peers[&pid].revealed, "the skin must break at the end");

    // And it stays off: force the lunge to fail and confirm it lands in a REVEALED state rather
    // than dropping back into a clothed Stalk.
    driver.movers[0].blocked_ticks = PHANTOM_SPRINT_GIVEUP_TICKS;
    driver.step(&mut net, 0.1, player, 270.0, false, false, 0);
    assert_eq!(driver.movers[0].state, PhantomState::Hunting);
    assert!(
        net.peers[&pid].revealed,
        "a failed lunge must not grow the skin back"
    );

    // The ONLY way back into the disguise: losing you.
    let far = Vec3::new(here.x + 500.0, 1.8, here.z);
    driver.step(&mut net, 0.1, far, 270.0, false, false, 0);
    assert!(
        matches!(
            driver.movers[0].state,
            PhantomState::Search | PhantomState::Wander
        ),
        "losing contact is what re-dresses it, got {:?}",
        driver.movers[0].state
    );
    assert!(!net.peers[&pid].revealed, "…and then the skin is back on");
}

#[test]
fn it_remembers_where_you_hid_and_checks_there_before_giving_up() {
    // ADR-053 — the cheap, legible half of "let it learn": no model, a list of four places. Unit
    // test on the memory itself, because the FSM path around it is timing-heavy and this is where
    // the actual rules live.
    let mut driver = PhantomDriver::new(42);
    driver.add(0xF001, PHANTOM_INITIAL_HEADING, Vec3::ZERO, true);
    let m = &mut driver.movers[0];

    // Places where hunts ended get filed…
    m.remember_hideout(Vec3::new(10.0, 1.8, 0.0));
    m.remember_hideout(Vec3::new(60.0, 1.8, 0.0));
    assert_eq!(m.hideouts.len(), 2);

    // …and a second hunt ending in the SAME corner refreshes it instead of eating a slot, or four
    // slots fill with four corners of one room.
    m.remember_hideout(Vec3::new(13.0, 1.8, 0.0)); // within PHANTOM_HIDEOUT_MERGE_RADIUS of the first
    assert_eq!(
        m.hideouts.len(),
        2,
        "nearby spots must merge, not accumulate"
    );

    // Oldest out once full: the list tracks recent habits, not ancient ones.
    for x in [200.0, 260.0, 320.0, 380.0] {
        m.remember_hideout(Vec3::new(x, 1.8, 0.0));
    }
    assert_eq!(m.hideouts.len(), PHANTOM_HIDEOUT_MEMORY);
    assert!(
        !m.hideouts.iter().any(|h| h.x < 100.0),
        "the oldest memories must be the ones evicted"
    );

    // Recall picks the NEAREST worth a detour, ignores the spot it is standing on, and ignores
    // memories from the other side of the level.
    // Memories now: 200, 260, 320, 380.
    let here = Vec3::new(205.0, 1.8, 0.0);
    let got = m.recall_hideout(here).expect("something is in range");
    assert_eq!(got.x, 200.0, "nearest first");
    // Standing ON the only memory in range: nothing to detour to. It just searched here, and
    // walking one metre to re-search the same spot would be a creature stuck in a loop.
    assert!(
        m.recall_hideout(Vec3::new(321.0, 1.8, 0.0)).is_none(),
        "the spot underfoot does not count as a detour"
    );
    assert!(
        m.recall_hideout(Vec3::new(10_000.0, 1.8, 0.0)).is_none(),
        "a memory 10 km away is not a detour, it is a different hunt"
    );

    // And the per-hunt budget is what keeps hiding a real escape: it checks a couple of places,
    // not the whole building.
    m.hideouts_checked = PHANTOM_HIDEOUT_CHECKS_PER_HUNT;
    assert!(
        m.recall_hideout(here).is_none(),
        "out of checks means give up — otherwise a search never ends"
    );
}

#[tokio::test]
async fn an_unmasked_hunter_recovers_its_breath_and_does_not_ping_pong_into_sprint() {
    // TWO REGRESSIONS AT ONCE, both introduced by ADR-051 adding `Hunting` between SPRINT and the
    // rest of the FSM, and both of which made a creature stop being a threat.
    //
    // 1) `winded_for` was ticked ONLY inside `tick_sprint`. A lunge that gave up wedged left SPRINT
    //    while still out of breath, nothing decremented the timer any more, and `tick_hunting`'s
    //    re-lunge gate (`winded_for <= 0.0`) could never open again: it circled you forever,
    //    visible and harmless.
    // 2) The give-up cleared `strike_recover`, and `tick_hunting` re-lunges the moment that is
    //    spent — so it bounced Sprint→Hunting→Sprint every tick, screaming VOCAL_REVEAL on each
    //    bounce because `enter_sprint` vocalises.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    driver.movers[0].hunger = 0.0;
    driver.movers[0].state = PhantomState::Hunting;
    // Exactly the state a wedged give-up leaves behind: out of breath, in Hunting.
    driver.movers[0].winded_for = PHANTOM_SPRINT_RECOVER_SECONDS;
    driver.movers[0].strike_recover = 0.0;
    let here = Vec3::from_array(net.peers[&pid].position);
    let player = Vec3::new(here.x + 8.0, 1.8, here.z);

    // (1) The breath has to come back even though we never enter SPRINT.
    for _ in 0..((PHANTOM_SPRINT_RECOVER_SECONDS / 0.1) as i32 + 2) {
        driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
    }
    assert_eq!(
        driver.movers[0].winded_for, 0.0,
        "the recovery timer must drain outside SPRINT too, or it circles you forever"
    );

    // (2) And from a real give-up, it must NOT be back in SPRINT on the next tick.
    driver.movers[0].state = PhantomState::Sprint;
    driver.movers[0].blocked_ticks = PHANTOM_SPRINT_GIVEUP_TICKS;
    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
    assert_eq!(driver.movers[0].state, PhantomState::Hunting);
    assert!(
        driver.movers[0].strike_recover > 0.0,
        "giving up must cost something, or it grinds into the same wall forever"
    );
    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
    assert_eq!(
        driver.movers[0].state,
        PhantomState::Hunting,
        "it must circle for a beat before coming again, not bounce on the next tick"
    );

    // …and it DOES come again once that beat is spent — the fix must not make it passive.
    for _ in 0..((PHANTOM_STRIKE_RECOVERY / 0.1) as i32 + 2) {
        driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
    }
    assert_eq!(
        driver.movers[0].state,
        PhantomState::Sprint,
        "an unmasked hunter has to keep coming"
    );
}

#[tokio::test]
async fn it_throws_your_own_voice_back_at_you_but_only_while_disguised() {
    // ADR-053 — it already steals the name, the face and the posture; the voice was what was left.
    // The two gates ARE the effect: from something already revealed and on top of you it would be
    // noise, and the horror is specifically a voice you know coming out of a figure down a
    // corridor that still looks like your friend.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let start = [0.0, 1.8, 0.0];
    let pid = net.spawn_phantom("Robapieles_Test", start);
    let mut driver = PhantomDriver::new(42);
    driver.add(pid, PHANTOM_INITIAL_HEADING, Vec3::from_array(start), true);
    // Something we said is on file, which is what makes an echo possible at all.
    net.voice_echo.insert(net.local_id, vec![0xAA, 0xBB, 0xCC]);
    let here = Vec3::from_array(net.peers[&pid].position);
    let player = Vec3::new(here.x + 20.0, 1.8, here.z); // past PHANTOM_ECHO_MIN_DISTANCE

    // Due now, and dressed.
    driver.movers[0].echo_cooldown = 0.0;
    driver.movers[0].state = PhantomState::Stalk;
    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
    assert_eq!(
        driver.voice_echoes,
        vec![(pid, net.local_id)],
        "a disguised creature at a distance must give your voice back"
    );
    assert!(
        driver.movers[0].echo_cooldown > 0.0,
        "and then shut up for a long while"
    );

    // Revealed: no echo. It is past pretending, and this would step on the sounds that matter.
    driver.voice_echoes.clear();
    driver.movers[0].echo_cooldown = 0.0;
    driver.movers[0].state = PhantomState::Hunting;
    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
    assert!(
        driver.voice_echoes.is_empty(),
        "an unmasked creature does not do voices"
    );

    // Nothing on file: nothing to give back. A creature cannot invent a voice.
    net.voice_echo.clear();
    driver.voice_echoes.clear();
    driver.movers[0].echo_cooldown = 0.0;
    driver.movers[0].state = PhantomState::Stalk;
    driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
    assert!(driver.voice_echoes.is_empty());
}

#[tokio::test]
async fn nothing_ever_wakes_up_in_your_face() {
    // Reported from play-test: "cuando me salgo y vuelvo a entrar las entidades spawnean en mi
    // cara". Reconnecting is the worst case — no creature is awake, so the whole neighbourhood is
    // eligible on one tick — but walking into a fresh block has the same hole.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut driver = population_driver(42, 24);
    // Dense, so the draw definitely has something anchored near wherever we stand.
    driver.density_scale = 40.0;

    // Sweep a grid of arrival points: any one of them could be the unlucky spot.
    for gx in 0..6i32 {
        for gz in 0..6i32 {
            let here = Vec3::new(gx as f32 * 37.0, stand_on(0), gz as f32 * 37.0);
            // Only the ones that wake up on THIS arrival are under test. One that woke legitimately
            // far from a previous point and is now nearby is not a spawn in your face — it is a
            // creature that walked, which is exactly what it is supposed to do.
            let before: std::collections::HashSet<PeerId> =
                driver.movers.iter().map(|m| m.id).collect();
            driver.population_sync_in = 0.0; // force a reconcile on this arrival
            driver.sync_population(&mut net, here, 0.1);

            for m in driver.movers.iter().filter(|m| !before.contains(&m.id)) {
                let Some(peer) = net.peers.get(&m.id) else {
                    continue;
                };
                let d = here.distance_xz(Vec3::from_array(peer.position));
                assert!(
                    d >= PHANTOM_MIN_SPAWN_DISTANCE - 0.01,
                    "a creature woke {d:.1} m from the player at {here:?} — floor is \
                     {PHANTOM_MIN_SPAWN_DISTANCE}"
                );
            }
        }
    }
    assert!(
        !driver.movers.is_empty(),
        "the floor must not empty the world — it only pushes spawns outward"
    );
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
        driver.step(&mut net, 0.1, player, 0.0, false, false, 0);
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
        driver.step(&mut net, 0.1, away, 0.0, false, false, 0);
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
        0,
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

    let attacks = driver.step(&mut net, 0.1, player, 270.0, false, false, 0);

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
            props: Vec::new(),
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

// ── ADR-070: los objetos soltados caen ──

use crate::network::protocol::StpItemInfo;
use crate::network::SettlingItem;

/// Helper: un item en el aire y su entrada de simulacion, sin levantar red ni loop. El integrador
/// es una funcion pura del par (roster, estado de caida) + la cache de colision, que es
/// precisamente por lo que la fisica vive en el backend y no repartida por N clientes: se puede
/// afirmar sobre ella.
fn falling_item(id: u32, position: [f32; 3], velocity: Vec3) -> (StpItemInfo, SettlingItem) {
    (
        StpItemInfo {
            id,
            def_id: 1,
            count: 1,
            position,
            rotation: 0.0,
            settling: true,
        },
        SettlingItem {
            id,
            velocity,
            quiet_ticks: 0,
            age_ticks: 0,
        },
    )
}

/// Simula hasta que el item duerme o se agota la paciencia del test, y devuelve
/// (ticks consumidos, se durmio). Un test que no converge tiene que FALLAR, no colgarse.
fn settle_until_asleep(
    item: StpItemInfo,
    state: SettlingItem,
    max_steps: u32,
) -> (StpItemInfo, u32, bool) {
    let mut cache = crate::world::grid_gen::GridGenChunkCache::with_rules(
        42,
        crate::world::zone_density::rules_for,
    );
    let mut items = vec![state];
    let mut roster = vec![item];
    let dt = 1.0 / 60.0;
    for step in 0..max_steps {
        let asleep = settle_items_tick(&mut items, &mut roster, &mut cache, dt, 1);
        if !asleep.is_empty() {
            return (roster[0].clone(), step + 1, true);
        }
    }
    (roster[0].clone(), max_steps, false)
}

/// El comportamiento que Joel pidió, escrito como contrato: un objeto soltado en el aire CAE, y
/// acaba parado en el suelo. Antes de ADR-070 el cliente ya mandaba la posición pegada al suelo y
/// el objeto nacía congelado ahí — este test falla con aquel comportamiento porque no habría
/// ninguna caída que medir.
#[test]
fn a_dropped_item_falls_and_comes_to_rest_on_the_floor() {
    let drop_height = 6.0;
    let (item, state) = falling_item(1, [30.0, drop_height, 30.0], Vec3::new(0.0, 0.0, 0.0));
    let (rested, steps, asleep) = settle_until_asleep(item, state, 600);

    assert!(asleep, "el objeto tiene que dormirse, no simular sin fin");
    assert!(
        rested.position[1] < drop_height - 1.0,
        "tiene que haber CAÍDO, acabó en y={}",
        rested.position[1]
    );
    let floor = crate::world::grid_gen::grid_floor_y(crate::world::grid_gen::world_pos_to_layer(
        rested.position[1],
    ));
    assert!(
        (rested.position[1] - floor).abs() < 0.5,
        "tiene que reposar SOBRE el suelo de su capa (suelo={floor}, item={})",
        rested.position[1]
    );
    assert!(
        steps < 200,
        "una caída de {drop_height} m no puede tardar {steps} ticks en asentarse"
    );
}

/// El impulso importa: lanzar hacia un lado tiene que mover el objeto en horizontal. Sin esto,
/// `velocity` podría llegar entera hasta el integrador y no usarse, y la única señal sería que en
/// juego "se cae raro".
#[test]
fn the_throw_impulse_moves_the_item_sideways() {
    let start = [30.0, 5.0, 30.0];
    let (item, state) = falling_item(1, start, Vec3::new(4.0, 0.0, 0.0));
    let (rested, _, asleep) = settle_until_asleep(item, state, 600);

    assert!(asleep);
    assert!(
        rested.position[0] > start[0] + 0.5,
        "lanzado en +X tiene que acabar más allá de donde salió (x={} vs {})",
        rested.position[0],
        start[0]
    );

    // Control negativo: soltado sin impulso, en el mismo sitio, apenas se mueve en horizontal.
    let (item2, state2) = falling_item(2, start, Vec3::new(0.0, 0.0, 0.0));
    let (dropped, _, _) = settle_until_asleep(item2, state2, 600);
    assert!(
        (dropped.position[0] - start[0]).abs() < 0.5,
        "sin impulso no puede desplazarse solo, acabó en x={}",
        dropped.position[0]
    );
}

/// El presupuesto duro de la decisión 2. Un objeto al que se le da una velocidad absurda no puede
/// quedarse simulando para siempre: se duerme igual al agotar el presupuesto. Es la diferencia
/// entre un tope de diseño y confiar en que la física converja.
#[test]
fn a_never_settling_item_is_put_to_sleep_by_the_budget() {
    // Velocidad vertical enorme: sube durante segundos, así que la vía de "ticks tranquilos" no
    // puede ser la que lo duerma dentro del presupuesto.
    let (item, state) = falling_item(1, [30.0, 3.0, 30.0], Vec3::new(0.0, 400.0, 0.0));
    let (_, steps, asleep) = settle_until_asleep(item, state, (SETTLE_MAX_TICKS as u32) + 50);

    assert!(asleep, "el presupuesto tiene que dormirlo igualmente");
    assert_eq!(
        steps, SETTLE_MAX_TICKS as u32,
        "y tiene que dormirlo EXACTAMENTE al agotarse, ni antes ni después"
    );
}

/// Un objeto recogido a media caída desaparece del roster. La entrada de simulación tiene que
/// irse con él: si no, el integrador seguiría buscando un id que ya no existe en cada tick de cada
/// subpaso, para siempre.
#[test]
fn picking_an_item_up_mid_fall_drops_its_simulation_entry() {
    let mut cache = crate::world::grid_gen::GridGenChunkCache::with_rules(
        42,
        crate::world::zone_density::rules_for,
    );
    let (_, state) = falling_item(9, [30.0, 5.0, 30.0], Vec3::new(0.0, 0.0, 0.0));
    let mut items = vec![state];
    let mut roster: Vec<StpItemInfo> = Vec::new(); // recogido: ya no está en el roster

    let asleep = settle_items_tick(&mut items, &mut roster, &mut cache, 1.0 / 60.0, 1);
    assert!(
        items.is_empty(),
        "la entrada de simulación tiene que morir con el item"
    );
    assert!(
        asleep.is_empty(),
        "y NO cuenta como dormido: no hay nada a lo que limpiarle el flag"
    );
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

// ── ADR-069: la cama arma el respawn al CONSTRUIRSE, no al plantar el fantasma ──

/// El bug que ADR-069 corrige, escrito como contrato: plantar el fantasma solo puede armar el
/// PENDIENTE. Mientras `respawn_point` siga a None, `resolve_respawn` manda al arranque fijo —
/// que es exactamente lo que se ve en juego cuando la cama no está construida.
#[test]
fn a_pending_bed_alone_does_not_move_the_respawn() {
    let mut world = crate::world::World::new(1);
    insert_clean_flat_chunk(&mut world, (10, 10));
    let bed = Vec3::new(10.0 * CHUNK_SIZE + 25.0, 1.8, 10.0 * CHUNK_SIZE + 25.0);

    let mut player = Player::new(1, "Placer");
    player.pending_respawn_point = Some(bed); // lo que ahora hace `stp_place`

    let res = resolve_respawn(&mut world, player.respawn_point, player.id);
    assert_eq!(
        res.chunk,
        (0, 0),
        "un fantasma sin construir NO puede desviar el respawn de la cama de arranque"
    );
}

/// La promoción completa, con su control negativo: la confirmación de OTRA cama (la que llega
/// cuando un vecino termina la suya, porque el cliente las reporta todas) no puede tocar nada.
#[test]
fn bed_constructed_promotes_only_the_matching_pending_point() {
    let bed = Vec3::new(120.0, 1.8, 340.0);

    // Control negativo 1: sin pendiente no hay nada que promocionar.
    let mut nobody = Player::new(1, "Bystander");
    assert!(
        !promote_pending_respawn(&mut nobody, bed),
        "sin pendiente, la confirmación es un no-op"
    );
    assert_eq!(nobody.respawn_point, None);

    // Control negativo 2: pendiente propio, pero la cama terminada es otra (lejos).
    let mut other = Player::new(2, "Neighbour");
    other.pending_respawn_point = Some(Vec3::new(500.0, 1.8, 500.0));
    assert!(
        !promote_pending_respawn(&mut other, bed),
        "la cama de otro jugador no puede armar mi respawn"
    );
    assert_eq!(other.respawn_point, None);
    assert_eq!(
        other.pending_respawn_point,
        Some(Vec3::new(500.0, 1.8, 500.0)),
        "y tampoco puede consumir mi pendiente"
    );

    // Caso real: mi cama, terminada. Se promociona y el pendiente se consume (una sola vez).
    let mut placer = Player::new(3, "Placer");
    placer.pending_respawn_point = Some(bed);
    // La posición reportada llega del `StpBuildingInfo` del host, no del pendiente: se admite
    // dentro de BED_MATCH_RADIUS_M, no por igualdad exacta.
    let reported = Vec3::new(bed.x + 0.2, bed.y, bed.z - 0.2);
    assert!(promote_pending_respawn(&mut placer, reported));
    assert_eq!(
        placer.respawn_point,
        Some(bed),
        "se guarda la posición que el propio jugador reclamó al colocar, no la reportada"
    );
    assert_eq!(placer.pending_respawn_point, None);
    assert!(
        !promote_pending_respawn(&mut placer, reported),
        "el pendiente ya se consumió: los reportes repetidos (un cliente por cama) son no-ops"
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

/// Un save con el jugador muerto NO puede hidratarse tal cual: el cliente arranca sin DeathUI y
/// el edge de muerte re-anunciado se pierde contra un rig que aún no existe → jugador congelado
/// sin botón de respawn. Cargar muerto ES el respawn (mismo reset + recolocación que
/// `respawn_request`).
#[test]
fn a_dead_snapshot_revives_on_load() {
    use crate::persistence::save::PlayerSnapshot;

    let mut dead = Player::new(1, "Host");
    dead.stats.health = 0.0;
    dead.stats.hunger = 0.0;
    dead.stats.thirst = 0.0;
    dead.position = Vec3::new(999.0, 1.8, 999.0);

    let mut player = Player::new(1, "Host");
    let mut world = crate::world::World::new(1);
    apply_player_snapshot(&mut player, PlayerSnapshot::from_player(&dead));
    revive_if_dead_on_load(&mut player, &mut world);

    assert!(
        !player.stats.is_dead(),
        "cargar un save muerto debe revivir al jugador"
    );
    assert!((player.stats.health - 100.0).abs() < 1e-4);
    assert!(
        (player.stats.hunger - 100.0).abs() < 1e-4,
        "el revive usa on_respawn — hunger/thirst llenos, no los del save muerto"
    );
    assert_ne!(
        player.position,
        Vec3::new(999.0, 1.8, 999.0),
        "sin cama el revive recoloca en el starter, no en la posición de la muerte"
    );
}

/// Con cama puesta el revive delega en `resolve_respawn` igual que `respawn_request`: aterriza
/// en el chunk de la cama, no en el starter ni en la posición de la muerte.
#[test]
fn a_dead_snapshot_revives_at_the_bed_when_one_is_placed() {
    use crate::persistence::save::PlayerSnapshot;

    let mut dead = Player::new(1, "Host");
    dead.stats.health = 0.0;
    dead.position = Vec3::new(999.0, 1.8, 999.0);
    dead.respawn_point = Some(Vec3::new(
        10.0 * CHUNK_SIZE + 25.0,
        1.8,
        10.0 * CHUNK_SIZE + 25.0,
    ));

    let mut player = Player::new(1, "Host");
    let mut world = crate::world::World::new(1);
    insert_clean_flat_chunk(&mut world, (10, 10));
    apply_player_snapshot(&mut player, PlayerSnapshot::from_player(&dead));
    revive_if_dead_on_load(&mut player, &mut world);

    assert!(!player.stats.is_dead());
    let chunk = (
        (player.position.x / CHUNK_SIZE).floor() as i32,
        (player.position.z / CHUNK_SIZE).floor() as i32,
    );
    assert_eq!(
        chunk,
        (10, 10),
        "el revive con cama debe aterrizar en el chunk de la cama, got {:?}",
        player.position
    );
}

/// El guardián solo actúa sobre saves muertos: un save vivo (aunque tocado) hidrata intacto —
/// stats y posición del fichero, sin recolocación.
#[test]
fn an_alive_snapshot_hydrates_untouched() {
    use crate::persistence::save::PlayerSnapshot;

    let mut hurt = Player::new(1, "Host");
    hurt.stats.health = 61.0;
    hurt.stats.hunger = 5.0;
    hurt.position = Vec3::new(999.0, 1.8, 999.0);

    let mut player = Player::new(1, "Host");
    let mut world = crate::world::World::new(1);
    apply_player_snapshot(&mut player, PlayerSnapshot::from_player(&hurt));
    revive_if_dead_on_load(&mut player, &mut world);

    assert!((player.stats.health - 61.0).abs() < 1e-4);
    assert!((player.stats.hunger - 5.0).abs() < 1e-4);
    assert_eq!(player.position, Vec3::new(999.0, 1.8, 999.0));
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

// ─── ADR-056: fin de sesión cuando cae el host ───

/// Drains every event currently queued on the receiver, so a test can assert on what a handler
/// emitted without depending on the order of the other events it also sends.
fn drain_event_types(rx: &mut broadcast::Receiver<ServerMessage>) -> Vec<String> {
    let mut out = Vec::new();
    while let Ok(ServerMessage::Event(ev)) = rx.try_recv() {
        out.push(ev.event_type);
    }
    out
}

fn scratch_player_path(name: &str) -> std::path::PathBuf {
    let mut p = std::env::temp_dir();
    p.push(format!("backrooms_adr056_{name}.json"));
    let _ = std::fs::remove_file(&p);
    p
}

/// The whole point of ADR-056: when the peer that leaves is the HOST, the joiner's backend ends
/// the session — it persists the player file and tells Unity, which owns the teardown.
#[tokio::test]
async fn host_departure_saves_the_player_and_announces_session_ended() {
    let mut net = NetworkManager::bind(0, 0, 42, false).await.unwrap();
    let mut world = World::new(42);
    let mut player = Player::new(7, "Joiner");
    player.identity_key = Some("uuid:adr056".into());
    player.stats.health = 61.0;
    let (tx, mut rx) = broadcast::channel(16);
    let mut processed: HashSet<(u16, u64)> = HashSet::new();

    let host_id = 1;
    net.host_peer_id = Some(host_id);
    let path = scratch_player_path("host_departure");

    handle_network_event(
        NetworkEvent::PeerDisconnected {
            id: host_id,
            reason: "clean_shutdown".into(),
        },
        &mut player,
        &mut world,
        &mut net,
        &tx,
        &tx,
        &mut processed,
        0,
        Some(path.as_path()),
    )
    .await;

    let events = drain_event_types(&mut rx);
    assert!(
        events.iter().any(|e| e == "session_ended"),
        "the host leaving must announce the end of the session, got: {events:?}"
    );
    assert!(
        events.iter().any(|e| e == "player_left"),
        "and it must not swallow the generic departure event, got: {events:?}"
    );

    let saved = crate::persistence::player_save::load_or_fresh(&path)
        .expect("the player file must have been written before announcing");
    assert_eq!(saved.identity_key, "uuid:adr056");
    assert!(
        (saved.snapshot.stats.health - 61.0).abs() < 1e-4,
        "and it must carry this session's state, not a default"
    );
    let _ = std::fs::remove_file(&path);
}

/// Control negativo: ANY other peer leaving is an ordinary departure. Without this the feature
/// would end the session every time a joiner quits — the exact failure the `host_peer_id`
/// comparison exists to prevent.
#[tokio::test]
async fn a_non_host_peer_leaving_does_not_end_the_session() {
    let mut net = NetworkManager::bind(0, 0, 42, false).await.unwrap();
    let mut world = World::new(42);
    let mut player = Player::new(7, "Joiner");
    player.identity_key = Some("uuid:adr056b".into());
    let (tx, mut rx) = broadcast::channel(16);
    let mut processed: HashSet<(u16, u64)> = HashSet::new();

    net.host_peer_id = Some(1);
    let path = scratch_player_path("non_host_departure");

    handle_network_event(
        NetworkEvent::PeerDisconnected {
            id: 5,
            reason: "heartbeat timeout".into(),
        },
        &mut player,
        &mut world,
        &mut net,
        &tx,
        &tx,
        &mut processed,
        0,
        Some(path.as_path()),
    )
    .await;

    let events = drain_event_types(&mut rx);
    assert!(
        events.iter().any(|e| e == "player_left"),
        "another peer leaving is still reported, got: {events:?}"
    );
    assert!(
        !events.iter().any(|e| e == "session_ended"),
        "but it must NOT end the session, got: {events:?}"
    );
    assert!(
        !path.exists(),
        "and it must not write the player file either — that belongs to the shutdown path"
    );
}

/// A HOST's own backend has `host_peer_id == None`, so no departure can ever end its session.
/// Pinned because the comparison is against an `Option`: a `None == None` slip would make every
/// disconnect on the host end its own session.
#[tokio::test]
async fn the_host_never_ends_its_own_session_when_a_peer_leaves() {
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut world = World::new(42);
    let mut player = Player::new(1, "Host");
    let (tx, mut rx) = broadcast::channel(16);
    let mut processed: HashSet<(u16, u64)> = HashSet::new();

    assert!(net.host_peer_id.is_none(), "precondition: this IS the host");

    handle_network_event(
        NetworkEvent::PeerDisconnected {
            id: 2,
            reason: "heartbeat timeout".into(),
        },
        &mut player,
        &mut world,
        &mut net,
        &tx,
        &tx,
        &mut processed,
        0,
        None,
    )
    .await;

    let events = drain_event_types(&mut rx);
    assert!(
        !events.iter().any(|e| e == "session_ended"),
        "a host losing a joiner is business as usual, got: {events:?}"
    );
}

/// The reason travels verbatim from the transport to the client, so the UI can tell "the host
/// closed" (goodbye) from "the host crashed" (timeout).
#[tokio::test]
async fn session_ended_carries_the_disconnect_reason() {
    let mut net = NetworkManager::bind(0, 0, 42, false).await.unwrap();
    let mut world = World::new(42);
    let mut player = Player::new(7, "Joiner");
    let (tx, mut rx) = broadcast::channel(16);
    let mut processed: HashSet<(u16, u64)> = HashSet::new();
    net.host_peer_id = Some(1);

    handle_network_event(
        NetworkEvent::PeerDisconnected {
            id: 1,
            reason: "heartbeat timeout".into(),
        },
        &mut player,
        &mut world,
        &mut net,
        &tx,
        &tx,
        &mut processed,
        0,
        None,
    )
    .await;

    let mut reason = None;
    while let Ok(ServerMessage::Event(ev)) = rx.try_recv() {
        if ev.event_type == "session_ended" {
            reason = ev
                .data
                .get("reason")
                .and_then(|r| r.as_str())
                .map(String::from);
        }
    }
    assert_eq!(reason.as_deref(), Some("heartbeat timeout"));
}

// ─────────────────────────── ADR-068 — spray (S1) ───────────────────────────

/// Una petición de pintada bien formada, centrada donde está el jugador.
fn spray_request(place_id: u64, at: [f32; 3]) -> crate::ipc::SprayPlaceRequest {
    crate::ipc::SprayPlaceRequest {
        place_id,
        layer: 0,
        world_pos: at,
        yaw: 90.0,
        size: [1.0, 1.0],
        strokes: vec![crate::world::spray::SprayStroke {
            color: 2,
            width: 4,
            points: vec![0, 0, 10, 10, 20, 20],
        }],
    }
}

#[tokio::test]
async fn the_host_anchors_a_spray_to_its_chunk_in_local_coordinates() {
    // El invariante de ADR-068 decisión 3, extremo a extremo: entra en coordenadas de MUNDO y
    // debe quedar guardado en LOCALES del chunk correcto. Se elige a propósito un chunk que no
    // es el (0,0) — con world_pos dentro del primer chunk, local y global coinciden y el test
    // pasaría aunque el anclaje estuviera roto.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let (tx, mut rx) = broadcast::channel(16);
    let world_pos = [137.5, 1.6, -80.0]; // chunk (2, -2)
    let mut player = Player::new(1, "Host");
    player.position = Vec3::new(world_pos[0], world_pos[1], world_pos[2] + 1.0);

    process_spray_place(spray_request(1, world_pos), &player, &mut net, 99, &tx).await;

    let stored = net.sprays.chunk((2, -2, 0));
    assert_eq!(stored.len(), 1, "la pintada debe caer en el chunk (2,-2)");
    assert_eq!(stored[0].local_pos, [37.5, 1.6, 20.0]);
    assert_eq!(
        stored[0].world_pos(),
        world_pos,
        "y volver a su sitio al leer"
    );
    assert_eq!(stored[0].tick, 99, "el tick del host ES el orden de render");
    assert_ne!(stored[0].id, 0, "el host acuña el id");

    match rx.try_recv() {
        Ok(ServerMessage::SprayPlaced(s)) => assert_eq!(s.id, stored[0].id),
        other => panic!("el host debe hacer eco de la pintada aceptada: {other:?}"),
    }
}

#[tokio::test]
async fn the_host_refuses_what_the_caps_of_decision_5_forbid() {
    // Cada rechazo deja el almacén INTACTO. Si alguno dejara de rechazar, un cliente modificado
    // llegaría al save por esa vía.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let (tx, _rx) = broadcast::channel(16);
    let at = [10.0, 1.6, 10.0];
    let mut player = Player::new(1, "Host");
    player.position = Vec3::new(at[0], at[1], at[2]);

    // Lienzo por encima del tope.
    let mut req = spray_request(1, at);
    req.size = [99.0, 1.0];
    process_spray_place(req, &player, &mut net, 1, &tx).await;

    // NaN: pasaría cualquier comparación de rango si la guarda no fuera lo primero.
    let mut req = spray_request(2, at);
    req.world_pos = [f32::NAN, 1.6, 10.0];
    process_spray_place(req, &player, &mut net, 1, &tx).await;

    // Blob de puntos impar: X sin su Y.
    let mut req = spray_request(3, at);
    req.strokes[0].points.push(7);
    process_spray_place(req, &player, &mut net, 1, &tx).await;

    // Fuera del alcance del brazo: pintar a través de media planta.
    let far = [at[0] + 40.0, at[1], at[2]];
    process_spray_place(spray_request(4, far), &player, &mut net, 1, &tx).await;

    assert_eq!(
        net.sprays.len(),
        0,
        "ningún rechazo puede llegar al almacén"
    );
}

#[tokio::test]
async fn a_retransmitted_place_paints_exactly_one_spray() {
    // El `place_id` es la misma defensa que `stp_place`: el transporte fiable reenvía, y una
    // pintada duplicada no solo se ve mal, gasta una plaza del cap del chunk.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let (tx, _rx) = broadcast::channel(16);
    let at = [10.0, 1.6, 10.0];
    let mut player = Player::new(1, "Host");
    player.position = Vec3::new(at[0], at[1], at[2]);

    process_spray_place(spray_request(77, at), &player, &mut net, 1, &tx).await;
    process_spray_place(spray_request(77, at), &player, &mut net, 2, &tx).await;

    assert_eq!(net.sprays.len(), 1);
}

#[tokio::test]
async fn a_joiner_never_mints_a_spray_id_of_its_own() {
    // ADR-063: los acuñadores runtime son host-only. Hasta que el commit de P2P lo reenvíe, un
    // joiner NO pinta — pero tampoco inventa un id que colisionaría con el del host.
    let mut net = NetworkManager::bind(0, 2, 42, false).await.unwrap();
    let (tx, mut rx) = broadcast::channel(16);
    let at = [10.0, 1.6, 10.0];
    let mut player = Player::new(1, "Joiner");
    player.position = Vec3::new(at[0], at[1], at[2]);

    process_spray_place(spray_request(1, at), &player, &mut net, 1, &tx).await;

    assert_eq!(net.sprays.len(), 0);
    assert!(
        rx.try_recv().is_err(),
        "un joiner no puede anunciar una pintada como aceptada"
    );
}

/// ADR-068 — el CICLO COMPLETO de guardado, con fichero real en disco: pintar, guardar, cargar
/// y volver a servir la pintada con el chunk.
///
/// Los tests de round-trip previos prueban cada mitad por separado; éste es el único que
/// recorre lo que hace el juego de verdad, incluido el paso por JSON, que es donde el blob de
/// puntos deja de ser binario y podría deformarse.
#[tokio::test]
async fn a_painted_wall_survives_saving_and_reloading() {
    use crate::persistence::save::{build_save, load_or_fresh, SaveMeta};

    let world = World::new(42);
    let mut player = Player::new(1, "Host");
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let (tx, _rx) = broadcast::channel(16);

    // 1. Pintar dos pintadas en una pared de un chunk que NO es el (0,0) — dentro del primero,
    //    local y global coinciden y el test pasaría aunque el anclaje estuviera roto.
    let at = [137.5, 1.6, -80.0]; // chunk (2, -2)
    player.position = Vec3::new(at[0], at[1], at[2]);
    process_spray_place(spray_request(1, at), &player, &mut net, 10, &tx).await;
    process_spray_place(spray_request(2, at), &player, &mut net, 20, &tx).await;
    assert_eq!(net.sprays.chunk((2, -2, 0)).len(), 2);
    let painted = net.sprays.all();

    // 2. Guardar a un fichero REAL.
    let mut path = std::env::temp_dir();
    path.push("backrooms_adr068_save_cycle.json");
    let _ = std::fs::remove_file(&path);
    let mut save = build_save(
        "s",
        &world,
        &player,
        &SaveMeta::default(),
        &[],
        &[],
        &[],
        &[],
        1.0,
        &painted,
    );
    save.save_to(&path).expect("el guardado debe escribir");

    // 3. Cargar en un backend LIMPIO, como haría el arranque siguiente.
    let loaded = load_or_fresh(&path).expect("el save debe cargar");
    assert_eq!(loaded.sprays.len(), 2, "las pintadas estan en el fichero");

    let mut fresh_net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let mut fresh_world = World::new(42);
    let mut fresh_player = Player::new(1, "Host");
    assert_eq!(fresh_net.sprays.len(), 0, "arranca sin nada pintado");
    hydrate_from_save(&mut fresh_world, &mut fresh_player, &mut fresh_net, loaded);

    // 4. Y el chunk vuelve a servirlas, en orden de render y con los trazos intactos.
    let served = fresh_net.sprays.chunk((2, -2, 0));
    assert_eq!(served.len(), 2, "la pared sigue pintada tras recargar");
    assert!(served[0].tick < served[1].tick, "y en orden de render");
    assert_eq!(
        served[0].strokes[0].points,
        vec![0, 0, 10, 10, 20, 20],
        "el blob de puntos sobrevive al viaje por JSON"
    );
    assert_eq!(
        served[0].local_pos, painted[0].local_pos,
        "sigue anclada al chunk, no a coordenadas globales"
    );

    let _ = std::fs::remove_file(&path);
}

#[tokio::test]
async fn a_joiner_asks_for_a_chunks_sprays_once_and_only_once() {
    // Sin la pregunta, quien se une a un mundo ya pintado ve paredes limpias hasta que alguien
    // pinte delante de él: su almacén arranca vacío y la geometría, que SÍ se deriva del seed,
    // no arrastra pintadas. Y sin el dedup la pediría en cada pasada de streaming.
    let mut net = NetworkManager::bind(0, 2, 42, false).await.unwrap();

    assert!(net.requested_spray_chunks.insert((4, -1, 0)), "primera vez");
    assert!(
        !net.requested_spray_chunks.insert((4, -1, 0)),
        "el mismo chunk no se vuelve a pedir al re-streamear"
    );
    assert!(
        net.requested_spray_chunks.insert((4, -1, 3)),
        "otra capa es otra pared, y sí se pide"
    );
}

#[tokio::test]
async fn the_spray_opcodes_belong_to_spray_and_travel_reliably() {
    // Centinela de opcode, igual que los de ADR-037 y ADR-046: fija los tres códigos y que los
    // tres van fiables. Una pintada perdida no se auto-cura — nadie la reintenta.
    use crate::network::protocol::{PacketPayload, PacketType};
    use crate::network::reliability::is_reliable;

    let request = PacketPayload::SprayPlaceRequest {
        place_id: 1,
        layer: 0,
        world_pos: [1.0, 2.0, 3.0],
        yaw: 90.0,
        size: [1.0, 1.0],
        strokes: vec![],
    };
    let placed = PacketPayload::SprayPlaced {
        spray: crate::world::spray::Spray {
            id: 1,
            cx: 0,
            cz: 0,
            layer: 0,
            local_pos: [1.0, 2.0, 3.0],
            yaw: 0.0,
            size: [1.0, 1.0],
            author: 1,
            tick: 1,
            strokes: vec![],
        },
    };
    let chunk_req = PacketPayload::SprayChunkRequest {
        cx: 1,
        cz: 2,
        layer: 0,
    };

    assert_eq!(request.type_code(), 0x51);
    assert_eq!(placed.type_code(), 0x52);
    assert_eq!(chunk_req.type_code(), 0x53);
    assert_eq!(
        PacketType::from_u16(0x51),
        Some(PacketType::SprayPlaceRequest)
    );
    assert_eq!(PacketType::from_u16(0x52), Some(PacketType::SprayPlaced));
    assert_eq!(
        PacketType::from_u16(0x53),
        Some(PacketType::SprayChunkRequest)
    );
    for code in [0x51, 0x52, 0x53] {
        assert!(is_reliable(code), "el opcode {code:#x} debe viajar fiable");
    }
}

#[tokio::test]
async fn the_host_measures_reach_against_the_requesting_peer_not_its_own_player() {
    // El agujero que este test cierra: si el host validara el alcance contra SU propia posición,
    // un joiner al otro lado del nivel pintaría gratis, o no podría pintar nunca a su lado. La
    // posición contra la que se mide es la que el host ya conoce de ESE peer.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let (tx, _rx) = broadcast::channel(16);
    let host_player = Player::new(1, "Host"); // lejísimos de donde pinta el joiner
    let at = [300.0, 1.6, 300.0];

    // Sin peer conocido no hay posición contra la que medir: se ignora en vez de asumir.
    let accepted = accept_spray(
        spray_request(1, at),
        9,
        Vec3::new(at[0], at[1], at[2]),
        &mut net,
        1,
        &tx,
    );
    assert!(
        accepted.is_some(),
        "con su posición real, el joiner sí pinta"
    );
    assert_eq!(
        accepted.unwrap().author,
        9,
        "la autoría es del peticionario"
    );

    // Y con la posición del host (lejos), el mismo trazo se rechaza.
    let refused = accept_spray(
        spray_request(2, at),
        9,
        host_player.position,
        &mut net,
        1,
        &tx,
    );
    assert!(
        refused.is_none(),
        "medir contra la posición equivocada debe rechazar, no colar"
    );
    assert_eq!(net.sprays.len(), 1);
}

#[tokio::test]
async fn a_relayed_spray_is_revalidated_before_entering_the_local_store() {
    // El joiner NO se fía del paquete solo porque venga del host: un relay malformado no puede
    // envenenar su almacén (y, si algún día ese peer guarda, tampoco su save).
    let mut net = NetworkManager::bind(0, 2, 42, false).await.unwrap();

    let mut poisoned = crate::world::spray::Spray {
        id: 1,
        cx: 0,
        cz: 0,
        layer: 0,
        local_pos: [f32::NAN, 1.6, 10.0],
        yaw: 0.0,
        size: [1.0, 1.0],
        author: 1,
        tick: 1,
        strokes: vec![crate::world::spray::SprayStroke {
            color: 0,
            width: 2,
            points: vec![1, 1],
        }],
    };
    assert!(poisoned.validate().is_err());
    if poisoned.validate().is_ok() {
        net.sprays.insert(poisoned.clone());
    }
    assert_eq!(net.sprays.len(), 0, "un relay inválido no entra");

    poisoned.local_pos = [10.0, 1.6, 10.0];
    if poisoned.validate().is_ok() {
        net.sprays.insert(poisoned);
    }
    assert_eq!(net.sprays.len(), 1, "y uno válido sí");
}

#[tokio::test]
async fn loading_a_painted_world_never_reuses_a_spray_id() {
    // El mismo fallo que `id_allocators_reseed_inside_their_own_range` documenta para los cuatro
    // asignadores STP: sin re-sembrar, tras cargar la PRIMERA pintada de la sesión reacuña un id
    // que ya existe en el almacén. Aquí se comprueba sobre el efecto observable — la pintada
    // nueva no puede colisionar con ninguna cargada — y no leyendo el AtomicU32, que es estático
    // de proceso y los tests corren en hilos del mismo proceso.
    let mut world = World::new(42);
    let mut player = Player::new(1, "Host");
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let (tx, _rx) = broadcast::channel(16);

    let mut saved = crate::persistence::save::SaveFile::new("s", 42);
    saved.sprays = vec![crate::world::spray::Spray {
        id: 500,
        cx: 0,
        cz: 0,
        layer: 0,
        local_pos: [10.0, 1.6, 10.0],
        yaw: 0.0,
        size: [1.0, 1.0],
        author: 1,
        tick: 1,
        strokes: vec![crate::world::spray::SprayStroke {
            color: 0,
            width: 2,
            points: vec![1, 1, 2, 2],
        }],
    }];

    hydrate_from_save(&mut world, &mut player, &mut net, saved);
    assert_eq!(net.sprays.len(), 1, "la pintada guardada debe hidratarse");

    let at = [10.0, 1.6, 10.0];
    player.position = Vec3::new(at[0], at[1], at[2]);
    process_spray_place(spray_request(1, at), &player, &mut net, 50, &tx).await;

    let ids: Vec<u32> = net.sprays.chunk((0, 0, 0)).iter().map(|s| s.id).collect();
    assert_eq!(ids.len(), 2);
    assert!(
        ids[1] > 500,
        "el id nuevo ({}) debe quedar por encima del cargado",
        ids[1]
    );
}

#[tokio::test]
async fn sprays_ride_the_chunk_the_client_already_asks_for() {
    // La hidratación de ADR-068 §6: `GridChunkData` las lleva en ORDEN DE RENDER, y un chunk sin
    // pintar no paga nada (`skip_serializing_if`), que es lo que permite no relayarlas por tick.
    let mut net = NetworkManager::bind(0, 1, 42, true).await.unwrap();
    let (tx, _rx) = broadcast::channel(16);
    let at = [10.0, 1.6, 10.0];
    let mut player = Player::new(1, "Host");
    player.position = Vec3::new(at[0], at[1], at[2]);

    process_spray_place(spray_request(1, at), &player, &mut net, 10, &tx).await;
    process_spray_place(spray_request(2, at), &player, &mut net, 20, &tx).await;

    let sprays = net.sprays.chunk((0, 0, 0)).to_vec();
    assert_eq!(sprays.len(), 2);
    assert!(
        sprays[0].tick < sprays[1].tick,
        "la más nueva se dibuja la última"
    );

    let painted = crate::ipc::GridChunkData {
        cx: 0,
        cz: 0,
        layer: 0,
        walls: [[0u8; 10]; 10],
        room_zones: vec![],
        sprays,
    };
    let body = rmp_serde::to_vec_named(&painted).unwrap();
    let decoded: crate::ipc::GridChunkData = rmp_serde::from_slice(&body).unwrap();
    assert_eq!(
        decoded.sprays.len(),
        2,
        "las pintadas deben sobrevivir al wire"
    );
    assert_eq!(
        decoded.sprays[0].strokes[0].points,
        vec![0, 0, 10, 10, 20, 20]
    );

    let clean = crate::ipc::GridChunkData {
        sprays: vec![],
        ..painted
    };
    let clean_body = rmp_serde::to_vec_named(&clean).unwrap();
    assert!(
        !String::from_utf8_lossy(&clean_body).contains("sprays"),
        "un chunk sin pintar no debe pagar ni la clave"
    );
}
