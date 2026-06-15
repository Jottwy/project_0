using System.Text;
using BackroomsSurvival.Net;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Toggleable on-screen network debug HUD (F3). Read-only and removable: delete this
    /// file (or the whole _Migration/STPIntegration folder) and it is gone. Self-bootstraps
    /// via RuntimeInitializeOnLoadMethod, so no scene wiring is needed.
    ///
    /// Surfaces exactly what is needed to diagnose "neither machine sees the other":
    ///   * role (host/client) + effective role,
    ///   * self NET_ID and the remote-player owner ids reported by the backend,
    ///   * IPC connection state + transport endpoints (IPC addr:port, local UDP NET_PORT),
    ///   * CONNECT_TO target (the smoking gun: localhost vs the host's LAN IP),
    ///   * backend peer count (world_state.remote_players) vs avatars actually rendered.
    /// </summary>
    public sealed class NetworkDebugHud : MonoBehaviour
    {
        private static NetworkDebugHud _instance;

        private bool _visible = true;
        private GUIStyle _style;
        private Texture2D _bg;
        private readonly StringBuilder _sb = new StringBuilder(640);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[NetworkDebugHud]");
            _instance = go.AddComponent<NetworkDebugHud>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (TogglePressed())
                _visible = !_visible;
        }

        private static bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.f3Key.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.F3);
#endif
        }

        private void OnGUI()
        {
            if (!_visible)
                return;

            EnsureStyle();

            _sb.Clear();
            BuildReport(_sb);
            string text = _sb.ToString();

            Vector2 size = _style.CalcSize(new GUIContent(text));
            var box = new Rect(10f, 10f, size.x + 20f, size.y + 16f);
            GUI.DrawTexture(box, _bg);
            GUI.Label(new Rect(box.x + 10f, box.y + 8f, size.x, size.y), text, _style);
        }

        private void BuildReport(StringBuilder sb)
        {
            sb.AppendLine("== NET DEBUG (F3 to hide) ==");

            var init = NetworkInitializer.Instance;
            bool hasIpc = IPCClient.TryGetInstance(out var ipc);
            bool connected = hasIpc && ipc.IsConnected;

            if (init != null)
            {
                sb.Append("role: ").Append(init.CurrentRole)
                  .Append("  (effective: ").Append(init.LastEffectiveRole).AppendLine(")");
                sb.Append("self NET_ID: ").AppendLine(init.LastSelectedNetId.ToString());
                sb.Append("IPC: ").Append(init.LastSelectedIpcAddress).Append(':').Append(init.LastSelectedIpcPort)
                  .Append("   connected: ").AppendLine(connected ? "YES" : "no");
                sb.Append("UDP NET_PORT (local): ").AppendLine(init.LastSelectedNetPort.ToString());
                sb.Append("CONNECT_TO: ")
                  .AppendLine(string.IsNullOrEmpty(init.LastConnectTo) || init.LastConnectTo == "<none>"
                      ? "<none / acting as host>"
                      : init.LastConnectTo);
                sb.Append("backend proc: ").Append(init.HasBackendProcess ? "alive" : "none")
                  .Append("   status: ").AppendLine(init.StatusMessage);
            }
            else
            {
                sb.AppendLine("NetworkInitializer: <none yet>");
                sb.Append("IPC connected: ").AppendLine(connected ? "YES" : "no");
            }

            // Backend-reported peers = remote players in the latest world state snapshot.
            int remoteCount = 0;
            string remoteList = "-";
            if (hasIpc && ipc.LatestState != null && ipc.LatestState.remotePlayers != null)
            {
                var rps = ipc.LatestState.remotePlayers;
                remoteCount = rps.Count;
                if (remoteCount > 0)
                {
                    var ids = new StringBuilder();
                    for (int i = 0; i < rps.Count; i++)
                    {
                        if (i > 0) ids.Append(", ");
                        ids.Append('#').Append(rps[i].id);
                        if (!string.IsNullOrEmpty(rps[i].name))
                            ids.Append('(').Append(rps[i].name).Append(')');
                        Vector3 p = rps[i].position;
                        ids.Append(" @(").Append(p.x.ToString("F1")).Append(',')
                           .Append(p.y.ToString("F1")).Append(',').Append(p.z.ToString("F1")).Append(')');
                    }
                    remoteList = ids.ToString();
                }
            }
            sb.Append("peers (world remote_players): ").AppendLine(remoteCount.ToString());
            sb.Append("   owner ids: ").AppendLine(remoteList);

            var rpm = FindFirstObjectByType<RemotePlayerManager>();
            sb.Append("rendered avatars: ")
              .AppendLine(rpm != null ? rpm.ActiveCount.ToString() : "<no RemotePlayerManager>");

            AppendLocalSend(sb);
        }

        // LOCAL SEND: the pose THIS machine reports for its own player, mirrored read-only from
        // the single active sender (PlayerPoseTransmitter). The transmitter reads the live
        // CharacterControllerMotor.transform.position and sends at 20 Hz, skipping entirely while
        // no motor exists — so IsSending reflects whether anything is actually on the wire and
        // LastSent is the exact pose that went out. Read-only mirror: does not touch the send path.
        private static void AppendLocalSend(StringBuilder sb)
        {
            if (PlayerPoseTransmitter.IsSending)
            {
                Vector3 sp = PlayerPoseTransmitter.LastSent;
                sb.Append("LOCAL SEND: (")
                  .Append(sp.x.ToString("F1")).Append(',')
                  .Append(sp.y.ToString("F1")).Append(',')
                  .Append(sp.z.ToString("F1")).Append(')')
                  .AppendLine("  [sending via PlayerPoseTransmitter @20Hz]");
            }
            else
            {
                sb.AppendLine("LOCAL SEND: <no motor — not transmitting>");
            }
        }

        private void EnsureStyle()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    richText = false,
                    wordWrap = false,
                    alignment = TextAnchor.UpperLeft
                };
                _style.normal.textColor = Color.white;
            }

            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
                _bg.Apply();
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
            if (_bg != null)
                Destroy(_bg);
        }
    }
}
