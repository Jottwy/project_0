using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Represents a designated sleeping place with a specific position and rotation.
    /// </summary>
    public interface ISleepingSpot : IMonoBehaviour
    {
        /// <summary>
        /// Gets the position where the character will sleep.
        /// </summary>
        Vector3 SleepPosition { get; }

        /// <summary>
        /// Gets the rotation of the character while sleeping.
        /// </summary>
        Vector3 SleepOrientation { get; }
    }
}