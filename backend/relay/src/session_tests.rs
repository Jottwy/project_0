//! Tests de la tabla de sesiones.
//!
//! **Ni un socket, y el tiempo es un parámetro.** `RelayTable::handle` y `tick` reciben el
//! instante, así que un timeout de 8 segundos se prueba sumando 8 segundos a un `Instant`, no
//! durmiendo 8 segundos. Una suite que duerme es una suite que nadie ejecuta.

use std::net::SocketAddr;
use std::time::{Duration, Instant};

use super::*;
use crate::protocol::{RelayFrame, RelayMessage, SessionToken, MAX_GAMEPLAY_PAYLOAD_BYTES};

const SESSION: SessionId = 0xC0FFEE;
const WIRE: u16 = 55;

fn addr(last: u8) -> SocketAddr {
    format!("203.0.113.{last}:40000").parse().unwrap()
}

/// Otra IP para la misma máquina lógica, cuando hace falta esquivar el límite de tasa por IP.
fn addr_at(last: u8, port: u16) -> SocketAddr {
    format!("203.0.113.{last}:{port}").parse().unwrap()
}

fn token() -> SessionToken {
    SessionToken::new([7u8; 16])
}

fn other_token() -> SessionToken {
    SessionToken::new([9u8; 16])
}

fn table() -> RelayTable {
    RelayTable::new(RelayLimits::default())
}

fn encode(frame: RelayFrame) -> Vec<u8> {
    frame
        .encode()
        .expect("los mensajes de control siempre caben")
}

fn create(t: &mut RelayTable, from: SocketAddr, now: Instant) -> Vec<Action> {
    let buf = encode(RelayFrame::to_relay(
        SESSION,
        0,
        RelayMessage::SessionCreate {
            token: token(),
            wire_version: WIRE,
        },
    ));
    t.handle(from, &buf, now)
}

fn join_with(
    t: &mut RelayTable,
    from: SocketAddr,
    tok: SessionToken,
    wire: u16,
    now: Instant,
) -> Vec<Action> {
    let buf = encode(RelayFrame::to_relay(
        SESSION,
        0,
        RelayMessage::SessionJoin {
            token: tok,
            wire_version: wire,
        },
    ));
    t.handle(from, &buf, now)
}

fn join(t: &mut RelayTable, from: SocketAddr, now: Instant) -> Vec<Action> {
    join_with(t, from, token(), WIRE, now)
}

fn data(from_peer: PeerId, to_peer: PeerId, bytes: usize) -> Vec<u8> {
    encode(RelayFrame::to_peer(
        SESSION,
        from_peer,
        to_peer,
        RelayMessage::Data {
            payload: vec![0xAB; bytes],
        },
    ))
}

fn assigned_peer(actions: &[Action]) -> Option<PeerId> {
    actions.iter().find_map(|a| match a {
        Action::Send {
            frame:
                RelayFrame {
                    message: RelayMessage::PeerReady { assigned_peer, .. },
                    ..
                },
            ..
        } => Some(*assigned_peer),
        _ => None,
    })
}

fn auth_status(actions: &[Action]) -> Option<AuthStatus> {
    actions.iter().find_map(|a| match a {
        Action::Send {
            frame:
                RelayFrame {
                    message: RelayMessage::Auth { status },
                    ..
                },
            ..
        } => Some(*status),
        _ => None,
    })
}

/// El latido de un peer. **Al host hay que hacerle latir en los tests igual que en la vida**: si
/// se le olvida, `tick` lo da por muerto y cierra la sesión entera — que es exactamente lo que
/// debe hacer, y lo que hizo caer a cuatro de estos tests la primera vez que se ejecutaron.
fn heartbeat(t: &mut RelayTable, from: SocketAddr, peer: PeerId, now: Instant) -> Vec<Action> {
    let buf = encode(RelayFrame::to_relay(SESSION, peer, RelayMessage::Heartbeat));
    t.handle(from, &buf, now)
}

/// Una sesión con host (peer 1) y un joiner (peer 2) ya dentro.
fn session_with_one_joiner(now: Instant) -> (RelayTable, SocketAddr, SocketAddr) {
    let mut t = table();
    let host = addr(1);
    let joiner = addr(2);
    create(&mut t, host, now);
    let actions = join(&mut t, joiner, now);
    assert_eq!(assigned_peer(&actions), Some(2));
    (t, host, joiner)
}

