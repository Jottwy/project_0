//! Tests del cliente de relay. Sin sockets: el tiempo entra por parámetro, así que un presupuesto
//! de 10 segundos se prueba sumando 10 segundos, no esperándolos.

use super::*;
use backrooms_relay::protocol::RELAY_ENDPOINT;

const SESSION: SessionId = 0x00AB_CDEF;

fn config(as_host: bool) -> RelayConfig {
    RelayConfig {
        relay_addr: "203.0.113.7:7790".parse().unwrap(),
        session_id: SESSION,
        token: SessionToken::new([5u8; 16]),
        wire_version: 55,
        as_host,
    }
}

fn decode(bytes: &[u8]) -> RelayFrame {
    RelayFrame::decode(bytes).expect("el cliente tiene que emitir marcos legibles")
}

fn peer_ready(assigned: RelayPeerId) -> RelayFrame {
    RelayFrame::to_peer(
        SESSION,
        RELAY_ENDPOINT,
        assigned,
        RelayMessage::PeerReady {
            assigned_peer: assigned,
            host_peer: HOST_PEER_ID,
        },
    )
}

#[test]
fn el_host_crea_la_sesion_y_el_joiner_entra_en_ella() {
    let now = Instant::now();

    let mut host = RelayClient::new(config(true));
    let frame = decode(&host.poll(now).expect("tiene que registrarse ya"));
    assert!(matches!(
        frame.message,
        RelayMessage::SessionCreate {
            wire_version: 55,
            ..
        }
    ));
    assert_eq!(frame.session_id, SESSION);

    let mut joiner = RelayClient::new(config(false));
    let frame = decode(&joiner.poll(now).unwrap());
    assert!(matches!(
        frame.message,
        RelayMessage::SessionJoin {
            wire_version: 55,
            ..
        }
    ));
}

#[test]
fn sin_peer_ready_no_hay_enlace_y_por_tanto_no_sale_nada_al_relay() {
    // Es la puerta: `send_datagram` sólo envuelve cuando hay enlace, y el enlace sólo existe tras
    // el `PeerReady`. Sin esto, un backend podría estar mandando gameplay a un relay que todavía
    // no le ha admitido.
    let mut client = RelayClient::new(config(false));
    assert!(client.link().is_none());

    client.poll(Instant::now());
    assert!(client.link().is_none(), "haber pedido no es haber entrado");
}

#[test]
fn el_peer_ready_abre_el_enlace_con_el_id_que_dio_el_relay() {
    let now = Instant::now();
    let mut client = RelayClient::new(config(false));
    client.poll(now);

    let event = client.on_control(peer_ready(4), now);

    assert_eq!(
        event,
        Some(RelayClientEvent::Ready {
            my_peer: 4,
            host_peer: HOST_PEER_ID
        })
    );
    let link = client.link().expect("ahora sí hay enlace");
    assert_eq!(link.my_peer, 4);
    assert_eq!(link.session_id, SESSION);
    assert_eq!(link.relay_addr, config(false).relay_addr);
}

#[test]
fn el_registro_se_repite_una_vez_por_segundo_y_no_mas() {
    let now = Instant::now();
    let mut client = RelayClient::new(config(false));

    assert!(client.poll(now).is_some(), "el primero sale enseguida");
    assert!(
        client.poll(now + Duration::from_millis(500)).is_none(),
        "medio segundo después todavía no toca"
    );
    assert!(client.poll(now + Duration::from_millis(1100)).is_some());
    assert_eq!(client.attempts(), 2);
}

#[test]
fn si_el_relay_no_contesta_nunca_se_deja_de_intentar_con_un_motivo() {
    // ADR-117 D10: nada de reintentar para siempre en silencio. El estado final tiene nombre y ese
    // nombre va al `fallback_reason`.
    let now = Instant::now();
    let mut client = RelayClient::new(config(false));

    let mut t = now;
    for _ in 0..12 {
        client.poll(t);
        t += Duration::from_secs(1);
    }

    assert_eq!(*client.state(), RelayClientState::TimedOut);
    assert!(client.state().is_final());
    assert_eq!(client.state().name(), "RELAY_NO_RESPONSE");
    assert!(client.poll(t).is_none(), "y ya no se manda nada más");
    assert!(client.link().is_none());
}

#[test]
fn un_no_del_relay_se_distingue_de_un_silencio() {
    // Los dos son fallos y mandan a mirar sitios opuestos: uno es «el relay dijo que no» y el otro
    // «el relay no está o no me llega».
    let now = Instant::now();
    let mut client = RelayClient::new(config(false));
    client.poll(now);

    let denial = RelayFrame::to_peer(
        SESSION,
        RELAY_ENDPOINT,
        0,
        RelayMessage::Auth {
            status: AuthStatus::WireVersionMismatch,
        },
    );
    let event = client.on_control(denial, now);

    assert_eq!(
        event,
        Some(RelayClientEvent::Denied(AuthStatus::WireVersionMismatch))
    );
    assert_eq!(client.state().name(), "WIRE_VERSION_MISMATCH");
    assert!(client.state().is_final());
    assert!(client.poll(now + Duration::from_secs(2)).is_none());
}

