# ADR-047 — El robapieles hiere a quien ataca, y oye a quien dispara

> **ESTADO: APPENDEADO A `../DECISIONS.md` (2026-08-02) — este archivo es la FORMA LARGA, no la ley.**
> La entrada canónica vive en `docs/DECISIONS.md` (línea 1654, `## ADR-047 — …`) y es la
> que manda. Este documento se conserva porque lleva el diagnóstico con números de línea
> y los bloques de código del payload, que la entrada del registro comprime.
>
> El append se hizo con Edit anclado al final del archivo según la regla dura #11, una vez
> la sesión paralela de ADR-045/046 commiteó: `docs/DECISIONS.md` 1652 → 1688 líneas,
> +36 solo inserciones, verificado antes y después.
>
> - Fecha de redacción: 2026-08-02 · appendeado el 2026-08-02
> - Estado real: IMPLEMENTADA (`2a007c8`, `7171559`), **PENDIENTE DE PLAYTEST A 2 JUGADORES**
> - Depende de: ADR-009, ADR-016, ADR-025, ADR-029, ADR-039, ADR-041, ADR-042, ADR-043
> - Enmienda de forma acotada: ADR-041 (punto 1), ADR-043 (deuda de daño), ADR-016 (alcance de la autoridad)
> - `WIRE_SCHEMA_VERSION` 15 → 16 (ADR-046 leyó la constante después y tomó el 17)

## Contexto

Joel reporta dos defectos en partida a dos jugadores:

1. *"Los NPCs, si hacen daño a un player que no sea host, le hacen daño al host, no al player que debería atacar."*
2. *"Si un player dispara, los spawneados no siguen el sonido de la bala."*

La auditoría (24 agentes, 14 hallazgos confirmados con verificación adversarial)
demuestra que **son el mismo agujero estructural**: el robapieles se simula
exclusivamente en el backend del host (`game_loop.rs:812`, `if net.is_host`), y ese
backend no tiene **ningún carril** ni de salida hacia el jugador correcto ni de
entrada desde un joiner.

### Defecto 1 — el daño se descarga sobre quien no toca

`nearest_real_target` (`game_loop.rs:4116-4139`) elige el objetivo real más cercano
e **incluye a los peers remotos**. Toda la percepción se evalúa correctamente contra
ese objetivo: cono de visión, sonido por velocidad, `crouch`, la distancia `< 1.5`
del golpe y el `player_is_looking_at` que decide golpe frontal contra muerte por la
espalda.

Y entonces el id del objetivo **se descarta explícitamente**, una línea antes de
construir el ataque:

```rust
game_loop.rs:5242   let (_, tpos, dist, tyaw) = target.unwrap();   // STATUE — tid al vacío
game_loop.rs:5309   let (_, tpos, dist, tyaw) = target.unwrap();   // SPRINT — tid al vacío
game_loop.rs:4229   enum PhantomAttack { Hit(f32), Kill, Knockback(f32, f32) }   // sin víctima
```

El consumidor (`game_loop.rs:860-894`) no tiene sobre qué bifurcar, así que aplica
todo al jugador local del backend host. En partida: la criatura persigue, acecha y
golpea al joiner, y los 35 de daño —o la muerte— los paga el host, que puede estar a
cien metros y sin nada delante.

La deuda anotada (`game_loop.rs:824-826`, ADR-043, `STATE.md`) decía *"el fantasma no
puede herir a un joiner"*. La realidad es peor y de otra forma: **hiere a un tercero**.

Agravantes que no estaban anotados:

- `if player.stats.is_dead() { break }` (`game_loop.rs:861`) rompe el bucle **entero**:
  con el host muerto, ningún golpe dirigido a un joiner llegaría nunca, ni siquiera
  con el carril nuevo, si la guarda no se acota a la víctima local.
- `DEV_INVINCIBLE` omite el `take_damage` pero **no** el envío del `GameEvent`: la UI
  del host reacciona igual. Es un flag local a cada backend y debe seguir siéndolo.

### Defecto 2 — el disparo del joiner no existe para nadie

`report_noise` es una acción **IPC local**: el cliente la manda a su propio backend
(`IPCClient.cs:597` → `game_loop.rs:1964` → `net.pending_noises`). El único drenaje
es `hear_noises`, y solo corre dentro de `PhantomDriver::step`, bajo el `if net.is_host`.

