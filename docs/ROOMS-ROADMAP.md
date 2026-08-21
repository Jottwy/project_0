# ROOMS-ROADMAP.md — plan de trabajo de las salas autoradas

> Escrito el 2026-08-20 al cerrar la sesión que implementó ADR-083 enmiendas 1 y 2.
> Pensado para **ejecutarse tal cual en la siguiente sesión**: cada ítem dice qué tocar, cómo
> verificarlo y si necesita ADR antes.
>
> Recorrido del sistema: [`systems/authored-rooms.md`](systems/authored-rooms.md).
> Las decisiones vinculantes: ADR-083, ADR-084 y ADR-085 con sus enmiendas, en
> [`DECISIONS.md`](DECISIONS.md).

---

## 0. Dónde estamos (verificado con el mundo corriendo, no supuesto)

Funciona de punta a punta: horneas una sala → el horneado reexporta `room_manifest.json` → el
backend lo carga al arrancar → reserva sitio, vacía, cierra con anillo y excava un pasillo por cada
abertura → manda `authored_rooms` por wire 41 → el cliente instancia el prefab y no pinta nada
encima.

Números medidos: **31 salas en 9 km², una cada ~539 m**. 27 tests propios en verde.

**Lo que NO hay que volver a tocar** porque ya está resuelto y probado:

| | |
|---|---|
| Emplazamiento | Puro por seed, idéntico en las dos representaciones del mundo |
| Margen | Macizo, sin un hueco — hay test |
| Anillo | `SealedWall`, `repair_connectivity` no lo perfora — hay test |
| Puertas | N por sala, un tile de ancho, alineadas al boquete real ya rotado — hay test |
| Alcanzabilidad | La sala se alcanza a pie tras reparar — hay test |
| Geometría duplicada | Resuelta: el cliente no pinta suelo/techo/paredes en tiles de sala |
| Desparejado pool↔manifiesto | Guarda ruidosa en cliente + digest en el handshake |

---

## 1. Bloque A — sin ADR, ejecutable directo

Ordenado por retorno. Los tres primeros suben la nota del mundo de ~2,7 a ~6-7 sin tocar
arquitectura; ver §3 para el porqué.

### A1. Varias salas por chunk — ✅ HECHO (2026-08-20, ADR-083 enmienda 3, wire 39)

`plan_authored_rooms` devuelve un `AuthoredRoomSet` (array fijo, `Copy`, tope 3). El wire pasa a
`authored_rooms: Vec<[u16;4]>` y `WireSchema.Expected` a 39 en el mismo commit.

**Lo que costó más de lo previsto, y hay que saberlo antes de tocar esto otra vez:** no basta con
que las reservas no se solapen. **Entre dos reservas va un tile de separación obligatorio**, porque
dos anillos `SealedWall` espalda contra espalda son infranqueables para `repair_connectivity` y la
sala nace sellada. Medido: 2 de 14 salas incomunicadas sin separación, 0 de 17 con ella.

**Consecuencia que manda sobre el contenido:** la regla de qué cabe es **`T₁ + T₂ ≤ 3` tiles**.
Dos salas de 2 × 2 NO conviven. 2+1 y 1+1 sí. Con el pool de hoy (una sala de 5 × 5) sigue saliendo
una y solo una: **esto es tubería y no cambia nada de lo que se ve hasta que haya salas de 1 × 1 y
2 × 2 horneadas.**

### A2. Selección por tipo de zona

Que la sala que sale case con la zona (oficina, almacén…). Hoy el sorteo ignora `zone_kind`.

- **Tocar:** el manifiesto gana un campo de etiquetas por sala (lo escribe el exportador desde el
  modelo o desde un campo nuevo de `RoomPool.RoomEntry`); `plan_authored_room` filtra candidatas
  por la zona, que ya se resuelve con `crate::world::zone_density::rules_for`.
- **Sin bump de wire:** el filtro es local al backend, el wire sigue mandando el índice.
- **Verificar:** una sala etiquetada solo para una zona nunca sale en otra, barriendo seeds.

