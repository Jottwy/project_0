> **PROPUESTO — 2026-09-16.** Auditoría + diseño consolidado del **Smiler**, la entidad gaseosa.
> **NADA DE ESTO ESTÁ IMPLEMENTADO.** No hay ADR todavía: este documento es el material con el que
> escribirlos. Pendiente de aprobación de Joel antes de tocar una sola línea de código.
> Sucesor previsto: `ADR-156` (canal de entidad no-humanoide) y los que salgan de §7.

# Smiler — auditoría del proyecto y diseño consolidado

Índice de los 23 entregables pedidos: §1 auditoría · §2 recursos · §3 reutilizables · §4 problemas ·
§5 contradicciones · §6 diseño consolidado · §7 arquitectura técnica · §8 IA · §9 navegación 3D ·
§10 propagación · §11 VFX · §12 luz · §13 batería · §14 manivela · §15 entorno · §16 sonido ·
§17 persecución · §18 ataque · §19 multijugador · §20 rendimiento · §21 riesgos · §22 prototipo ·
§23 criterios de éxito.

---

## 1. Auditoría del Smiler existente

### 1.1 El resultado principal: **no existe**

Barrido exhaustivo del repositorio completo (4,4 GB de `Assets/`, `backend/`, `docs/`, `tools/`,
`ProjectSettings/`, `Packages/`), sin distinguir mayúsculas:

| Término | Ficheros que lo contienen |
|---|---|
| `smiler` | **0** |
| `smile` | **1** — `Assets/TextMesh Pro/Fonts/LiberationSans.ttf` (binario de una fuente; falso positivo) |
| `Horror Entity` | 0 |
| `gaseous` / `gaseosa` | 0 |

**No hay prototipo anterior del Smiler.** Ni script, ni prefab, ni VFX, ni material, ni shader, ni
escena, ni ScriptableObject, ni ADR, ni TODO, ni comentario, ni línea de roadmap. No hay nada que
recuperar, nada obsoleto que borrar y ninguna «implementación anterior» con la que comparar. Los
puntos 8 y 9 del encargo (ideas interesantes de la implementación anterior, contradicciones con
ella) se responden sobre lo **adyacente**, que sí es abundante, no sobre un Smiler previo.

### 1.2 Lo que sí existe y le toca

El proyecto tiene **cuatro especies de criatura** construidas, en tres generaciones tecnológicas
distintas, y una de ellas es una de las piezas de ingeniería más maduras del repositorio.

| # | Criatura | Dónde | Líneas | Estado real |
|---|---|---|---|---|
| 1 | **Robapieles** (`phantom`) | `backend/src/game_loop/phantom.rs` | 5 629 | **VIVO y maduro.** 14 estados, ~120 constantes calibradas, 20+ ADR |
| 2 | **Faceling adulto / niño** | `backend/src/game_loop/faceling.rs` | 4 256 | VIVO. Colmena, roles, mirada que congela (ADR-094) |
| 3 | **Vigilante** (`watcher`) | `backend/src/game_loop/watcher.rs` | 275 | VIVO. `species = 3`, estático, no hace nada a propósito (ADR-131) |
| 4 | **Lurker / Crawler / Shadow** | `backend/src/world/entity.rs` | 399 | **OBSOLETO.** Daño desactivado desde 2026-07-07, sin colisión, XZ plano, `clamp_to_chunk`, renderizado con cubos de colores |

Y los sistemas que el encargo nombra explícitamente:

| Concepto pedido | Qué hay hoy | Fichero |
|---|---|---|
| Navegación | A\* 4-vecinas **con cota en la clave** sobre celdas de 0,5 m | `backend/src/world/wg3/nav.rs` (529) |
| Geometría consultable | Ráster de **columnas de spans macizos** por celda XZ | `backend/src/world/wg3/raster.rs`, `collision.rs` |
| Luz / linterna | Linterna de manivela completa, con batería, salud de batería y parpadeo | `Assets/Scripts/Gameplay/CrankFlashlightWieldable.cs` (548) |
| Parpadeo ambiental | Cadencia de fluorescentes por hash: apagados, parpadeo, fase, Hz | `Assets/Scripts/WorldGen3/Wg3LightCadence.cs` |
| Sonido de caza | Drone + latido sintetizados por distancia | `Assets/Scripts/Gameplay/Audio/ChaseAudioLayer.cs` |
| Detección por ruido | `report_noise` con viaje, error y caducidad (ADR-041) | `Assets/Scripts/Network/NoiseReporter.cs` + `phantom.rs` |
| Oclusión de audio | Por CUENTA de paredes, ≤ 18 m | `Assets/Scripts/Gameplay/Audio/AudioOcclusionMath.cs` |
| VFX | **Nada.** Cero `.vfx`, cero `.shadergraph`, cero `ParticleSystem` en código | — |
| Volumétrico | Rejilla de ocupación 3D **render-only**, sólo con semilla 7778 | `backend/src/world/volumetric_grid.rs` (3 702) |
| SDF | 0 referencias en código propio | — |

### 1.3 Documentación previa encontrada, y qué aporta

- **`docs/FACELING-ROADMAP.md`** — la lección más valiosa del repositorio para esto: el atasco
  «se quedan intentando atravesar una pared» tenía **cinco causas y el pathfinder no era ninguna**;
  la que mandaba era que *la detección era un test de ángulo sin oclusión*. Un test de navegación
  no discrimina un fallo de percepción. Se aplica palabra por palabra al Smiler.
- **`docs/LIGHTING-STUDY.md`** (275 líneas, 2026-09-07) — el inventario verificado del render.
  Es lo que fija los límites duros de §11.
- **`docs/AUDIO-PROPAGATION-ROADMAP.md`** — confirma que **nada lejano suena hoy**: todo
  `AudioSource` con `maxDistance` explícito del proyecto vive entre 7 y 18 m.
- **`docs/systems/ipc-wire-schema.md`** — el changelog del wire, que es LEY.
- **ADR-016** (robapieles = peer sintético), **ADR-040** (A\*), **ADR-041** (ruido), **ADR-042**
  (`light_on`), **ADR-080** (la luz te delata), **ADR-108** (WG3 sólo sabe contestar dos preguntas),
  **ADR-123** (agacharse y conductos), **ADR-133** (linterna de manivela).

---

## 2. Recursos encontrados — tabla FEATURE / EXISTENTE / ESTADO / REUTILIZABLE / CAMBIO

Búsqueda por los términos pedidos. `Estado` = lo que hace hoy, medido leyendo el código, no lo que
dice su comentario.

| Feature | Existente | Estado | Reutilizable | Cambio necesario |
|---|---|---|---|---|
| **Smiler** | — | No existe | — | Construir de cero |
| **smoke / humo** | — | No existe | — | Todo nuevo (§10, §11) |
| **entity (canal legado)** | `world/entity.rs`, `EntityView` en el wire, `EntityRenderer.cs` | OBSOLETO: daño OFF desde 07-07, `clamp_to_chunk`, sin colisión, cubos de colores | **El CANAL sí** (`visible_entities` ya viaja y ya se renderiza); la IA no | Retirar `EntityType`/FSM; reusar `visible_entities` como transporte de nodos, o canal propio |
| **entity (peer falso)** | ADR-016, `network/phantom.rs`, `species:u8` | VIVO, maduro | **NO para el Smiler** (§5.2) | Ninguno: el Smiler no cabe ahí |
| **horror / IA** | `phantom.rs` FSM de 14 estados | VIVO, calibrado en playtest | **SÍ, el ESQUELETO** | Reescribir el cuerpo de cada estado; conservar forma, temporizadores y temperamento |
| **VFX** | `TeleportationVFX.cs` | Vivo pero es un **overlay de Canvas** (flash + ruido de pantalla), no partículas | Sólo como patrón de «efecto autoarrancable y borrable» | — |
| **VFX Graph** | **PAQUETE NO INSTALADO** | `Packages/manifest.json` no tiene `com.unity.visualeffectgraph` | — | **Decisión de Joel**: instalarlo (§11) |
| **ParticleSystem** | Módulo presente en el manifest | **Cero usos en 6,2 MB de scripts** | — | Todo nuevo |
| **Shader Graph** | `com.unity.shadergraph 17.0.4` instalado | **Cero `.shadergraph` en el proyecto** | Disponible | Primer grafo del proyecto |
| **volumetric** | `world/volumetric_grid.rs` | Andamiaje; sólo con `WORLD_SEED=7778`; **NO borrable** (su campo está en `ChunkView`) | **La IDEA sí** (rejilla de ocupación 3D), el código no | Ignorarlo; no tocarlo (deuda declarada, bloque B5) |
| **navigation** | `wg3/nav.rs`: A\* con cota, `floors_at`, `segment_is_clear`, `string_pull` | **VIVO y correcto**, sube y baja escaleras | **SÍ, tal cual, para el CUERPO PRINCIPAL** | Añadir un grafo hermano de propagación (§9) |
| **navigation (legado)** | `grid_gen/nav.rs`: A\* 4-conexo, celdas 2,5 m, 2D + `layer` | VIVO pero es el mundo VIEJO | NO | — |
| **NavMesh de Unity** | `com.unity.ai.navigation 2.0.11` instalado | **Cero usos.** Ni `NavMesh`, ni `NavMeshAgent`, ni `NavMeshSurface` | **NO** (§9.1) | Ninguno: no se va a usar |
| **colisión / geometría** | `Wg3Raster`: por celda XZ de 0,5 m, lista ordenada de **spans macizos** en cm (`i16`) | **VIVO.** `is_solid_at`, `blocked_standing_at`, `floor_below`, `headroom_above_floor` | **SÍ — la pieza clave de todo el diseño** (§10.1) | Añadir consultas de HUECO (el complemento del span) |
| **light** | `Wg3SceneAssembler` (4 luces/tramo, alcance 11, intensidad 3,1), `Wg3ShadowBudget` (tope global), `Wg3StoreyLayers` (capa por planta) | VIVO | SÍ | El Smiler debe respetar capa de planta y presupuesto de sombras |
| **flicker ambiental** | `Wg3LightCadence`: `offChance` 0,12, `flickerChance` 0,08, Hz 0,7–5,5, fase por hash | VIVO y determinista por semilla | **SÍ, la mitad** | Le falta un canal de *influencia externa*: hoy nada puede decirle «hay algo cerca» (§12.4) |
| **flashlight** | `CrankFlashlightWieldable` (548 líneas, ADR-133) | **VIVO y completo**: carga en `Durability`, salud de batería persistente, parpadeo por `intensity` | **SÍ, casi entero** | Añadir drenaje ×2 y fallo por proximidad (§13, §14) |
| **battery** | Salud de batería 0,8–1,0 sorteada y escrita en el item | VIVO | SÍ | Multiplicador de consumo por influencia del Smiler |
| **crank** | Ciclo accionar→luz, 18–22 s por vuelta, ×0,4 de velocidad al dar cuerda | VIVO | SÍ | Curva de fallo (§14) |
| **detection / tracking** | Cono 120°, 15 m, histéresis a 25 m, **38 m si llevas luz**, `PHANTOM_SPEED_SANITY_MAX` | VIVO y calibrado | **SÍ el mecanismo**, invertido el signo de la luz | §5.1 |
| **chase** | `Stalk`/`Sprint`/`Hunting` + estamina + `sprint_blind_for` + `ChaseAudioLayer` | VIVO | **SÍ, la forma** | Velocidad por estado pasa a ser velocidad de FRENTE (§17) |
| **propagation** | — | No existe | — | Todo nuevo (§10) |
| **SDF** | — | No existe | — | Decisión abierta (§11.3) |
| **sonido** | `ChaseAudioLayer`, `AudioOcclusionMath`, `ReverbMixerDriver`, `FluorescentHumDirector`, `IsolationDirector` | VIVO | SÍ | Nada suena a más de 18 m (§16) |

