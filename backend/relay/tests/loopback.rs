//! El relay CON sockets de verdad, en loopback.
//!
//! Los tests de `session` prueban las reglas sin red; éstos prueban lo único que aquellos no
//! pueden: que un datagrama entra por un socket, sale por otro, y llega con lo que tiene que
//! llegar. Es la diferencia entre «la tabla decide reenviar» y «el byte llegó».
//!
//! Puerto 0 en todos los sockets: el sistema elige uno libre. Un puerto fijo en un test es un test
//! que falla el día que alguien tiene ese puerto ocupado.

use std::time::Duration;

use backrooms_relay::protocol::{
    AuthStatus, PeerId, RelayFrame, RelayMessage, SessionId, SessionToken,
    MAX_GAMEPLAY_PAYLOAD_BYTES,
};
use backrooms_relay::server;
use backrooms_relay::session::{RelayLimits, RelayTable, HOST_PEER_ID};
use tokio::net::UdpSocket;
use tokio::sync::oneshot;

const SESSION: SessionId = 0xBEEF;
const WIRE: u16 = 55;

fn token() -> SessionToken {
    SessionToken::new([3u8; 16])
}

/// Levanta un relay en un puerto libre. Devuelve su dirección y el interruptor de apagado.
async fn start_relay() -> (std::net::SocketAddr, oneshot::Sender<()>) {
    let socket = UdpSocket::bind("127.0.0.1:0").await.unwrap();
    let addr = socket.local_addr().unwrap();
    let (tx, rx) = oneshot::channel();

    tokio::spawn(async move {
        server::serve(socket, RelayTable::new(RelayLimits::default()), async {
            let _ = rx.await;
        })
        .await;
    });

    (addr, tx)
}

async fn client() -> UdpSocket {
    UdpSocket::bind("127.0.0.1:0").await.unwrap()
}

async fn send(sock: &UdpSocket, to: std::net::SocketAddr, frame: RelayFrame) {
    sock.send_to(&frame.encode().unwrap(), to).await.unwrap();
}

/// Espera un datagrama, con tope. Sin tope, un fallo se manifestaría como un test colgado para
/// siempre en vez de como un fallo.
async fn recv(sock: &UdpSocket) -> RelayFrame {
    let mut buf = [0u8; 2048];
    let (n, _) = tokio::time::timeout(Duration::from_secs(5), sock.recv_from(&mut buf))
        .await
        .expect("el relay no contestó a tiempo")
        .unwrap();
    RelayFrame::decode(&buf[..n]).expect("el relay mandó algo que no se puede leer")
}

/// Host y joiner dentro de la misma sesión, por sockets reales.
async fn session_with_joiner() -> (
    std::net::SocketAddr,
    UdpSocket,
    UdpSocket,
    PeerId,
    oneshot::Sender<()>,
) {
    let (relay, shutdown) = start_relay().await;
    let host = client().await;
    let joiner = client().await;

    send(
        &host,
        relay,
        RelayFrame::to_relay(
            SESSION,
            0,
            RelayMessage::SessionCreate {
                token: token(),
                wire_version: WIRE,
            },
        ),
    )
    .await;
    let ready = recv(&host).await;
    assert!(matches!(
        ready.message,
        RelayMessage::PeerReady {
            assigned_peer: HOST_PEER_ID,
            ..
        }
    ));

    send(
        &joiner,
        relay,
        RelayFrame::to_relay(
            SESSION,
            0,
            RelayMessage::SessionJoin {
                token: token(),
                wire_version: WIRE,
            },
        ),
    )
    .await;
    let RelayMessage::PeerReady {
        assigned_peer,
        host_peer,
    } = recv(&joiner).await.message
    else {
        panic!("el joiner tenía que recibir su PeerReady");
    };
    assert_eq!(host_peer, HOST_PEER_ID);

    // Y el host se entera de que ha entrado alguien.
    let joined = recv(&host).await;
    assert_eq!(
        joined.message,
        RelayMessage::PeerJoined {
            peer: assigned_peer
        }
    );

    (relay, host, joiner, assigned_peer, shutdown)
}

#[tokio::test]
async fn un_datagrama_del_joiner_llega_al_host_por_el_relay() {
    let (relay, host, joiner, joiner_id, _shutdown) = session_with_joiner().await;

    let payload = b"esto es un paquete de gameplay opaco".to_vec();
    send(
        &joiner,
        relay,
        RelayFrame::to_peer(
            SESSION,
            joiner_id,
            HOST_PEER_ID,
            RelayMessage::Data {
                payload: payload.clone(),
            },
        ),
    )
    .await;

    let recibido = recv(&host).await;
    assert_eq!(recibido.src, joiner_id);
    assert_eq!(recibido.dst, HOST_PEER_ID);
    assert_eq!(recibido.message, RelayMessage::Data { payload });
}

