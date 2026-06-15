using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents a 1D spring force, with a value for the force and a duration for how long the force is applied.
    /// </summary>
    [Serializable]
    public struct SpringForce1D
    {
        [Tooltip("The force value for the spring force in 1D space.")]
        public float Force;

        [Range(0, 1f)]
        [Tooltip("The duration (in frames) for which the force is applied, clamped between 0 and 100.")]
        public float Duration;

        /// <summary>
        /// The default spring force, with zero force and a duration of 0.125 frames.
        /// </summary>
        public static readonly SpringForce1D Default = new(0f, 0.125f);

        /// <summary>
        /// Constructs a SpringForce1D with the given force and duration.
        /// </summary>
        /// <param name="force">The force value to apply in 1D space.</param>
        /// <param name="duration">The duration of the force in frames.</param>
        public SpringForce1D(float force, float duration)
        {
            Force = force;
            Duration = Mathf.Max(0, duration);
        }

        /// <summary>
        /// Multiplies the force of the SpringForce1D by a modifier value.
        /// </summary>
        /// <param name="springForce">The SpringForce1D instance to modify.</param>
        /// <param name="mod">The modifier value to scale the force by.</param>
        /// <returns>A new SpringForce1D with the scaled force.</returns>
        public static SpringForce1D operator *(SpringForce1D springForce, float mod)
        {
            springForce.Force *= mod;
            return springForce;
        }
    }

    /// <summary>
    /// Represents a delayed 1D spring force, consisting of a force and a delay before the force is applied.
    /// </summary>
    [Serializable]
    public struct DelayedSpringForce1D
    {
        [Range(0f, 2f)]
        [Tooltip("The delay time (in seconds) before the force is applied, clamped between 0 and 2 seconds.")]
        public float Delay;

        [Tooltip("The spring force to be applied after the delay.")]
        public SpringForce1D SpringForce;

        /// <summary>
        /// The default delayed spring force, with zero force and no delay.
        /// </summary>
        public static readonly DelayedSpringForce1D Default = new(SpringForce1D.Default, 0f);

        /// <summary>
        /// Constructs a DelayedSpringForce1D with the given force and delay.
        /// </summary>
        /// <param name="force">The spring force to apply after the delay.</param>
        /// <param name="delay">The delay before applying the force.</param>
        public DelayedSpringForce1D(SpringForce1D force, float delay)
        {
            Delay = delay;
            SpringForce = force;
        }
    }
}