En el backend de un joiner nadie drena ese vector: el disparo muere ahí y además
**fuga memoria** (un elemento por disparo, toda la sesión). No existe ningún
`PacketPayload` de ruido en el árbol.

`fire_seq` (ADR-042) sí cruza P2P, pero es cosmético por diseño y **no puede** usarse
como estímulo: `NoiseReporter.cs:95` lo incrementa **antes** del gate de sonoridad, de
modo que un arma silenciada invocaría a la criatura. ADR-042 lo prohíbe por escrito.

Consecuencia de juego: disparar solo tiene coste para el host. Para todos los demás
jugadores, el sigilo que ADR-040/041 diseñaron es gratis.

### Tres defectos colaterales, destapados por la misma auditoría

3. **Los 500 m de ADR-041 son inalcanzables.** Solo pueden oír los fantasmas **ya
   simulados**, y `sync_population` únicamente despierta a los que están dentro de
   `PHANTOM_ACTIVATE_RADIUS = 150` de un jugador. Más allá, la criatura ni siquiera
   existe como entidad (ADR-043 la deja como un hash sin materializar). El viaje de
   ~2,8 minutos que ADR-041 describe como el núcleo de su tensión **no puede ocurrir**.
   Es una contradicción ADR-041 ↔ ADR-043 que ninguno de los dos anotó.
4. **El arco emite los mismos 500 m que el rifle.** ADR-041 §2 sitúa la tabla de armas
   en el cliente precisamente para que no se duplique en Rust; esa tabla nunca se
   escribió: `NoiseReporter` tiene un único `_firearmLoudness` para todo.
5. **El ruido atraviesa pisos.** `hear_noises` compara distancia XZ y no la capa, así
   que un disparo en la capa 0 alerta a un robapieles de otra.

## Decisión

El patrón ya está validado en este repo por ADR-029 y **obligado** por ADR-025
(*"la salud sigue siendo autoritativa POR BACKEND propio"*): **el host decide, el
backend de la víctima aplica**. Ningún backend escribe la salud de otro.

### D1 — La víctima es obligatoria en el TIPO, no un dato adjunto

`enum PhantomAttack` pasa a `struct PhantomAttack { victim: PeerId, kind: PhantomAttackKind }`.

Esto es el blindaje, no un detalle de estilo: los tres `push` no compilan sin nombrar
víctima, y los dos `let (_, tpos, …)` que hoy tiran el `tid` dejan de compilar. **El
compilador es el primer test.** Un tipo que no puede representar el estado defectuoso
es más fuerte que cualquier guarda que alguien pueda quitar.

El consumidor deja de **aplicar** y pasa a **enrutar**:

- `victim == net.local_id` → exactamente el código de hoy (dev flags, `take_damage`,
  los tres `GameEvent` IPC).
- `victim != net.local_id` → el host emite `PhantomAttackGrant` al backend de la víctima.
- **Víctima sin canal** (el peer desapareció entre la elección de objetivo y el golpe)
  → `warn!` explícito y **descarte**. Jamás se desvía al jugador local. Ese desvío es
  literalmente el bug que este ADR cierra; reintroducirlo como "fallback" sería
  reproducirlo bajo otro nombre.
- La guarda de muerte se acota a la víctima local. Hoy un host muerto se traga el golpe
  dirigido a un joiner vivo.

### D2 — `PhantomAttackGrant = 0x4D` (host → backend de la víctima, FIABLE)

```rust
PhantomAttackGrant {
    request_id: u64,   // monotónico del host; clave de dedupe por sí sola
    victim_id: u32,
    kind: u8,          // 0 = hit, 1 = kill, 2 = knockback
    damage: f32,
    impulse: [f32; 2], // dx, dz — solo para kind = 2
}
```

**NO lleva el id del fantasma.** El invariante duro de ADR-016 §1 —la marca de
"esto es un fantasma" nunca cruza el wire— se preserva íntegro, y no se pierde nada:
`PhantomAttackHandler.cs:115-137` no lee atacante en ninguna de sus tres ramas. Un
campo sin consumidor que debilita un invariante duro es un mal negocio.

