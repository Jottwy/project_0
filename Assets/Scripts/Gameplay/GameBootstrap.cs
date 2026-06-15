using BackroomsSurvival.Net;
using BackroomsSurvival.UI;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    public sealed class GameBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            //EnsureComponent<ChunkRenderer>();
            EnsureComponent<EntityRenderer>();
            EnsureComponent<ItemRenderer>();
            EnsureComponent<WorldInteractor>();
            EnsureComponent<SanityEffects>();
            EnsureComponent<TeleportationVFX>();
            EnsureComponent<MinimapRenderer>();
            EnsureComponent<HUDUpdater>();
            EnsureComponent<PoiDebugHud>();
            EnsureComponent<VerticalDebugMarkerRenderer>();
            EnsureComponent<NetworkInitializer>();
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
