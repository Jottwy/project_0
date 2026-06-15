using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents a 1D animation curve with a multiplier for scaling the evaluated value.
    /// This class allows evaluating the curve at a given time and applying the multiplier.
    /// </summary>
    [Serializable]
    public sealed class AnimCurve1D
    {
        [Tooltip("A multiplier that scales the value of the curve.")]
        [SerializeField, Range(-10f, 10f)]
        private float _multiplier = 1f;

        [Tooltip("The animation curve to evaluate over time.")]
        [SerializeField]
        private AnimationCurve _curve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        /// <summary>
        /// Gets the duration of the curve based on the time of the last keyframe.
        /// </summary>
        public float Duration => _curve[_curve.length - 1].time;

        /// <summary>
        /// Evaluates the curve at the specified time and returns the scaled value.
        /// </summary>
        /// <param name="time">The time at which to evaluate the curve.</param>
        /// <returns>The evaluated value of the curve at the specified time, scaled by the multiplier.</returns>
        public float Evaluate(float time) => _curve.Evaluate(time) * _multiplier;
    }
}