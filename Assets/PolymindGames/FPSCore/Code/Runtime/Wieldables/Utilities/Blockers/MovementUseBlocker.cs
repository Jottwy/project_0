using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireCharacterComponent(typeof(IWieldablesControllerCC), typeof(IMovementControllerCC), typeof(IMotorCC))]
    public sealed class MovementUseBlocker : WieldableActionBlocker
    {
        [SerializeField, Title("Settings")]
        [Tooltip("Toggle to allow using the wieldable while airborne.")]
        private bool _useWhileAirborne = true;

        [SerializeField]
        [Tooltip("Toggle to allow using the wieldable while running.")]
        private bool _useWhileRunning = false;

        private IMovementControllerCC _movement;
        private IMotorCC _motor;

        protected override void OnBehaviourStart(ICharacter character)
        {
            _movement = character.GetCC<IMovementControllerCC>();
            _motor = character.GetCC<IMotorCC>();
        }

        protected override ActionBlockHandler GetBlockHandler(IWieldable wieldable)
        {
            if (wieldable is IUseInputHandler input)
                return input.UseBlocker;

            return null;
        }

        protected override bool IsActionValid()
        {
            bool isValid = (_useWhileAirborne || _motor.IsGrounded) &&
                           (_useWhileRunning || _movement.ActiveState != MovementStateType.Run);

            return isValid;
        }
    }
}