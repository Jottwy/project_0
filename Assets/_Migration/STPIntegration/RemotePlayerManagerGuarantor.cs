using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Safety net that guarantees exactly one <see cref="RemotePlayerManager"/> is alive in
    /// the gameplay scene once the IPC connection is up — even if GameBootstrap never created
    /// one.
    ///
    /// Why this exists: remote avatars are produced only by RemotePlayerManager, which is
    /// normally added by GameBootstrap on a single scene-baked GameObject (originally the
    /// "__NetworkBoot" object created by NetworkFlowSetup, now the scene's "GameManager").
    /// If that object is missing, replaced, or its Awake doesn't take effect at runtime,
    /// there is NO recovery: the player sees zero avatars and F3 reads
    /// "rendered avatars: &lt;no RemotePlayerManager&gt;". Unlike NetworkDebugHud,
    /// RemotePlayerManager has no self-bootstrap of its own; this adds one.
    ///
    /// Self-bootstraps via RuntimeInitializeOnLoadMethod (no scene wiring) and is fully
    /// removable: delete this file or the whole _Migration/STPIntegration folder and it is
    /// gone. Idempotent: it only spawns a manager when none exists, so when GameBootstrap
    /// works this component does nothing (no duplicate managers, no duplicate avatars).
    /// </summary>
    public sealed class RemotePlayerManagerGuarantor : MonoBehaviour
    {
        // Scene where remote avatars must render. Matches NetworkMenuBootstrap._gameplayScene
        // so the manager is never spawned over the menu.
        private const string GameplayScene = "STP_Showcase";

        // Once a manager exists we stop polling every frame and only re-check at this cadence
        // (cheap self-heal in case the manager is later destroyed).
        private const float SatisfiedRecheckInterval = 1f;

        private static RemotePlayerManagerGuarantor _instance;
        private bool _satisfied;
        private float _nextRecheck;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[RemotePlayerManagerGuarantor]");
            _instance = go.AddComponent<RemotePlayerManagerGuarantor>();
            DontDestroyOnLoad(go);
        }

        private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
        private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        // A fresh scene load (e.g. LevelManager.CreateGame) can drop a scene-scoped
        // RemotePlayerManager, so re-arm the per-frame check for the new scene.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => _satisfied = false;

        private void Update()
        {
            // While satisfied, poll cheaply (self-heal); while unsatisfied, check every frame
            // so the manager is up on the very first frame after connecting.
            if (_satisfied)
            {
                if (Time.unscaledTime < _nextRecheck)
                    return;
                _nextRecheck = Time.unscaledTime + SatisfiedRecheckInterval;
            }

            if (FindFirstObjectByType<RemotePlayerManager>() != null)
            {
                _satisfied = true;
                return;
            }

            _satisfied = false;

            // Only act in the gameplay scene, so we never spawn avatars over the menu.
            if (SceneManager.GetActiveScene().name != GameplayScene)
                return;

            // Wait for the backend connection; before that there is no world state to render
            // and a RemotePlayerManager would just idle.
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            var go = new GameObject("RemotePlayerManager (guarantor)");
            var rpm = go.AddComponent<RemotePlayerManager>();
            // Assign the avatar prefab in the SAME frame, BEFORE RemotePlayerManager's first
            // Update subscribes and the first world_state spawns an avatar. Otherwise the first
            // remote spawns as the default capsule, snapped (RemotePlayerManager.cs:103) to an
            // early/origin pose and never re-created.
            rpm.remotePlayerPrefab = RemoteAvatarProvider.EnsureTemplate();
            _satisfied = true;

            Debug.LogWarning(
                "[RemotePlayerManagerGuarantor] RemotePlayerManager was missing in '" +
                GameplayScene + "' after IPC connect; created one as a fallback. " +
                "GameBootstrap did not provide it — check the Console for an Awake exception " +
                "or a missing __NetworkBoot/GameManager boot object in the scene.");
        }
    }
}
