using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Handles the camera's recoil motion and recovery based on player input.
    /// </summary>
    public sealed class CharacterRecoilMotion : IMixedMotion
    {
        private readonly ILookHandlerCC _lookHandler;
        private readonly Spring2D _spring;
        
        private SpringSettings _recoverySpringSettings;
        private SpringSettings _recoilSpringSettings;
        private Vector2 _controlledRotationOffset;
        private Vector2 _currentRotation;
        private Vector2 _targetRotation;
        private bool _isRecoveryActive;
        private bool _isRecoilActive;

        public CharacterRecoilMotion(ICharacter character)
        {
            _recoilSpringSettings = SpringSettings.Default;
            _recoverySpringSettings = SpringSettings.Default;
            _spring = new Spring2D(SpringSettings.Default);
            _lookHandler = character.GetCC<ILookHandlerCC>();
        }

        /// <summary>
        /// Activates recoil motion with the given recoil amount.
        /// </summary>
        public void AddRecoil(Vector2 recoilAmount)
        {
            if (!_isRecoilActive)
                _lookHandler.SetAdditiveLookInput(CalculateRecoil);

            _spring.Adjust(_recoilSpringSettings);

            _targetRotation = _isRecoveryActive ? _currentRotation + recoilAmount : _targetRotation + recoilAmount;
            _spring.SetTargetValue(_targetRotation);

            _isRecoveryActive = false;
            _isRecoilActive = true;
        }

        /// <summary>
        /// Sets the spring settings for recoil and recovery.
        /// </summary>
        public void SetRecoilSprings(SpringSettings recoilSpringSettings, SpringSettings recoverySpringSettings)
        {
            _recoilSpringSettings = recoilSpringSettings;
            _recoverySpringSettings = recoverySpringSettings;
            _spring.Adjust(_isRecoveryActive ? recoverySpringSettings : recoilSpringSettings);
        }

        /// <summary>
        /// Calculates the recoil value, handling recoil and recovery.
        /// </summary>
        private Vector2 CalculateRecoil()
        {
            if (!_isRecoveryActive)
            {
                HandleRecoil();
            }

            if (_isRecoveryActive && Mathf.Abs(_spring.Acceleration.x) < 0.01f)
            {
                StopRecoil();
            }

            _currentRotation = _spring.Evaluate(Time.deltaTime);
            return _currentRotation;
        }

        /// <summary>
        /// Handles the recoil behavior before transitioning to recovery.
        /// </summary>
        private void HandleRecoil()
        {
            Vector2 lookInput = _lookHandler.LookDelta;

            if (lookInput.x > 0f)
            {
                _controlledRotationOffset.x += lookInput.x;
            }

            _controlledRotationOffset.y += lookInput.y;

            if (HasReachedTarget(_currentRotation, _targetRotation))
            {
                if (_controlledRotationOffset.x > Mathf.Abs(_targetRotation.x))
                {
                    StopRecoil();
                }
                else
                {
                    _isRecoveryActive = true;
                    StartRecoilRecovery();
                }
            }
        }

        /// <summary>
        /// Starts the recovery phase after recoil.
        /// </summary>
        private void StartRecoilRecovery()
        {
            Vector2 adjustedTarget = new Vector2(
                Mathf.Clamp(-_controlledRotationOffset.x, _targetRotation.x, 0f),
                Mathf.Clamp(-_controlledRotationOffset.y, -Mathf.Abs(_targetRotation.y / 2f), Mathf.Abs(_targetRotation.y / 2f))
            );

            _spring.Adjust(_recoverySpringSettings);
            _spring.SetTargetValue(adjustedTarget);
        }

        /// <summary>
        /// Stops the recoil and resets values.
        /// </summary>
        private void StopRecoil()
        {
            _targetRotation = Vector2.zero;
            _controlledRotationOffset = Vector2.zero;
            _isRecoveryActive = false;
            _isRecoilActive = false;

            _spring.Reset();
            _lookHandler.SetAdditiveLookInput(null);
        }

        /// <summary>
        /// Determines if the recoil has reached the target rotation.
        /// </summary>
        private static bool HasReachedTarget(Vector2 current, Vector2 target)
        {
            const float Tolerance = 0.005f;
            return Mathf.Abs(current.x + Tolerance * Mathf.Sign(current.x)) > Mathf.Abs(target.x) &&
                   Mathf.Abs(current.y + Tolerance * Mathf.Sign(current.y)) > Mathf.Abs(target.y);
        }

        #region Unused Interface Implementation
        public float Multiplier { get; set; } = 1f;
        public void UpdateMotion(float deltaTime) { }
        public Vector3 GetPosition(float deltaTime) => Vector3.zero;
        public Quaternion GetRotation(float deltaTime) => Quaternion.identity;
        #endregion
    }
}