---

## 3. Sistemas reutilizables (el inventario que ahorra semanas)

Por orden de valor.

### 3.1 `Wg3Raster` — la representación del mundo que el Smiler necesita, y ya existe

`backend/src/world/wg3/raster.rs`. Por cada celda XZ de **0,5 m**, una lista ordenada de
**spans macizos** `{bottom_cm: i16, top_cm: i16}`. Es decir: el mundo ya está guardado como
*columnas de materia y hueco*, en 3D real, para toda la altura del chunk (no por capas).

**El hueco por el que se propaga el humo es literalmente el complemento de esos spans**, y sale de
restar dos enteros. No hay que hornear nada, no hay que sincronizar nada, no hay una segunda copia
del mundo que pueda desviarse — el mismo argumento que ADR-108 D3 usa para la andabilidad.

`Wg3CollisionCache` ya lo cachea (256 rásteres, desalojo por uso reciente, medido) y ya lo comparten
todas las criaturas.

### 3.2 `wg3::nav` — A\* con cota, que ya sube y baja escaleras

`find_path` usa como clave `(x, z, cota_del_suelo)` y enlaza vecinas si el salto cabe en
`MAX_WALK_STEP_CM`. `floors_at` devuelve **hasta dos** cotas por celda — el arreglo documentado del
robapieles que subía escaleras y no las bajaba. Ventana declarada en **metros** (no en celdas).
Más `segment_is_clear` (muestreo cada media celda, con control de salto vertical) y `string_pull`.

Sirve **tal cual** para el destino del Smiler. Lo que NO sirve es para el humo: un humo que sigue
waypoints es exactamente lo que el encargo prohíbe (§10).

### 3.3 `phantom.rs` — el esqueleto de IA, no su contenido

14 estados, temporizador por estado, temperamento reproducible desde la semilla (`PhantomTraits`),
hambre como motor lento, memoria de escondites, flanqueo por el punto ciego, paciencia, estamina con
ventana de agotamiento, sello cosmético al final del tick, `pending_vocal` puesto en escena y
sellado al final (porque la FSM tiene muchos `continue` tempranos y un escritor por rama se olvida
uno). **Todo eso es forma, y la forma vale.** Lo que no vale es el cuerpo: son las decisiones de un
humanoide con piernas.

Piezas concretas que se copian sin cambios: `PHANTOM_SPEED_SANITY_MAX` (ignorar deltas de
teleport/desplazamiento de chunk), el patrón de sellado, el reconciliador de población con
activación 150 m / desactivación 200 m e histéresis, el tope activo (`PHANTOM_ACTIVE_CAP = 6`), el
`env_tuning` de cada constante.

### 3.4 El canal de ruido (ADR-041)

`NoiseReporter` → `report_noise` → el ruido **viaja** a 4,5 m/s, llega con error del 8 %, caduca a
los 130 s y tiene paciencia de búsqueda. Está probado y es exactamente lo que necesita un Smiler
que oye antes de ver.

### 3.5 La linterna de manivela (ADR-133)

`CrankFlashlightWieldable` ya resuelve: carga como propiedad de INSTANCIA del item (persiste, se
saquea del cadáver, la UI del vendor la pinta sola), salud de batería sorteada una vez y escrita
para siempre, parpadeo **por `intensity` y nunca por `Light.enabled`** (porque `Light.enabled` es
lo que se relaya a los demás como `light_on`, y un estrobo en ese bit rompería la detección de los
otros clientes). Ambos sistemas de §13 y §14 son enganches sobre esto, no reescrituras.

### 3.6 `Wg3LightCadence` — la mitad del parpadeo ambiental

Ya hay parpadeo determinista por hash de (chunk, posición, índice, semilla), con fase y frecuencia
propias por lámpara. Lo que falta es un **canal de influencia** para que la presencia del Smiler lo
module sin romper el determinismo. Es un campo, no un sistema.

### 3.7 `ChaseAudioLayer` y su regla de oro

Lee **sólo** `revealed` (que es conocimiento público del relay) y la distancia. Nunca lee estado
privado — hambre, ids de fantasma, la FSM — porque eso lo convertiría en un oráculo. Esa disciplina
se hereda entera: la capa sonora del Smiler puede leer su presencia visible, jamás su intención.

### 3.8 `TorchShadowCaster`

Promueve **exactamente una** luz de todo el juego a proyectar sombras, siempre la del wieldable
activo, sin preguntar qué item es, y le escribe la capa de planta cada frame. Es el sitio natural
donde colgar la «Light Protection Zone» (§12), porque ya es el único componente que sabe
autoritativamente cuál es la luz que el jugador lleva encendida.

---

## 4. Problemas existentes (lo que hay que saber antes de empezar)

1. **VFX Graph no está instalado.** `Packages/manifest.json` no lo lista. Instalarlo mete compute
   shaders, tiempo de build y superficie de plataforma nueva en un proyecto que hoy es
   URP/Lit + mallas. Es una decisión de Joel, no un detalle de implementación.
2. **Cero cultura de VFX en el repositorio.** Ni un `.vfx`, ni un `.shadergraph`, ni un
   `ParticleSystem` en 6,2 MB de C#. El Smiler sería el primero de todo.
3. **URP 17.0.4 no tiene nada volumétrico.** Sin niebla volumétrica, sin GI (`LIGHTING-STUDY.md`
   §3.4: «con URP 17.0.4 la única indirecta posible es sintética»), ambiente **negro** por petición
   de Joel, tope de **256 luces visibles por cámara**, atlas de sombras 4096 con un presupuesto
   global ya implementado (`Wg3ShadowBudget`) porque 6-8 sombras suaves ya forzaban el atlas.
4. **Las luces se reparten por capa de render y por planta.** `Wg3StoreyLayers`: la calle es el
   bit 3 y B3 es el bit 0 (`Default`). Cualquier luz o efecto del Smiler que nazca con la capa por
   defecto **iluminará B3 y sólo B3**. Esto ya costó una tanda con la antorcha del vendor.
5. **No existen puertas con estado que el servidor conozca.** Los vanos de WG3 son huecos sin hoja;
   las únicas puertas con hoja son las que construye un jugador (`GridDoorLeafBuildingPiece`), y
   abrir/cerrar lo resuelve el componente `Door` del vendor **en el cliente**, sin viajar. El
   requisito «puerta cerrada: el humo no pasa / puerta abierta: entra» **no tiene hoy nada sobre lo
   que apoyarse** (§15.2).
6. **Los conductos no existen todavía.** ADR-123 está PROPUESTA, pendiente de Joel, y su D3 dice
   literalmente que **las criaturas no entran** (§5.3).
7. **El mundo servido va ×1 mientras ADR-128 sigue en rojo** (24 tests), y `MAX_SEGMENT_M` está
   congelado. Cualquier constante en metros que se escriba ahora cambia de significado si ADR-128
   entra.
8. **`docs/DECISIONS.md` miente por omisión sobre el robapieles.** Los ADR **055, 068, 070-073,
   075, 076, 078-084, 088-092** están escritos con `###` y no con `##`, así que son **invisibles**
   tanto para `DECISIONS-INDEX.md` como para el protocolo de lectura oficial
   (`grep -n "^## ADR-NNN"`). El código cita `ADR-080 point 4` y `ADR-092` en cada estado del FSM y
   la ley que los sostiene no se encuentra siguiendo las reglas del propio `CLAUDE.md`.
   `STATE.md` sólo tiene anotado el caso de ADR-149 enm. 1-8; es mucho más ancho. **No lo arregla
   este trabajo** (regla 11: `DECISIONS.md` es append-only), pero hay que saberlo antes de escribir
   el ADR del Smiler apoyándose en ellos.
