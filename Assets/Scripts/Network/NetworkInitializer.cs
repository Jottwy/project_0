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

        [Header("Debug")]
        [Tooltip("Spawn the robapieles (phantom peer) on the host for play-testing. Injects DEBUG_SPAWN_PHANTOM=1 into the backend env. Host-only; no effect on joiners.")]
        public bool debugSpawnPhantom = false;

        public Role CurrentRole { get; private set; } = Role.None;
        public bool IsBackendReady { get; private set; }
        public bool HasBackendProcess => _backendProcess != null && !_backendProcess.HasExited;
        public string StatusMessage { get; private set; } = "";
        public int LastSelectedIpcPort { get; private set; }
        public int LastSelectedNetPort { get; private set; }
        public int LastSelectedNetId { get; private set; }
        /// <summary>World seed handed to the backend as WORLD_SEED. Observable so the
        /// port-passed-as-seed regression stays covered by a test.</summary>
        public int LastSelectedWorldSeed { get; private set; }
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

        public void StartAsHost(string playerName, int worldSeed = 42)
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
        public void StartAsHostOnPort(string playerName, int hostListenPort, int worldSeed = 42)
        {
            StartAsHost(playerName, worldSeed, false, hostListenPort);
        }

        public void StartAsAutoSolo(string playerName, int worldSeed = 42)
        {
            StartAsHost(playerName, worldSeed, true, null);
        }

        private void StartAsHost(string playerName, int worldSeed, bool autoSolo)
        {
            StartAsHost(playerName, worldSeed, autoSolo, null);
        }

        private void StartAsHost(string playerName, int worldSeed, bool autoSolo, int? requestedHostListenPort)
        {
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

        public void StartAsJoiner(string serverIP, int serverNetPort, string playerName)
        {
            CurrentRole = Role.Joiner;
            StatusMessage = "Starting backend (joiner)...";
            Debug.Log($"[NetworkInitializer] user input hostIp={serverIP}, hostPort={serverNetPort}");

            string sessionMode = ReadSessionMode();
            var config = SelectLaunchConfig("joiner", ipcPort + joinerNetPortOffset, netPort + joinerNetPortOffset, joinerNetId);
            StoreSelectedConfig(config);
            LastEffectiveRole = "joiner";
            LastConnectTo = $"{serverIP}:{serverNetPort}";
            ConfigureIpcClient(config.IpcAddress, config.IpcPort);
            ArmSessionEndHandler();
            ResetSessionScopedRegistries();

            var env = new Dictionary<string, string>
            {
                ["IPC_PORT"] = config.IpcPort.ToString(),
                ["NET_PORT"] = config.NetPort.ToString(),
                ["NET_ID"] = config.NetId.ToString(),
                ["NET_NAME"] = playerName,
                ["CONNECT_TO"] = $"{serverIP}:{serverNetPort}",
                ["RUST_LOG"] = "info",
            };
            AddIpcAddressEnv(env, config.IpcAddress, config.IpcPort);

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
                return false;
            }

            Debug.Log($"[NetworkInitializer] MPTRACE step=RUBIK event=unity_backend_exe_path path={exePath}");

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
                // A previous handle (e.g. a caller that launches again without going through
                // KillBackend/Shutdown first) would otherwise leak its native handle here —
                // Dispose() only releases OUR wrapper, it does not touch whatever OS process it
                // pointed at.
                _backendProcess?.Dispose();
                _backendProcess = Process.Start(psi);
                _backendProcess.EnableRaisingEvents = true;
                _backendProcess.Exited += OnBackendExited;

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
                return true;
            }
            catch (Exception e)
            {
                StatusMessage = $"Error: {e.Message}";
                Debug.LogError("[NetworkInitializer] backend launch failed");
                Debug.LogError($"[NetworkInitializer] Failed to start backend: {e}");
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
            if (!_waitingForBackend) return;

            _startupTimer += Time.unscaledDeltaTime;

            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                _waitingForBackend = false;
                IsBackendReady = true;
                StatusMessage = "Connected";
                Debug.Log("[NetworkInitializer] Backend is ready, IPC connected");
                Debug.Log($"[NetworkInitializer] MPTRACE step=RUBIK event=unity_ipc_port_connected ipc_address={LastSelectedIpcAddress} ipc_port={LastSelectedIpcPort} launch_mode=launched_new_backend");
                return;
            }

            if (_backendProcess != null && _backendProcess.HasExited)
            {
                _waitingForBackend = false;
                StatusMessage = $"Error: backend exited with code {_backendProcess.ExitCode}";
                Debug.LogError($"[NetworkInitializer] Backend died during startup (exit code {_backendProcess.ExitCode})");
                return;
            }

            if (_startupTimer > startupTimeout)
            {
                _waitingForBackend = false;
                StatusMessage = "Timeout: backend did not respond";
                Debug.LogWarning("[NetworkInitializer] Backend startup timed out");
            }
            else
            {
                StatusMessage = $"Connecting... ({_startupTimer:0.0}s)";
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

        private static void LogBackendLine(string line, bool fromStdErr)
        {
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
            if (!VerboseBackendLog &&
                (ContainsLogLevel(line, "INFO") ||
                 ContainsLogLevel(line, "DEBUG") ||
                 ContainsLogLevel(line, "TRACE")))
                return;

            Debug.Log($"[Backend] {line}");
        }

        private static bool ContainsLogLevel(string line, string level)
        {
            return line.IndexOf(level, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnBackendExited(object sender, EventArgs e)
        {
            int exitCode = -1;
            try { exitCode = _backendProcess?.ExitCode ?? -1; } catch { }
            Debug.LogWarning($"[NetworkInitializer] Backend process exited (code={exitCode})");
            IsBackendReady = false;
            _waitingForBackend = false;
            StatusMessage = $"Backend exited (code={exitCode})";
        }

        public void Shutdown()
        {
            _waitingForBackend = false;
            IsBackendReady = false;
            KillBackend();
            CurrentRole = Role.None;
            StatusMessage = "";
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
