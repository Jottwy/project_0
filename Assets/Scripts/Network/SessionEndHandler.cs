using System;
using BackroomsSurvival.UI;
using PolymindGames;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-056 — ends the session when the host goes away.
    ///
    /// The backend raises `session_ended` when the peer that left was the host. There is no host
    /// migration, so what remains is a world that cannot advance: chunk displacement is gated on
    /// the host, the STP rosters freeze on their last snapshot, and every request aimed at the
    /// host is dropped in silence. Rather than leave the player in a world that looks alive, this
    /// tears the session down and returns to the main menu.
    ///
    /// It cables together two halves that already existed and did not know about each other:
    /// NetworkInitializer.Shutdown() (kills the backend with a graceful save, but never leaves the
    /// scene) and STP's LevelManager.CloseCurrentGame (goes back to the menu, but never touches
    /// the network). CloseCurrentGame is called as public vendor API — nothing under
    /// Assets/PolymindGames/ is edited.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class SessionEndHandler : MonoBehaviour
    {
        private const string SessionEndedEvent = "session_ended";

        [SerializeField]
        [Tooltip("Menu scene to return to when the session ends. Must be in Build Settings.")]
        private string _mainMenuScene = "STP_MainMenu";

        private IPCClient _ipc;
        private bool _ending;

        private void Update()
        {
            // The IPCClient singleton is created by whichever bootstrap runs first, so this
            // subscribes on the first frame it exists rather than assuming an ordering.
            if (_ipc != null) return;
            if (!IPCClient.TryGetInstance(out var ipc)) return;

            _ipc = ipc;
            _ipc.AddEventListener(OnGameEvent);
        }

        private void OnDestroy()
        {
            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);
        }

        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev.eventType != SessionEndedEvent) return;

            // The backend keeps emitting world state until Unity kills it, and the event can be
            // delivered more than once if the disconnect is detected twice (goodbye packet AND
            // heartbeat timeout). Ending twice would kill a backend that a NEW session had
            // already launched.
            if (_ending) return;
            _ending = true;

            string reason = ReadReason(ev);
            Debug.LogWarning($"[SessionEndHandler] Session ended (reason={reason}) — returning to menu");

            // IPCClient.NotifyListeners dispatches inside `try { h(ev); } catch { }`, so anything
            // thrown from here leaves NO trace and — worse — leaves `_ending` latched, which kills
            // session-end for the rest of the process. It is not hypothetical: LevelManager's
            // CloseCurrentGame runs ThrowIfSceneDoesNotExist BEFORE its IsLoadingOrSaving check, so
            // a `_mainMenuScene` that is missing from Build Settings throws ArgumentException.
            // Catch it here (not in IPCClient, which is shared by every other listener): log it,
            // and re-arm so a later session_ended can retry instead of stranding the player.
            try
            {
                EndSession();
            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"[SessionEndHandler] EndSession threw — the session may be half torn down; " +
                    $"re-arming for the next event: {e}");
                _ending = false;
            }
        }

        /// <summary>
        /// Clears the once-per-session latch. Called by <see cref="NetworkInitializer"/> when a new
        /// session is being configured (host or join).
        ///
        /// The successful path deliberately leaves `_ending` set: the backend keeps emitting until
        /// Unity kills it, and the duplicate session_ended (goodbye packet AND heartbeat timeout)
        /// must not tear down whatever replaced that session. Nothing used to clear it again —
        /// and this component lives on the DontDestroyOnLoad NetworkInitializer object, so the
        /// latch survived the trip back to the menu and the SECOND session of a process could
        /// never end itself.
        /// </summary>
        public void ResetForNewSession()
        {
            if (!_ending) return;
            _ending = false;
            Debug.Log("[SessionEndHandler] Re-armed for a new session");
        }

        /// The event payload is the free-form object tree MsgPackReader.ReadValue produces —
        /// maps come back as Dictionary&lt;string, object&gt; — and every other consumer reads it
        /// through IPCParse. Same idiom here, rather than a hand-rolled cast that would have to
        /// be re-checked against the reader's actual output type.
        ///
        /// Public (not internal) so the EditMode suite can reach it: the compile-check builds each
        /// assembly with a `_check` suffix, so an InternalsVisibleTo would never find the right
        /// friend name — the same criterion already applied to InventoryRestorer.ParseStacks.
        public static string ReadReason(GameEventMsg ev)
        {
            var map = ev.data as System.Collections.Generic.Dictionary<string, object>;
            string reason = IPCParse.S(map, "reason");
            return string.IsNullOrEmpty(reason) ? "unknown" : reason;
        }

        /// One best-effort teardown step. Logs and swallows, so the remaining steps still run —
        /// the opposite of IPCClient's silent `catch { }`, which is what made this failure mode
        /// invisible in the first place.
        private static void Step(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SessionEndHandler] Teardown step '{what}' failed (continuing): {e}");
            }
        }

        private void EndSession()
        {
            // Steps 1-3 are independent, and the whole point of this handler is to not leave the
            // player in a dead world: one of them throwing must not skip the ones after it, least
            // of all the return to the menu. Each is logged on failure and execution continues;
            // step 4 stays unguarded on purpose, so OnGameEvent's catch re-arms the latch.
            Step("backend shutdown", () =>
            {
                // 1. Kill the backend first, while the IPC connection is still up: Shutdown() sends
                //    save_and_shutdown over it and waits for a clean exit before falling back to
                //    Kill(). Pausing reconnect before this would cost the graceful save.
                var init = NetworkInitializer.Instance;
                if (init != null)
                    init.Shutdown();
                else
                    Debug.LogWarning("[SessionEndHandler] No NetworkInitializer — backend not torn down");
            });

            Step("IPC reconnect pause", () =>
            {
                // 2. Park the IPC client. The backend's port is dead; without this the reconnect
                //    loop dials it for the rest of the process's life. NOT IPCClient.Shutdown(),
                //    which would clear the singleton and stop the thread — the client has to stay
                //    reusable for a second session (ConfigureEndpoint lifts the pause).
                if (_ipc != null)
                    _ipc.PauseReconnect();
            });

            Step("connect panel reset", () =>
            {
                // 3. Reset the connect panel's latched state. Fields only — destroying it or its
                //    GameObject would let a later ShowConnectPanel build a fresh instance whose
                //    Start() re-runs the SESSION_MODE/CONNECT_TO auto-connect, looping forever.
                var ui = FindFirstObjectByType<JoinSessionUI>();
                if (ui != null)
                    ui.ResetForNewSession();
            });

            // 4. Back to the menu, through STP's own loader so its game-state teardown runs.
            //    Skipped when we are already there (nothing to close) — the panel is enough.
            if (SceneManager.GetActiveScene().name == _mainMenuScene)
            {
                Debug.Log("[SessionEndHandler] Already in the menu scene — nothing to unload");
                _ending = false;
                return;
            }

            var level = LevelManager.Instance;
            if (level == null)
            {
                Debug.LogError("[SessionEndHandler] No LevelManager — cannot return to the menu");
                _ending = false;
                return;
            }

            if (!level.CloseCurrentGame(_mainMenuScene))
            {
                // Only fails while a load/save is already in flight. Re-arm so the next
                // session_ended (or the heartbeat-timeout one that follows a lost goodbye) can
                // retry, instead of stranding the player in the dead world.
                Debug.LogWarning("[SessionEndHandler] LevelManager busy — will retry on the next event");
                _ending = false;
            }
        }
    }
}
