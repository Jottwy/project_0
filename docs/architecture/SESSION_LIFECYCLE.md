# Unity Session Lifecycle

> Escrito el 2026-08-30 durante la tanda T6 (lifecycle + UI/UX hardening), contra el código real
> y contra el vendor. Describe lo que el sistema HACE, no lo que se pretendía que hiciera.
>
> Compañeros: [SESSION_INVARIANTS.md](SESSION_INVARIANTS.md) (las reglas, en una lista que se
> puede comprobar) y [NETWORKING_AND_SESSION_ARCHITECTURE.md](NETWORKING_AND_SESSION_ARCHITECTURE.md)
> (dónde encaja esto en la red). El transporte y el reparto de roles siguen documentados en
> [../NETWORK_ARCHITECTURE_CURRENT.md](../NETWORK_ARCHITECTURE_CURRENT.md); aquí no se repiten.

## 1. Las fases

`BackroomsSurvival.Net.SessionPhase` — `Assets/Scripts/Network/SessionStateMachine.cs`.

| Fase | Significa | Panel | Cursor |
|---|---|---|---|
| `Menu` | No hay sesión. | Oculto o de arranque | libre |
| `Starting` | Se pidió lanzar el backend. | «Starting host…» / «Connecting to ip:port…» | libre |
| `Connecting` | Backend lanzado; falta IPC (host) o `session_joined` (joiner). | igual + contador | libre |
| `Connected` | Sesión establecida **de verdad**. | oculto | capturado |
| `InGame` | La escena de juego está cargada y activa. | oculto | capturado |
| `Disconnecting` | Teardown en marcha. | bloqueado | libre |
| `Disconnected` | La sesión terminó (con motivo). | «Session ended: …» + [Retry] [Back to menu] | libre |
| `Failed` | El intento no llegó a establecerse. | «Could not connect: …» + [Retry] [Back to menu] | libre |

`Menu`, `Disconnected` y `Failed` son las tres fases OCIOSAS: desde las tres se puede arrancar
(`CanStart`). Desde ninguna otra.

## 2. El grafo

```
                      RequestStart(role)
        Menu ─────────────────────────────────► Starting
   Disconnected ──────────┘                        │ NotifyBackendLaunched
       Failed ────────────┘                        ▼
                                              Connecting
                                       ┌──────────┴───────────┐
              NotifyIpcConnected       │                      │  NotifySessionJoined
              (SOLO host/autosolo)     │                      │  (SOLO joiner)
                                       └──────────┬───────────┘
                                                  ▼
                                              Connected
                                                  │ NotifyEnteredWorld(scene)
                                                  ▼
                                                InGame
                                                  │
      NotifyFailed(reason)                        │ RequestLeave(reason)
      (solo desde Starting/Connecting)            ▼
              │                             Disconnecting
              ▼                                   │ NotifyLeaveComplete(keepReason)
           Failed ◄───────────────────────────────┤
                                                  ▼
                    keepReason=false → Menu
                    keepReason=true  → Disconnected  si WasEstablished
                                     → Failed        si NO WasEstablished

    Disconnected/Failed ──AcknowledgeAndReturnToMenu()──► Menu   (sin tocar recursos)
```

**Dos decisiones, no una.**

- **`keepReason`** distingue salida voluntaria de final ajeno. `Quit to Menu` y el botón
  [Back to menu] → `Menu`, sin panel: el menú del juego ya es la UI y anunciar «Sesión terminada»
  encima sería ruido. Host que se va, backend muerto, conexión perdida → panel con el motivo.
- **`WasEstablished`** parte ese panel en dos, y esto sí es la diferencia que el jugador necesita
  leer: **nunca entró** (IP mal tecleada, host apagado, `CONNECT_TIMEOUT`) → `Failed`,
  «Could not connect: …»; **entró y se perdió** → `Disconnected`, «Session ended: …».
  Importa porque el camino real de un timeout de join **no** pasa por `NotifyFailed`: el backend
  agota su presupuesto y manda `session_ended`, que entra por el teardown normal. Sin esta
  distinción, una IP equivocada terminaba diciendo «Session ended».