### A3. Cadencia y agrupación

`AUTHORED_CHANCE = 0.01` da una cada 539 m. Con un sorteo secundario, a veces 2-3 salas en chunks
vecinos = un "complejo" sin necesidad de multi-chunk.

- **Tocar:** solo `authored_rooms.rs`. Es una constante y un hash extra.
- **Verificar:** la sonda `real_manifest_cadence` (ya existe, `--ignored`) da la cadencia real.

### A4. Tinte de zona en la sala — ✅ HECHO (2026-08-20)

`TintAuthoredRoom` **multiplica** el `_BaseColor` autorado por `zoneTint`, por SUBMALLA (una pieza
con dos materiales tiene dos colores base). Sustituirlo aplanaría a un color plano toda la paleta
hecha a mano. No consume tiradas del `rng` por tile.

### A5. Limpieza de deuda que dejó esta sesión

- ~~**Suelo doble en altura**~~ ✅ **HECHO (2026-08-20) — y no era suelo doble.** El punto 7 de la
  enmienda 1 ya hace que el bucle de tiles se salte los de la sala, así que el chunk no suela ahí.
  Lo que quedaba era un **escalón de 4 cm hacia abajo en cada puerta** (`RoomMeshBuilder` autora la
  cara pisable en `y = 0`, la losa del pasillo la tiene a `0,04`). Corregido subiendo la sala
  `PropFloorY` al INSTANCIARLA, no en el horneado: sube alineados suelo, paredes y proxy de
  colisión, sin rehornear un solo prefab. La nota de riesgo del ADR queda desfasada.
- ~~**Dedup de puertas**~~ ✅ **HECHO (2026-08-20).** Barrido cuadrado sobre array en pila. Test
  verificado desactivando el dedup: 72 celdas talladas contra 70 distintas.
- **Salas selladas viejas en el pool:** el exportador las descarta, así que no aparecen, pero
  ocupan sitio y confunden. Borrarlas a mano cuando el contenido nuevo esté validado.
- **Puerta automática en plantas curvas:** `EnsureDoorway` aproxima. Funciona (busca la faceta más
  cercana al tile del pasillo y activa `spanCorners`), pero en una planta muy irregular puede
  quedar descentrada.
- **Arneses temporales sin commitear** en `Assets/Editor/`: `ClaudeTriggerRunner.cs`,
  `ClaudeRoomCheck.cs`, `ClaudeRoomsUiProbe.cs`, `TileShapeTestSceneCreator.cs`. Decidir si se
  quedan o se borran; el runner de disparo es útil para automatizar el editor abierto.

---

## 2. Bloque B — necesita ADR nuevo antes de tocar código (regla dura 7)

### B1. Props y loot — **lo más serio de lo pendiente**

`RoomMarker` se hornea y **no lo lee nadie**. En cuanto un prop tenga contenido deja de ser
decorado y pasa a ser estado del mundo: qué hay dentro lo tira el servidor, si ya se saqueó es
estado persistente por chunk, y dos jugadores abriendo a la vez piden la guarda de "una petición en
vuelo" que ya hizo falta para los cadáveres. Ver §7.2 de `systems/authored-rooms.md`.

### B2. Salas multi-chunk — ✅ HECHO Y EJERCITADO (ADR-084 + enmiendas 1-3, wire 41, 2026-08-21)

Una sala de 50 m no cabe en un chunk (interior utilizable 18 celdas = 45 m). Rompe la invariante
que sostiene el worldgen entero: **un chunk se genera solo, sin mirar a los vecinos** — aunque eso
ya no era cierto: `stitch_edges` coordina la costura de dos chunks por clave canónica desde hace
mucho, y ADR-084 se apoya en ese precedente. Además obliga a sacar el prefab del ciclo de vida del
chunk, o descargar el chunk ancla con el jugador dentro borra media sala.

Los cinco trozos, todos commiteados y todos **inertes al entrar** -- cada uno se pudo verificar sin
mover el mundo ni un byte, y el mundo solo cambio al llegar el contenido:

| | | |
|---|---|---|
| **T1** `df251e15` | Coordenadas de sala con signo; el tallado recorta al chunk en vez de descartar | ✅ |
| **T2** `cfa4be1a` | Un chunk barre vecinos buscando salas ancladas fuera que asomen aquí | ✅ |
| **T3** `009eb733` | Cap a 2 × 2 chunks + retirada por orden canónico | ✅ |
| **T4** `8820b3a8` | Costura suprimida en los bordes que cubre la sala | ✅ |
| **T5** `85a11a77` + `ee471982` | Wire 40 → 41 con el chunk ancla; prefab fuera del root con refcount | ✅ |

**Y ya se ve en el mundo.** `room_2` (12 × 12 tiles = 60 m, 12 m de alto, cuatro vanos) está en el
pool desde el 2026-08-21: **504 de las 1165 salas de un radio de 10 km cruzan de chunk**, y cada una
ocupa las cuatro esquinas de un bloque de 2 × 2. Los cuatro chunks la sitúan en la misma coordenada
de mundo, y la densidad no se mueve — una cada ~520 m antes y después.

Se hornea con **Backrooms ▸ Create Multi-Chunk Room**, que copia la forma de `room_1` y solo cambia
la medida. Después hay que exportar el manifiesto SIEMPRE, y **reiniciar la sesión**: el backend lo
lee una vez, en un `OnceLock`.

`room_1` (32 × 32 tiles = 160 m) sigue sin colocarse y seguirá: pasa del cap de 2 × 2 chunks, que es
80 m. El exportador lo dice en cada exportación.

Coste medido (verificación (f) de ADR-084, en release, sonda `probe_neighbour_sweep_cost` — **lánzala
SOLA**, ver abajo): el barrido de T2 costaba `generate_chunk_layer` 26,2 → 29,6 µs, **+13 %**. T3 lo
deshace: un chunk pregunta solo a las cuatro anclas que pueden alcanzarlo y el ±2 se paga dentro de la
retirada, que solo corre para el 1 % de anclas con sala. Queda en **423 ns/chunk** de emplazamiento y
**27,4 µs** de generación — el 1,5 % del coste de un chunk.

Dos cosas que la implementación cerró y viven en **ADR-084 enmienda 2**: la retirada entre anclas es
de **profundidad 1** (una sala retirada sigue ocupando su sitio, porque lo contrario hace la
supervivencia recursiva sin fondo), y la ventana de sorteo extendida la usan **solo** las salas que no
caben en su chunk, que es lo que deja el mundo existente donde estaba.

**Trampa de las sondas:** `active_manifest()` es un `OnceLock` del PROCESO. Si `real_manifest_cadence`
o `dump_chunk` corren antes en la misma invocación de `cargo test`, `generate_chunk_layer` empieza a
tallar `room_0` dentro de lo que la sonda está midiendo y los números salen falseados (las
incomunicadas pasaban de 1 de 11 a 5 de 11). Las sondas ahora se caen con el comando correcto en el
mensaje.

**Regla de autorado que salió de medir esto:** una sala grande con UN SOLO vano nace incomunicada
**6 de 55 veces**; con cuatro, **0 de 58**. El backend excava un pasillo por abertura hasta el
laberinto, y si la única da a un sitio del que no se sale, la sala queda sellada. Se agrava con la
costura suprimida: una sala multi-chunk se come hasta cuatro aperturas y devuelve las puertas que
tenga. El exportador avisa por debajo de dos vanos si la sala no cabe en un chunk.

**Deuda abierta que NO es de este ADR:** con el manifiesto REAL la sonda da **0 incomunicadas de
58**, pero el manifiesto de prueba de 4 × 4 marca 2 de 59, y viene de antes. El único vano de esas
salas apunta a una `SealedRoom` estampada de Fase 4, el túnel muere contra su perímetro y
`repair_connectivity` no perfora `SealedWall`.