9. **Deuda de tests viva:** 12 tests de EditMode en rojo y `phantom_sprints_after_patience_exceeded`
   flaquea cargado. Un rojo nuevo en el arnés del Smiler tiene que poder distinguirse de ese ruido.
10. **`world/entity.rs` es un fósil que todavía viaja.** `visible_entities` se sigue serializando y
    `EntityRenderer` sigue instanciando cubos. Nadie lo ha retirado.
11. **`docs/INDEX.md` está lleno hasta el byte: 4 096 B de un tope de 4 096.**
    `tools/dev/CheckStateBudget.py` se
    pone en rojo —y con él el gate del commit— al añadir **cualquier** línea, por corta que sea. Por
    eso **este documento NO está dado de alta en `INDEX.md`**, en contra de la convención del
    proyecto («índice de toda la documentación»). Registrarlo exige antes recortar otra fila o subir
    el tope, y las dos cosas son decisión de Joel, no de esta sesión. Mientras tanto, un documento
    que el índice no lista es un documento que la siguiente sesión no encuentra al arrancar.

---

## 5. Contradicciones entre lo existente y el diseño actual

Cinco, y las tres primeras son de las que exigen **PARAR y preguntar** (regla dura 2).

### 5.1 🔴 La luz te DELATA — el diseño del Smiler dice que te PROTEGE

ADR-080 punto 2, implementado y calibrado:

```
PHANTOM_DETECT_RADIUS       = 15,0 m   // te ve
PHANTOM_LIGHT_DETECT_RADIUS = 38,0 m   // te ve si llevas luz encendida
```

con el comentario: *«carrying the brightest object in the game had no cost… Turning the light off is
the counterplay, and that is the whole point»*. Y ADR-042 puso `light_on` en el wire justamente para
eso.

El Smiler invierte la regla entera, y no en grado sino en signo: **luz activa = objetivo inválido,
inmunidad absoluta**.

No es irreconciliable —son especies distintas y una regla opuesta por especie es diseño legítimo y
memorable— pero **es una decisión de Joel, no mía**, y tiene una consecuencia de juego que hay que
mirar de frente: en un mundo donde ambas criaturas coexisten, encender la linterna te salva del
Smiler y te delata al robapieles a 38 m. Eso puede ser **excelente** (la luz deja de ser un botón y
pasa a ser una apuesta, y el jugador tiene que saber qué le está cazando) o **ilegible** (el jugador
concluye que el juego es arbitrario). La diferencia entera está en si el jugador puede identificar
qué le persigue **antes** de decidir. Lo trata §12.6.

### 5.2 🔴 ADR-016: «una criatura es un peer falso humanoide» — el Smiler no tiene cuerpo

ADR-016 es el principio director de toda criatura del proyecto: *«el robapieles es un peer SINTÉTICO
inyectado en `net.peers` FUERA del handshake… CERO código de cliente nuevo»*. Las tres especies
vivas cuelgan de ahí, y `species: u8` (0 humano, 1 adulto, 2 niño, 3 vigilante) es **un byte
cosmético del relay de poses** que sólo tiene sentido sobre un avatar humanoide con esqueleto,
animación retargeteada, `crouch`, `pitch`, `held_item` y `equipment`.

El Smiler no tiene cuerpo humanoide, ni cabeza, ni transform único, ni pose. **`species = 4` sería
un error de diseño**, no un atajo: heredaría un avatar, un `AnimatorController`, un nameplate y una
tubería de poses que no le sirven de nada, y pagaría su ancho de banda.

**Conclusión: el Smiler necesita canal propio.** Es un ADR nuevo con bump de wire (regla dura 7). Lo
propone §7.3.

### 5.3 🟠 ADR-123 D3: «las criaturas NO entran en los conductos»

Textual: *«`wg3::nav::floor_at` sigue exigiendo `BODY_M`: el conducto es refugio. El robapieles y
los facelings se paran en la boca. Es lo que hace que agacharse signifique algo. Si algún día una
criatura repta, es otro ADR.»*

El diseño del Smiler pide explícitamente conductos como vía de propagación y de retirada. La ADR
anticipó el caso y dijo cómo se resuelve: **otro ADR**. Además ADR-123 está PROPUESTA (no aceptada)
y el conducto **todavía no existe en el mundo**, así que el conflicto es de futuro, no de hoy. Pero
si se acepta ADR-123 y después el Smiler entra por los conductos, **se destruye la única promesa que
ADR-123 le hace al jugador**: que agacharse te pone a salvo.

Mi recomendación, para que Joel decida: **el conducto sigue siendo refugio contra todo lo que tiene
cuerpo, y el Smiler SÍ entra — pero por eso es el único sitio donde no puedes esconderte de él.**
Eso convierte los dos sistemas en un triángulo en vez de en una contradicción: el conducto te salva
del robapieles y te condena con el Smiler; la linterna al revés.

### 5.4 🟡 «La iluminación ambiental no protege» choca con la lectura que el mundo ya enseña

En el juego de hoy la única luz que hay es la del techo, y el `IsolationDirector` y el
`FluorescentHumDirector` ya han enseñado al jugador que **iluminado = habitado = menos malo**. El
diseño del Smiler dice lo contrario. Es una contradicción de LECTURA, no de código, y se resuelve
enseñándola: si el Smiler se anuncia haciendo parpadear precisamente esas luces (§12.4), el propio
sistema explica que ahí no hay refugio.

### 5.5 🟡 Nada del proyecto reacciona a una fuente de luz que no sea `Light.enabled`

`PlayerPoseTransmitter.ReadLightOn` (ADR-042) es literalmente *«¿hay alguna `Light` habilitada bajo
el wieldable activo?»*, muestreado a 10 Hz, viajando como **un bit** (bit 3 de las flags de pose).
El encargo pide explícitamente no depender de eso, y tiene razón: ese bit no sabe hacia dónde apunta
el haz, ni su alcance, ni si hay una pared por medio, ni si la intensidad ha caído al 15 % por
batería baja. §12 lo sustituye por una zona geométrica de verdad.

---

## 6. Diseño consolidado

Lo que valido del encargo, lo que matizo y lo que propongo añadir.

### 6.1 Validado sin cambios

- Cuerpo de humo volumétrico, sin anatomía; ojos y sonrisa blancos **dentro** de la masa, no unidos
  a una cabeza.
- Masa distribuida, no un transform.
- Dos capas separadas: la IA decide **adónde**, el humo decide **cómo**.
- Luz del jugador = inmunidad **absoluta**, sin probabilidad ni gradiente.
- Consumo ×2 mientras el Smiler está en zona de influencia, sin tocar la protección.
- Toda aparición y toda retirada son progresivas y físicas: nada de `SetActive`, nada de teleport.
- Luz ambiental: señal, nunca protección.

### 6.2 Matizado

- **«No debe atravesar una pared porque sí»** → la formulación correcta para este proyecto es:
  *el Smiler se propaga por el mismo espacio libre que el ráster ya describe, con un cuerpo de
  radio menor que el del jugador.* Así «atraviesa una rendija que tú no» sale gratis del modelo, en
  vez de ser un caso especial.
- **La progresión de 9 fases del encuentro** es demasiado fina para un primer prototipo: `LATENT`,
  `PRESENCE`, `PROPAGATION`, `PRESSURE`, `INVASION`, `HUNT`, `CHASE`, `EXTREME PROXIMITY`,
  `ATTACK WINDOW` son nueve transiciones que hay que poder distinguir a ojo para poder calibrarlas.
  Propongo **seis estados** en v1 (§8.2) y subir a nueve sólo cuando el playtest pida el matiz. Es
  la lección de ADR-050 punto 14 y punto 15: constantes puestas «por si acaso» que luego hubo que
  desandar.
- **Las distancias como parámetros**: de acuerdo, y con la forma que el proyecto ya usa —
  `env_tuning("SMILER_X", DEFAULT)`, constante de código con valor por defecto y palanca de
  entorno, criterio explícito de ADR-075 (*«primero se juega, luego se decide qué palanca merece
  env»*).

### 6.3 Añadido que propongo

- **El Smiler es la única criatura que el jugador puede ver sin que ella le vea.** El humo entrando
  por una rejilla al fondo del pasillo es información gratis, y es lo que convierte la linterna en
  una decisión anticipada en vez de en un botón de pánico.
- **Un solo Smiler por sesión.** No es una restricción de rendimiento (que también): dos Smilers
  hacen que el jugador deje de razonar sobre «el» Smiler.
- **La retirada tiene memoria.** El Smiler que se retira por un conducto vuelve por otro. Reusa
  `hideouts` de `phantom.rs`, que ya guarda dónde acabó una caza.

### 6.4 La frase que gobierna todas las decisiones de abajo

> **Puedes estar seguro mientras tengas luz.** El juego no es esquivar al Smiler: es administrar
> luz. El Smiler existe para poner precio a la oscuridad, y todo lo demás — la propagación, el
> sonido, el parpadeo, la manivela — está al servicio de que ese precio se sienta.

---

## 7. Arquitectura técnica

### 7.1 Principio: **autoridad en el servidor, teatro en el cliente**

Es ADR-009 y no se negocia. Pero el reparto no es el de las otras criaturas, porque lo que hay que
replicar no es una pose.

```
 BACKEND (Rust, host-autoritativo)          │ CLIENTE (Unity)
 ───────────────────────────────────────────┼──────────────────────────────────────
 SmilerMind    — la IA. Decide intención    │ SmilerPresence — recibe el campo
 SmilerField   — la MASA. Nodos de          │ SmilerVolume   — lo convierte en humo
                 presencia sobre el ráster  │ SmilerFace     — ojos y sonrisa
 SmilerLight   — zonas de luz del jugador   │ SmilerAudio    — voz y flujo
 (todo determinista, sin HashSet sin        │ SmilerFlicker  — modula Wg3LightCadence
  ordenar — regla dura 13)                  │ (CERO decisiones; sólo interpreta)
```

