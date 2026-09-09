//! Tests de la secuencia de conexión. El tiempo entra por parámetro: los presupuestos se prueban
//! sumando segundos, no esperándolos.

use super::*;

fn direct() -> SocketAddr {
    "94.73.55.235:7778".parse().unwrap()
}

fn lan() -> SocketAddr {
    "192.168.1.168:7778".parse().unwrap()
}

fn relay() -> SocketAddr {
    // La dirección sintética del host dentro del relay: es a donde apunta la etapa de relay.
    crate::network::transport::synthetic_addr(0xABCD, 1)
}

fn steam() -> SocketAddr {
    // ADR-135 D2: el túnel de Unity, en ESTA máquina. No es un endpoint de internet y aquí nadie
    // sabe que detrás hay una red de relay de Valve.
    "127.0.0.1:52345".parse().unwrap()
}

fn stages(seq: &ConnectSequence) -> Vec<ConnectStage> {
    seq.attempted().iter().map(|(s, _)| *s).collect()
}

#[test]
fn con_una_sola_via_la_secuencia_se_comporta_como_siempre() {
    // Una partida en LAN de toda la vida: un destino, un presupuesto, cero cambios respecto a
    // antes de ADR-117.
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), None, None, None);
    seq.start(now);

    assert_eq!(seq.stage(), Some(ConnectStage::Direct));
    assert_eq!(
        seq.advance_if_expired(now + Duration::from_secs(4)),
        Advance::Stay
    );

    let advance = seq.advance_if_expired(now + Duration::from_secs(6));
    assert!(matches!(advance, Advance::Exhausted { .. }));
    assert_eq!(seq.current(), None);
}

#[test]
fn sin_ninguna_via_se_dice_en_vez_de_esperar() {
    let seq = ConnectSequence::new(None, None, None, None);
    assert!(!seq.has_candidates());
    assert!(seq.describe_failure().contains("ninguna dirección"));
}

#[test]
fn el_orden_es_directa_lan_relay() {
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), Some(lan()), None, Some(relay()));
    seq.start(now);

    assert_eq!(seq.stage(), Some(ConnectStage::Direct));

    let mut t = now + Duration::from_secs(6);
    assert!(matches!(
        seq.advance_if_expired(t),
        Advance::Moved { to, .. } if to.stage == ConnectStage::Lan && to.addr == lan()
    ));

    t += Duration::from_secs(4);
    assert!(matches!(
        seq.advance_if_expired(t),
        Advance::Moved { to, .. } if to.stage == ConnectStage::Relay
    ));

    assert_eq!(
        stages(&seq),
        vec![ConnectStage::Direct, ConnectStage::Lan, ConnectStage::Relay]
    );
}

#[test]
fn cada_etapa_gasta_su_propio_presupuesto_y_no_el_del_vecino() {
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), Some(lan()), None, Some(relay()));
    seq.start(now);

    // La directa tiene 5 s: a los 4 sigue.
    assert_eq!(
        seq.advance_if_expired(now + Duration::from_secs(4)),
        Advance::Stay
    );
    // A los 5 salta a la LAN, y el reloj de la LAN empieza AHÍ, no en el inicio.
    assert!(matches!(
        seq.advance_if_expired(now + Duration::from_secs(5)),
        Advance::Moved { .. }
    ));
    // La LAN tiene 3 s propios: a los 7 (2 s de LAN) sigue.
    assert_eq!(
        seq.advance_if_expired(now + Duration::from_secs(7)),
        Advance::Stay
    );
    assert_eq!(seq.stage(), Some(ConnectStage::Lan));
    // A los 8 (3 s de LAN) salta al relay.
    assert!(matches!(
        seq.advance_if_expired(now + Duration::from_secs(8)),
        Advance::Moved { to, .. } if to.stage == ConnectStage::Relay
    ));
}

#[test]
fn sin_lan_se_salta_directamente_al_relay() {
    // Es el caso del playtest del 2026-09-02: dos personas en redes distintas, sin nada que
    // justifique probar una dirección privada.
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), None, None, Some(relay()));
    seq.start(now);

    assert!(matches!(
        seq.advance_if_expired(now + Duration::from_secs(6)),
        Advance::Moved { to, .. } if to.stage == ConnectStage::Relay
    ));
    assert_eq!(
        stages(&seq),
        vec![ConnectStage::Direct, ConnectStage::Relay]
    );
}

