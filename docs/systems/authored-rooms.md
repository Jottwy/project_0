# Salas autoradas y props — cómo funciona hoy, y qué le toca decidir al backend

> Carpetas del subsistema: `Assets/Scripts/Gameplay/GridWorld/` (modelo, malla, colliders, colocación) + `Assets/Editor/RoomAuthoringWindow*.cs` (la herramienta) + `Assets/Tests/EditMode/RoomMeshTests.cs` (131 tests).

Este documento existe por una razón concreta: **hoy no hay nada de este sistema que sea autoritativo en el servidor**, y varias piezas están escritas para que lo sea. Antes de conectar el backend hay que saber qué se guarda ya, qué viaja por el wire, qué se re-deriva en el cliente y dónde están las trampas. Sin eso, la salida fácil es que el cliente le diga al servidor qué hay en el mundo, que es exactamente lo que no puede pasar con loot.

No es un ADR. Las decisiones vinculantes están en [`../DECISIONS.md`](../DECISIONS.md); lo que aquí se marca como PENDIENTE necesita ADR antes de tocar código (regla dura 7).

---

## 1. Qué es una sala autorada

Una pieza de geometría hecha a mano que el mundo procedural **coloca dentro de una zona que ya existía**. No sustituye al generador: lo amuebla.

Esto es lo primero que hay que tener claro, porque decide de quién es cada cosa:

| Lo pone | Qué |
|---|---|
| **Backend** (`grid_gen`) | La zona, su perímetro, dónde están las aperturas. Es la autoridad de lo caminable. |
| **Cliente** (`GridChunkBuilder`) | Suelo, techo y paredes del perímetro, desde el bitmask que manda el backend. |
| **Sala autorada** | Solo el INTERIOR: columnas, bloques, escaleras, entreplantas, aberturas interiores, marcadores. |

Sustituir el perímetro sería la Fase 3 y pide su propia decisión: contradiría lo que Rust cree que es caminable.

---

## 2. El recorrido completo

```
RoomDefinition  ──►  RoomMeshBuilder   ──►  una malla
   (modelo)     └─►  RoomColliderBuilder ─►  lista de cajas orientadas
                          │
                          ▼
                   Bake (herramienta)
                          │
        ┌─────────────────┼──────────────────┐
        ▼                 ▼                  ▼
   room_N.prefab    room_N_mesh.asset    RoomPool.asset
   (+BoxColliders)                        (catálogo)
        │                                     │
        └──────────────► GridChunkBuilder.AuthoredRooms ◄──┘
                                  │
                          instancia en el chunk
```

**El punto que no se puede perder de vista:** malla y colliders salen del **mismo modelo**, nunca uno de la otra. Es lo que impide el fallo de "veo una puerta y me choco contra ella". Cualquier cosa que se añada tiene que resolverse en `RoomDefinition` y que los dos generadores lean de ahí — hay helpers compartidos justo para eso (`ResolveHoles`, `HoleRect.InsideAny`, `StairsReaching`, `PitsThroughSlab`).

---

## 3. El modelo (`RoomDefinition`)

Un `[Serializable]` plano. Se clona por JSON (así lo carga la herramienta desde el pool), así que **todo campo nuevo tiene que sobrevivir un round-trip** — hay un test que lo comprueba, y un campo olvidado no da error de compilación, solo un dato perdido en silencio.

Piezas:

- **Planta** — `tilesX`/`tilesZ` en tiles de 5 m, y un `planMode`: `Polygon` (convexa, de redonda a rectangular), `Blocks` (muerde tiles para L/T/U) o `Manual` (contorno a mano).
- **Aberturas** (`WallHole`) — puertas, ventanas y rejas en el perímetro. Pueden doblar esquina (`spanCorners`).
- **Pozos** (`FloorHole`) — agujeros en el suelo, con fondo o sin él.
- **Sólidos** — `Pillar`, `PillarGrid`, `Block` (con sus propios boquetes).
- **Verticalidad** — `Level` (entreplanta del ancho de la sala) y `Stairs`.
- **Marcadores** (`Marker`) — sitios sin geometría: luz, prop, aparición.

