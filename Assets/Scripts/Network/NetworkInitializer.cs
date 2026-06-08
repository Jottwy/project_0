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

        public Role CurrentRole { get; private set; } = Role.None;
        public bool IsBackendReady { get; private set; }
        public string StatusMessage { get; private set; } = "";

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
        }

        public void StartAsHost(string playerName, int worldSeed = 42)
        {
            CurrentRole = Role.Host;
            StatusMessage = "Starting backend...";
            ResolveIpcEndpoint(ipcPort, out string localIpcAddress, out int localIpcPort);
            int localNetPort = ReadIntEnv("NET_PORT", netPort);
            int localNetId = ReadIntEnv("NET_ID", hostNetId);
            ConfigureIpcClient(localIpcAddress, localIpcPort);

            var env = new Dictionary<string, string>
            {
                ["IPC_PORT"] = localIpcPort.ToString(),
                ["NET_PORT"] = localNetPort.ToString(),
                ["NET_ID"] = localNetId.ToString(),
                ["NET_NAME"] = playerName,
                ["WORLD_SEED"] = worldSeed.ToString(),
                ["RUST_LOG"] = "info",
            };
            AddIpcAddressEnvIfSet(env);

            LogLaunchConfig(Role.Host, localIpcAddress, localIpcPort, localNetPort, localNetId, null);

            if (!LaunchBackendProcess(env))
                return;

            _waitingForBackend = true;
            _startupTimer = 0f;
        }

        public void StartAsJoiner(string serverIP, int serverNetPort, string playerName)
        {
            CurrentRole = Role.Joiner;
            StatusMessage = "Starting backend (joiner)...";

            ResolveIpcEndpoint(ipcPort + joinerNetPortOffset, out string localIpcAddress, out int localIpcPort);
            int localNetPort = ReadIntEnv("NET_PORT", netPort + joinerNetPortOffset);
            int localNetId = ReadIntEnv("NET_ID", joinerNetId);
            ConfigureIpcClient(localIpcAddress, localIpcPort);

            var env = new Dictionary<string, string>
            {
                ["IPC_PORT"] = localIpcPort.ToString(),
                ["NET_PORT"] = localNetPort.ToString(),
                ["NET_ID"] = localNetId.ToString(),
                ["NET_NAME"] = playerName,
                ["CONNECT_TO"] = $"{serverIP}:{serverNetPort}",
                ["RUST_LOG"] = "info",
            };
            AddIpcAddressEnvIfSet(env);

            LogLaunchConfig(Role.Joiner, localIpcAddress, localIpcPort, localNetPort, localNetId, $"{serverIP}:{serverNetPort}");

            if (!LaunchBackendProcess(env))
                return;

            _waitingForBackend = true;
            _startupTimer = 0f;
        }

        private bool LaunchBackendProcess(Dictionary<string, string> env)
        {
            string exePath = ResolveBackendPath();
            if (exePath == null)
            {
                StatusMessage = "Error: backend executable not found";
                Debug.LogError(
                    $"[NetworkInitializer] Backend not found at {backendPath}, " +
                    $"{fallbackBackendPath}, or on PATH ({executableName})");
                return false;
            }

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

            foreach (var kvp in env)
                psi.EnvironmentVariables[kvp.Key] = kvp.Value;

            try
            {
                _backendProcess = Process.Start(psi);
                _backendProcess.EnableRaisingEvents = true;
                _backendProcess.Exited += OnBackendExited;

                _backendProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Debug.Log($"[Backend] {e.Data}");
                };
                _backendProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Debug.LogWarning($"[Backend ERR] {e.Data}");
                };

                _backendProcess.BeginOutputReadLine();
                _backendProcess.BeginErrorReadLine();

                Debug.Log($"[NetworkInitializer] Launched backend PID={_backendProcess.Id} from {exePath}");
                return true;
            }
            catch (Exception e)
            {
                StatusMessage = $"Error: {e.Message}";
                Debug.LogError($"[NetworkInitializer] Failed to start backend: {e}");
                return false;
            }
        }

        private void Update()
        {
            if (!_waitingForBackend) return;

            _startupTimer += Time.unscaledDeltaTime;

            if (IPCClient.Instance.IsConnected)
            {
                _waitingForBackend = false;
                IsBackendReady = true;
                StatusMessage = "Connected";
                Debug.Log("[NetworkInitializer] Backend is ready, IPC connected");
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
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

            // 1. Check release build relative to project root.
            string primary = Path.IsPathRooted(backendPath)
                ? backendPath
                : Path.Combine(projectRoot, backendPath);
            if (File.Exists(primary)) return Path.GetFullPath(primary);

            // 2. Check debug build relative to project root.
            string fallback = Path.IsPathRooted(fallbackBackendPath)
                ? fallbackBackendPath
                : Path.Combine(projectRoot, fallbackBackendPath);
            if (File.Exists(fallback)) return Path.GetFullPath(fallback);

            // 3. Search PATH environment variable.
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string candidate = Path.Combine(dir.Trim(), executableName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        private static int ReadIntEnv(string name, int fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, out int parsed) ? parsed : fallback;
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

        private static void AddIpcAddressEnvIfSet(Dictionary<string, string> env)
        {
            string ipcAddr = Environment.GetEnvironmentVariable("IPC_ADDR");
            if (!string.IsNullOrWhiteSpace(ipcAddr))
                env["IPC_ADDR"] = ipcAddr;
        }

        private static void ConfigureIpcClient(string address, int localIpcPort)
        {
            IPCClient.Instance.ConfigureEndpoint(address, localIpcPort);
        }

        private static void LogLaunchConfig(Role role, string localIpcAddress, int localIpcPort, int localNetPort, int localNetId, string connectTo)
        {
            string roleName = role == Role.Host ? "host" : "joiner";
            string target = string.IsNullOrEmpty(connectTo) ? "<none>" : connectTo;
            Debug.Log(
                $"[NetworkInitializer] Launch config: IPC_ADDR={localIpcAddress}:{localIpcPort}, " +
                $"IPC_PORT={localIpcPort}, NET_PORT={localNetPort}, NET_ID={localNetId}, " +
                $"role={roleName}, CONNECT_TO={target}");
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

        private void KillBackend()
        {
            if (_backendProcess == null) return;
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
                    Debug.Log("[NetworkInitializer] Backend process killed");
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

        private void OnApplicationQuit() => Shutdown();
    }
}