### B3. Altura por encima de una capa — ✅ HECHO (2026-08-21, ADR-085 + enmiendas 1 y 2, wire 40)

La altura viaja en el manifiesto (`height_meters`, que ya se autoraba y ya se horneaba en
`RoomPool.RoomEntry` sin que la leyera nadie), el backend talla la sala en las capas invadidas y el
cliente suprime ahí su geometría. `room_0` mide **12 m** y hasta ahora se veía cortada a 4.

Tres cosas que la redacción original de este punto daba por buenas y NO lo eran:

- *"Toca el contrato de capas, que ADR-026 tiene bloqueado"* — **falso**. Lo bloqueado son las partes
  1–2; la parte 3 lleva desbloqueada desde 2026-07-06 y dice que la Y del cliente es autoritativa.
  Esa frase mantuvo este punto aparcado sin motivo.
- *"Decidir si el agujero resultante en la capa 1 es un fallo o una entrada"* — **no hay agujero que
  decidir**. Las propias paredes de la sala cruzan el plano de esa capa y la cierran por su
  perímetro; quien camine por arriba choca contra el muro. Y si el autor abre un vano a esa altura,
  entonces es una entrada autorada.
- El prerrequisito de `LAYER_HEIGHT` que ADR-085 declaró bloqueante estaba **mal enunciado**: no eran
  dos valores de la misma constante, sino dos constantes de dos subsistemas. Ver ADR-085 enmienda 1.

Las capas invadidas son `1 ..= ceil(h / LH) − 1`, no `floor(h / LH)`: una losa justo a la altura del
techo no invade, **es** el techo. Con `floor` la sala de 4 m —el default de `heightMeters`— se habría
quedado sin techo. Cap: **12 m**, para que la capa más alta (la única que dibuja techo) nunca se
invada.

### B4. Colisión del interior

`RoomPool.collisionBoxes` se hornea y **no lo consume nadie**. Consecuencia viva: el robapieles no
atraviesa la sala ni entra por la pared, pero **sí atraviesa un pilar o un bloque de dentro**.
Aceptado en ADR-083 enmienda 1 punto 9; el consumidor natural es ADR-026 (movimiento
server-authoritative), hoy BLOQUEADO.

---

## 3. El techo del sistema, para no empujar en la dirección equivocada

Esto es un sistema de **MOBILIARIO, no de TRAZADO**. Tres cosas se lo impiden por diseño:

1. La reserva es un rectángulo con margen macizo — lo que lo hace seguro es también lo que hace que
   **siempre se lea como una caja dejada caer en el laberinto**.
2. Dos salas nunca comparten pared: el margen lo prohíbe. No hay distritos autorados.
3. La costura siempre es la misma: un túnel de un tile cruzando 5 m de macizo.

Si lo que se quiere es autorar **el trazado** (una planta de oficinas entera, no una sala suelta),
la palanca es otra: `backend/src/world/architecture/layout_grammars.rs` y los patrones macro
(`starter_cluster`, `hallway_chain`, `intersection`). Son dos sistemas distintos y no compiten: uno
amuebla, el otro traza.

---

## 4. Trampas que ya costaron tiempo — no repetirlas

1. **El horneado tiene DOS rutas.** `SaveGeneratedRoom` (procedimental, la de la herramienta) y
   `BakeRoom` (manual). Lo que se enganche a una hay que engancharlo a la otra: reexportar el
   manifiesto solo en una dejó pool y manifiesto desparejados y el mundo sin salas.
2. **El backend lee el manifiesto UNA vez**, en un `OnceLock`. Reexportar con la partida corriendo
   no hace nada: hay que reiniciar la sesión.
3. **El exe de release no se puede reconstruir con un backend vivo.** Salir del Play, y matar
   huérfanos: se acumulan (llegó a haber tres a la vez).
4. **Un vano más ancho que su propia pared se recorta hasta desaparecer.** En planta redonda cada
   faceta mide ~1,2 m. `spanCorners` es la cura.
