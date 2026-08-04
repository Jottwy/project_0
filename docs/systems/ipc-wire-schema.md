# IPC wire schema — changelog v2 → v19

> **La autoridad sobre el número es el CÓDIGO**: `backend/src/ipc/server.rs`, constante
> `WIRE_SCHEMA_VERSION` (hoy **19**). Este documento es el changelog, no la versión. Al
> bumpear la constante, añade aquí la entrada correspondiente **en el mismo commit**.
>
> Fuente de la decisión: [`../DECISIONS.md`](../DECISIONS.md) — cada entrada cita su ADR.
> Bumpear `WIRE_SCHEMA_VERSION` SIEMPRE requiere ADR nuevo (regla dura #7 de `CLAUDE.md`:
> cambio de API pública).

## Qué versiona

Revisión del esquema de wire de ADR-009. Se bumpea cuando cambia el esquema de
input/estado de jugador; **el transporte en sí no cambia** (sigue siendo MessagePack con
prefijo de longitud de 4 bytes big-endian).

## Regla: qué obliga a bumpear

Un cambio **solo-P2P también bumpea** este contador. El log se contradecía a sí mismo
(ADR-039 lo llamaba "el esquema IPC", pero ADR-028 Fase E bumpeó v8→v9 por cuatro variantes
P2P con la superficie IPC intacta) y ADR-047 lo zanja por escrito: **añadir un
`PacketPayload` bumpea**.

Coordinación entre ADRs en vuelo (caso real ADR-046 / ADR-047): el CÓDIGO es la autoridad —
quien aterrice segundo lee la constante y toma el número siguiente. ADR-046 deliberadamente
no escribió número fijo en el documento precisamente por esto; aterrizó segundo y tomó el 17.

Patrón invariante de compatibilidad: campos nuevos siempre `#[serde(default)]`, de modo que
un peer con la versión vieja interopera decodificando el default — nunca error, degradación
cosmética.

## Changelog

> Entradas transcritas literalmente del doc-comment que vivía sobre la constante (inglés
> original), sin reescribir, para no perder matices ni introducir errores de traducción.

### v2

Adds the client-prediction fields to `PlayerInput` and `ack_input_seq`/`stamina` to the
snapshot.

### v3 (ADR-020)

Adds `crouch:bool` to `PlayerInput` and `RemotePlayerState` — all `serde(default)`, so v1/v2
clients still interoperate (a missing `crouch` decodes to false).

### v4 (ADR-021)

Adds `pitch:i8` to `RemotePlayerState` (and the P2P `PlayerUpdate`) — reusing the existing
`PlayerInput.look[0]` for input, also `serde(default)` (missing `pitch` decodes to 0 =
looking forward).

### v5 (ADR-022)

Adds `equipment:[i32;4]` (worn clothing item IDs) to `PlayerInput`, `RemotePlayerState` and
the P2P `PlayerUpdate` — all `serde(default)`, so older clients interoperate (a missing
`equipment` decodes to `[0,0,0,0]` = no clothing).

### v6 (ADR-023)

Adds `held_item:i32` (the held wieldable item ID) to `PlayerInput`, `RemotePlayerState` and
the P2P `PlayerUpdate` — also `serde(default)`, so older clients interoperate (a missing
`held_item` decodes to 0 = empty hands).

### v7 (ADR-024)

Adds `hit_seq:u8` (a monotonic hit-reaction counter, incremented on each local
`DamageReceived`) to `PlayerInput`, `RemotePlayerState` and the P2P `PlayerUpdate` — also
`serde(default)`, so older clients interoperate (a missing `hit_seq` decodes to 0 = never
hit, no flinch). Resumen operativo: [`damage-sync.md`](damage-sync.md).

### v8 (ADR-028)

Adds `visible_corpses: Vec<CorpseView>` to `WorldState` (lootable corpses: id, owner, frozen
death position, equipment/held_item snapshot, loot stacks) — `serde(default)` and skipped
when empty, so a v7 client interoperates (it simply never sees corpses).

### v9 (ADR-028 Fase E)

Adds four P2P `PacketPayload` variants for the host-authoritative corpse relay —
`CorpseList` broadcast, `CorpseSpawnRequest` and `CorpseTakeRequest` joiner→host, and
`CorpseTakeResult` host→requester; the IPC surface is unchanged from v8. A v8 peer drops the
unknown packets on decode (fails the payload parse, packet ignored) and simply never sees
remote corpses.

