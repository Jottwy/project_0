# SERVER_BROWSER.md — Navegador de servidores (cliente)

> **Steam YA INICIALIZA** (2026-08-31, primera vez en la historia del proyecto — §14.1 y §14.1b
> cuentan los dos fallos encadenados que lo impedían). Build hecho y suite EditMode real en verde.
> **Lo que sigue sin validar es el descubrimiento entre dos PCs**: nadie ha visto todavía un lobby
> publicado ni un lobby listado. §12 marca qué está hecho y qué no, paso por paso.

## 0. Estado, en cuatro columnas

| | Qué |
|---|---|
| **IMPLEMENTADO** | `SteamLobbyDirectory` (consulta real por `SteamMatchmaking.LobbyList`), `SteamLobbyPublisher` + `HostAnnouncementDriver` (el host publica solo al llegar al mundo y se retira solo al salir), modelo de lobby con TTL/aforo/versión/endpoint, filtro, orden, panel con sus estados, botón *Buscar partida*, gate contra `SessionState.Current.CanStart`, entrada por el ÚNICO camino de conexión, coexistencia intacta con el Host/Join por IP, y **App ID configurable** (`SteamAppConfig`, §14). |
| **VALIDADO** | **En el ejecutable real, 2026-08-31**: Steam inicializa (`Steam initialized app_id=480 (Spacewar / desarrollo) name=Jottwy6901 steam_id=…`), el App ID se resuelve por `steam_appid.txt` y lo dice, el nativo llega al build, y el arranque no deja excepciones ni divergencia de claves. **Suite EditMode real de Unity: 926 tests, 912 pasan; los 190 de lobby/browser/Steam en verde** (los 14 rojos son ajenos: 7 `NetworkInitializerTests`, 2 `StorageRackDisplayTests`, 4 Wg3, 1 audio). `CompileCheckClient` 0 errores en las cuatro asambleas. `cargo test`/clippy/fmt limpios — de la rama; **este trabajo no toca Rust**. |
| **MOCK** | `MockLobbyDirectory` y `MockLobbyPublisher`, ya sólo para tests y para trabajar en la UI sin Steam (`BS_LOBBY_DIRECTORY=mock`). **No** se usan en producción. |
| **PENDIENTE** | **Todo lo que exige dos PCs, y una cosa más barata: nadie ha pulsado todavía *Buscar partida* ni ha visto publicarse un lobby** — que Steam arranque no prueba que la consulta conteste ni que el anuncio salga (§12, pasos 5-12). Además: ping (Steam no lo da en la consulta: ver §8), la IP pública del host la sigue escribiendo el humano (§6), aforo por constante espejo (§6), contraseñas (no hay campo en el wire), virtualizar listas largas. |

## 1. Arquitectura final

```
                    ┌──────────────────── HOST ────────────────────┐
SessionStateMachine ──fase──> ServerBrowserBootstrap.DriveAnnouncement
                                  ↓ HostAnnouncementState (puro)
                             HostAnnouncementDriver      ← decide CUÁNDO
                                  ↓ ILobbyPublisher
                             SteamLobbyPublisher         ← decide QUÉ
                                  ↓ ISteamLobbyHost
                             FacepunchSteamLobbyHost → SteamLobbyManager → Steam

                    ┌─────────────────── CLIENTE ───────────────────┐
ServerBrowserUI ──> ServerBrowserViewModel ──> ILobbyDirectory
                                                  ↓
                                          SteamLobbyDirectory        ← timeout, cancelación, TTL
                                                  ↓ ISteamLobbyQuery
                                          FacepunchSteamLobbyQuery → SteamLobbyManager → Steam
                                                  ↓ SteamLobbyMapper (puro)
                                             LobbyList → filtro → orden → tabla
                                                  ↓ selección + [Entrar]
                                          LobbyJoinRouter → ILobbyJoinSink
                                                  ↓
                                          JoinSessionLobbyJoinSink
                                                  ↓ JoinSessionUI.TryBeginSteamJoin(ip, port, name)
                                          NetworkInitializer.StartAsJoiner
                                                  ↓
                                          SessionStateMachine → backend → Connected → World
```

**Las dos costuras contra Steam (`ISteamLobbyQuery`, `ISteamLobbyHost`) existen por una razón
concreta**: todo lo que puede fallar de verdad —respuestas viejas, timeout, cero resultados,
publicar dos veces, tocar después de retirar— queda del lado que se puede probar sin cliente de
Steam. Los dos adaptadores de Facepunch no tienen ni una decisión dentro.

### Ownership

| Pregunta | Quién manda |
|---|---|
| ¿En qué fase está la sesión? | `SessionStateMachine` (`SessionState.Current`) |
| ¿Se puede arrancar una sesión? | `SessionState.Current.CanStart` — el navegador lo **lee y lo pasa**, no lo replica |
| ¿Quién lanza y mata el backend? | `NetworkInitializer` |
| ¿Quién conecta? | `JoinSessionUI` → `NetworkInitializer.StartAsJoiner`. **Único camino** |
| ¿Quién habla con Steam? | `SteamLobbyManager` — **un solo lobby y un solo dueño** |
| ¿Cuándo se anuncia/retira? | `HostAnnouncementDriver`, a partir de la fase. **Nunca un botón** |
| ¿Qué se anuncia? | `SteamLobbyPublisher` |
| ¿Qué se lista y qué se puede pulsar? | `ServerBrowserViewModel` + `LobbyJoinRouter` |
| ¿Cursor? | `SessionCursor` |

## 2. Steam Discovery

`SteamLobbyDirectory` implementa `ILobbyDirectory` sobre
`SteamMatchmaking.LobbyList.FilterDistanceWorldwide().WithKeyValue(bs_game, backrooms_survival)
.WithMaxResults(50).RequestAsync()`.

**El filtro `bs_game` no es un adorno.** El proyecto usa el App ID **480 (Spacewar)**, el id
público de pruebas de Valve: sin filtro, la consulta devuelve los lobbies de cualquiera que esté
probando Steamworks en el mundo.

Reglas, todas probadas:

- **el resultado se entrega dentro de `Tick`**, nunca desde una continuación asíncrona. Es lo que
  garantiza el hilo de Unity y lo que permite pintar el estado "cargando";
- **cero lobbies ≠ error.** Steam devuelve `null` cuando no encuentra nada; eso es "No se
  encontraron partidas.", no "No se pudieron obtener las partidas.";
- **Steam ausente es un error limpio**, con su mensaje: *"Steam no está disponible. Puedes entrar
  por IP desde el menú de multijugador."* Nunca una lista vacía, que parecería que no hay nadie;
- **timeout de 12 s.** Steam no promete contestar; sin tope el panel se queda en "Buscando…" para
  siempre;
- **una respuesta vieja jamás pisa una consulta posterior**: la anterior se cancela (`Cancelled`,
  que no es un error) y su tarea se **suelta** — Facepunch no deja cancelarla, así que la respuesta
  tardía cae en el vacío;
- **un lobby ilegible no se lleva la lista por delante**: se descarta él solo.

### Conversión (`SteamLobbyMapper`, puro)