**El cliente no decide nada del Smiler.** Ni si te ve, ni si te ataca, ni si se retira. Interpola,
dibuja y suena. Es lo mismo que ADR-016 impone para el robapieles y por la misma razón: dos
autoridades sobre una criatura es un desincronizado garantizado en multijugador.

**Excepción declarada**: la zona de protección se calcula en el **cliente** y se reporta (§12.3).
Es la única concesión, tiene motivo y tiene guarda.

### 7.2 Las tres capas, y por qué son tres y no dos

| Capa | Frecuencia | Vive en | Pregunta que contesta |
|---|---|---|---|
| **Intención** | 2 Hz | Backend | «Quiero llegar a esa habitación» |
| **Presencia** | 10 Hz | Backend, viaja | «Estoy en estos 6 sitios, con esta densidad» |
| **Humo** | Por frame | Cliente, no viaja | «Así se ve y así se mueve el humo entre esos sitios» |

La capa del medio es la que el encargo llama «representación espacial» y es la que hace posible todo
lo demás: es lo único que cruza el cable, es lo que el VFX consume y es lo que hace que dos
jugadores vean **el mismo** Smiler sin sincronizar una sola partícula.

### 7.3 El canal de wire (ADR nuevo obligatorio, regla dura 7)

No `species = 4`. Un mensaje propio, del orden de:

```rust
struct SmilerFieldMsg {        // ~10 B por nodo, ≤ 8 nodos
    node_count: u8,
    nodes: [SmilerNode; N],
}
struct SmilerNode {
    x_dm: i16, y_dm: i16, z_dm: i16,  // decímetros: 0,1 m basta para humo
    radius_dm: u8,                    // 0,1–25,5 m
    density: u8,                      // 0–255
    flags: u8,                        // cara visible, retirándose, atacando
}
```

**8 nodos × 10 B = 80 B más cabecera**, holgadamente dentro del límite de 1 200 B por datagrama
(ADR-113) y comparable a **dos** poses delgadas de ADR-144 (23 B). A 10 Hz son ~0,9 KB/s por
jugador que lo vea, contra los ~320 KB/s de subida del host con 50 jugadores juntos: **menos del
0,3 %**. Lo cuantifica §19.

---

## 8. Arquitectura de IA

### 8.1 Forma

Copia deliberada de `PhantomMover`/`PhantomDriver`, que ya resolvió todos los problemas de forma:
temporizador por estado, decisiones puestas en escena y selladas al final del tick, temperamento
reproducible desde la semilla, `env_tuning` por constante, reconciliador de población con histéresis.

Lo que **no** se copia: nada del cuerpo de los estados. Un humanoide y una masa gaseosa no comparten
ni una decisión.

### 8.2 Los estados (seis en v1)

| Estado | Qué hace | Salida |
|---|---|---|
| `Latent` | Existe, muy lejos, sin cuerpo visible. Deriva lentamente por el nivel | Un jugador entra en el radio de presencia |
| `Presence` | Se anuncia: las luces de la zona empiezan a fallar, hay flujo lejano. Aún sin humo visible | Sigue acercándose → `Propagation` · pierde interés → `Latent` |
| `Propagation` | El humo entra en el espacio del jugador, por suelo/techo/rendijas. Frentes múltiples | Contacto de frente → `Pressure` · luz → `Retreat` |
| `Pressure` | Ocupa el espacio, corta salidas, la cara emerge y se disuelve | Proximidad extrema → `Strike` · luz → `Retreat` |
| `Strike` | La ventana de ataque: el humo se retira del campo de visión, suena, reaparece encima, la sonrisa emerge, y hay N segundos para encender | Luz → `Retreat` · tiempo agotado → daño → `Retreat` |
| `Retreat` | Contracción, fuga física por la geometría, dispersión | `Latent` |

Nótese que **`Retreat` es la salida de todo**. No hay un estado «muerto», no hay despawn y no hay
`SetActive(false)`: el Smiler siempre se va por algún sitio. Es el requisito del encargo convertido
en topología del grafo, que es la única forma de que no se incumpla por descuido en una rama.

Las nueve fases del encargo se mapean así: `LATENT`→`Latent`; `PRESENCE`→`Presence`;
`PROPAGATION`→`Propagation`; `PRESSURE`+`INVASION`→`Pressure`; `HUNT`+`CHASE`→ no son estados sino
el **modo de avance** del frente dentro de `Propagation`/`Pressure` (§17);
`EXTREME PROXIMITY`+`ATTACK WINDOW`→`Strike`.

### 8.3 Percepción

Tres sentidos, y **ninguno es un cono de visión** — un gas no tiene cara con la que mirar:

1. **Presencia** — distancia por el grafo de propagación (no euclídea), con el mismo par
   radio/histéresis del robapieles. Que sea por el grafo importa: estar a 4 m con un forjado por
   medio no es estar a 4 m.
2. **Ruido** — `report_noise` tal cual (ADR-041). Es el canal principal: el Smiler te oye antes de
   «verte».
3. **Luz** — el veto absoluto (§12). Se evalúa **antes** que todo lo demás, en el preámbulo del
   tick, y al evaluarse a `true` corta la intención de raíz, no la atenúa.

**Regla que sale directa de `FACELING-ROADMAP.md` §2.1 y que no se negocia: toda percepción lleva
oclusión real.** El bug que costó una sesión entera a los facelings fue *«la detección era un test
de ángulo sin oclusión»*, con el resultado de que el pack convergía sobre un objetivo inalcanzable.
Con el Smiler el mismo fallo sería invisible y peor: un humo que empuja contra un forjado para
siempre.

### 8.4 Temperamento

Reusar la forma de `PhantomTraits`: valores centrados en 1,0, sorteados desde la semilla, fijos
mientras la criatura existe. Para el Smiler: `patience_scale` (cuánto ronda antes de presionar),
`spread_scale` (cuántos frentes abre a la vez), `boldness` (cuánto se acerca a una luz encendida sin
entrar). Tres, no doce.

---

## 9. Arquitectura de navegación 3D

### 9.1 Qué NO se usa, y por qué no

**NavMesh de Unity queda descartado**, aunque `com.unity.ai.navigation 2.0.11` esté instalado (con
cero usos en todo el proyecto). Cuatro razones, cualquiera de ellas bastaría:

1. La autoridad está en el servidor Rust. Un NavMesh vive en Unity: la IA tendría que mudarse al
   cliente, contra ADR-009.
2. El mundo es procedural y se streamea por chunks. `NavMeshSurface` hornea, y hornear en runtime
   por chunk es un coste que nadie ha medido y una tercera representación del mundo que puede
   discrepar de las otras dos — exactamente lo que ADR-108 D1 prohíbe.
3. NavMesh es una **superficie**. El Smiler se propaga por **volumen**: techos, paredes, huecos
   verticales. Un NavMesh no sabe decir «por el techo».
4. El proyecto ya tiene navegación 3D que funciona y está probada en playtest.

### 9.2 Lo que sí: dos grafos hermanos sobre el MISMO ráster

```
                    Wg3Raster  (celda 0,5 m, spans macizos en cm)
                         │
        ┌────────────────┴────────────────┐
        │                                 │
  wg3::nav (EXISTE)              smiler::flow (NUEVO)
  «¿por dónde anda un cuerpo      «¿por dónde pasa un gas de radio r?»
   de pie de 1,8 m?»               - vecinas: 6 (±X, ±Z, ±Y)
  - vecinas: 4 (±X, ±Z)            - pasa si el hueco libre ≥ 2·r
  - clave (x, z, cota_suelo)       - clave (x, z, banda_de_hueco)
  - salto ≤ MAX_WALK_STEP_CM       - sin restricción de salto vertical
```

**Una sola fuente de verdad del mundo, dos lecturas.** Es el mismo argumento de ADR-108 D3 aplicado
otra vez, y es lo que garantiza que el humo y el jugador nunca discrepen sobre dónde hay pared.

### 9.3 El nodo de propagación

La clave del grafo del humo no es `(x, z, cota_del_suelo)` sino **`(x, z, índice_de_hueco)`**: el
hueco entre dos spans macizos consecutivos de esa columna. Así:

- El **suelo de la planta 2** y el **techo de la planta 1** son huecos distintos de la misma
  columna, y no se confunden nunca.
- Un **pozo** (ADR-126) es dos huecos que se tocan: la conexión vertical sale gratis.
- Un **conducto** de 1,0 m de alto es un hueco donde un cuerpo de pie no cabe y un gas de radio
  0,25 m sí. La propagación por conductos no necesita una regla: es la ausencia de la regla del
  cuerpo.
- Una **rendija bajo una puerta** es un hueco de 2 cm. Con radio de gas 0,25 m no pasa. Si algún
  día queremos que pase, es bajar un número, no escribir un caso.

### 9.4 Coste, y el argumento que lo sostiene

Este grafo es más caro que el de un cuerpo: 6 vecinas en vez de 4, y varias bandas por columna.
Pero la mitiga la escala: el frente del humo se busca a **8–15 m**, no a 60. La ventana en metros
que ADR-108 D1 exige declarar sale mucho más pequeña, y con ella el cuadrado del coste.

**Y hay un techo duro que se hereda tal cual:** `NAV_MAX_EXPANSIONS`. Un A\* sin tope puede comerse
un tick, y este proyecto ya lo midió (`cre_prewarm` llegó a 7 531 ms en un tick de 16,67 por un
caché mal dimensionado). Best-effort acotado, como el de WG3.

---

## 10. Arquitectura de propagación

### 10.1 El modelo: un campo de presencia sobre nodos, no partículas

El backend mantiene entre **4 y 8 nodos de presencia**. Cada uno es `(posición, radio, densidad)`.
No son partículas: son *dónde está el Smiler*, y son invisibles.