#[test]
fn sin_endpoint_directo_se_empieza_por_el_relay() {
    // El lobby relay-only de ADR-117 D7: el host no publicó `connect_ip` porque no tenía ninguno
    // defendible, y aun así se puede entrar.
    let mut seq = ConnectSequence::new(None, None, None, Some(relay()));
    seq.start(Instant::now());

    assert_eq!(seq.stage(), Some(ConnectStage::Relay));
    assert!(seq.has_candidates());
}

#[test]
fn una_lan_que_es_la_misma_direccion_que_la_directa_no_se_prueba_dos_veces() {
    // Pasa cuando el host anuncia su LAN como `connect_ip` por no tener nada mejor — que es
    // exactamente lo que hacía antes de ADR-117 D7. Repetirlo gastaría 3 s del jugador para
    // volver a fallar igual.
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(lan()), Some(lan()), None, Some(relay()));
    seq.start(now);

    assert!(matches!(
        seq.advance_if_expired(now + Duration::from_secs(6)),
        Advance::Moved { to, .. } if to.stage == ConnectStage::Relay
    ));
}

#[test]
fn el_motivo_de_cada_salto_nombra_de_donde_se_viene_y_a_donde_se_va() {
    // ADR-117 D10: `fallback_reason`. Un salto mudo es indistinguible de un cuelgue.
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), None, None, Some(relay()));
    seq.start(now);

    let Advance::Moved { reason, .. } = seq.advance_if_expired(now + Duration::from_secs(6)) else {
        panic!("tenía que saltar");
    };
    assert!(reason.contains("direct"), "{reason}");
    assert!(reason.contains("94.73.55.235"), "{reason}");
    assert!(reason.contains("relay"), "{reason}");
}

#[test]
fn el_fallo_final_cuenta_todo_lo_que_se_intento() {
    // El jugador tiene que poder leer en el panel qué se probó, sin abrir un log.
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), Some(lan()), None, Some(relay()));
    seq.start(now);

    let mut t = now;
    let mut reason = String::new();
    for _ in 0..4 {
        t += Duration::from_secs(13);
        if let Advance::Exhausted { reason: r } = seq.advance_if_expired(t) {
            reason = r;
            break;
        }
    }

    assert!(reason.contains("direct"), "{reason}");
    assert!(reason.contains("lan"), "{reason}");
    assert!(reason.contains("relay"), "{reason}");
    assert!(reason.contains("94.73.55.235"), "{reason}");
    assert!(reason.contains("192.168.1.168"), "{reason}");
}

#[test]
fn una_vez_agotada_la_secuencia_no_resucita() {
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), None, None, None);
    seq.start(now);

    assert!(matches!(
        seq.advance_if_expired(now + Duration::from_secs(6)),
        Advance::Exhausted { .. }
    ));
    assert_eq!(
        seq.advance_if_expired(now + Duration::from_secs(60)),
        Advance::Stay,
        "no se vuelve a anunciar el fracaso una y otra vez"
    );
    assert_eq!(seq.current(), None);
}

#[test]
fn el_presupuesto_total_cabe_en_lo_que_un_jugador_espera() {
    // 5 + 3 + 8 + 12 = 28 s en el peor caso, y ese caso es el que HOY no conecta de ninguna
    // manera: hacen falta las cuatro vías configuradas y que fallen las cuatro. El anterior era de
    // 15 s por un solo destino.
    let total: Duration = [
        ConnectStage::Direct,
        ConnectStage::Lan,
        ConnectStage::Steam,
        ConnectStage::Relay,
    ]
    .iter()
    .map(|s| s.budget())
    .sum();

    assert_eq!(total, Duration::from_secs(28));
    assert!(
        ConnectStage::Relay.budget() > crate::network::relay_client::REGISTER_TIMEOUT,
        "el relay necesita más presupuesto que su propio registro, o saltaría antes de acabarlo"
    );
}

// ─── ADR-135: la cuarta vía ─────────────────────────────────────────────────────────────────

