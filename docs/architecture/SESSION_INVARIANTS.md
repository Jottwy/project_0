# Session Invariants

> Las reglas del ciclo de vida de sesión, en una lista que se puede comprobar. El *cómo* está en
> [SESSION_LIFECYCLE.md](SESSION_LIFECYCLE.md); esto es el *qué no puede pasar nunca*.
>
> Cada invariante lleva **quién la impone** y **qué la prueba**. Una invariante sin ejecutor es
> un deseo, y una sin prueba se rompe en la siguiente tanda sin que nadie se entere.

---

### I1 — No puede existir más de una sesión activa

**Impone:** `SessionStateMachine.RequestStart`, que sólo acepta desde `Menu`, `Disconnected` o
`Failed`. Los ocho caminos de arranque pasan por `NetworkInitializer.StartAsHost`/`StartAsJoiner`,
que consultan ese gate antes de tocar nada.
**Prueba:** `JoinPressedTwiceStartsOneSession`, `HostDuringJoiningIsRejected`,
`StartDuringTeardownIsRejected`.

### I2 — Un Joiner sólo está `Connected` después de `session_joined`

`IPCClient.IsConnected` significa «Unity habló con **su propio** backend por TCP en 127.0.0.1».
Para un Host eso ES la sesión; para un Joiner no prueba nada — su backend local acepta ese TCP y
sirve un mundo aunque el handshake UDP contra el host no haya salido nunca de la máquina.
**Impone:** `SessionStateMachine.NotifyIpcConnected`, que devuelve `false` sin mover la fase
cuando el rol es `Joiner`.
**Prueba:** `AJoinerNeverShowsConnectedForHavingIpcAlone`,
`JoinerIsNotConnectedUntilTheHandshakeCompletes`.
**No romper la otra mitad:** un Host **no** manda handshake ninguno y no puede quedarse esperando
uno — `AHostIsConnectedAsSoonAsItsOwnBackendAnswers`.

### I3 — La UI nunca pinta un estado que la sesión no tiene

Ninguna fase que no sea «en el mundo» (`Connected`, `InGame`) puede pintarse como
`PanelState.Connected`. `Disconnecting` se pinta como `Disconnected` a propósito: dejar el estado
anterior mientras se desmonta la sesión es como se acaba enseñando «Connected» sobre una sesión
que ya no existe.
**Impone:** `JoinSessionUI.PanelStateFor`, la única correspondencia fase → panel.
**Prueba:** `NoPhaseOutsideTheWorldIsPaintedAsConnected` (barre TODAS las fases × TODOS los roles).

### I4 — Abandonar una sesión limpia todos sus recursos

Proceso backend, socket IPC, lobby de Steam, latches del panel, cursor. En ese orden y sin que un
paso fallido se salte los siguientes.
**Impone:** `SessionEndHandler.TeardownNetworkResources` (§5 de SESSION_LIFECYCLE).
**Prueba:** la parte de estado, en `JoinDisconnectJoinFiveTimesLeavesNoResidue`. La parte de
proceso **no es probable en EditMode**: `tools/dev/CheckOrphanBackends.ps1 -ExpectCount 0`.

### I5 — Una sesión nueva no puede heredar callbacks de una anterior

Dos mecanismos distintos porque hay dos formas de heredar:

- **Suscripciones duplicadas.** `IPCClient` sobrevive a la sesión (hilo y singleton siguen vivos
  para que la siguiente los reutilice), así que un suscriptor que se re-suscriba sin quitarse
  primero corría **dos veces por evento** el resto de la vida del proceso. **Impone:**
  `IPCClient.AddUnique`, usado por los siete `Add*Listener`. **Prueba:**
  `SubscribingTwiceLeavesOneListener` y su control negativo
  `DifferentSubscribersAreNotConfusedWithADuplicate`.
- **Callbacks tardíos del proceso.** `Process.Exited` del backend de la sesión 1 llega en un hilo
  del pool cuando la sesión 2 ya está arriba, y no trae identidad. **Impone:** la comparación de
  `SessionStateMachine.Generation` en `NetworkInitializer.ShouldApplyBackendExit`. **Prueba:**
  `AStaleBackendExitCannotTouchTheNewSession`, `EveryAttemptGetsItsOwnGeneration`.

### I6 — Un backend anterior nunca puede recibir órdenes de la sesión nueva