### v10 (ADR-028 post-E3)

Adds `dead:bool` to `RemotePlayerState` and the P2P `PlayerUpdate` — SERVER-derived
(`player.stats.is_dead()`, not client-reported; `PlayerInput` unchanged), `serde(default)`,
so a v9 peer decodes false (never hides the proxy).

### v11 (ADR-037)

Adds the `stp_demolish` IPC action and one P2P `PacketPayload` variant, `StpDemolishRequest`
(0x1D), so a cancelled-but-unbuilt building piece is retired from the host-authoritative
roster instead of being respawned by the relay. Nothing existing changes shape. A v10 peer
fails the payload parse and ignores the packet, so the canceller sees the piece come back —
exactly today's behaviour, which is the degradation this bump is meant to fix rather than to
introduce.

### v12 (ADR-038)

Adds `revealed:bool` to `RemotePlayerState` and the P2P `PlayerUpdate` — BACKEND-derived
(sealed by `PhantomDriver` from its `Sprint`/`Statue` states; `PlayerInput` deliberately
unchanged, so no client can set it), `serde(default)`, so a v11 peer decodes false and simply
never sees the robapieles drop its disguise.

### v13 (ADR-041)

Adds the `report_noise` client action (`position` + `loudness` in metres), the stimulus that
lets a gunshot reach the robapieles. Additive and inert: a client that never sends it simply
never attracts the phantom, with no error on either side. Nothing else changes shape, and it
does NOT enter the P2P surface — the phantom is host-authoritative (ADR-016).

### v14 (ADR-042)

Adds `light_on:bool` (the active wieldable is emitting light) and `fire_seq:u8` (a monotonic
shot counter, bumped on each native `IFirearmTrigger.Shoot`) to `PlayerInput`,
`RemotePlayerState` and the P2P `PlayerUpdate` — both client-reported and `serde(default)`,
so a v13 peer decodes false/0 and simply sees a dark, silent peer. Cosmetic only: neither
field feeds the phantom's perception, which hears exclusively through v13's `report_noise`.

### v15 (ADR-044)

Adds `melee_seq:u8` (a monotonic melee-swing counter) and promotes the EXISTING `buttons:u16`
from a dead field to a cosmetic sustained-state bitfield (bit 0 = aiming, bit 1 = reloading)
carried by `RemotePlayerState` and the P2P `PlayerUpdate`. `buttons` already lived in
`PlayerInput`, written as a literal 0 and read by nobody, so the client frame gains ONE
field, not two. Both `serde(default)`, so a v14 peer decodes 0/0 and simply never aims,
reloads or swings.

### v16 (ADR-047)

Adds TWO P2P packet types and changes NO client-facing field: `PhantomAttackGrant` (0x4D,
host → the victim's backend, reliable) and `NoiseReport` (0x4E, joiner → host, unreliable).
Until now the robapieles could neither hurt anyone but the host — it damaged the HOST even
while attacking a joiner, because the attack carried no victim — nor hear a joiner's gunshot
at all.

Este es el bump que fija por escrito la regla "un cambio solo-P2P bumpea" (ver arriba).

### v17 (ADR-046)

Adds the voice path: `ClientMessage::Voice { seq, data }` inbound and
`ServerMessage::PeerVoice { peer_id, seq, data }` outbound. ADR-046 deliberately wrote no
fixed number into the document precisely so this could be read off the code — landed second,
took 17. Additive and inert in both directions: a client that never speaks is byte-identical
to a v16 one.

### v18 (ADR-048)

Adds `vocal_seq:u8` + `vocal_kind:u8` to `RemotePlayerState` and the P2P `PlayerUpdate` —
BACKEND-derived like `revealed`, absent from `PlayerInput`, so no client writer changes and
the C6 goldens stay valid. Additive and inert: a creature that never vocalises is
byte-identical to a v17 one, and a v17 receiver decodes both to 0 and simply hears nothing.

### v19 (ADR-049) — actual

Adds `carry_def:i32` + `carry_count:u8` to `PlayerInput`, `RemotePlayerState` and the P2P
`PlayerUpdate`. Unlike v18 these ARE client-reported — the backend keeps no per-player carry
state to derive them from — so this is the first pose bump in a while that touches the C#
writer: `SendPlayerInput` goes from 19 to 21 fields and its golden is regenerated ON PURPOSE.
Additive and inert otherwise: a player who never carries is byte-identical to a v18 one, and
a v18 receiver decodes both to 0 and simply sees empty hands.