#[test]
fn una_vez_dentro_se_late_cada_dos_segundos() {
    let now = Instant::now();
    let mut client = RelayClient::new(config(false));
    client.poll(now);
    client.on_control(peer_ready(3), now);

    assert!(
        client.poll(now + Duration::from_secs(1)).is_none(),
        "al segundo todavía no"
    );

    let bytes = client
        .poll(now + Duration::from_secs(2))
        .expect("a los dos segundos sí");
    let frame = decode(&bytes);
    assert_eq!(frame.message, RelayMessage::Heartbeat);
    assert_eq!(frame.src, 3, "late con el id que le dio el relay");
}

#[test]
fn el_latido_deja_cuatro_oportunidades_antes_de_que_el_relay_nos_de_por_muertos() {
    // No es aritmética ociosa: si el intervalo se acercara al `peer_timeout` del relay, UN latido
    // perdido bastaría para que nos expulsaran a mitad de partida.
    let timeout = backrooms_relay::session::RelayLimits::default().peer_timeout;
    assert!(
        HEARTBEAT_INTERVAL * 4 <= timeout,
        "latido {HEARTBEAT_INTERVAL:?} contra timeout {timeout:?}"
    );
}

#[test]
fn un_peer_ready_repetido_no_reabre_nada() {
    // El relay contesta a CADA reintento, y los nuestros se cruzan con su primera respuesta.
    let now = Instant::now();
    let mut client = RelayClient::new(config(false));
    client.poll(now);

    assert!(client.on_control(peer_ready(2), now).is_some());
    assert!(
        client.on_control(peer_ready(2), now).is_none(),
        "el segundo no es un evento nuevo"
    );
    assert_eq!(client.link().unwrap().my_peer, 2);
}

#[test]
fn el_host_se_entera_de_quien_entra_y_de_quien_se_va() {
    let now = Instant::now();
    let mut host = RelayClient::new(config(true));
    host.poll(now);
    host.on_control(peer_ready(HOST_PEER_ID), now);

    let joined = RelayFrame::to_peer(
        SESSION,
        RELAY_ENDPOINT,
        HOST_PEER_ID,
        RelayMessage::PeerJoined { peer: 6 },
    );
    assert_eq!(
        host.on_control(joined, now),
        Some(RelayClientEvent::PeerJoined(6))
    );

    let left = RelayFrame::to_peer(
        SESSION,
        RELAY_ENDPOINT,
        HOST_PEER_ID,
        RelayMessage::PeerDisconnected {
            peer: 6,
            reason: DisconnectReason::Timeout,
        },
    );
    assert_eq!(
        host.on_control(left, now),
        Some(RelayClientEvent::PeerLeft(6, DisconnectReason::Timeout))
    );
}

#[test]
fn cuando_la_sesion_se_cierra_el_enlace_se_cae() {
    let now = Instant::now();
    let mut client = RelayClient::new(config(false));
    client.poll(now);
    client.on_control(peer_ready(2), now);
    assert!(client.link().is_some());

    let closed = RelayFrame::to_peer(
        SESSION,
        RELAY_ENDPOINT,
        2,
        RelayMessage::SessionClosed {
            reason: CloseReason::HostLeft,
        },
    );
    assert_eq!(
        client.on_control(closed, now),
        Some(RelayClientEvent::Closed(CloseReason::HostLeft))
    );
    assert!(
        client.link().is_none(),
        "sin sesión no hay por dónde mandar"
    );
    assert!(client.state().is_final());
}

#[test]
fn un_marco_de_otra_sesion_no_nos_toca() {
    // Resto de un enlace anterior, o un relay compartido contestando tarde.
    let now = Instant::now();
    let mut client = RelayClient::new(config(false));
    client.poll(now);

    let ajeno = RelayFrame::to_peer(
        SESSION + 1,
        RELAY_ENDPOINT,
        2,
        RelayMessage::SessionClosed {
            reason: CloseReason::HostLeft,
        },
    );
    assert!(client.on_control(ajeno, now).is_none());
    assert_eq!(*client.state(), RelayClientState::Registering);
}

#[test]
fn el_joiner_sabe_a_que_direccion_sintetica_mandar_su_handshake() {
    let client = RelayClient::new(config(false));
    assert_eq!(
        client.host_synthetic_addr(),
        crate::network::transport::synthetic_addr(SESSION, HOST_PEER_ID)
    );
}