Cada tick (10 Hz):

1. **Difusión.** Cada nodo reparte densidad a los nodos-hueco vecinos accesibles. La conductancia
   entre dos huecos vecinos es proporcional a la sección libre entre ellos: un vano ancho pasa
   mucho, una rendija pasa poco, una pared pasa cero. **Es lo que hace que el humo rodee un
   obstáculo sin que nadie escriba «rodea el obstáculo».**
2. **Sesgo de intención.** La dirección que pide `SmilerMind` (§8) añade un peso a la conductancia
   hacia el objetivo. Es el único punto donde la IA toca el humo.
3. **Flotabilidad.** Peso extra hacia arriba con densidad baja, hacia abajo con densidad alta. Es lo
   que hace que suba por un hueco de escalera y se derrame por un pozo, gratis.
4. **Conservación y poda.** La masa total es constante salvo en `Propagation` (crece) y `Retreat`
   (decrece). Los nodos por debajo de un umbral se funden con su vecino más denso; los que superan
   un radio se parten. Es un **quadtree de columnas**, no una malla.
5. **Sellado.** Los nodos se ordenan por `(x, z, y)` antes de emitirse. **Regla dura 13**: jamás
   iterar un `HashSet`/`HashMap` para producir salida.

### 10.2 Por qué así y no de las otras dos formas

| Alternativa | Por qué no |
|---|---|
| **Rejilla de vóxeles densa** con simulación de fluido | Una habitación de 20×20×3 m a 0,5 m son 14 400 celdas. Por tick, por Smiler. Y el mundo se streamea: los bordes de la rejilla son casos especiales para siempre |
| **Partículas autoritativas en el servidor** | N partículas × N jugadores en el cable. Imposible con el presupuesto medido (§19) |
| **Todo en el cliente** | Dos jugadores verían dos Smilers distintos, y la autoridad del ataque estaría en el cliente. Contra ADR-009 |

El campo de nodos es la única representación que es a la vez **barata de simular**, **barata de
replicar** y **suficiente para alimentar un sistema visual bonito**. Y encaja con la petición del
encargo textualmente: los elementos lógicos son invisibles y su función es decir dónde está presente
el Smiler.

### 10.3 Precalentado del ráster

`Wg3CollisionCache::prewarm_for_move` ya existe y es el patrón obligatorio (el préstamo mutable del
caché y el inmutable de la consulta no pueden coexistir). El Smiler precalienta el 3×3 de chunks
alrededor de la caja envolvente de sus nodos, una vez por ronda. **Esto es lo que ya se midió como
partida mayor del coste de criaturas**: el tope de rásteres (256, desalojo por uso reciente) es lo
que lo hace viable, y hay que medirlo otra vez con el Smiler dentro, no suponerlo.

---

## 11. Arquitectura VFX

### 11.1 El límite duro que decide todo lo demás

`docs/LIGHTING-STUDY.md` es taxativo: URP 17.0.4 Forward+, **sin GI de ningún tipo**, ambiente
negro, tope de 256 luces visibles por cámara, atlas de sombras 4096 ya repartido, y presupuesto
global de sombras ya implementado porque 6-8 sombras suaves lo forzaban.

**No hay niebla volumétrica en URP.** El «humo volumétrico» del Smiler **no** puede salir de una luz
volumétrica ni de un `LocalVolumetricFog`. Tiene que salir de geometría que se dibuja: partículas
con billboards, o un raymarch propio en un shader.

### 11.2 La decisión que necesita Joel: VFX Graph, sí o no

**Con VFX Graph** (`com.unity.visualeffectgraph`, hay que instalarlo):
- ✅ Curl noise, vector fields, colisión contra el depth buffer, cientos de miles de partículas en GPU.
- ✅ Es literalmente la herramienta que el encargo describe.
- ❌ Paquete nuevo en un proyecto con cero cultura de VFX, compute shaders, tiempo de build, y una
  superficie de plataforma que nadie ha probado aquí.
- ❌ La colisión contra SDF necesita SDFs. En un mundo procedural hay que hornearlos en runtime
  (`MeshToSDFBaker`), que es coste por región y trabajo que nadie ha medido.

**Sin VFX Graph** (`ParticleSystem` + Shader Graph):
- ✅ Cero paquetes nuevos. Shader Graph ya está instalado (y sin usar).
- ✅ Suficiente para un prototipo honesto: unos cientos de billboards por nodo, con ruido en el
  shader y con las fuerzas calculadas en C#.
- ❌ Coste de CPU por partícula; techo mucho más bajo; el resultado es «humo de videojuego de 2015»
  en vez de «humo».

**Mi recomendación:** **empezar sin VFX Graph**, con `ParticleSystem` + un shader propio, y medirlo.
No por conservadurismo, sino porque **el prototipo que hay que validar primero no es el humo: es la
propagación** (§22). Un Smiler que se propaga correctamente con esferas grises de depuración ya
demuestra el 80 % del riesgo del proyecto. Un Smiler precioso que atraviesa forjados no demuestra
nada. Cuando la propagación esté validada, cambiar la capa visual es un trabajo acotado y con
criterio — y ése es el momento de decidir VFX Graph con datos.

### 11.3 Sobre el SDF

El encargo pide investigar SDF. La conclusión honesta: **el proyecto ya tiene algo mejor que un SDF
para este uso**. Un SDF es una aproximación horneada de la distancia a la superficie; el
`Wg3Raster` es la geometría exacta, ya cacheada, con consulta O(spans de la columna). Para la
propagación (§10) el ráster gana claramente.

Donde un SDF **sí** aportaría es en la capa visual, para que las partículas colisionen en GPU sin
volver a CPU. Eso es exactamente la conversación de §11.2 y queda diferido a después del prototipo.

### 11.4 Las transiciones

Lo que el encargo pide (`SMALL SMOKE → EXPANSION → PROPAGATION → DENSITY → CONCENTRATION → EYES →
SMILE`) **no es una animación: es una lectura del campo**. Cada fase es un rango de `density` y de
`node_count`:

| Fase | Lectura del campo |
|---|---|
| `SMALL SMOKE` | 1 nodo, radio pequeño, densidad baja |
| `EXPANSION` | radio creciendo |
| `PROPAGATION` | `node_count` creciendo |
| `DENSITY` | densidad media subiendo |
| `CONCENTRATION` | `node_count` bajando con densidad alta (se junta) |
| `EYES` / `SMILE` | flag de cara + el nodo más denso pasa un umbral |

Así no hay un `Animator` que pueda desincronizarse del estado real, y la transición «depende de la
situación y del espacio» sin que nadie la autore: depende porque el campo depende.

**La cara** (`SmilerFace`) es una malla plana con billboard hacia la cámara, colocada en el nodo más
denso, con la opacidad atada a la densidad de ese nodo y **sin sombra propia**. Nace dentro del
humo, no se pega a nada, y se disuelve cuando la densidad cae. Emisiva pura: en un mundo con
ambiente negro (petición de Joel) una sonrisa emisiva blanca es lo único que se ve, y eso es el
efecto que se busca.

### 11.5 Tres trampas del proyecto que este sistema tiene que respetar desde el primer commit

1. **Capa de planta.** Todo renderer y toda luz del Smiler escribe su `Wg3StoreyLayers` como hace
   `TorchShadowCaster`. Si no, el humo de B1 se ve en la calle, o no se ve nunca.
2. **Cero luces con sombra.** `Wg3ShadowBudget` es global. El Smiler no proyecta sombras; si emite
   luz (la sonrisa), nace con `LightShadows.None`, como las lámparas de worldgen y las antorchas de
   los peers.
3. **Warp del viewmodel.** Regla dura 14 / ADR-077 enm. 2: **si el humo llega a tocar la cámara en
   primera persona hay que decidir si deforma con el viewmodel o no**. La respuesta correcta es que
   **no** es viewmodel — es mundo — así que usa shaders de mundo y *no* `LitFieldOfView`. Anotado
   porque el caso «el humo te envuelve la cara» está en el diseño del ataque.

---

## 12. Sistema de luz — la «Light Protection Zone»

### 12.1 Por qué `light_on` no vale

Un bit a 10 Hz (ADR-042) que sólo dice «hay una `Light` habilitada bajo el wieldable activo». No
sabe hacia dónde apunta, ni hasta dónde llega, ni si hay una pared, ni si la intensidad ha caído al
55 % por batería baja (`lowChargeDim`). Y no puede saberlo: es un bit.

### 12.2 Lo que sustituye: una zona geométrica

```
LightProtectionZone = { origen, dirección, alcance, semiángulo, válida }
```

- **origen / dirección**: la transform de la luz promovida por `TorchShadowCaster` — que ya es el
  único componente del proyecto que sabe autoritativamente cuál es la luz que el jugador lleva.
- **alcance / semiángulo**: los del `Light` de verdad (el spot de la manivela es 18 m / 55°),
  escalados por la intensidad efectiva, **no** por la nominal.
- **válida**: la luz está encendida **y** su intensidad efectiva supera un umbral. Un haz moribundo
  al 15 % no protege, y eso es tensión gratis.

El jugador está protegido si está dentro de su propia zona (siempre lo está, es su linterna) — es
decir, **la zona no protege el sitio, protege al portador**. El Smiler no puede entrar en el volumen
de la zona, y eso es lo que hace que la protección tenga forma en el espacio en vez de ser un flag.

### 12.3 Quién la calcula, y la única concesión de autoridad del diseño

La zona se calcula en el **cliente**, porque es quien tiene la `Light`, y se reporta al servidor
junto a la pose.

Es la única grieta de autoridad del sistema y hay que declararla, no esconderla. Se cierra con dos
guardas, ambas server-side:

1. **El servidor acota los parámetros.** Alcance y ángulo se clampean al máximo que cualquier fuente
   de luz del juego puede tener. Un cliente que reporte un cono de 500 m recibe uno de 25.
