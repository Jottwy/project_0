using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents data for applying a shake effect with a configurable multiplier and motion data.
    /// </summary>
    [Serializable]
    public struct ShakeData
    {
        [HideLabel, BeginHorizontal]
        [Tooltip("The motion profile that defines the shake behavior.")]
        public ShakeMotionProfile Profile;

        [Clamp(0f, 10f)]
        [Tooltip("The duration (in seconds) for which the shake effect lasts.")]
        public float Duration;

        [EndHorizontal]
        [Tooltip("The intensity multiplier for the shake effect.")]
        public float Multiplier;

        /// <summary>
        /// Returns true if the shake effect is worth playing (has a valid motion and significant multiplier).
        /// </summary>
        public bool IsPlayable => Duration > 0.01f;
    }

    /// <summary>
    /// Defines motion data for shake effects, including position and rotation shaking in 3D space.
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Motion/Shake", fileName = "Shake_")]
    public sealed class ShakeMotionProfile : ScriptableObject
    {
        [SerializeField, SubGroup]
        [Tooltip("Settings for shaking the position in 3D space.")]
        public ShakeSettings3D PositionShake = ShakeSettings3D.Default;

        [SerializeField, SubGroup]
        [Tooltip("Settings for shaking the rotation in 3D space.")]
        public ShakeSettings3D RotationShake = ShakeSettings3D.Default;
    }
}