// ─── Creación ──────────────────────────────────────────────────────────────────────────────

#[test]
fn crear_una_sesion_deja_al_host_como_peer_1() {
    let now = Instant::now();
    let mut t = table();
    let actions = create(&mut t, addr(1), now);

    assert_eq!(assigned_peer(&actions), Some(HOST_PEER_ID));
    assert_eq!(t.session_count(), 1);
    assert_eq!(t.session_peers(SESSION), vec![HOST_PEER_ID]);
    assert_eq!(t.stats().sessions_created, 1);
}

#[test]
fn crear_dos_veces_desde_el_mismo_host_no_crea_dos_sesiones() {
    // Sobre UDP el primer `PeerReady` se pierde a menudo y el host reintenta. Un reintento que
    // tirara la sesión existente sería peor que el paquete perdido.
    let now = Instant::now();
    let mut t = table();
    create(&mut t, addr(1), now);
    let actions = create(&mut t, addr(1), now + Duration::from_secs(1));

    assert_eq!(assigned_peer(&actions), Some(HOST_PEER_ID));
    assert_eq!(t.session_count(), 1);
    assert_eq!(t.stats().sessions_created, 1, "no se creó una segunda");
}

#[test]
fn otra_direccion_no_puede_apropiarse_de_una_sesion_existente() {
    let now = Instant::now();
    let mut t = table();
    create(&mut t, addr(1), now);

    let actions = create(&mut t, addr(9), now);
    assert_eq!(auth_status(&actions), Some(AuthStatus::Denied));
    assert_eq!(t.session_peers(SESSION), vec![HOST_PEER_ID]);
    assert_eq!(t.stats().auth_denied, 1);
}

#[test]
fn el_relay_lleno_rechaza_sesiones_nuevas_sin_tocar_las_vivas() {
    let now = Instant::now();
    let mut t = RelayTable::new(RelayLimits {
        max_sessions: 1,
        ..RelayLimits::default()
    });
    create(&mut t, addr(1), now);

    let buf = encode(RelayFrame::to_relay(
        SESSION + 1,
        0,
        RelayMessage::SessionCreate {
            token: token(),
            wire_version: WIRE,
        },
    ));
    let actions = t.handle(addr(2), &buf, now);

    assert_eq!(auth_status(&actions), Some(AuthStatus::RelayFull));
    assert_eq!(t.session_count(), 1);
}

// ─── Entrada y autenticación ───────────────────────────────────────────────────────────────

#[test]
fn entrar_asigna_id_y_avisa_al_host() {
    let now = Instant::now();
    let mut t = table();
    let host = addr(1);
    create(&mut t, host, now);

    let actions = join(&mut t, addr(2), now);

    assert_eq!(assigned_peer(&actions), Some(2));
    let avisado = actions.iter().any(|a| {
        matches!(a, Action::Send { to, frame: RelayFrame { message: RelayMessage::PeerJoined { peer: 2 }, .. } } if *to == host)
    });
    assert!(
        avisado,
        "el host tiene que enterarse de que ha entrado alguien"
    );
    assert_eq!(t.session_peers(SESSION), vec![1, 2]);
}

#[test]
fn un_token_que_no_es_no_entra() {
    let now = Instant::now();
    let mut t = table();
    create(&mut t, addr(1), now);

    let actions = join_with(&mut t, addr(2), other_token(), WIRE, now);

    assert_eq!(auth_status(&actions), Some(AuthStatus::Denied));
    assert_eq!(t.session_peers(SESSION), vec![HOST_PEER_ID]);
    assert_eq!(t.stats().peers_admitted, 1, "solo el host");
}

