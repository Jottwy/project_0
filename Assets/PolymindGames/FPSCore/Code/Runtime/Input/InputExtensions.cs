using System.Runtime.CompilerServices;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine;
using System;

namespace PolymindGames.InputSystem
{
    /// <summary>
    /// Contains extension methods for managing InputActionReferences with added support for registering,
    /// unregistering, enabling, and disabling input actions.
    /// </summary>
    public static class InputExtensions
    {
        // A dictionary that tracks enabled actions to ensure they are disabled when no longer needed
        private static readonly Dictionary<InputActionReference, int> _enabledActions = new();

        #region Initialization
#if UNITY_EDITOR
        /// <summary>
        /// Clears all enabled actions when reloading in the Unity Editor.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reload()
        {
            // Disable all actions on reload
            foreach (var action in _enabledActions.Keys)
                action.action.Disable();

            _enabledActions.Clear();
        }
#endif
        #endregion

        /// <summary>
        /// Registers a callback to be invoked when the action's "started" phase is triggered.
        /// </summary>
        /// <param name="actionRef">The reference to the input action.</param>
        /// <param name="callback">The callback to invoke when the action is started.</param>
        public static void RegisterStarted(this InputActionReference actionRef, Action<InputAction.CallbackContext> callback)
        {
            EnsureActionIsNotNull(actionRef);

            EnableAction(actionRef);
            actionRef.action.started += callback;
        }

        /// <summary>
        /// Registers a callback to be invoked when the action's "performed" phase is triggered.
        /// </summary>
        /// <param name="actionRef">The reference to the input action.</param>
        /// <param name="callback">The callback to invoke when the action is performed.</param>
        public static void RegisterPerformed(this InputActionReference actionRef, Action<InputAction.CallbackContext> callback)
        {
            EnsureActionIsNotNull(actionRef);

            EnableAction(actionRef);
            actionRef.action.performed += callback;
        }

        /// <summary>
        /// Registers a callback to be invoked when the action's "canceled" phase is triggered.
        /// </summary>
        /// <param name="actionRef">The reference to the input action.</param>
        /// <param name="callback">The callback to invoke when the action is canceled.</param>
        public static void RegisterCanceled(this InputActionReference actionRef, Action<InputAction.CallbackContext> callback)
        {
            EnsureActionIsNotNull(actionRef);

            EnableAction(actionRef);
            actionRef.action.canceled += callback;
        }

        /// <summary>
        /// Unregisters a callback from the action's "started" phase.
        /// </summary>
        /// <param name="actionRef">The reference to the input action.</param>
        /// <param name="callback">The callback to remove from the "started" phase.</param>
        public static void UnregisterStarted(this InputActionReference actionRef, Action<InputAction.CallbackContext> callback)
        {
            EnsureActionIsNotNull(actionRef);

            actionRef.action.started -= callback;
            DisableAction(actionRef);
        }

        /// <summary>
        /// Unregisters a callback from the action's "performed" phase.
        /// </summary>
        /// <param name="actionRef">The reference to the input action.</param>
        /// <param name="callback">The callback to remove from the "performed" phase.</param>
        public static void UnregisterPerformed(this InputActionReference actionRef, Action<InputAction.CallbackContext> callback)
        {
            EnsureActionIsNotNull(actionRef);

            actionRef.action.performed -= callback;
            DisableAction(actionRef);
        }

        /// <summary>
        /// Unregisters a callback from the action's "canceled" phase.
        /// </summary>
        /// <param name="actionRef">The reference to the input action.</param>
        /// <param name="callback">The callback to remove from the "canceled" phase.</param>
        public static void UnregisterCanceled(this InputActionReference actionRef, Action<InputAction.CallbackContext> callback)
        {
            EnsureActionIsNotNull(actionRef);

            actionRef.action.canceled -= callback;
            DisableAction(actionRef);
        }

        /// <summary>
        /// Enables the specified action, ensuring it is only enabled once if multiple listeners are attached.
        /// </summary>
        /// <param name="actionRef">The reference to the input action to enable.</param>
        public static void EnableAction(this InputActionReference actionRef)
        {
            EnsureActionIsNotNull(actionRef);

            if (_enabledActions.TryGetValue(actionRef, out var listenerCount))
            {
               _enabledActions[actionRef] = listenerCount + 1;
            }
            else
            {
               _enabledActions.Add(actionRef, 1);
                actionRef.action.Enable();
            }
        }

        /// <summary>
        /// Disables the specified action, ensuring it is only disabled once all listeners have been removed.
        /// </summary>
        /// <param name="actionRef">The reference to the input action to disable.</param>
        public static void DisableAction(this InputActionReference actionRef)
        {
            EnsureActionIsNotNull(actionRef);

            if (_enabledActions.TryGetValue(actionRef, out var listenerCount))
            {
                listenerCount--;
                if (listenerCount == 0)
                {
                   _enabledActions.Remove(actionRef);
                    actionRef.action.Disable();
                }
                else
                {
                   _enabledActions[actionRef] = listenerCount;
                }
            }
        }

        /// <summary>
        /// Ensures that the input action reference is not null.
        /// </summary>
        /// <param name="actionRef">The input action reference to check.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureActionIsNotNull(InputActionReference actionRef)
        {
#if DEBUG
            if (actionRef == null)
                Debug.LogError("The passed input action is null, you need to set it in the inspector.");
#endif
        }
    }
}