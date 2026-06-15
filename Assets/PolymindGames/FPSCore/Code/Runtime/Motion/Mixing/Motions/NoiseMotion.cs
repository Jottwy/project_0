using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/Motion/Noise Motion")]
    public sealed class NoiseMotion : DataMotionBehaviour<NoiseMotionData>
    {
        [SerializeField, Title("Settings")]
        private SpringSettings _positionSpring = new(10f, 100f, 1f, 1f);

        [SerializeField]
        private SpringSettings _rotationSpring = new(10f, 100f, 1f, 1f);

        protected override SpringSettings GetDefaultPositionSpringSettings() => _positionSpring;
        protected override SpringSettings GetDefaultRotationSpringSettings() => _rotationSpring;

        public override void UpdateMotion(float deltaTime)
        {
            if (Data == null)
                return;

            float jitter = Data.NoiseJitter < 0.01f ? 0f : Random.Range(0f, Data.NoiseJitter);
            float speed = Time.time * Data.NoiseSpeed;

            Vector3 posNoise = new()
            {
                x = (Mathf.PerlinNoise(jitter, speed) - 0.5f) * Data.PositionAmplitude.x,
                y = (Mathf.PerlinNoise(jitter + 1f, speed) - 0.5f) * Data.PositionAmplitude.y,
                z = (Mathf.PerlinNoise(jitter + 2f, speed) - 0.5f) * Data.PositionAmplitude.z
            };

            Vector3 rotNoise = new()
            {
                x = (Mathf.PerlinNoise(jitter, speed) - 0.5f) * Data.RotationAmplitude.x,
                y = (Mathf.PerlinNoise(jitter + 1f, speed) - 0.5f) * Data.RotationAmplitude.y,
                z = (Mathf.PerlinNoise(jitter + 2f, speed) - 0.5f) * Data.RotationAmplitude.z
            };

            SetTargetPosition(posNoise);
            SetTargetRotation(rotNoise);
        }
    }
}