Se **rechaza poco**: sólo lo que no se puede ni pintar (sin id, sin aforo por metadato ni por
Steam). Lo demás se lista y es `EvaluateJoinability` quien lo bloquea:

| Caso | Se ve | Se entra |
|---|---|---|
| Puerto o IP ilegibles | sí | no — `InvalidEndpoint` |
| Sin `bs_wire`, o versión distinta | sí | no — `VersionMismatch` |
| Lleno | sí | no — `Full` |
| `bs_state = closed` | sí | no — `Closed` |

**Sin versión de wire no hay compatibilidad.** Rellenarla con la nuestra sería inventarse que el
lobby es compatible — y con el App ID compartido puede ser de otro juego entero.

## 3. Steam Publishing

`SteamLobbyPublisher` escribe estos metadatos sobre el lobby propio:

| Clave | Qué |
|---|---|
| `bs_game` | marca del juego — el filtro de la consulta |
| `connect_ip` / `connect_port` | el destino. **El puerto es `NetworkInitializer.LastSelectedNetPort`**, el realmente elegido |
| `bs_name` | nombre visible (persona de Steam) |
| `bs_wire` | **versión de WIRE** (`WireSchema.Expected`), no `Application.version` |
| `bs_players` / `bs_max` | aforo |
| `bs_map` | escena del mundo (`SessionState.Current.WorldScene`) |
| `bs_state` | `open` / `ingame` / `closed` |
| `bs_at` | hora Unix del host en el último latido |
| `host_name` | el del spike original, intacto |

Las claves están declaradas **dos veces**: en `SteamLobbyKeys` (sin Steam dentro, para poder
probar el publicador) y en `SteamLobbyManager` (el lado que habla con Steam). Si divergen, el host
publica con unas y el navegador lee con otras y **la lista sale vacía sin un solo error** — por eso
hay un test de paridad y un `LogError` al arrancar.

### Quién decide cuándo: el ciclo, no un botón

`HostAnnouncementDriver` (puro) recibe cada frame el estado del host y aplica cuatro reglas:

1. **no somos host, o la sesión no está establecida, o no hay endpoint → si había anuncio, se
   retira.** Eso cubre TODOS los finales sin distinguirlos: `Quit to Menu`, backend muerto,
   desconexión, timeout;
2. **host con sesión establecida y sin anuncio → publicar**, reintentando cada 2 s mientras Steam
   termina de crear el lobby (la creación es asíncrona: el primer intento casi nunca la tiene);
3. **ya anunciado → latido cada 10 s** con jugadores y estado actualizados;
4. **al cerrar el proceso → `ForceWithdraw`**.

Un anuncio atado a un botón sobrevive al día en que alguien sale por otro camino. Eso es un lobby
fantasma, y es el fallo más caro: el jugador lo descubre a los 15 s de espera.

**No se publica antes de `Connected`** porque hasta entonces el endpoint todavía puede cambiar.

#### La ventana de la creación a medias (arreglado el 2026-08-31)

La regla 1 decía "si había anuncio, se retira", y "había anuncio" era `IsPublishing`. Eso deja
fuera el caso peor: `Publish` devolvió false porque Steam **seguía creando** el lobby, así que
`IsPublishing` nunca llegó a ser cierto. Si la sesión terminaba en esa ventana de ~2 s, la
retirada no se disparaba, la creación aterrizaba después y dejaba en Steam un lobby **público,
joinable, apuntando a un endpoint ya muerto y sin nadie que volviera a cerrarlo** hasta que se
cerrara el proceso. Un fantasma perfecto.

Se tapa por los dos lados, porque son dos objetos distintos y cualquiera de los dos puede llegar
primero:

- **`HostAnnouncementDriver._publishPending`** — la retirada cubre "lo estaba intentando", no sólo
  "lo consiguió". `Withdraw` ya era idempotente y ya cerraba el lobby que el host tuviera aunque
  el publicador no se creyera publicando; lo que faltaba era llamarlo;
- **`SteamLobbyManager._lobbyEpoch`** — sube en cada `CloseHostedLobby`, **también cuando no había
  lobby que cerrar**. `CreateSteamLobby` captura el epoch antes del `await` y, si cambió, cierra
  el lobby recién nacido en vez de adoptarlo. Un `bool` no bastaba: el problema no es "hay
  teardown", es "esta creación es de la sesión ANTERIOR" — el mismo patrón que `Generation` en
  `SessionStateMachine`.

Regresiones: `ALobbyThatFinishesCreatingAfterTheSessionEndedIsClosed`,
`ForceWithdrawAlsoClosesALobbyThatArrivedLate`, `APublishThatNeverStartedDoesNotCountAsAWithdrawal`
y `ThePendingFlagClearsOnceThePublishSucceeds` (`SteamLobbyPublisherTests`).

## 4. Browser → Join

Sin cambios respecto a la tanda anterior, y ésa es la idea: cambiar el mock por Steam **no tocó
una línea** del navegador, del filtro, del orden, del TTL ni del camino de entrada.

Orden de los rechazos (todos antes de tocar la conexión):

1. ya hay un intento en marcha (doble clic) → `SessionBusy`;
2. `CanStart` en falso → `SessionBusy`;
3. el lobby dice que no: caducado → cerrado → privado → endpoint inválido → versión → lleno →
   contraseña;
4. el camino de conexión dice que no → `SinkRefused`.

Tras arrancar, el navegador se **esconde** y lee la FASE: `Connected`/`InGame` → se cierra;
`Failed`/`Disconnected`/`Menu` → vuelve a un estado reutilizable. **El error lo pinta el panel de
conexión**, que es el único sitio con [Retry] y [Back to menu].

## 5. IP manual → Join

**Intacto.** El botón [Join], el campo de IP y el de puerto son los de siempre; el navegador **no
los escribe** (el sink llama a `TryBeginSteamJoin`, que rellena los campos sólo cuando el jugador
elige un lobby, igual que ya hacía el auto-join por invitación).

Y lo importante: **si Steam falla, el Join por IP no se entera**. El navegador enseña su error, la
sesión no se toca, `CanStart` sigue en cierto. Hay test.

## 6. Lo que el cliente NO sabe, y de dónde sale

- **La IP del host.** Se publicaba **el campo del panel verbatim**, y eso resultó ser un fallo, no
  una limitación: el valor por defecto del campo es `127.0.0.1` y así salía al lobby. Ver §15.
  Ahora pasa por `LobbyEndpointPolicy`: manda lo escrito si sirve, si no la dirección de la ruta
  por defecto, y **si no hay ninguna defendible la partida no se anuncia**. La IP **pública** sigue
  sin poder deducirse: para jugar fuera de la LAN hay que escribirla a mano y abrir el puerto
  (§13.8).
- **El aforo.** `max_players` viaja en el `SessionConfig` del handshake P2P entre backends, no por
  el IPC, así que el cliente no lo ve. Se publica una **constante espejo**
  (`ServerBrowserBootstrap.SessionMaxPlayers = 50`) de `SessionConfig::default().max_players`, con
  la misma disciplina que `WireSchema`: si el backend cambia, esto sube con él. **Riesgo anotado**:
  nada lo comprueba automáticamente.