**Fiable**, y por tanto entra en `is_reliable` — el invariante "si y solo si" de
ADR-039 se rompería en silencio si no (su único uso es decidir si el receptor emite el
ACK; sin él, el emisor reintenta 6 veces y al agotar `MAX_RETRIES` la cola fiable
entera del peer se purga, llevándose pickups, corpses y PvP por delante). El test
`GAMEPLAY_REQUEST_FAMILY` pasa de 16 a 17 códigos.

En el lado víctima: rechazo si `victim_id != net.local_id` (patrón exacto de
`game_loop.rs:1609-1615`), dedupe por `request_id`, re-chequeo defensivo de
`invuln_until_tick` e `is_dead()`, y solo entonces `take_damage` + los **mismos**
`GameEvent` que su propio Unity ya consume.

**Cliente: cero C# nuevo.** `PhantomAttackHandler` se auto-arranca por
`RuntimeInitializeOnLoadMethod` y resuelve siempre al jugador local, así que funciona
tal cual dentro de un proceso joiner.

### D3 — `NoiseReport = 0x4E` (joiner → host, NO FIABLE)

```rust
NoiseReport { position: [f32; 3], loudness: f32 }
```

Sin id de jugador, por ADR-041 punto 1: lo que viaja es **un ruido en un sitio**, no
la posición de un jugador. Esa distinción es el diseño entero de ADR-041.

**No fiable, deliberadamente.** Un estímulo transitorio no merece ocupar la ventana
fiable de 32: con un arma automática la llenaría, y al agotar `MAX_RETRIES` el barrido
purga la cola entera del peer. Un ruido perdido se autocura con el disparo siguiente.
Al no ser fiable, **no** entra en `is_reliable` y el invariante "si y solo si" de
ADR-039 sigue exacto.

**Una sola puerta de saneado.** La validación inline del brazo IPC se extrae a
`sanitize_noise(pos, loudness) -> Option<([f32;3], f32)>` y la usan **las dos**
entradas (IPC local y 0x4E). Dos validaciones paralelas divergen; una sola, probada
desde ambos lados, no puede.

Esto además mata la fuga de memoria **por construcción**: el joiner deja de empujar a
un vector que nadie drena, en vez de drenarlo artificialmente.

### D4 — Enmienda acotada a ADR-041 punto 1

ADR-041 dice literalmente que `report_noise` *"NO viaja por P2P — es host-authoritative
como todo lo del fantasma"*. Se acota esa frase a su intención real: **el fantasma
sigue siendo 100 % host-autoritativo** —nadie más lo simula, nadie más decide sus
estados ni sus ataques—. Lo que se abre es un carril de **estímulo entrante**, que es
justo lo que hacía falta para que la frase "un disparo tiene coste" fuese cierta para
todos los jugadores y no solo para el host.

### D5 — El ruido despierta población (cierra la contradicción ADR-041 ↔ ADR-043)

Un ruido pasa a ser un **centro de activación** además de un estímulo:
`sync_population` mira `net.pending_noises` **sin drenarlo** (lo drena `hear_noises`
dentro de `step`, que corre justo después — el orden ya existe y ADR-043 lo eligió a
propósito), y despierta hasta `PHANTOM_NOISE_ACTIVATE_MAX = 2` robapieles por ruido,
los más cercanos al origen, **siempre sujetos al `PHANTOM_ACTIVE_CAP` global**.

Los dos topes son la parte importante. Sin ellos, un disparo de rifle con radio 500 m
barre un área de ~785.000 m² y despierta todo lo que haya dentro, que es exactamente
el presupuesto que ADR-043 midió y protegió. Con ellos, el coste máximo de un disparo
es acotado y conocido.

La retirada no necesita cambios: la regla de ADR-043 "solo se retira en `Wander`" ya
protege a una criatura que viaja, porque viaja en `Search`.

### D6 — Tabla de sonoridad por arma (cliente)

`NoiseReporter` pasa de un `_firearmLoudness` único a una tabla autorada
`{ nombre de arma → metros }` con un valor por defecto. Se resuelve por el nombre del
wieldable del **trigger que disparó**, así que es exacta respecto al arma que sonó y no
respecto a lo que el inventario cree que hay equipado. La tabla sigue en el cliente,
como ADR-041 §2 exige.

### D7 — El ruido respeta la capa

`hear_noises` compara la capa del origen contra la del oyente
(`world_pos_to_layer`) y descarta lo que venga de otro piso.

## Alternativas rechazadas

