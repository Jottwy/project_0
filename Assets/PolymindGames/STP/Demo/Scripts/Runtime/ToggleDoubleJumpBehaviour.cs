using PolymindGames.MovementSystem;
using UnityEngine;

namespace PolymindGames.Demo
{
    public sealed class ToggleDoubleJumpBehaviour : MonoBehaviour
    {
        public void ToggleDoubleJump(ICharacter character)
        {
            if (character.TryGetCC(out IMovementControllerCC movement))
            {
                var jumpState = movement.GetStateOfType<CharacterJumpState>();

                if (jumpState != null)
                    jumpState.MaxJumpsCount = jumpState.MaxJumpsCount == jumpState.DefaultJumpsCount ? 2 : jumpState.DefaultJumpsCount;
            }
        }
    }
}