- **Los jugadores dentro.** `RemotePlayerManager.ActiveCount + 1`. Es el único contador real del
  cliente; en el menú, sin ese componente en escena, se anuncia 1, que es cierto.

## 7. El reloj y el TTL

Ni el modelo ni el directorio miran `Time.*`: **el reloj lo pasa quien llama**.

El host publica `bs_at` con **su** hora Unix. El navegador **no la usa para el TTL**: dos relojes
distintos no se pueden restar. La ficha se sella con **nuestro** reloj en el momento de verla, así
que el TTL (60 s) mide *"cuánto hace que la vimos en una consulta"*, que es lo único que este
cliente puede afirmar. La liveness de verdad la da Steam: si el host se va, el lobby desaparece de
la lista.

## 8. Ping

**No hay ping, y no se inventa.** La consulta de lobbies de Steam no devuelve latencia
(`LobbyQuery` sólo ofrece `OrderByNear`, que ordena por un campo numérico propio, no por ping
medido). Todas las fichas salen con `PingMs = UnknownPing`, la columna enseña `—` y el orden por
ping **hunde** los desconocidos en ambas direcciones — el comportamiento que ya tenía.

Medirlo de verdad exigiría sondear el endpoint, o sea tocar transporte. **Pendiente, con ADR.**

## 9. Qué pasa cuando algo falla

| Situación | Qué ve el jugador | Qué pasa por dentro |
|---|---|---|
| Steam cerrado / DLL ausente | *"No se pudieron obtener las partidas."* + el motivo | ni se pregunta; el Join por IP sigue igual |
| Steam no contesta | lo mismo, tras 12 s | la consulta se abandona |
| Cero lobbies | *"No se encontraron partidas."* | resultado válido, no error |
| Refresco durante un intento de entrada | nada: el refresco se ignora | `RequestRefresh` sale si `IsJoining` |
| El lobby elegido caduca | desaparece de la tabla y se deselecciona solo | podado por TTL cada segundo |
| El lobby desaparece justo antes de Entrar | *"Ese servidor ya no se anuncia."* y se poda | `RejectedByLobby / Expired`, sin tocar la conexión |
| Join falla (timeout, backend muerto) | el panel de conexión enseña el motivo con [Retry] | el navegador vuelve a estado usable |
| El host se va / muere el backend | el lobby deja de anunciarse | el conductor retira al salir de `Connected/InGame` |
| El proceso del host se cierra | ídem | `ForceWithdraw` en `OnApplicationQuit`/`OnDestroy` |

## 10. La entrada de menú, y por qué está donde está

El botón **"Buscar partida"** sale en el panel de multijugador, en su propia fila bajo [Host]
[Join] — el mismo patrón que el botón de Steam. Se **inyecta** desde `ServerBrowserBootstrap`
(auto-instalado con `[RuntimeInitializeOnLoadMethod]`), que busca `JoinSessionCanvas/Panel` por
nombre.

**Por qué inyectado:** ese panel no está en ninguna escena (lo monta `JoinSessionUI` por código), y
tanto ese fichero como `STP_MainMenu.unity` los estaba editando otra sesión. Un `.unity` no se
fusiona: se pisa. Es un **puente para retirar** cuando ese trabajo cierre; si el panel se renombra,
el botón simplemente no aparece y Host/Join siguen intactos.

## 11. Validación ejecutada

- `bash tools/dev/CompileCheckClient.sh` → **0 errores** en las cuatro asambleas.
- **Suite EditMode real de Unity** (`-batchmode -runTests -testPlatform EditMode`, 2026-08-30):
  **151/151 de las once suites del navegador en verde**, incluidos los 8 que exigen Unity y que el
  runner headless no puede ejecutar (gate de `CanStart` contra `SessionState` real,
  `BackendLaunchCount` sin moverse al navegar, `ClientVersion == WireSchema.Expected`, paridad de
  claves de metadata, listeners sin duplicar). Cubren además mapeo, directorio (timeout,
  cancelación, respuesta obsoleta, cero resultados, Steam ausente), publicador (una sola vez,
  endpoint real, latido, retirada idempotente, nada de tocar tras retirar), conductor (no publica si
  no somos host, retira en cualquier final) y el circuito publicar→leer contra el contrato de claves.
- El run completo da **901 tests: 887 pasan, 14 fallan**, y **ninguno de los 14 es de este trabajo**:
  8 en `NetworkInitializerTests` (el fichero que otra sesión tiene reescrito sin commitear), 4 de
  worldgen, 2 de inventario y 1 de audio. Comprobado re-ejecutando **sólo esas cinco suites, sin un
  test de lobbies en el run**: fallan igual (15 allí, con un `HostLaunchesBackendWithValidPath`
  flaky que lanza backend real). No hay contaminación de orden.
- Los mismos cuerpos de test puros corren también en un runner headless (`dotnet`): 143/143. Es un
  atajo para iterar con el editor abierto, **no** un sustituto de la suite real.
- El `-testResults` bajo `Temp/` se lo lleva Unity al limpiar; la copia utilizable queda en
  `C:/Users/JOELV/AppData/LocalLow/JottwyGames/Playtest 0.0.1.0/TestResults.xml`.
- `cargo fmt --check`, `cargo clippy --all-targets -D warnings`, `cargo test` → limpios,
  **1189 passed / 0 failed**. Estado de la rama; **este trabajo no toca Rust**.

## 12. Procedimiento exacto de playtest, y qué falta

**Estado: 4 ejecutados de 12 (2026-08-31).** Los pasos 1-4 están hechos y con evidencia. Del 5 al 12
faltan: el 5 y el 6 sólo necesitan que alguien pulse botones en ESTA máquina; del 7 al 12 hacen
falta dos PCs con Steam.

### Preparación (una vez)

0. **Editor de Unity CERRADO** — `-runTests` y `-executeMethod` necesitan el lock libre. Y con el
   editor abierto, `steam_api64.dll` no se importa hasta que Unity recupera el foco.

1. **Suite EditMode real** (esto sí lo puede hacer una sesión sola):
   ```bash
   "C:/UnityInstall/6000.0.71f1/Editor/Unity.exe" -batchmode -projectPath "J:/Unity/BackroomsSurvivalMMO" -runTests -testPlatform EditMode -testResults "J:/Unity/BackroomsSurvivalMMO/Temp/editmode-results.xml" -logFile "J:/Unity/BackroomsSurvivalMMO/Temp/editmode.log"
   ```
   (sin `-quit`: con `-runTests` corta el run antes de que termine.)

2. **Build de desarrollo** — Development escribe `steam_appid.txt = 480` solo:
   ```bash
   "C:/UnityInstall/6000.0.71f1/Editor/Unity.exe" -batchmode -quit -nographics -projectPath "J:/Unity/BackroomsSurvivalMMO" -executeMethod BackroomsSurvival.EditorTools.DevPlayerBuild.BuildWindows64 -buildOutput "J:/Unity/BackroomsSurvivalMMO/Builds/Build_steam/Playtest 0.0.1.0.exe" -logFile "J:/Unity/BackroomsSurvivalMMO/Temp/build.log"
   ```
   El build **falla a propósito** si falta el backend o si falta `steam_api64.dll`. Comprobar en el
   log: `[SteamAppIdBuildPostprocessor] steam_appid.txt = 480 (Spacewar / desarrollo)` y
   `steam_api64.dll presente en …`.

