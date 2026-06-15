using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents a 2D spring force, with a vector for the force and a duration for how long the force is applied.
    /// </summary>
    [Serializable]
    public struct SpringForce2D
    {
        [Tooltip("The force vector that defines the spring force in 2D space.")]
        public Vector2 Force;

        [Range(0, 1f)]
        [Tooltip("The duration (in frames) for which the force is applied, clamped between 0 and 100.")]
        public float Duration;

        /// <summary>
        /// The default spring force, with zero force and a duration of 0.125 frames.
        /// </summary>
        public static readonly SpringForce2D Default = new(Vector3.zero, 0.125f);

        /// <summary>
        /// Constructs a SpringForce2D with the given force and duration.
        /// </summary>
        /// <param name="force">The force vector to apply in 2D space.</param>
        /// <param name="frames">The duration of the force in frames.</param>
        public SpringForce2D(Vector2 force, float frames)
        {
            Force = force;
            Duration = Mathf.Max(0, frames);
        }

        /// <summary>
        /// Checks if the SpringForce2D is empty (i.e., has no applied force).
        /// </summary>
        /// <returns>True if the duration is 0, otherwise false.</returns>
        public bool IsEmpty() => Duration == 0f;

        public static SpringForce2D operator *(SpringForce2D springForce, float mod)
        {
            springForce.Force *= mod;
            return springForce;
        }
    }

    /// <summary>
    /// Represents a delayed 2D spring force, consisting of a force and a delay before the force is applied.
    /// </summary>
    [Serializable]
    public struct DelayedSpringForce2D
    {
        [Range(0f, 10f)]
        [Tooltip("The delay time (in seconds) before the force is applied, clamped between 0 and 10 seconds.")]
        public float Delay;

        [Tooltip("The spring force to be applied after the delay.")]
        public SpringForce2D SpringForce;

        /// <summary>
        /// The default delayed spring force, with zero force and no delay.
        /// </summary>
        public static readonly DelayedSpringForce2D Default = new(SpringForce2D.Default, 0f);

        /// <summary>
        /// Constructs a DelayedSpringForce2D with the given force and delay.
        /// </summary>
        /// <param name="force">The spring force to apply after the delay.</param>
        /// <param name="delay">The delay before applying the force.</param>
        public DelayedSpringForce2D(SpringForce2D force, float delay)
        {
            Delay = delay;
            SpringForce = force;
        }
    }
}