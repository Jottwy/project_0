# Player Damage Sync — [G] (ADR-024)

> Piloto de arquitectura de contexto. Carpetas del subsistema: `Assets/_Migration/STPIntegration/RemoteAvatar/` (hook de proxy) + campo `hit_seq` en `Assets/Scripts/Network/` (cliente) y en el backend Rust (`player.rs`, `ipc/mod.rs`, `network/protocol.rs`, `network/peer.rs`, `sync.rs`, `game_loop.rs`).

Fuente completa: [`../DECISIONS.md`](../DECISIONS.md) — buscar `## ADR-024`. Este documento es un resumen operativo, no reemplaza el ADR.

## Qué hace
Cuando un peer recibe daño LOCAL (caída, peligro ambiental; combate a futuro), su proxy 3P en las pantallas de los demás ejecuta un flinch cosmético. Es presentación pura — **no autoritativo**, no toca combate/hitreg/inventario/stats. El daño autoritativo real es ADR-010/ADR-029 (PvP), ortogonal a este sistema.

## Decisión de diseño
- Campo tipado `hit_seq: u8` en la pose relay — **contador monotónico** (wrapping), no un bool ni el canal `animation`. Se envía como NIVEL en cada pose (mirror de `crouch`/`pitch`/`equipment`/`held_item`), no como evento one-shot.
- El proxy detecta el delta (`!=` contra el último valor visto, sentinela `int.MinValue` para el primer sample) y dispara un flinch por cada incremento.
- Self-healing sobre pérdida UDP (un paquete perdido se corrige en la siguiente pose); golpes muy seguidos pueden coalescer en un solo flinch (aceptable, cosmético).
- Late-joiner correcto gratis: el proxy no flinchea al entrar (sentinela).
- Origen de la señal: evento `IHealthManager.DamageReceived` del character LOCAL — nunca sondeo de `Health` (el `StatInterpolator` de ADR-009 escribe salud vía `SetHealthSilent`, sin evento; sondear daría falsos flinches en reconciliación).

## Superficie de wire (schema v6→v7)
`Player.hit_seq` (backend) → `ipc::PlayerInput.hit_seq` (cliente→backend, serde default) → `ipc::RemotePlayerState.hit_seq` (backend→Unity) → `PacketPayload::PlayerUpdate.hit_seq` (P2P) → `PeerConnection.hit_seq` → `NetworkEvent::RemotePlayerUpdate.hit_seq` → `broadcast_player_update`/`broadcast_peer_poses` → `build_world_state`.

`update_player_state` NO se toca (mismo patrón que `held_item`) → el robapieles (ADR-016) siempre tiene `hit_seq=0`, nunca flinchea, gratis.

## Componentes cliente (C#)
- `PlayerPoseTransmitter` — se suscribe a `DamageReceived` del character local, incrementa `_hitSeq` (byte wrapping), lo pasa a `IPCClient.SendPlayerInput`. Único campo EMPUJADO por evento (el resto se lee por-pose). Desuscribe en rig rebuild / `OnDestroy`.
- `IPCMessages.RemotePlayerMsg` / `RemotePlayerView.hitSeq` — reset a 0 en Acquire/Release (pool).
- `ProxyHitReactionHook` (NUEVO) — cachea bones de spine por nombre (rig Generic: `UpperSpine`/`MiddleSpine`), change-detect por sentinela, arranca un impulso decayente (~0.3s) que en `LateUpdate` aplica recoil ADITIVO alrededor de `transform.right`. Removible.
- Cableado: `RemoteAvatarPrefabBuilder.WireHitReactionHook` (defaults `_magnitude=18`, `_recoverTime=0.3`), hornea el hook en `RemotePlayerAvatar.prefab`.

## Invariantes
- Ortogonal a `crouch`/`pitch`/`equipment`/`held_item`/`animation` — un peer agachado + mirando arriba + con casco + hacha en mano Y recibiendo un golpe: los seis ejes viajan juntos sin pisarse.
- Receptor v6 sin `hit_seq` → decodifica `0` (nunca flinchea), compat hacia atrás sin error.
- No muta estado de juego real — pura pose/presentación.

## Estado (ver `../STATE.md` para el detalle vivo y actualizado)
- Backend: implementado, `cargo test` verde, release GNU recompilado y desplegado (`grep -aoc hit_seq exe`=2).
- Cliente: implementado (compile-green, commiteado).
- **Pendiente**: (1) bake del prefab — menú *Backrooms ▸ Build Remote Avatar Prefab* (añade `ProxyHitReactionHook` al prefab; no hecho, editor abierto al momento de escribir esto). (2) play-test + calibración en vivo de `_magnitude`/`_recoverTime`/`_invert`.
- Slice 2 (diferido, sin ADR nuevo): direccionalidad vía `DamageArgs`, clip real de flinch en vez de recoil procedural, escala por severidad.

## Protocolo de cierre de sesión (piloto de arquitectura de contexto)
Secuencia obligatoria para cerrar un incremento de trabajo en este piloto —
no cerrar saltándose pasos:

1. **Tests**: `cargo test` en `backend/` si se tocó Rust; compile-check/build
   del lado C# si se tocó Unity (ver checklist en `.claude/rules/pose-relay-proxy-hook-csharp.md`).
2. **Invocar al agente `revisor-diffs` en su Rol 2 (evaluador de cierre)** —
   NO el Rol 1 de revisión rápida. Debe ejecutar build/tests él mismo (no
   asumir que el paso 1 ya alcanza) y devolver `BLOQUEANTES` /
   `ADVERTENCIAS` / `OK PARA COMMIT`.
3. **Si `OK PARA COMMIT: sí`**: actualizar `docs/STATE.md` (Última sesión /
   Próximo paso, respetando su esquema) reflejando el incremento cerrado.
4. **Commit descriptivo** (`feat(net): …` / `fix(net): …`, ver
   `docs/CONVENTIONS.md`), scope acotado al piloto.

**Si el evaluador devuelve `BLOQUEANTES`**: el incremento NO se cierra — no
se toca `STATE.md`, no hay commit. Se resuelven los bloqueantes y se repite
desde el paso 1 (o al menos desde el paso 2 si el paso 1 ya sigue en verde).

Los hooks deterministas (`.claude/settings.json`) son un piso mínimo
(formato/lint por archivo tocado, guard de `DECISIONS.md`, aviso si
`STATE.md` no se tocó hoy) — NO sustituyen este protocolo, lo complementan.

## Alternativas rechazadas (ver ADR-024 para detalle)
- Reusar `animation:String` con valor "hit" — colisiona con el flanco de pickup (ADR-011 acota el canal a una acción).
- Bool `hit` sostenido con ventana — no expresa golpes repetidos dentro de la ventana.
- Sondear el delta de `Health` — falsos flinches en cada reconciliación de `StatInterpolator`.
- Daño autoritativo real en este slice — es ADR-010/ADR-029, fuera de alcance (este ADR es solo presentación).
