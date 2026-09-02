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

fn stages(seq: &ConnectSequence) -> Vec<ConnectStage> {
    seq.attempted().iter().map(|(s, _)| *s).collect()
}

#[test]
fn con_una_sola_via_la_secuencia_se_comporta_como_siempre() {
    // Una partida en LAN de toda la vida: un destino, un presupuesto, cero cambios respecto a
    // antes de ADR-117.
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), None, None);
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
    let seq = ConnectSequence::new(None, None, None);
    assert!(!seq.has_candidates());
    assert!(seq.describe_failure().contains("ninguna dirección"));
}

#[test]
fn el_orden_es_directa_lan_relay() {
    let now = Instant::now();
    let mut seq = ConnectSequence::new(Some(direct()), Some(lan()), Some(relay()));
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
    let mut seq = ConnectSequence::new(Some(direct()), Some(lan()), Some(relay()));
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
    let mut seq = ConnectSequence::new(Some(direct()), None, Some(relay()));
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
    let mut seq = ConnectSequence::new(None, None, Some(relay()));
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
    let mut seq = ConnectSequence::new(Some(lan()), Some(lan()), Some(relay()));
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
    let mut seq = ConnectSequence::new(Some(direct()), None, Some(relay()));
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
    let mut seq = ConnectSequence::new(Some(direct()), Some(lan()), Some(relay()));
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
    let mut seq = ConnectSequence::new(Some(direct()), None, None);
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
    // 5 + 3 + 12 = 20 s en el peor caso. El anterior era de 15 s por un solo destino, así que se
    // paga un tercio más de espera a cambio de tres vías en vez de una.
    let total: Duration = [ConnectStage::Direct, ConnectStage::Lan, ConnectStage::Relay]
        .iter()
        .map(|s| s.budget())
        .sum();

    assert_eq!(total, Duration::from_secs(20));
    assert!(
        ConnectStage::Relay.budget() > crate::network::relay_client::REGISTER_TIMEOUT,
        "el relay necesita más presupuesto que su propio registro, o saltaría antes de acabarlo"
    );
}