#[test]
fn la_etapa_de_steam_existe_con_su_nombre_y_su_presupuesto() {
    // El nombre es el que sale en `transport=` del log y el que un tester filtra con grep: si
    // cambia, los diagnósticos de ADR-117 D10 dejan de encontrarse.
    assert_eq!(ConnectStage::Steam.name(), "steam");
    assert_eq!(ConnectStage::Steam.budget(), Duration::from_secs(8));
    // ADR-135 D6: paga salto, así que va por detrás de las dos vías que no lo pagan; y por delante
    // del relay propio, que además tiene que negociar su registro.
    assert!(ConnectStage::Steam.budget() > ConnectStage::Lan.budget());
    assert!(ConnectStage::Steam.budget() < ConnectStage::Relay.budget());
}

#[test]
fn el_orden_de_las_cuatro_vias_es_directa_lan_steam_relay() {
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), Some(lan()), Some(steam()), Some(relay()));
    seq.start(now);

    let mut t = now + Duration::from_secs(6);
    assert!(matches!(
        seq.advance_if_expired(t),
        Advance::Moved { to, .. } if to.stage == ConnectStage::Lan
    ));

    t += Duration::from_secs(4);
    assert!(matches!(
        seq.advance_if_expired(t),
        Advance::Moved { to, .. } if to.stage == ConnectStage::Steam && to.addr == steam()
    ));

    t += Duration::from_secs(9);
    assert!(matches!(
        seq.advance_if_expired(t),
        Advance::Moved { to, .. } if to.stage == ConnectStage::Relay
    ));

    assert_eq!(
        stages(&seq),
        vec![
            ConnectStage::Direct,
            ConnectStage::Lan,
            ConnectStage::Steam,
            ConnectStage::Relay
        ]
    );
}

#[test]
fn sin_tunel_de_steam_la_etapa_no_existe_y_todo_es_como_antes() {
    // "Las etapas que no existen se saltan": una partida sin Steam —el joiner con el cliente
    // cerrado, o un build de itch— se comporta exactamente como antes de ADR-135.
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), Some(lan()), None, Some(relay()));
    seq.start(now);

    let mut t = now + Duration::from_secs(6);
    assert!(matches!(seq.advance_if_expired(t), Advance::Moved { .. }));
    t += Duration::from_secs(4);
    assert!(matches!(
        seq.advance_if_expired(t),
        Advance::Moved { to, .. } if to.stage == ConnectStage::Relay
    ));
    assert!(!stages(&seq).contains(&ConnectStage::Steam));
}

#[test]
fn un_lobby_steam_only_empieza_por_steam_sin_gastar_nada_antes() {
    // El host publicó `bs_steam_host` y nada más: ni endpoint directo ni relay propio. Es el caso
    // que ADR-135 existe para arreglar, y no puede costar ni un segundo de etapas vacías.
    let mut seq = ConnectSequence::new(None, None, Some(steam()), None);
    seq.start(Instant::now());

    assert_eq!(seq.stage(), Some(ConnectStage::Steam));
    assert_eq!(seq.current().map(|c| c.addr), Some(steam()));
}

#[test]
fn steam_y_relay_propio_conviven_en_la_misma_secuencia() {
    // ADR-135: el relay propio NO se retira. Un host con las dos vías anuncia las dos, y el joiner
    // prueba Steam primero y cae al relay si Valve no le lleva.
    let now = Instant::now();
    let mut seq = ConnectSequence::new(None, None, Some(steam()), Some(relay()));
    seq.start(now);

    assert_eq!(seq.stage(), Some(ConnectStage::Steam));
    let Advance::Moved { to, reason } = seq.advance_if_expired(now + Duration::from_secs(9)) else {
        panic!("tenía que saltar al relay");
    };
    assert_eq!(to.stage, ConnectStage::Relay);
    assert!(reason.contains("steam"), "{reason}");
    assert!(reason.contains("relay"), "{reason}");
}

#[test]
fn el_loopback_del_tunel_es_una_direccion_a_la_que_el_backend_puede_registrar_un_peer() {
    // ADR-135 D2: la etapa Steam NO necesita dirección sintética (a diferencia del relay propio,
    // ADR-117 D3) porque el loopback ya pasa el filtro del receptor. Si esto dejara de ser cierto,
    // el host registraría al joiner y no podría contestarle: un peer que se calla.
    assert!(crate::network::sync::is_routable_peer_addr(&steam()));
}
