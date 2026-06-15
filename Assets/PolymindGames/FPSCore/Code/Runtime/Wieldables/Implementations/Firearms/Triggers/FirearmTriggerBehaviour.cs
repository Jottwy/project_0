using UnityEngine.Events;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Abstract base class for handling trigger behaviors in firearms.
    /// </summary>
    public abstract class FirearmTriggerBehaviour : FirearmComponentBehaviour, IFirearmTrigger
    {
        protected const string AddMenuPath = "Polymind Games/Wieldables/Firearms/Triggers/";

        /// <inheritdoc/>
        public bool IsTriggerHeld { get; protected set; }

        /// <inheritdoc/>
        public float TriggerCharge { get; protected set; } = 1f;

        /// <inheritdoc/>
        public event UnityAction Shoot;

        /// <inheritdoc/>
        public virtual void HoldTrigger()
        {
            if (!IsTriggerHeld)
                TapTrigger();

            IsTriggerHeld = true;
        }

        /// <inheritdoc/>
        public virtual void ReleaseTrigger()
        {
            IsTriggerHeld = false;
        }

        /// <summary>
        /// Method called when the trigger is tapped. Override for custom behavior.
        /// </summary>
        protected virtual void TapTrigger() { }

        /// <summary>
        /// Raises the shoot event to notify subscribers that shooting has occurred.
        /// </summary>
        protected void RaiseShootEvent() => Shoot?.Invoke();

        /// <summary>
        /// Sets the trigger for the associated firearm.
        /// </summary>
        protected virtual void OnEnable()
        {
            if (Firearm != null)
                Firearm.Trigger = this;
        }
    }
}