## 3. Quién es dueño de qué

La regla que hacía falta escribir: **una sola cosa es dueña de cada estado, y el resto lee.**

| Estado | Dueño | Fichero |
|---|---|---|
| Proceso backend, rol, config de lanzamiento | `NetworkInitializer` | `Assets/Scripts/Network/NetworkInitializer.cs` |
| Socket IPC, `IsConnected`, epoch | `IPCClient` | `Assets/Scripts/Network/IPCClient.cs` |
| **Fase de sesión** | `SessionStateMachine` (vía `SessionState.Current`) | `Assets/Scripts/Network/SessionStateMachine.cs` |
| Teardown y vuelta al menú | `SessionEndHandler` | `Assets/Scripts/Network/SessionEndHandler.cs` |
| Panel de conexión | `JoinSessionUI` | `Assets/Scripts/UI/JoinSessionUI.cs` |
| Cursor **en las transiciones de sesión** | `SessionCursor` | `Assets/Scripts/Network/SessionCursor.cs` |
| Cursor **durante la partida** | el vendor (`GameMode`, `UIInput`, `InventoryInspectionManager`) | `Assets/PolymindGames/…` |

`SessionState` es **static de proceso**, no un componente: los tres puntos de entrada
(`AutoConnect`, `NetworkMenuBootstrap`, `JoinSessionUI`) construyen sus objetos en runtime y en
orden distinto según por dónde se entre, así que colgar la fase de cualquiera de ellos la haría
depender de quién se construyó primero. Se reinicia por
`RuntimeInitializeOnLoadMethod(SubsystemRegistration)`, igual que `IPCClient.ResetStatics`.

## 4. Quién dispara cada transición

| Transición | Disparador | Dónde |
|---|---|---|
| `RequestStart` | `NetworkInitializer.StartAsHost` / `StartAsJoiner` | el **embudo**: los ocho caminos de arranque pasan por ahí |
| `NotifyBackendLaunched` | `LaunchBackendProcess`, tras `Process.Start` | |
| `NotifyIpcConnected` | `NetworkInitializer.UpdateBackendStartup` | no mueve la fase de un joiner, a propósito |
| `NotifySessionJoined` | evento IPC `session_joined` | `SessionEndHandler.OnGameEvent` y `JoinSessionUI.OnSessionEvent` — el mismo método idempotente |
| `NotifyEnteredWorld` | `SceneManager.activeSceneChanged` | `SessionEndHandler.OnActiveSceneChanged` |
| `NotifyFailed` | exe ausente, `Process.Start` falló, backend muerto, timeout de arranque, backstop de handshake | `NetworkInitializer` |
| `RequestLeave` | `session_ended`, botón Disconnect, salida de la escena de juego, backend muerto en partida | `SessionEndHandler.TeardownNetworkResources` |
| `NotifyLeaveComplete` | final del teardown | idem |
| `AcknowledgeAndReturnToMenu` | botón [Back to menu] | `JoinSessionUI` |

## 5. El teardown, en orden

`SessionEndHandler.TeardownNetworkResources`. El orden **no es arbitrario**:

1. **`RequestLeave` primero.** Es el gate de idempotencia: si devuelve `false` no había sesión
   viva (o ya hay un teardown en marcha) y **no se toca nada más**.
2. **Matar el backend con el IPC todavía arriba.** `NetworkInitializer.Shutdown()` manda
   `save_and_shutdown` por esa conexión y espera una salida limpia antes de recurrir a `Kill()`.
   Parar el IPC antes costaría el guardado (ADR-032).
3. **Aparcar el IPC** (`PauseReconnect`). El puerto está muerto; sin esto el bucle marca ese
   puerto el resto de la vida del proceso. **No** `IPCClient.Shutdown()`, que se lleva el
   singleton y el hilo — el cliente tiene que seguir reutilizable.