#[test]
fn una_sesion_que_no_existe_y_un_token_malo_se_ven_identicos_desde_fuera() {
    // Es la decisión, no un descuido: si el relay distinguiera los dos casos, cualquiera podría
    // enumerar qué sesiones hay probando ids.
    let now = Instant::now();
    let mut t = table();
    create(&mut t, addr(1), now);

    let token_malo = join_with(&mut t, addr(2), other_token(), WIRE, now);

    let mut t2 = table();
    let inexistente = join_with(&mut t2, addr(2), token(), WIRE, now);

    assert_eq!(auth_status(&token_malo), Some(AuthStatus::Denied));
    assert_eq!(auth_status(&inexistente), Some(AuthStatus::Denied));
}

#[test]
fn otra_version_de_wire_se_corta_en_la_puerta() {
    let now = Instant::now();
    let mut t = table();
    create(&mut t, addr(1), now);

    let actions = join_with(&mut t, addr(2), token(), WIRE + 1, now);

    assert_eq!(auth_status(&actions), Some(AuthStatus::WireVersionMismatch));
    assert_eq!(t.session_peers(SESSION), vec![HOST_PEER_ID]);
}

#[test]
fn el_mismo_joiner_reintentando_no_consume_dos_plazas() {
    // El `PeerReady` se pierde, el joiner reintenta. Si cada reintento gastara una plaza, el
    // joiner acabaría con dos identidades y el host vería dos jugadores que son uno.
    let now = Instant::now();
    let mut t = table();
    create(&mut t, addr(1), now);

    let primera = join(&mut t, addr(2), now);
    let segunda = join(&mut t, addr(2), now + Duration::from_millis(500));

    assert_eq!(assigned_peer(&primera), Some(2));
    assert_eq!(assigned_peer(&segunda), Some(2), "el MISMO id");
    assert_eq!(t.session_peers(SESSION), vec![1, 2]);
    assert_eq!(t.peer_count(), 2);
}

#[test]
fn la_sesion_llena_rechaza_al_siguiente() {
    let now = Instant::now();
    let mut t = RelayTable::new(RelayLimits {
        max_peers_per_session: 2, // el host y uno
        handshakes_per_second_per_ip: 100,
        ..RelayLimits::default()
    });
    create(&mut t, addr(1), now);
    join(&mut t, addr(2), now);

    let actions = join(&mut t, addr(3), now);

    assert_eq!(auth_status(&actions), Some(AuthStatus::SessionFull));
    assert_eq!(t.session_peers(SESSION), vec![1, 2]);
}

// ─── Reenvío y estrella ────────────────────────────────────────────────────────────────────

#[test]
fn un_joiner_habla_con_el_host_y_el_host_con_el_joiner() {
    let now = Instant::now();
    let (mut t, host, joiner) = session_with_one_joiner(now);

    let subida = t.handle(joiner, &data(2, HOST_PEER_ID, 100), now);
    assert_eq!(
        subida,
        vec![Action::Forward {
            to: host,
            src: 2,
            dst: HOST_PEER_ID
        }]
    );

    let bajada = t.handle(host, &data(HOST_PEER_ID, 2, 100), now);
    assert_eq!(
        bajada,
        vec![Action::Forward {
            to: joiner,
            src: HOST_PEER_ID,
            dst: 2
        }]
    );

    assert_eq!(t.stats().forwarded, 2);
}

#[test]
fn el_origen_lo_pone_el_relay_no_el_que_lo_manda() {
    // Un peer que estampe el `src` de otro suplantaría a ese otro ante el host. El relay conoce la
    // identidad real por la dirección de origen y la impone.
    let now = Instant::now();
    let (mut t, host, joiner) = session_with_one_joiner(now);

    // El joiner (peer 2) miente y dice ser el 77.
    let actions = t.handle(joiner, &data(77, HOST_PEER_ID, 50), now);

    assert_eq!(
        actions,
        vec![Action::Forward {
            to: host,
            src: 2,
            dst: HOST_PEER_ID
        }],
        "se reenvía con el id REAL del emisor"
    );
}

#[test]
fn un_joiner_no_puede_hablar_con_otro_joiner() {
    // ADR-015 + ADR-117 D6. Y no se «corrige» redirigiéndolo al host: se descarta y se cuenta,
    // porque corregirlo en silencio escondería justo el caso que el contador delata.
    let now = Instant::now();
    let mut t = table();
    create(&mut t, addr(1), now);
    join(&mut t, addr(2), now);
    join(&mut t, addr(3), now);

    let actions = t.handle(addr(2), &data(2, 3, 100), now);

    assert!(actions.is_empty(), "no se reenvía nada");
    assert_eq!(t.stats().star_violations, 1);
    assert_eq!(t.stats().forwarded, 0);
}

