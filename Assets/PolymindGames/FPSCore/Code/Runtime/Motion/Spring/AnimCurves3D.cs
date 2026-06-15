using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents 3D animation curves for X, Y, and Z axes with an optional multiplier. 
    /// Used to evaluate values over time for procedural motion effects.
    /// </summary>
    [Serializable]
    public sealed class AnimCurves3D
    {
        [SerializeField, Range(-10f, 10f)]
        [Tooltip("A multiplier that scales the values of the curves.")]
        public float Multiplier = 1f;

        [SerializeField, Label("Curves", SpaceBefore = 3f)]
        [Tooltip("The animation curve for the X axis.")]
        public AnimationCurve XCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [SerializeField]
        [Tooltip("The animation curve for the Y axis.")]
        public AnimationCurve YCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [SerializeField]
        [Tooltip("The animation curve for the Z axis.")]
        public AnimationCurve ZCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        private float? _duration;
        private const float MaxDuration = 1000f;

        /// <summary>
        /// Initializes a new instance of the AnimCurves3D class.
        /// </summary>
        public AnimCurves3D() { }

        /// <summary>
        /// Initializes a new instance of the AnimCurves3D class with the specified multiplier and curves.
        /// </summary>
        /// <param name="multiplier">The multiplier to scale the curve values.</param>
        /// <param name="xCurve">The animation curve for the X axis.</param>
        /// <param name="yCurve">The animation curve for the Y axis.</param>
        /// <param name="zCurve">The animation curve for the Z axis.</param>
        public AnimCurves3D(float multiplier, AnimationCurve xCurve, AnimationCurve yCurve, AnimationCurve zCurve)
        {
            Multiplier = multiplier;
            XCurve = xCurve;
            YCurve = yCurve;
            ZCurve = zCurve;
        }

        /// <summary>
        /// Gets the duration of the longest animation curve, clamped by the maximum duration limit.
        /// </summary>
        public float Duration => _duration ??= GetDuration();

        /// <summary>
        /// Evaluates the animation curves at the specified time and returns a Vector3 with the results.
        /// </summary>
        /// <param name="time">The time at which to evaluate the curves for all axes.</param>
        /// <returns>A Vector3 representing the evaluated values for X, Y, and Z axes.</returns>
        public Vector3 Evaluate(float time)
        {
            return new Vector3
            {
                x = XCurve.Evaluate(time) * Multiplier,
                y = YCurve.Evaluate(time) * Multiplier,
                z = ZCurve.Evaluate(time) * Multiplier
            };
        }

        /// <summary>
        /// Evaluates the animation curves at the specified times for each axis and returns a Vector3 with the results.
        /// </summary>
        /// <param name="xTime">The time at which to evaluate the X axis curve.</param>
        /// <param name="yTime">The time at which to evaluate the Y axis curve.</param>
        /// <param name="zTime">The time at which to evaluate the Z axis curve.</param>
        /// <returns>A Vector3 representing the evaluated values for X, Y, and Z axes at the specified times.</returns>
        public Vector3 Evaluate(float xTime, float yTime, float zTime)
        {
            return new Vector3
            {
                x = XCurve.Evaluate(xTime) * Multiplier,
                y = YCurve.Evaluate(yTime) * Multiplier,
                z = ZCurve.Evaluate(zTime) * Multiplier
            };
        }

        /// <summary>
        /// Calculates the maximum duration of the animation curves based on their keyframes.
        /// </summary>
        /// <returns>The duration of the longest animation curve, clamped to the maximum duration.</returns>
        private float GetDuration()
        {
            float curvesDuration = 0f;

            curvesDuration = GetKeyTimeLargerThan(XCurve, curvesDuration);
            curvesDuration = GetKeyTimeLargerThan(YCurve, curvesDuration);
            curvesDuration = GetKeyTimeLargerThan(ZCurve, curvesDuration);

            return Mathf.Clamp(curvesDuration, 0f, MaxDuration);
        }

        /// <summary>
        /// Gets the duration of the longest keyframe in the provided animation curve that is greater than the given duration.
        /// </summary>
        /// <param name="animCurve">The animation curve to check.</param>
        /// <param name="largerThan">The duration to compare against.</param>
        /// <returns>The time of the last keyframe in the curve if it is larger than the specified duration, otherwise the specified duration.</returns>
        private static float GetKeyTimeLargerThan(AnimationCurve animCurve, float largerThan)
        {
            if (animCurve.length == 0)
                return largerThan;

            float duration = animCurve.keys[animCurve.length - 1].time;
            return duration > largerThan ? duration : largerThan;
        }
    }
}