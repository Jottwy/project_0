using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents a spring system with configurable physical properties for simulating spring motion.
    /// </summary>
    public sealed class Spring1D
    {
        private SpringSettings _settings;
        private float _acceleration;
        private float _targetValue;
        private float _velocity;
        private float _value;
        private bool _isIdle;

        private const float MaxStepSize = 1f / 61f;

        /// <summary>
        /// Initializes a new instance of the <see cref="Spring1D"/> class with default settings.
        /// </summary>
        public Spring1D() : this(SpringSettings.Default) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Spring1D"/> class with specified settings.
        /// </summary>
        public Spring1D(SpringSettings settings)
        {
            _settings = settings;
            _isIdle = true;
            _targetValue = 0f;
            _velocity = 0f;
            _acceleration = 0f;
        }

        /// <summary>
        /// Gets whether the spring is idle (i.e., it has reached its target value and is not moving).
        /// </summary>
        public bool IsIdle => _isIdle;
        
        public float CurrentValue => _value;

        /// <summary>
        /// Adjust the spring settings and continue simulating the motion.
        /// </summary>
        /// <param name="settings">The new settings for the spring.</param>
        public void Adjust(SpringSettings settings)
        {
            _settings = settings;
            _isIdle = false; // Spring will no longer be idle after adjustments.
        }

        /// <summary>
        /// Resets all values to their initial states, effectively stopping the spring motion.
        /// </summary>
        public void Reset()
        {
            _isIdle = true;
            _value = 0f;
            _velocity = 0f;
            _acceleration = 0f;
        }

        /// <summary>
        /// Sets the target value and starts the spring motion towards that value.
        /// The current velocity is reused, and the motion will interpolate smoothly.
        /// </summary>
        /// <param name="value">The new target value.</param>
        public void SetTargetValue(float value)
        {
            _targetValue = value;
            _isIdle = false; // Spring will now be active and not idle.
        }

        /// <summary>
        /// Advances the spring simulation by one step, based on the given delta time.
        /// This method simulates the spring's motion by applying spring physics calculations.
        /// </summary>
        /// <param name="deltaTime">The time passed since the last update (in seconds).</param>
        /// <returns>The current value of the spring after applying the step.</returns>
        public float Evaluate(float deltaTime)
        {
            if (_isIdle)
                return 0f;

            float damp = _settings.Damping;
            float stf = _settings.Stiffness;
            float val = _value;
            float vel = _velocity;
            float acc = _acceleration;

            float stepSize = deltaTime * _settings.Speed;
            float maxStepSize = Mathf.Min(stepSize, MaxStepSize);
            float steps = (int)(stepSize / maxStepSize + 0.5f);

            const float Epsilon = 0.0001f;
            
            for (var i = 0; i < steps; i++)
            {
                float dt = Mathf.Abs(i - (steps - 1)) < Epsilon ? stepSize - i * maxStepSize : maxStepSize;

                val += vel * dt + acc * (dt * dt * 0.5f);

                float calcAcc = (-stf * (val - _targetValue) + -damp * vel);

                vel += (acc + calcAcc) * (dt * 0.5f);
                acc = calcAcc;
            }
            
            _value = val;
            _velocity = vel;
            _acceleration = acc;

            if (Mathf.Abs(acc) < Mathf.Epsilon)
                _isIdle = true;

            return _value;
        }
    }
}