3. **Copiar la carpeta del build al segundo PC.** Steam abierto y con sesión iniciada en los dos.

### Las doce comprobaciones

| # | Qué | Cómo se sabe que salió bien | Estado |
|---|---|---|---|
| 1 | `CompileCheckClient` | `errors: 0` en las cuatro asambleas | ✅ 2026-08-31 |
| 2 | Suite EditMode real | verde, incluidas las cuatro regresiones de lobby fantasma | ✅ 190/190 de lobby; 912/926 en total, 14 rojos ajenos |
| 3 | Build real | el build sale; el postprocesador confirma nativo y App ID | ✅ `Builds/Build_steam`, `result=Succeeded` |
| 4 | **Steam inicializa** (PC A) | en el log: `Steam initialized app_id=480 (Spacewar / desarrollo) name=… steam_id=…`. **Si no aparece ESTA línea, parar aquí**: §14.4 dice cuál de las cuatro condiciones falta | ✅ `name=Jottwy6901` |
| 5 | Host real (PC A) | escribir la **IP LAN** en el campo (no `127.0.0.1`, §6), [Host], entrar al mundo. Log: `[SteamLobbyManager] Lobby created id=… connect=IP:PUERTO` | ⬜ **una sola máquina basta** |
| 6 | El navegador consulta (PC A) | menú → Multijugador → **Buscar partida** → [Refrescar]. Con 0 partidas ajenas el resultado correcto es *"No se encontraron partidas."*, **no** un error: eso ya prueba que la consulta llega a Steam y vuelve | ⬜ **una sola máquina basta** |
| 7 | El lobby se ve (PC B) | la partida de A en la lista, con nombre, jugadores, mapa y versión | ⬜ 2 PCs |
| 8 | Join desde el Browser (PC B) | seleccionar → [Entrar] → `Connecting…` → `Connected`; los dos se ven moverse | ⬜ 2 PCs |
| 9 | Quit to Menu (PC A) | en B, [Refrescar]: **el lobby ya no está** | ⬜ 2 PCs |
| 10 | Volver a hostear (PC A) | en B, [Refrescar]: aparece **otra vez**, y con el puerto de la partida nueva | ⬜ 2 PCs |
| 11 | Join por IP (PC B) | con el navegador cerrado, [Join] con IP y puerto a mano: entra igual | ⬜ 2 PCs |
| 12 | Invitación Steam | desde A, "Invite via Steam" → B acepta desde el overlay → entra por el mismo `TryBeginSteamJoin` | ⬜ 2 PCs |

**Los pasos 5 y 6 son el siguiente corte, y no necesitan el segundo PC.** Hasta hacerlos, que Steam
arranque **no** prueba que el anuncio salga ni que la consulta conteste: son llamadas distintas de
la API y ninguna se ha ejecutado nunca contra Steam de verdad.

Extras que conviene pasar en la misma vuelta:

- **Steam cerrado en B**: el navegador da *"No se pudieron obtener las partidas."* y el Join por IP
  sigue funcionando. Es la coexistencia, y es lo único que se puede probar con una sola máquina.
- `tools/dev/CheckOrphanBackends.ps1 -ExpectCount 0` antes y después de cada vuelta.

Hasta que 1–12 estén hechos, esto **no es** un server browser validado.

## 13. Riesgos abiertos

1. **Nada jugado.** Ni una vuelta con Steam de verdad. Los tests prueban reglas, y la suite real
   confirma que las reglas se cumplen — no que Steam conteste lo que creemos.
2. **App ID 480 (Spacewar)** es compartido: el filtro `bs_game` es lo único que separa nuestros
   lobbies del resto del mundo. Con el App ID propio esto deja de importar.
3. **La IP la escribe el humano** (§6). Con `127.0.0.1` el lobby es inútil fuera de la máquina, y
   nada avisa.
4. **`SessionMaxPlayers = 50` es una constante espejo** sin comprobación automática.
5. **La costura del botón es por nombre** (`JoinSessionCanvas/Panel`); si se renombra, desaparece
   en silencio.
6. **`ServerBrowserBootstrap` se auto-instala** con `RuntimeInitializeOnLoadMethod`: singleton
   nuevo, contra la convención, justificado sólo mientras dure el puente.
7. **`FacepunchSteamLobbyQuery` suelta la Task en vez de cancelarla** (Facepunch no deja): la
   respuesta tardía se descarta, pero la petición sigue viva en Steam hasta que conteste.
8. **Sin NAT traversal ni relay.** El endpoint es IP:puerto directo: sin reenvío de puertos, media
   lista será inalcanzable aunque la ficha diga que sí. Fuera de alcance; pediría ADR.
9. **El lobby de Steam admite 8 miembros** (`SteamLobbyManager.MaxLobbyMembers`) y se anuncia
   `bs_max = 50`. No se contradicen porque **entrar por el navegador NO entra en el lobby de
   Steam** —se conecta por IP:puerto y el lobby es sólo la ficha del directorio—, pero **las
   invitaciones sí**: a partir de 8 invitados el overlay deja de admitir. Limitación conocida.

## 14. Steam: inicialización y App ID

### 14.1 El fallo que lo tapaba todo

Hasta el 2026-08-31, el `Player.log` de cualquier build decía:

```
[SteamLobbyManager] SteamClient.Init(480) failed: steam_api64 assembly:<unknown assembly> type:<unknown type> member:(null). Steam path disabled; manual Host/Join unaffected.
```

Eso es un `DllNotFoundException`, no "Steam cerrado". El binding **managed** de Facepunch
(`Facepunch.Steamworks.Win64.dll`) estaba en el proyecto desde el principio; el **nativo**
`steam_api64.dll` del SDK de Steamworks **no**:
`Assets/Plugins/Facepunch.Steamworks/redistributable_bin/` era una carpeta vacía. Consecuencia
completa: Steam nunca inicializó, **ni en el Editor ni en ningún build**, así que el lobby, las
invitaciones y el navegador de servidores nunca han corrido de verdad. El código de invitación es
correcto y jamás se había ejecutado.

Lo que se hizo:

- se puso `steam_api64.dll` en `Assets/Plugins/Facepunch.Steamworks/redistributable_bin/win64/`,
  con su `.meta` marcando **Editor (x86_64/Windows)** y **Standalone Win64 (x86_64)**. **De la
  versión correcta, que no es la más nueva** — ver §14.1b;
- `SteamLobbyManager.TryInitSteam` separa `DllNotFoundException` del resto y lo saca por
  `LogError` **nombrando el fichero y las dos rutas donde tiene que estar**. El mensaje genérico
  ("Steam unavailable") es lo que hizo que se leyera durante semanas como un problema del cliente
  de Steam;
- `SteamAppIdBuildPostprocessor` **falla el build** si el nativo no llegó a
  `<exe>_Data/Plugins/x86_64/`. Misma disciplina que `BackendBuildPostprocessor`: un player que no
  puede hablar con Steam es peor que un build que no sale, porque el fallo se descubre en el
  playtest y no aquí.

