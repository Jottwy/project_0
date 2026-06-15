using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.InputSystem
{
    /// <summary>
    /// Manages events that are triggered when the input context changes
    /// to a specific context, enabling and disabling events accordingly.
    /// </summary>
    public sealed class InputContextEvents : MonoBehaviour
    {
        [SerializeField, NotNull]
        [Tooltip("The input context that this handler listens for.")]
        private InputContext _targetContext;

        [SerializeField, SpaceArea]
        [Tooltip("Event invoked when the target input context becomes active.")]
        private UnityEvent _contextEnabledEvent;

        [SerializeField]
        [Tooltip("Event invoked when the target input context becomes inactive.")]
        private UnityEvent _contextDisabledEvent;

        private bool _wasContextActive;
        
        private void OnEnable() => InputManager.Instance.ContextChanged += HandleContextChanged;
        private void OnDisable() => InputManager.Instance.ContextChanged -= HandleContextChanged;

        /// <summary>
        /// Handles context changes and invokes the corresponding events
        /// based on whether the target context is active or inactive.
        /// </summary>
        /// <param name="context">The current input context.</param>
        private void HandleContextChanged(InputContext context)
        {
            if (context == _targetContext)
            {
                if (!_wasContextActive)
                {
                    _wasContextActive = true;
                    _contextEnabledEvent.Invoke();
                }
            }
            else if (_wasContextActive)
            {
                _contextDisabledEvent.Invoke();
                _wasContextActive = false;
            }
        }
    }
}
