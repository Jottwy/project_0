using JetBrains.Annotations;
using UnityEngine;
using System;

namespace PolymindGames.MovementSystem
{
    [Serializable]
    [UsedImplicitly]
    public class CharacterSlideState : CharacterMovementState
    {
        [SerializeField]
        [Tooltip("Sliding speed over time.")]
        private AnimationCurve _slideSpeed;

        [SerializeField, Range(0f, 100f)]
        private float _slideImpulse = 10f;

        [SerializeField, Range(0f, 100f)]
        [Tooltip("How fast will the character stop when there's no input (a high value will make the movement feel snappier).")]
        private float _slideAcceleration = 10f;

        [SerializeField, Range(0f, 2f), SpaceArea]
        [Tooltip("The controllers height when sliding.")]
        private float _slideHeight = 1f;

        [SerializeField]
        private AnimationCurve _slideEnterSpeed;

        [SerializeField, Range(0f, 25f)]
        private float _minSlideSpeed = 4f;

        [SerializeField, Range(0.1f, 10f)]
        private float _slideStopSpeed = 2f;

        [SerializeField, Range(0f, 10f), SpaceArea]
        [Tooltip("How much control does the Player input have on the slide direction.")]
        private float _inputFactor;

        [SerializeField, Range(0f, 50f)]
        private float _slopeFactor = 2f;

        [SerializeField, SpaceArea]
        private AudioData _slideAudio = new(null);

        [SerializeField, Range(0f, 2f)]
        private float _slideAudioDuration = 0.3f;
        
        private float _initialSlideImpulseMod;
        private AudioSource _slideLoopSource;
        private Vector3 _slideDirection;
        private bool _isJumpingPressed;
        private float _slideStartTime;
        
        public override MovementStateType StateType => MovementStateType.Slide;
        public override bool ApplyGravity => false;
        public override bool SnapToGround => true;
        public override float StepCycleLength => float.PositiveInfinity;
        
        protected override void OnStateInitialized()
        {
            _slideDirection = Vector3.zero;
            _initialSlideImpulseMod = 0f;
            _slideStartTime = 0f;
        }

        public override bool IsValid()
        {
            Vector3 motorVelocity = Motor.Velocity;

            bool canSlide =
                new Vector2(motorVelocity.x, motorVelocity.z).magnitude > _minSlideSpeed &&
                Motor.IsGrounded &&
                Motor.CanSetHeight(_slideHeight);

            return canSlide;
        }

        public override void OnEnter(MovementStateType prevStateType)
        {
            _slideDirection = Vector3.ClampMagnitude(Motor.SimulatedVelocity, 1f);
            _slideStartTime = Time.time;
            Motor.Height = _slideHeight;

            _initialSlideImpulseMod = _slideEnterSpeed.Evaluate(Motor.Velocity.magnitude) * _slideImpulse;

            if (prevStateType == MovementStateType.Airborne)
                _initialSlideImpulseMod *= 0.33f;

            _isJumpingPressed = false;
            Input.MarkRunInputUsed();

            _slideLoopSource = Character.Audio.StartLoop(_slideAudio, BodyPoint.Feet, _slideAudioDuration);
        }

        public override void OnExit()
        {
            Character.Audio.StopLoop(_slideLoopSource);
            _slideLoopSource = null;
        }

        public override void UpdateLogic()
        {
            // Transition to airborne state.
            if (!Motor.IsGrounded && Controller.TrySetState(MovementStateType.Airborne)) return;

            if (Motor.Velocity.magnitude < _slideStopSpeed)
            {
                // Transition to a running state.
                if (Input.IsRunning && Controller.TrySetState(MovementStateType.Run)) return;

                // Transition to a crouch state.
                if ((Motor.Velocity.sqrMagnitude < 2f || Input.RawMovement.sqrMagnitude > 0.1f) && Controller.TrySetState(MovementStateType.Crouch)) return;
            }

            if (Input.IsJumping)
                _isJumpingPressed = true;

            // Transition to a jump state.
            if (_isJumpingPressed && _slideStartTime + 0.2f < Time.time)
                Controller.TrySetState(MovementStateType.Jump);
        }

        public override Vector3 UpdateVelocity(Vector3 currentVelocity, float deltaTime)
        {
            // Calculate the sliding impulse velocity.
            Vector3 targetVelocity = _slideDirection * (_slideSpeed.Evaluate(Time.time - _slideStartTime) * _initialSlideImpulseMod);

            // Sideways movement.
            if (Mathf.Abs(Input.RawMovement.x) > 0.01f)
                targetVelocity += Vector3.ClampMagnitude(Motor.transform.TransformVector(new Vector3(Input.RawMovement.x, 0f, 0f)), 1f);

            // Combine the target velocity with the sideways movement.
            float previousMagnitude = targetVelocity.magnitude;
            targetVelocity = Vector3.ClampMagnitude(Input.ProcessedMovement * _inputFactor + targetVelocity, previousMagnitude);

            // Make sure to increase the speed when descending steep surfaces.
            float surfaceAngle = Motor.GroundSurfaceAngle;
            if (surfaceAngle > 3f)
            {
                bool isDescendingSlope = Vector3.Dot(Motor.GroundNormal, Motor.SimulatedVelocity) > 0f;
                float slope = Mathf.Min(surfaceAngle, Motor.SlopeLimit) / Motor.SlopeLimit;
                Vector3 slopeDirection = Motor.GroundNormal;
                slopeDirection.y = 0f;

                // Increase the sliding force when going down slopes.
                if (isDescendingSlope)
                    currentVelocity += slopeDirection * (slope * _slopeFactor * deltaTime * 10f);
            }

            // Get the velocity mod.
            float velocityMod = Controller.SpeedModifier.EvaluateValue() * Motor.GetSlopeSpeedMultiplier();

            // Get the acceleration mod.
            float acceleration = targetVelocity.sqrMagnitude > 0.1f ? Controller.AccelerationModifier.EvaluateValue() : Controller.DecelerationModifier.EvaluateValue();

            // Lower velocity if the motor has collided with anything.
            if (Motor.CollisionFlags.Has(CollisionFlags.CollidedSides))
                velocityMod *= 0.5f;

            // Finally multiply the target velocity with the velocity modifier.
            targetVelocity *= velocityMod;

            currentVelocity = Vector3.Lerp(currentVelocity, targetVelocity, deltaTime * _slideAcceleration * acceleration);

            return currentVelocity;
        }
    }
}