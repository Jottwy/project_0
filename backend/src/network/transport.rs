//! Direcciones sintéticas: cómo un peer que sólo se alcanza por relay se parece a cualquier otro
//! peer — ADR-117 D3.
//!
//! ## El problema, y por qué la solución no es un tipo nuevo
//!
//! `PeerConnection.addr` es una `SocketAddr` y la tocan `handlers.rs`, `roster.rs`, `sync.rs`,
//! `peer.rs` y ~5.000 líneas de tests. Cambiarla por un `enum PeerAddr { Direct, Relay }` sería
//! correcto de modelo y sería exactamente el refactor masivo que el encargo prohíbe.
//!
//! Así que un peer alcanzable por relay recibe una `SocketAddr` **de verdad**, sólo que de un
//! rango que nadie más usa, y la traducción vive en los DOS únicos puntos que tocan el socket:
//! `send_datagram` (la salida) y `receive_loop` (la entrada). Entre medias, nada se entera:
//! `handle_handshake` registra un peer, `check_timeouts` lo expulsa y `broadcast_*` le manda
//! chunks sin saber por dónde viajan.
//!
//! ## El rango, y por qué ÉSE
//!
//! `fd52:5245:4c41:5900::/64` — dentro de `fc00::/7`, que RFC 4193 reserva para direcciones
//! locales únicas, así que no es de nadie y no se enruta a ninguna parte. Los bytes deletrean
//! `RELAY` en ASCII (`52 45 4c 41 59`), que es lo que hace que una línea de log se lea sola:
//!
//! ```text
//! HBTRACE event=LIVENESS_SCAN peer_id=7 endpoint=[fd52:5245:4c41:5900::beef]:3
//! ```
//!
//! Eso ya dice «este peer va por relay, sesión beef, peer 3 del relay» sin un solo campo nuevo en
//! ninguna traza.
//!
//! El reparto de los 128 bits es: 64 de prefijo + **64 del `session_id`**. El id de peer del relay
//! va en el **PUERTO**, que es un `u16` y es exactamente lo que hace falta. De ahí sale la
//! propiedad que importa: dos joiners de la misma sesión tienen direcciones DISTINTAS.
//!
//! Sin eso, todos los joiners llegarían al host con la dirección real del relay, y el segundo
//! handshake caería en la rama «ya registrado por endpoint» de `handle_handshake` — el host se
//! creería que es una reconexión del primero y la segunda persona no entraría nunca.
//!
//! ## Lo que NO es
//!
//! Una dirección sintética **jamás sale por el socket**. Si una llegara a `socket.send_to`, el
//! sistema intentaría enrutar una IPv6 desde un socket que hace bind en `0.0.0.0` y fallaría con
//! un error que no señala a nada. Ése es el motivo de que `is_synthetic` se compruebe en el punto
//! de salida y no en el llamante: los puntos de salida son dos, los llamantes son decenas.

use std::net::{Ipv6Addr, SocketAddr, SocketAddrV6};

use backrooms_relay::protocol::{PeerId as RelayPeerId, SessionId};

/// Los 8 primeros bytes de toda dirección sintética: `fd52:5245:4c41:5900`.
///
/// `fd` de ULA (RFC 4193) + `52 45 4c 41 59` = `"RELAY"` + un byte de versión de formato, por si
/// algún día hay que repartir los 128 bits de otra manera sin confundir las dos formas.
const SYNTHETIC_PREFIX: [u8; 8] = [0xfd, 0x52, 0x52, 0x45, 0x4c, 0x41, 0x59, 0x00];

/// La dirección con la que este backend representa a `relay_peer` dentro de `session`.
///
/// El puerto es el id de peer del relay, que **nunca es 0** (el 1 es el host, los joiners van del
/// 2 en adelante). Importa: `sync::is_routable_peer_addr` rechaza el puerto 0, y una dirección que
/// no pasara ese filtro sería descartada por el roster sin decir por qué.
pub fn synthetic_addr(session: SessionId, relay_peer: RelayPeerId) -> SocketAddr {
    let mut octets = [0u8; 16];
    octets[..8].copy_from_slice(&SYNTHETIC_PREFIX);
    octets[8..].copy_from_slice(&session.to_be_bytes());
    SocketAddr::V6(SocketAddrV6::new(Ipv6Addr::from(octets), relay_peer, 0, 0))
}

/// `true` si esta dirección es de las nuestras, o sea si lo que va a ella tiene que ir al relay.
pub fn is_synthetic(addr: &SocketAddr) -> bool {
    match addr {
        SocketAddr::V6(v6) => v6.ip().octets()[..8] == SYNTHETIC_PREFIX,
        SocketAddr::V4(_) => false,
    }
}

