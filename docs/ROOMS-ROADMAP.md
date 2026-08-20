# ROOMS-ROADMAP.md — plan de trabajo de las salas autoradas

> Escrito el 2026-08-20 al cerrar la sesión que implementó ADR-083 enmiendas 1 y 2.
> Pensado para **ejecutarse tal cual en la siguiente sesión**: cada ítem dice qué tocar, cómo
> verificarlo y si necesita ADR antes.
>
> Recorrido del sistema: [`systems/authored-rooms.md`](systems/authored-rooms.md).
> Las decisiones vinculantes: ADR-083 y sus enmiendas 1 y 2 en [`DECISIONS.md`](DECISIONS.md).

---

## 0. Dónde estamos (verificado con el mundo corriendo, no supuesto)

Funciona de punta a punta: horneas una sala → el horneado reexporta `room_manifest.json` → el
backend lo carga al arrancar → reserva sitio, vacía, cierra con anillo y excava un pasillo por cada
abertura → manda `authored_room` por wire 38 → el cliente instancia el prefab y no pinta nada
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

### B2. Salas multi-chunk

Una sala de 50 m no cabe en un chunk (interior utilizable 18 celdas = 45 m). Rompe la invariante
que sostiene el worldgen entero: **un chunk se genera solo, sin mirar a los vecinos**. Además obliga
a sacar el prefab del ciclo de vida del chunk, o descargar el chunk ancla con el jugador dentro
borra media sala.

### B3. Altura por encima de una capa

`LayerHeight = 4 m` y el cliente construye SIEMPRE las 4 capas (`BuildDesiredSet`). Una sala más
alta se come el suelo de la capa 1. Para permitirlo: la altura viaja en manifiesto y wire, el
backend reserva el footprint en las capas invadidas, y el cliente suprime su geometría ahí. Toca el
contrato de capas, que ADR-026 tiene bloqueado. Y arrastra decidir si el agujero resultante en la
capa 1 es un fallo o una entrada.

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
| **hasta 7 × 7 tiles** (35 m) | Variedad. 35 m es el techo físico del chunk; por encima es multi-chunk (B2, con ADR). |

Cap de altura en las dos: **≤ 4 m** (`LayerHeight`).