`KillBackend` pide el guardado **por el IPC**, así que terminar un backend viejo tiene que ocurrir
**antes** de reconfigurar el endpoint a la sesión nueva; si no, el `save_and_shutdown` se lo
llevaría el backend nuevo.
**Impone:** `TerminateLeftoverBackend`, llamado como primer efecto de `StartAsHost`/`StartAsJoiner`
—después del gate y **antes** de `ConfigureIpcClient`. `LaunchBackendProcess` grita
`INVARIANTE ROTA` si aun así llega con un proceso registrado.
**Prueba:** revisión de orden en el código + `CheckOrphanBackends.ps1`.

### I7 — `Leave` es idempotente

El fin de sesión llega **siempre** por partida doble (paquete de despedida **y** timeout de
latido), y el jugador puede pulsar Disconnect dos veces.
**Impone:** `RequestLeave` devuelve `false` desde cualquier fase que no esté viva, y el teardown
sale por ahí sin tocar nada.
**Prueba:** `LeaveCalledTwiceIsHarmless`, `LeaveWithNoSessionDoesNothing`.

### I8 — `Join` durante `Joining` se ignora, no se encola

Ignorar y **decirlo en el log** con la fase actual. Encolarlo daría un segundo backend contra el
primero; ignorarlo en silencio haría que un clic no produjera nada sin explicación.
**Impone:** `RequestStart` (motor) + `JoinSessionUI.RejectIfBusy` (para que la UI tampoco mienta).
**Prueba:** `JoinPressedTwiceStartsOneSession`.

### I9 — El cursor queda restaurado al volver al menú

`panel visible → libre` · `panel oculto + en el mundo → capturado` · `panel oculto + resto → libre`.
Y `SessionCursor.ReleaseToMenu()` al final de **cada** teardown, haya panel o no.
**Impone:** `SessionCursor`. Nadie más escribe `Cursor.*` en el camino de sesión.
**Prueba:** `CursorIsFreeInEveryPhaseThatIsNotTheWorld`, `AVisibleMenuAlwaysFreesTheCursor`,
`CursorIsRestoredAfterEveryExit`.
**Frontera:** durante la partida el cursor es del vendor (`GameMode`, `UIInput`,
`InventoryInspectionManager`). Esta invariante NO se lo disputa.

### I10 — Un backend muerto no puede dejar la UI permanentemente bloqueada

**Impone:** `NetworkInitializer.WatchBackendLiveness`, que vigila el proceso **cada frame mientras
la fase esté viva** (no sólo durante el arranque), más el backstop de `joinerHandshakeTimeout`
para el caso en que el aviso autoritativo se pierda.
**Prueba:** paso 4 del procedimiento manual (§11 de SESSION_LIFECYCLE). No es probable en
EditMode: hace falta un proceso de verdad al que matar.

### I11 — Ningún timeout de cliente tapa un error real

Quien acota la espera de un joiner es el `CONNECT_TIMEOUT` del backend (15 s), que contesta con un
motivo redactado. El único timeout de cliente está **por encima** (25 s), cubre exclusivamente el
hueco en que ese aviso no puede llegar, y se registra con la palabra `BACKSTOP` en el log.
**Impone:** `NetworkInitializer.UpdateJoinerHandshakeBackstop`.
**Prueba:** revisión + el log del backend (`Builds/PlaytestLogs/`).

### I12 — Salir de la escena de juego es salir de la sesión

Aunque nadie de red se entere: el `Quit to Menu` del vendor no toca red y el vendor no se edita.
**Impone:** `SessionEndHandler.OnActiveSceneChanged`, comparando contra la escena en la que se
ENTRÓ (`WorldScene`), no contra el nombre del menú.
**Prueba:** `JoinGameplayMenuJoinWorksWithoutTouchingAnythingElse` (la regla) + paso 1 del
procedimiento manual (el cableado).

### I13 — El panel se rearma, no se reconstruye

Destruir `JoinSessionUI` o su GameObject dejaría que el siguiente `ShowConnectPanel` construyera
una instancia nueva cuyo `Start()` repite el auto-connect de `SESSION_MODE`/`CONNECT_TO`, en
bucle, llevándose por delante el arnés de playtest multi-instancia.
**Impone:** `ResetForNewSession` (sólo campos) y el early-return de
`NetworkMenuBootstrap.ShowConnectPanel`.
**Prueba:** revisión. Es una invariante de ADR-056 que esta tanda **conserva**.

### I14 — Una sesión que termina no deja anuncio en Steam

