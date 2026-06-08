using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Minimalist immediate-mode HUD. Reads state from <see cref="IPCClient"/> and
    /// draws a connection indicator, the local player's position/stats, and a short
    /// event feed. Uses OnGUI so it needs zero scene wiring — just add the component.
    /// Upgrade to a uGUI Canvas in a later UI phase.
    /// </summary>
    public sealed class HUD : MonoBehaviour
    {
        private static readonly Color HealthColor = new Color(0.85f, 0.20f, 0.20f);
        private static readonly Color HungerColor = new Color(0.90f, 0.55f, 0.15f);
        private static readonly Color ThirstColor = new Color(0.20f, 0.55f, 0.90f);
        private static readonly Color SanityColor = new Color(0.65f, 0.30f, 0.85f);

        private Texture2D _tex;
        private GUIStyle _label;
        private readonly List<string> _eventFeed = new List<string>();
        private const int MaxFeed = 6;

        private void Awake()
        {
            // Touch the singleton so the backend connection starts even without a player.
            _ = IPCClient.Instance;
        }

        private void Update()
        {
            // Drain events on the main thread (don't touch the queue inside OnGUI).
            while (IPCClient.Instance.Events.TryDequeue(out var ev))
            {
                _eventFeed.Add($"• {ev.eventType}");
                if (_eventFeed.Count > MaxFeed) _eventFeed.RemoveAt(0);
            }
        }

        private void OnGUI()
        {
            EnsureResources();

            var ipc = IPCClient.Instance;

            // Connection status.
            GUI.color = ipc.IsConnected ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.8f, 0.3f);
            GUI.Label(new Rect(12, 10, 500, 22),
                ipc.IsConnected ? $"● Connected  {ipc.serverAddress}:{ipc.port}" : $"○ Connecting to {ipc.serverAddress}:{ipc.port}…", _label);
            GUI.color = Color.white;

            var state = ipc.LatestState;
            if (state != null && state.localPlayer != null)
            {
                var lp = state.localPlayer;
                GUI.Label(new Rect(12, 32, 600, 22),
                    $"tick {state.tick}   pos ({lp.position.x:0.0}, {lp.position.y:0.0}, {lp.position.z:0.0})   " +
                    $"chunks {state.visibleChunks.Count}  entities {state.visibleEntities.Count}", _label);

                float y = 64f;
                DrawBar(new Rect(12, y, 220, 16), "Health", lp.stats.health, HealthColor); y += 22;
                DrawBar(new Rect(12, y, 220, 16), "Hunger", lp.stats.hunger, HungerColor); y += 22;
                DrawBar(new Rect(12, y, 220, 16), "Thirst", lp.stats.thirst, ThirstColor); y += 22;
                DrawBar(new Rect(12, y, 220, 16), "Sanity", lp.stats.sanity, SanityColor); y += 22;
            }
            else
            {
                GUI.Label(new Rect(12, 32, 600, 22), "Waiting for world state from backend…", _label);
            }

            // Event feed (bottom-left).
            for (int i = 0; i < _eventFeed.Count; i++)
            {
                GUI.Label(new Rect(12, Screen.height - 24 - (_eventFeed.Count - i) * 18, 500, 18), _eventFeed[i], _label);
            }

            DrawCrosshair();
        }

        private void DrawBar(Rect r, string label, float value01to100, Color color)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(r, _tex);

            GUI.color = color;
            var fill = new Rect(r.x, r.y, r.width * Mathf.Clamp01(value01to100 / 100f), r.height);
            GUI.DrawTexture(fill, _tex);

            GUI.color = Color.white;
            GUI.Label(new Rect(r.x + 6, r.y - 2, r.width, r.height + 4), $"{label}  {value01to100:0}", _label);
        }

        private void DrawCrosshair()
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            GUI.DrawTexture(new Rect(cx - 2f, cy - 2f, 4f, 4f), _tex);
            GUI.color = Color.white;
        }

        private void EnsureResources()
        {
            if (_tex == null)
            {
                _tex = new Texture2D(1, 1);
                _tex.SetPixel(0, 0, Color.white);
                _tex.Apply();
            }
            if (_label == null)
            {
                _label = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            }
        }

        private void OnDestroy()
        {
            if (_tex != null) Destroy(_tex);
        }
    }
}
