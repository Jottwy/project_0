using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents a 2D spring system with configurable physical properties for simulating spring motion.
    /// </summary>
    public sealed class Spring2D
    {
        private SpringSettings _settings;
        private Vector2 _acceleration;
        private Vector2 _targetValue;
        private Vector2 _velocity;
        private Vector2 _value;
        private bool _isIdle;
        
        private const float Precision = 0.01f;
        private const float MaxStepSize = 1f / 61f;

        /// <summary>
        /// Initializes a new instance of the Spring2D class with default settings.
        /// </summary>
        public Spring2D() : this(SpringSettings.Default) { }

        /// <summary>
        /// Initializes a new instance of the Spring2D class with custom settings.
        /// </summary>
        public Spring2D(SpringSettings settings)
        {
            _settings = settings;
            _isIdle = true;
            _targetValue = Vector2.zero;
            _velocity = Vector2.zero;
            _acceleration = Vector2.zero;
        }

        /// <summary>
        /// Gets the spring settings.
        /// </summary>
        public SpringSettings Settings => _settings;

        /// <summary>
        /// Gets a value indicating whether the spring is idle (not moving).
        /// </summary>
        public bool IsIdle => _isIdle;
        
        /// <summary>
        /// Gets the current acceleration.
        /// </summary>
        public Vector2 Acceleration => _acceleration;

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
            _value = Vector2.zero;
            _velocity = Vector2.zero;
            _acceleration = Vector2.zero;
            _targetValue = Vector2.zero;
        }

        /// <summary>
        /// Sets a new target value for the spring motion.
        /// This will cause the spring to interpolate smoothly towards the new target.
        /// </summary>
        /// <param name="value">The target value to move towards.</param>
        public void SetTargetValue(Vector2 value)
        {
            _targetValue = value;
            _isIdle = false;
        }

        /// <summary>
        /// Sets a new target value for the spring motion, specified by x and y coordinates.
        /// This will cause the spring to interpolate smoothly towards the new target.
        /// </summary>
        /// <param name="x">The target x-coordinate.</param>
        /// <param name="y">The target y-coordinate.</param>
        public void SetTargetValue(float x, float y)
        {
            _targetValue.x = x;
            _targetValue.y = y;
            _isIdle = false;
        }

        /// <summary>
        /// Advances the spring system by a step based on the provided deltaTime.
        /// This simulates the motion of the spring and updates its position, velocity, and acceleration.
        /// </summary>
        /// <param name="deltaTime">The delta time (time elapsed) since the last frame.</param>
        /// <returns>The current value of the spring after the update.</returns>
        public Vector2 Evaluate(float deltaTime)
        {
            if (_isIdle)
                return Vector2.zero;

            float damp = _settings.Damping;
            float stf = _settings.Stiffness;
            Vector2 val = _value;
            Vector2 vel = _velocity;
            Vector2 acc = _acceleration;

            float stepSize = deltaTime * _settings.Speed;
            float maxStepSize = Mathf.Min(stepSize, MaxStepSize);
            float steps = (int)(stepSize / maxStepSize + 0.5f);

            const float Epsilon = 0.0001f;
            
            for (var i = 0; i < steps; i++)
            {
                float dt = Mathf.Abs(i - (steps - 1)) < Epsilon ? stepSize - i * maxStepSize : maxStepSize;

                val += vel * dt + acc * (dt * dt * 0.5f);

                Vector2 calcAcc = (-stf * (val - _targetValue) + -damp * vel);// / mass;

                vel += (acc + calcAcc) * (dt * 0.5f);
                acc = calcAcc;
            }

            _value = val;
            _velocity = vel;
            _acceleration = acc;

            if (Mathf.Abs(acc.x) < Precision && Mathf.Abs(acc.y) < Precision)
                _isIdle = true;

            return _value;
        }
    }
}