- **Derivar el estímulo de `fire_seq` (cero wire).** ILEGAL: ADR-042 lo prohíbe por
  escrito, y es incorrecto además — `NoiseReporter.cs:95` incrementa el contador
  **antes** del gate de sonoridad, así que un arma silenciada llamaría a la criatura.
- **Salud server-única-autoridad.** ADR-025 la evaluó y la RECHAZÓ por escrito. No es
  una mejora libre que se pueda colar en un arreglo de sesión.
- **Que el host escriba la salud del joiner.** Imposible, no solo ilegal: el host no
  tiene esa salud (ADR-025, multi-backend).
- **`NoiseReport` fiable.** Ver D3: llena la ventana y el purgado se lleva por delante
  paquetes de otras familias.
- **Llevar `phantom_id` en el grant.** Debilita el invariante duro de ADR-016 §1 a
  cambio de un campo que ningún consumidor lee.
- **Arreglar solo el enrutado y dejar al joiner inmune** (entrega parcial sin wire).
  Descartada explícitamente por Joel: abriría una ventana en la que el robapieles no
  hiere a nadie mientras persigue a un joiner, y el próximo playtest lo leería como
  regresión.

## Invariantes que quedan fijados con test

1. Un ataque cuya víctima es un peer **jamás** toca la salud local (test centinela).
2. Un ataque cuya víctima es el jugador local sigue comportándose exactamente como hoy
   (**centinela del camino bueno** — sin esta mitad, un arreglo que rompa el caso host
   pasa verde).
3. Un host muerto no se traga el golpe dirigido a un joiner vivo.
4. Una víctima sin canal produce `warn!` y descarte, nunca daño desviado.
5. `is_reliable(0x4D) == true` y `is_reliable(0x4E) == false`.
6. `PacketType::from_u16(0x50).is_none()` — centinela que deja libre el opcode que
   ADR-046 reserva para la voz.
7. Round-trips de ambos payloads con valores **no por defecto** (un round-trip de ceros
   pasa con los campos intercambiados).
8. `sanitize_noise` rechaza no-finitos, sonoridad ≤ 0 y clampa al techo, probada desde
   las dos puertas.

## Bump de esquema

`WIRE_SCHEMA_VERSION` 15 → 16.

**El registro se contradice y este ADR lo resuelve por escrito en vez de elegir el
precedente que le conviene:** ADR-039 dice que ese contador *"versiona el schema IPC
cliente↔backend"*, pero ADR-039 **no añadía payload**. ADR-028 Fase E sí, y bumpeó
v8→v9 por cuatro variantes P2P con el IPC intacto. **Regla que queda escrita: añadir
un `PacketPayload` bumpea.** Es el caso de ADR-028, no el de ADR-039.

Coordinación con ADR-046 (voz), que declaró "hoy 15→16": **manda el código, no el
ADR.** Quien aterrice segundo lee la constante y toma el siguiente. Hay precedente
documentado de dos sesiones pisándose el número.

## Riesgo declarado: sesión de versiones mixtas

`Handshake.version` es hoy un literal que nadie comprueba: no hay gate de versión P2P.
Un backend v15 que reciba 0x4D no lo decodifica, no ACKea, y tras `MAX_RETRIES` el
barrido purga la cola fiable de ese peer, llevándose pickups/corpses/PvP.

**Mitigación declarada: despliegue lockstep.** Hay un único exe en `Builds/Backend/`
y el cliente lo lanza; no existe hoy un escenario real de dos versiones distintas
salvo que alguien copie un binario viejo a mano. Se acepta y se anota; una negociación
de capacidad sería un ADR propio.

## Deuda que este ADR NO cierra

- **Rate-limit por peer en 0x4E.** ADR-041 ya aceptó el nivel de confianza "un ruido
  forjado solo pasea al fantasma" para el host; ahora aplica a todos. Un joiner
  modificado puede pasear robapieles por el mundo compartido. Los topes de D5 acotan
  el coste de CPU, no el abuso. Deuda declarada, no resuelta.
- **Playtest a 2 jugadores.** `PhantomAttackHandler.StartDeath` y `ApplyKnockback`
  **nunca** se han ejecutado en un proceso joiner, y su respawn pasa por
  `RespawnRequester`/ADR-027. Hasta ese playtest, el defecto 1 no se declara cerrado.
