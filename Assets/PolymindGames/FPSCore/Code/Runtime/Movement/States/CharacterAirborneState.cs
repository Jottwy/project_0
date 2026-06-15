using JetBrains.Annotations;
using UnityEngine;
using System;
using UnityEngine.Serialization;

namespace PolymindGames.MovementSystem
{
    [Serializable]
    [UsedImplicitly]
    public class CharacterAirborneState : CharacterMovementState
    {
        [SerializeField, Range(1f, 1000f)]
        private float _maxDownwardSpeed = 30f;

        [SerializeField, Range(0f, 100f)]
        private float _maxUpwardSpeed = 30f;

        [SerializeField, Range(0f, 10f)]
        private float _minAirSpeed = 3f;

        [SerializeField, Range(0f, 10f)]
        private float _inputInfluenceAir = 2f;

        [SerializeField, Range(0.1f, 10f)]
        private float _inputResponsivenessAir = 4f;

        [SerializeField, Range(0f, 5f)]
        private float _landVelocityMod = 0.8f;

        [SerializeField, Range(0f, 10f)]
        private float _airMomentumDecayRate = 2f;

        private bool _wasGroundedLastFrame;
        private Vector3 _originalVelocity;
        private Vector3 _currentInput;
        private float _maxSpeed;

        public override MovementStateType StateType => MovementStateType.Airborne;
        public override float StepCycleLength => 1f;
        public override bool ApplyGravity => true;
        public override bool SnapToGround => false;

        public override bool IsValid() => !Motor.IsGrounded;

        public override void OnEnter(MovementStateType prevStateType)
        {
            _maxSpeed = Mathf.Max(Motor.SimulatedVelocity.GetHorizontal().magnitude, _minAirSpeed);
            _originalVelocity = Motor.SimulatedVelocity.GetHorizontal();
            _currentInput = Vector3.zero;
            _wasGroundedLastFrame = true;
        }

        public override void UpdateLogic()
        {
            if (Input.IsJumping && Controller.TrySetState(MovementStateType.Jump)) return;

            if (Motor.IsGrounded)
            {
                if (Input.IsRunning && Controller.TrySetState(MovementStateType.Run)) return;
                if (Input.IsCrouching && Controller.TrySetState(MovementStateType.Slide)) return;
                if (Controller.TrySetState(MovementStateType.Idle)) return;
                if (Controller.TrySetState(MovementStateType.Walk)) return;
            }

            if (Input.RawMovement.sqrMagnitude < 0.01f)
            {
                Input.MarkCrouchInputUsed();
            }

            _wasGroundedLastFrame = Motor.IsGrounded;
        }

        public override Vector3 UpdateVelocity(Vector3 currentVelocity, float deltaTime)
        {
            // Smooth directional input
            _currentInput = Vector3.Lerp(
                _currentInput,
                _inputInfluenceAir * Input.ProcessedMovement,
                deltaTime * _inputResponsivenessAir
            );

            // Decay momentum from takeoff
            _originalVelocity = Vector3.Lerp(
                _originalVelocity,
                Vector3.zero,
                deltaTime * _airMomentumDecayRate
            );

            var horizVelocity = _currentInput + _originalVelocity;
            horizVelocity = Vector3.ClampMagnitude(horizVelocity, _maxSpeed);

            // Clamp vertical speed
            currentVelocity.y = Mathf.Clamp(currentVelocity.y, -_maxDownwardSpeed, _maxUpwardSpeed);

            // Ceiling bounce logic
            if (Motor.CollisionFlags.Has(CollisionFlags.CollidedAbove) && currentVelocity.y > 0.1f)
            {
                currentVelocity.y = Mathf.Min(0f, -currentVelocity.y * 0.5f);
            }

            // Apply landing modifier only on actual landing transition
            if (Motor.IsGrounded && !_wasGroundedLastFrame)
            {
                horizVelocity *= _landVelocityMod;
                currentVelocity.y = -1f;
            }

            return new Vector3(horizVelocity.x, currentVelocity.y, horizVelocity.z);
        }
    }
}