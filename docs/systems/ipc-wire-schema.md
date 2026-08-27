# IPC wire schema — changelog v2 → v43

> **La autoridad sobre el número es el CÓDIGO**: `backend/src/ipc/server.rs`, constante
> `WIRE_SCHEMA_VERSION` (hoy **43**). Este documento es el changelog, no la versión. Al
> bumpear la constante, añade aquí la entrada correspondiente **en el mismo commit**. (El
> título quedó desactualizado en v34→v41 por deuda de proceso ajena — ver nota antes de v42.)
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

## Regla: qué NO obliga a bumpear

Un **bit nuevo dentro de un campo que ya viaja** no bumpea: el esquema no cambia de forma,
los dos lados decodifican igual y un peer viejo simplemente no interpreta el bit. El sitio
para eso es `buttons` (u16), que ADR-044 creó como bitfield de estados SOSTENIDOS y dejó con
14 bits libres a propósito; lo único que ese ADR prohíbe es reusar los bits 0 y 1 con otro
significado. Precedentes: el lean Q/E (bits 2 y 3) y el chorro de spray de ADR-068 fase A
(bit 4), ninguno de los dos con campo nuevo ni una línea de Rust — el backend relaya
`buttons` sin mirarlo.

La asignación de bits vive **una sola vez** en `Assets/Scripts/Network/RemoteButtons.cs`, y
`SprayRelayTests` comprueba que nadie pida uno ya cogido. Un choque de bits no da error: se
manifiesta como "el chorro sale cuando el otro se asoma".

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

### v19 (ADR-049)

Adds `carry_def:i32` + `carry_count:u8` to `PlayerInput`, `RemotePlayerState` and the P2P
`PlayerUpdate`. Unlike v18 these ARE client-reported — the backend keeps no per-player carry
state to derive them from — so this is the first pose bump in a while that touches the C#
writer: `SendPlayerInput` goes from 19 to 21 fields and its golden is regenerated ON PURPOSE.
Additive and inert otherwise: a player who never carries is byte-identical to a v18 one, and
a v18 receiver decodes both to 0 and simply sees empty hands.

### v20 (ADR-050)

No pose field changes at all. Adds one inbound IPC action, two outbound IPC events and one
P2P packet type, all of them for the grab:

- `report_struggle` (client → its own backend). NO PAYLOAD: the victim is the sender, which
  the transport already knows, so unlike `report_noise` there is nothing to clamp or forge.
- `phantom_grab_start { window: f32 }` and `phantom_grab_release` (backend → client), which
  join `phantom_hit` / `phantom_kill` / `phantom_knockback`. `window` is how many seconds the
  victim has to break out, so the client stops holding its own copy of that number.
- `StruggleReport` (0x4F, joiner → host, **reliable**). Claims the opcode ADR-047 reserved
  and stopped short of. Reliable where `NoiseReport` is not: a dropped noise self-heals on the
  next shot, a dropped struggle is a death the player earned their way out of.

`PhantomAttackGrant` (0x4D) gains kinds 3 and 4 with **no layout change** — `kind` was always
a `u8` with 3..255 spare, and ADR-047 wrote that spare down on purpose. The grab window rides
the existing `damage` field, so a v19 victim backend, whose `_` arm treats unknown kinds as a
hit, would apply 2.5 damage instead of opening a window: the v20 backend has explicit arms for
both. Degradation is therefore NOT silent across this bump for a mixed-version session, which
is why it is a bump and not a quiet addition.

### v21 (ADR-054) — actual

Adds `phantom_density_scale: f32` to `PacketPayload::HandshakeAck` (P2P, host → joiner),
`#[serde(default = "default_phantom_density_scale")]` to 1.0 = no scaling. Same precedent as
`world_seed` in the same packet: the phantom population draw (`phantom_spawn::draw_into`) is a
pure function of `world_seed` AND this scalar, so a joiner deriving it from its own process env
instead of the host's would compute a different population from the same seed. A v20 peer omits
the field and decodes 1.0, so an old-vs-new session degrades to "no density scaling applied" —
cosmetic, never an error.

Landed in `fc1ab70` without this bump — the commit reasoned "additive, nothing to break", which
is true of every prior pose-relay field addition (v3 through v19) and none of those skipped the
counter for that reason. The rule this changelog states at the top (a P2P-only change bumps the
counter too, regardless of whether the shape change is additive) applies here exactly as it did
there; this entry and the `WIRE_SCHEMA_VERSION` bump close that gap.

### v22 (ADR-045 Fase 1)

Adds the `set_identity` client action (`key: string`, IPC only — client → its own backend, never
P2P). No new wire struct: `PlayerAction` was already generic (`action_type` + free-form `data`),
same shape as `report_noise`'s v13 bump, itself IPC-only and still counted. Additive and inert: a
client that never sends it leaves `Player::identity_key` at `None` forever, and the backend simply
never resolves a per-player save file for that session (ADR-045 Fase 2) — same degradation
`report_noise` already established for an IPC-only addition.

