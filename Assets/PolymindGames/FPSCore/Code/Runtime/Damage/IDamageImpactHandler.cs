using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Interface for handling impact-related events on a damageable object.
    /// </summary>
    public interface IDamageImpactHandler : IMonoBehaviour
    {
        /// <summary>
        /// Handles the physical impact on the object caused by an external force or explosion.
        /// </summary>
        /// <param name="hitPoint">The point of impact on the object.</param>
        /// <param name="hitForce">The force vector applied to the object.</param>
        void HandleImpact(Vector3 hitPoint, Vector3 hitForce);
    }
}
