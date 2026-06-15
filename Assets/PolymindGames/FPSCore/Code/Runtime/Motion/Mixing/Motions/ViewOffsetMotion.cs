using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/Motion/View Offset Motion")]
    [RequireCharacterComponent(typeof(ILookHandlerCC))]
    public sealed class ViewOffsetMotion : MotionBehaviour
    {
        [SerializeField, Title("Interpolation")]
        private SpringSettings _positionSpring = SpringSettings.Default;

        [SerializeField]
        private SpringSettings _rotationSpring = SpringSettings.Default;

        [SerializeField, Title("Rotation Settings")]
        private Vector3 _positionOffset = new(1f, -2, 0f);

        [SerializeField]
        private Vector3 _rotationOffset = new(2f, -0.5f, -2f);

        private ILookHandlerCC _lookHandler;

        protected override void OnBehaviourStart(ICharacter character) => _lookHandler = character.GetCC<ILookHandlerCC>();
        protected override SpringSettings GetDefaultPositionSpringSettings() => _positionSpring;
        protected override SpringSettings GetDefaultRotationSpringSettings() => _rotationSpring;

        public override void UpdateMotion(float deltaTime)
        {
            if (_lookHandler == null)
                return;
            
            float angle = _lookHandler.ViewAngles.x;
            bool isValidAngle = angle > 30f;

            if (!isValidAngle && PositionSpring.IsIdle && RotationSpring.IsIdle)
                return;

            if (isValidAngle)
            {
                float angleFactor = 1f - Mathf.Min(70f - Mathf.Abs(angle), 70f) / 40f;
                Vector3 targetPosition = 0.01f * angleFactor * _positionOffset;
                Vector3 targetRotation = _rotationOffset * angleFactor;

                SetTargetPosition(targetPosition);
                SetTargetRotation(targetRotation);
            }
            else
            {
                SetTargetPosition(Vector3.zero);
                SetTargetRotation(Vector3.zero);
            }
        }
    }
}