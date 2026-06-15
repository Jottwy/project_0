using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireCharacterComponent(typeof(IWieldablesControllerCC), typeof(IMovementControllerCC), typeof(IMotorCC))]
    public sealed class MovementAimBlocker : WieldableActionBlocker
    {
        [SerializeField, LeftToggle, Title("Settings")]
        [Tooltip("Toggle to block movement while aiming.")]
        private bool _blockRunning = true;

        [SerializeField, LeftToggle]
        [Tooltip("Toggle to block jumping while aiming.")]
        private bool _blockJumping = false;

        [SerializeField, LeftToggle]
        [Tooltip("Toggle to allow aiming while running.")]
        private bool _aimWhileRunning = true;

        [SerializeField, LeftToggle]
        [Tooltip("Toggle to allow aiming while airborne.")]
        private bool _aimWhileAirborne = false;

        private IAimInputHandler _aimInputHandler;
        private IMovementControllerCC _movement;
        private bool _statesBlocked;
        private IMotorCC _motor;

        protected override void OnBehaviourStart(ICharacter character)
        {
            _motor = character.GetCC<IMotorCC>();
            _movement = character.GetCC<IMovementControllerCC>();
        }

        protected override ActionBlockHandler GetBlockHandler(IWieldable wieldable)
        {
            BlockMovement(false);
            if (wieldable is IAimInputHandler input)
            {
                _aimInputHandler = input;
                return input.AimBlocker;
            }

            return null;
        }

        protected override bool IsActionValid()
        {
            BlockMovement(_aimInputHandler.IsAiming);

            bool isValid = (_aimWhileAirborne || _motor.IsGrounded) &&
                           (_aimWhileRunning || _movement.ActiveState != MovementStateType.Run);

            return isValid;
        }

        private void BlockMovement(bool block)
        {
            if (_statesBlocked == block)
                return;

            if (block)
            {
                if (_blockRunning) _movement.AddStateBlocker(this, MovementStateType.Run);
                if (_blockJumping) _movement.AddStateBlocker(this, MovementStateType.Jump);
            }
            else
            {
                if (_blockRunning) _movement.RemoveStateBlocker(this, MovementStateType.Run);
                if (_blockJumping) _movement.RemoveStateBlocker(this, MovementStateType.Jump);
            }

            _statesBlocked = block;
        }
    }
}