#[tokio::test]
async fn el_host_contesta_al_joiner_por_el_mismo_camino() {
    let (relay, host, joiner, joiner_id, _shutdown) = session_with_joiner().await;

    let payload = vec![0x5A; 700];
    send(
        &host,
        relay,
        RelayFrame::to_peer(
            SESSION,
            HOST_PEER_ID,
            joiner_id,
            RelayMessage::Data {
                payload: payload.clone(),
            },
        ),
    )
    .await;

    let recibido = recv(&joiner).await;
    assert_eq!(recibido.src, HOST_PEER_ID);
    assert_eq!(recibido.message, RelayMessage::Data { payload });
}

#[tokio::test]
async fn un_payload_en_el_techo_cruza_entero() {
    // 1200 B de gameplay + 16 de sobre = 1216 en el cable (ADR-113 enm. 3). Es el caso que decide
    // si el relay sirve para este juego: los chunks van justo por ahí (página máxima medida
    // 1196 B).
    let (relay, host, joiner, joiner_id, _shutdown) = session_with_joiner().await;

    let payload: Vec<u8> = (0..MAX_GAMEPLAY_PAYLOAD_BYTES)
        .map(|i| (i % 251) as u8)
        .collect();
    send(
        &joiner,
        relay,
        RelayFrame::to_peer(
            SESSION,
            joiner_id,
            HOST_PEER_ID,
            RelayMessage::Data {
                payload: payload.clone(),
            },
        ),
    )
    .await;

    let recibido = recv(&host).await;
    assert_eq!(
        recibido.message,
        RelayMessage::Data { payload },
        "los 1200 bytes tienen que llegar sin perder ni cambiar uno"
    );
}

#[tokio::test]
async fn el_relay_no_reenvia_entre_joiners() {
    // La estrella, sobre sockets reales. El segundo joiner NO puede recibir nada del primero.
    let (relay, _host, joiner_a, id_a, _shutdown) = session_with_joiner().await;
    let joiner_b = client().await;

    send(
        &joiner_b,
        relay,
        RelayFrame::to_relay(
            SESSION,
            0,
            RelayMessage::SessionJoin {
                token: token(),
                wire_version: WIRE,
            },
        ),
    )
    .await;
    let RelayMessage::PeerReady {
        assigned_peer: id_b,
        ..
    } = recv(&joiner_b).await.message
    else {
        panic!("el segundo joiner tenía que entrar");
    };
    assert_ne!(id_a, id_b);

    send(
        &joiner_a,
        relay,
        RelayFrame::to_peer(
            SESSION,
            id_a,
            id_b,
            RelayMessage::Data {
                payload: b"hola vecino".to_vec(),
            },
        ),
    )
    .await;

    let mut buf = [0u8; 2048];
    let esperado_silencio =
        tokio::time::timeout(Duration::from_millis(300), joiner_b.recv_from(&mut buf)).await;
    assert!(
        esperado_silencio.is_err(),
        "un joiner no puede recibir nada de otro joiner"
    );
}

#[tokio::test]
async fn un_token_que_no_es_recibe_un_no_por_el_socket() {
    let (relay, shutdown) = start_relay().await;
    let host = client().await;
    let intruso = client().await;

    send(
        &host,
        relay,
        RelayFrame::to_relay(
            SESSION,
            0,
            RelayMessage::SessionCreate {
                token: token(),
                wire_version: WIRE,
            },
        ),
    )
    .await;
    recv(&host).await;

    send(
        &intruso,
        relay,
        RelayFrame::to_relay(
            SESSION,
            0,
            RelayMessage::SessionJoin {
                token: SessionToken::new([0xFF; 16]),
                wire_version: WIRE,
            },
        ),
    )
    .await;

    let respuesta = recv(&intruso).await;
    assert_eq!(
        respuesta.message,
        RelayMessage::Auth {
            status: AuthStatus::Denied
        }
    );
    drop(shutdown);
}

#[tokio::test]
async fn a_la_basura_no_se_le_contesta_nada() {
    // Sin respuesta no hay amplificación. Se comprueba con el socket, que es donde importa.
    let (relay, _shutdown) = start_relay().await;
    let curioso = client().await;

    curioso.send_to(b"GET / HTTP/1.1", relay).await.unwrap();
    curioso.send_to(&[0u8; 40], relay).await.unwrap();

    let mut buf = [0u8; 2048];
    let silencio =
        tokio::time::timeout(Duration::from_millis(300), curioso.recv_from(&mut buf)).await;
    assert!(
        silencio.is_err(),
        "el relay no contesta a lo que no entiende"
    );
}

#[tokio::test]
async fn al_apagarse_el_relay_se_despide_de_los_suyos() {
    // Cuesta un datagrama por peer y le ahorra a cada cliente los 8 s de timeout preguntándose si
    // el relay sigue ahí.
    let (_relay, host, joiner, _id, shutdown) = session_with_joiner().await;

    drop(shutdown);

    for sock in [&host, &joiner] {
        let despedida = recv(sock).await;
        assert!(
            matches!(despedida.message, RelayMessage::SessionClosed { .. }),
            "cada peer tiene que recibir el cierre"
        );
    }
}