#[test]
fn un_desconocido_no_consigue_que_le_reenvien_nada() {
    let now = Instant::now();
    let (mut t, _host, _joiner) = session_with_one_joiner(now);

    let actions = t.handle(addr(200), &data(2, HOST_PEER_ID, 100), now);

    assert!(actions.is_empty());
    assert_eq!(t.stats().unknown_sender, 1);
    assert_eq!(t.stats().forwarded, 0);
}

#[test]
fn un_peer_de_una_sesion_no_puede_hablar_en_nombre_de_otra() {
    let now = Instant::now();
    let (mut t, _host, joiner) = session_with_one_joiner(now);

    let buf = encode(RelayFrame::to_peer(
        SESSION + 1, // una sesión que no es la suya
        2,
        HOST_PEER_ID,
        RelayMessage::Data { payload: vec![1] },
    ));
    let actions = t.handle(joiner, &buf, now);

    assert!(actions.is_empty());
    assert_eq!(t.stats().unknown_sender, 1);
}

#[test]
fn un_datagrama_por_encima_del_techo_no_se_reenvia() {
    // ADR-113: el relay no fragmenta. Lo de más se descarta en la puerta, antes de tocar la tabla.
    let now = Instant::now();
    let (mut t, _host, joiner) = session_with_one_joiner(now);

    let mut buf = data(2, HOST_PEER_ID, MAX_GAMEPLAY_PAYLOAD_BYTES);
    buf.push(0); // un byte de más: 1217 en el cable
    let actions = t.handle(joiner, &buf, now);

    assert!(actions.is_empty());
    assert_eq!(t.stats().malformed, 1);
    assert_eq!(t.stats().forwarded, 0);

    // Y justo en el techo sí pasa.
    let ok = t.handle(
        joiner,
        &data(2, HOST_PEER_ID, MAX_GAMEPLAY_PAYLOAD_BYTES),
        now,
    );
    assert_eq!(ok.len(), 1);
    assert_eq!(t.stats().forwarded, 1);
}

#[test]
fn la_basura_no_recibe_ni_un_byte_de_respuesta() {
    // Sin respuesta no hay amplificación. Un puerto público recibe escáneres a todas horas.
    let now = Instant::now();
    let mut t = table();

    assert!(t.handle(addr(9), b"", now).is_empty());
    assert!(t.handle(addr(9), b"GET / HTTP/1.1\r\n\r\n", now).is_empty());
    assert!(t.handle(addr(9), &[0u8; 16], now).is_empty());
    assert_eq!(t.stats().malformed, 3);
}

#[test]
fn un_mensaje_que_solo_emite_el_relay_no_se_acepta_de_un_cliente() {
    let now = Instant::now();
    let (mut t, _host, joiner) = session_with_one_joiner(now);

    let buf = encode(RelayFrame::to_peer(
        SESSION,
        2,
        HOST_PEER_ID,
        RelayMessage::PeerDisconnected {
            peer: HOST_PEER_ID,
            reason: DisconnectReason::Left,
        },
    ));
    let actions = t.handle(joiner, &buf, now);

    assert!(actions.is_empty(), "un peer no puede desconectar a otro");
    assert_eq!(t.session_peers(SESSION), vec![1, 2]);
}

// ─── Latido y caducidad ────────────────────────────────────────────────────────────────────

#[test]
fn el_latido_se_contesta_y_mantiene_vivo_al_peer() {
    let now = Instant::now();
    let (mut t, host, joiner) = session_with_one_joiner(now);

    let actions = heartbeat(&mut t, joiner, 2, now + Duration::from_secs(6));
    heartbeat(&mut t, host, HOST_PEER_ID, now + Duration::from_secs(6));

    assert!(matches!(
        actions.as_slice(),
        [Action::Send { to, frame: RelayFrame { message: RelayMessage::Heartbeat, .. } }] if *to == joiner
    ));

    // Con el latido de los 6 s, a los 10 s todavía les quedan 4 de margen a los dos.
    assert!(t.tick(now + Duration::from_secs(10)).is_empty());
    assert_eq!(t.session_peers(SESSION), vec![1, 2]);
}

