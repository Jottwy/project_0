using Random = UnityEngine.Random;
using UnityEngine;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Contains extension methods for mathematical operations.
    /// </summary>
    public static class MathExtensions
    {
        public static Vector3 NormalizeEulerAngles(this Vector3 angles)
        {
            angles.x = (angles.x > 180) ? angles.x - 360 : angles.x;
            angles.y = (angles.y > 180) ? angles.y - 360 : angles.y;
            angles.z = (angles.z > 180) ? angles.z - 360 : angles.z;
            return angles;
        }

        /// <summary>
        /// Normalizes a value within a specified range.
        /// </summary>
        /// <param name="value">The value to normalize.</param>
        /// <param name="minValue">The minimum value of the range.</param>
        /// <param name="maxValue">The maximum value of the range.</param>
        /// <returns>The normalized value between 0 and 1.</returns>
        public static float Normalize(this float value, float minValue, float maxValue) =>
            Mathf.Clamp01((value - minValue) / (maxValue - minValue));

        /// <summary>
        /// Adds jitter (random noise) to a value.
        /// </summary>
        /// <param name="value">The value to add jitter to.</param>
        /// <param name="jitter">The amount of jitter to add.</param>
        /// <returns>The original value with added jitter.</returns>
        public static float Jitter(this float value, float jitter) =>
            value + Random.Range(-jitter, jitter);

        /// <summary>
        /// Returns a random point within the given bounds.
        /// </summary>
        /// <param name="bounds">The bounds to generate the random point within.</param>
        /// <returns>A random point within the bounds.</returns>
        public static Vector3 GetRandomPoint(this Bounds bounds)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            return new Vector3(
                Random.Range(min.x, max.x),
                Random.Range(min.y, max.y),
                Random.Range(min.z, max.z));
        }

        /// <summary>
        /// Checks if a point is inside a rotated bounding box.
        /// </summary>
        /// <param name="bounds">The bounds of the rotated box.</param>
        /// <param name="rotation">The rotation of the box.</param>
        /// <param name="rotationPivot">The origin of rotation.</param>
        /// <param name="point">The point to check.</param>
        /// <returns>True if the point is inside the rotated bounds, otherwise false.</returns>
        public static bool IsPointInsideRotatedBounds(this Bounds bounds, Quaternion rotation, Vector3 rotationPivot, Vector3 point)
        {
            // Translate so that rotationOrigin is the new origin
            Vector3 translatedPoint = point - rotationPivot;
            Vector3 translatedBoundsCenter = bounds.center - rotationPivot;

            // Counter-rotate the point
            Vector3 counterRotatedPoint = Quaternion.Inverse(rotation) * translatedPoint + rotationPivot;

            // The bounds' center also needs to be translated back after the counter-rotation
            Vector3 counterRotatedBoundsCenter = Quaternion.Inverse(rotation) * translatedBoundsCenter + rotationPivot;

            Bounds counterRotatedBounds = new Bounds(counterRotatedBoundsCenter, bounds.size);

            return counterRotatedBounds.Contains(counterRotatedPoint);
        }

        /// <summary>
        /// Checks if a ray intersects with a rotated bounding box.
        /// </summary>
        /// <param name="bounds">The bounds of the rotated box.</param>
        /// <param name="rotation">The rotation of the box.</param>
        /// <param name="rotationPivot">The origin of rotation.</param>
        /// <param name="ray">The ray to check.</param>
        /// <param name="hitDistance">The distance from the ray's origin to the point of intersection (if any).</param>
        /// <returns>True if the ray intersects the rotated bounds, otherwise false.</returns>
        public static bool RayIntersectsRotatedBounds(this Bounds bounds, Quaternion rotation, Vector3 rotationPivot, Ray ray, out float hitDistance)
        {
            // Translate so that rotationOrigin is the new origin
            Vector3 translatedRayOrigin = ray.origin - rotationPivot;
            Vector3 translatedBoundsCenter = bounds.center - rotationPivot;

            // Counter-rotate the ray
            Vector3 counterRotatedRayOrigin = Quaternion.Inverse(rotation) * translatedRayOrigin;
            Vector3 counterRotatedRayDirection = Quaternion.Inverse(rotation) * ray.direction;

            // The bounds' center also needs to be translated back after the counter-rotation
            Vector3 counterRotatedBoundsCenter = Quaternion.Inverse(rotation) * translatedBoundsCenter;
            Bounds counterRotatedBounds = new Bounds(counterRotatedBoundsCenter, bounds.size);

            // Create a new ray with the counter-rotated origin and direction
            Ray counterRotatedRay = new Ray(counterRotatedRayOrigin, counterRotatedRayDirection);

            // Perform a raycast against the non-rotated bounds
            if (counterRotatedBounds.IntersectRay(counterRotatedRay, out hitDistance))
            {
                // If the ray hits the counter-rotated bounds, return true
                return true;
            }

            hitDistance = 0;
            return false;
        }

        /// <summary>
        /// Jitters the components of a vector by random amounts.
        /// </summary>
        /// <param name="vector">The vector to jitter.</param>
        /// <param name="xJit">The maximum absolute amount to jitter in the x-direction.</param>
        /// <param name="yJit">The maximum absolute amount to jitter in the y-direction.</param>
        /// <param name="zJit">The maximum absolute amount to jitter in the z-direction.</param>
        /// <returns>The jittered vector.</returns>
        public static Vector3 Jitter(this Vector3 vector, float xJit, float yJit, float zJit)
        {
            vector.x -= Mathf.Abs(vector.x * Random.Range(0, xJit)) * 2f;
            vector.y -= Mathf.Abs(vector.y * Random.Range(0, yJit)) * 2f;
            vector.z -= Mathf.Abs(vector.z * Random.Range(0, zJit)) * 2f;

            return vector;
        }

        /// <summary>
        /// Rounds the components of a vector to a specified number of digits.
        /// </summary>
        /// <param name="vector">The vector to round.</param>
        /// <param name="digits">The number of digits to round to.</param>
        /// <returns>The rounded vector.</returns>
        public static Vector3 Round(this Vector3 vector, int digits)
        {
            vector.x = (float)Math.Round(vector.x, digits);
            if (Mathf.Approximately(vector.x, 0f))
                vector.x = 0f;

            vector.y = (float)Math.Round(vector.y, digits);
            if (Mathf.Approximately(vector.y, 0f))
                vector.y = 0f;

            vector.z = (float)Math.Round(vector.z, digits);
            if (Mathf.Approximately(vector.z, 0f))
                vector.z = 0f;

            return vector;
        }

        /// <summary>
        /// Returns a vector with its y-component set to zero.
        /// </summary>
        /// <param name="vector">The vector to modify.</param>
        /// <returns>The vector with the y-component set to zero.</returns>
        public static Vector3 GetHorizontal(this Vector3 vector)
        {
            return new Vector3(vector.x, 0f, vector.z);
        }

        /// <summary>
        /// Returns a new Vector3 instance with the specified components.
        /// If a component is null, the corresponding value from the original Vector3 instance is used.
        /// </summary>
        /// <param name="vector3">The original Vector3 instance.</param>
        /// <param name="x">The new x component, or null to keep the original x value.</param>
        /// <param name="y">The new y component, or null to keep the original y value.</param>
        /// <param name="z">The new z component, or null to keep the original z value.</param>
        /// <returns>A new Vector3 instance with the specified components.</returns>
        public static Vector3 With(this Vector3 vector3, float? x = null, float? y = null, float? z = null)
        {
            return new Vector3(x ?? vector3.x, y ?? vector3.y, z ?? vector3.z);
        }

        public static Vector3 WithX(this Vector3 vector3, float x)
        {
            return new Vector3(x, vector3.y, vector3.z);
        }

        public static Vector3 WithY(this Vector3 vector3, float y)
        {
            return new Vector3(vector3.x, y, vector3.z);
        }

        public static Vector3 WithZ(this Vector3 vector3, float z)
        {
            return new Vector3(vector3.x, vector3.y, z);
        }

        /// <summary>
        /// Returns a random float value within the range defined by the vector.
        /// </summary>
        /// <param name="vector">The vector defining the range.</param>
        /// <returns>A random float value within the specified range.</returns>
        public static float GetRandomFromRange(this Vector2 vector)
        {
            return Random.Range(vector.x, vector.y);
        }

        /// <summary>
        /// Returns a random integer value within the range defined by the vector.
        /// </summary>
        /// <param name="vector">The vector defining the range.</param>
        /// <returns>A random integer value within the specified range.</returns>
        public static int GetRandomFromRange(this Vector2Int vector)
        {
            return Random.Range(vector.x, vector.y + 1);
        }

        /// <summary>
        /// Checks if two vectors are approximately equal within a given tolerance.
        /// </summary>
        /// <param name="v1">The first vector.</param>
        /// <param name="v2">The second vector.</param>
        /// <param name="tolerance">The tolerance within which the vectors are considered approximately equal.</param>
        /// <returns>True if the vectors are approximately equal, false otherwise.</returns>
        public static bool ApproximatelyEquals(this Vector3 v1, Vector3 v2, float tolerance = 0.0001f)
        {
            return Mathf.Abs(v1.x - v2.x) < tolerance &&
                   Mathf.Abs(v1.y - v2.y) < tolerance &&
                   Mathf.Abs(v1.z - v2.z) < tolerance;
        }

        public static Color WithAlpha(this Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
        }
    }
}