using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/Motion/Land Motion")]
    [RequireCharacterComponent(typeof(IMotorCC))]
    public sealed class LandMotion : DataMotionBehaviour<GeneralMotionData>
    {
        [Title("Settings")]
        [SerializeField, Range(1f, 100f)]
        private float _minLandSpeed = 4f;

        [SerializeField, Range(0f, 100f)]
        private float _maxLandSpeed = 11f;

        private float _currentFallTime;
        private float _landSpeedFactor;

        protected override void OnBehaviourEnable(ICharacter character)
        {
            base.OnBehaviourEnable(character);
            character.GetCC<IMotorCC>().FallImpact += OnFallImpact;
        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            base.OnBehaviourDisable(character);
            character.GetCC<IMotorCC>().FallImpact -= OnFallImpact;
        }

        protected override void OnDataChanged(GeneralMotionData data)
        {
            if (data != null)
            {
                var landData = data.Land;
                PositionSpring.Adjust(landData.PositionSpring);
                RotationSpring.Adjust(landData.RotationSpring);
            }
        }

        public override void UpdateMotion(float deltaTime)
        {
            if (Data == null || _landSpeedFactor <= 0f)
            {
                _currentFallTime = 100f;
                return;
            }
            
            var landData = Data.Land;

            bool playPosLand = landData.PositionCurves.Duration > _currentFallTime;
            if (playPosLand)
            {
                // Evaluate position landing curves.
                Vector3 posLand = landData.PositionCurves.Evaluate(_currentFallTime) * _landSpeedFactor;
                posLand = MotionMixer.TargetTransform.InverseTransformVector(posLand);
                SetTargetPosition(posLand);
            }

            bool playRotLand = landData.RotationCurves.Duration > _currentFallTime;
            if (playRotLand)
            {
                // Evaluate rotation landing curves.
                Vector3 rotLand = landData.RotationCurves.Evaluate(_currentFallTime) * _landSpeedFactor;
                SetTargetRotation(rotLand);
            }

            _currentFallTime += deltaTime;

            if (!playPosLand && !playRotLand)
            {
                _landSpeedFactor = -1f;
                SetTargetPosition(Vector3.zero);
                SetTargetRotation(Vector3.zero);
            }
        }

        private void OnFallImpact(float landSpeed)
        {
            float impactVelocityAbs = Mathf.Abs(landSpeed);

            if (impactVelocityAbs > _minLandSpeed)
            {
                _currentFallTime = 0f;
                _landSpeedFactor = Mathf.Clamp01(impactVelocityAbs / _maxLandSpeed);
            }
        }
    }
}