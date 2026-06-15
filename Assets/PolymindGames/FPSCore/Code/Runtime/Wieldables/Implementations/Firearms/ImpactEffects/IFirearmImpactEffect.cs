using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Interface for handling the visual and auditory effects
    /// when a projectile impacts an object.
    /// </summary>
    public interface IFirearmImpactEffect : IFirearmComponent
    {
        /// <summary>
        /// Triggers the impact effect based on raycast hit information.
        /// </summary>
        /// <param name="hit">Information about the raycast hit.</param>
        /// <param name="hitDirection">Direction of the hit.</param>
        /// <param name="speed">Projectile speed at the time of impact.</param>
        /// <param name="distanceTravelled">Distance travelled by the projectile.</param>
        void TriggerHitEffect(in RaycastHit hit, Vector3 hitDirection, float speed, float distanceTravelled);
    
        /// <summary>
        /// Triggers the impact effect based on collision data.
        /// </summary>
        /// <param name="collision">Collision data.</param>
        /// <param name="distanceTravelled">Distance travelled by the projectile.</param>
        void TriggerHitEffect(Collision collision, float distanceTravelled);
    }

    public sealed class DefaultFirearmImpactEffect : IFirearmImpactEffect
    {
        public static readonly DefaultFirearmImpactEffect Instance = new();

        public void TriggerHitEffect(in RaycastHit hit, Vector3 hitDirection, float speed, float travelledDistance) { }
        public void TriggerHitEffect(Collision collision, float travelledDistance) { }
        public void Attach() { }
        public void Detach() { }
    }
}