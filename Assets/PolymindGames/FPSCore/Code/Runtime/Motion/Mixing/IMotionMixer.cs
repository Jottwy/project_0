using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Interface for mixing multiple motion effects applied to a target transform.
    /// The motions can be blended based on a weight multiplier, and it supports retrieving, adding, and removing motions.
    /// </summary>
    public interface IMotionMixer : IMonoBehaviour
    {
        /// <summary>
        /// Gets the target transform that the motions are applied to.
        /// </summary>
        Transform TargetTransform { get; }
    
        /// <summary>
        /// Gets or sets the weight multiplier for blending motions.
        /// The value is clamped between 0 (no effect) and 1 (full effect).
        /// </summary>
        float WeightMultiplier { get; set; }
    
        /// <summary>
        /// Gets the pivot offset applied when mixing motions.
        /// </summary>
        Vector3 PivotOffset { get; }
    
        /// <summary>
        /// Gets the position offset applied when mixing motions.
        /// </summary>
        Vector3 PositionOffset { get; }
    
        /// <summary>
        /// Gets the rotation offset applied when mixing motions.
        /// </summary>
        Vector3 RotationOffset { get; }
    
        /// <summary>
        /// Resets the motion mixer with a new target transform and offset values.
        /// </summary>
        /// <param name="targetTransform">The new target transform to apply motions to.</param>
        /// <param name="pivotOffset">The new pivot offset to apply.</param>
        /// <param name="positionOffset">The new position offset to apply.</param>
        /// <param name="rotationOffset">The new rotation offset to apply.</param>
        void ResetMixer(Transform targetTransform, Vector3 pivotOffset, Vector3 positionOffset, Vector3 rotationOffset);
    
        /// <summary>
        /// Retrieves a motion of type T from the mixer. Logs an error if no motion of that type exists.
        /// </summary>
        /// <typeparam name="T">The type of motion to retrieve.</typeparam>
        /// <returns>The motion of type T, if found; otherwise, null.</returns>
        T GetMotion<T>() where T : class, IMixedMotion;
    
        /// <summary>
        /// Attempts to retrieve a motion of type T from the mixer. Returns true if found, false otherwise.
        /// </summary>
        /// <typeparam name="T">The type of motion to retrieve.</typeparam>
        /// <param name="motion">The motion of type T, if found.</param>
        /// <returns>True if a motion of type T exists in the mixer; otherwise, false.</returns>
        bool TryGetMotion<T>(out T motion) where T : class, IMixedMotion;
    
        /// <summary>
        /// Adds a new motion to the mixer.
        /// </summary>
        /// <param name="motion">The motion to add to the mixer.</param>
        void AddMotion(IMixedMotion motion);
    
        /// <summary>
        /// Removes a motion from the mixer.
        /// </summary>
        /// <param name="motion">The motion to remove from the mixer.</param>
        void RemoveMotion(IMixedMotion motion);
    }
}