### 14.1b El segundo fallo: el nativo NUEVO tampoco vale

Con el SDK **1.65** puesto, el `DllNotFoundException` desapareció y salió otro, medido en el
ejecutable real:

```
[SteamLobbyManager] SteamClient.Init(480) failed: EntryPointNotFoundException: SteamAPI_SteamApps_v008
```

El nativo cargó; lo que no cuadra son las **versiones de interfaz**. Comparando los exportados de
los dos ficheros:

| | `ISteamApps` |
|---|---|
| `steam_api64.dll` del SDK 1.65 | exporta `SteamAPI_SteamApps_v009` |
| `Facepunch.Steamworks.Win64.dll` vendorizado | pide `SteamAPI_SteamApps_v008` |

**El binding managed manda sobre la versión del nativo, y va por detrás del SDK.** El vendorizado
pide además `SteamTimeline_v004` (SDK ≥ 1.60) y `SteamUGC_v020`, lo que lo sitúa en **SDK 1.61** —
que es el que está puesto (319 584 bytes, `sha256:670D654AA3255C50…`). Ese fichero aparece **byte
a byte idéntico** en tres juegos de editores distintos instalados en la máquina, o sea que es el
redistribuible de Valve sin tocar.

Del único accessor que el 1.61 no exporta, `SteamAPI_SteamAppList_v001`, no depende nada:
`ISteamAppList` es una interfaz restringida a partners aprobados y `SteamClient.Init` no la
inicializa.

`TryInitSteam` tiene ahora un `catch` propio para `EntryPointNotFoundException` que **dice que el
problema es la versión del SDK**, no el cliente de Steam. Sin él, este fallo manda a mirar Steam,
que estaba perfectamente.

> **Regla que se lleva:** al actualizar `Facepunch.Steamworks.Win64.dll` hay que actualizar el
> nativo **a la versión del SDK que ESE binding espera**. Coger el SDK más reciente es un fallo, no
> una precaución. Para saber cuál pide, leer los `SteamAPI_Steam*_v###` que el managed referencia.

**Cómo comprobarlo de un vistazo** (log del juego, no del backend):

```
[SteamLobbyManager] Steam init: app_id=480 (Spacewar / desarrollo) origen=AppIdFile (…/steam_appid.txt)
[SteamLobbyManager] Steam initialized app_id=480 (Spacewar / desarrollo) name=… steam_id=…
```

La primera línea se emite **antes** de intentar el Init: si el Init revienta, el log ya dice con
qué id se intentó y de dónde salió ese id.

### 14.2 De dónde sale el App ID

Fuente única: **`SteamAppConfig`** (`Assets/Scripts/Network/SteamAppConfig.cs`). Antes era una
constante `SpacewarAppId = 480` dentro del código de red, así que publicar exigía editar red.

| | Valor |
|---|---|
| `SteamAppConfig.DevAppId` | **480** — Spacewar, el id público de pruebas de Valve |
| `SteamAppConfig.ProductionAppId` | **5072740** — el del juego |

Resolución, en cuatro escalones, y **gana el primero que dé un entero positivo**:

1. **`BS_STEAM_APPID`** (variable de entorno) — el override de desarrollo y QA. Va primero para
   poder forzar 480 sobre cualquier cosa, incluso con un cliente de Steam delante;
2. **`SteamAppId`**, la variable que **pone el propio cliente de Steam al lanzar el proceso**. Es
   la respuesta más autorizada a *«¿qué app soy?»*: no se deduce, la dice quien nos arrancó.
   Gracias a este escalón **el mismo binario sirve para el juego (5072740) y para el playtest
   (5200320) sin recompilar y sin que 5200320 aparezca en ninguna constante de resolución**;
3. **`steam_appid.txt`** junto al ejecutable (en el Editor, la raíz del proyecto) — el mecanismo de
   Valve para arrancar **fuera** del cliente. Leerlo aquí es lo que garantiza que el fichero y el
   argumento de `SteamClient.Init` **no puedan discrepar**. Si discrepan, Steam rechaza el Init y
   no dice por qué. **No se empaqueta en el depot** — ver §16;
4. la **constante compilada**: `DevAppId` en Editor y en Development build, `ProductionAppId` en
   release.

Un valor degenerado en un escalón (vacío, `0`, no numérico) **cae al siguiente**; no gana ni
bloquea. Hay test por cada caso (`SteamAppConfigTests`).

**El default de release es producción a propósito.** Un release que saliera con 480 funciona,
lista lobbies ajenos y nadie se entera hasta que un jugador ve partidas de otro juego.

### 14.3 Cómo se cambia de id sin recompilar

El build escribe su `steam_appid.txt` solo (`SteamAppIdBuildPostprocessor`: 480 en Development,
5072740 en release). Para cambiarlo después:

```bash
pwsh ./tools/dev/SetBuildSteamAppId.ps1 -AppId 480
```

Sin `-BuildFolder` toma el build más reciente bajo `Builds/`. Avisa si falta el nativo y si hay un
`BS_STEAM_APPID` en la sesión que le ganaría al fichero.

### 14.4 Qué hace falta para que Steam arranque

Las cuatro, y el log dice cuál falla:

1. el **cliente de Steam abierto y con sesión iniciada** en esa máquina;
2. **`steam_api64.dll`** en el build (`<exe>_Data/Plugins/x86_64/`) **y de la versión de SDK que
   pide el binding** (hoy 1.61) — si falta, `LogError` nombrando el fichero; si está pero es de
   otra versión, `LogError` distinto con `EntryPointNotFoundException` y el número de SDK (§14.1b);
3. **`steam_appid.txt`** junto al exe (o `BS_STEAM_APPID`) con un id al que esa cuenta tenga
   acceso. 480 lo tiene todo el mundo; 5072740 sólo las cuentas del partner hasta que se publique;
4. **no hace falta lanzarlo desde Steam.** Con `steam_appid.txt` al lado, un doble clic al `.exe`
   inicializa igual. Lanzar desde Steam es lo que hará el jugador final, y también vale.

### 14.5 Lo que el App ID NO cambia

Nada del transporte. Steam mueve **metadatos** (`connect_ip`, `connect_port` y las claves `bs_*`);
el UDP sigue yendo directo entre backends. Cambiar de 480 a 5072740 no toca el wire, ni el
backend, ni el Join por IP. El filtro `bs_game` se mantiene aunque con id propio deje de ser
imprescindible: es gratis y protege del día en que alguien arranque con `BS_STEAM_APPID=480`.

## 15. El `10054`: qué era y qué no era

Primer Join real por el navegador, 2026-08-31. El backend joiner escupía en bucle:

```
UDP recv error: Se ha forzado la interrupción de una conexión existente por el host remoto. (os error 10054)
```

### Qué significa 10054, medido y no supuesto

En Windows, `recvfrom` sobre un socket UDP devuelve `WSAECONNRESET (10054)` cuando **un datagrama
que TÚ enviaste** rebotó como ICMP *port unreachable*. Es el sistema operativo diciendo *«no hay
nadie en el destino»* — no un fallo del socket, ni del bind, ni del protocolo.

