using System.Runtime.CompilerServices;
using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents a base class for motion behaviors that handle position and rotation using spring-based calculations.
    /// This class interacts with an <see cref="IMotionMixer"/> to blend motion smoothly and applies multipliers to scale movement.
    /// </summary>
    [RequireComponent(typeof(IMotionMixer))]
    public abstract class MotionBehaviour : CharacterBehaviour, IMixedMotion
    {
        [SerializeField, Range(0f, 10f)]
        [Tooltip("Base multiplier for motion calculations.")]
        private float _multiplier = 1f;

        protected readonly Spring3D PositionSpring = new();
        protected readonly Spring3D RotationSpring = new();

        private bool _ignoreMixerMultiplier = false;
        private float _externalMultiplier = 1f;

        /// <summary>
        /// Reference to the motion mixer on the GameObject.
        /// </summary>
        protected IMotionMixer MotionMixer { get; private set; }

        /// <summary>
        /// Indicates whether the parent multiplier should be ignored.
        /// </summary>
        public bool IgnoreParentMultiplier
        {
            get => _ignoreMixerMultiplier;
            set
            {
                _ignoreMixerMultiplier = value;
                _externalMultiplier = value ? 1f : MotionMixer.WeightMultiplier;
            }
        }

        /// <summary>
        /// External multiplier value, used in combination with other motion factors.
        /// </summary>
        float IMixedMotion.Multiplier
        {
            get => _externalMultiplier;
            set => _externalMultiplier = _ignoreMixerMultiplier ? 1f : Mathf.Clamp01(value);
        }

        /// <summary>
        /// The base multiplier applied to motion.
        /// </summary>
        public float Multiplier
        {
            get => _multiplier;
            set => _multiplier = value;
        }

        /// <summary>
        /// The final multiplier value, calculated as the product of the base and external multipliers.
        /// </summary>
        protected float FinalMultiplier
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _multiplier * _externalMultiplier;
        }

        /// <summary>
        /// Updates the motion behavior each frame.
        /// </summary>
        /// <param name="deltaTime">The time since the last frame.</param>
        public abstract void UpdateMotion(float deltaTime);

        /// <summary>
        /// Evaluates the current position based on spring calculations.
        /// </summary>
        /// <param name="deltaTime">The time since the last evaluation.</param>
        /// <returns>The calculated position.</returns>
        public Vector3 GetPosition(float deltaTime)
        {
            return PositionSpring.Evaluate(deltaTime);
        }

        /// <summary>
        /// Evaluates the current rotation based on spring calculations.
        /// </summary>
        /// <param name="deltaTime">The time since the last evaluation.</param>
        /// <returns>The calculated rotation as a Quaternion.</returns>
        public Quaternion GetRotation(float deltaTime)
        {
            Vector3 value = RotationSpring.Evaluate(deltaTime);
            return Quaternion.Euler(value);
        }

        /// <summary>
        /// Sets the target position for the position spring.
        /// </summary>
        /// <param name="target">The target position.</param>
        protected void SetTargetPosition(Vector3 target)
        {
            float multiplier = FinalMultiplier;
            target.x *= multiplier;
            target.y *= multiplier;
            target.z *= multiplier;
            PositionSpring.SetTargetValue(target);
        }

        /// <summary>
        /// Sets the target rotation for the rotation spring.
        /// </summary>
        /// <param name="target">The target rotation.</param>
        protected void SetTargetRotation(Vector3 target)
        {
            float multiplier = FinalMultiplier;
            target.x *= multiplier;
            target.y *= multiplier;
            target.z *= multiplier;
            RotationSpring.SetTargetValue(target);
        }

        /// <summary>
        /// Called when the script instance is being loaded.
        /// </summary>
        protected virtual void Awake()
        {
            PositionSpring.Adjust(GetDefaultPositionSpringSettings());
            RotationSpring.Adjust(GetDefaultRotationSpringSettings());
            MotionMixer = GetComponent<IMotionMixer>();
        }

        /// <summary>
        /// Called when the behaviour becomes enabled and active.
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();
            MotionMixer.AddMotion(this);
        }

        /// <summary>
        /// Called when the behaviour becomes disabled or inactive.
        /// </summary>
        protected override void OnDisable()
        {
            base.OnDisable();
            MotionMixer.RemoveMotion(this);
        }

        /// <summary>
        /// Retrieves the default spring settings for position adjustments.
        /// </summary>
        /// <returns>The default position spring settings.</returns>
        protected virtual SpringSettings GetDefaultPositionSpringSettings() => SpringSettings.Default;

        /// <summary>
        /// Retrieves the default spring settings for rotation adjustments.
        /// </summary>
        /// <returns>The default rotation spring settings.</returns>
        protected virtual SpringSettings GetDefaultRotationSpringSettings() => SpringSettings.Default;

#if UNITY_EDITOR
        /// <summary>
        /// Called when the script is loaded or a value is changed in the inspector (Editor only).
        /// </summary>
        protected virtual void OnValidate()
        {
            if (!Application.isPlaying || PositionSpring == null)
                return;

            PositionSpring.Adjust(GetDefaultPositionSpringSettings());
            RotationSpring.Adjust(GetDefaultRotationSpringSettings());
        }
#endif
    }
}