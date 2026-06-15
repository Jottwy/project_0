using PolymindGames.WorldManagement;
using PolymindGames.UserInterface;
using PolymindGames.SaveSystem;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// A game mode for solo survival gameplay, managing player spawn and respawn behaviors.
    /// Implements saving functionality for persistent components.
    /// </summary>
    [SelectionBase]
    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public class SoloSurvivalGameMode : GameMode, ISaveableComponent
    {
        private bool _firstSpawn = true;

        /// <inheritdoc/>
        protected override void OnPlayerInitialized(Player player, PlayerUI playerUI)
        {
            ResetPlayerName(player);
            
            if (_firstSpawn)
                SetPlayerPosition(player);

            player.HealthManager.Respawn += OnPlayerRespawn;
        }
        
        private void ResetPlayerName(Player player)
        {
            if (string.IsNullOrEmpty(player.Name))
                player.Name = PlayerNameManager.GetPlayerName();
        }

        private void SetPlayerPosition(HumanCharacter player)
        {
            // Check if a "sleep position" is available and use it as the spawn position.
            if (player.TryGetCC(out ISleepControllerCC sleep) && sleep.LastSleepPosition != Vector3.zero)
            {
                player.SetPositionAndRotation(sleep.LastSleepPosition, sleep.LastSleepRotation);
            }
            else
            {
                var (position, rotation) = GetRandomSpawnPoint(_firstSpawn);
                player.SetPositionAndRotation(position, rotation);
            }

            // Ensure this logic is applied only on the first spawn.
            _firstSpawn = false;
        }

        /// <summary>
        /// Handles player respawn by resetting the player’s position to a designated spawn point.
        /// </summary>
        private void OnPlayerRespawn() => SetPlayerPosition(LocalPlayer);

        #region Save & Load
        void ISaveableComponent.LoadMembers(object members) => _firstSpawn = false;
        object ISaveableComponent.SaveMembers() => null;
        #endregion
    }
}