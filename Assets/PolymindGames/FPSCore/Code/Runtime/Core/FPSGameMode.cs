using PolymindGames.UserInterface;
using PolymindGames.SaveSystem;
using UnityEngine;

namespace PolymindGames
{
    [SelectionBase]
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public class FPSGameMode : GameMode, ISaveableComponent
    {
        private bool _firstSpawn = true;

        protected override void OnPlayerInitialized(Player player, PlayerUI playerUI)
        {
            if (_firstSpawn)
                SetPlayerPosition(player);

            player.HealthManager.Respawn += OnPlayerRespawn;
        }

        private void SetPlayerPosition(HumanCharacter player)
        {
            var (position, rotation) = GetRandomSpawnPoint(_firstSpawn);
            player.SetPositionAndRotation(position, rotation);
        }

        private void OnPlayerRespawn() => SetPlayerPosition(LocalPlayer);

        #region Save & Load
        void ISaveableComponent.LoadMembers(object members) => _firstSpawn = false;
        object ISaveableComponent.SaveMembers() => null;
        #endregion
    }
}
