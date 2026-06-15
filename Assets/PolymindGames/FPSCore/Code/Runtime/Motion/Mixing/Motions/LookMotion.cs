using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/Motion/Look Motion")]
    [RequireCharacterComponent(typeof(ILookHandlerCC))]
    public sealed class LookMotion : DataMotionBehaviour<GeneralMotionData>
    {
        private ILookHandlerCC _lookHandler;

        protected override void OnBehaviourStart(ICharacter character)
        {
            _lookHandler = character.GetCC<ILookHandlerCC>();
        }

        protected override void OnDataChanged(GeneralMotionData data)
        {
            if (data != null)
            {
                var lookData = data.Look;
                PositionSpring.Adjust(lookData.PositionSpring);
                RotationSpring.Adjust(lookData.RotationSpring);
            }
        }

        public override void UpdateMotion(float deltaTime)
        {
            if (Data == null)
                return;

            var lookData = Data.Look;
            
            // Calculate the look input.
            Vector2 lookInput = _lookHandler.LookInput;
            lookInput = Vector2.ClampMagnitude(lookInput, lookData.MaxSwayLength);

            Vector3 posSway = new(
                lookInput.y * lookData.PositionSway.x,
                lookInput.x * lookData.PositionSway.y);

            Vector3 rotSway = new(
                lookInput.x * lookData.RotationSway.x,
                lookInput.y * -lookData.RotationSway.y,
                lookInput.y * -lookData.RotationSway.z);

            SetTargetPosition(posSway);
            SetTargetRotation(rotSway);
        }
    }
}