### v23 (ADR-045 Fase 3)

Widens two existing IPC-only messages, no new action/event name. `report_inventory`'s `items`
entries gain optional `container: u8`, `slot: u8`, `props: [{id: i32, value: f64}]` — a
pre-Fase-3 client's plain `{item_id, quantity}` entries lack `container`/`slot`, so the backend's
new `parse_inventory_v2_stacks` parser skips them (`?` short-circuit) and `Player::inventory_v2`
stays empty for that session; `parse_loot_stacks` keeps populating `stp_inventory` exactly as
before, unaffected. `inventory_restored`'s `items` entries gain the SAME three fields, sent by
the backend when `inventory_v2` is non-empty; when it is empty (every save from Fases 1+2, or a
session with a pre-Fase-3 client either side) the event falls back to the original flat
`{item_id, quantity}` shape, byte-for-byte what Fases 1+2 already emit. Both directions
therefore degrade to exactly today's behavior when either side of the connection predates Fase
3 — additive, `serde(default)`-equivalent (the JSON parser simply doesn't find the new keys).

### v24 (ADR-056) — actual

Adds one IPC-only event, backend → its own Unity client: `session_ended` with
`data: { reason: string }`. Emitted by a JOINER's backend when the peer that leaves is the host
(`NetworkManager::host_peer_id`), carrying the reason forward from the underlying
`PeerDisconnected` — `"clean_shutdown"` when the host announced itself, `"heartbeat timeout"`
when it died outright, so the UI can tell "the host closed" from "the host crashed". A host's own
backend never emits it (`host_peer_id` is `None` there), and neither does a joiner for any other
peer leaving.

New event name rather than widening the existing `player_left`: that one has no Unity consumer at
all today (`docs/ARCHITECTURE_RISK_REVIEW.md:146`) and the client does not know which peer id is
the host, so carrying this on it would mean widening it — which bumps just the same (precedent
v23, `inventory_restored`). An IPC-only addition counts for this counter with or without a new
struct (precedents v13 `report_noise`, v22 `set_identity`).

Degradation is total in both directions: a pre-v24 client receives an event name it has no
listener for and ignores it (the same shape every one-off event already has), and a pre-v24
backend simply never emits it, leaving today's behavior — the joiner stays in a frozen world
until the player quits manually. **No P2P change accompanies this bump**: the goodbye that makes
the common case immediate reuses `PacketPayload::Disconnect` (0x06), which has existed with a
complete receiver since the baseline commit.

### v25 (ADR-060)

Cambio solo-P2P (la superficie IPC no se toca; bumpea por la regla de ADR-047: añadir un
`PacketPayload` bumpea). Dos variantes nuevas para el goteo del snapshot de mundo:
`WorldSyncChunk { world_revision, data }` (0x36) y `WorldSyncEnd { world_revision, chunk_count }`
(0x37), que sustituyen al envío monolítico `WorldSync` (0x04) — un solo datagrama UDP con TODOS
los chunks, que muere en `WSAEMSGSIZE` al superar 65 507 B (~50–80 chunks) y antes de eso depende
de fragmentación IP. `WorldSync` queda deprecado: su decode se conserva esta versión, ningún
emisor queda, y el variant se retira en el siguiente bump.

Degradación: NINGUNA interop cross-versión — un peer v24 no decodifica 0x36/0x37 y un host v25 ya
no emite 0x04, así que un join mixto conecta pero nunca recibe mundo (el joiner queda pre-spawn,
visible, sin corromper estado). `#[serde(default)]` no puede salvar un variant entero que el otro
lado no conoce (mismo caso que ADR-028 Fase E, v8→v9). **OJO — verificado en esta sesión:** el
`version` del `Handshake` P2P se IGNORA en `handle_handshake` (`_version`), así que NO existe
gate que rechace el join mixto; el agujero es previo a este bump y este bump lo hace por primera
vez observable en juego. Cerrar el gate (rellenar `version` con `WIRE_SCHEMA_VERSION` y rechazar
en el host) queda anotado como corrección pendiente en ADR-060.

### v26 (ADR-061) — actual

Primer cambio que hace que este número **viaje**. Hasta aquí `WIRE_SCHEMA_VERSION` solo se
imprimía en el log de arranque: existía desde ADR-009 y ninguna de las dos partes lo comprobaba
jamás. Variante nueva `ServerMessage::Hello { schema_version: u32 }`, emitida como **primer frame
de cada conexión IPC** — escrita en `handle_connection` antes de spawnear `write_loop`, de modo
que precede a cualquier mensaje ya bufferizado en el `broadcast::Receiver` suscrito en `run()`.
Unity la compara por igualdad exacta contra `WireSchema.Expected` (`Assets/Scripts/Network/
WireSchema.cs`, la constante espejo que hay que bumpear en el mismo cambio).

Motivo: sin gate, un mismatch de esquema no fallaba — **degradaba a defaults silenciosos**. El
contrato `else r.Skip()` del decoder de Unity es correcto y aditivo por diseño, pero convierte
cualquier deriva en datos plausibles; el caso peor (`STABILITY_AUDIT_CURRENT.md` §R4, P1) es que
un fallo de parseo de `remote_players` sea byte a byte indistinguible de "no hay jugadores
remotos". El gate es de **envelope, no por campo**: el default silencioso por campo sigue siendo
el contrato y `IPCMessagesParityTests` lo sigue congelando.

Igualdad exacta y no un mínimo porque el despliegue es lockstep (un único exe en
`Builds/Backend/`, ADR-047): un backend más nuevo también es una build desincronizada.

Degradación en las dos direcciones, ambas benignas:
- **Cliente ≤v25 + backend v26:** el frame `hello` cae en la rama `default:` de `Dispatch` (log de
  warning, frame descartado). El resto de la sesión, intacta.
- **Cliente v26 + backend ≤v25:** no llega hello; el cliente loguea un warning una vez por
  conexión ("versión NO verificada") y sigue. Tolerancia deliberada, sin timeout — el conjunto de
  backends sin hello solo decrece.

Mismatch real ⇒ fallo duro: `LogError` + `session_ended` sintético con
`reason = "wire_schema_mismatch backend=vX client=vY"`, que reutiliza el teardown de ADR-056 (cero
UI nueva). Sigue **sin** cubrir el gate P2P: el `_version` ignorado en `handle_handshake` es otro
transporte y sigue siendo la corrección pendiente de ADR-060.

### v27 (ADR-060 commit d) — actual

Cambio solo-P2P: los CINCO rosters completos (`StpItemList` 0x16, `StpBuildingList` 0x1A,
`StpCarryableList` 0x40, `StpHarvestableList` 0x44, `CorpseList` 0x46) ganan tres campos de
paginación — `generation: u32`, `page: u16`, `page_count: u16` — y pasan a viajar troceados en
páginas de ≤1000 B de contenido en vez de un datagrama con la lista entera. Motivo: por encima
de 65 507 B el `send_to` fallaba con `WSAEMSGSIZE` y la replicación de ese roster se detenía
**permanentemente**, con el único rastro del warn 1/s de `send_datagram` — el mismo final que ese
doc-comment ya predecía para `StpBuildingList` (~800 piezas).

El receptor reensambla por generación (`network::roster::RosterAssembler`) y **solo aplica el
roster cuando la generación está completa**, conservando la semántica de reemplazo verbatim: una
página suelta no puede sustituir a la lista entera, porque aplicar media lista borraría la otra
mitad de los objetos del joiner. Una página perdida deja su generación incompleta y la ronda
siguiente (100 ms después) la sustituye entera — la misma autocuración que ADR-039 invocó para
dejar estos cinco fuera de `is_reliable`, y que este cambio conserva.

Degradación:
- **Emisor ≤v26 + receptor v27:** el roster llega sin los tres campos; `page_count` tiene
  `#[serde(default = "default_page_count")]` = **1** (no 0, que sería incoherente y haría que el
  roster no se aplicara nunca), así que decodifica exactamente como lo que era: una página única
  con la lista entera. Interoperable de verdad.
- **Emisor v27 + receptor ≤v26:** el receptor viejo ignora los campos nuevos y aplica CADA página
  como si fuera el roster completo ⇒ con más de una página se queda con la última. Degradación
  real, cubierta por el gate de envelope de v26 (ADR-061), que rechaza la sesión antes.

Techo práctico MEDIDO, documentado en `roster.rs`: 4 000 elementos (222 páginas) llegan enteros en
una sola ronda; hacia 20 000 (1 111 páginas) la ráfaga desborda el buffer de recepción y ninguna
generación completa. El monolito moría a ~2 200 elementos y de forma permanente. Cruzar el techo
nuevo pediría un rediseño a deltas, explícitamente fuera de ADR-060.

### v28 (ADR-063)

Caso distinto de los anteriores: **ningún payload cambia de forma.** Los ids de entidad/item que ya
viajan como `u32`/`uint` (`EntityView.id`, `ItemView.id`, `EntitySyncData.id`, `ItemSyncData.id` en
`ipc/mod.rs`; `target_id`/`item_id` de `Interact`/`WorldInteractRequest` en P2P) cambian el
**contrato de unicidad** del valor, no su tipo: antes, un id runtime era un contador de proceso
plano (arranca en 1/`0xF000_0000`, sin coordinación entre backends); ahora es
`(peer_id as u32) << 16 | contador`, particionado por quién lo acuñó
(`world::architecture::chunk_generator::partition_runtime_id`). Bumpea de todas formas, por la
regla dura #7 (cambio de API pública = ADR) — mismo patrón que el proyecto ya distingue de ADR-039
(que NO bumpeó porque cambió semántica de *transporte*, sin tocar el dato; esto cambia la semántica
del *dato* que ya cruza el wire).

Motivo: `NEXT_ENTITY_ID`/`NEXT_DROPPED_ID` (`chunk_generator.rs`, `world/mod.rs`) son contadores de
proceso — dos backends acuñando runtime ids independientemente producían colisiones garantizadas,
no probabilísticas. Hoy ambos acuñadores están gateados host-only (`game_loop.rs`, ADR-009 §4), así
que la colisión no es activa; el particionado es defensa a futuro si algún día el acuñado se
descentraliza. Ver `docs/DECISIONS.md` ADR-063 (enmienda de estado 2026-08-10) para la fórmula
completa, incluida la corrección de un split 8/24 erróneo del primer borrador que sí habría
truncado `peer_id` por encima de 255.

Ids estables (`stable_entity_id`/`stable_item_id`, hash `(seed, pos, index)`) intactos — el ADR
particiona solo la familia runtime.

Degradación: **ninguna interop cross-versión, y no hace falta ninguna.** El gate de handshake P2P
(corrección de ADR-060) y el `hello` IPC (ADR-061) — ambos ya activos desde antes de este bump —
rechazan cualquier mismatch de versión ANTES de que un id con significado nuevo cruce el wire hacia
un peer que no lo entiende. Un v27 y un v28 nunca completan la conexión entre sí; no hay escenario
de mezcla de formatos en producción.

### v29 (ADR-068) — actual

Pintadas de spray, en los DOS transportes.

**IPC.** `ClientMessage::SprayPlace(SprayPlaceRequest)` es variante nueva de nivel superior y no
una `PlayerAction`, por una razón dura: el `data` de `PlayerAction` es un `serde_json::Value` y
`Value` no tiene tipo de bytes, así que el blob `bin` con los puntos del trazo **no decodifica
ahí**. Las demás colocaciones (`stp_place`, `stp_drop`) son acciones precisamente porque su
payload es todo números. Vuelta: `ServerMessage::SprayPlaced(Spray)` (eco de la aceptada) y el
campo aditivo `GridChunkData.sprays`, omitido del wire cuando está vacío.

**P2P.** Tres opcodes nuevos, los tres fiables: `SprayPlaceRequest` **0x51** (joiner → host,
petición), `SprayPlaced` **0x52** (host → peers, ya aceptada) y `SprayChunkRequest` **0x53**
(joiner → host, "qué hay pintado en este chunk"). Una pintada perdida no se auto-cura como un
`NoiseReport` — nadie la reintenta y el jugador se queda mirando una pared que para los demás sí
está pintada.

0x53 existe porque el almacén de un joiner arranca VACÍO: la geometría cada peer la deriva del
seed, pero una pintada no es función del seed, así que quien se une a un mundo ya pintado tiene
que preguntar o ve paredes limpias. Se pregunta una vez por chunk (Unity vuelve a pedir el mismo
chunk en cada pasada de streaming) y el host responde con un 0x52 por pintada.

**Una pintada por paquete, NO un roster.** Es la diferencia con `StpBuildingList` y compañía: una
pintada mide ~1,9 KB, así que hasta un puñado reventaría el datagrama que ADR-060 (d) ya tuvo que
paginar para elementos mucho más ligeros. La hidratación en bloque viaja por `GridChunkData`, con
el chunk que el cliente ya pide, y no por un roster a 10 Hz.

`requester_id` de `SprayPlaceRequest` sale de la CABECERA del paquete, no del payload: el host mide
el alcance contra la posición que ya conoce de ESE peer, así que un cliente no puede reclamar estar
pintando desde el sitio de otro.

Degradación: **ninguna interop cross-versión, y no hace falta.** Igual que en v28, el gate de
handshake P2P (corrección de ADR-060) y el `hello` IPC (ADR-061) rechazan el mismatch antes de que
un opcode desconocido cruce. `WireSchema.Expected` (C#) bumpeado a 29 en el mismo commit — ADR-061:
desincronizarlos deja el juego inarrancable, no con un warning.

### v30 (ADR-069)

Añade la acción de cliente `bed_constructed { position: [f32;3] }` — IPC only, cliente → **su
propio** backend, nunca P2P. Sin struct de wire nuevo: `PlayerAction` ya es genérica
(`action_type` + `data` libre), exactamente la misma forma que `report_noise` (v13) y
`set_identity` (v22), ambas IPC-only y ambas contadas.

Qué la motiva: `stp_place` armaba `respawn_point` al plantar el **fantasma** de la cama, así que un
jugador tenía respawn sin gastar un material. El backend no puede detectarlo solo — los requisitos
de construcción viven en el prefab STP, del lado cliente — de ahí el mensaje.

Ninguna forma existente cambia. `PlayerInput`, `RemotePlayerState` y todos los `PacketPayload`
quedan byte-idénticos a v29; los goldens de C6 siguen válidos. El único cambio de datos es interno
al backend (`PlayerSnapshot.pending_respawn_point`, aditivo con `serde(default)`, ADR-032 punto 5),
que no cruza el wire.

Degradación si un cliente no la manda: el respawn simplemente no se arma nunca y el jugador
reaparece en el arranque fijo — la misma clase de degradación inerte que estableció `report_noise`.
`WireSchema.Expected` (C#) bumpeado a 30 en el mismo commit — ADR-061.

### v31 (ADR-070)

Tres adiciones, todas `serde(default)`, para que los objetos soltados caigan en vez de aparecer
posados y congelados:

- `StpItemInfo.settling: bool` (roster host→clientes, IPC **y** P2P). Dice al cliente que la
  `position` de ese ítem se MUEVE entre relays y hay que interpolarla en vez de clavarla. Ausente =
  `false` = el comportamiento de siempre, que es justo lo que quiere todo lo que ya existe (loot de
  chunk, cadáveres, cofres): nacen posados y no cuestan nada.
- `stp_drop.velocity: [f32;3]` (acción IPC cliente→backend). El impulso del lanzamiento. Y un
  cambio de SIGNIFICADO en un campo que ya existía, que importa más que el campo nuevo:
  `stp_drop.position` pasa a ser **la mano**, no un sitio ya pegado al suelo. El cliente dejó de
  rayear hacia abajo; dónde acaba el objeto lo decide el host.
- `PacketPayload::StpDropRequest.velocity: [f32;3]` (P2P, joiner→host). El mismo impulso, para que
  el drop de un joiner no se degrade a caída vertical al reenviarse. Cubierto por
  `stp_drop_request_carries_the_throw_velocity_across_the_wire`, con valor no-default en los tres
  ejes.

**NO se añade orientación.** La rotación del objeto mientras cae es cosmética y se queda en el
cliente (ADR-070 decisión 3): son tres floats por ítem en cada relay que no compran una sola regla
de juego. Dos clientes acabarán viendo la lata girada distinta y da igual — la recogida va por id.

Degradación: un backend viejo nunca marca `settling`, así que el cliente nuevo trata todo como
posado y se comporta como v30. Un cliente viejo omite `velocity` y el backend nuevo lo lee como
cero: el objeto cae recto desde la mano en vez de salir lanzado. Ninguna de las dos es un error.
`WireSchema.Expected` (C#) bumpeado a 31 en el mismo commit — ADR-061.

## v32 — ADR-072: el botín lleva las propiedades de instancia (2026-08-14)

Sin esto **morir REPARABA el equipo**: el botín del cadáver viajaba como `{item_id, quantity}`, así
que lootear el propio cuerpo devolvía la antorcha a valor de fábrica.

- `ItemStackView.props: Vec<ItemPropertyValue>` (roster host→cliente, dentro de
  `world_state.visible_corpses[].items`), y su gemelo autoritativo `CorpseStack.props` en el save y
  en el relay P2P de cadáveres. `ItemPropertyValue` es el **mismo tipo** que ya usa el inventario
  desde ADR-045 Fase 3 (`{id: i32, value: f64}`): un solo formato de propiedad en todo el proyecto.
- `report_death_loot.items[].props` y `spawn_world_chest.items[].props` (acciones IPC
  cliente→backend), opcionales — un cliente que no las mande produce el vector vacío de siempre.

**Las propiedades son POR STACK, no por unidad**, y eso es fidelidad exacta con STP y no una
carencia del wire: un `ItemStack` del vendor es UN `Item` más un contador. Ver la enmienda de
ADR-072, que lo cierra con los 12 assets del proyecto medidos.

**Coste MEDIDO, no estimado** (sonda `corpse_view_wire_cost`, `#[ignore]`, con el encoder real):
cadáver saturado de 64 stacks = 1836 B sin propiedades → 3116 B con 1 por stack (×1,70), 5676 B con
3, 12076 B con el tope de 8 (×6,58). Son **20 B por propiedad**, dominados por el nombre de campo
repetido en cada entrada — la misma mecánica que descuadró el presupuesto de ADR-068 por 2,6×. El
caso real es mucho menor: un cadáver normal trae ~20 stacks y casi ninguno con propiedades.
`MAX_PROPS_PER_STACK = 8` es higiene contra un reporte del cliente y **avisa por log al recortar**.

Degradación: campo aditivo con `serde(default)` en los dos lados. Un save anterior carga con
`props` vacías y sin migración (test `a_save_written_before_adr_072_loads_its_corpses_without_props`);
un peer o cliente de otra versión no existe como caso, porque el gate de ADR-061 es de igualdad
exacta. `WireSchema.Expected` (C#) bumpeado a 32 en el mismo commit — ADR-061, y desde `7532876`
hay un test de `cargo test` que lo comprueba.

**Pendiente (Fase 2 de ADR-072):** `stp_drop` sigue viajando como `def_id` + cantidad, así que lo
que cae al suelo —incluido el sobrante que devuelven la recogida con inventario lleno y el
restaurador— vuelve al mundo a valor de fábrica.

## v33 — ADR-076: la emboscada disfrazada aturde, no mata (2026-08-14)

`PhantomAttackGrant` (0x4D) gana el kind **5 = Knockdown**, sin cambio de layout — el mismo
precedente que v20 sentó para los kinds 3/4: `kind` siempre fue un `u8` con 3..255 de sobra, y
ADR-047 dejó ese sobrante por escrito a propósito. El stun (segundos) viaja en `damage` (mismo
truco que `GrabStart`), el empuje en `impulse` (mismo carril que `Knockback`). **Cero daño de
vida**: ni el host ni el joiner tocan `player.stats` en este kind.

**Por qué bumpea y no es opcional**: el brazo `_` del joiner trata un `kind` desconocido como Hit
y aplica `damage` como daño de vida — un joiner sin actualizar que reciba un derribo se comería
2,0 puntos de vida en silencio. Degradación NO silenciosa, el mismo criterio de bump que fijó v20.
El brazo `5 =>` va explícito, ANTES del `_`.

Evento IPC nuevo `phantom_knockdown {"seconds", "dx", "dz"}`, emitido por host y joiner con el
mismo nombre y forma (el cliente no necesita saber dónde corre). `WIRE_SCHEMA_VERSION` y
`WireSchema.Expected` (C#) a 33 en el mismo commit — el test de `cargo test` que compara ambos
como texto lo vigila.

Sin cambio en `PacketType`, `from_u16` ni `type_code`: ningún opcode nuevo, ningún campo nuevo.

## v34 — ADR-078: el trazo de spray se dibuja mientras se pinta (2026-08-16)

Opcode nuevo **`0x54 SprayDraft`**, y por eso bumpea: la regla de arriba fija que añadir un
`PacketPayload` cuenta, aditivo o no. Lleva un trozo de un trazo EN CURSO — `place_id`, `layer`,
ancla en mundo + `yaw` (que definen el plano), color, grosor, `first_index` y los puntos nuevos
como pares `i16` en milímetros sobre ese plano (4 B por punto).

**Deliberadamente fuera de `is_reliable`**, como el `NoiseReport` de 0x4E: son ~10 paquetes por
segundo mientras dura un trazo y no pueden ocupar la ventana de 32 huecos. Un borrador perdido no
se reintenta y no hace falta — la pintada autoritativa (`0x52`, fiable) llega entera al soltar y
sustituye lo dibujado.

Transporte calcado de la voz (ADR-046/050): el host reenvía con `send_unreliable_as(pintor, dest)`
y elige destinos por DISTANCIA al pintor (`spray_draft_destinations`, 40 m). El filtro vive en el
host porque un filtro en el receptor es un filtro que el receptor puede quitar. Un joiner
precomprueba que haya alguien cerca y manda solo al host.

Dos mensajes IPC nuevos: `ClientMessage::SprayDraft` (Unity → su backend) y
`ServerMessage::SprayDraft` (backend → Unity, con el `painter` añadido). Variantes propias y no
`PlayerAction` por lo mismo que `SprayPlace`: llevan blob binario, y `serde_json::Value` no tiene
tipo de bytes.

Degradación: un cliente o peer sin actualizar no decodifica `0x54` y no ve dibujarse nada — o sea,
el comportamiento de antes de este ADR. `WireSchema.Expected` (C#) a 34 en el mismo commit.

**Nada de esto es autoridad**: el borrador no entra en `SprayStore`, no se guarda, no cuenta para
el cap de 64 por chunk y no se valida contra ningún tope. Eso sigue siendo íntegramente `0x52`.

## v35 — ADR-079: el joiner ve al robapieles (`PeerInfo.relay_only`) (2026-08-17)

Campo aditivo **`relay_only: bool`** (`#[serde(default)]`) en `PeerInfo` — las entradas del
`PeerList`/`HandshakeAck`. El host marca con `true` sus fantasmas inyectados (ADR-016), que hasta
ahora se EXCLUÍAN del roster (H10) y por eso ningún joiner los registraba ni les aplicaba las
poses relayadas: el robapieles era invisible para todo cliente no-host desde siempre.

Contrato: en una entrada `relay_only` la `addr` es el placeholder `"0.0.0.0:0"` y el receptor NO
la usa — registra el peer con su propia addr inerte local (`127.0.0.1:1`) y TODA la superficie de
envío lo salta (`broadcast_destinations`, `broadcast_reliable`, `send_reliable`,
`send_reliable_queued`, `send_unreliable_to`, `send_prepared_unreliable`). Conocerlo sin poder
dirigirse a él: es la protección H10 (veneno de socket por datagramas a addr inerte) movida del
emisor al contrato del campo.

Degradación v34: decodifica `relay_only` ausente → false y adopta la entrada como peer real con
la addr placeholder — warns de envío troteados 1/s + ciclo evict/re-add del reliable (ADR-062).
NO silenciosa, y por eso bumpea (criterio v20/v33). `WireSchema.Expected` (C#) a 35 en el mismo
commit.

## v36 — ADR-081: territorio (`StpBuildingInfo.owner_id`) (2026-08-17)

Campo aditivo **`owner_id: u16`** (`#[serde(default)]`) en `StpBuildingInfo` — o sea, en el roster
`StpBuildingList` y en el `SaveFile`. Es el `PeerId` de quien colocó la pieza, tomado de la
CABECERA del paquete y nunca del payload (mismo criterio que `SprayPlaceRequest.requester_id` de
ADR-068).

Hoy solo se lee en la pieza del MARCADOR de territorio: **un claim no es una tabla aparte, es un
marcador plantado**, así que la propiedad de una zona se deriva de `stp_buildings` y se persiste,
replica y retira con su marcador sin estado propio. `StpPlaceRequest` NO cambia de forma: el
`requester_id` ya viajaba en la cabecera de todo paquete.

Degradación v35: un peer viejo decodifica `owner_id` ausente → 0, y 0 no es dueño de nada para
ninguna comprobación — o sea, sus marcadores existen pero no reclaman, y sus dueños no pueden
construir en su propio territorio. Silenciosamente equivocado, no un error visible, que es
exactamente el caso que el criterio v20/v33 manda bumpear. `WireSchema.Expected` (C#) a 36 en el
mismo commit.


## v36 -> v37 — `GridChunkData.build_room` (ADR-081 enmienda 5)

`GridChunkData` gana `build_room: Option<[u8; 3]>` — `[tile_x, tile_z, door_side]` de la HABITACIÓN
CONSTRUIBLE del chunk, en tiles de 5 m, u omitido si el chunk no tiene ninguna. Campo ADITIVO y
`skip_serializing_if`, mismo patrón que `room_zones` y `sprays`: un chunk sin sala pesa lo mismo que
antes de que este campo existiera.

**Por qué viaja en vez de re-derivarse en el cliente**, que es lo que se hace con todo lo demás que
sale del seed (los carteles de zona, la escalera de OFFICE): el emplazamiento sortea con `StdRng`
—ChaCha— y eso no se replica en C# sin reimplementar el generador entero. Se eligió mandar 3 bytes
antes que mantener dos generadores de aleatorios en fase, que es el tipo de espejo que se pudre en
silencio.

Degradación v36: un cliente viejo no ve la clave y no sabe dónde están las habitaciones. Como desde
esta enmienda la habitación es el ÚNICO sitio construible, ese cliente pinta el fantasma en rojo en
todas partes y no manda ni una colocación — no puede construir en absoluto, y además la sala se
renderiza con el material del pasillo y con luces de techo. Roto de forma visible, no silenciosa,
pero roto: por eso bumpea. `WireSchema.Expected` (C#) a 37 en el mismo commit.

> **Hueco v38–v41 sin registrar aquí.** El código (`WIRE_SCHEMA_VERSION`) avanzó a 41 en sesiones
> posteriores (salas multi-chunk ADR-084, altura de sala ADR-085) sin que este changelog se
> actualizara — deuda de proceso ajena a esta entrada, no corregida aquí para no mezclar
> conceptos. El código sigue siendo la autoridad (regla de cabecera de este documento).

## v42 — ADR-093: el estado del Level 4 y el cruce de puerta (2026-08-24)

Tres `PacketPayload` nuevos (**por qué bumpea**: "añadir un `PacketPayload` bumpea", regla de
cabecera de este documento — ninguno reusa un campo existente):

- **`Level4State`** (`0x57`, host→peers, self-healing a 10 Hz, **fuera** de `is_reliable` — mismo
  trato que `CorpseList`): `epoch`, `window_open`, `return_dest`. El único estado que un joiner
  necesita mirroriza; el resto (punto de entrada, rumbo de deriva, instante de apertura) es
  host-only y nunca sale del proceso.
- **`Level4DoorRequest`** (`0x58`, peer→host, **fiable**) / **`Level4DoorVerdict`** (`0x59`,
  host→requester, **fiable**): la pareja petición/veredicto de cruzar una puerta.
  `Level4DoorRequest` NO lleva `requester_id` en el payload — sale de la CABECERA del paquete,
  mismo criterio que `SprayPlaceRequest` (ADR-068) y `StpPlaceRequest` (ADR-081): el host resuelve
  el destino contra la posición YA CONOCIDA de ese peer, y el payload no puede votar quién cruza.

`0x55`/`0x56` se dejan libres a propósito: el borrador de ADR-094 (Facelings, propuesta
concurrente sin código) los cita textualmente para su propio par petición/veredicto. El código es
la autoridad (precedente ADR-046/047 en este mismo documento) — quien implemente primero se queda
el número, pero saltarse estos dos evita que la otra implementación copie un opcode ya tomado.

E2 de `docs/LEVEL4-ROADMAP.md` (ADR-093) entra **inerte**: el host procesa `Level4DoorRequest` de
verdad (abre la ventana, resuelve el destino con deriva proporcional al overstay), pero ninguna
puerta física existe todavía para mandarlo — eso es E3. Degradación: un peer viejo no decodifica
los tres opcodes nuevos y simplemente nunca ve región ni puertas, que es exactamente el estado
actual de todo el mundo. `WireSchema.Expected` (C#) a 42 en el mismo commit — el test de
`cargo test` (`7532876`) que compara ambos como texto lo vigila.

## v43 — ADR-094 E0: `species` en la pose (2026-08-24)

Un campo nuevo en `PacketPayload::PlayerUpdate`/`ipc::RemotePlayerState` (**por qué bumpea**: "campo
nuevo en la pose bumpea", regla de cabecera de este documento):

- **`species: u8`** (`#[serde(default)]` = 0). 0 = humano (y el robapieles disfrazado, ADR-016 —
  su indistinguibilidad queda intacta: su campo vale 0 igual que el de cualquier jugador); 1 =
  faceling adulto; 2 = faceling niño. Sellado por el driver que posea el peer, junto a `revealed`
  (regla `pose-relay-wire-rust.md`, nunca en `update_player_state`) — un jugador real nunca lo
  varía. El cliente elige modelo/animador/bancos de audio por este valor.

Entra **inerte**: ningún driver escribe todavía un valor distinto de 0 (ni `PhantomDriver`, que
mantiene su indistinguibilidad, ni ningún faceling — esos aún no existen en código). Degradación:
un peer viejo no decodifica el campo y decodifica 0 (humano), que es el único valor que circula
hoy — cero cambio visible hasta E1/E2. `WireSchema.Expected` (C#) a 43 en el mismo commit —
`the_csharp_mirror_declares_the_same_wire_schema_version` lo vigila.

## v46 — ADR-095 F2: el carril de WorldGen3 (2026-08-27)

Dos mensajes nuevos y dos campos en el saludo. **Ningún mensaje existente cambia** — es lo que
ADR-095 D3 quiere decir con "wire propio": WG3 no toca `GridChunkData` ni ninguna otra estructura de
WG2, para que el día del borrado sea borrar y no desenredar. (Que el bump ocurra igual es
inevitable: `ServerMessage` es un enum etiquetado sobre un solo socket, así que **añadir una
variante ya es un cambio de esquema**. Anotado en ADR-095 enmienda 2.)

- **`ClientMessage::RequestWg3Chunk { cx, cz }`** — pide el chunk de WG3. Variante propia y no un
  campo en `RequestChunk`, por la regla R4. **Sin `layer`, y no es un olvido**: con columnas de
  tramos (D2) la capa deja de existir como restricción de geometría, así que un chunk de WG3 es uno
  solo y cubre toda la altura.
- **`ServerMessage::Wg3Chunk(Wg3ChunkView)`** — `{ cx, cz, placements[] }`, donde cada colocación es
  `{ piece: u16, rotation: u8, origin_x_cm: i32, origin_z_cm: i32 }`. **Once bytes por pieza**, y
  ésa es la propiedad que hace barato el paradigma: el catálogo ya está en el build de las dos
  partes, así que por el cable solo va qué pieza, girada cómo y puesta dónde. El origen va en
  centímetros ENTEROS porque se compara entre dos procesos y tiene que coincidir bit a bit; un `f32`
  acumulado a lo largo de una cadena de piezas no lo garantiza. `placements` vacío es un resultado
  VÁLIDO —un chunk sin nada—, y el cliente tiene que saber distinguirlo de "aún no ha llegado".
- **`ServerHello.wg3_enabled: bool`** y **`ServerHello.wg3_manifest_digest: String`**, los dos con
  `serde(default, skip_serializing_if)`.

**El saludo sale byte a byte como en v45 mientras WG3 esté apagado**, que es el estado de toda
sesión de hoy. No hacía falta para la compatibilidad —el parser de `HelloMsg` en C# ya salta claves
desconocidas (`else r.Skip()`), así que un cliente viejo lee `schema_version` y reporta el desajuste
igual—, pero el saludo es lo ÚNICO que informa de un desajuste de versión (ADR-061) y es el peor
sitio del protocolo para añadir superficie por una bandera que hoy está apagada en todas partes. Lo
fija el test `the_hello_frame_is_unchanged_while_wg3_is_off`.

El digest no es decoración: cliente y servidor hornean el catálogo por separado, y si no coinciden,
la geometría que se dibuja y la que bloquea son de mundos distintos, **nada da error**, y el síntoma
es atravesar paredes que se ven. Comparar dos cadenas en el saludo lo convierte en un rechazo con
motivo.

Degradación: un backend con `BACKROOMS_WG3` sin poner responde `RequestWg3Chunk` con la lista vacía
en vez de callar — un cliente que pidió y no recibe nada no puede distinguir "aquí no hay nada" de
"no me han contestado todavía", y esperaría para siempre. `WireSchema.Expected` (C#) a 46 en el
mismo commit.