### El sistema de PISOS, que es el que ordena todo lo demás

Cada feature lleva un `level`. `StoreyBaseY(level, minCeil)` traduce eso a metros, y **todas las alturas del feature se miden desde ahí**. Consecuencia: mover una losa arrastra consigo todo lo que vive en ese piso, sin tocar nada más.

Los pisos se ordenan **por altura, no por posición en el array**. Un feature con un `level` mayor que el número de losas cae al último piso (mismo `Clamp` en el modelo y en la herramienta).

Dos casos que no se adivinan y hay que saber:

- Una `WallHole` con piso solo mueve el ORIGEN de su `baseY`; el recorte por arriba lo sigue poniendo el techo de la sala, porque la pared es una superficie continua que no se parte por pisos.
- Un `FloorHole` con piso ≥ 1 **cambia de naturaleza**: deja de ser una caja colgando y pasa a ser un hueco recto que atraviesa la losa, igual que el que abre una escalera. `depth` y `bottomless` dejan de aplicar.

---

## 4. El proxy de colisión — autorado, guardado y HOY SIN LEER

`RoomColliderBuilder` deriva del modelo una lista de **cajas orientadas** (`RoomPool.CollisionBox`: centro, tamaño, yaw). Sale del modelo y no de la malla a propósito: sabiendo que un lado es "un trozo recto de 3 m con una puerta en medio" salen tres cajas exactas, mientras que deducirlo de triángulos sería adivinar — y la malla lleva detalle visual que no debe costar colisión.

Una caja es **el mismo volumen exacto en Unity y en Rust**. Esa es la razón de que sea una caja y no una malla: dos motores de física distintos resolviendo la misma malla pueden discrepar, y ahí aparece el rubber-banding.

**Estado real hoy, sin adornos:**

- El campo `RoomPool.RoomEntry.collisionBoxes` se **escribe** al hornear y **no lo lee nadie**. Ni el cliente.
- Lo que bloquea al jugador local son los `BoxCollider` de verdad que la herramienta mete dentro del prefab, resueltos por PhysX **en cliente**.
- El backend no sabe que la sala existe. El robapieles la **atraviesa entera** (colisiona contra celdas de `grid_gen`, 2,5 m).

El registro de esas cajas en el backend es **ADR-083, PENDIENTE** (wire 37 → 38). Ver §7.

---

## 5. Cómo se coloca una sala en el mundo

`GridChunkBuilder.AuthoredRooms.cs`, y es **puro**: no toca el `rng` por tile, no instancia nada al decidir.

1. Solo se consideran zonas `RoomZoneKind.SealedRoom` — las que el backend talla con perímetro cerrado y aperturas conocidas.
2. El rect tiene que caer en frontera de tile (las celdas del backend son de 2,5 m; los tiles, de 5 m). Si no, se descarta y el generador lo llena como siempre.
3. Se descarta si la escalera de oficina ya reservó ese tile.
4. `ApertureSides` mira por dónde se entra de verdad, leyendo el bitmask del backend. **Si el lado cae fuera del chunk, NO se declara abierto**: el vecino no ha llegado y afirmar una apertura que no existe pondría la puerta contra un muro.
5. Candidatas: (entrada del pool, giro) cuyo footprint case con el rect **y** cuya puerta mire a un lado abierto de verdad. La tabla `(entrada, giro) → (footprint, lado)` se cachea por pool.
6. Se elige con `Hash01(gx, gz, RoomSaltPick)` sobre coordenadas de tile **globales**.

**Por qué un hash y no `Random.Range`:** dos clientes con la misma seed tienen que colocar la MISMA sala con el MISMO giro sin que viaje nada por el wire. Un `Random` aquí es un desync visual entre jugadores.