2. **El servidor cruza con la carga.** La carga de la linterna ya es autoritativa (vive en el item);
   una zona válida reportada con carga cero se ignora.

El riesgo residual es que un tramposo se declare protegido siempre. Es **exactamente el mismo
riesgo que el proyecto ya acepta y documenta** para `consume_item` (ADR-030, trust-the-client
asumido) y para la posición de los cofres; con una diferencia a favor: aquí el peor caso es que el
tramposo se haga inmune a **una** criatura en **su propia** partida. No es PvP y no toca la
persistencia.

### 12.4 El parpadeo ambiental (señal, nunca protección)

`Wg3LightCadence` ya decide por hash qué lámpara parpadea, a qué Hz y con qué fase. Lo que falta es
**un campo de influencia**:

```
influencia(lámpara) = f(distancia_por_el_grafo(lámpara, nodo_más_cercano_del_Smiler))
```

Y esa influencia **no** enciende un booleano: **empuja** los parámetros que ya existen. Sube
`flickerChance` efectivo, baja la frecuencia hacia el extremo lento, y mete caídas de intensidad más
profundas. El encargo pide explícitamente evitar `if (distance < X) flicker = true`, y la forma de
evitarlo no es añadir ruido a esa condición: es **no tener la condición**, porque el parámetro ya
era continuo y por hash.

Tres consecuencias buenas que salen solas:
- **Sigue siendo determinista**: la misma semilla y el mismo campo dan el mismo parpadeo.
- **Es impredecible para el jugador** sin ser aleatorio, porque una lámpara con `flickerChance` alto
  por hash reacciona antes que su vecina. Dos habitaciones no avisan igual.
- **Usa la distancia por el GRAFO**, no la euclídea: la sala de al lado con un muro por medio no
  parpadea y la que está al final del pasillo sí. Eso es información espacial de verdad.

Configurable por Inspector como pide el encargo: distancia, intensidad, frecuencia, duración,
aleatoriedad, sobre un `Wg3LightCadenceSettings` ampliado.

### 12.5 Lo que la luz ambiental NO hace

Nada. No protege, no molesta al Smiler, no le ralentiza. Sólo avisa. Y el hecho de que las luces
del techo estén encendidas y aun así entre es precisamente lo que enseña la regla.

### 12.6 Cerrar la contradicción de §5.1 con diseño, no con una nota

Para que «la luz me salva de uno y me delata al otro» sea una decisión y no una arbitrariedad, el
jugador tiene que poder identificar **qué** le persigue antes de decidir. Ya hay canal para eso y no
hace falta inventarlo: **el Smiler se anuncia con las luces del techo, y el robapieles nunca.** Si
las lámparas del pasillo empiezan a fallar, es el Smiler y hay que encender. Si no fallan, lo que
venga es de los que ven tu linterna a 38 m.

Eso convierte el parpadeo ambiental de «efecto de atmósfera» en **la interfaz de la decisión más
importante del juego**, y es la razón por la que §12.4 merece ser un sistema y no un `Coroutine`.

---

## 13. Sistema de batería

Lo mínimo posible sobre `CrankFlashlightWieldable`, que ya lo hace casi todo.

```
consumo_por_segundo = base / salud_batería × drenaje_smiler
drenaje_smiler ∈ {1.0, 2.0}   // 2.0 si el jugador está en zona de influencia del Smiler
```

- **La influencia es una zona del servidor**, derivada del campo (§10): dentro del radio del nodo
  más cercano más un margen. Viaja como un flag en el estado del jugador, no como un cálculo del
  cliente — así el consumo es autoritativo y no depende de lo que el cliente crea que ve.
- **El ×2 no toca la protección.** Es un requisito explícito del encargo y hay que blindarlo con un
  test: *«un jugador con la luz encendida en influencia del Smiler sigue siendo objetivo inválido
  con la carga al 1 %»*. La protección cae **sólo** cuando la carga llega a cero y la luz se apaga.
- **El aviso ya existe**: `lowChargeFraction` 0,15 y `lowChargeDim` 0,55 bajan la intensidad del haz
  cuando queda poco. Con la zona de protección escalada por intensidad efectiva (§12.2), **el
  círculo de seguridad se te encoge delante de los ojos**. No hace falta ninguna UI.

---

## 14. Sistema de manivela

El ciclo `ACCIONAR → LUZ → ACCIONAR → LUZ` ya está: mantener el uso da vueltas, cada vuelta entrega
18–22 s de luz (sorteados, «no se da la cuerda igual dos veces»), y dar cuerda te deja al 40 % de
velocidad — *«una mano ocupada: andas, no huyes»*.

**Lo nuevo es el fallo por proximidad.** La curva que propongo, y el razonamiento:

```
p_fallo_por_segundo = p_max × clamp01((d_umbral − d) / (d_umbral − d_min))^k
```

con `d` = distancia **por el grafo de propagación** al nodo más cercano.

- **`k = 2`** (cuadrática, no lineal). Con `k = 1` el fallo es perceptible desde lejos y se lee como
  «la linterna está rota». Con `k = 2` el fallo es raro hasta que el Smiler está encima, y entonces
  es frecuente: la curva cuenta la historia correcta.
- **`p_max` acotado** de forma que el peor caso dé una esperanza del orden de **un fallo cada 4–6 s**
  con el Smiler a bocajarro. Más que eso y la manivela deja de ser una herramienta.
- **Suelo de gracia**: **nunca dos fallos en menos de `t_gracia`** (~1,5 s). Ésta es la pieza que
  convierte «RNG injusto» en «tensión»: garantiza que siempre tienes tiempo material de volver a dar
  cuerda. Sin ella, dos tiradas malas seguidas son una muerte que el jugador no pudo evitar, y eso
  es exactamente lo que el encargo prohíbe.
- **Nunca falla en la ventana de ataque** (§18). Si el jugador enciende dentro de `Strike`, la luz
  prende. La ventana ya es la tensión; añadirle una tirada de dados sería quitarle al jugador la
  agencia justo en el momento en que el diseño se la acaba de dar.
- **Todo determinista y semilleado**, como el resto: `env_tuning` en las cuatro constantes.

---

## 15. Sistema de interacción con el entorno

Cada caso del encargo, con lo que hace falta:

| Caso | Solución | ¿Existe? |
|---|---|---|
| **Obstáculo** | Sale de la conductancia (§10.1): la sección libre alrededor del obstáculo es > 0 y a través es 0 | ✅ Gratis |
| **Techo** | El hueco superior de una columna es un nodo como cualquier otro; la flotabilidad lo favorece | ✅ Gratis |
| **Escalera** | Huecos consecutivos con solape vertical. `floors_at` ya resolvió el caso análogo para cuerpos | ✅ Gratis |
| **Agujero / pozo** | Dos huecos que se tocan en vertical. ADR-126 los genera | ✅ Gratis |
| **Hueco estrecho** | La conductancia es proporcional a la sección: el humo pasa **despacio**, que es justo lo que se quiere ver | ✅ Gratis |
| **Conducto** | Un hueco donde un cuerpo de 1,8 m no cabe y un gas de 0,25 m sí | ⚠️ Depende de ADR-123 (§5.3) |
| **Puerta abierta / cerrada** | **NO HAY NADA.** Los vanos de WG3 son huecos sin hoja; las puertas construidas por jugadores las abre el vendor en el cliente y no viajan | ❌ §15.2 |

### 15.1 Lo que quiero subrayar

**Siete de las ocho interacciones que el encargo pide salen de una sola decisión** — que la
conductancia entre dos nodos sea proporcional a la sección libre. Ninguna es un caso especial,
ninguna necesita un marcador en el mundo, ninguna puede olvidarse en una rama de la FSM.

Eso es lo que hace que este modelo valga la pena frente a una IA que persigue waypoints, y es el
criterio con el que hay que juzgar cualquier alternativa que se proponga: **cuántos de los ocho
casos necesitan código propio.**

### 15.2 El único que no sale gratis: las puertas

Hoy no existe el concepto «puerta con estado» en el servidor. Tres vías, para que Joel elija:

- **(a) No hacerlo en v1.** La puerta es un hueco ancho y el humo pasa. El requisito se pospone. Es
  lo más barato y no rompe nada.
- **(b) Las puertas construidas por jugadores informan su ángulo al servidor.** Es un canal nuevo,
  ADR y probablemente bump de wire, para cubrir sólo las puertas que un jugador ha puesto. Alcance
  pequeño, valor pequeño.
- **(c) WG3 genera puertas con hoja** en algunos vanos, con estado autoritativo. Es un sistema de
  mundo entero, con su ADR, su wire y su semana. **Vale mucho más que por el Smiler** (cerrar una
  puerta detrás de ti es mecánica de survival horror en sí misma) pero no es este trabajo.

**Mi recomendación: (a) en el prototipo, y (c) como ADR propio cuando toque.** Meter (b) es pagar el
coste de un ADR de wire por un caso que casi nunca se da.

---

## 16. Sistema de sonido

### 16.1 El estado real, que es peor de lo que parece

`AUDIO-PROPAGATION-ROADMAP.md`, medido: **todo `AudioSource` con `maxDistance` explícito del
proyecto vive entre 7 y 18 m**. No existe nada lejano. La oclusión (`AudioOcclusionMath`) es un rayo
recto que cuenta paredes y **no dobla esquinas**.

Para una criatura cuya mitad del terror es que la oyes antes de verla, eso es un problema de fondo.

### 16.2 Las cuatro capas

1. **Flujo cercano** (≤ 15 m, espacial, en el nodo más denso) — el sonido del humo moviéndose.
   Volumen atado a `density`, tono atado a la **velocidad de cambio** del campo: un humo quieto
   susurra, un humo que se derrama por un pozo ruge. Que el tono salga de la derivada del campo y
   no de un estado es lo que hace que el sonido cuente lo que está pasando.
