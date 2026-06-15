using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents the different types of spring behaviors.
    /// </summary>
    public enum SpringType
    {
        /// <summary>
        /// A smooth spring behavior, which is typically slower and more gradual in reaching the target value.
        /// </summary>
        Smooth = 1,

        /// <summary>
        /// A responsive spring behavior, which quickly reaches the target value with less damping.
        /// </summary>
        Responsive = 2,

        /// <summary>
        /// A custom spring behavior, where the motion can be adjusted based on user-defined settings.
        /// </summary>
        Custom = 3
    }
    
    /// <summary>
    /// Represents the settings for a spring system, including damping, stiffness, mass, and speed.
    /// </summary>
    [Serializable]
    public struct SpringSettings
    {
        [Range(0f, 100f)]
        [Tooltip("The damping factor that reduces oscillations over time.")]
        public float Damping;

        [Range(0f, 1000f)]
        [Tooltip("The stiffness of the spring, controlling how much force it applies to resist movement.")]
        public float Stiffness;

        [Range(0f, 10f), Hide]
        [Tooltip("The mass attached to the spring, affecting its acceleration.")]
        public float Mass;

        [Range(0f, 10f)]
        [Tooltip("The speed at which the spring reacts to forces.")]
        public float Speed;

        /// <summary>
        /// Default settings for the spring with typical values for damping, stiffness, mass, and speed.
        /// </summary>
        public static readonly SpringSettings Default = new(10f, 120f, 1f, 1f);

        /// <summary>
        /// Initializes a new instance of the <see cref="SpringSettings"/> struct with the specified damping, stiffness, mass, and speed values.
        /// </summary>
        /// <param name="damping">The damping factor.</param>
        /// <param name="stiffness">The stiffness of the spring.</param>
        /// <param name="mass">The mass attached to the spring.</param>
        /// <param name="speed">The speed at which the spring reacts.</param>
        public SpringSettings(float damping, float stiffness, float mass, float speed)
        {
            Damping = damping;
            Stiffness = stiffness;
            Mass = mass;
            Speed = speed;
        }

        /// <summary>
        /// Checks whether the spring settings are considered null or invalid.
        /// </summary>
        /// <returns>True if the damping or stiffness are too low to be considered valid, false otherwise.</returns>
        public readonly bool IsNull()
        {
            bool nullOrEmpty = Damping < 0.01f || Stiffness < 0.01f;
            return nullOrEmpty;
        }

        /// <summary>
        /// Provides a string representation of the spring settings.
        /// </summary>
        /// <returns>A string that describes the spring's damping, stiffness, mass, and speed values.</returns>
        public override string ToString()
        {
            return $"Damping: {Damping} | Stiffness: {Stiffness} | Mass: {Mass} | Speed: {Speed}";
        }
    }
}