**Trampa registrada:** el orden de enumeración de las variantes es parte del contrato. La elección es un hash sobre el índice dentro de la lista de candidatas — reordenar cambia qué sala sale en cada sitio del mundo, y dos clientes con versiones distintas verían salas distintas.

---

## 6. Props y marcadores

### Qué se hornea

| Marcador | Se convierte en |
|---|---|
| `Light` | Un `Light` de Unity real. Ya está listo al instanciar la sala. |
| `Prop` | Un `RoomMarker { kind, label }` — solo el sitio y la etiqueta. |
| `Spawn` | Igual: `RoomMarker`. |

**`RoomMarker` no lo lee NADIE.** Es un punto con etiqueta esperando dueño. Ese dueño es justo lo que hay que diseñar.

La etiqueta es texto libre a propósito: la leerán sistemas distintos (props del mundo, aparición de jugadores, loot), y el modelo no debe casarse con ninguno.

### La vista previa de props (solo editor)

Desde `RoomAuthoringWindow.Props.cs`: si la etiqueta de un marcador `Prop` resuelve, el mueble se ve en la escena y sigue al marcador mientras se arrastra. Dos fuentes:

1. **El catálogo del mundo** — el `LayerVisualConfig` que ya amuebla los chunks (`PropEntry.placeholderType`: `desk`, `chair`, `cabinet`, `filecab`, `monitor`, `boxes`, `plant`, `whiteboard`…). Se usa este y no un catálogo propio para que lo que se ve sea exactamente lo que el mundo va a poner.
2. **Prefabs del proyecto con un TAG de Unity en la raíz**, agrupados por ese tag. El tag es el **filtro** —qué prefabs se ofrecen—, no la identidad: lo que se guarda en el marcador es el NOMBRE del prefab, porque veinte prefabs con el tag `RoomProp` son veinte props distintos.

Orden de resolución: `placeholderType` → nombre de prefab del catálogo → prefab etiquetado.

**Es SOLO previsualización, y el detalle importa:** esos objetos cuelgan de una raíz aparte, **no** del root de la sala, con `HideFlags.DontSave`. Si colgaran del root, el horneado manual —que guarda el root entero como prefab y recoge sus `BoxCollider` para el proxy— metería el mueble dentro de la sala horneada, bloqueando el paso.

---

## 7. Lo que le toca decidir al backend

Aquí está el motivo de este documento. Todo lo de abajo está **sin decidir** y pide ADR.

### 7.1 Colisión — ADR-083, PENDIENTE (wire 37 → 38)

El backend tiene que saber dónde están las cajas de la sala para que bloqueen de verdad en servidor. Preguntas abiertas: quién decide el emplazamiento (hoy lo decide el cliente con un hash), cómo llega al backend la geometría del pool (manifiesto en disco frente a wire), y si se registra a resolución de caja exacta o de celda de 2,5 m. Precedente que manda: `build_room` de ADR-081 enmienda 5 eligió **mandar 3 bytes antes que mantener dos generadores en fase**.

### 7.2 Props y loot — SIN ADR, y es lo más serio

Un marcador `Prop` que hoy es un punto será mañana **una caja con cosas dentro**. En el momento en que tenga contenido, deja de ser decoración y pasa a ser **estado del mundo**, y eso es del backend sin discusión:

- **Qué hay dentro** lo tira el servidor. Si lo tira el cliente, el loot es editable.
- **Si ya se ha saqueado** es estado persistente por chunk, y tiene que sobrevivir a la descarga del chunk y al reinicio.
- **Dos jugadores abriéndola a la vez** necesitan la guarda de "una petición en vuelo" que ya hicieron falta para los cadáveres, o el material se duplica.
- **Regenerar** ya está decidido en dirección (los props del mundo regeneran, ver ADR pendiente de props desmontables en `STATE.md`), pero no para las salas autoradas.