2. **Dirección estructural** — el flujo se emite desde el nodo, así que si el nodo está en el hueco
   del techo, **suena arriba**. No hace falta un sistema de «sonidos laterales»: sale de que la
   representación sea espacial.
3. **Presencia lejana** (`Presence`/`Propagation`, > 20 m, 2D, bus Music) — una capa sin posición,
   copiando `ChaseAudioLayer`: sintetizada, autoarrancable, borrable, y **leyendo sólo lo que es
   público**. Esto sí resuelve la petición del encargo de tensión sin visibilidad, y **sin** esperar
   al roadmap de propagación de audio.
4. **El silencio previo al ataque** — al entrar en `Strike`, las capas 1 y 3 se cortan con un
   release rápido y **el ambiente entero baja**. Un corte de sonido es más fuerte que cualquier
   sonido, y es gratis.

### 16.3 La regla de oro, heredada

`ChaseAudioLayer` lee **sólo** `revealed` y distancia, a propósito, *«reading anything private would
make this an oracle»*. El Smiler hereda la disciplina entera: la capa de audio puede leer el campo
de presencia (que es público, ya viaja y se está dibujando en pantalla) y **nunca** la intención, el
temperamento ni el objetivo. Un jugador con los cascos puestos no debe poder saber a quién está
cazando.

### 16.4 Reacción a la luz

Al entrar `Retreat` por luz: un sonido propio, de retracción, **descendente y alejándose**. Es la
confirmación auditiva de que la regla funcionó, y en un juego donde la regla es absoluta, esa
confirmación es lo que enseña la regla sin un tutorial.

---

## 17. Sistema de persecución

**Sí hay persecución, y no hay velocidad de la criatura.** Hay velocidad **del frente**, que es un
concepto distinto: el Smiler no se desplaza, se propaga por un lado y se retrae por otro.

```
LATENT    frente ~0,3 m/s   deriva
APPROACH  frente ~1,0 m/s   entra en el espacio
HUNT      frente ~2,0 m/s   ritmo de andar
ACQUIRE   frente ~3,0 m/s   te alcanza si andas
CHASE     frente ~4,5 m/s   por debajo del sprint del jugador
ATTACK    explosiva, y sólo en el eje hacia el jugador
```

Cinco decisiones que van con eso:

1. **El `CHASE` es más lento que el sprint del jugador, a propósito.** Correr **funciona**. Lo que
   no funciona es correr sin saber adónde, porque el Smiler no te sigue: te rodea. Si el juego
   convierte el Smiler en «más rápido que tú», la única mecánica que queda es la linterna y el
   espacio deja de importar.
2. **La velocidad es del FRENTE, no del centro de masa.** El frente avanza rápido y la cola tarda.
   Eso es lo que hace que un Smiler que te persigue por un pasillo **ocupe el pasillo**, en vez de
   ser una nube que viaja.
3. **Las rutas alternativas son gratis.** Un campo de difusión con varios nodos ya reparte por
   todos los caminos que hay. Que te corte el paso por el otro lado del pasillo no es una táctica
   que haya que programar: es la consecuencia de que el humo se difunda.
4. **Curva de aceleración, no escalón.** Interpolación suave entre velocidades con una constante de
   tiempo del orden de 1,5 s — mismo orden que `PHANTOM_SPRINT_RAMP`. Un frente que cambia de
   velocidad de golpe se lee como un bug.
5. **Rendirse.** Como el robapieles: paciencia por temperamento, y al agotarse, `Retreat`. Con la
   pieza que ya existe de `phantom.rs`: recordar dónde se perdió la caza (`hideouts`), porque un
   Smiler que vuelve a mirar donde te escondiste la última vez es más terrorífico que uno más
   rápido.

---

## 18. Sistema de ataque

### 18.1 La transición sin teleport

El encargo pide que el humo desaparezca del campo de visión y reaparezca encima, sin teleport
visible. Con un campo de nodos sale de forma natural y **sin ningún caso especial**:

```
 1. El Smiler ya tiene nodos DETRÁS del jugador (la difusión los puso ahí hace rato)
 2. Entra `Strike`:
    - los nodos del campo de VISIÓN pierden densidad rápido  → «desaparece»
    - los nodos de DETRÁS la ganan                            → «está detrás»
 3. Suena (§16.4)
 4. El nodo de detrás supera el umbral de cara → ojos y sonrisa emergen
 5. Ventana de N segundos
 6. Luz → Retreat · nada → daño → Retreat
```

**No hay teleport porque nada se ha movido**: sólo ha cambiado el reparto de densidad de un campo
que ya estaba ahí. Y hay un requisito previo que lo hace honesto: **`Strike` sólo puede entrar si
ya hay nodos detrás del jugador**. Si no los hay —porque estás de espaldas a una pared— el Smiler no
puede atacar por detrás, y ahí el diseño te acaba de dar una táctica sin que nadie la escribiera.

### 18.2 La ventana

- **Duración**: del orden de 1,5–2,5 s, sorteada y `env_tuning`. Suficiente para reaccionar,
  insuficiente para pensarlo.
- **Cancelable sólo con luz.** No con moverse, no con atacar. La regla es absoluta y esto es lo que
  la enseña en el peor momento posible, que es cuando se aprende.
- **La manivela no falla dentro de la ventana** (§14).
- **Un solo `Strike` por encuentro**, con cooldown. Como `ambushed_this_hunt` del robapieles: un
  truco que se repite deja de serlo.

### 18.3 El daño

Autoritativo en el servidor, por el carril existente. `PhantomAttackHandler` ya hace el trabajo de
la cinemática de agarre en el cliente y `FacelingDazeEffect` el de las secuelas; la forma está
resuelta y no hay que inventarla.

**Qué hace el ataque exactamente** (¿mata? ¿tumba? ¿roba? ¿drena cordura?) **es decisión de Joel** y
es lo único de este documento que no puedo recomendar desde el código: depende del game loop, no de
la arquitectura. Lo que sí digo es que con `SanityEffects` ya presente en el cliente, un Smiler que
te deja vivo y con algo roto encaja mejor en un survival persistente que uno que te mata — el
robapieles ya cubre el nicho de «lo que te mata».

---

## 19. Estrategia multijugador

### 19.1 El presupuesto real, medido

De `STATE.md` y de la 53.ª/54.ª tanda, todo medido y no estimado:

- **El techo es la SUBIDA del host**, no la CPU (3,0 ms/ronda con 50 jugadores juntos).
- Con 50 juntos: **~320 KB/s** de régimen tras P0–P4, desde 4 786 KB/s.
- Pose delgada **23 B** (ADR-144), `MAX_POSES_PER_BATCH` 8, datagrama ≤ **1 200 B** (ADR-113).
- Cadencia 9,2 Hz con 50 juntos; el objetivo de Joel es ≤ 100 ms (suelo ~15 Hz).

### 19.2 Lo que cabe

| Opción | Coste a 10 Hz | Veredicto |
|---|---|---|
| Partículas replicadas | miles de B/tick | ❌ Impensable |
| Un transform por Smiler | 23 B | ❌ No representa una masa distribuida |
| **Campo de 8 nodos** (§7.3) | **~90 B → 0,9 KB/s por observador** | ✅ **0,3 % del presupuesto** |

Y con tres reductores que **ya existen y se aplican solos**:

1. **PVS** (ADR-140, encendido, `ad7d3c57`) — oculta el 24,3 %. Un Smiler en otra planta no viaja.
2. **Cadencia por distancia** (ADR-074 enm. 3) — de lejos va a menos Hz. Perfecto para un humo, que
   no tiene pose que interpolar.
3. **Delta** — un nodo que no cambia no se manda.

**Guarda de ADR-074 que hay que respetar:** el filtro **no puede distinguir la fuente**. Si el campo
del Smiler entra en el relay, entra con las mismas reglas de aforo y cono que todo lo demás, sin
excepción y sin un carril propio. Es una regla explícita de `STATE.md`.

### 19.3 Autoridad, y los dos casos raros

- **El Smiler lo simula el host**, como todas las criaturas. Se desvanece con el host (ADR-016).
- **Dos jugadores, uno con luz y otro sin.** El Smiler ignora al iluminado y caza al otro. Sale solo
  de que la protección sea por jugador y no por zona, y es buena mecánica cooperativa: **el que
  tiene luz es el que puede ir a buscarte**.
- **El jugador protegido se mete entre el Smiler y su objetivo.** El Smiler no puede entrar en la
  zona: lo rodea. Otra vez gratis, y otra vez es mecánica emergente que nadie escribió.

---

## 20. Estrategia de rendimiento

### 20.1 Presupuestos que propongo (a confirmar midiendo, no antes)

| Recurso | Presupuesto | De dónde sale |
|---|---|---|
| Tick de backend por Smiler | **≤ 0,5 ms** | El bloque de criaturas ya compite con todo lo demás en 16,67 ms |
| Nodos | **≤ 8** | §19 |
| Smilers activos | **1** | §6.3 |
| Rásteres precalentados | Los que ya cachea `Wg3CollisionCache` | No añadir un caché nuevo |
| Partículas cliente | **≤ 2 000** en total | Sin medida previa en este proyecto: es una hipótesis |
| Luces del Smiler | **≤ 1**, sin sombras | `Wg3ShadowBudget` y el tope de 256 |
| Wire | **≤ 1 KB/s** por observador | §19.2 |

### 20.2 Las tres lecciones del proyecto que aplican directamente

1. **«Debug miente en CPU.»** El mismo arnés dio «muro CPU a 48» en debug y 64 en release. **Todo
   techo del Smiler se mide con `cargo test --release … -- --ignored --nocapture --test-threads=1`**
   o no se ha medido.
