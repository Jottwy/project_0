using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    [AddComponentMenu("Polymind Games/Motion/Fall Motion")]
    [RequireCharacterComponent(typeof(IMotorCC))]
    public sealed class FallMotion : DataMotionBehaviour<GeneralMotionData>
    {
        [Title("Settings")]
        [SerializeField, Range(0f, 100f)]
        private float _fallSpeedLimit = 10f;

        private IMotorCC _motor;

        protected override void OnBehaviourStart(ICharacter character)
        {
            _motor = character.GetCC<IMotorCC>();
        }

        protected override void OnDataChanged(GeneralMotionData data)
        {
            if (Data != null)
            {
                var fallData = Data.Fall;
                PositionSpring.Adjust(fallData.PositionSpring);
                RotationSpring.Adjust(fallData.RotationSpring);
            }
        }

        public override void UpdateMotion(float deltaTime)
        {
            if (Data == null || (_motor.IsGrounded && RotationSpring.IsIdle && PositionSpring.IsIdle))
                return;

            var fallData = Data.Fall;
            
            float factor = Mathf.Max(_motor.Velocity.y, -_fallSpeedLimit);

            Vector3 posFall = new Vector3(0f, factor * fallData.PositionValue, 0f);
            Vector3 rotFall = new Vector3(factor * fallData.RotationValue, 0f, 0f);

            SetTargetPosition(posFall);
            SetTargetRotation(rotFall);
        }
    }
}