---
paths:
  - "backend/src/ipc/*.rs"
  - "backend/src/network/*.rs"
  - "backend/src/game_loop.rs"
  - "Assets/Scripts/Network/*.cs"
---

# Red: wire, autoridad y las tres formas de romperlo sin que nadie avise

Aplica a cualquier cambio de protocolo o de autoridad. La ley son los ADR (003, 004, 009, 030, 044,
061, 081, 117); esto es lo que muerde al implementarlos.

## 1. El wire tiene DOS puntas y sólo una da error

`WIRE_SCHEMA_VERSION` (`backend/src/ipc/server.rs:38`) y `WireSchema.Expected`
(`Assets/Scripts/Network/WireSchema.cs:25`) valen hoy **60** y tienen que subir **en el mismo
commit**. Desde ADR-061, bumpear una y no la otra no da un aviso: deja el juego **inarrancable**
(`wire_schema_mismatch`).

Y antes de subirlo: **regla dura 7** — un cambio de protocolo, de formato de chunk o de schema de
guardado exige **ADR nuevo antes de tocar código**.

## 2. Comprueba si de verdad hace falta un campo

ADR-044 dejó **bits libres en `buttons`**. Un estado sostenido nuevo entre jugadores cabe ahí: sin
campo nuevo, sin bump de wire y sin tocar Rust. Mirar eso primero cuesta un minuto y ahorra un ADR.

Si el campo sí hace falta: va **append-only** al final de su `PacketPayload`, y el test de
round-trip se actualiza con valores **no-default y distintos entre sí** — con ceros, un campo mal
colocado pasa igual.

## 3. `update_player_state` se deja en paz

Es el punto donde los campos cosméticos heredan sus defaults gratis. Lo específico del robapieles
se escribe en `PhantomDriver` (`seal_cosmetics`), **al lado** de la llamada, nunca dentro. Va por la
octava vez que se respeta; ver `.claude/rules/pose-relay-wire-rust.md` paso 6.

## 4. Autoridad: la posición se mide contra el ROSTER, jamás contra el paquete

`authoritative_requester_pos` (`game_loop.rs`) da la pose que el host ya conoce, y si no la hay se
rechaza (`reason=unknown_pose`); aceptar sin pose reabre el agujero entero. Igual con las
cantidades: un `amount` del cliente se valida finito y positivo y **se recorta** a su tope
(`MAX_HARVEST_FRACTION_PER_HIT`), porque un `NaN` envenena `remaining` para siempre —toda
comparación con él es falsa— y sin log que lo explique después.

Territorio: `owner_id` en la pieza + `requester_id` de la cabecera del paquete. `owner_id == 0` no
lo demuele nadie, y es decisión de Joel, no un efecto del código.

**Agujero ABIERTO a 2026-09-05:** `process_stp_build_add` (`game_loop.rs:7297`) no mira `owner_id`
ni distancia, y `process_stp_demolish` valida dueño pero **no** distancia. Si tocas construcción,
esto es lo primero.

## 5. Toda lista nueva en la cabecera de un chunk necesita paginarse

`split_chunk_pages` (`network/sync.rs:221`) reparte `entities` e `items`. Un `Vec` nuevo en
`layout` que no pase por ahí **repite su peso en cada página**, así que ninguna cantidad de páginas
salva el chunk: se rechaza por presupuesto de datagrama y ese chunk **no llega jamás** a ese peer.
Pasó con `layout.inter_layer_volumes` y sólo se vio con un joiner real al otro lado de un NAT.

## 6. Determinismo

Nada de emitir salida iterando un `HashSet`/`HashMap` sin ordenar antes (regla dura 13).
`item_id` y `entity_id`, estables. Misma seed + misma versión ⇒ mismo chunk en las dos puntas.

## 7. Nada de `catch { }` mudo

`IPCClient.cs` ya arrastra cuatro en los notificadores de listeners, y pueden estar tragando fallos
hoy mismo. No añadas el quinto.
