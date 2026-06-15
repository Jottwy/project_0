using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    [RequireComponent(typeof(Collider))]
    public sealed class TriggerEventHandler : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Event triggered when another collider enters the trigger.")]
        private UnityEvent<Collider> _onTriggerEnter;

        [SerializeField]
        [Tooltip("Event triggered when another collider exits the trigger.")]
        private UnityEvent<Collider> _onTriggerExit;

        /// <summary>
        /// Event for external listeners to handle trigger enter actions.
        /// </summary>
        public event UnityAction<Collider> TriggerEnter
        {
            add => _onTriggerEnter.AddListener(value);
            remove => _onTriggerEnter.RemoveListener(value);
        }

        /// <summary>
        /// Event for external listeners to handle trigger exit actions.
        /// </summary>
        public event UnityAction<Collider> TriggerExit
        {
            add => _onTriggerExit.AddListener(value);
            remove => _onTriggerExit.RemoveListener(value);
        }

        /// <summary>
        /// Invoked when another collider enters the trigger.
        /// </summary>
        /// <param name="other">The collider that entered the trigger.</param>
        private void OnTriggerEnter(Collider other) => _onTriggerEnter?.Invoke(other);

        /// <summary>
        /// Invoked when another collider exits the trigger.
        /// </summary>
        /// <param name="other">The collider that exited the trigger.</param>
        private void OnTriggerExit(Collider other) => _onTriggerExit?.Invoke(other);

#if UNITY_EDITOR
        /// <summary>
        /// Ensures that the component has a trigger collider during development.
        /// </summary>
        private void Reset()
        {
            if (!TryGetComponent(out Collider col))
                col = gameObject.AddComponent<SphereCollider>();

            col.isTrigger = true;
        }
#endif
    }
}