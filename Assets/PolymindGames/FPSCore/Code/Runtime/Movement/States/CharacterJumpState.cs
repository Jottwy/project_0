using JetBrains.Annotations;
using UnityEngine;
using System;

namespace PolymindGames.MovementSystem
{
    [Serializable]
    [UsedImplicitly]
    public class CharacterJumpState : CharacterMovementState
    {
        [SerializeField, Range(0.1f, 10f)]
        [Tooltip("The max height of a jump.")]
        private float _jumpHeight = 1f;

        [SerializeField, Range(1, 10)]
        private int _jumpsCount;

        [SerializeField, Range(0f, 1.5f)]
        [Tooltip("How often can this character jump (in seconds).")]
        private float _jumpCooldown = 0.3f;

        [SerializeField, Range(0f, 1f)]
        private float _coyoteJumpTime = 0.1f;

        [SerializeField, SpaceArea]
        private AudioData _jumpAudio = new(null);
        
        private int _jumpsCountLeft;
        private int _maxJumpsCount;
        private float _jumpTimer;

        public override MovementStateType StateType => MovementStateType.Jump;
        public override float StepCycleLength => 1f;
        public override bool ApplyGravity => false;
        public override bool SnapToGround => false;

        public int MaxJumpsCount
        {
            get => _maxJumpsCount;
            set
            {
                _maxJumpsCount = value;

                if (Motor.IsGrounded)
                    ResetJumpsCount();
            }
        }

        public int DefaultJumpsCount => _jumpsCount;

        protected override void OnStateInitialized()
        {
            Motor.GroundedChanged += OnGroundedChanged;

            _maxJumpsCount = _jumpsCount;
            _jumpTimer = -1f;
            ResetJumpsCount();
        }

        public override bool IsValid()
        {
            if (!Motor.CanSetHeight(Motor.DefaultHeight))
                return false;

            if (_jumpsCountLeft == _maxJumpsCount && !IsGrounded())
                _jumpsCountLeft--;

            return _jumpsCountLeft > 0 && _jumpTimer < Time.time;
        }

        public override void OnEnter(MovementStateType prevStateType)
        {
            Motor.Height = Motor.DefaultHeight;
            _jumpsCountLeft--;

            Input.MarkCrouchInputUsed();
            Input.MarkJumpInputUsed();

            Character.Audio.PlayClip(_jumpAudio, BodyPoint.Legs);
        }

        public override void UpdateLogic()
        {
            // Transition to airborne state.
            Controller.TrySetState(MovementStateType.Airborne);
        }

        public override Vector3 UpdateVelocity(Vector3 currentVelocity, float deltaTime)
        {
            float jumpHeight = _jumpHeight;

            if (jumpHeight > 0.1f)
            {
                float jumpSpeed = Mathf.Sqrt(2 * Motor.Gravity * jumpHeight);
                currentVelocity = new Vector3(currentVelocity.x, jumpSpeed, currentVelocity.z);
            }

            return currentVelocity;
        }

        private void OnGroundedChanged(bool grounded)
        {
            if (grounded)
            {
                _jumpTimer = Time.time + _jumpCooldown;
                ResetJumpsCount();
            }
        }

        private bool IsGrounded() => Motor.LastGroundedChangeTime + _coyoteJumpTime > Time.time || Motor.IsGrounded;
        
        private void ResetJumpsCount() => _jumpsCountLeft = _maxJumpsCount;

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying && Motor != null)
            {
                _maxJumpsCount = _jumpsCount;
                ResetJumpsCount();
            }
        }
#endif
        #endregion
    }
}