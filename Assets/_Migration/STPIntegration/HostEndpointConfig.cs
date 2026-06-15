using System;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// FIX for root cause (B): transport without a cross-machine bridge.
    ///
    /// The joiner's Rust backend bridges to the host over UDP at whatever address the Join
    /// flow passes as CONNECT_TO — which comes from JoinSessionUI's IP field, defaulting to
    /// 127.0.0.1 (loopback). On two physical machines that loopback never reaches the host,
    /// so the backends never peer and world_state.remote_players stays empty -> neither side
    /// sees the other. (The host's UDP socket already binds 0.0.0.0, so it IS LAN-reachable;
    /// only the joiner's target address is wrong.)
    ///
    /// This component exposes the host address as configuration and feeds it into the
    /// CONNECT_TO env hook that JoinSessionUI already honors, so the joiner targets the real
    /// host instead of localhost. Resolution order (first non-empty wins):
    ///   1. an explicit CONNECT_TO already set by the launcher (left untouched),
    ///   2. BACKROOMS_HOST_IP env var          (per-machine — recommended for 2-PC tests),
    ///   3. saved PlayerPrefs value             (per-machine, via SetHostAddress),
    ///   4. the inspector field on a placed instance (per-build — see caveat below).
    ///
    /// Opt-in and removable:
    ///   * HOST machine: leave everything empty -> CONNECT_TO unset -> hosts exactly as before.
    ///   * JOINER machine: set BACKROOMS_HOST_IP=192.168.x.y (or call SetHostAddress) -> joins it.
    /// Zero configuration == zero behavior change. Delete the file to remove entirely.
    ///
    /// CAVEAT: the inspector field is baked into the (shared) scene, so both machines would
    /// read it. For a 2-machine test prefer the per-machine env var / PlayerPrefs channels.
    /// </summary>
    public sealed class HostEndpointConfig : MonoBehaviour
    {
        public const string EnvHostIp = "BACKROOMS_HOST_IP";
        public const string EnvConnectTo = "CONNECT_TO";
        private const string PrefKey = "backrooms.hostAddress";
        private const int DefaultNetPort = 7778;

        [SerializeField]
        [Tooltip("Host address 'ip' or 'ip:port' to join. Empty = act as host / unchanged. " +
                 "Overridden by the BACKROOMS_HOST_IP env var or a saved PlayerPrefs value. " +
                 "NOTE: this field is shared via the scene; prefer the env var for per-machine setup.")]
        private string _hostAddress = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyFromConfig()
        {
            // Respect an explicit CONNECT_TO from the launcher / NetworkInitializer.
            if (HasEnv(EnvConnectTo))
                return;

            string addr = ResolveConfiguredAddress();
            if (string.IsNullOrWhiteSpace(addr))
                return; // No host configured -> no-op (this machine acts as host).

            addr = Normalize(addr);
            Environment.SetEnvironmentVariable(EnvConnectTo, addr);
            Debug.Log($"[HostEndpointConfig] CONNECT_TO='{addr}' (root-B fix: join real host instead of localhost).");
        }

        private void Awake()
        {
            // Inspector-placed fallback: only if nothing higher-priority is set.
            if (string.IsNullOrWhiteSpace(_hostAddress) || HasEnv(EnvConnectTo))
                return;
            if (!string.IsNullOrWhiteSpace(ResolveConfiguredAddress()))
                return; // env/PlayerPrefs already won in ApplyFromConfig.

            string addr = Normalize(_hostAddress);
            Environment.SetEnvironmentVariable(EnvConnectTo, addr);
            Debug.Log($"[HostEndpointConfig] CONNECT_TO='{addr}' from inspector field.");
        }

        private static string ResolveConfiguredAddress()
        {
            string env = Environment.GetEnvironmentVariable(EnvHostIp);
            return !string.IsNullOrWhiteSpace(env) ? env : PlayerPrefs.GetString(PrefKey, "");
        }

        private static string Normalize(string addr)
        {
            addr = addr.Trim();
            return addr.Contains(":") ? addr : $"{addr}:{DefaultNetPort}";
        }

        private static bool HasEnv(string name) =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

        /// <summary>Persist a host address per-machine (e.g. wire to a UI field). Empty clears it.</summary>
        public static void SetHostAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                PlayerPrefs.DeleteKey(PrefKey);
            else
                PlayerPrefs.SetString(PrefKey, address.Trim());
            PlayerPrefs.Save();
        }

        public static string GetHostAddress() => PlayerPrefs.GetString(PrefKey, "");
    }
}