5. **El `along` de un `WallHole` no basta para saber el tile.** Al girar la sala la puerta se muda
   de tile; hay que rotar la posición REAL del boquete. Cualquier cuenta local acierta solo a 0°.
6. **Una abertura con `baseY` de 30 cm es una puerta**, no una ventana. El umbral está en
   `MaxDoorStepHeight = 0.5`.
7. **El `rng` de `GridChunkBuilder` es POR TILE**, no por chunk: saltarse un tile entero es seguro,
   saltarse una draw dentro de un tile no.
8. **Una celda abierta NO es una celda comunicada.** La reserva de una sala tapia trozos de
   laberinto y deja al otro lado muñones ciegos de una o dos celdas que siguen estando "abiertos".
   Un túnel de puerta que pare ahí deja la sala sellada. Por eso el túnel mide contra
   `main_component`, y por eso entre dos reservas va un tile. Con UNA sala no se notaba (0 de 11);
   con varias, 2 de 14.
9. **Un test de propiedad sobre el primer chunk que aparezca no vale para esto.** Los tests de sala
   única pasaban en verde mientras el sistema colocaba salas selladas. Lo cazó
   `probe_unreachable_rooms` (`--ignored`), que CUENTA incomunicadas sobre un barrido de seeds.
   Cualquier cambio al emplazamiento o al tallado se mide con ella antes de darse por bueno.
10. **Verificar sin arrancar el juego** sale casi gratis: `dump_chunk` (dibuja el chunk en ASCII y
   el valor que viaja por el wire), `real_manifest_cadence` (cadencia y las 5 salas más cercanas al
   spawn) y `hunt_seed_with_room_near_spawn` (busca una seed con sala pegada al spawn). Los tres
   `--ignored`, en `authored_rooms.rs`.

---

## 5. Qué hacer primero si hay poco tiempo

**Nada de código: hornear salas.** El cuello no es la tubería, es que hay **una sola sala** en el
pool y sale siempre la misma. Es el 80 % del valor y cuesta cero líneas.

Dos medidas, y las dos hacen falta:

| Tamaño | Para qué |
|---|---|
| **1 × 1 y 2 × 2 tiles** (5 m y 10 m) | Las únicas de las que caben VARIAS en un chunk. Regla: dos salas conviven si `T₁ + T₂ ≤ 3`. Sin estas, A1 no se ve. |
| **hasta 6 × 6 tiles** (30 m) | Variedad. 30 m es lo que de verdad cabe dentro de un chunk — ver el cap abajo. |
| **de 7 × 7 a 16 × 16 tiles** (35–80 m) | Multi-chunk, ya funcionando. `room_2` es la primera; hacen falta más para que no salga siempre la misma. |

**Los caps reales son 6 × 6 y 16 × 16, no 7 × 7 y 17 × 17.** El origen del footprint se sortea en
tiles, así que un origen impar no existe, y a los tamaños de la cuenta ingenua el único sitio donde
cabría la reserva empieza en celda impar. Una sala de ese tamaño se hornea, se exporta, y el
emplazamiento la descarta sin decir nada (ADR-084 enmienda 3). Y **cualquier sala que no quepa en un
chunk necesita al menos dos vanos**, mejor uno por lado.

Cap de altura: **≤ 12 m** desde ADR-085 (2026-08-21) — ya no son 4. Una sala más alta que una capa
hace que las capas que invade dejen de pintarle encima, y eso ya funciona de punta a punta. Por
encima de 12 m el mundo se quedaría sin techo, así que ahí sí hay tope duro.

La tercera medida ya está hecha: `room_2` encendió el multi-chunk el 2026-08-21 y con ella el cap de
2 × 2 chunks, la retirada entre anclas, la costura suprimida y el prefab con refcount dejaron de ser
código dormido. Lo que queda de ella es **verla jugando**: que no haya costura visible en el suelo o
el techo a caballo de dos chunks, y que atravesarla a pie coincida con lo que dice la colisión — las
verificaciones (a) y (b) de ADR-084, las únicas que no se pueden medir sin arrancar el juego.