4. **Cerrar el lobby de Steam**, que publica un `ip:puerto` que acaba de morir.
5. **Rearmar el panel** (`JoinSessionUI.ResetForNewSession`). **Sólo campos**: destruirlo dejaría
   que el siguiente `ShowConnectPanel` construyera una instancia nueva cuyo `Start()` repite el
   auto-connect de `SESSION_MODE`/`CONNECT_TO`, en bucle.
6. **Soltar el cursor** (`SessionCursor.ReleaseToMenu`), haya panel o no.
7. **Cerrar la máquina** (`NotifyLeaveComplete`). A partir de aquí `CanStart` vuelve a ser `true`.
8. **Volver al menú**, si se pidió y no estamos ya allí.

Cada paso 2–6 va envuelto en `Step(...)`: se registra y se continúa. Uno que falle no puede
saltarse los siguientes — el objetivo del teardown es no dejar al jugador en un mundo muerto.

## 6. El enganche de escena (el `Quit to Menu` del vendor)

`PauseMenu.QuitToMenu()` llama a `LevelManager.CloseCurrentGame` **a pelo, sin tocar nada de
red**, y el vendor no se edita. El enganche es `SceneManager.activeSceneChanged` en
`SessionEndHandler`:

- fase `Connected` + cambio de escena ⇒ `NotifyEnteredWorld(escena)`. La sesión está en el mundo.
- fase `InGame` + escena distinta de `WorldScene` ⇒ **es una salida**: teardown completo, sin
  pedir otra carga de escena (el menú ya se está cargando) y sin panel de aviso.

Se compara contra la escena en la que se ENTRÓ, no contra el nombre del menú: el
`SerializedScene` del `PauseMenu` es un campo suyo y puede no coincidir con el `_mainMenuScene`
de este componente. De lo que sí hay certeza es de dónde estábamos.

## 7. Cursor / input

La regla entera está en `SessionCursor.ShouldLock(menuVisible, phase)` y es una función pura:

```
panel visible          → cursor LIBRE, sea cual sea la fase
panel oculto + InWorld → cursor CAPTURADO   (InWorld = Connected | InGame)
panel oculto + resto   → cursor LIBRE
```

El fallo que esto mata: `JoinSessionUI.HideMenu()` capturaba el cursor **siempre**, también al
ocultar el panel estando en el menú. Con el `Quit to Menu` del vendor —que dejaba la fase en
`Connected` porque nadie la bajaba— eso significaba volver al menú con el ratón preso sobre una
pantalla que sólo se usa con el ratón.

Durante la partida el cursor **sigue siendo del vendor**: `GameMode` lo captura al aparecer el
personaje, `UIInput` y `InventoryInspectionManager` lo sueltan y lo recuperan al abrir y cerrar
sus paneles. `SessionCursor` no compite con ellos: sólo escribe en las transiciones del ciclo de
sesión, que es donde no mandaba nadie.

## 8. Errores y reconexión

| Caso | Cómo termina | Qué ve el jugador |
|---|---|---|
| Host inexistente / IP o puerto equivocados | `CONNECT_TIMEOUT` del backend (15 s) → `session_ended` con motivo | `Failed` + motivo + [Retry] [Back to menu] |
| Host apagado a mitad | `session_ended` (despedida o timeout de latido) | `Disconnected` + motivo |
| Backend no arranca (exe ausente, `Process.Start` falla) | `NotifyFailed` inmediato | `Failed` + motivo |
| Backend muere durante el arranque | `UpdateBackendStartup` lo ve | `Failed` + código de salida |
| Backend muere después de conectar | `WatchBackendLiveness` (cada frame mientras la fase esté viva) | `Failed` o `Disconnected` según fase |
| El aviso autoritativo se pierde | backstop de `joinerHandshakeTimeout` (25 s > los 15 del backend), **registrado como BACKSTOP** | `Failed` |

