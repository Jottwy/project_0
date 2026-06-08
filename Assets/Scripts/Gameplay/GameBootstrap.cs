using BackroomsSurvival.Net;
using BackroomsSurvival.UI;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    public sealed class GameBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            EnsureComponent<ChunkRenderer>();
            EnsureComponent<EntityRenderer>();
            EnsureComponent<ItemRenderer>();
            EnsureComponent<SanityEffects>();
            EnsureComponent<TeleportationVFX>();
            EnsureComponent<MinimapRenderer>();
            EnsureComponent<HUDUpdater>();
            EnsureComponent<NetworkInitializer>();
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
