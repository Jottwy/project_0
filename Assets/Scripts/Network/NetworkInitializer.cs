using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BackroomsSurvival.Net
{
    public sealed class NetworkInitializer : MonoBehaviour
    {
        public enum Role { None, Host, Joiner }

        [Header("Backend")]
        [Tooltip("Path to backrooms_server.exe relative to project root, or absolute.")]
        public string backendPath = "backend/target/release/backrooms_server.exe";
        public string fallbackBackendPath = "backend/target/debug/backrooms_server.exe";
        public string executableName = "backrooms_server.exe";

        [Header("Connection")]
        public int ipcPort = 7777;
        public int netPort = 7778;
        public int hostNetId = 1;
        public int joinerNetId = 2;
        public int joinerNetPortOffset = 1;
        public float startupTimeout = 10f;

        /// <summary>
        /// Techo de cliente para el handshake de un joiner, POR ENCIMA del autoritativo.
        ///
        /// Quien acota de verdad la espera es el backend con su `CONNECT_TIMEOUT` (15 s), que
        /// contesta con `session_ended` y un motivo redactado. Esto no lo sustituye ni lo tapa:
        /// solo cubre el hueco en el que ese aviso NO puede llegar - el proceso backend murio,
        /// o el IPC se cayo, despues de conectar y antes de confirmar la sesion. Sin esto el
        /// panel se queda en "Joining..." para siempre, que es el sintoma que este trabajo mata.
        ///
        /// Se registra como BACKSTOP en el log, con ese nombre, para que nunca se confunda con
        /// un diagnostico real.
        /// </summary>
        public float joinerHandshakeTimeout = 25f;

        [Header("Debug")]
        [Tooltip("Spawn the robapieles (phantom peer) on the host for play-testing. Injects DEBUG_SPAWN_PHANTOM=1 into the backend env. Host-only; no effect on joiners.")]
        public bool debugSpawnPhantom = false;

        public Role CurrentRole { get; private set; } = Role.None;
        public bool IsBackendReady { get; private set; }
        public bool HasBackendProcess => _backendProcess != null && !_backendProcess.HasExited;
        public string StatusMessage { get; private set; } = "";
        public int LastSelectedIpcPort { get; private set; }
        public int LastSelectedNetPort { get; private set; }
        /// <summary>
        /// ADR-111 — el <c>NET_ID</c> **PROPUESTO**: lo que Unity pidió al lanzar su backend, no
        /// lo que el host asignó. NO es identidad autoritativa y no debe compararse con ningún
        /// id venido del backend (`owner_id`, ids de `remote_players`, `victim_id`) ni usarse
        /// para particionar ids de petición. Para eso está <see cref="NetIdentity.Local"/>.
        /// Legítimo sólo para hablar del LANZAMIENTO (logs de configuración, el HUD de depuración).
        /// </summary>
        public int LastSelectedNetId { get; private set; }
        /// <summary>World seed handed to the backend as WORLD_SEED. Observable so the
        /// port-passed-as-seed regression stays covered by a test.</summary>
        public int LastSelectedWorldSeed { get; private set; }
        /// <summary>ADR-095 — arranca el backend sirviendo mundo de WorldGen3 en vez de WG2.
        ///
        /// <b>ENCENDIDO por defecto desde ADR-109.</b> Nació apagado porque los dos mundos convivían
        /// y el servido en sesión normal era WG2; eso dejó de ser cierto cuando la mudanza cerró
        /// —geometría, jugador, IA, loot, construcción y claims resuelven contra WG3— y WG2 dejó de
        /// producirse: con la etapa 1 de la retirada, arrancar sin esta bandera no da «el mundo
        /// anterior», da un mundo que ya nadie genera.
        ///
        /// El defecto importa porque <b>este componente no vive en ninguna escena</b>: lo crean en
        /// runtime <c>AutoConnect</c>, <c>NetworkMenuBootstrap</c> y <c>JoinSessionUI</c>. El
        /// interruptor por escena de <c>GameBootstrap</c> lo escribe DESPUÉS de crearlo, y eso
        /// funciona al darle a Play dentro de la escena —el bootstrap corre antes de hostear— pero
        /// NO cuando se pasa por el menú: ahí hostea <c>JoinSessionUI</c> y el backend ya se lanzó
        /// con la bandera en false. Es el fallo que hacía que un build hecho desde el menú sirviera
        /// el mundo viejo mientras el editor servía el nuevo, con la misma escena.
        ///
        /// Apagarlo sigue siendo posible y es lo que quieren las escenas de prueba de WG2: la
        /// casilla de <c>GameBootstrap</c> escribe este campo en los dos sentidos.</summary>
        [Header("WorldGen3 (ADR-095)")]
        [Tooltip("Arranca el backend con BACKROOMS_WG3=1. Exige haber exportado el manifiesto. " +
                 "Encendido por defecto desde ADR-109: WG2 ya no se produce.")]
        public bool enableWorldGen3 = true;

        public string LastSelectedIpcAddress { get; private set; } = "127.0.0.1";
        public string LastEffectiveRole { get; private set; } = "none";
        public string LastConnectTo { get; private set; } = "<none>";

        // ADR-032: how long teardown waits for the backend to save + self-exit before force-killing.
        // The backend saves synchronously and exits in ~tens of ms; this is a generous ceiling that
        // never blocks teardown indefinitely.
        private const int SaveOnQuitTimeoutMs = 1500;

        private Process _backendProcess;
        private float _startupTimer;
        private bool _waitingForBackend;
        private float _handshakeTimer;

        /// <summary>
        /// Generacion del lanzamiento al que pertenece <see cref="_backendProcess"/>. Se copia
        /// de <see cref="SessionStateMachine.Generation"/> en cada lanzamiento y se captura en el
        /// callback <c>Exited</c>.
        ///
        /// El callback llega en un hilo del pool, puede llegar TARDE y no trae identidad: el
        /// backend de la sesion 1 avisa de su muerte cuando la sesion 2 ya esta arriba, y sin
        /// esta comparacion apagaba `IsBackendReady` y escribia "Backend exited" en el
        /// StatusMessage de la sesion NUEVA. Es la mitad Unity de la invariante "un backend
        /// anterior nunca puede dar ordenes a la sesion nueva".
        /// </summary>
        private int _backendGeneration = -1;

        /// <summary>Procesos backend lanzados por este proceso de Unity. Instrumentacion: la
        /// unica forma de demostrar "no queda backend huerfano" es contar los dos lados.</summary>
        public static int BackendLaunchCount { get; private set; }
        /// <summary>Procesos backend terminados por este proceso de Unity.</summary>
        public static int BackendTerminateCount { get; private set; }
        /// <summary>Backends lanzados y aun no terminados. Invariante: 0 o 1, nunca mas.</summary>
        public static int LiveBackendCount => BackendLaunchCount - BackendTerminateCount;

        /// <summary>
        /// Un callback tardio del proceso se aplica SOLO si es de la generacion en curso.
        /// Pura y publica para que la suite EditMode recorra la regla sin lanzar un proceso -
        /// mismo criterio que <see cref="BuildChildEnvironment"/>.
        /// </summary>
        public static bool ShouldApplyBackendExit(int eventGeneration, int currentGeneration)
            => eventGeneration == currentGeneration;

        private static NetworkInitializer _instance;
        public static NetworkInitializer Instance => _instance;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            ipcPort = ReadIntEnv("IPC_PORT", ipcPort);
            netPort = ReadIntEnv("NET_PORT", netPort);

            // ADR-056: the session-end listener lives on this same DontDestroyOnLoad object, so
            // it exists exactly once and survives the trip back to the menu. Added here rather
            // than placed in a scene because both entry points (NetworkMenuBootstrap from the
            // menu, GameBootstrap from the gameplay scene) build this object in code.
            if (GetComponent<SessionEndHandler>() == null)
                gameObject.AddComponent<SessionEndHandler>();
        }

        /// <summary>
        /// Seed por defecto del mundo. 42 es el valor de producción.
        ///
        /// Para ir a ver una sala autorada sin cruzar medio kilómetro de laberinto, cámbialo por una
        /// seed que ponga una junto al spawn: las encuentra el test
        /// `hunt_seed_with_room_near_spawn` (con la 157 hay una a 28 m). Volver a 42 antes de
        /// commitear — una seed de playtest colada en un commit cambia el mundo de todos.
        /// </summary>
        private const int DefaultWorldSeed = 42;

        public void StartAsHost(string playerName, int worldSeed = DefaultWorldSeed)
        {
            StartAsHost(playerName, worldSeed, false);
        }

        /// <summary>
        /// Host on an explicit P2P listen port. Deliberately NOT an overload of
        /// <see cref="StartAsHost(string,int)"/>: a two-arg call would bind to that one instead
        /// (C# prefers the candidate that fills no optional parameter), so the port was silently
        /// passed as <c>worldSeed</c> — it never reached NET_PORT, and it renamed the save file
        /// to <c>world_{port}.json</c>, making a changed port look like a lost world.
        /// A distinct name makes that mistake impossible to re-introduce.
        /// </summary>
        public void StartAsHostOnPort(string playerName, int hostListenPort, int worldSeed = DefaultWorldSeed)
        {
            StartAsHost(playerName, worldSeed, false, hostListenPort);
        }

        public void StartAsAutoSolo(string playerName, int worldSeed = DefaultWorldSeed)
        {
            StartAsHost(playerName, worldSeed, true, null);
        }

        private void StartAsHost(string playerName, int worldSeed, bool autoSolo)
        {
            StartAsHost(playerName, worldSeed, autoSolo, null);
        }

        private void StartAsHost(string playerName, int worldSeed, bool autoSolo, int? requestedHostListenPort)
        {
            // EL EMBUDO. Los seis caminos que arrancan un host (menu, Steam, autosolo,
            // SESSION_MODE=host, AutoConnect, arnes de pruebas) pasan por aqui, asi que el gate
            // se pone una vez y no seis. Un segundo Host durante un Joining se RECHAZA en vez de
            // lanzar un segundo backend contra el primero.
            if (!SessionState.Current.RequestStart(Role.Host))
            {
                Debug.LogWarning(
                    $"[NetworkInitializer] Host ignorado: ya hay sesion en fase {SessionState.Phase}. " +
                    "Sal de la actual antes de arrancar otra.");
                return;
            }
            TerminateLeftoverBackend("arranque de host");

            CurrentRole = Role.Host;
            StatusMessage = "Starting backend...";
            LastSelectedWorldSeed = worldSeed;
            string sessionMode = ReadSessionMode();
            if (sessionMode != null && sessionMode.Equals("join", StringComparison.OrdinalIgnoreCase))
                Debug.LogWarning("[NetworkInitializer] SESSION_MODE=join is set, but manual Host was requested; effective role=host");
            int requestedNetPort = requestedHostListenPort.GetValueOrDefault(netPort);
            Debug.Log($"[NetworkInitializer] user input hostListenPort={requestedNetPort}");
            var config = SelectLaunchConfig(
                autoSolo ? "autosolo" : "host",
                ipcPort,
                requestedNetPort,
                hostNetId,
                requestedHostListenPort);
            StoreSelectedConfig(config);
            LastEffectiveRole = autoSolo ? "autosolo" : "host";
            LastConnectTo = "<none>";
            ConfigureIpcClient(config.IpcAddress, config.IpcPort);
            ArmSessionEndHandler();
            ResetSessionScopedRegistries();

            var env = new Dictionary<string, string>
            {
                ["IPC_PORT"] = config.IpcPort.ToString(),
                ["NET_PORT"] = config.NetPort.ToString(),
                ["NET_ID"] = config.NetId.ToString(),
                ["NET_NAME"] = playerName,
                ["WORLD_SEED"] = worldSeed.ToString(),
                ["RUST_LOG"] = "info",
            };
            AddIpcAddressEnv(env, config.IpcAddress, config.IpcPort);
            AddRoomManifestEnv(env);
            AddWorldGen3Env(env);
            // ADR-117: el host abre su sesión de relay al arrancar, sin esperar a saber si le hará
            // falta. Cuesta un registro y dos datagramas por segundo, y a cambio el relay ya está
            // listo cuando entra el primer joiner que no puede por vía directa — que es la mayoría.
            // Sin relay configurado en la build esto no hace nada.
            AddRelayEnv(env, Connectivity.RelaySessionCredentials.Current(), asHost: true);

            // ADR-136 D3: el anfitrión también declara su identidad, y así «me invitó el anfitrión»
            // resuelve por el mismo mapa que «me invitó un cliente». Sin Steam es 0 y no se pone.
            Connectivity.PeerIdentityEnv.Apply(env, SteamLobbyManager.LocalSteamId, 0UL);

            // ADR-135: y el túnel de Steam, por el mismo motivo y con el mismo coste: sin joiners
            // no mueve un byte, y cuando llega el primero ya está escuchando. El backend NO se
            // entera de que existe —le llegan datagramas de loopback como los de cualquier otro
            // peer—, así que aquí no hay ninguna variable de entorno que poner: el túnel del host
            // habla directamente a su `NET_PORT`.
            //
            // Sin Steam disponible esto no hace nada y el Host directo se comporta igual que antes.
            Connectivity.SteamTunnelRunner.BeginHost(
                new FacepunchSteamTunnelTransport(),
                Connectivity.SteamTunnelCredentials.Current(),
                config.NetPort);

            // Fase 6B (Slice 1): debug-spawn the robapieles on the host. The backend reads
            // DEBUG_SPAWN_PHANTOM from its env (inherited from Unity via UseShellExecute=false);
            // injected here so it's a single inspector toggle, OFF by default. Host-only.
            if (debugSpawnPhantom)
                env["DEBUG_SPAWN_PHANTOM"] = "1";

            LogLaunchConfig(
                sessionMode,
                config.IpcAddress,
                config.IpcPort,
                config.NetPort,
                config.NetId,
                playerName,
                autoSolo ? "autosolo" : "host",
                true,
                config.DefaultIpcOccupied,
                env);

            if (!LaunchBackendProcess(env))
                return;

            IPCClient.Instance?.StartClient();
            _waitingForBackend = true;
            _startupTimer = 0f;
        }

        public void StartAsJoiner(string serverIP, int serverNetPort, string playerName) =>
            StartAsJoiner(serverIP, serverNetPort, playerName, default, default);

        public void StartAsJoiner(string serverIP, int serverNetPort, string playerName,
            Lobbies.LobbyRelay relay) =>
            StartAsJoiner(serverIP, serverNetPort, playerName, relay, default);

        /// <summary>
        /// Igual, con la sesión de relay que anunció el lobby (ADR-117).
        ///
        /// Con `relay` válido, `serverIP` puede venir vacío: es el lobby relay-only de D7, y
        /// entonces el backend arranca directamente por la etapa de relay.
        ///
        /// `invitedBy` (ADR-136 D2): la identidad de quien invitó, sólo cuando se entra por una
        /// invitación del overlay de Steam; 0 para el navegador, el join manual y el auto-solo.
        /// </summary>
        public void StartAsJoiner(string serverIP, int serverNetPort, string playerName,
            Lobbies.LobbyRelay relay, Lobbies.LobbySteamHost steamHost, ulong invitedBy = 0UL)
        {
            // Mismo embudo que el host: ver el comentario en StartAsHost. Cubre el doble clic en
            // Join, el Join durante un Joining y el auto-join de Steam llegando encima de uno
            // manual.
            if (!SessionState.Current.RequestStart(Role.Joiner))
            {
                Debug.LogWarning(
                    $"[NetworkInitializer] Join ignorado: ya hay sesion en fase {SessionState.Phase}. " +
                    "Sal de la actual antes de arrancar otra.");
                return;
            }
            // La higiene del destino va AQUÍ y no en el panel porque éste es el embudo por el que
            // pasan las tres rutas de join —manual, [Retry] y navegador de Steam—, así que una
            // sola comprobación las cubre y ninguna futura se queda fuera. Ver
            // `HostAddressInput`: en la sesión del 2026-08-31 una dirección PEGADA con un salto de
            // línea produjo `CONNECT_TO=31.4.149.48\n:7778`, que no parsea.
            //
            // El rechazo es ANTES de `TerminateLeftoverBackend` y antes de lanzar nada: matar el
            // backend anterior y arrancar otro condenado para que falle dentro es exactamente el
            // camino largo que costó el diagnóstico.
            // ADR-117 D7: un lobby sin endpoint directo no anuncia `connect_ip`, así que aquí llega
            // vacío —y eso NO es un campo sin rellenar, es un host que no tenía ninguno defendible.
            // Sin esta rama, `NormalizeOrDefault` lo convertiría en `127.0.0.1` y el backend
            // gastaría los cinco segundos de la etapa directa disparando contra su propio loopback.
            // ADR-135 añade el lobby Steam-only al mismo caso.
            bool indirectOnly = string.IsNullOrWhiteSpace(serverIP) && (relay.IsValid || steamHost.IsValid);

            string host = indirectOnly ? null : HostAddressInput.NormalizeOrDefault(serverIP);
            if (!indirectOnly && !HostAddressInput.IsUsable(host, out string hostProblem))
            {
                CurrentRole = Role.None;
                StatusMessage = $"Dirección inválida: {hostProblem}";
                Debug.LogError($"[NetworkInitializer] Join abortado, {hostProblem}");
                SessionState.Current.NotifyFailed(hostProblem);
                return;
            }
            if (!indirectOnly && !string.Equals(host, serverIP, StringComparison.Ordinal))
            {
                // Que quede dicho: un valor que cambia al limpiarlo es el síntoma de un pegado, y
                // sin esta línea el log enseña la dirección ya limpia y nadie sabría que venía
                // sucia.
                Debug.LogWarning(
                    $"[NetworkInitializer] La dirección del host venía con espacio en blanco y se ha " +
                    $"limpiado: {serverIP.Replace("\r", "\\r").Replace("\n", "\\n")} -> {host}");
            }
            serverIP = host;

            TerminateLeftoverBackend("arranque de joiner");

            CurrentRole = Role.Joiner;
            StatusMessage = "Starting backend (joiner)...";
            Debug.Log($"[NetworkInitializer] user input hostIp={serverIP}, hostPort={serverNetPort}");

            string sessionMode = ReadSessionMode();
            var config = SelectLaunchConfig("joiner", ipcPort + joinerNetPortOffset, netPort + joinerNetPortOffset, joinerNetId);
            StoreSelectedConfig(config);
            LastEffectiveRole = "joiner";
            LastConnectTo = indirectOnly ? "<indirecta>" : $"{serverIP}:{serverNetPort}";
            ConfigureIpcClient(config.IpcAddress, config.IpcPort);
            ArmSessionEndHandler();
            ResetSessionScopedRegistries();

            var env = new Dictionary<string, string>
            {
                ["IPC_PORT"] = config.IpcPort.ToString(),
                ["NET_PORT"] = config.NetPort.ToString(),
                ["NET_ID"] = config.NetId.ToString(),
                ["NET_NAME"] = playerName,
                ["RUST_LOG"] = "info",
            };

            // ADR-117 D7: sin endpoint directo NO se pone `CONNECT_TO`. Ponerlo vacío o en
            // loopback haría que el backend gastara la etapa directa contra sí mismo.
            if (!indirectOnly)
            {
                env["CONNECT_TO"] = $"{serverIP}:{serverNetPort}";
            }

            AddSteamTunnelEnv(env, steamHost);
            AddRelayEnv(env, relay, asHost: false);
            // ADR-136 D1/D2: quién soy y quién me invitó, para que el anfitrión me ponga a su lado.
            // Dos números opacos para el backend; sin Steam no se pone ninguno.
            Connectivity.PeerIdentityEnv.Apply(env, SteamLobbyManager.LocalSteamId, invitedBy);
            if (invitedBy != 0UL)
                Debug.Log($"[NetworkInitializer] INVITED_BY={invitedBy}: se pide nacer al lado del invitador (ADR-136).");
            AddIpcAddressEnv(env, config.IpcAddress, config.IpcPort);
            AddRoomManifestEnv(env);
            AddWorldGen3Env(env);

            LogLaunchConfig(
                sessionMode,
                config.IpcAddress,
                config.IpcPort,
                config.NetPort,
                config.NetId,
                playerName,
                "joiner",
                false,
                config.DefaultIpcOccupied,
                env);

            if (!LaunchBackendProcess(env))
                return;

            IPCClient.Instance?.StartClient();
            _waitingForBackend = true;
            _startupTimer = 0f;
        }

        /// <summary>
        /// OS-level environment variables the child backend process needs regardless of role,
        /// verified empirically against <c>backrooms_server.exe</c> rather than guessed:
        /// launching it with a fully stripped <see cref="ProcessStartInfo.EnvironmentVariables"/>
        /// fails with "Failed to bind P2P UDP socket: os error 10106" (WSAEPROVIDERFAILEDINIT —
        /// Winsock cannot initialize) before either socket binds; adding ONLY <c>SystemRoot</c>
        /// (no PATH, no TEMP) was sufficient for a full startup — both sockets bound, IPC
        /// listening, world generated. Nothing else is in this list on purpose: every other
        /// variable the backend reads (IPC_PORT, NET_PORT, WORLD_SEED, CONNECT_TO, ...) is
        /// explicit application config, declared per-launch by the caller — never inherited.
        /// </summary>
        private static readonly string[] EssentialPassthroughEnvKeys = { "SystemRoot" };

        /// <summary>
        /// Mete la sesión de relay en el entorno del backend (ADR-117).
        ///
        /// **`RELAY_ROLE` no es redundante con `CONNECT_TO`.** El backend deducía el rol SÓLO de
        /// esa variable: sin ella, host. Un joiner de un lobby relay-only no la recibe, así que sin
        /// `RELAY_ROLE` habría arrancado como host y se habría puesto a servir un mundo en
        /// solitario — el mismo fallo mudo que ADR-111 vino a cerrar, colándose por otra puerta.
        ///
        /// El token va aquí y **no se escribe en ningún log**: el entorno del proceso hijo es la
        /// vía por la que ya viajan `NET_NAME` y las rutas de manifiesto, y no aparece en la línea
        /// de comandos.
        /// </summary>
        /// <summary>
        /// Abre el túnel de Steam del JOINER y pone `CONNECT_STEAM` — ADR-135 D3.
        ///
        /// El puerto se elige **antes** de lanzar el backend porque tiene que viajar en su entorno.
        /// Si el túnel no se puede abrir —Steam cerrado, el host no publicó su `SteamId`, la red de
        /// Valve no contesta— no se pone la variable y la etapa Steam simplemente no existe: las
        /// otras vías de la secuencia siguen intactas.
        ///
        /// **No se entra al lobby de Steam** (D4'.1): la autorización es el secreto que el lobby ya
        /// publicó y que el túnel manda en su primer mensaje.
        /// </summary>
        private static void AddSteamTunnelEnv(Dictionary<string, string> env, Lobbies.LobbySteamHost steamHost)
        {
            if (!steamHost.IsValid) return;

            int localPort = Connectivity.SteamTunnelRunner.BeginJoiner(
                new FacepunchSteamTunnelTransport(), steamHost.SteamId, steamHost.Secret);
            if (localPort <= 0) return;

            env["CONNECT_STEAM"] = $"127.0.0.1:{localPort}";
            // El secreto NO se registra (ADR-135 D4'.6).
            Debug.Log($"[NetworkInitializer] CONNECT_STEAM=127.0.0.1:{localPort} (host {steamHost}).");
        }

        private static void AddRelayEnv(Dictionary<string, string> env, Lobbies.LobbyRelay relay, bool asHost)
        {
            if (!relay.IsValid) return;

            env["RELAY_ADDR"] = relay.Address;
            env["RELAY_SESSION"] = relay.Session;
            env["RELAY_TOKEN"] = relay.Token;
            env["RELAY_ROLE"] = asHost ? "host" : "joiner";

            Debug.Log($"[NetworkInitializer] RELAY_ADDR={relay.Address} RELAY_SESSION={relay.Session} " +
                      $"RELAY_ROLE={(asHost ? "host" : "joiner")} (token oculto)");
        }

        /// <summary>
        /// Builds the CLOSED set of environment variables the child backend process receives:
        /// exactly <paramref name="declared"/> (the per-launch app config built by StartAsHost/
        /// StartAsJoiner) plus <see cref="EssentialPassthroughEnvKeys"/>, read from
        /// <paramref name="parentEnvReader"/> ONLY when <paramref name="declared"/> doesn't
        /// already set that key. Nothing else survives.
        ///
        /// This exists because <see cref="ProcessStartInfo.EnvironmentVariables"/> starts
        /// pre-populated with a COPY of Unity's own process environment — assigning keys into it
        /// (the old code) only ever ADDS or OVERWRITES, it can never remove. A key Unity's own
        /// process happened to carry (e.g. <c>CONNECT_TO</c>, left over from THIS SAME Unity
        /// process having joined a previous session) rode along into every later launch that
        /// didn't explicitly declare it — a Host launch silently inherited a joiner's
        /// <c>CONNECT_TO</c> and the backend started as a joiner instead, with <c>is_host=false</c>
        /// silently disabling world load/save/lock and per-player persistence, no error anywhere.
        ///
        /// Pure and testable without spawning a process: <paramref name="parentEnvReader"/> is
        /// injected so a test can simulate a poisoned parent environment without touching the
        /// real one. Comparisons are ordinal-ignore-case, matching how Windows itself treats
        /// environment variable names.
        ///
        /// Public rather than private so the EditMode suite can reach it directly: the
        /// compile-check builds each assembly with a <c>_check</c> suffix, so an
        /// <c>InternalsVisibleTo</c> friend name never matches — same reason
        /// <c>InventoryRestorer.ParseStacks</c>/<c>SessionEndHandler.ReadReason</c> are public.
        /// </summary>
        public static Dictionary<string, string> BuildChildEnvironment(
            IDictionary<string, string> declared,
            Func<string, string> parentEnvReader)
        {
            var result = new Dictionary<string, string>(declared, StringComparer.OrdinalIgnoreCase);
            foreach (string key in EssentialPassthroughEnvKeys)
            {
                if (result.ContainsKey(key))
                    continue; // an explicitly declared value always wins over the OS passthrough

                string value = parentEnvReader(key);
                if (!string.IsNullOrEmpty(value))
                    result[key] = value;
            }
            return result;
        }

        private bool LaunchBackendProcess(Dictionary<string, string> env)
        {
            WarnIfExistingBackend();

            string exePath = ResolveBackendPath();
            if (exePath == null)
            {
                StatusMessage = "Error: Backend executable not found. Build or copy backrooms_server.exe.";
                // Fail loudly: never silently continue with an unverifiable backend.
                Debug.LogError("[NetworkInitializer] MPTRACE step=RUBIK event=unity_backend_exe_path path=UNRESOLVED status=fail_loud");
                Debug.LogError("[NetworkInitializer] Backend executable not found. Build or copy backrooms_server.exe.");
                SessionState.Current.NotifyFailed("backend executable not found");
                return false;
            }

            Debug.Log($"[NetworkInitializer] MPTRACE step=RUBIK event=unity_backend_exe_path path={exePath}");
            WarnIfBackendIsStale(exePath);

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Application.persistentDataPath,
            };

            // Allowlist, not a blacklist of keys to remove: ProcessStartInfo.EnvironmentVariables
            // starts pre-populated with Unity's OWN process environment, and assigning into it
            // can only add/overwrite — Clear() first makes the child's env EXACTLY
            // BuildChildEnvironment's result, no matter what Unity's process happens to carry.
            // See that method's doc comment for the bug this fixes.
            var childEnv = BuildChildEnvironment(env, Environment.GetEnvironmentVariable);
            psi.EnvironmentVariables.Clear();
            foreach (var kvp in childEnv)
                psi.EnvironmentVariables[kvp.Key] = kvp.Value;

            try
            {
                // Llegados aqui `_backendProcess` tiene que ser null: TerminateLeftoverBackend lo
                // dejo asi al principio de StartAsHost/StartAsJoiner. El `Dispose` de antes solo
                // soltaba NUESTRO envoltorio y dejaba el proceso vivo - un huerfano por cada
                // relanzamiento. Se avisa a gritos si la invariante se rompe en vez de repetir el
                // Dispose silencioso.
                if (_backendProcess != null)
                {
                    Debug.LogError("[NetworkInitializer] INVARIANTE ROTA: se lanza un backend con otro " +
                                   "todavia registrado. Se termina el anterior para no dejar huerfanos.");
                    KillBackend();
                }

                _backendProcess = Process.Start(psi);
                BackendLaunchCount++;
                _backendGeneration = SessionState.Current.Generation;
                int launchGeneration = _backendGeneration;
                _backendProcess.EnableRaisingEvents = true;
                _backendProcess.Exited += (s, e) => OnBackendExited(launchGeneration);
                OpenBackendLogFile(_backendProcess.Id, env);

                _backendProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        LogBackendLine(e.Data, false);
                };
                _backendProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        LogBackendLine(e.Data, true);
                };

                _backendProcess.BeginOutputReadLine();
                _backendProcess.BeginErrorReadLine();

                Debug.Log($"[NetworkInitializer] Launched backend PID={_backendProcess.Id} from {exePath}");
                Debug.Log("[NetworkInitializer] backend launch succeeded");
                // This initializer ALWAYS spawns a fresh backend (on a free IPC
                // port if 7777 is busy) and connects to it — never to a stale one.
                Debug.Log($"[NetworkInitializer] MPTRACE step=RUBIK event=unity_backend_launch_mode mode=launched_new_backend pid={_backendProcess.Id} exe={exePath} ipc_port={LastSelectedIpcPort}");
                SessionState.Current.NotifyBackendLaunched();
                return true;
            }
            catch (Exception e)
            {
                StatusMessage = $"Error: {e.Message}";
                Debug.LogError("[NetworkInitializer] backend launch failed");
                Debug.LogError($"[NetworkInitializer] Failed to start backend: {e}");
                SessionState.Current.NotifyFailed($"backend launch failed: {e.Message}");
                return false;
            }
        }

        // Detect (but do not connect to) a backend already holding IPC port 7777.
        // We always launch a fresh isolated backend; this just makes the stale
        // process visible in logs (with PID) so identity is never ambiguous.
        private void WarnIfExistingBackend()
        {
            bool occupied = !PortUtility.IsTcpPortAvailable(7777);
            if (!occupied)
            {
                Debug.Log("[NetworkInitializer] MPTRACE step=RUBIK event=unity_existing_backend_detected ipc_port=7777 occupied=false");
                return;
            }

            Debug.LogWarning("[NetworkInitializer] MPTRACE step=RUBIK event=unity_existing_backend_detected ipc_port=7777 occupied=true note=launching_fresh_isolated_backend");
            try
            {
                foreach (var p in Process.GetProcessesByName("backrooms_server"))
                {
                    try { Debug.LogWarning($"[NetworkInitializer] existing backrooms_server detected PID={p.Id}"); }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkInitializer] could not enumerate backend processes: {e.Message}");
            }
        }

        private void Update()
        {
            if (_waitingForBackend)
                UpdateBackendStartup();

            UpdateJoinerHandshakeBackstop();
            WatchBackendLiveness();
        }

        private void UpdateBackendStartup()
        {
            _startupTimer += Time.unscaledDeltaTime;

            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                _waitingForBackend = false;
                IsBackendReady = true;
                // El IPC arriba NO es "Connected" para un joiner: su backend local acepta ese TCP
                // aunque el host no exista. La fase la decide la maquina, que para un joiner se
                // queda en Connecting hasta `session_joined`; el texto tiene que decir lo mismo o
                // vuelve el fallo entero por la puerta de la UI.
                bool isJoiner = CurrentRole == Role.Joiner;
                SessionState.Current.NotifyIpcConnected();
                StatusMessage = isJoiner ? $"Contacting host {LastConnectTo}..." : "Connected";
                _handshakeTimer = 0f;
                Debug.Log("[NetworkInitializer] Backend is ready, IPC connected");
                Debug.Log($"[NetworkInitializer] MPTRACE step=RUBIK event=unity_ipc_port_connected ipc_address={LastSelectedIpcAddress} ipc_port={LastSelectedIpcPort} launch_mode=launched_new_backend");
                return;
            }

            if (_backendProcess != null && _backendProcess.HasExited)
            {
                _waitingForBackend = false;
                int code = SafeExitCode();
                StatusMessage = $"Error: backend exited with code {code}";
                Debug.LogError($"[NetworkInitializer] Backend died during startup (exit code {code})");
                SessionState.Current.NotifyFailed($"backend exited with code {code}");
                return;
            }

            if (_startupTimer > startupTimeout)
            {
                _waitingForBackend = false;
                StatusMessage = "Timeout: backend did not respond";
                Debug.LogWarning("[NetworkInitializer] Backend startup timed out");
                SessionState.Current.NotifyFailed("backend did not respond");
            }
            else
            {
                StatusMessage = $"Connecting... ({_startupTimer:0.0}s)";
            }
        }

        /// <summary>
        /// Backstop del handshake del joiner. Ver <see cref="joinerHandshakeTimeout"/>: quien
        /// acota de verdad es el `CONNECT_TIMEOUT` del backend, y esto solo cubre el caso en que
        /// su aviso NO puede llegar.
        /// </summary>
        private void UpdateJoinerHandshakeBackstop()
        {
            if (CurrentRole != Role.Joiner) return;
            if (SessionState.Phase != SessionPhase.Connecting) { _handshakeTimer = 0f; return; }

            _handshakeTimer += Time.unscaledDeltaTime;
            if (_handshakeTimer <= joinerHandshakeTimeout) return;

            _handshakeTimer = 0f;
            StatusMessage = $"Timeout: no session confirmation from {LastConnectTo}";
            Debug.LogError(
                "[NetworkInitializer] BACKSTOP: el backend local no confirmo `session_joined` y " +
                $"tampoco mando `session_ended` en {joinerHandshakeTimeout:0}s. Su CONNECT_TIMEOUT " +
                "es de 15 s, asi que este camino significa que el aviso autoritativo se perdio " +
                "(proceso muerto o IPC caido). Mira el log del backend antes de culpar al timeout.");
            SessionState.Current.NotifyFailed($"no session confirmation from {LastConnectTo}");
        }

        /// <summary>
        /// El backend puede morir DESPUES de que el IPC conectara y antes de que la sesion se
        /// establezca (o en mitad de la partida). Sin esto, ese caso no lo detectaba nadie: el
        /// gate de arranque ya habia soltado `_waitingForBackend` y el panel se quedaba en
        /// "Joining..." para siempre - "un backend muerto no puede dejar la UI bloqueada".
        /// </summary>
        private float _livenessTimer;
        /// Cada medio segundo, no cada frame: `Process.HasExited` es una llamada al SO
        /// (GetExitCodeProcess) y esto corre durante toda la partida. Medio segundo de retraso en
        /// detectar un backend muerto no lo nota nadie; 60 P/Invoke por segundo, en un profiler sí.
        private const float LivenessCheckSeconds = 0.5f;

        private void WatchBackendLiveness()
        {
            if (_backendProcess == null) return;
            if (!SessionStateMachine.IsLive(SessionState.Phase)) return;

            _livenessTimer += Time.unscaledDeltaTime;
            if (_livenessTimer < LivenessCheckSeconds) return;
            _livenessTimer = 0f;

            bool exited;
            try { exited = _backendProcess.HasExited; }
            catch { return; }
            if (!exited) return;

            int code = SafeExitCode();
            string reason = $"backend exited with code {code}";
            Debug.LogError($"[NetworkInitializer] {reason} (sesion viva en fase {SessionState.Phase})");
            IsBackendReady = false;
            _waitingForBackend = false;
            StatusMessage = $"Error: {reason}";

            if (!SessionState.Current.NotifyFailed(reason))
                SessionEndHandler.LeaveCurrentSession(reason, showPanel: true);
        }

        private int SafeExitCode()
        {
            try { return _backendProcess?.ExitCode ?? -1; }
            catch { return -1; }
        }

        /// <summary>
        /// Shout when the exe about to be launched is OLDER than the Rust sources it was built
        /// from. Editor-only, log-only — it never blocks the launch.
        ///
        /// This exists because a stale binary is INVISIBLE at runtime and cost three straight
        /// play-tests (2026-08-25): the ADR-093 door fix was committed, `cargo test` was green
        /// 1004/0, and the game still did nothing — because `cargo test` builds the TEST binary
        /// and never relinks `target/release/backrooms_server.exe`. Nothing in the wire schema
        /// changes when only behaviour changes, so <see cref="WireSchema"/>'s version gate
        /// cannot catch this class of drift; a file timestamp can.
        ///
        /// Deliberately compares against `backend/src`, not the whole crate: `Cargo.lock` and
        /// `target/` churn for reasons that do not change the binary's behaviour.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private static void WarnIfBackendIsStale(string exePath)
        {
            try
            {
                string srcDir = Path.Combine(ResolveProjectRoot(Application.dataPath), "backend", "src");
                if (!Directory.Exists(srcDir) || !File.Exists(exePath))
                    return; // a packaged build with no sources alongside it — nothing to compare

                DateTime exeTime = File.GetLastWriteTimeUtc(exePath);
                string newest = null;
                DateTime newestTime = DateTime.MinValue;
                foreach (string rs in Directory.EnumerateFiles(srcDir, "*.rs", SearchOption.AllDirectories))
                {
                    DateTime t = File.GetLastWriteTimeUtc(rs);
                    if (t > newestTime)
                    {
                        newestTime = t;
                        newest = rs;
                    }
                }

                if (newest == null || newestTime <= exeTime)
                    return;

                Debug.LogError(
                    "[NetworkInitializer] STALE BACKEND: the exe about to run predates the Rust sources — " +
                    $"you are play-testing OLD server behaviour.\n  exe   {exePath} ({exeTime:u})\n" +
                    $"  newer {newest} ({newestTime:u})\n" +
                    "  Fix: cargo build --release && tools/dev/CopyReleaseBackendToBuild.ps1 " +
                    "(cargo test does NOT relink the release exe).");
            }
            catch (Exception e)
            {
                // A freshness check must never be the reason the game fails to start.
                Debug.LogWarning($"[NetworkInitializer] backend freshness check skipped: {e.Message}");
            }
        }

        private string ResolveBackendPath()
        {
            string dataPath = Application.dataPath;
            string streamingAssetsPath = Application.streamingAssetsPath;
            string currentDirectory = Directory.GetCurrentDirectory();
            string appBaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string buildFolder = ResolveBuildFolder(dataPath, appBaseDirectory);
            string projectRoot = ResolveProjectRoot(dataPath);

            Debug.Log($"[NetworkInitializer] Application.dataPath={dataPath}");
            Debug.Log($"[NetworkInitializer] Application.streamingAssetsPath={streamingAssetsPath}");
            Debug.Log($"[NetworkInitializer] Directory.GetCurrentDirectory()={currentDirectory}");
            Debug.Log($"[NetworkInitializer] AppDomain.CurrentDomain.BaseDirectory={appBaseDirectory}");
            Debug.Log($"[NetworkInitializer] backend build folder={buildFolder}");
            Debug.Log($"[NetworkInitializer] backend project root={projectRoot}");

            string explicitExe = Environment.GetEnvironmentVariable("BACKROOMS_BACKEND_EXE");
            if (!string.IsNullOrWhiteSpace(explicitExe))
            {
                string selected = CheckBackendCandidate(explicitExe, "BACKROOMS_BACKEND_EXE");
                if (selected != null) return selected;
            }

            var candidates = new List<string>();
            // Canonical packaged location (matches the validation copy step):
            // <projectRoot>/Builds/Backend/backrooms_server.exe. Preferred so the
            // runtime always uses the build that was validated/copied, removing
            // "which backend is running?" ambiguity.
            candidates.Add(Path.Combine(projectRoot, "Builds", "Backend", executableName));
            if (Application.isEditor)
            {
                candidates.Add(Path.Combine(buildFolder, executableName));
                candidates.Add(Path.Combine(buildFolder, "Backend", executableName));
            }
            else
            {
                candidates.Add(Path.Combine(buildFolder, "Backend", executableName));
                candidates.Add(Path.Combine(buildFolder, executableName));
            }

            candidates.Add(Path.Combine(buildFolder, "backend", "target", "release", executableName));
            candidates.Add(Path.Combine(streamingAssetsPath, "Backend", executableName));
            candidates.Add(Path.Combine(streamingAssetsPath, executableName));
            candidates.Add(Path.IsPathRooted(backendPath) ? backendPath : Path.Combine(projectRoot, backendPath));
            candidates.Add(Path.IsPathRooted(fallbackBackendPath) ? fallbackBackendPath : Path.Combine(projectRoot, fallbackBackendPath));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                string fullPath = Path.GetFullPath(candidate);
                if (!seen.Add(fullPath)) continue;

                string selected = CheckBackendCandidate(fullPath, "candidate");
                if (selected != null) return selected;
            }

            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string candidate = Path.Combine(dir.Trim(), executableName);
                    string selected = CheckBackendCandidate(candidate, "PATH");
                    if (selected != null) return selected;
                }
            }

            return null;
        }

        private static string CheckBackendCandidate(string candidate, string source)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkInitializer] backend search candidate path={candidate}, source={source}, invalid={e.Message}");
                return null;
            }

            bool exists = File.Exists(fullPath);
            Debug.Log($"[NetworkInitializer] backend search candidate path={fullPath}, source={source}, exists={exists}");

            if (!exists) return null;

            Debug.Log($"[NetworkInitializer] selected backend path={fullPath}");
            return fullPath;
        }

        private static string ResolveBuildFolder(string dataPath, string appBaseDirectory)
        {
            if (!Application.isEditor)
            {
                string normalizedDataPath = Path.GetFullPath(dataPath);
                if (normalizedDataPath.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                {
                    string parent = Directory.GetParent(normalizedDataPath)?.FullName;
                    if (!string.IsNullOrWhiteSpace(parent))
                        return parent;
                }

                if (!string.IsNullOrWhiteSpace(appBaseDirectory))
                    return Path.GetFullPath(appBaseDirectory);
            }

            return ResolveProjectRoot(dataPath);
        }

        private static string ResolveProjectRoot(string dataPath)
        {
            string normalizedDataPath = Path.GetFullPath(dataPath);
            if (string.Equals(Path.GetFileName(normalizedDataPath), "Assets", StringComparison.OrdinalIgnoreCase))
                return Directory.GetParent(normalizedDataPath)?.FullName ?? normalizedDataPath;

            if (normalizedDataPath.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                return Directory.GetParent(normalizedDataPath)?.FullName ?? normalizedDataPath;

            return Path.GetFullPath(Path.Combine(normalizedDataPath, ".."));
        }

        private static int ReadIntEnv(string name, int fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, out int parsed) ? parsed : fallback;
        }

        private static LaunchConfig SelectLaunchConfig(string roleName, int fallbackIpcPort, int fallbackNetPort, int fallbackNetId)
        {
            return SelectLaunchConfig(roleName, fallbackIpcPort, fallbackNetPort, fallbackNetId, null);
        }

        private static LaunchConfig SelectLaunchConfig(string roleName, int fallbackIpcPort, int fallbackNetPort, int fallbackNetId, int? forcedNetPort)
        {
            ResolveIpcEndpoint(fallbackIpcPort, out string ipcAddress, out int requestedIpcPort);

            bool defaultIpcOccupied = !PortUtility.IsTcpPortAvailable(7777);
            bool ipcFromEnv = HasEnv("IPC_PORT") || HasEnv("IPC_ADDR");
            bool netFromEnv = HasEnv("NET_PORT");
            bool idFromEnv = HasEnv("NET_ID");

            int selectedIpcPort = requestedIpcPort;
            if (!PortUtility.IsTcpPortAvailable(selectedIpcPort))
            {
                int free = PortUtility.FindFreeTcpPort(selectedIpcPort + 1);
                Debug.LogWarning(
                    $"[NetworkInitializer] Port busy, selected free port {free} for IPC_PORT");
                selectedIpcPort = free;
            }

            int requestedNetPort = forcedNetPort ?? ReadIntEnv("NET_PORT", fallbackNetPort);
            if (forcedNetPort.HasValue && netFromEnv)
                Debug.LogWarning($"[NetworkInitializer] NET_PORT env is set, but manual host port {forcedNetPort.Value} takes priority");
            int selectedNetPort = requestedNetPort;
            if (selectedNetPort == selectedIpcPort || !PortUtility.IsUdpPortAvailable(selectedNetPort))
            {
                int start = selectedNetPort + 1;
                int free = PortUtility.FindFreeUdpPort(start);
                while (free == selectedIpcPort)
                    free = PortUtility.FindFreeUdpPort(free + 1);
                Debug.LogWarning(
                    $"[NetworkInitializer] Port busy, selected free port {free} for NET_PORT");
                selectedNetPort = free;
            }

            int selectedNetId;
            if (idFromEnv)
                selectedNetId = ReadIntEnv("NET_ID", fallbackNetId);
            else if (roleName == "autosolo" && (selectedIpcPort != 7777 || selectedNetPort != 7778))
                selectedNetId = GenerateDebugNetId();
            else if (roleName == "joiner")
                selectedNetId = GenerateDebugNetId();
            else
                selectedNetId = fallbackNetId;

            if (!idFromEnv && selectedNetId != fallbackNetId)
                Debug.LogWarning($"[NetworkInitializer] NET_ID conflict, selected NET_ID {selectedNetId}");

            if (!ipcFromEnv && roleName == "autosolo" && defaultIpcOccupied)
                Debug.LogWarning("[NetworkInitializer] Default IPC_PORT=7777 occupied; autosolo will launch an isolated backend");

            Debug.Log(
                $"[NetworkInitializer] Selected IPC_PORT={selectedIpcPort} (tcp), " +
                $"Selected NET_PORT={selectedNetPort} (udp), Selected NET_ID={selectedNetId}, " +
                $"ipcFromEnv={ipcFromEnv}, netFromEnv={netFromEnv}, idFromEnv={idFromEnv}");

            return new LaunchConfig
            {
                IpcAddress = ipcAddress,
                IpcPort = selectedIpcPort,
                NetPort = selectedNetPort,
                NetId = selectedNetId,
                DefaultIpcOccupied = defaultIpcOccupied,
            };
        }

        private static void ResolveIpcEndpoint(int fallbackPort, out string address, out int port)
        {
            address = "127.0.0.1";
            port = ReadIntEnv("IPC_PORT", fallbackPort);

            string ipcAddr = Environment.GetEnvironmentVariable("IPC_ADDR");
            if (string.IsNullOrWhiteSpace(ipcAddr)) return;

            int colon = ipcAddr.LastIndexOf(':');
            if (colon <= 0 || colon >= ipcAddr.Length - 1) return;

            string parsedAddress = ipcAddr.Substring(0, colon);
            string parsedPort = ipcAddr.Substring(colon + 1);
            if (!int.TryParse(parsedPort, out int parsedPortNumber)) return;

            address = parsedAddress;
            port = parsedPortNumber;
        }

        private static void AddIpcAddressEnv(Dictionary<string, string> env, string address, int port)
        {
            env["IPC_ADDR"] = $"{address}:{port}";
        }

        /// <summary>
        /// ADR-083 enmienda 1 — le dice al backend dónde está el manifiesto de salas autoradas
        /// (<c>room_manifest.json</c>, escrito por el horneado en StreamingAssets).
        ///
        /// Va por variable de entorno y no por una ruta que el backend deduzca solo: en el editor el
        /// ejecutable vive en <c>backend/target/release/</c> y el manifiesto en
        /// <c>Assets/StreamingAssets/</c>, dos sitios sin ninguna relación de ruta estable, y en un
        /// build la cosa cambia otra vez. Config explícita por lanzamiento, nunca heredada — mismo
        /// criterio que <c>WORLD_SEED</c> o <c>IPC_PORT</c>.
        ///
        /// Si el fichero no está, NO se declara la variable y el backend arranca sin salas
        /// autoradas. Es un estado válido a propósito: un proyecto sin pool horneado tiene que poder
        /// jugarse igual.
        /// </summary>
        private static void AddRoomManifestEnv(Dictionary<string, string> env)
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "room_manifest.json");
            if (System.IO.File.Exists(path))
                env["BACKROOMS_ROOM_MANIFEST"] = path;
            else
                Debug.LogWarning($"[NetworkInitializer] Sin manifiesto de salas en {path} — el mundo " +
                                 "saldrá sin salas autoradas. Ejecuta Backrooms ▸ Export Room Manifest.");
        }

        /// <summary>
        /// Enciende WorldGen3 en el backend si <see cref="enableWorldGen3"/> está marcado.
        ///
        /// Se DECLARAN las dos variables, no se heredan: <see cref="BuildChildEnvironment"/> es una
        /// lista blanca estricta, así que una variable que no se declare aquí no llega al hijo por
        /// mucho que esté puesta en el entorno de Unity. Es deliberado — la alternativa fue la que
        /// dejó a un host arrancando como joiner porque heredó un <c>CONNECT_TO</c> viejo.
        ///
        /// Sin manifiesto NO se enciende la bandera, aunque esté marcada: el backend con WG3 activo
        /// y sin catálogo sirve chunks vacíos —un mundo sin suelo por el que se cae— y eso es peor
        /// que quedarse en WG2. El backend hace la misma comprobación por su cuenta; ésta es para
        /// que el aviso salga en la consola de Unity, que es donde se está mirando.
        /// </summary>
        private void AddWorldGen3Env(Dictionary<string, string> env)
        {
            if (!enableWorldGen3) return;

            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "wg3_manifest.json");
            if (!System.IO.File.Exists(path))
            {
                Debug.LogError($"[NetworkInitializer] WorldGen3 pedido pero no hay manifiesto en {path}. " +
                               "Ejecuta Backrooms ▸ WorldGen3 ▸ Exportar manifiesto. Se arranca con WG2.");
                return;
            }

            env["BACKROOMS_WG3"] = "1";
            env["BACKROOMS_WG3_MANIFEST"] = path;
            Debug.Log($"[NetworkInitializer] WorldGen3 ACTIVO — manifiesto {path}");
        }

        // ADR-056: a new session is starting, so clear SessionEndHandler's once-per-session latch.
        // The latch is set on the first session_ended and cleared only on the paths where the
        // teardown FAILED — on the successful path it stays set, which is what stops the duplicate
        // event (goodbye packet AND heartbeat timeout) from killing a session that already replaced
        // the dead one. The handler sits on this same DontDestroyOnLoad object, so nothing else
        // clears it: without this call, session-end works exactly once per process.
        // Paired with ConfigureIpcClient — both undo a piece of the previous session's teardown.
        private void ArmSessionEndHandler()
        {
            var handler = GetComponent<SessionEndHandler>();
            if (handler != null)
                handler.ResetForNewSession();
        }

        // Paired with ArmSessionEndHandler for the same reason: BuildRoomRegistry y ZoneRegistry
        // solo se vacían por su cuenta en RuntimeInitializeOnLoadMethod(SubsystemRegistration), es
        // decir una vez por proceso — sin esto, reconectar a un mundo con seed distinta sin
        // reiniciar Unity deja salas/zonas fantasma del mundo anterior hasta que cada chunk se
        // vuelva a pedir. Los dos comparten causa raíz (mismo patrón de reset-solo-al-arrancar-
        // proceso) y se limpian en el mismo punto a propósito.
        private static void ResetSessionScopedRegistries()
        {
            BackroomsSurvival.Gameplay.BuildRoomRegistry.ResetForNewConnection();
            BackroomsSurvival.Gameplay.AuthoredRoomRegistry.ResetForNewConnection();
            // ADR-084 punto 5: los PREFABS ya instanciados, además de los planes. Cuelgan de un root
            // de mundo que no es hijo de nada del generador, así que reconectar a otra seed dejaría
            // las salas del mundo anterior flotando en el sitio donde estaban.
            BackroomsSurvival.Gameplay.GridWorld.AuthoredRoomInstances.ClearAll();
            BackroomsSurvival.Gameplay.ZoneRegistry.ResetForNewSession();
        }

        private static void ConfigureIpcClient(string address, int localIpcPort)
        {
            var ipc = IPCClient.Instance;
            if (ipc != null)
                ipc.ConfigureEndpoint(address, localIpcPort);
        }

        private void StoreSelectedConfig(LaunchConfig config)
        {
            LastSelectedIpcAddress = config.IpcAddress;
            LastSelectedIpcPort = config.IpcPort;
            LastSelectedNetPort = config.NetPort;
            LastSelectedNetId = config.NetId;
            Debug.Log($"[NetworkInitializer] selected IPC_PORT={LastSelectedIpcPort}");
            Debug.Log($"[NetworkInitializer] selected local NET_PORT={LastSelectedNetPort}");
            Debug.Log($"[NetworkInitializer] selected NET_ID={LastSelectedNetId}");
        }

        private static void LogLaunchConfig(
            string sessionMode,
            string localIpcAddress,
            int localIpcPort,
            int localNetPort,
            int localNetId,
            string netName,
            string roleName,
            bool autoHost,
            bool defaultIpcOccupied,
            IDictionary<string, string> env)
        {
            // Reads CONNECT_TO from `env` itself — the SAME dictionary LaunchBackendProcess turns
            // into the child's environment via BuildChildEnvironment — instead of a
            // separately-passed argument. A hand-passed value can drift from what actually ships:
            // the Host call site used to hardcode `null` here regardless of what the child's real
            // (Unity-inherited) environment carried, which is exactly what hid the CONNECT_TO leak
            // this method's caller now guards against.
            string target = env.TryGetValue("CONNECT_TO", out string connectTo) && !string.IsNullOrEmpty(connectTo)
                ? connectTo
                : "<none>";
            Debug.Log(
                $"[NetworkInitializer] Launch config: SESSION_MODE={FormatNone(sessionMode)}, " +
                $"IPC_ADDR={localIpcAddress}:{localIpcPort}, " +
                $"IPC_PORT={localIpcPort}, NET_PORT={localNetPort}, NET_ID={localNetId}, " +
                $"NET_NAME={netName}, role={roleName}, effective role={roleName}, autoHost={autoHost}, " +
                $"default IPC occupied={defaultIpcOccupied}, CONNECT_TO={target}");
        }

        private static string ReadSessionMode() => Environment.GetEnvironmentVariable("SESSION_MODE");

        private static bool HasEnv(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

        private static int GenerateDebugNetId()
        {
            int pid = Process.GetCurrentProcess().Id;
            return 1000 + (pid % 60000);
        }

        private static string FormatNone(string value) => string.IsNullOrWhiteSpace(value) ? "<none>" : value;

        private struct LaunchConfig
        {
            public string IpcAddress;
            public int IpcPort;
            public int NetPort;
            public int NetId;
            public bool DefaultIpcOccupied;
        }

        /// <summary>
        /// Reenvía a la consola de Unity las líneas INFO/DEBUG/TRACE del backend. APAGADO por
        /// defecto, y no es una preferencia de gusto: con `RUST_LOG=info` el backend emite
        /// MPTRACE por tick y por mover, cada línea se convierte en un <c>Debug.Log</c>, y
        /// Unity adjunta un stack trace de ~500 B a CADA UNO. El 2026-08-13 eso dejó un
        /// Editor.log de 8 GB y el editor murió en ScriptingDomainUnload, llevándose por
        /// delante trabajo sin guardar.
        ///
        /// No se pierde diagnóstico: el backend ya escribe su propio log completo en
        /// Builds/PlaytestLogs/&lt;timestamp&gt;, que es donde se mira una traza a posteriori.
        /// Lo que esto corta es solo el ESPEJO en Unity. ERROR y WARN siguen pasando siempre
        /// — son raros y son justo lo que hay que ver sin ir a buscar un archivo.
        /// </summary>
        public static readonly bool VerboseBackendLog =
            Environment.GetEnvironmentVariable("BACKROOMS_VERBOSE_LOG") == "1";

        // ─── El log del backend, en un fichero ────────────────────────────────────────────────
        //
        // El backend escribe a stdout/stderr y Unity lo recibe línea a línea, pero el espejo a la
        // consola está FILTRADO: INFO y DEBUG se tiran salvo con `BACKROOMS_VERBOSE_LOG=1` (que el
        // 2026-08-13 dejó un Editor.log de 8 GB y mató al editor). Resultado: toda la traza de red
        // a nivel INFO —handshake, registro de peers, latidos, márgenes de liveness— NO QUEDABA EN
        // NINGÚN SITIO. Dos comentarios de este mismo archivo prometían un `Builds/PlaytestLogs/`
        // desde el 2026-08-24 y nadie lo escribía; comprobado otra vez el 2026-08-30, tampoco
        // existía el directorio.
        //
        // Esto lo escribe. SIN filtrar (el filtro es solo del espejo en consola) y sin stack traces
        // de Unity, que era lo que pesaba. Un fallo de conectividad a posteriori se diagnostica
        // leyendo este fichero, que es justo lo que no se podía hacer.
        private static readonly object _backendLogLock = new object();
        private static System.IO.StreamWriter _backendLogWriter;
        /// <summary>Ruta del log de esta sesión del backend, o cadena vacía si no se pudo abrir.</summary>
        public static string BackendLogPath { get; private set; } = "";

        private void OpenBackendLogFile(int pid, IDictionary<string, string> env)
        {
            CloseBackendLogFile();
            try
            {
                string dir = Path.Combine(ResolveProjectRoot(Application.dataPath), "Builds", "PlaytestLogs");
                Directory.CreateDirectory(dir);
                string role = LastEffectiveRole;
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path = Path.Combine(dir, $"backend_{role}_{stamp}_pid{pid}.log");

                var writer = new System.IO.StreamWriter(path, append: false) { AutoFlush = true };
                // AutoFlush por línea a propósito: el caso que hay que diagnosticar es justo aquel
                // en el que el proceso muere, y un buffer sin volcar se lleva por delante las
                // últimas líneas — las únicas que importan.
                writer.WriteLine($"# backend session role={role} pid={pid} started={DateTime.Now:O}");
                foreach (var kvp in env)
                    writer.WriteLine($"# env {kvp.Key}={kvp.Value}");

                lock (_backendLogLock)
                    _backendLogWriter = writer;

                BackendLogPath = path;
                Debug.Log($"[NetworkInitializer] Backend log -> {path}");
            }
            catch (Exception e)
            {
                BackendLogPath = "";
                // Nunca fatal: sin log el juego funciona igual, solo se diagnostica peor.
                Debug.LogWarning($"[NetworkInitializer] No se pudo abrir el log del backend: {e.Message}");
            }
        }

        private static void CloseBackendLogFile()
        {
            lock (_backendLogLock)
            {
                if (_backendLogWriter == null) return;
                try { _backendLogWriter.Dispose(); } catch { }
                _backendLogWriter = null;
            }
        }

        private static void LogBackendLine(string line, bool fromStdErr)
        {
            // Al fichero SIEMPRE y sin filtrar, antes que nada: es el único rastro completo.
            // stdout y stderr llegan por hilos distintos, de ahí el lock.
            lock (_backendLogLock)
            {
                if (_backendLogWriter != null)
                {
                    try { _backendLogWriter.WriteLine(line); }
                    catch { _backendLogWriter = null; } // un fallo de E/S no puede tumbar la sesión
                }
            }

            if (ContainsLogLevel(line, "ERROR"))
            {
                Debug.LogError($"[Backend] {line}");
                return;
            }
            if (ContainsLogLevel(line, "WARN"))
            {
                Debug.LogWarning($"[Backend] {line}");
                return;
            }

            // Todo lo demás es ruido de alta frecuencia. Las líneas SIN nivel reconocible
            // (arranque, panics sin formato) sí pasan: son pocas y son las que explican un
            // fallo de lanzamiento.
            if (!VerboseBackendLog && !IsRareEventTrace(line) &&
                (ContainsLogLevel(line, "INFO") ||
                 ContainsLogLevel(line, "DEBUG") ||
                 ContainsLogLevel(line, "TRACE")))
                return;

            Debug.Log($"[Backend] {line}");
        }

        /// <summary>
        /// Trazas que pasan el filtro de INFO aunque `VerboseBackendLog` esté apagado, porque son
        /// por EVENTO y no por tick.
        ///
        /// El motivo de apagar el espejo (8 GB de Editor.log el 2026-08-13) fue el MPTRACE del
        /// robapieles, que emite por tick Y por mover — cientos de líneas por segundo. Los eventos
        /// de faceling son de otra naturaleza: cerco abierto, pack congelado, golpe, robo, muerte.
        /// En una sesión entera son decenas, no millones, y sin ellos el comportamiento de las dos
        /// especies es INDIAGNOSTICABLE a posteriori: el `Builds/PlaytestLogs/` que este comentario
        /// prometía no lo escribe nadie (comprobado 2026-08-24), así que la consola de Unity es de
        /// hecho el único sitio donde queda rastro.
        ///
        /// Deliberadamente NO incluye `step=FL_POP`: el reconcile de población sí es periódico.
        /// </summary>
        private static bool IsRareEventTrace(string line)
        {
            return line.IndexOf("step=FL_", StringComparison.Ordinal) >= 0
                && line.IndexOf("step=FL_POP", StringComparison.Ordinal) < 0;
        }

        private static bool ContainsLogLevel(string line, string level)
        {
            return line.IndexOf(level, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Llega en un HILO DEL POOL, no en el de Unity, y puede llegar tarde: el backend de la
        /// sesion anterior avisa de su muerte cuando la nueva ya esta arriba. Por eso lo primero
        /// que hace es comparar generaciones - sin eso, la muerte de un backend viejo apagaba
        /// `IsBackendReady` y sobrescribia el `StatusMessage` de la sesion NUEVA.
        ///
        /// Solo toca campos y `Debug.Log` (ambos validos fuera del hilo principal); nada de API
        /// de Unity.
        /// </summary>
        private void OnBackendExited(int launchGeneration)
        {
            if (!ShouldApplyBackendExit(launchGeneration, _backendGeneration))
            {
                Debug.Log(
                    $"[NetworkInitializer] Exited de un backend viejo (gen={launchGeneration}, " +
                    $"actual={_backendGeneration}) IGNORADO: no puede tocar la sesion en curso.");
                return;
            }

            Debug.LogWarning("[NetworkInitializer] Backend process exited");
            IsBackendReady = false;
            _waitingForBackend = false;
            StatusMessage = "Backend exited";
            // El proceso murió: cerrar el fichero para que su cola quede en disco. El siguiente
            // lanzamiento abre uno nuevo — un log por sesión de backend, no uno acumulado.
            CloseBackendLogFile();
        }

        /// <summary>
        /// Mata el backend y borra el estado de sesion de ESTE componente. Idempotente: llamarlo
        /// dos veces no vuelve a matar nada (KillBackend sale solo si no hay proceso) y no deja
        /// un estado invalido.
        /// </summary>
        public void Shutdown()
        {
            _waitingForBackend = false;
            _handshakeTimer = 0f;
            IsBackendReady = false;
            KillBackend();
            CurrentRole = Role.None;
            StatusMessage = "";
            LastConnectTo = "<none>";
        }

        /// <summary>
        /// Restos de una sesion anterior que nadie limpio (el `Quit to Menu` del vendor era el
        /// caso real: cargaba el menu sin pasar por ningun teardown de red). Se termina ANTES de
        /// configurar el endpoint de la sesion nueva, porque `KillBackend` pide el guardado por
        /// el IPC y hacerlo despues se lo mandaria al puerto NUEVO.
        /// </summary>
        private void TerminateLeftoverBackend(string why)
        {
            if (_backendProcess == null) return;
            Debug.LogWarning(
                $"[NetworkInitializer] Backend de una sesion anterior seguia vivo al {why}; " +
                "se termina para no dejar un huerfano ocupando puertos.");
            KillBackend();
        }

        // ADR-032: ask the backend to persist the world NOW (before we kill it). Best-effort — if
        // the IPC stream is already down or this isn't a host, it's a no-op and we fall through
        // to the kill (the backend itself has an independent fallback for exactly that case —
        // see the ADR-045 fix note below). The send is synchronous (IPCClient.SendFrame), so on
        // success the frame is on the socket before we return. Uses TryGetInstance to bypass the
        // quitting gate (Instance returns null once MarkQuitting has run during
        // OnApplicationQuit).
        //
        // ADR-045 fix: this gate used to fail SILENTLY — no log at all when TryGetInstance
        // returned false, which is exactly what happens if IPCClient's OWN OnApplicationQuit (a
        // separate MonoBehaviour, no execution order between the two is guaranteed anywhere in
        // this project) runs first and nulls its singleton before this one gets to ask for a
        // save. Both failure branches below now log explicitly, so the next time this race wins,
        // it leaves a trace instead of looking like it worked. The backend-side fallback
        // (game_loop::run reacting to its own IPC disconnect) is the real fix for the race
        // itself — this is the diagnosability half.
        private void TryRequestBackendSave()
        {
            try
            {
                if (!IPCClient.TryGetInstance(out var ipc) || ipc == null)
                {
                    Debug.LogWarning(
                        "[NetworkInitializer] ADR-045: no IPCClient instance at save-on-quit time " +
                        "(likely its own OnApplicationQuit ran first) — skipping SendSaveAndShutdown, " +
                        "relying on the backend's own disconnect-triggered save.");
                    return;
                }

                if (!ipc.IsConnected)
                {
                    Debug.LogWarning(
                        "[NetworkInitializer] ADR-045: IPCClient instance found but not connected at " +
                        "save-on-quit time — skipping SendSaveAndShutdown, relying on the backend's own " +
                        "disconnect-triggered save.");
                    return;
                }

                ipc.SendSaveAndShutdown();
                Debug.Log("[NetworkInitializer] ADR-032: requested backend save-on-quit");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkInitializer] ADR-032: could not request backend save: {e.Message}");
            }
        }

        private void KillBackend()
        {
            if (_backendProcess == null) return;

            // ADR-032: graceful save-on-quit — request a synchronous save and give the backend a
            // short window to persist and self-exit BEFORE we force-kill. Never blocks indefinitely.
            TryRequestBackendSave();
            try
            {
                if (!_backendProcess.HasExited)
                    _backendProcess.WaitForExit(SaveOnQuitTimeoutMs);
            }
            catch { }

            try
            {
                _backendProcess.CancelOutputRead();
                _backendProcess.CancelErrorRead();
            }
            catch { }

            try
            {
                if (!_backendProcess.HasExited)
                {
                    _backendProcess.Kill();
                    _backendProcess.WaitForExit(2000);
                    Debug.Log("[NetworkInitializer] Backend process killed (save-on-quit timed out or unavailable)");
                }
                else
                {
                    Debug.Log("[NetworkInitializer] Backend exited gracefully after save-on-quit");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkInitializer] Error killing backend: {e.Message}");
            }
            finally
            {
                try { _backendProcess.Dispose(); } catch { }
                _backendProcess = null;
                _backendGeneration = -1;
                BackendTerminateCount++;
                CloseBackendLogFile();
            }
        }

        private void OnDestroy()
        {
            Shutdown();
            if (_instance == this) _instance = null;
        }

        private void OnApplicationQuit()
        {
            IPCClient.MarkQuitting();
            Shutdown();
        }
    }
}