Ningún timeout de cliente tapa un error real: el único que existe está **por encima** del
autoritativo y grita en el log que lo es.

## 9. Repetibilidad

`MENU → JOIN → CONNECT → GAME → MENU → JOIN → …` y `MENU → HOST → … → MENU → HOST → …` se pueden
repetir indefinidamente porque cada vuelta:

- termina el proceso backend anterior (`Shutdown`, y `TerminateLeftoverBackend` como red de
  seguridad al arrancar la siguiente);
- suelta el gate del joiner, el rol y la escena (`NotifyLeaveComplete`);
- deja `_loadingGameplay` en `false` (`JoinSessionUI.ResetForNewSession`), que era el latch que
  impedía que la SEGUNDA sesión cargara escena alguna;
- da al intento siguiente una `Generation` nueva, con la que se descartan los callbacks tardíos
  del backend anterior.

Cubierto por `Assets/Tests/EditMode/SessionLifecycleTests.cs`
(`JoinDisconnectJoinFiveTimesLeavesNoResidue`, `HostDisconnectHostFiveTimesLeavesNoResidue`).

## 10. Lo que estos tests NO prueban

Corren sin editor, sin escena y sin backend: prueban las REGLAS. **No** prueban el cableado a
Unity — que `activeSceneChanged` dispare de verdad con el `Quit to Menu` del vendor, que
`Process.Exited` llegue, que el backend suelte sus sockets. Eso se comprueba a mano con el
procedimiento del §11 y con `tools/dev/CheckOrphanBackends.ps1`.

## 11. Procedimiento manual de verificación

```powershell
tools/dev/CheckOrphanBackends.ps1 -Label antes -ExpectCount 0
```

1. **Rejoin ×5** — Menú → Multiplayer → Join → jugar → Esc → Quit to Menu → Multiplayer → Join…
   Tras cada vuelta: `CheckOrphanBackends.ps1 -ExpectCount 0`, el cursor visible en el menú, y
   los botones Host/Join presentes y pulsables.
2. **Host ×5** — lo mismo con Host.
3. **Timeout → retry** — Join a `192.0.2.1:7778` (rango de documentación, nunca contesta).
   A los ~15 s el panel tiene que decir `Could not connect: …` con el motivo del backend, y
   [Retry] tiene que volver a intentarlo sin reescribir nada.
4. **Backend muerto** — durante la partida, `Stop-Process -Name backrooms_server -Force`.
   El panel tiene que aparecer con el motivo y el cursor libre; nunca quedarse en el mundo.
5. **Doble clic** — pulsar Join dos veces seguidas. En la consola:
   `[NetworkInitializer] Join ignorado: ya hay sesion en fase Starting`, y **un solo** PID nuevo.

Cierre: `CheckOrphanBackends.ps1 -Label final -ExpectCount 0`.

## 12. El Server Browser dentro del ciclo

El navegador de servidores es **un consumidor más** de este ciclo, por los dos lados: como cliente
que pide entrar, y como host que se anuncia. No lo extiende, no lo esquiva y no añade fases.

### Como cliente: preguntar antes, mirar la fase después

**Antes de pedir entrar, pregunta.** `ServerBrowserUI` lee `SessionState.Current.CanStart` y se lo
pasa como parámetro a `ServerBrowserViewModel.RequestJoin`. El navegador **no** guarda su propia
idea de si hay sesión viva —que es el fallo de tres criterios distintos que esta máquina de estados
vino a cerrar—. Con `CanStart` en falso el intento se rechaza antes de tocar el camino de conexión:
cero backends, cero llamadas a `NetworkInitializer` (hay test contra `BackendLaunchCount`).

**Después, mira la FASE para saber cómo acabó.** No inventa un estado propio ni se suscribe a
eventos del backend:

| Fase observada | Qué hace el navegador |
|---|---|
| `Starting` / `Connecting` | esperar, con el panel escondido y el de conexión a la vista |
| `Connected` / `InGame` | cerrarse; el mundo manda |
| `Failed` / `Disconnected` / `Menu` | volver a un estado **reutilizable** y soltar el intento |

