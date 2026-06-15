using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents a 3D spring system with configurable physical properties for simulating spring motion.
    /// </summary>
    public sealed class Spring3D
    {
        private SpringSettings _settings;
        private Vector3 _acceleration;
        private Vector3 _targetValue;
        private Vector3 _velocity;
        private Vector3 _value;
        private bool _isIdle;

        private const float Precision = 0.00001f;
        private const float MaxStepSize = 1f / 61f;

        /// <summary>
        /// Initializes a new instance of the Spring3D class with default settings.
        /// </summary>
        public Spring3D() : this(SpringSettings.Default) { }

        /// <summary>
        /// Initializes a new instance of the Spring3D class with custom settings.
        /// </summary>
        public Spring3D(SpringSettings settings)
        {
            _settings = settings;
            _isIdle = true;
            _targetValue = Vector3.zero;
            _velocity = Vector3.zero;
            _acceleration = Vector3.zero;
        }

        public SpringSettings Settings => _settings;
        public Vector3 CurrentValue => _value;

        /// <summary>
        /// Gets a value indicating whether the spring is idle (not moving).
        /// </summary>
        public bool IsIdle => _isIdle;

        /// <summary>
        /// Adjusts the spring settings.
        /// </summary>
        /// <param name="settings">The new spring settings.</param>
        public void Adjust(SpringSettings settings)
        {
            _settings = settings;
            _isIdle = false;
        }

        /// <summary>
        /// Resets the spring system to its initial state, clearing all values.
        /// </summary>
        public void Reset()
        {
            _isIdle = true;
            _value = Vector3.zero;
            _velocity = Vector3.zero;
            _acceleration = Vector3.zero;
            _targetValue = Vector3.zero; // Reset target value
        }

        /// <summary>
        /// Sets a new target value for the spring motion.
        /// This will cause the spring to interpolate smoothly towards the new target.
        /// </summary>
        /// <param name="value">The target value to move towards.</param>
        public void SetTargetValue(Vector3 value)
        {
            _targetValue = value;
            _isIdle = false;
        }

        /// <summary>
        /// Advances the spring system by a step based on the provided deltaTime.
        /// This simulates the motion of the spring and updates its position, velocity, and acceleration.
        /// </summary>
        /// <param name="deltaTime">The delta time (time elapsed) since the last frame.</param>
        /// <returns>The current value of the spring after the update.</returns>
        public Vector3 Evaluate(float deltaTime)
        {
            if (_isIdle)
                return Vector3.zero;

            float damp = _settings.Damping;
            float stf = _settings.Stiffness;
            Vector3 val = _value;
            Vector3 vel = _velocity;
            Vector3 acc = _acceleration;

            float stepSize = deltaTime * _settings.Speed;
            float maxStepSize = Mathf.Min(stepSize, MaxStepSize);
            float steps = (int)(stepSize / maxStepSize + 0.5f);

            const float Epsilon = 0.0001f;

            for (var i = 0; i < steps; i++)
            {
                float dt = Mathf.Abs(i - (steps - 1)) < Epsilon ? stepSize - i * maxStepSize : maxStepSize;

                val += vel * dt + acc * (dt * dt * 0.5f);

                Vector3 calcAcc = (-stf * (val - _targetValue) + -damp * vel);// / mass;

                vel += (acc + calcAcc) * (dt * 0.5f);
                acc = calcAcc;
            }

            _value = val;
            _velocity = vel;
            _acceleration = acc;

            if (Mathf.Abs(acc.x) < Precision && Mathf.Abs(acc.y) < Precision && Mathf.Abs(acc.z) < Precision)
                _isIdle = true;

            return _value;
        }
    }
}