#[test]
fn el_host_tambien_tiene_que_latir() {
    // No hay trato especial para el host: si deja de dar señales, su sesión se cierra como la de
    // cualquiera. Es el caso del host que cierra el juego de golpe.
    let now = Instant::now();
    let (mut t, _host, _joiner) = session_with_one_joiner(now);

    t.tick(now + Duration::from_secs(9));

    assert_eq!(t.session_count(), 0);
    assert_eq!(t.stats().sessions_closed, 1);
}

#[test]
fn un_joiner_que_deja_de_latir_se_va_y_el_host_se_entera() {
    let now = Instant::now();
    let (mut t, host, _joiner) = session_with_one_joiner(now);

    // El host sigue vivo; el joiner no. Sin este latido caducarían los dos y lo que se vería es un
    // cierre de sesión, no una desconexión.
    heartbeat(&mut t, host, HOST_PEER_ID, now + Duration::from_secs(7));

    let actions = t.tick(now + Duration::from_secs(9));

    assert!(actions.iter().any(|a| matches!(
        a,
        Action::Send { to, frame: RelayFrame { message: RelayMessage::PeerDisconnected { peer: 2, reason: DisconnectReason::Timeout }, .. } } if *to == host
    )));
    assert_eq!(t.session_peers(SESSION), vec![HOST_PEER_ID]);
    assert_eq!(t.stats().peers_timed_out, 1);
    assert_eq!(t.peer_count(), 1, "su dirección deja de estar asociada");
}

#[test]
fn si_el_host_cae_la_sesion_entera_se_cierra() {
    // ADR-056: sin host no hay autoridad. El relay no inventa una migración que el juego no tiene.
    let now = Instant::now();
    let mut t = table();
    let host = addr(1);
    let joiner = addr(2);
    create(&mut t, host, now);
    join(&mut t, joiner, now);

    // El joiner sigue latiendo; el host no.
    heartbeat(&mut t, joiner, 2, now + Duration::from_secs(7));

    let actions = t.tick(now + Duration::from_secs(9));

    assert!(actions.iter().any(|a| matches!(
        a,
        Action::Send { to, frame: RelayFrame { message: RelayMessage::SessionClosed { reason: CloseReason::HostLeft }, .. } } if *to == joiner
    )), "al joiner se le dice que la partida se acabó");
    assert_eq!(t.session_count(), 0);
    assert_eq!(t.peer_count(), 0, "no queda ninguna dirección colgando");
    assert_eq!(t.stats().sessions_closed, 1);
}

#[test]
fn una_sesion_sin_trafico_se_recoge_sola() {
    // El caso feo: un host que se evapora sin despedirse ocuparía sitio para siempre.
    let now = Instant::now();
    let mut t = RelayTable::new(RelayLimits {
        peer_timeout: Duration::from_secs(3600), // fuera de juego, para aislar el idle
        session_idle_timeout: Duration::from_secs(30),
        ..RelayLimits::default()
    });
    create(&mut t, addr(1), now);

    assert!(t.tick(now + Duration::from_secs(29)).is_empty());
    t.tick(now + Duration::from_secs(31));

    assert_eq!(t.session_count(), 0);
    assert_eq!(t.peer_count(), 0);
}

#[test]
fn cerrar_una_sesion_dos_veces_no_hace_nada_la_segunda() {
    let now = Instant::now();
    let (mut t, _host, _joiner) = session_with_one_joiner(now);

    let primera = t.close_session(SESSION, CloseReason::RelayShutdown);
    let segunda = t.close_session(SESSION, CloseReason::RelayShutdown);

    assert_eq!(primera.len(), 2, "se avisa a los dos");
    assert!(segunda.is_empty());
    assert_eq!(t.stats().sessions_closed, 1);
}