Ese último renglón es la regla que impide el botón *Entrar* muerto. **El error no lo pinta él**:
quien enseña [Retry] y [Back to menu] es el panel de conexión, único camino de recuperación.

**Salir del navegador no es salir de una sesión.** El botón *Volver* cierra el panel y llama a
`JoinSessionUI.ShowConnectMenu()`; no llama a `Shutdown`, ni a `LeaveCurrentSession`, ni toca el
backend. El cursor va por `SessionCursor.Apply`, igual que el panel.

### Como host: el anuncio nace y muere con la fase

`ServerBrowserBootstrap` alimenta cada frame a `HostAnnouncementDriver` con `Role` y `Phase`:

| Estado del ciclo | Qué le pasa al anuncio de Steam |
|---|---|
| `Role != Host`, o fase fuera de `Connected`/`InGame` | **se retira** — lo publicado Y lo que se estaba intentando publicar |
| Host en `Connected`/`InGame`, sin anuncio | se publica, reintentando cada 2 s mientras Steam lo crea |
| Host anunciado | latido cada 10 s con jugadores y estado |
| Proceso cerrándose | `ForceWithdraw` en `OnApplicationQuit` / `OnDestroy` |

La primera fila es la importante: **cubre todos los finales sin distinguirlos**. El `Quit to Menu`
del vendor, el backend que muere, la pérdida de conexión y el teardown normal salen todos de
`Connected`/`InGame`, así que todos retiran el anuncio por el mismo sitio. No hace falta —ni se
debe— enganchar el publicador a `SessionEndHandler`: la fase ya lo dice.

Y el matiz que costó un fantasma: **"lo que se estaba intentando"** no es adorno. La creación del
lobby en Steam es asíncrona, así que hay ~2 s en los que `Publish` ya se llamó y todavía devuelve
false. Si la sesión termina ahí, `IsPublishing` es falso —nunca llegó a ser cierto— y sin
`_publishPending` la retirada no se dispara: la creación aterriza después y deja un lobby público
apuntando a un endpoint muerto. Ver la invariante **I14** en
[SESSION_INVARIANTS.md](SESSION_INVARIANTS.md).

**No se publica antes de `Connected`** porque hasta entonces `LastSelectedNetPort` todavía puede
cambiar, y publicar un puerto que acaba siendo otro deja un lobby apuntando a la nada.

**Y no se publica nada si Steam no inicializó**, que hasta el 2026-08-31 era *siempre*: faltaba el
nativo `steam_api64.dll`. La fase no se entera —`IsAvailable` en falso sólo apaga el camino
Steam— y el Host/Join por IP se comporta exactamente igual. Ver
[../SERVER_BROWSER.md](../SERVER_BROWSER.md) §14.

### Repetibilidad

`MENU → BUSCAR PARTIDA → JOIN → GAME → QUIT TO MENU → BUSCAR PARTIDA → …` funciona por lo mismo del
§9, más dos cosas propias: el navegador cancela su consulta al cerrarse (una respuesta lenta no
puede repintar un panel que ya no se ve) y el anuncio del host se retira al salir de `InGame`, así
que la siguiente partida publica un lobby nuevo con SU puerto.

Cubierto por `Assets/Tests/EditMode/ServerBrowserIntegrationTests.cs` (gate, camino único,
reintento tras fallo, doble clic), `SteamLobbyPublisherTests.cs` (publicar una vez, retirar en
cualquier final, no tocar tras retirar, volver a anunciar en la segunda partida) y
`ServerBrowserSessionGateTests.cs` (el adaptador contra `SessionState` de verdad). Lo que **no**
cubren: que el botón del menú aparezca, que la vuelta repinte el panel y que dos PCs se vean — eso
es cableado y playtest, y está pendiente en [../SERVER_BROWSER.md](../SERVER_BROWSER.md) §12.