Reproducido con dos sondas contra la misma máquina, una con host vivo y otra a un puerto muerto:

```
[host vivo]     127.0.0.1:7778 -> TIMEOUT sin error => HAY listener, datagrama aceptado
[puerto muerto] 127.0.0.1:7999 -> OSError 10054      => ICMP port-unreachable, NO hay listener
```

Y el backend confirmó la entrega de la primera:

```
NETPROBE event=datagram_received local=0.0.0.0:7778 from=127.0.0.1:64892 bytes=14 first_from_this_addr=true
```

**Conclusión: 10054 es SIEMPRE un síntoma.** Uno por reintento de handshake — el joiner registró
9-10, que es exactamente lo que dice la UI (*«tras 10 intentos en 15 s»*). Buscar la causa en el
transporte es perder el tiempo; la causa está en el DESTINO.

### El flujo, auditado extremo a extremo

Nada entre el navegador y el backend transforma el endpoint. Probado con los logs de aquel intento:

| Eslabón | Valor |
|---|---|
| Lo que anunció Steam | `[SteamLobbyManager] Lobby created id=… connect=127.0.0.1:7778` |
| Lo que pidió el navegador | `[ServerBrowser] Join solicitado a 127.0.0.1:7778` |
| Lo que lanzó `NetworkInitializer` | `Launch config: … role=joiner, CONNECT_TO=127.0.0.1:7778` |
| Lo que recibió el backend | `# env CONNECT_TO=127.0.0.1:7778` |
| Puertos del joiner | `UDP bound on 0.0.0.0:7779`, `IPC … 127.0.0.1:7778` — **sin colisión** con el host (UDP 7778 / TCP 7777) |

El navegador **pasa el par publicado tal cual**. Descartado como sospechoso, y con él toda la
cadena `LobbyJoinRouter → TryBeginSteamJoin → StartAsJoiner`.

### Las dos causas, que son distintas y se arreglan distinto

**A — El lobby anunciado ya no tenía host.** Es lo que produjo *esos* 10054. Línea temporal de
`Builds/…/PlaytestLogs/`: el último host murió a las **00:59:09**, los dos Joins fueron a las
**01:04:28** y **01:04:56**, y el siguiente host no arrancó hasta las **01:05:43**. No había
ningún `backrooms_server` escuchando en ese hueco.

**B — Se publicaba `127.0.0.1`.** En la MISMA máquina funciona por casualidad: el backend hace bind
en `0.0.0.0`, que cubre loopback. **En cualquier otra máquina significa esa otra máquina**, así que
el Join no falla — va a otro sitio, y el fallo tarda 15 s en manifestarse.

### El arreglo

`LobbyEndpointPolicy` (puro, con tests) + `LocalAddressProbe` (toca el sistema, sin tests):

1. **manda lo que el humano escribió** si es publicable — puede ser una IP pública con reenvío de
   puertos, que la máquina no puede deducir sola;
2. si no, **la dirección de la interfaz de la ruta por defecto**, resuelta con un `Connect` UDP a
   una dirección de documentación (RFC 5737): en UDP eso **no transmite nada**, sólo obliga al
   sistema a elegir interfaz, que se lee con `LocalEndPoint`. En esta máquina devuelve
   `192.168.1.40`, la correcta;
3. si tampoco, **no se anuncia la partida** y se dice por qué. *Publicar un endpoint malo cuesta
   más que no publicar*: al que lo elige le cuesta 15 s y un mensaje que no le explica nada.

**«La primera IPv4 que no sea loopback» habría sido otro fallo.** Esta máquina tiene diez IPv4:
la buena (`192.168.1.40`, Ethernet/DHCP), dos de VPN (`10.5.0.2` NordLynx, `26.213.115.149`
Radmin) y **seis APIPA `169.254.x.x`**. Publicar una APIPA le falla al joiner igual de silencioso
que loopback. Se descartan loopback, `0.0.0.0`/`::` y link-local/APIPA; el resto se ordena y el
llamante decide.

**Riesgo anotado:** con una VPN enrutando, la ruta por defecto es la de la VPN y eso es lo que se
publica. Es la respuesta correcta a la pregunta que se hace («¿por dónde salgo?») y puede no ser la
que el humano quiere — para eso el campo del panel gana.

Y por qué el mismo PC sigue funcionando: publicar `192.168.1.40` en vez de `127.0.0.1` **no rompe**
el Join local, porque el backend escucha en `0.0.0.0` y el sistema cortocircuita a loopback una
dirección propia.

### El agujero de observabilidad que hizo caro el diagnóstico

En la sesión del fallo el log tenía **tres `Lobby created` y cero `anuncio retirado`**, lo que
parecía una fuga y no lo era: `SessionEndHandler` retira por `SteamLobbyManager.LeaveLobby()`, que
hace `Leave()` y vacía `_hostedLobby` **sin log**. El lobby sí moría. Pero como `LeaveLobby` corría
primero, el conductor veía `IsPublishing == false`, su `Withdraw()` no llegaba a ejecutarse,
`WithdrawCount` no subía y **la invariante I14 no cubría la puerta que de verdad se usa**.

Arreglado invirtiendo el orden: `ServerBrowserBootstrap.WithdrawAnnouncement()` **antes** de
`LeaveLobby()`, en las dos rutas de teardown (`SessionEndHandler` y
`JoinSessionUI.ShowRecoverablePanel`). Las dos son idempotentes, y `LeaveLobby` sigue haciendo
falta después porque además suelta el lobby **ajeno** en el que se entró por invitación.

## 16. Subida a SteamPipe (Playtest, App 5200320)

### Las tres apps, y por qué son tres

| App | Id | Qué |
|---|---|---|
| Juego | `5072740` | la app de venta. `SteamAppConfig.ProductionAppId` |
| **Playtest** | **`5200320`** | app **APARTE**, con su propio depot `5200321` |
| Spacewar | `480` | el id público de pruebas de Valve, sólo desarrollo |

**El id del playtest NO está en ninguna constante de resolución, a propósito.** Lo dice el cliente
de Steam por la variable `SteamAppId` (§14.2, escalón 2), así que **el mismo binario vale para las
tres** sin recompilar. Una constante por app obligaría a un build por app y a acordarse de cuál es
cuál — que es exactamente el error del que veníamos.

### Ficheros

- `tools/steam/app_build_5200320.vdf` — App 5200320, `ContentRoot` a `Builds/Build_steam`.
  **Sin la clave `SetLive`**, y eso no es un olvido: sin ella el build se sube y queda registrado en
  Steamworks **sin asignar a ninguna rama**. Añadirla activaría el playtest.
