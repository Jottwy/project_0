using UnityEngine.Events;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Interface representing a firearm trigger mechanism,
    /// handling the trigger state and shooting events.
    /// </summary>
    public interface IFirearmTrigger : IFirearmComponent
    {
        /// <summary>
        /// Indicates whether the trigger is currently held down.
        /// </summary>
        bool IsTriggerHeld { get; }

        /// <summary>
        /// Gets the current charge level of the trigger,
        /// indicating how long it has been held.
        /// </summary>
        float TriggerCharge { get; }

        /// <summary>
        /// Event triggered when the firearm is fired.
        /// </summary>
        event UnityAction Shoot;

        /// <summary>
        /// Activates the trigger, indicating it is being held.
        /// </summary>
        void HoldTrigger();

        /// <summary>
        /// Deactivates the trigger, indicating it has been released.
        /// </summary>
        void ReleaseTrigger();
    }

    public sealed class DefaultFirearmTrigger : IFirearmTrigger
    {
        public static readonly DefaultFirearmTrigger Instance = new();

        public bool IsTriggerHeld => false;
        public float TriggerCharge => 1f;

        public event UnityAction Shoot
        {
            add { }
            remove { }
        }

        public void HoldTrigger() { }
        public void ReleaseTrigger() { }
        public void Attach() { }
        public void Detach() { }
    }
}