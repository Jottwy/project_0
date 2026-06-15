using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireCharacterComponent(typeof(IWieldablesControllerCC), typeof(IMovementControllerCC), typeof(IMotorCC))]
    public sealed class MovementReloadBlocker : WieldableActionBlocker
    {
        [SerializeField, LeftToggle, Title("Settings")]
        [Tooltip("Toggle to block movement while reloading.")]
        private bool _blockRunning = true;

        [SerializeField, LeftToggle]
        [Tooltip("Toggle to block jumping while reloading.")]
        private bool _blockJumping = false;

        [SerializeField, LeftToggle]
        [Tooltip("Toggle to allow reloading while running.")]
        private bool _reloadWhileRunning = true;

        [SerializeField, LeftToggle]
        [Tooltip("Toggle to allow reloading while airborne.")]
        private bool _reloadWhileAirborne = false;

        private IReloadInputHandler _reloadInputHandler;
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
            if (wieldable is IReloadInputHandler input)
            {
                _reloadInputHandler = input;
                return input.ReloadBlocker;
            }

            return null;
        }

        protected override bool IsActionValid()
        {
            BlockMovement(_reloadInputHandler.IsReloading);

            bool isValid = (_reloadWhileAirborne || _motor.IsGrounded) &&
                _movement.ActiveState != MovementStateType.Run || _reloadWhileRunning;

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