Incluida la que termina **mientras Steam está creando el lobby**. En esa ventana el publicador aún
no se cree publicando (`Publish` devolvió false), así que la retirada por fase no se disparaba y la
creación aterrizaba después: lobby público, joinable, apuntando a un endpoint muerto, y sin dueño
hasta cerrar el proceso. El jugador que lo elige lo descubre a los 15 s de espera.
**Impone:** `HostAnnouncementDriver._publishPending` (retira también lo que *se estaba
intentando*) y `SteamLobbyManager._lobbyEpoch` (una creación que aterriza tras el teardown se
cierra en vez de adoptarse — mismo patrón que `Generation`, porque la pregunta no es "¿hay
teardown?" sino "¿de qué sesión es esto?"). Y **el orden del teardown**:
`ServerBrowserBootstrap.WithdrawAnnouncement()` va **antes** de `SteamLobbyManager.LeaveLobby()` en
las dos rutas (`SessionEndHandler` y `JoinSessionUI.ShowRecoverablePanel`). Al revés, `LeaveLobby`
vacía `_hostedLobby` sin log, el conductor ve `IsPublishing == false`, su `Withdraw()` no corre y
esta invariante **no cubre la puerta que de verdad se usa** — el lobby moría igual, pero sin dejar
rastro, y eso costó una sesión de diagnóstico el 2026-08-31.
**Prueba:** `ALobbyThatFinishesCreatingAfterTheSessionEndedIsClosed`,
`ForceWithdrawAlsoClosesALobbyThatArrivedLate`, `APublishThatNeverStartedDoesNotCountAsAWithdrawal`
y `ThePendingFlagClearsOnceThePublishSucceeds` (`SteamLobbyPublisherTests`); el epoch, sólo por
revisión — vive del lado de Steamworks y no hay costura que lo cubra sin cliente de Steam.

### I14b — Un anuncio nunca lleva una dirección que no sea alcanzable desde fuera

Loopback (`127.0.0.0/8`, `::1`, `localhost`), la de bind (`0.0.0.0`, `::`) y link-local/APIPA
(`169.254.0.0/16`, `fe80::/10`) **no se publican jamás**. `127.0.0.1` no es «una IP que sólo vale en
local»: en la máquina del joiner **es la máquina del joiner**, así que el Join no falla — va a otro
sitio, y tarda 15 s en decirlo. Y si no hay ninguna dirección defendible, **no se anuncia la
partida**: publicar un endpoint malo le cuesta 15 s a quien lo elige, no publicar no le cuesta nada.
**Impone:** `LobbyEndpointPolicy.ResolvePublishableHost`, consultada por
`ServerBrowserBootstrap.ResolveHostEndpoint`; los candidatos los trae `LocalAddressProbe`, ordenados
con la interfaz de la ruta por defecto primero.
**Prueba:** `LobbyEndpointPolicyTests` (25 casos: clasificación, precedencia campo→candidatos→nada,
y `WithNothingDefensibleTheGameIsNotAnnouncedAtAll`). El descubrimiento de direcciones NO se prueba:
depende de la máquina.

### I15 — Steam caído nunca bloquea la sesión

Ni el arranque del proceso, ni el Host, ni el Join por IP dependen de que Steam inicialice. Un
`SteamClient.Init` fallido deja `IsAvailable` en falso y **nada más**: el navegador enseña su error,
`CanStart` sigue en cierto y el panel conserva [Host] y [Join].
**Impone:** `SteamLobbyManager.TryInitSteam` (captura y sigue; nunca propaga) y
`ServerBrowserBootstrap.CreateDirectory`, que **no cae al mock en silencio** — una lista de mentira
parecería una lista de verdad.
**Prueba:** `SteamFailingDoesNotBlockTheManualIpPath` (`ServerBrowserSessionGateTests`) y
`SteamUnavailableIsACleanErrorAndNeverAnEmptyList` (`SteamLobbyDirectoryTests`) para la regla; el
cableado, por el extra de §12 de `SERVER_BROWSER.md` (Steam cerrado en el segundo PC), **no
ejecutado todavía**.

---

## Cómo comprobarlas

```bash
# Las reglas (sin editor, sin backend):
#   Test Runner del editor → EditMode → SessionLifecycleTests + JoinSessionUITests
bash tools/dev/CompileCheckClient.sh      # la puerta de compilación del cliente
```

```powershell
tools/dev/CheckOrphanBackends.ps1 -Label <etiqueta> -ExpectCount 0
```

El backend no cambia en esta tanda; su puerta sigue siendo `cargo test`, `cargo clippy
--all-targets -- -D warnings` y `cargo fmt --check`.
