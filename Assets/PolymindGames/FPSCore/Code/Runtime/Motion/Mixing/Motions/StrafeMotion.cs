using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/Motion/Strafe Motion")]
    [RequireCharacterComponent(typeof(IMotorCC))]
    public sealed class StrafeMotion : DataMotionBehaviour<GeneralMotionData>
    {
        private IMotorCC _motor;

        protected override void OnBehaviourStart(ICharacter character)
        {
            _motor = character.GetCC<IMotorCC>();
        }

        protected override void OnDataChanged(GeneralMotionData data)
        {
            if (data != null)
            {
                var strafeData = data.Strafe;
                PositionSpring.Adjust(strafeData.PositionSpring);
                RotationSpring.Adjust(strafeData.RotationSpring);
            }
        }

        public override void UpdateMotion(float deltaTime)
        {
            if (Data == null || PositionSpring.IsIdle && RotationSpring.IsIdle && _motor.Velocity == Vector3.zero)
                return;
            
            var strafeData = Data.Strafe;

            // Calculate the strafe input.
            Vector3 strafeInput = transform.InverseTransformVector(_motor.Velocity);
            strafeInput = Vector3.ClampMagnitude(strafeInput, strafeData.MaxSwayLength);

            // Calculate the strafe position sway.
            Vector3 posSway = new()
            {
                x = strafeInput.x * strafeData.PositionSway.x,
                y = -Mathf.Abs(strafeInput.x * strafeData.PositionSway.y),
                z = -strafeInput.z * strafeData.PositionSway.z
            };

            // Calculate the strafe rotation sway.
            Vector3 rotSway = new()
            {
                x = strafeInput.z * strafeData.RotationSway.x,
                y = -strafeInput.x * strafeData.RotationSway.y,
                z = strafeInput.x * strafeData.RotationSway.z
            };

            SetTargetPosition(posSway);
            SetTargetRotation(rotSway);
        }
    }
}