- `tools/steam/depot_build_5200321.vdf` — Depot 5200321, todo recursivo menos tres exclusiones.
- `J:\SteamBuild\ContentBuilder\` — el ContentBuilder oficial, extraído del SDK. **Fuera del repo**.

### Las tres exclusiones, cada una con su motivo

1. **`steam_appid.txt`** — Valve dice que no se envíe, y aquí además es activo: `SteamAppConfig`
   **lo lee**, así que un fichero con `480` dentro del paquete inicializaría el juego como Spacewar
   en la máquina de cada tester, por delante de lo que Steam dijera. Sin lobby, sin invitaciones,
   sin navegador. Existe sólo para lanzar el `.exe` a mano fuera de Steam.
2. **`Builds\`** — carpeta anidada con los logs de backend de las pruebas locales (10 ficheros,
   6,5 MB) con rutas absolutas de la máquina de desarrollo y nombres de persona de Steam dentro.
3. **`*_BurstDebugInformation_DoNotShip`** — lo nombra Unity.

### El procedimiento

El login es un paso **humano**: pide contraseña y código de Steam Guard. Una vez hecho, steamcmd
cachea la sesión y la subida ya no necesita secretos.

```bash
J:\SteamBuild\ContentBuilder\builder\steamcmd.exe +login TU_USUARIO +quit
```

Ensayo sin subir nada (`"Preview" "1"` en el `.vdf`) para revisar que las exclusiones hacen lo que
se cree — el manifiesto sale en `J:\SteamBuild\output\`:

```bash
J:\SteamBuild\ContentBuilder\builder\steamcmd.exe +login TU_USUARIO +run_app_build "J:\Unity\BackroomsSurvivalMMO\tools\steam\app_build_5200320.vdf" +quit
```

Con `"Preview" "0"` esa misma orden sube de verdad. Al terminar, el build aparece en
Steamworks → *Builds*, **sin rama asignada**. Activarlo es un acto aparte y manual.

### Subida ejecutada — 2026-08-31 02:09

| | |
|---|---|
| **BuildID** | **25027542** |
| Manifest del depot 5200321 | `810281909723404086` |
| Contenido | **712 ficheros, 2,12 GB, 2713 chunks** (todos nuevos: no había manifiesto base) |
| Resultado | `Successfully finished AppID 5200320 build`, exit 0 |
| Rama | **ninguna** — sin `SetLive`, comprobado a posteriori: cero coincidencias de `SetLive`, `Setting build live` ni `branch` en toda la salida |

Exclusiones verificadas contra el manifiesto del ensayo antes de subir: **0** entradas de
`steam_appid.txt`, `PlaytestLogs`, `backend_host_`, `backend_joiner_` y `.log`; y presentes el exe,
`UnityPlayer.dll`, `steam_api64.dll`, `backrooms_server.exe`, `BackroomsSurvival.dll`,
`Facepunch.Steamworks.Win64.dll` y los dos manifiestos de StreamingAssets. De
`*_BurstDebugInformation_DoNotShip` sobrevive **la carpeta vacía** (entrada de directorio, 0 bytes):
el fichero de dentro sí se excluyó.

**Trampa del método, anotada porque costó una comprobación mal dada:** en PowerShell,
`Select-String -SimpleMatch` con un patrón pasado por `[regex]::Escape` busca literalmente
`steam_appid\.txt`, que no existe en ningún manifiesto. Da **cero coincidencias siempre**, así que
una exclusión rota y una exclusión correcta se ven idénticas. O `-SimpleMatch` sin escapar, o
regex con escape; nunca las dos.

**Lo que NO se ha verificado desde aquí:** que Steamworks muestre el build en su panel. Eso exige
entrar en partner.steamgames.com con la cuenta, y no es algo que se haga por terminal. La evidencia
local es la línea de éxito de SteamPipe con su BuildID.

---

## 17. La puerta a internet: IP pública y UPnP (ADR-112)

Hasta hoy, para que alguien de fuera entrara en tu partida había que meterse en el router y
redirigir un puerto UDP a mano. Esa barrera se la iba a comer entera el Playtest de Steam.

### Qué hace ahora el host

Al hostear, y **en segundo plano** (no bloquea nada, no se consulta en ningún `Update`):

1. Busca un router UPnP en la red local por SSDP, con el socket **atado a la interfaz de la ruta
   por defecto**. Esto no es un detalle: esta máquina tiene diez IPv4 —la buena, dos de VPN y seis
   APIPA— y un socket sin atar puede mandar el descubrimiento por el túnel de la VPN, donde no hay
   ningún router doméstico escuchando. El síntoma sería idéntico al de "no hay UPnP".
2. Le pregunta al router su IP de WAN (`GetExternalIPAddress`). Si no hay router, consulta hasta
   tres ecos HTTP de dueños distintos.
3. Le pide un reenvío UDP del puerto **realmente elegido** (no el tecleado), con caducidad de una
   hora.
4. **Lo relee.** Ver abajo.
5. Publica en el lobby sólo lo que ha podido confirmar.

### Por qué la relectura no es paranoia

`AddPortMapping` devolviendo 200 **no demuestra que exista el mapeo**. Hay routers que aceptan la
petición y aplican otra cosa: otro cliente interno, otro puerto, o la dejan deshabilitada. Si
"confirmado" se dedujera del OK, el host anunciaría una IP pública que reenvía **al PC del vecino**,
y el joiner que la eligiera se comería quince segundos sin entender nada. La única prueba es
`GetSpecificPortMappingEntry` comprobando que el cliente interno y el puerto son los nuestros.

Por eso el estado es una escalera de cinco peldaños y no un booleano. Y por eso ninguno de los cinco
dice "internet funciona": el último —peer remoto observado— lo enciende igual un joiner de la misma
LAN.

### Qué acaba en `connect_ip`

Precedencia completa, de la que sólo el segundo escalón es nuevo:

1. **Lo que escribió el humano en el panel**, si sirve. Puede saber algo que nosotros no.
2. **La IP pública con mapeo CONFIRMADO y sin sospecha de CGNAT.**
3. **La dirección local**, que es lo que se anunciaba antes de todo esto.
4. **Nada, y la partida no se anuncia.**

`LobbyEndpointPolicy` no cambió: sigue siendo pura y sigue recibiendo los candidatos ya ordenados.
Lo único nuevo es quién va primero en esa lista.

### `bs_lan_ip`, y por qué el reintento y no el primer intento

Con la IP pública en `connect_ip`, un joiner de la **misma red** sólo llega si el router hace
hairpin (NAT loopback), y muchos routers domésticos no lo hacen: la mejora habría roto lo que ya
funcionaba. El host publica además su LAN en `bs_lan_ip` y el joiner la usa en el **[Retry]**.

No en el primer intento, y esto es la decisión: **desde el joiner no se puede saber si está en la
misma red que el host.** Dos casas distintas pueden ser las dos `192.168.1.0/24`. Adivinarlo
mandaría a gente de fuera a una dirección privada, que es el fallo contrario y peor. Primero lo
anunciado, la alternativa después.

La clave está declarada **dos veces** —`SteamLobbyKeys.LanIp` y `SteamLobbyManager.LanIpKey`— con
su comprobación en `SteamLobbyKeyParity`, como todas las demás: si divergen, el host publica con
una y el navegador lee con otra, y la lista sale vacía sin un solo error.

### CGNAT: lo que no tiene arreglo

Si el operador te mete detrás de su propio NAT (`100.64.0.0/10`, RFC 6598) compartes IP pública con
cientos de abonados y **no hay puerto que reenviar**. Ninguna cantidad de UPnP lo arregla. Se
detecta por tres indicios, en orden de fuerza: WAN del router en ese rango; WAN privada (doble NAT);
o el router y el mundo no ven la misma dirección. Es una **heurística** y se llama así: demostrarlo
pediría STUN, y STUN es un cambio de transporte.

Con CGNAT sospechado se avisa, no se anuncia la IP pública, y **no se bloquea nada**: la partida
sigue sirviendo en LAN y por invitación de Steam a quien alcance.

### Los cinco códigos

`UPNP_UNAVAILABLE`, `UPNP_DISABLED`, `UPNP_MAPPING_FAILED`, `CGNAT_SUSPECTED`,
`PUBLIC_ENDPOINT_UNKNOWN`. Viajan al log **literales**, para poder buscarlos con grep en el
`Player.log` de un tester copiando lo que dice esta página. Se filtran por `NATPROBE` y por
`[HostConnectivity]`.

### Privacidad y interruptores

Al eco externo sale un `GET` sin cuerpo. El servicio ve la dirección desde la que se le pregunta
—que es lo que se le está preguntando— y nada más: ni nombre, ni identificador de jugador, ni de
partida. Se apaga con `BS_NO_PUBLIC_IP_LOOKUP=1`; el UPnP entero, con `BS_NO_UPNP=1`. Con los dos
puestos se hostea en LAN exactamente igual que siempre.

### Lo que NO está validado

**No hay ningún IGD en la máquina de desarrollo.** Medido el 2026-08-31: M-SEARCH multicast atado a
`192.168.1.40` con el `ST` de `InternetGatewayDevice` y con `ssdp:all` → cero respuestas; unicast a
`192.168.1.1:1900` → timeout. Así que aquí se ha podido validar **el camino de fallo**, no el de
éxito con hardware real. Lo que sí está ejecutado es el comportamiento del cliente contra un router
de mentira que habla HTTP de verdad, incluido el caso del router que acepta el mapeo y aplica otro
cliente interno.

Lo que falta, con nombre: (a) un host con router UPnP de verdad; (b) un host sin él, comprobando
que sigue sirviendo en LAN; (c) dos máquinas en redes distintas.

## 18. La tercera vía: el relay de respaldo (ADR-117)

El playtest del 2026-09-02 midió lo que faltaba. El navegador anunciaba
`A12ex — 192.168.1.168:7778` —una IP privada— y el join fallaba. Pero la auditoría midió algo más
importante: el join **manual** a la IP pública del host también agotó los 15 s. No faltaba un
anuncio mejor; **no había ningún camino directo que anunciar**. Ninguno de los dos routers
respondió al SSDP y ninguno de los dos puertos estaba reenviado.

UPnP (§17) arregla el caso del router que colabora. El relay arregla el resto.

### Qué es y qué no

Un proceso aparte (`backrooms_relay`) que reenvía datagramas entre el host y sus joiners. **No
simula nada**: el payload es opaco, la autoridad sigue entera en el backend del host (ADR-015), y
el wire del juego no cambia ni un byte. Sigue siendo nuestro UDP, con un salto más.

No es NAT traversal ni hole punching. Steam Networking Sockets, Steam Datagram Relay, STUN y TURN
de terceros siguen fuera de R1.

### Las tres vías, en orden

```
Direct ──5 s──▶ Lan ──3 s──▶ Relay ──12 s──▶ Failed
```

Las etapas que no existen se saltan. Una partida en LAN de siempre tiene una sola etapa y se
comporta exactamente como antes. Cada salto escribe su `fallback_reason`, y el fallo final nombra
las tres vías con sus direcciones — el jugador ve en el panel qué se intentó sin abrir un log.

Los presupuestos no son redondeos: con el puerto abierto el `HandshakeAck` vuelve en decenas de
milisegundos incluso entre continentes, así que 5 s distinguen «lento» de «no está»; el relay
necesita 12 porque tiene dos pasos, registrarse y luego el handshake.

### Las cinco claves nuevas del lobby

| clave | qué lleva |
|---|---|
| `bs_relay_addr` | dónde está el relay |
| `bs_relay_session` | el id de sesión que generó el host |
| `bs_relay_token` | 32 hexadecimales, el secreto de sesión |

Valen **como un bloque**: o están las tres, con el token de la longitud que el backend exige, o no
hay relay. Media sesión no sirve para entrar, y admitirla convertiría un lobby mal publicado en uno
que promete algo que no puede cumplir.

Y `connect_ip` cambia de significado: **una IP privada ya no se publica ahí cuando hay relay**. Se
queda en `bs_lan_ip`, donde significa lo que es. Sin relay sí se publica, y eso no es incoherencia:
una partida en LAN pura no tiene otra forma de anunciarse, y quitársela para cumplir una regla
pensada para internet sería romper lo que ya funcionaba.

**Un lobby sin `connect_ip` pero con relay es entrable.** Antes salía como `InvalidEndpoint` con el
botón apagado; es exactamente lo que le pasó a Alejandro.

### El token, y por qué está en metadata pública

Impide que el relay sea un **relay abierto**: sin token no se crea ni se entra en ninguna sesión,
así que nadie que no haya visto la lista puede usarlo de reflector. Lo que NO hace es defender de
quien sí ve el lobby — que es justo quien tiene derecho a entrar. Es el mismo nivel de confianza
que la partida ya tenía. Tickets de Steam es R2.

No se escribe en ningún log: el tipo que lo transporta se niega a imprimirlo, en Rust y en C#.

### Cómo se enciende

El relay por defecto está **vacío** a propósito: sin máquina que lo aloje, una dirección inventada
haría que cada host gastara 10 s de registro contra un puerto que no existe. Se enciende poniendo
la dirección en `RelaySessionCredentials.DefaultRelayAddress` o en la variable `BS_RELAY_ADDR`.

El relay se lanza con `cargo run -p backrooms_relay`; escucha en `0.0.0.0:7790` y se configura con
`RELAY_BIND`, `RELAY_MAX_SESSIONS`, `RELAY_MAX_PEERS`, `RELAY_PEER_TIMEOUT_SECS`,
`RELAY_IDLE_TIMEOUT_SECS` y `RELAY_HANDSHAKES_PER_SEC`.

### Qué buscar en el log

`CONNECTIVITY transport=direct|lan|relay`, con `stage=start|fallback|connected` y
`fallback_reason=…`. Y del lado del relay, `RELAY event=session_created|peer_joined|ready|denied`.
La línea que contesta «¿esta partida fue por relay?» es la de `stage=connected`.

Un peer alcanzable sólo por relay aparece en las trazas con una dirección de este aspecto:

```
HBTRACE event=LIVENESS_SCAN peer_id=7 endpoint=[fd52:5245:4c41:5900::beef]:3
```

No es una IPv6 de verdad: es una dirección sintética (ADR-117 D3) que deletrea `RELAY` en ASCII y
lleva dentro la sesión y el peer. Verla ya dice que ese peer va por relay.

### Lo que NO está validado

**Nadie ha entrado todavía por un relay que esté en internet.** Lo probado es: el relay real con
tres procesos en una máquina (host, joiner y relay, con el joiner sin conocer ninguna dirección del
host), y toda la lógica en tests. Falta desplegarlo en una máquina pública y repetir el playtest
que originó todo esto, con las dos personas en redes distintas.
