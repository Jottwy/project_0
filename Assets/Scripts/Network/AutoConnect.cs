using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// First-boot auto-host: skips the JoinSession UI by spawning a NetworkInitializer
    /// and starting a host session on scene load (launches the Rust backend and connects
    /// the IPC client). Sets <see cref="GameBootGate"/> ready so GameMode spawns the
    /// player immediately instead of waiting for the connection.
    ///
    /// Use this OR <c>GameBootGateBinder</c> in a scene, not both: AutoConnect forces the
    /// gate ready (spawn-now + connect-in-background), the binder makes GameMode wait.
    /// </summary>
    public sealed class AutoConnect : MonoBehaviour
    {
        [SerializeField] private string _host = "127.0.0.1";
        [SerializeField] private int _port = 7777;

        private void Awake()
        {
            // Skip GameMode's connection wait — we drive the connection here.
            GameBootGate.IsReady = () => true;
        }

        private void Start()
        {
            // There is NO StartAsHost(host, port) overload. Signatures are:
            //   StartAsHost(string playerName, int worldSeed = 42)
            //   StartAsHost(string playerName, int hostListenPort, int worldSeed = 42)
            // The IPC endpoint = NetworkInitializer.ipcPort + the IPC client address
            // (127.0.0.1 by default, or the IPC_ADDR env). So map _port onto ipcPort and
            // host with a single-arg call — passing _port as hostListenPort would set the
            // P2P NET_PORT to 7777 and collide with the IPC port.
            if (!string.IsNullOrWhiteSpace(_host) && _host != "127.0.0.1")
                System.Environment.SetEnvironmentVariable("IPC_ADDR", $"{_host}:{_port}");

            var init = new GameObject("NetworkInitializer").AddComponent<NetworkInitializer>();
            init.ipcPort = _port;
            init.StartAsHost("AutoHost");
        }
    }
}