/// La sesión y el peer que hay dentro de una dirección sintética, o `None` si no lo es.
pub fn synthetic_parts(addr: &SocketAddr) -> Option<(SessionId, RelayPeerId)> {
    let SocketAddr::V6(v6) = addr else {
        return None;
    };
    let octets = v6.ip().octets();
    if octets[..8] != SYNTHETIC_PREFIX {
        return None;
    }
    let mut session = [0u8; 8];
    session.copy_from_slice(&octets[8..]);
    Some((u64::from_be_bytes(session), v6.port()))
}

/// Sólo el id de peer del relay.
pub fn synthetic_peer(addr: &SocketAddr) -> Option<RelayPeerId> {
    synthetic_parts(addr).map(|(_, peer)| peer)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::network::sync::is_routable_peer_addr;

    #[test]
    fn una_direccion_sintetica_se_reconoce_y_se_desmonta() {
        let addr = synthetic_addr(0xDEAD_BEEF_1234_5678, 7);

        assert!(is_synthetic(&addr));
        assert_eq!(
            synthetic_parts(&addr),
            Some((0xDEAD_BEEF_1234_5678, 7)),
            "la sesión y el peer sobreviven al viaje de ida y vuelta"
        );
        assert_eq!(synthetic_peer(&addr), Some(7));
    }

    #[test]
    fn dos_joiners_de_la_misma_sesion_no_comparten_direccion() {
        // ES LA PROPIEDAD QUE JUSTIFICA TODO ESTO. Si los dos llegaran al host con la dirección
        // real del relay, el segundo handshake caería en la rama «ya registrado por endpoint» de
        // `handle_handshake`: el host lo tomaría por una reconexión del primero y la segunda
        // persona no entraría jamás.
        let a = synthetic_addr(42, 2);
        let b = synthetic_addr(42, 3);

        assert_ne!(a, b);
        assert_ne!(a.port(), b.port());
        assert_eq!(
            a.ip(),
            b.ip(),
            "misma sesión, misma IP; les separa el puerto"
        );
    }

    #[test]
    fn dos_sesiones_distintas_no_se_pisan() {
        assert_ne!(synthetic_addr(1, 2), synthetic_addr(2, 2));
    }

    #[test]
    fn una_direccion_de_verdad_no_es_sintetica() {
        for real in [
            "127.0.0.1:7778",
            "192.168.1.168:7778",
            "94.73.55.235:7778",
            "0.0.0.0:0",
            "[::1]:7778",
            "[2001:db8::1]:7778",
        ] {
            let addr: SocketAddr = real.parse().unwrap();
            assert!(!is_synthetic(&addr), "{real} no puede pasar por sintética");
            assert_eq!(synthetic_parts(&addr), None);
        }
    }

    #[test]
    fn el_centinela_inerte_de_los_fantasmas_no_colisiona() {
        // `INERT_PEER_ADDR` es `127.0.0.1:1` (ADR-079). Es IPv4, así que no puede confundirse con
        // una sintética ni por accidente — pero se comprueba, porque las dos son «direcciones que
        // no son direcciones» y acabar mezclándolas sería un fallo silencioso.
        assert!(!is_synthetic(&crate::network::INERT_PEER_ADDR));
    }

    #[test]
    fn el_roster_acepta_una_direccion_sintetica() {
        // `is_routable_peer_addr` descarta `0.0.0.0` y el puerto 0. Una sintética tiene que
        // pasarlo: si no, el roster la tiraría y el peer relayado desaparecería de las listas sin
        // un solo error.
        assert!(is_routable_peer_addr(&synthetic_addr(1, 1)));
        assert!(is_routable_peer_addr(&synthetic_addr(0, 2)));
    }

    #[test]
    fn el_host_siempre_es_el_peer_uno() {
        // Es la constante de la que depende la regla de la estrella en el relay.
        assert_eq!(
            synthetic_addr(7, backrooms_relay::session::HOST_PEER_ID).port(),
            1
        );
    }

    #[test]
    fn una_sintetica_se_lee_en_el_log_sin_descifrar_nada() {
        // El formato importa: es lo que un humano ve en `HBTRACE ... endpoint=...` cuando algo va
        // mal, y tiene que decirle «esto va por relay» sin consultar esta documentación.
        let texto = synthetic_addr(0xBEEF, 3).to_string();
        assert!(texto.starts_with("[fd52:5245:4c41:5900:"), "{texto}");
        assert!(texto.ends_with("]:3"), "{texto}");
    }
}
