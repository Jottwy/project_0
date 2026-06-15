using JetBrains.Annotations;
using PolymindGames.Options;
using System;

namespace PolymindGames.MovementSystem
{
    [Serializable]
    [UsedImplicitly]
    public class CharacterWalkState : CharacterGroundedState
    {
        public override MovementStateType StateType => MovementStateType.Walk;
        public override bool IsValid() => Motor.IsGrounded && Motor.CanSetHeight(Motor.DefaultHeight);
        public override void OnEnter(MovementStateType prevStateType) => Motor.Height = Motor.DefaultHeight;

        public override void UpdateLogic()
        {
            // Transition to an idle state.
            if (Input.RawMovement.sqrMagnitude < 0.1f && Motor.SimulatedVelocity.sqrMagnitude < 0.01f
                && Controller.TrySetState(MovementStateType.Idle)) return;

            // Transition to a run state.
            if ((Input.IsRunning || InputOptions.Instance.AutoRun) && Controller.TrySetState(MovementStateType.Run)) return;

            // Transition to a crouch state.
            if (Input.IsCrouching && Controller.TrySetState(MovementStateType.Crouch)) return;

            // Transition to an airborne state.
            if (!Motor.IsGrounded && Controller.TrySetState(MovementStateType.Airborne)) return;

            // Transition to a jumping state.
            if (Input.IsJumping)
                Controller.TrySetState(MovementStateType.Jump);
        }
    }
}