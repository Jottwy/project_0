using System;
using System.Collections.Generic;
using BackroomsSurvival.Lobbies;
using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// La entrada del navegador de servidores en el menú, y el cableado de las dos mitades:
    /// **descubrimiento** (Steam → navegador) y **anuncio** (host → Steam).
    ///
    /// **Por qué se auto-instala en vez de vivir en la escena.** El botón tiene que salir en el
    /// panel de multijugador, y ese panel NO está en ninguna escena: lo monta `JoinSessionUI` por
    /// código. Añadir un objeto a `STP_MainMenu.unity` habría tocado un fichero de escena que
    /// otras sesiones están editando a la vez, y un `.unity` no se fusiona: se pisa. Así que este
    /// componente se crea solo al arrancar el proceso e INYECTA su botón en la fila que el panel
    /// ya construye.
    ///
    /// Es un puente, y está pensado para retirarse: cuando el trabajo concurrente sobre
    /// `JoinSessionUI` cierre, las líneas de <see cref="AttachBrowseButton"/> se mueven dentro de
    /// su `BuildUI` (junto al botón de Steam, que es el mismo patrón) y este auto-arranque
    /// desaparece. Ver `docs/SERVER_BROWSER.md`.
    /// </summary>
    public sealed class ServerBrowserBootstrap : MonoBehaviour
    {
        public const string BrowseButtonName = "BrowseServersBtn";
        public const string BrowseButtonLabel = "Buscar partida";

        /// Variable de entorno para forzar el directorio de mentira sin Steam delante. Es para
        /// trabajar en la UI, no para producción.
        public const string DirectoryOverrideEnv = "BS_LOBBY_DIRECTORY";

        /// <summary>
        /// Aforo de la sesión. **Espejo** de `SessionConfig::default().max_players` en
        /// `backend/src/network/protocol.rs`, igual que <see cref="WireSchema"/> lo es del número
        /// de esquema: el backend es la autoridad y este número tiene que subir con él.
        ///
        /// Por qué un espejo y no un dato leído: `max_players` viaja en el `SessionConfig` del
        /// handshake P2P entre backends, no por el IPC, así que el cliente de Unity no lo ve.
        /// </summary>
        public const int SessionMaxPlayers = 50;

        private static ServerBrowserBootstrap _instance;

        private ServerBrowserUI _browser;
        private Button _browseButton;
        private HostAnnouncementDriver _announcer;
        private RemotePlayerManager _remotePlayers;

        /// <summary>
        /// La versión que el navegador compara contra la que anuncia cada lobby: la del **WIRE**,
        /// no la del build. Dos builds con el mismo `Application.version` y distinto esquema no
        /// se pueden hablar, y al revés, un cambio de versión de marketing no rompe nada.
        /// </summary>
        public static string ClientVersion => WireSchema.Expected.ToString();

        /// <summary>
        /// El directorio que se inyecta. **Steam en producción**; el mock sólo si se pide por
        /// entorno. Si Steam no está disponible, el navegador enseña un error limpio y el Join
        /// por IP del panel sigue funcionando igual — que es la razón de no caer al mock en
        /// silencio: una lista de mentira parecería una lista de verdad.
        /// </summary>
        public static ILobbyDirectory CreateDirectory()
        {
            string mode = Environment.GetEnvironmentVariable(DirectoryOverrideEnv);
            if (!string.IsNullOrWhiteSpace(mode) &&
                mode.Trim().Equals("mock", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[ServerBrowser] {DirectoryOverrideEnv}=mock — directorio SIMULADO.");
                return MockLobbyDirectory.CreateDefault(ClientVersion);
            }

            return new SteamLobbyDirectory(new FacepunchSteamLobbyQuery());
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (_instance != null) return;

            var go = new GameObject("ServerBrowserBootstrap");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ServerBrowserBootstrap>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }

            _instance = this;

            // Un solo publicador por proceso, creado aquí y en ningún otro sitio.
            _announcer = new HostAnnouncementDriver(new SteamLobbyPublisher(new FacepunchSteamLobbyHost()));

            if (!SteamLobbyKeyParity.KeysMatch(out string mismatch))
            {
                Debug.LogError("[ServerBrowser] El host publicaría con unas claves y el navegador " +
                               "leería con otras; la lista saldría vacía sin error. " + mismatch);
            }
        }

        private void OnDestroy()
        {
            if (_instance != this) return;

            // Cerrar el proceso sin retirar el anuncio es la receta del lobby fantasma. Steam
            // suele limpiarlo al morir el cliente, pero eso es su cortesía, no nuestra garantía.
            _announcer?.ForceWithdraw();
            _instance = null;
        }

        private void OnApplicationQuit() => _announcer?.ForceWithdraw();

        private void Update()
        {
            DriveAnnouncement();

            // El panel de conexión se monta cuando al jugador le da por pulsar Multijugador, y se
            // puede reconstruir. Mientras el botón no exista, se reintenta; el `== null` de Unity
            // también es cierto si el objeto fue destruido, así que esto vuelve a engancharlo
            // solo cuando el menú se rehace.
            if (_browseButton == null) AttachBrowseButton();
            if (_browseButton == null) return;

            // Con sesión viva no se busca partida: el panel enseña Disconnect y este botón no
            // pinta nada. Tampoco mientras el navegador está delante.
            bool visible = SessionState.Current.CanStart && (_browser == null || !_browser.IsOpen);
            if (_browseButton.gameObject.activeSelf != visible) _browseButton.gameObject.SetActive(visible);
        }

        /// <summary>
        /// El anuncio lo gobierna la FASE, no un botón. Un anuncio atado a un botón sobrevive al
        /// día en que alguien sale por otro camino —el `Quit to Menu` del vendor, el backend que
        /// muere, una desconexión— y eso es exactamente un lobby fantasma.
        /// </summary>
        private void DriveAnnouncement()
        {
            if (_announcer == null) return;

            SessionStateMachine session = SessionState.Current;
            bool isHost = session.Role == NetworkInitializer.Role.Host;
            bool established = SessionStateMachine.IsInWorld(session.Phase);

            _announcer.Update(
                new HostAnnouncementState(
                    isHost,
                    established,
                    ResolveHostEndpoint(),
                    ResolveLobbyName(),
                    ClientVersion,
                    ResolvePlayerCount(),
                    SessionMaxPlayers,
                    string.IsNullOrEmpty(session.WorldScene) ? "Unknown" : session.WorldScene,
                    session.Phase == SessionPhase.InGame ? LobbyStatus.InProgress : LobbyStatus.Waiting),
                Time.unscaledTimeAsDouble);
        }

        /// <summary>
        /// Por qué no se está anunciando la partida, o null si se anuncia. Observable para el log
        /// y para quien monte UI encima; se rellena en <see cref="ResolveHostEndpoint"/>.
        /// </summary>
        public static string AnnouncementBlockReason { get; private set; }

        /// Cada cuánto se vuelve a preguntar al sistema por su dirección. La resolución abre un
        /// socket, y esto corre en un `Update`: sin caché serían 60 sockets por segundo. Diez
        /// segundos es la cadencia del propio latido del anuncio.
        private const double LocalAddressRefreshSeconds = 10d;

        private static List<string> _localAddresses;
        private static double _localAddressesStaleAt;
        private static string _lastLoggedEndpointNote;

        private static IReadOnlyList<string> LocalAddresses(double nowUnscaled)
        {
            if (_localAddresses != null && nowUnscaled < _localAddressesStaleAt) return _localAddresses;

            _localAddresses = LocalAddressProbe.Candidates();
            _localAddressesStaleAt = nowUnscaled + LocalAddressRefreshSeconds;
            return _localAddresses;
        }

        /// <summary>
        /// El destino que se anuncia. **El puerto es el realmente elegido**
        /// (`NetworkInitializer.LastSelectedNetPort`), no el tecleado: `SelectLaunchConfig` puede
        /// haberlo desplazado por colisión, y publicar el tecleado dejaría el lobby apuntando a un
        /// puerto muerto.
        ///
        /// **La IP ya NO se publica verbatim.** Hasta el 2026-08-31 se anunciaba el contenido del
        /// campo del panel, cuyo valor por defecto es `127.0.0.1`, así que el lobby decía
        /// `connect_ip=127.0.0.1` — que en la máquina del joiner significa la máquina del joiner.
        /// Ahora pasa por <see cref="LobbyEndpointPolicy"/>: manda lo que el humano escribió si
        /// sirve, si no la dirección de la ruta por defecto, y **si no hay ninguna defendible no
        /// se anuncia nada**. Un lobby con endpoint malo le cuesta 15 s de espera a quien lo
        /// elige; uno que no aparece no le cuesta nada.
        /// </summary>
        private static LobbyEndpoint ResolveHostEndpoint()
        {
            NetworkInitializer init = NetworkInitializer.Instance;
            if (init == null || init.LastSelectedNetPort <= 0)
            {
                AnnouncementBlockReason = "todavía no hay puerto de red elegido";
                return LobbyEndpoint.None;
            }

            // `JoinSessionUI.CurrentServerIP`, no `FindFirstObjectByType`: esto corre cada frame
            // mientras dura la partida, y el panel es `DontDestroyOnLoad` con instancia estática.
            string field = JoinSessionUI.CurrentServerIP;
            string host = LobbyEndpointPolicy.ResolvePublishableHost(
                field, LocalAddresses(Time.unscaledTimeAsDouble), out string reason);

            // El motivo se registra UNA vez por valor: esto corre a 60 Hz y un warning por frame
            // es indistinguible de un bucle roto.
            string note = $"{host}|{reason}";
            if (!string.Equals(note, _lastLoggedEndpointNote, StringComparison.Ordinal))
            {
                _lastLoggedEndpointNote = note;
                if (host == null)
                    Debug.LogWarning($"[ServerBrowser] La partida NO se anuncia en Steam: {reason}. " +
                                     "El Host y el Join por IP no se ven afectados.");
                else
                    Debug.Log($"[ServerBrowser] Endpoint anunciado {host}:{init.LastSelectedNetPort} — {reason}.");
            }

            if (host == null)
            {
                AnnouncementBlockReason = reason;
                return LobbyEndpoint.None;
            }

            AnnouncementBlockReason = null;
            return new LobbyEndpoint(host, init.LastSelectedNetPort);
        }

        /// <summary>
        /// Retira el anuncio de Steam por el camino del publicador. La llama el teardown de sesión
        /// **antes** de <c>SteamLobbyManager.LeaveLobby()</c>, y el orden importa: `LeaveLobby`
        /// vacía `_hostedLobby` por su cuenta y sin log, así que si corre primero el conductor ve
        /// `IsPublishing == false`, su `Withdraw()` no llega a ejecutarse, `WithdrawCount` no sube
        /// y en el log no queda ni una línea de que el anuncio se retiró. El lobby sí moría —por
        /// el `Leave()` de dentro— pero la invariante I14 no cubría la puerta que de verdad se usa,
        /// y diagnosticar un lobby obsoleto costó una sesión entera por eso.
        ///
        /// Idempotente y segura sin Steam: si no había anuncio, no hace nada.
        /// </summary>
        public static void WithdrawAnnouncement()
        {
            _instance?._announcer?.ForceWithdraw();
        }

        private static string ResolveLobbyName()
        {
            string persona = SteamLobbyManager.SteamPersonaName;
            if (!string.IsNullOrWhiteSpace(persona)) return persona;
            return ResolvePlayerName();
        }

        /// <summary>
        /// Jugadores dentro: los remotos que el cliente tiene vivos, más uno (el host). Es el
        /// único contador real que hay en el cliente; sin `RemotePlayerManager` en escena
        /// —todavía en el menú, o cargando— se anuncia 1, que es cierto.
        /// </summary>
        private int ResolvePlayerCount()
        {
            if (_remotePlayers == null) _remotePlayers = FindFirstObjectByType<RemotePlayerManager>();
            return _remotePlayers != null ? _remotePlayers.ActiveCount + 1 : 1;
        }

        /// <summary>
        /// Mete el botón en el panel de `JoinSessionUI`, en su propia fila justo debajo de
        /// [Host] [Join] — el mismo sitio y el mismo patrón que el botón de Steam.
        ///
        /// Se busca por NOMBRE de objeto porque el panel es privado. Es la costura fea de este
        /// puente y está acotada: si el panel no existe todavía, o cambia de nombre, esto no
        /// rompe nada — simplemente no aparece el botón y el flujo Host/Join queda intacto.
        /// </summary>
        private void AttachBrowseButton()
        {
            Transform panel = FindConnectPanel();
            if (panel == null) return;

            Transform existing = panel.Find(BrowseButtonName);
            if (existing != null)
            {
                _browseButton = existing.GetComponent<Button>();
                return;
            }

            Transform buttonRow = panel.Find("ButtonRow");

            var go = new GameObject(BrowseButtonName);
            go.transform.SetParent(panel, false);
            if (buttonRow != null) go.transform.SetSiblingIndex(buttonRow.GetSiblingIndex() + 1);

            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(300f, 40f);

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 300f;
            le.preferredHeight = 40f;

            var img = go.AddComponent<Image>();
            img.color = new Color(0.24f, 0.30f, 0.52f);

            _browseButton = go.AddComponent<Button>();
            _browseButton.targetGraphic = img;
            _browseButton.onClick.AddListener(ShowBrowser);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;

            var label = labelGo.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 16;
            label.fontStyle = FontStyle.Bold;
            label.color = Color.white;
            label.alignment = TextAnchor.MiddleCenter;
            label.text = BrowseButtonLabel;
            label.raycastTarget = false;

            Debug.Log("[ServerBrowser] Botón 'Buscar partida' enganchado al panel de multijugador.");
        }

        private static Transform FindConnectPanel()
        {
            Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < canvases.Length; i++)
            {
                if (canvases[i].name != "JoinSessionCanvas") continue;
                return canvases[i].transform.Find("Panel");
            }

            return null;
        }

        /// <summary>Abre (o reabre) el navegador. Idempotente. NO arranca backend ni sesión.</summary>
        public void ShowBrowser()
        {
            // El panel de conexión se aparta; no se destruye ni se toca su estado de sesión.
            var connectUi = FindFirstObjectByType<JoinSessionUI>();
            if (connectUi != null) connectUi.HideMenu();

            if (_browser != null)
            {
                _browser.Open();
                return;
            }

            var go = new GameObject("ServerBrowser");
            DontDestroyOnLoad(go);

            _browser = go.AddComponent<ServerBrowserUI>();
            _browser.Configure(CreateDirectory(), new JoinSessionLobbyJoinSink(), ClientVersion,
                ResolvePlayerName);
            _browser.OnBackRequested = ReturnToConnectPanel;

            Debug.Log($"[ServerBrowser] Panel abierto (directorio: {_browser.ViewModel.Directory.Description}).");
        }

        public void HideBrowser() => _browser?.Close();

        /// <summary>
        /// Volver al panel de multijugador. Pasa por <see cref="JoinSessionUI.ShowConnectMenu"/>,
        /// que es el método que ese panel tiene para reabrirse en estado de partida y que YA sabe
        /// qué hacer si hay una sesión viva. No se apaga nada, no se arranca nada.
        /// </summary>
        private static void ReturnToConnectPanel()
        {
            var connectUi = FindFirstObjectByType<JoinSessionUI>();
            if (connectUi != null)
            {
                connectUi.ShowConnectMenu();
                return;
            }

            // Sin panel montado, se pide por el mismo evento que usa el botón de Multijugador:
            // lo levanta NetworkMenuBootstrap, no nosotros.
            PolymindGames.UserInterface.MainMenu.OnMultiplayerClicked?.Invoke();
        }

        /// El nombre sale del panel de conexión si está montado (es lo que el jugador tecleó); si
        /// no, del entorno, que es de donde lo saca también el arranque por variable.
        private static string ResolvePlayerName()
        {
            var connectUi = FindFirstObjectByType<JoinSessionUI>();
            if (connectUi != null && !string.IsNullOrWhiteSpace(connectUi.PlayerName)) return connectUi.PlayerName;

            string fromEnv = Environment.GetEnvironmentVariable("NET_NAME");
            return string.IsNullOrWhiteSpace(fromEnv) ? "Player" : fromEnv;
        }
    }
}
