using JetBrains.Annotations;
using UnityEngine;
using System;

namespace PolymindGames.MovementSystem
{
    [Serializable]
    [UsedImplicitly]
    public class CharacterCrouchState : CharacterGroundedState
    {
        [SerializeField, Range(0f, 2f), SpaceArea]
        [Tooltip("The controllers height when crouching.")]
        private float _crouchHeight = 1f;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("How long does it take to crouch.")]
        private float _crouchCooldown = 0.3f;

        [SerializeField, SpaceArea]
        private AudioData _crouchAudio = new(null);

        [SerializeField]
        private AudioData _standUpAudio = new(null);

        private float _crouchTimer;
        
        public override MovementStateType StateType => MovementStateType.Crouch;

        protected override void OnStateInitialized()
        {
            _crouchTimer = 0f;
        }

        public override bool IsValid()
        {
            bool canCrouch =
                Time.time > _crouchTimer &&
                Motor.IsGrounded &&
                Motor.CanSetHeight(_crouchHeight);

            return canCrouch;
        }

        public override void OnEnter(MovementStateType prevStateType)
        {
            _crouchTimer = Time.time + _crouchCooldown;
            Motor.Height = _crouchHeight;
            Character.Audio.PlayClip(_crouchAudio, BodyPoint.Torso);
        }

        public override void UpdateLogic()
        {
            // Transition to an airborne state.
            if (Controller.TrySetState(MovementStateType.Airborne)) return;

            // Transition to an idle or walk state.
            if (Time.time > _crouchTimer + _crouchCooldown && (!Input.IsCrouching || Input.IsJumping || Input.IsRunning))
            {
                Input.MarkCrouchInputUsed();
                Input.MarkJumpInputUsed();
                Controller.TrySetState(Motor.SimulatedVelocity.sqrMagnitude > 0.1f ? MovementStateType.Walk : MovementStateType.Idle);
            }
        }

        public override void OnExit()
        {
            _crouchTimer = Time.time + _crouchCooldown;
            Character.Audio.PlayClip(_standUpAudio, BodyPoint.Torso);
        }
    }
}