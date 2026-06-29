using BackroomsSurvival.Net;
using BackroomsSurvival.UI;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    public sealed class GameBootstrap : MonoBehaviour
    {
        [Header("Debug")]
        [Tooltip("Spawn the robapieles (phantom peer) on the host for play-testing. Forwarded to the runtime-added NetworkInitializer (which injects DEBUG_SPAWN_PHANTOM=1).")]
        [SerializeField] private bool _debugSpawnPhantom = false;

        private void Awake()
        {
            //EnsureComponent<ChunkRenderer>();
            //EnsureComponent<EntityRenderer>();
            EnsureComponent<ItemRenderer>();
            EnsureComponent<WorldInteractor>();
            //EnsureComponent<SanityEffects>();
            EnsureComponent<TeleportationVFX>();
            EnsureComponent<MinimapRenderer>();
            EnsureComponent<HUDUpdater>();
            EnsureComponent<PoiDebugHud>();
            EnsureComponent<VerticalDebugMarkerRenderer>();
            EnsureComponent<NetworkInitializer>();
            // EnsureComponent added it to THIS GameObject (it's in no scene/prefab), so forward
            // the inspector toggle before NetworkInitializer launches the backend (in Start).
            var ni = GetComponent<NetworkInitializer>();
            if (ni != null) ni.debugSpawnPhantom = _debugSpawnPhantom;
            // Gate player spawn on the IPC connection (10 s offline fallback lives in
            // GameMode). Restores the always-ready default on teardown, so non-networked
            // scenes are unaffected.
            EnsureComponent<GameBootGateBinder>();
            EnsureComponent<RemotePlayerManager>();
            EnsureComponent<JoinSessionUI>();
        }

        private void EnsureComponent<T>() where T : Component
        {
            if (FindFirstObjectByType<T>() == null)
                gameObject.AddComponent<T>();
        }
    }
}
