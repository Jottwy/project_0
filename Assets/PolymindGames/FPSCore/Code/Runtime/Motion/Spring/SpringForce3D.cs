using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    using Random = UnityEngine.Random;
    
    /// <summary>
    /// Represents a 3D spring force with a specified force vector and duration.
    /// Can be used for procedural animations or physics effects.
    /// </summary>
    [Serializable]
    public struct SpringForce3D
    {
        [Tooltip("The force vector that defines the spring force in 3D space.")]
        public Vector3 Force;

        [Range(0f, 1f)]
        [Tooltip("The duration for which the force is applied, clamped between 0 and 1.")]
        public float Duration;

        /// <summary>
        /// A default spring force with no force and a short duration.
        /// </summary>
        public static readonly SpringForce3D Default = new(Vector3.zero, 0.125f);

        /// <summary>
        /// Initializes a new instance of the <see cref="SpringForce3D"/> struct with the specified force and duration.
        /// </summary>
        /// <param name="force">The force vector.</param>
        /// <param name="duration">The duration for the force, clamped to be non-negative.</param>
        public SpringForce3D(Vector3 force, float duration)
        {
            Force = force;
            Duration = Mathf.Max(0f, duration);
        }

        /// <summary>
        /// Checks if the spring force is effectively empty (duration below a small threshold).
        /// </summary>
        /// <returns>True if the force duration is negligible; otherwise, false.</returns>
        public bool IsEmpty() => Duration < 0.01f;

        /// <summary>
        /// Adds a vector to the force component of the spring force.
        /// </summary>
        /// <param name="springForce">The original spring force.</param>
        /// <param name="force">The force to be added.</param>
        /// <returns>A new <see cref="SpringForce3D"/> with the added force.</returns>
        public static SpringForce3D operator +(SpringForce3D springForce, Vector3 force)
        {
            springForce.Force += force;
            return springForce;
        }

        /// <summary>
        /// Multiplies the force component of the spring force by a modifier.
        /// </summary>
        /// <param name="springForce">The original spring force.</param>
        /// <param name="mod">The multiplier for the force.</param>
        /// <returns>A new <see cref="SpringForce3D"/> with the modified force.</returns>
        public static SpringForce3D operator *(SpringForce3D springForce, float mod)
        {
            springForce.Force *= mod;
            return springForce;
        }
    }

    /// <summary>
    /// Represents a 3D spring force that can be delayed before being applied.
    /// </summary>
    [Serializable]
    public struct DelayedSpringForce3D
    {
        [Range(0f, 10f)]
        [Tooltip("The delay time before the force is applied, clamped between 0 and 10 seconds.")]
        public float Delay;

        [Tooltip("The spring force to be applied after the delay.")]
        public SpringForce3D SpringForce;

        /// <summary>
        /// A default delayed spring force with no force and zero delay.
        /// </summary>
        public static readonly DelayedSpringForce3D Default = new(SpringForce3D.Default, 0f);

        /// <summary>
        /// Initializes a new instance of the <see cref="DelayedSpringForce3D"/> struct with the specified force and delay.
        /// </summary>
        /// <param name="force">The spring force.</param>
        /// <param name="delay">The delay before the force is applied.</param>
        public DelayedSpringForce3D(SpringForce3D force, float delay)
        {
            Delay = delay;
            SpringForce = force;
        }
    }

    /// <summary>
    /// Represents a 3D spring force that can be randomized within specified bounds.
    /// </summary>
    [Serializable]
    public struct RandomSpringForce3D
    {
        [Tooltip("Indicates whether to randomly invert the sign of each force component.")]
        public bool RandomSign;

        [Tooltip("The minimum force vector for generating a random force.")]
        public Vector3 MinForce;

        [Tooltip("The maximum force vector for generating a random force.")]
        public Vector3 MaxForce;

        [Range(0, 100)]
        [Tooltip("The duration for which the random force is applied.")]
        public float Duration;

        /// <summary>
        /// A default random spring force with no force and a short duration.
        /// </summary>
        public static readonly RandomSpringForce3D Default = new(Vector3.zero, Vector3.zero);

        /// <summary>
        /// Initializes a new instance of the <see cref="RandomSpringForce3D"/> struct with specified force bounds and duration.
        /// </summary>
        /// <param name="minForce">The minimum force vector.</param>
        /// <param name="maxForce">The maximum force vector.</param>
        /// <param name="duration">The duration for the force.</param>
        /// <param name="randomSign">Whether to apply a random sign to the force components.</param>
        public RandomSpringForce3D(Vector3 minForce, Vector3 maxForce, float duration = 0.125f, bool randomSign = false)
        {
            MinForce = minForce;
            MaxForce = maxForce;
            Duration = duration;
            RandomSign = randomSign;
        }

        /// <summary>
        /// Implicitly converts a <see cref="RandomSpringForce3D"/> to a <see cref="SpringForce3D"/> by generating a random force within the specified bounds.
        /// </summary>
        /// <param name="randomSpring">The random spring force.</param>
        /// <returns>A <see cref="SpringForce3D"/> with a random force and the specified duration.</returns>
        public static implicit operator SpringForce3D(RandomSpringForce3D randomSpring)
        {
            var minForce = randomSpring.MinForce;
            var maxForce = randomSpring.MaxForce;

            var x = Random.Range(minForce.x, maxForce.x);
            var y = Random.Range(minForce.y, maxForce.y);
            var z = Random.Range(minForce.z, maxForce.z);

            if (randomSpring.RandomSign)
            {
                x *= Mathf.Sign(Random.Range(-100, 100));
                y *= Mathf.Sign(Random.Range(-100, 100));
                z *= Mathf.Sign(Random.Range(-100, 100));
            }

            return new SpringForce3D(new Vector3(x, y, z), randomSpring.Duration);
        }
    }
}