using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Represents 2D animation curves for X and Y axes with a multiplier. 
    /// Used for evaluating values over time, supporting both 2D and 3D-like evaluations.
    /// </summary>
    [Serializable]
    public sealed class AnimCurves2D
    {
        [SerializeField, Range(-25f, 25f)]
        [Tooltip("A multiplier that scales the values of the curves.")]
        public float Multiplier = 1f;

        [SerializeField, Label("Curves", SpaceBefore = 3f)]
        [Tooltip("The animation curve for the X axis.")]
        public AnimationCurve XCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [SerializeField]
        [Tooltip("The animation curve for the Y axis.")]
        public AnimationCurve YCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        private float? _duration;

        /// <summary>
        /// Gets the duration of the longest animation curve based on the keyframe times.
        /// </summary>
        public float Duration => _duration ??= GetDuration();

        /// <summary>
        /// Evaluates the animation curves at the specified time and returns a Vector2 with the results.
        /// </summary>
        /// <param name="time">The time at which to evaluate both the X and Y axis curves.</param>
        /// <returns>A Vector2 representing the evaluated values for X and Y axes.</returns>
        public Vector2 Evaluate(float time)
        {
            return new Vector2
            {
                x = XCurve.Evaluate(time) * Multiplier,
                y = YCurve.Evaluate(time) * Multiplier
            };
        }

        /// <summary>
        /// Evaluates the animation curves at the specified times for each axis and returns a Vector2 with the results.
        /// </summary>
        /// <param name="xTime">The time at which to evaluate the X axis curve.</param>
        /// <param name="yTime">The time at which to evaluate the Y axis curve.</param>
        /// <returns>A Vector2 representing the evaluated values for X and Y axes at the specified times.</returns>
        public Vector2 Evaluate(float xTime, float yTime)
        {
            return new Vector2
            {
                x = XCurve.Evaluate(xTime) * Multiplier,
                y = YCurve.Evaluate(yTime) * Multiplier
            };
        }

        /// <summary>
        /// Evaluates the animation curves at the specified times for X and Z axes and returns a Vector3 with the results.
        /// Useful for simulating a 3D effect based on 2D curves.
        /// </summary>
        /// <param name="xTime">The time at which to evaluate the X axis curve.</param>
        /// <param name="zTime">The time at which to evaluate the Y axis curve, mapped to Z axis.</param>
        /// <returns>A Vector3 representing the evaluated values for X and Z axes, with Y always set to 0.</returns>
        public Vector3 EvaluateVec3(float xTime, float zTime)
        {
            return new Vector3
            {
                x = XCurve.Evaluate(xTime) * Multiplier,
                y = 0f,
                z = YCurve.Evaluate(zTime) * Multiplier
            };
        }

        /// <summary>
        /// Calculates the maximum duration of the animation curves based on their keyframe times.
        /// </summary>
        /// <returns>The duration of the longest animation curve based on the time of the last keyframe.</returns>
        private float GetDuration()
        {
            float curvesDuration = 0f;

            curvesDuration = GetKeyTimeLargerThan(XCurve, curvesDuration);
            curvesDuration = GetKeyTimeLargerThan(YCurve, curvesDuration);

            return curvesDuration;
        }

        /// <summary>
        /// Gets the time of the last keyframe in the provided animation curve that is larger than the given duration.
        /// </summary>
        /// <param name="animCurve">The animation curve to check.</param>
        /// <param name="largerThan">The duration to compare against.</param>
        /// <returns>The time of the last keyframe in the curve if it is larger than the specified duration, otherwise the specified duration.</returns>
        private static float GetKeyTimeLargerThan(AnimationCurve animCurve, float largerThan)
        {
            foreach (var key in animCurve.keys)
            {
                if (key.time > largerThan)
                    largerThan = key.time;
            }

            return largerThan;
        }
    }
}