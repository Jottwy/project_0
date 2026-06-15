using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    public interface IMixedMotion
    {
        float Multiplier { get; set; }

        /// <summary>
        /// Advances this motion by one frame.
        /// </summary>
        /// <param name="deltaTime"></param>
        void UpdateMotion(float deltaTime);

        /// <summary>
        /// Return the position offset of this motion in the current frame.
        /// </summary>
        Vector3 GetPosition(float deltaTime);

        /// <summary>
        /// Return the rotation offset of this motion in the current frame.
        /// </summary>
        Quaternion GetRotation(float deltaTime);
    }
}