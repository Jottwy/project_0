using System;
using System.Text;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Matchmaking Steam. El App ID sale de <see cref="SteamAppConfig"/>, nunca de aquí:
    /// desarrollo usa Spacewar (480) y producción el id real, y el mismo binario sirve para los
    /// dos. Capa ADITIVA sobre el flujo
    /// manual Host/Join: publica un lobby con el ip:puerto que el flujo manual ya usa,
    /// abre el overlay de invitación, y al entrar en un lobby ajeno reinyecta esos datos
    /// en <see cref="NetworkInitializer.StartAsJoiner"/> — el MISMO método interno que
    /// pulsa el botón Join. No existe un segundo camino de conexión.
    ///
    /// NO toca el transporte P2P del backend Rust: Steam solo transporta metadatos
    /// (connect_ip / connect_port). El UDP sigue yendo directo entre backends.
    ///
    /// Si Steam no está disponible (cliente cerrado, DLL ausente, Init fallido) todo
    /// queda inerte: <see cref="IsAvailable"/> es false, la UI oculta el botón y el
    /// flujo manual se comporta exactamente igual que antes de este archivo.
    /// </summary>
    public sealed partial class SteamLobbyManager : MonoBehaviour
    {
        /// El App ID **no vive aquí**: sale de <see cref="SteamAppConfig"/>, que lo resuelve por
        /// entorno → `steam_appid.txt` → constante compilada. Se guarda el efectivo para poder
        /// nombrarlo en los diagnósticos.
        public static uint ActiveAppId { get; private set; }

        // Claves de metadata del lobby. Son el contrato completo del spike: Steam no
        // transporta nada más.
        public const string ConnectIpKey = "connect_ip";
        public const string ConnectPortKey = "connect_port";
        public const string HostNameKey = "host_name";

        private const int MaxLobbyMembers = 8;

        /// El backend recibe el nombre por variable de entorno (NET_NAME). Una persona
        /// Steam admite Unicode arbitrario; se recorta y se limpian los controles para no
        /// meter un valor degenerado en el env del proceso hijo.
        private const int MaxPlayerNameLength = 32;

        private static SteamLobbyManager _instance;
        public static SteamLobbyManager Instance => _instance;

        /// True solo si SteamClient.Init tuvo éxito. Todo lo demás cuelga de esto.
        public static bool IsAvailable => _instance != null && _instance._initialized && SteamClient.IsValid;

        /// Nombre de persona Steam, o null si Steam no está disponible.
        public static string SteamPersonaName => IsAvailable ? SteamClient.Name : null;

        /// <summary>
        /// El `SteamId` de ESTA máquina, o 0 sin Steam. ADR-135 lo publica como `bs_steam_host`
        /// para que un joiner sepa a quién llamar por la red de Valve — **y por eso sale de aquí y
        /// no de `Lobby.Owner`**: la propiedad de un lobby migra cuando el dueño se va, mientras
        /// que quien sirve el mundo es este proceso.
        /// </summary>
        public static ulong LocalSteamId => IsAvailable ? SteamClient.SteamId.Value : 0UL;

        public string StatusMessage { get; private set; } = "";

        private bool _initialized;

        /// El lobby que ESTA instancia creó como host. Se guarda aparte de
        /// <see cref="_currentLobby"/> a propósito: aceptar el invite de otro mientras se
        /// tiene lobby propio abierto machacaba la referencia y dejaba el propio huérfano,
        /// imposible de cerrar (LeaveLobby cerraba el ajeno).
        private Lobby? _hostedLobby;

        /// El último lobby en el que se ha ENTRADO (propio o ajeno).
        private Lobby? _currentLobby;

        private bool _creatingLobby;
        private bool _joinRequestInFlight;

        /// <summary>
        /// Identidad de la tanda de anuncio en curso. Sube en cada <see cref="CloseHostedLobby"/>,
        /// también cuando no había lobby que cerrar.
        ///
        /// `CreateLobbyAsync` tarda; si la sesión termina mientras está en vuelo, la continuación
        /// aterriza DESPUÉS del teardown y adoptaba el lobby recién creado — público, joinable,
        /// apuntando a un endpoint ya muerto y sin nadie que volviera a cerrarlo. Un `bool` no
        /// bastaba: el problema no es "hay teardown", es "esta creación es de la sesión
        /// ANTERIOR". Mismo patrón que <c>Generation</c> en <see cref="SessionStateMachine"/>.
        /// </summary>
        private int _lobbyEpoch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            // No hay entry point de app propio (GameBootGate es vendor y GameBootstrap
            // vive en la escena de gameplay), así que el init se ancla aquí: corre una
            // sola vez por proceso, antes de cualquier escena, y sobrevive a los loads.
            if (_instance != null) return;
            var go = new GameObject("SteamSession");
            go.AddComponent<SteamLobbyManager>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
            TryInitSteam();
        }

        private void TryInitSteam()
        {
            if (_initialized) return;

            // Init ya hecho por otra ruta (recarga de dominio con el cliente vivo):
            // adoptarlo en vez de re-inicializar. SteamClient.Init dos veces lanza.
            if (SteamClient.IsValid)
            {
                _initialized = true;
                ActiveAppId = SteamClient.AppId.Value;
                SubscribeCallbacks();
                StatusMessage = $"Steam ready ({SteamClient.Name})";
                Debug.Log($"[SteamLobbyManager] SteamClient already valid; adopted. " +
                          $"app_id={SteamAppConfig.Describe(ActiveAppId)} name={SteamClient.Name} " +
                          $"steam_id={SteamClient.SteamId.Value}");
                return;
            }

            uint appId = SteamAppConfig.Current(out SteamAppConfig.AppIdSource source, out string sourceDetail);
            ActiveAppId = appId;

            // Se registra ANTES de intentar nada: si Init revienta, el log ya dice con qué id se
            // intentó y de dónde salió. Un fallo de Steam sin esa línea obliga a adivinar.
            Debug.Log($"[SteamLobbyManager] Steam init: app_id={SteamAppConfig.Describe(appId)} " +
                      $"origen={source} ({sourceDetail})");

            try
            {
                // asyncCallbacks:false — los callbacks se bombean desde Update, así que
                // aterrizan en el hilo principal de Unity y pueden tocar la API de Unity.
                SteamClient.Init(appId, false);
            }
            catch (DllNotFoundException e)
            {
                // ESTE es el fallo que estuvo escondido: el binding managed de Facepunch está en
                // Assets/Plugins, pero el NATIVO `steam_api64.dll` del SDK de Steamworks es un
                // fichero aparte, y sin él Init lanza `DllNotFoundException` — no "Steam cerrado".
                // Se separa del catch general a propósito: el mensaje genérico ("Steam
                // unavailable") hizo que se leyera durante semanas como "el cliente no está".
                StatusMessage = "Steam unavailable (steam_api64.dll ausente)";
                Debug.LogError(
                    $"[SteamLobbyManager] FALTA EL NATIVO DE STEAM: {e.Message}. " +
                    "Esto NO es 'Steam cerrado'. `steam_api64.dll` del SDK de Steamworks tiene que " +
                    "estar en Assets/Plugins/Facepunch.Steamworks/redistributable_bin/win64/ (Editor) " +
                    "y en <build>_Data/Plugins/x86_64/ (player). " +
                    "Steam, invitaciones y navegador de servidores quedan desactivados; " +
                    "el Host/Join por IP no se ve afectado.");
                return;
            }
            catch (EntryPointNotFoundException e)
            {
                // El nativo cargó pero es de OTRA versión del SDK que la que espera el binding
                // managed de Facepunch. Medido: con el SDK 1.65, `SteamAPI_SteamApps_v009`; el
                // binding pide `_v008`. Se separa del catch general porque el mensaje genérico
                // manda a mirar el cliente de Steam, y aquí el cliente no tiene nada que ver.
                StatusMessage = "Steam unavailable (versión de steam_api64.dll)";
                Debug.LogError(
                    $"[SteamLobbyManager] VERSIÓN DE STEAM_API64.DLL EQUIVOCADA: {e.Message}. " +
                    "El nativo cargó, pero exporta otra versión de interfaz que la que pide " +
                    "Facepunch.Steamworks.Win64.dll. El binding vendorizado targetea el " +
                    "**SDK 1.61**; poner ese redistribuible en " +
                    "Assets/Plugins/Facepunch.Steamworks/redistributable_bin/win64/. " +
                    "Steam path disabled; manual Host/Join unaffected.");
                return;
            }
            catch (Exception e)
            {
                // Cliente de Steam cerrado, o el App ID no coincide con `steam_appid.txt`. NO es
                // un error del juego: el flujo manual sigue intacto, solo desaparece Steam.
                StatusMessage = "Steam unavailable";
                Debug.LogWarning(
                    $"[SteamLobbyManager] SteamClient.Init({appId}) failed: {e.GetType().Name}: {e.Message}. " +
                    $"Comprobar: (a) el cliente de Steam está abierto y con sesión iniciada; " +
                    $"(b) {SteamAppConfig.AppIdFileName} junto al ejecutable dice {appId}; " +
                    $"(c) la cuenta tiene acceso a ese App ID. " +
                    "Steam path disabled; manual Host/Join unaffected.");
                return;
            }

            if (!SteamClient.IsValid)
            {
                StatusMessage = "Steam unavailable";
                Debug.LogWarning($"[SteamLobbyManager] SteamClient.Init({appId}) returned but IsValid=false. " +
                                 "Steam path disabled; manual Host/Join unaffected.");
                return;
            }

            _initialized = true;
            SubscribeCallbacks();
            StatusMessage = $"Steam ready ({SteamClient.Name})";
            Debug.Log($"[SteamLobbyManager] Steam initialized app_id={SteamAppConfig.Describe(appId)} " +
                      $"name={SteamClient.Name} steam_id={SteamClient.SteamId.Value}");
        }

        private void SubscribeCallbacks()
        {
            SteamMatchmaking.OnLobbyCreated += HandleLobbyCreated;
            SteamMatchmaking.OnLobbyEntered += HandleLobbyEntered;
            SteamFriends.OnGameLobbyJoinRequested += HandleGameLobbyJoinRequested;
        }

        private void UnsubscribeCallbacks()
        {
            SteamMatchmaking.OnLobbyCreated -= HandleLobbyCreated;
            SteamMatchmaking.OnLobbyEntered -= HandleLobbyEntered;
            SteamFriends.OnGameLobbyJoinRequested -= HandleGameLobbyJoinRequested;
        }

        private void Update()
        {
            // Se comprueba IsValid además del flag propio: si el cliente de Steam muere (o
            // una recarga de dominio en el editor se lleva el estado estático de Facepunch)
            // RunCallbacks sobre un cliente inválido lanzaría UNA VEZ POR FRAME.
            if (!_initialized) return;
            if (!SteamClient.IsValid)
            {
                Debug.LogWarning("[SteamLobbyManager] SteamClient became invalid; stopping callback pump. Manual Host/Join unaffected.");
                _initialized = false;
                _hostedLobby = null;
                _currentLobby = null;
                StatusMessage = "Steam unavailable";
                return;
            }

            SteamClient.RunCallbacks();
        }

        // ─── Host ───

        /// <summary>
        /// Crea el lobby público y publica en su metadata el MISMO ip:puerto que el flujo
        /// manual usaría. Llamar DESPUÉS de <see cref="NetworkInitializer.StartAsHost"/>:
        /// el puerto que se publica es el efectivamente seleccionado
        /// (<see cref="NetworkInitializer.LastSelectedNetPort"/>), no el tecleado — pueden
        /// diferir si SelectLaunchConfig tuvo que desplazarlo por colisión.
        /// </summary>
        public async Task<bool> CreateSteamLobby(string connectIp, int connectPort)
        {
            if (!IsAvailable)
            {
                Debug.LogWarning("[SteamLobbyManager] CreateSteamLobby ignored: Steam unavailable.");
                return false;
            }

            if (_creatingLobby)
            {
                Debug.Log("[SteamLobbyManager] CreateSteamLobby ignored: creation already in flight.");
                return false;
            }

            if (_hostedLobby.HasValue)
            {
                // Ya hay lobby propio: refrescar los datos (el puerto pudo cambiar entre
                // intentos de host) y salir sin crear un segundo.
                if (!TryPublishConnectData(_hostedLobby.Value, connectIp, connectPort)) return false;
                Debug.Log($"[SteamLobbyManager] Lobby already open; refreshed metadata {connectIp}:{connectPort}");
                return true;
            }

            _creatingLobby = true;
            int epoch = _lobbyEpoch;
            StatusMessage = "Creating Steam lobby...";

            Lobby? created;
            try
            {
                created = await SteamMatchmaking.CreateLobbyAsync(MaxLobbyMembers);
            }
            catch (Exception e)
            {
                _creatingLobby = false;
                StatusMessage = "Steam lobby failed";
                Debug.LogError($"[SteamLobbyManager] CreateLobbyAsync threw: {e.Message}");
                return false;
            }

            _creatingLobby = false;

            if (!created.HasValue)
            {
                StatusMessage = "Steam lobby failed";
                Debug.LogError("[SteamLobbyManager] CreateLobbyAsync returned null.");
                return false;
            }

            var lobby = created.Value;

            // La sesión terminó mientras Steam creaba: este lobby es de la partida anterior.
            // Adoptarlo sería publicar un anuncio que ya nadie va a retirar.
            if (epoch != _lobbyEpoch)
            {
                TryLeave(lobby);
                StatusMessage = "Steam lobby cancelled";
                Debug.Log($"[SteamLobbyManager] Lobby {lobby.Id.Value} cerrado al nacer: la sesión " +
                          $"terminó mientras Steam lo creaba (epoch {epoch} → {_lobbyEpoch}).");
                return false;
            }

            _hostedLobby = lobby;

            // SetPublic/SetJoinable/SetData bajan a nativo y pueden lanzar igual que
            // CreateLobbyAsync; sin este try la excepción escaparía de un async void.
            try
            {
                lobby.SetPublic();
                lobby.SetJoinable(true);
            }
            catch (Exception e)
            {
                StatusMessage = "Steam lobby failed";
                Debug.LogError($"[SteamLobbyManager] Lobby {lobby.Id.Value} config threw: {e.Message}");
                return false;
            }

            if (!TryPublishConnectData(lobby, connectIp, connectPort)) return false;

            StatusMessage = "Steam lobby ready";
            Debug.Log($"[SteamLobbyManager] Lobby created id={lobby.Id.Value} connect={connectIp}:{connectPort}");
            return true;
        }

        /// <summary>
        /// Lo que cuelga del botón "Invite via Steam": asegura el lobby y, solo cuando ya
        /// existe, abre el overlay. Separado de <see cref="CreateSteamLobby"/> porque la
        /// creación es asíncrona y el overlay sin lobby no tiene nada que invitar.
        /// </summary>
        public async void CreateLobbyAndOpenInvite(string connectIp, int connectPort)
        {
            if (await CreateSteamLobby(connectIp, connectPort))
                OpenInviteOverlay();
        }

        private static bool TryPublishConnectData(Lobby lobby, string connectIp, int connectPort)
        {
            try
            {
                lobby.SetData(ConnectIpKey, connectIp ?? "");
                lobby.SetData(ConnectPortKey, connectPort.ToString());
                lobby.SetData(HostNameKey, SteamClient.IsValid ? SteamClient.Name : "");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SteamLobbyManager] SetData on lobby {lobby.Id.Value} threw: {e.Message}");
                return false;
            }
        }

        /// <summary>Abre el overlay de invitación de Steam sobre el lobby actual.</summary>
        public bool OpenInviteOverlay()
        {
            if (!IsAvailable)
            {
                Debug.LogWarning("[SteamLobbyManager] OpenInviteOverlay ignored: Steam unavailable.");
                return false;
            }

            // Se invita SIEMPRE al lobby propio: invitar al ajeno no es cosa de este botón.
            Lobby? target = _hostedLobby ?? _currentLobby;
            if (!target.HasValue)
            {
                Debug.LogWarning("[SteamLobbyManager] OpenInviteOverlay ignored: no lobby yet.");
                return false;
            }

            SteamFriends.OpenGameInviteOverlay(target.Value.Id);
            Debug.Log($"[SteamLobbyManager] Invite overlay opened for lobby {target.Value.Id.Value}");
            return true;
        }

        /// <summary>
        /// Abandona el lobby propio y el unido. Idempotente y seguro sin Steam: es lo que
        /// llama la UI en Disconnect y en el fallo de arranque del host, rutas que también
        /// existen cuando Steam nunca se inicializó.
        /// </summary>
        public void LeaveLobby()
        {
            bool sameLobby = _hostedLobby.HasValue && _currentLobby.HasValue &&
                             _hostedLobby.Value.Id.Value == _currentLobby.Value.Id.Value;

            TryLeave(_hostedLobby);
            if (!sameLobby) TryLeave(_currentLobby);

            _hostedLobby = null;
            _currentLobby = null;
            StatusMessage = IsAvailable ? $"Steam ready ({SteamClient.Name})" : "";
        }

        private static void TryLeave(Lobby? lobby)
        {
            if (!lobby.HasValue) return;
            try { lobby.Value.Leave(); }
            catch (Exception e) { Debug.LogWarning($"[SteamLobbyManager] Leave failed: {e.Message}"); }
        }

        // ─── Callbacks ───

        private void HandleLobbyCreated(Result result, Lobby lobby)
        {
            if (result != Result.OK)
                Debug.LogError($"[SteamLobbyManager] OnLobbyCreated result={result}");
        }

        /// El amigo aceptó la invitación desde el overlay. Une al lobby; el auto-connect
        /// real ocurre en <see cref="HandleLobbyEntered"/>, que es el único punto donde la
        /// metadata está garantizada disponible.
        private async void HandleGameLobbyJoinRequested(Lobby lobby, SteamId invitedBy)
        {
            Debug.Log($"[SteamLobbyManager] Join requested for lobby {lobby.Id.Value} (from {invitedBy.Value})");

            if (_joinRequestInFlight)
            {
                Debug.Log("[SteamLobbyManager] Join request ignored: another join in flight.");
                return;
            }

            _joinRequestInFlight = true;
            try
            {
                var enter = await lobby.Join();
                if (enter != RoomEnter.Success)
                {
                    Debug.LogError($"[SteamLobbyManager] lobby.Join() failed: {enter}");
                    _joinRequestInFlight = false;
                }
                // En éxito, OnLobbyEntered dispara y limpia el flag.
            }
            catch (Exception e)
            {
                _joinRequestInFlight = false;
                Debug.LogError($"[SteamLobbyManager] lobby.Join() threw: {e.Message}");
            }
        }

        private void HandleLobbyEntered(Lobby lobby)
        {
            _joinRequestInFlight = false;

            // El creador entra en su propio lobby: no hay nada a lo que conectarse.
            if (lobby.IsOwnedBy(SteamClient.SteamId))
            {
                _hostedLobby = lobby;
                Debug.Log($"[SteamLobbyManager] Entered own lobby {lobby.Id.Value}; host path, no auto-connect.");
                return;
            }

            _currentLobby = lobby;

            // Coexistencia (regla dura del spike): si ya hay una sesión manual en curso,
            // Steam NO la pisa. Se entra al lobby y se ignora el auto-connect.
            var init = NetworkInitializer.Instance;
            if (init != null && init.CurrentRole != NetworkInitializer.Role.None)
            {
                Debug.LogWarning($"[SteamLobbyManager] Entered lobby {lobby.Id.Value} but a {init.CurrentRole} session is already active; auto-connect skipped.");
                StatusMessage = "Already in a session";
                return;
            }

            // ADR-136 D8: las MISMAS vías que lee el navegador —directa, LAN, relay, Steam—, no sólo
            // `connect_ip`/`connect_port`. Hasta el 2026-09-09 una invitación entraba a pelo contra
            // `ip:puerto` mientras el navegador sí llevaba relay y túnel: la invitación conectaba
            // peor que el navegador, y un host sin endpoint directo (ADR-117 D7) no se podía
            // aceptar desde el overlay.
            var target = Lobbies.LobbyJoinTarget.FromMetadata(lobby.GetData);
            if (!target.HasSomeWayIn)
            {
                StatusMessage = "Lobby has no connect data";
                Debug.LogError($"[SteamLobbyManager] Lobby {lobby.Id.Value} no anuncia ninguna forma de entrar ({target}).");
                return;
            }

            string playerName = SanitizePlayerName(SteamClient.Name);
            Debug.Log($"[SteamLobbyManager] Auto-connect from lobby {lobby.Id.Value}: {target} as '{playerName}'");
            StatusMessage = target.HasDirect ? $"Joining {target.Ip}:{target.Port}..." : "Joining through Steam/relay...";

            // Camino único: delega en la UI cuando existe (para que el panel refleje el
            // estado y se cancele el auto-solo), y si no, llama al MISMO StartAsJoiner.
            if (!UI.JoinSessionUI.TryBeginSteamJoin(target.Ip, target.Port, playerName, target.FallbackIp,
                    target.Relay, target.SteamHost))
            {
                if (init == null)
                {
                    Debug.LogError("[SteamLobbyManager] No NetworkInitializer available; cannot auto-connect.");
                    return;
                }
                init.StartAsJoiner(target.Ip, target.Port, playerName, target.Relay, target.SteamHost);
            }
        }

        /// <summary>
        /// Valida la metadata de conexión de un lobby. Pura y sin dependencias de Steam ni
        /// de Unity a propósito: es el único punto donde datos que vienen de OTRA máquina
        /// entran al camino de conexión, así que tiene que ser testeable headless.
        /// </summary>
        public static bool TryParseConnectData(string rawIp, string rawPort, out string ip, out int port)
        {
            ip = null;
            port = 0;

            if (string.IsNullOrWhiteSpace(rawIp)) return false;
            if (!int.TryParse(rawPort, out int parsed)) return false;
            if (parsed <= 0 || parsed > 65535) return false;

            ip = rawIp.Trim();
            port = parsed;
            return ip.Length > 0;
        }

        /// <summary>
        /// Recorta una persona Steam a algo seguro como valor de variable de entorno
        /// (NET_NAME). No transcodifica: el Unicode pasa tal cual, solo se eliminan
        /// controles y se acota la longitud.
        /// </summary>
        public static string SanitizePlayerName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Player";

            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= MaxPlayerNameLength) break;
            }

            string cleaned = sb.ToString().Trim();
            return cleaned.Length == 0 ? "Player" : cleaned;
        }

        private void OnDestroy()
        {
            if (_instance != this) return;

            if (_initialized)
            {
                UnsubscribeCallbacks();
                LeaveLobby();
                try { SteamClient.Shutdown(); }
                catch (Exception e) { Debug.LogWarning($"[SteamLobbyManager] Shutdown failed: {e.Message}"); }
                _initialized = false;
            }

            _instance = null;
        }
    }
}