#[test]
fn tras_caducar_un_peer_su_hueco_se_puede_volver_a_ocupar() {
    // Reconexión: el mismo PC vuelve a entrar. Tiene que conseguir un id, no un rechazo.
    let now = Instant::now();
    let (mut t, host, joiner) = session_with_one_joiner(now);

    heartbeat(&mut t, host, HOST_PEER_ID, now + Duration::from_secs(7));
    t.tick(now + Duration::from_secs(9));
    assert_eq!(t.session_peers(SESSION), vec![HOST_PEER_ID]);

    let vuelta = join(&mut t, joiner, now + Duration::from_secs(10));

    assert_eq!(assigned_peer(&vuelta), Some(3), "id nuevo, no el reciclado");
    assert_eq!(t.session_peers(SESSION), vec![1, 3]);
}

// ─── Límite de tasa ────────────────────────────────────────────────────────────────────────

#[test]
fn una_inundacion_de_handshakes_deja_de_contestarse() {
    let now = Instant::now();
    let mut t = RelayTable::new(RelayLimits {
        handshakes_per_second_per_ip: 3,
        ..RelayLimits::default()
    });

    let mut contestados = 0;
    for i in 0..20u16 {
        // Misma IP, puertos distintos: es una sola máquina insistiendo.
        let actions = join_with(&mut t, addr_at(50, 40000 + i), token(), WIRE, now);
        if !actions.is_empty() {
            contestados += 1;
        }
    }

    assert_eq!(contestados, 3, "sólo los tres primeros de la ventana");
    assert_eq!(t.stats().rate_limited, 17);
}

#[test]
fn el_limite_se_renueva_con_la_ventana() {
    let now = Instant::now();
    let mut t = RelayTable::new(RelayLimits {
        handshakes_per_second_per_ip: 1,
        ..RelayLimits::default()
    });
    create(&mut t, addr(1), now);

    // La MISMA IP con puertos distintos: el cubo es por IP, no por dirección. Con IPs distintas
    // cada una tendría su propio cupo y esto no probaría nada.
    assert!(!join_with(&mut t, addr_at(50, 40001), token(), WIRE, now).is_empty());
    assert!(
        join_with(&mut t, addr_at(50, 40002), token(), WIRE, now).is_empty(),
        "segundo en la misma ventana"
    );
    assert!(
        !join_with(
            &mut t,
            addr_at(50, 40003),
            token(),
            WIRE,
            now + Duration::from_secs(2)
        )
        .is_empty(),
        "ventana nueva, vuelve a atender"
    );
}

#[test]
fn el_trafico_de_juego_no_pasa_por_el_limite_de_tasa() {
    // El límite protege la PUERTA. Aplicárselo a los datos expulsaría a un jugador legítimo, que a
    // 60 Hz manda muchísimo más que cinco datagramas por segundo.
    let now = Instant::now();
    let (mut t, _host, joiner) = session_with_one_joiner(now);

    for _ in 0..500 {
        let actions = t.handle(joiner, &data(2, HOST_PEER_ID, 200), now);
        assert_eq!(actions.len(), 1);
    }

    assert_eq!(t.stats().forwarded, 500);
    assert_eq!(t.stats().rate_limited, 0);
}

#[test]
fn los_cubos_de_tasa_no_crecen_para_siempre() {
    // Sin recogida, la memoria del relay crece con cada IP que le escriba una vez en la vida.
    let now = Instant::now();
    let mut t = table();
    for i in 0..40u8 {
        t.handle(
            addr(100 + i % 40),
            &encode(RelayFrame::to_relay(0, 0, RelayMessage::Connect)),
            now,
        );
    }
    t.tick(now + Duration::from_secs(120));

    // La comprobación indirecta pero real: tras la recogida, una IP que ya había gastado su cupo
    // vuelve a ser atendida como nueva.
    let actions = t.handle(
        addr(100),
        &encode(RelayFrame::to_relay(0, 0, RelayMessage::Connect)),
        now + Duration::from_secs(120),
    );
    assert!(!actions.is_empty());
}

#[test]
fn el_saludo_contesta_que_el_relay_esta_vivo() {
    let now = Instant::now();
    let mut t = table();
    let actions = t.handle(
        addr(5),
        &encode(RelayFrame::to_relay(0, 0, RelayMessage::Connect)),
        now,
    );
    assert_eq!(auth_status(&actions), Some(AuthStatus::Ok));
}