2. **«Una traza que no puede ver el caso que busca es peor que ninguna, porque parece una medida.»**
   (`faceling.rs`, `SYNCTRACE`). El arnés del Smiler tiene que capturar el **peor caso acumulado**,
   no una muestra al pasar: los ticks caros son raros.
3. **El caché mal dimensionado que garantizaba no acertar nunca** (`MAX_CACHED_RASTERS`, 7 531 ms en
   un tick). Antes de escribir un caché propio, medir si el que hay basta.

### 20.3 Degradación

Con el presupuesto agotado, en este orden: menos partículas → menos nodos visuales (el campo sigue
igual) → cadencia más baja de difusión → el Smiler pasa a `Latent`. **Nunca `SetActive(false)`**: es
el requisito del encargo, y con esta lista se cumple sin excepciones.

---

## 21. Riesgos técnicos

Ordenados por probabilidad × daño.

| # | Riesgo | Daño | Mitigación |
|---|---|---|---|
| **R1** | **El humo no se lee como humo con `ParticleSystem`** y hace falta VFX Graph igualmente | Alto | §22 valida la propagación con esferas de depuración ANTES de invertir en lo visual. Si hay que instalar VFX Graph, se instala con el problema real delante |
| **R2** | **El grafo de propagación se come el tick.** 6 vecinas × varias bandas por columna es más caro que el A\* de cuerpos | Alto | Ventana en metros pequeña (§9.4), tope de expansiones, arnés en release desde el día 1 |
| **R3** | **El humo se queda atascado empujando contra un forjado**, y como es un gas no se ve que está atascado | **Muy alto** | Es el bug de los facelings otra vez (`FACELING-ROADMAP` §2.1). Test de conectividad de la propagación, sonda de flujo neto, y **la percepción con oclusión desde el primer commit** |
| **R4** | **La regla absoluta de la luz se lee como un bug.** «Estaba encima y ha desaparecido» | Alto | La retirada tiene que ser LARGA y audible (§16.4). Si es instantánea, parece que el juego se ha roto |
| **R5** | **La zona de protección reportada por el cliente se puede falsear** | Medio | §12.3: clamp del servidor + cruce con la carga. Riesgo residual declarado y comparable al que el proyecto ya acepta |
| **R6** | **ADR-128 (mundo ×2) cambia el significado de toda constante en metros** | Medio | Todo en `env_tuning`; ninguna constante espacial del Smiler entra en un golden |
| **R7** | **La contradicción de la luz confunde al jugador** (§5.1) | Medio | §12.6: el parpadeo ambiental como interfaz de la decisión. Y playtest específico de ese caso |
| **R8** | **El campo no es determinista** por iterar un `HashMap` | Medio | Regla dura 13. Ordenar antes de emitir, y un test que lo vigile |
| **R9** | **Las puertas no existen** (§15.2) | Bajo | Vía (a) en v1 |
| **R10** | **Los ADR del robapieles no se encuentran** (§4.8) | Bajo pero insidioso | Anotado. El ADR del Smiler cita por número y por **línea** de `DECISIONS.md` |

---

## 22. Plan de prototipo — `SmilerSandbox`

Cinco fases. **Cada una cierra en verde antes de la siguiente**, como la semana de cierre de WG3.
Ninguna fase toca el mundo servido: el sandbox es una escena y un arnés, igual que
`Wg3LightCadenceRig`.

### Fase 0 — El arnés (½ día)

Escena `SmilerSandbox` con tres mundos WG3 de semillas distintas lado a lado (el patrón exacto de
`Wg3LightCadenceRig`, que ya resolvió por qué el desplazamiento va **después** de generar), con
escalera, pozo, atrio y pasillo estrecho. Vista cenital de depuración del campo. Sin Smiler todavía.

**Verde cuando:** se puede mirar una habitación, apretar un botón y ver los huecos del ráster
pintados en 3D. Sin eso, todo lo demás se depura a ciegas.

### Fase 1 — La propagación, con esferas grises (2 días) ← **la fase que importa**

`smiler::flow` sobre el `Wg3Raster`. Difusión, conductancia por sección, flotabilidad, fusión y
partición. **Cero visual**: 8 esferas grises de depuración con el radio del nodo.

**Verde cuando:**
- El humo soltado abajo llega arriba **por la escalera**, no atravesando el forjado.
- Rodea un pilar.
- Se derrama por un pozo.
- Cruza una rendija **despacio** y un vano ancho **deprisa**, sin que nadie haya escrito la
  diferencia.
- **El escenario del encargo entero** (Smiler abajo → pasillo → escalera → rellano → pasillo →
  jugador) en un mundo generado, no autorado.
- Coste medido **en release** dentro de 0,5 ms.

**Ésta es la fase que decide si el proyecto es viable.** Si la propagación no sale aquí, el resto no
merece empezarse — y habremos gastado dos días, no dos semanas.

### Fase 2 — La luz (1 día)

`LightProtectionZone` de verdad, sobre `TorchShadowCaster`. Veto absoluto. `Retreat` físico.
Drenaje ×2. Fallo de manivela con su curva y su suelo de gracia.

**Verde cuando:** enciendes y el campo **se retira por la geometría** —sin desaparecer, sin
teleport— y la carga baja al doble. Y cuando el test de «carga al 1 % sigue protegiendo» está en
verde.

### Fase 3 — El encuentro (2 días)

La FSM de seis estados, percepción con oclusión, ruido, parpadeo ambiental por influencia, ventana
de ataque.

**Verde cuando:** un encuentro completo se juega de principio a fin y **el parpadeo llega antes que
el humo**.

### Fase 4 — Lo visual y lo sonoro (2–3 días)

Partículas, shader, cara, las cuatro capas de audio. **Aquí y sólo aquí** se decide VFX Graph, con
la propagación ya validada y con un caso real delante.

### Fase 5 — Multijugador y presupuesto (1 día)

El campo por el wire, dos clientes, medición contra el presupuesto de §20.

**Total: ~9 días.** La decisión de seguir o parar se toma al final de la Fase 1, que son dos.

### Lo que el sandbox NO hace

No toca `phantom.rs`, no toca el mundo servido, no bumpea el wire hasta la Fase 5, no instala
paquetes hasta la Fase 4, y no entra en `main`/`migration/worldgraph-v1` hasta que Joel lo vea
en Play.

---

## 23. Criterios objetivos de éxito

Medibles, sin «se ve bien».

### Propagación
- **C1** — Sin un solo caso en 100 pruebas de que un nodo aparezca dentro de un span macizo del
  ráster. *Automático.*
- **C2** — Con jugador en planta N+1 y Smiler en planta N con escalera conectándolos, el humo llega
  en **≤ 20 s** en el 95 % de 100 semillas. *Automático.*
- **C3** — Mismo escenario **sin** conexión transitable: el humo **no** llega, nunca. *Automático.*
- **C4** — El caudal por una rendija de 0,5 m es **< 25 %** del caudal por un vano de 2 m.
  *Automático.*
- **C5** — Determinismo: misma semilla + mismos estímulos ⇒ campo byte a byte idéntico a los 60 s.
  *Automático (regla dura 13).*

### Luz
- **C6** — Con luz válida el Smiler **nunca** entra en el volumen de la zona: 0 en 1 000 ticks de
  presión continua. *Automático.*
- **C7** — Al encender, `Retreat` empieza en **≤ 1 tick** y el humo **sigue visible ≥ 2 s** mientras
  se va. *Automático.* (C7 es lo que separa «regla absoluta» de «bug».)
- **C8** — Protección al **1 %** de carga idéntica a la del 100 %. *Automático.*
- **C9** — Drenaje en influencia = **2,00 ×** ± 2 %. *Automático.*

### Manivela
- **C10** — Con el Smiler a bocajarro, **nunca** dos fallos en < `t_gracia`, en 10 000 tiradas.
  *Automático.*
- **C11** — Cero fallos dentro de la ventana de ataque, en 1 000 ventanas. *Automático.*

### Encuentro
- **C12** — El parpadeo ambiental precede al primer humo visible en **≥ 8 s** en el 90 % de los
  encuentros. *Automático.*
- **C13** — `Strike` **nunca** entra sin nodos detrás del jugador. *Automático.*
- **C14** — Cero `SetActive(false)`, cero teleports, cero despawns instantáneos en todo el ciclo.
  *Automático — un test que recorra las transiciones y falle si alguna salta el `Retreat`.*

### Rendimiento
- **C15** — ≤ **0,5 ms**/tick en release con 1 Smiler y 4 jugadores. *Medido en release, nunca en
  debug.*
- **C16** — ≤ **1 KB/s** por observador. *Medido con el arnés de red que ya existe.*
- **C17** — Sin caída medible de frametime en el cliente frente a la misma escena sin Smiler, contra
  `docs/perf/PERF_AUDIT_v1.md`.

### Lo subjetivo, y cómo se mide igualmente
- **C18** — Joel juega un encuentro completo sin que nadie le explique nada y, al acabar, **dice sin
  que se le pregunte** que la luz le salvó. Si hay que explicárselo, el sistema no comunica y se
  vuelve a §12.6.

---

## Lo que queda pendiente de Joel antes de escribir una línea de código

1. **§5.1** — ¿La luz protege del Smiler y delata al robapieles? Es la decisión de diseño más
   grande de todo el documento.
2. **§5.3** — ¿El Smiler entra en los conductos, rompiendo la promesa de ADR-123 D3?
3. **§11.2** — ¿VFX Graph? (mi recomendación: decidirlo en la Fase 4, no ahora).
4. **§15.2** — Puertas: (a), (b) o (c).
5. **§18.3** — ¿Qué hace exactamente el ataque?
6. **§22** — ¿Se aprueba el plan de 5 fases, con la puerta de decisión al final de la Fase 1?

**No se implementa nada hasta que estas seis estén contestadas y el ADR correspondiente escrito.**
