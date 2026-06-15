using UnityEngine;

namespace PolymindGames
{
    [RequireCharacterComponent(typeof(IMotorCC))]
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault1)]
    public sealed class CharacterYPositionEaser : CharacterBehaviour
    {
        [SerializeField, Range(5f, 50f)]
        [Tooltip("How quickly the transform adjusts to the character's Y position.")]
        private float _groundedLerpSpeed = 20f;

        [SerializeField, Range(50f, 200f)]
        [Tooltip("How quickly the transform adjusts when the character is airborne.")]
        private float _airborneLerpSpeed = 100f;

        private Transform _cachedTransform;
        private Transform _motorTransform;
        private float _currentLerpSpeed;
        private float _targetYPosition;
        private IMotorCC _motor;

        protected override void OnBehaviourStart(ICharacter character)
        {
            _motor = character.GetCC<IMotorCC>();
            _motorTransform = _motor.transform;
            _cachedTransform = transform;

            ResetYPosition();
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _motor.Teleported += ResetYPosition;
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _motor.Teleported -= ResetYPosition;
        }

        private void LateUpdate()
        {
            float deltaTime = Time.deltaTime;
            _currentLerpSpeed = Mathf.Lerp(_currentLerpSpeed, _motor.IsGrounded ? _groundedLerpSpeed : _airborneLerpSpeed, deltaTime * 10f);
            _targetYPosition = Mathf.Lerp(_targetYPosition, _motorTransform.position.y, _currentLerpSpeed * deltaTime);
            _cachedTransform.position = _motorTransform.position.WithY(_targetYPosition);
        }

        private void ResetYPosition()
            => _targetYPosition = _motorTransform.position.y;
    }
}