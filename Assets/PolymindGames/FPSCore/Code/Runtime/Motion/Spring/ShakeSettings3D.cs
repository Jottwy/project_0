using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents the settings for a 3D shake effect, including the duration, speed, and amplitude of the shake along the X, Y, and Z axes.
    /// </summary>
    [Serializable]
    public struct ShakeSettings3D
    {
        [Range(0f, 100f)]
        [Tooltip("The speed of the shake effect. Higher values result in faster shaking.")]
        public float Speed;
        
        [SerializeField]
        public SpringSettings Spring;

        [Range(0f, 50f), Title("Amplitude")]
        [Tooltip("The amplitude of the shake along the X axis. Controls how far the shake moves on the horizontal plane.")]
        public float XAmplitude;

        [Range(0f, 50f)]
        [Tooltip("The amplitude of the shake along the Y axis. Controls vertical shake.")]
        public float YAmplitude;

        [Range(0f, 50f)]
        [Tooltip("The amplitude of the shake along the Z axis. Controls depth shake.")]
        public float ZAmplitude;
        /// <summary>
        /// The default shake settings, with a duration of 0.2 seconds, a speed of 20, and no amplitude along any axis.
        /// </summary>
        public static readonly ShakeSettings3D Default =
            new()
            {
                Spring = SpringSettings.Default,
                Speed = 20f,
                XAmplitude = 0f,
                YAmplitude = 0f,
                ZAmplitude = 0f
            };

    }
}