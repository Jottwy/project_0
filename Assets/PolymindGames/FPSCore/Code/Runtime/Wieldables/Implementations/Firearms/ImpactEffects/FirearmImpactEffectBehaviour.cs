using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Abstract base class for implementing impact effects in firearms.
    /// </summary>
    public abstract class FirearmImpactEffectBehaviour : FirearmComponentBehaviour, IFirearmImpactEffect
    {
        protected const string AddMenuPath = "Polymind Games/Wieldables/Firearms/Impact Effects/";

        /// <inheritdoc/>
        public abstract void TriggerHitEffect(in RaycastHit hit, Vector3 hitDirection, float speed, float travelledDistance);
        
        /// <inheritdoc/>
        public abstract void TriggerHitEffect(Collision collision, float travelledDistance);

        /// <summary>
        /// Called when the behavior is enabled. Sets the impact effect for the associated firearm.
        /// </summary>
        protected virtual void OnEnable()
        {
            if (Firearm != null)
                Firearm.ImpactEffect = this;
        }
    }
}