Lo que el cliente puede aportar es **dónde** hay un sitio de loot; **nunca** qué hay en él ni si sigue ahí.

### 7.3 La pregunta de fondo: ¿quién interpreta un marcador?

Hay dos formas y hay que elegir una a conciencia:

- **(a) El backend aprende a leer las salas.** Recibe el catálogo (footprints, cajas, marcadores con su etiqueta y su piso), decide emplazamiento y le dice al cliente qué colocar. El cliente deja de sortear. Una sola implementación de la regla; a cambio, el backend tiene que conocer un formato que hoy es puramente de cliente.
- **(b) El backend solo conoce los SITIOS.** El cliente sigue colocando la sala, y de los marcadores solo se registran en servidor los que producen estado (loot, aparición). Menos que aprender; a cambio, los dos lados siguen derivando cosas por separado, que es la clase de fallo que ya está anotada como deuda en ADR-081 (la puerta con pared invisible por dos generadores independientes).

Sea cual sea, hace falta una **guarda de versión del pool**: si el cliente pinta la sala 3 mientras el servidor cree que ahí está la 7, el fallo es invisible hasta que alguien se choca con nada. El proyecto ya tiene el patrón (`WIRE_SCHEMA_VERSION` con su espejo en C#), y ya se aprendió por las malas que ese espejo no puede quedarse sin bumpear.

---

> **DESACTUALIZADO desde 2026-08-20 en tres puntos.** ADR-083 enmiendas 1 y 2 (VALIDADAS e
> IMPLEMENTADAS) cambiaron esto, y manda el ADR:
>
> 1. **El emplazamiento lo decide el BACKEND**, no el cliente, y ya no cuelga de las zonas
>    `SealedRoom` — la reserva la dicta la sala. Todo el §5 describe el camino retirado.
> 2. **El perímetro y el suelo son de la SALA**, no del generador. La invariante 4 de abajo
>    ("la sala solo amuebla") queda derogada.
> 3. El §7.1 ya no está pendiente: está hecho a resolución de celda, con el interior de la sala
>    fuera del alcance del servidor. El §7.2 (props y loot) sigue intacto y sin ADR.
>
> Plan de trabajo vigente: [`../ROOMS-ROADMAP.md`](../ROOMS-ROADMAP.md).

## 8. Invariantes que no se pueden romper

1. **Malla y colliders salen del mismo modelo.** Nunca derivar uno del otro.
2. **La colocación es determinista y sin red.** Mismo seed ⇒ misma sala, mismo giro, en todos los clientes.
3. **El orden de las variantes del pool es contrato.** Reordenar cambia el mundo.
4. **La sala solo amuebla.** El perímetro es del backend.
5. **La previsualización no se hornea.** Ni props, ni nada que no salga del modelo.
6. **Un campo nuevo en el modelo sobrevive al round-trip JSON**, o se pierde en silencio.
7. **Una apertura no se declara si el vecino no ha llegado.**

## 9. Dónde mirar

- Modelo: `RoomDefinition.cs` — incluye `HoleRect`, el sistema de pisos y los helpers compartidos.
- Malla: `RoomMeshBuilder.cs`. Colisión: `RoomColliderBuilder.cs`.
- Triangulación con agujeros: `PolygonTriangulator.cs` — recorte de orejas con puentes.
- Colocación: `GridChunkBuilder.AuthoredRooms.cs`.
- Catálogo: `RoomPool.cs` → `Assets/Resources/Rooms/RoomPool.asset`.
- Herramienta: `Assets/Editor/RoomAuthoringWindow.cs` (+ `.Props.cs`).
- Tests: `Assets/Tests/EditMode/RoomMeshTests.cs` y el arnés `RoomGeometry.cs`. La invariante fuerte es `AssertRoom`: cascarón cerrado, sin aristas repetidas y con las normales bien.
