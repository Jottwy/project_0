using System;
using BackroomsSurvival.UI;
using PolymindGames;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-056 — ends the session when the host goes away, y desde la tanda de ciclo de vida
    /// (2026-08-30) el DUENO del teardown de sesion, venga de donde venga.
    ///
    /// The backend raises `session_ended` when the peer that left was the host. There is no host
    /// migration, so what remains is a world that cannot advance: chunk displacement is gated on
    /// the host, the STP rosters freeze on their last snapshot, and every request aimed at the
    /// host is dropped in silence. Rather than leave the player in a world that looks alive, this
    /// tears the session down and returns to the main menu.
    ///
    /// It cables together two halves that already existed and did not know about each other:
    /// NetworkInitializer.Shutdown() (kills the backend with a graceful save, but never leaves the
    /// scene) and STP's LevelManager.CloseCurrentGame (goes back to the menu, but never touches
    /// the network). CloseCurrentGame is called as public vendor API — nothing under
    /// Assets/PolymindGames/ is edited.
    ///
    /// LA TERCERA MITAD, que faltaba: el vendor tiene su PROPIO camino de vuelta al menu.
    /// <c>PauseMenu.QuitToMenu()</c> llama a <c>LevelManager.CloseCurrentGame</c> a pelo, sin
    /// pasar por nada de red. Salir al menu por ahi dejaba el backend VIVO (el backend no se
    /// mata solo al caerse el IPC: su rama de `local_disconnect_rx` guarda y sigue), el IPC
    /// conectado a el, y `JoinSessionUI._loadingGameplay` en true para siempre - con lo que el
    /// siguiente Join no cargaba escena ninguna. No se puede editar el vendor, asi que el
    /// enganche es <see cref="SceneManager.activeSceneChanged"/>: si la escena cambia y habia una
    /// sesion viva, el teardown corre igual.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class SessionEndHandler : MonoBehaviour
    {
        private const string SessionEndedEvent = "session_ended";
        private const string SessionJoinedEvent = "session_joined";

        [SerializeField]
        [Tooltip("Menu scene to return to when the session ends. Must be in Build Settings.")]
        private string _mainMenuScene = "STP_MainMenu";

        private IPCClient _ipc;
        private bool _ending;

        private static SessionEndHandler _instance;

        private void Awake()
        {
            if (_instance == null) _instance = this;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        private void Update()
        {
            // The IPCClient singleton is created by whichever bootstrap runs first, so this
            // subscribes on the first frame it exists rather than assuming an ordering. Y se
            // RE-suscribe si la instancia cambia (mismo criterio que
            // `JoinSessionUI.EnsureSessionEventSubscription`): antes se quedaba pegado a la
            // primera para siempre, asi que un IPCClient recreado dejaba el fin de sesion sin
            // oyente y la sesion no se podia terminar nunca.
            IPCClient.TryGetInstance(out var ipc);
            if (ReferenceEquals(_ipc, ipc)) return;

            if (_ipc != null) _ipc.RemoveEventListener(OnGameEvent);
            _ipc = ipc;
            if (_ipc != null) _ipc.AddEventListener(OnGameEvent);
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);
            if (_instance == this) _instance = null;
        }

        // ─── El enganche de escena ───────────────────────────────────────────────────────────
        //
        // Dos preguntas distintas, las dos contestadas por el MISMO evento porque las dos son
        // "la escena activa cambio y la sesion tiene que enterarse":
        //
        //   1. Connected -> InGame. La escena de juego termino de cargar. Es lo que hace que el
        //      cursor pase a politica de partida y que sepamos, despues, de que escena salimos.
        //   2. InGame -> se fue. Cualquier cambio de escena estando dentro del mundo y sin
        //      teardown en marcha es una salida: el `Quit to Menu` del vendor, un LoadScene de
        //      un arnes, lo que sea. No se compara contra el NOMBRE del menu a proposito - el
        //      `SerializedScene` del PauseMenu del vendor es un campo suyo y puede no coincidir
        //      con `_mainMenuScene`; lo que sabemos seguro es de que escena saliamos.
        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            var state = SessionState.Current;

            if (state.Phase == SessionPhase.Connected)
            {
                if (state.NotifyEnteredWorld(next.name))
                    Debug.Log($"[SessionEndHandler] Sesion dentro del mundo (escena '{next.name}')");
                return;
            }

            if (state.Phase == SessionPhase.InGame && next.name != state.WorldScene)
            {
                Debug.LogWarning(
                    $"[SessionEndHandler] Se abandono la escena de juego '{state.WorldScene}' -> " +
                    $"'{next.name}' sin pasar por el teardown de red. Se limpia la sesion aqui.");
                // El menu ya se esta cargando, asi que NO se pide otra carga de escena, y no se
                // pinta "Sesion terminada": salir al menu es voluntario y el menu del juego ya es
                // la UI. Lo que si tiene que pasar es TODO lo demas: matar el backend, parar el
                // IPC, cerrar el lobby, rearmar el panel y soltar el cursor.
                LeaveSession("left to menu", showPanel: false, returnToMenu: false);
            }
        }

        // ─── Entradas ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Punto de entrada UNICO para "abandona la sesion", desde cualquier sitio y sin
        /// necesitar la referencia al componente. Idempotente: la segunda llamada seguida no
        /// hace nada, porque la que manda es
        /// <see cref="SessionStateMachine.RequestLeave"/> y esa ya no acepta.
        /// </summary>
        public static void LeaveCurrentSession(string reason, bool showPanel)
        {
            if (_instance != null)
            {
                _instance.LeaveSession(reason, showPanel, returnToMenu: true);
                return;
            }

            // Sin componente (escenas de prueba, arneses): el teardown de red igual tiene que
            // correr. Lo unico que se pierde es la vuelta al menu, que no existe sin LevelManager.
            Debug.LogWarning("[SessionEndHandler] No hay instancia; teardown de red sin vuelta al menu.");
            TeardownNetworkResources(reason, showPanel, returnToMenu: false, mainMenuScene: null);
        }

        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev.eventType == SessionJoinedEvent)
            {
                // La confirmacion de que el backend local registro al host. Es la UNICA cosa que
                // convierte a un joiner en "conectado"; se aplica aqui, sobre la maquina, para
                // que no haya dos copias del gate.
                SessionState.Current.NotifySessionJoined();
                return;
            }

            if (ev.eventType != SessionEndedEvent) return;

            // The backend keeps emitting world state until Unity kills it, and the event can be
            // delivered more than once if the disconnect is detected twice (goodbye packet AND
            // heartbeat timeout). Ending twice would kill a backend that a NEW session had
            // already launched.
            if (_ending) return;
            _ending = true;

            string reason = ReadReason(ev);
            Debug.LogWarning($"[SessionEndHandler] Session ended (reason={reason}) — returning to menu");

            // IPCClient.NotifyListeners dispatches inside `try { h(ev); } catch { }`, so anything
            // thrown from here leaves NO trace and — worse — leaves `_ending` latched, which kills
            // session-end for the rest of the process. It is not hypothetical: LevelManager's
            // CloseCurrentGame runs ThrowIfSceneDoesNotExist BEFORE its IsLoadingOrSaving check, so
            // a `_mainMenuScene` that is missing from Build Settings throws ArgumentException.
            // Catch it here (not in IPCClient, which is shared by every other listener): log it,
            // and re-arm so a later session_ended can retry instead of stranding the player.
            try
            {
                LeaveSession(reason, showPanel: true, returnToMenu: true);
            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"[SessionEndHandler] EndSession threw — the session may be half torn down; " +
                    $"re-arming for the next event: {e}");
                _ending = false;
            }
        }

        /// <summary>
        /// Clears the once-per-session latch. Called by <see cref="NetworkInitializer"/> when a new
        /// session is being configured (host or join).
        ///
        /// The successful path deliberately leaves `_ending` set: the backend keeps emitting until
        /// Unity kills it, and the duplicate session_ended (goodbye packet AND heartbeat timeout)
        /// must not tear down whatever replaced that session. Nothing used to clear it again —
        /// and this component lives on the DontDestroyOnLoad NetworkInitializer object, so the
        /// latch survived the trip back to the menu and the SECOND session of a process could
        /// never end itself.
        /// </summary>
        public void ResetForNewSession()
        {
            if (!_ending) return;
            _ending = false;
            Debug.Log("[SessionEndHandler] Re-armed for a new session");
        }

        /// The event payload is the free-form object tree MsgPackReader.ReadValue produces —
        /// maps come back as Dictionary&lt;string, object&gt; — and every other consumer reads it
        /// through IPCParse. Same idiom here, rather than a hand-rolled cast that would have to
        /// be re-checked against the reader's actual output type.
        ///
        /// Public (not internal) so the EditMode suite can reach it: the compile-check builds each
        /// assembly with a `_check` suffix, so an InternalsVisibleTo would never find the right
        /// friend name — the same criterion already applied to InventoryRestorer.ParseStacks.
        public static string ReadReason(GameEventMsg ev)
        {
            var map = ev.data as System.Collections.Generic.Dictionary<string, object>;
            string reason = IPCParse.S(map, "reason");
            return string.IsNullOrEmpty(reason) ? "unknown" : reason;
        }

        /// One best-effort teardown step. Logs and swallows, so the remaining steps still run —
        /// the opposite of IPCClient's silent `catch { }`, which is what made this failure mode
        /// invisible in the first place.
        private static void Step(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SessionEndHandler] Teardown step '{what}' failed (continuing): {e}");
            }
        }

        private void LeaveSession(string reason, bool showPanel, bool returnToMenu)
        {
            _ending = true;
            TeardownNetworkResources(reason, showPanel, returnToMenu, _mainMenuScene);
            if (SessionState.Phase != SessionPhase.Disconnecting)
                _ending = false; // el teardown corrio entero: rearmado para la siguiente sesion
        }

        /// <summary>
        /// EL teardown. Un solo sitio, un solo orden, y el gate de idempotencia al principio.
        ///
        /// El orden importa y no es arbitrario:
        ///  1. `RequestLeave` primero. Es el gate: si devuelve false no habia sesion viva (o ya
        ///     hay un teardown en marcha) y no se toca NADA. Es lo que hace que "Leave dos veces"
        ///     sea inofensivo, y lo que impide que el `session_ended` duplicado (paquete de
        ///     despedida Y timeout de latido) mate una sesion que ya fue reemplazada.
        ///  2. Matar el backend CON el IPC todavia arriba: `Shutdown()` manda `save_and_shutdown`
        ///     por esa conexion y espera una salida limpia antes de recurrir a `Kill()`. Parar el
        ///     IPC antes costaria el guardado.
        ///  3. Aparcar el IPC. El puerto del backend esta muerto; sin esto el bucle de reconexion
        ///     lo marca el resto de la vida del proceso. NO `IPCClient.Shutdown()`, que se lleva
        ///     el singleton y el hilo: el cliente tiene que seguir reutilizable
        ///     (`ConfigureEndpoint` levanta la pausa en la siguiente sesion).
        ///  4. Cerrar el lobby de Steam, que publica un ip:puerto que acaba de morir.
        ///  5. Rearmar el panel. SOLO campos: destruirlo dejaria que el siguiente
        ///     `ShowConnectPanel` construyera una instancia nueva cuyo `Start()` repite el
        ///     auto-connect de SESSION_MODE/CONNECT_TO, en bucle.
        ///  6. Soltar el cursor. Pase lo que pase y haya panel o no: es la garantia de
        ///     "el cursor queda restaurado al volver al menu".
        ///  7. Cerrar la maquina. A partir de aqui `CanStart` vuelve a ser true.
        /// </summary>
        private static void TeardownNetworkResources(string reason, bool showPanel, bool returnToMenu, string mainMenuScene)
        {
            if (!SessionState.Current.RequestLeave(reason))
            {
                Debug.Log(
                    $"[SessionEndHandler] Leave ignorado (fase {SessionState.Phase}): no hay sesion " +
                    "viva que abandonar. Es idempotente a proposito.");
                return;
            }

            Debug.LogWarning($"[SessionEndHandler] Teardown de sesion (motivo={reason})");

            Step("backend shutdown", () =>
            {
                var init = NetworkInitializer.Instance;
                if (init != null)
                    init.Shutdown();
                else
                    Debug.LogWarning("[SessionEndHandler] No NetworkInitializer — backend not torn down");
            });

            Step("IPC reconnect pause", () =>
            {
                if (IPCClient.TryGetInstance(out var ipc) && ipc != null)
                    ipc.PauseReconnect();
            });

            // El ORDEN importa, y costó una sesión de diagnóstico. `LeaveLobby` cierra el lobby de
            // Steam pero vacía `_hostedLobby` por su cuenta y sin log, así que si corre primero el
            // conductor ve `IsPublishing == false`, su `Withdraw()` no llega a ejecutarse,
            // `WithdrawCount` no sube y en el log no queda constancia de que el anuncio se retiró
            // (tres `Lobby created` y cero `anuncio retirado` en la misma sesión). Retirar por el
            // publicador PRIMERO deja el rastro y mantiene la invariante I14 sobre la puerta que
            // de verdad se usa; `LeaveLobby` después sigue haciendo falta, porque además suelta el
            // lobby AJENO en el que se entró por invitación. Las dos son idempotentes.
            Step("steam announcement", UI.ServerBrowserBootstrap.WithdrawAnnouncement);

            // ADR-135 D10. Va ENTRE los dos de arriba, y el orden tiene motivo en las dos
            // direcciones:
            //
            //  - DESPUÉS del anuncio: mientras el lobby siga publicado hay gente leyendo
            //    `bs_steam_host` y llamando por él. Cerrar el túnel antes de retirar el cartel sólo
            //    cambia un fallo por otro — es el mismo argumento que ya gobierna el mapeo UPnP.
            //  - ANTES de `LeaveLobby`: el lobby es lo que AUTORIZA (el secreto vive en su
            //    metadata, D4'), así que soltarlo primero dejaría una ventana con el túnel abierto
            //    y su autorización ya sin dueño.
            //
            // No bloquea más de medio segundo: el bombeo cede el turno cada pocos milisegundos y
            // el teardown no puede esperar a nadie. Idempotente y segura sin Steam, como todas.
            Step("steam p2p tunnel", () =>
            {
                Connectivity.SteamTunnelRunner.Shutdown();
                // El secreto muere con la sesión: reutilizarlo dejaría entrar mañana a quien vio
                // el lobby de hoy (D4'). Es la misma regla que `RelaySessionCredentials.Reset`.
                Connectivity.SteamTunnelCredentials.Reset();
            });

            Step("steam lobby", () => SteamLobbyManager.Instance?.LeaveLobby());

            // El reenvío de puerto que este host le pidió al router. Va DESPUÉS de retirar el
            // anuncio y no antes: mientras el lobby siga publicado hay gente que puede estar
            // llamando, y cerrarles la puerta antes de quitar el cartel sólo cambia un fallo por
            // otro. El orden de los dos pasos de arriba no se toca — está documentado en el
            // comentario que los precede y costó una sesión de diagnóstico.
            //
            // No bloquea: `Release` lanza el borrado y vuelve. Si el router no contesta, el mapeo
            // caduca solo en una hora (por eso se pide con caducidad y no permanente).
            Step("upnp mapping", Connectivity.HostConnectivityRunner.Release);

            Step("connect panel reset", () =>
            {
                var ui = FindFirstObjectByType<JoinSessionUI>();
                if (ui != null)
                    ui.ResetForNewSession();
            });

            Step("cursor", SessionCursor.ReleaseToMenu);

            SessionState.Current.NotifyLeaveComplete(keepReason: showPanel);

            if (!returnToMenu || string.IsNullOrEmpty(mainMenuScene))
                return;

            Step("return to menu", () => ReturnToMenu(mainMenuScene));
        }

        private static void ReturnToMenu(string mainMenuScene)
        {
            if (SceneManager.GetActiveScene().name == mainMenuScene)
            {
                Debug.Log("[SessionEndHandler] Already in the menu scene — nothing to unload");
                return;
            }

            var level = LevelManager.Instance;
            if (level == null)
            {
                Debug.LogError("[SessionEndHandler] No LevelManager — cannot return to the menu");
                return;
            }

            // Only fails while a load/save is already in flight. La sesion ya esta limpia a estas
            // alturas, asi que un false aqui deja al jugador en un mundo muerto pero SIN sesion
            // fantasma; el panel (showPanel) explica por que.
            if (!level.CloseCurrentGame(mainMenuScene))
                Debug.LogWarning("[SessionEndHandler] LevelManager ocupado: no se pudo volver al menu ahora.");
        }
    }
}
