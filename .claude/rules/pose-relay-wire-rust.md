---
paths:
  - "backend/src/player/mod.rs"
  - "backend/src/player/session.rs"
  - "backend/src/ipc/mod.rs"
  - "backend/src/network/protocol.rs"
  - "backend/src/network/peer.rs"
  - "backend/src/network/sync.rs"
  - "backend/src/game_loop.rs"
---

# Convención: campo tipado en pose relay (lado Rust / wire)

Estos son los archivos que forman la "superficie de pose relay" — donde vive
el patrón repetido de ADR-020/021/022/023/024 (crouch/pitch/equipment/
held_item/hit_seq). Esta regla aplica SOLO al tocar un campo de ESE patrón en
estos archivos, no a cualquier otro cambio que pase por ellos.
Referencia: `docs/DECISIONS.md` (`## ADR-024`) y
[docs/systems/damage-sync.md](../../docs/systems/damage-sync.md).

## Cadena obligatoria de un campo nuevo (mismo orden que ADR-024 §"Alcance Rust")
1. `player::Player` (o `player::session::Player`) += el campo, con su init
   por defecto.
2. `game_loop.rs` — junto a los sellos de campos hermanos ya existentes
   (ej. `player.held_item = received_input.held_item`), añade el sello del
   campo nuevo en la MISMA línea de bloque, no en un sitio distinto.
3. `ipc::PlayerInput` += `#[serde(default)] campo: T` (cliente→backend).
4. `ipc::RemotePlayerState` += `#[serde(default)] campo: T` (backend→Unity).
5. `network::protocol::PacketPayload::PlayerUpdate` += `campo: T` (P2P,
   serde default) — **actualiza el round-trip test** con un valor no-default
   para el campo nuevo.
6. `network::peer::PeerConnection` += `campo: T` (init default); fija el
   valor SOLO en `handle_packet` rama `PlayerUpdate`. **`update_player_state`
   se deja SIN TOCAR a propósito** (mismo patrón en los 5 ADRs) — así el
   robapieles (ADR-016) hereda el default del campo gratis, sin lógica
   dedicada.
7. `network::NetworkEvent::RemotePlayerUpdate` += `campo`.
8. `sync::broadcast_player_update` escribe el campo; `broadcast_peer_poses`
   (relay ADR-015) lo reenvía; `build_world_state` lo copia.

## Bump de schema
Siempre `#[serde(default)]` en los campos nuevos → un peer con la versión
vieja interopera decodificando el default (nunca error, degradación
cosmética). Bump de `WIRE_SCHEMA_VERSION` en `ipc/server.rs` — esto SIEMPRE
requiere ADR nuevo (regla dura #7 de CLAUDE.md: cambio de API pública).

## Invariante de higiene
Antes de cerrar: `cargo test` en verde, `grep -aoc <campo> backrooms_server.exe`
tras `cargo build --release` (release GNU) para confirmar que el binario
desplegado en `Builds/Backend/` contiene el campo — `cargo test --release`
NO relinkea el binario, ver memoria del proyecto.

## Al replicar este patrón a otro sistema
Sigue los 8 pasos en el mismo orden; no saltes `update_player_state` sin una
razón documentada (es la garantía de que el robapieles no imita estados que
no debería).
