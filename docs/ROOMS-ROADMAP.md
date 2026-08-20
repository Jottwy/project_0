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

### A1. Varias salas por chunk *(el que más sube la densidad percibida)*

Hoy `plan_authored_room` devuelve como mucho UNA. Con salas pequeñas caben varias sin solaparse.

- **Tocar:** `backend/src/world/grid_gen/authored_rooms.rs` — que devuelva `Vec`/array fijo de
  planes, con anti-solape entre reservas (ya existe `overlaps_build_room`, generalizarlo a
  reserva↔reserva). `stitching.rs` y `world/generator.rs` iteran. El wire pasa de
  `Option<[u16;4]>` a `Vec` ⇒ **bump de wire 38 → 39 y del espejo `WireSchema.Expected` en el
  MISMO commit**.
- **Cuidado:** `AuthoredRoomRegistry` guarda una sala por `(cx,cz)`; pasa a lista.
  `IsAuthoredRoomTile` ya recorre una lista, no cambia.
- **Verificar:** dos salas en un chunk sin reservas solapadas; ambas alcanzables; chunk sin salas
  sigue serializando igual que en wire 38.

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

### A4. Tinte de zona en la sala

La sala no recibe el tinte de su capa/zona, así que canta contra el laberinto.

- **Tocar:** `GridChunkBuilder.AuthoredRooms.cs` al instanciar; aplicar `zoneTint` a los renderers
  del prefab como ya hace `Paint(...)` para el resto.
- **Cuidado:** no consumir draws del `rng` por tile — ese `rng` decide el jitter del chunk.

### A5. Limpieza de deuda que dejó esta sesión

- **Suelo doble en altura:** la losa del chunk tiene su cara a `y = 0,04` y el suelo del prefab a
  `y = 0`, así que el jugador pisa la del chunk y el suelo autorado queda 4 cm por debajo. Anotado
  en ADR-083 enmienda 1, **sin verificar en playtest**. Si molesta: subir el suelo en el horneado,
  NO tocar el wire.
- **Dedup de puertas:** dos boquetes que caigan en el mismo tile excavan el mismo túnel dos veces.
  Inofensivo hoy, feo si alguien autora una pared llena de vanos.
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
8. **Verificar sin arrancar el juego** sale casi gratis: `dump_chunk` (dibuja el chunk en ASCII y
   el valor que viaja por el wire), `real_manifest_cadence` (cadencia y las 5 salas más cercanas al
   spawn) y `hunt_seed_with_room_near_spawn` (busca una seed con sala pegada al spawn). Los tres
   `--ignored`, en `authored_rooms.rs`.

---

## 5. Qué hacer primero si hay poco tiempo

**Nada de código: hornear 6-8 salas de ≤7×7 tiles y ≤4 m.** El cuello no es la tubería, es que hay
**una sola sala** en el pool y sale siempre la misma. Es el 80 % del valor y cuesta cero líneas.
