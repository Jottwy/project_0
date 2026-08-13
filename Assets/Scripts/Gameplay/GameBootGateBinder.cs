using BackroomsSurvival.Net;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Feeds PolymindGames' <see cref="GameBootGate"/> from the IPC connection state
    /// so <c>GameMode</c> delays the player spawn until the Rust backend is connected.
    /// No offline fallback: GameMode waits indefinitely (a spawn with no backend is a
    /// player in an empty world — worse than waiting with the Host/Join UI up).
    ///
    /// Scene-scoped: place it in scenes that use the networked flow. Standalone STP
    /// scenes omit it and keep the default always-ready gate. Awake runs before any
    /// Start, so the gate is set before GameMode's spawn coroutine checks it; the
    /// default is restored on teardown so unloading this scene doesn't gate others.
    /// </summary>
    public sealed class GameBootGateBinder : MonoBehaviour
    {
        private void Awake()
        {
            GameBootGate.IsReady = () => IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected;
        }

        private void OnDestroy()
        {
            GameBootGate.IsReady = () => true;
        }
    }
}
