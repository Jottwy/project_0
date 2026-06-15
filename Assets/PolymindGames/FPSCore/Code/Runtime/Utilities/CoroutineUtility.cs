using UnityEngine.Events;
using System.Collections;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// A custom enumerator that represents a delay in coroutines, allowing you to wait for a specified duration without using <see cref="WaitForSeconds"/>.
    /// </summary>
    public struct WaitForTime : IEnumerator
    {
        private float _remainingTime;

        /// <summary>
        /// Initializes a new instance of the <see cref="WaitForTime"/> struct with a specified delay duration.
        /// </summary>
        /// <param name="delay">The duration to wait, in seconds.</param>
        public WaitForTime(float delay)
        {
            _remainingTime = delay;
        }

        public bool MoveNext()
        {
            _remainingTime -= Time.deltaTime;
            return _remainingTime > 0f;
        }

        public void Reset() { }
        public object Current => null;
    }

    /// <summary>
    /// Utility class for managing coroutines in Unity.
    /// </summary>
    public static class CoroutineUtility
    {
        private static GlobalMonoBehaviour _globalMonoBehaviour;
        private static bool _hasGlobalMonoBehaviour;
        private const float MinInvokeDelay = 0.001f;

        /// <summary>
        /// Gets the global MonoBehaviour instance used for starting global coroutines.
        /// </summary>
        private static MonoBehaviour GlobalBehaviour
        {
            get
            {
                if (!_hasGlobalMonoBehaviour)
                {
                    var gameObj = new GameObject("GlobalMonoBehaviour")
                    {
                        hideFlags = HideFlags.HideInHierarchy
                    };

                    _globalMonoBehaviour = gameObj.AddComponent<GlobalMonoBehaviour>();
                    _hasGlobalMonoBehaviour = true;
                }

                return _globalMonoBehaviour;
            }
        }

        /// <summary>
        /// Starts a coroutine on the global MonoBehaviour instance.
        /// </summary>
        /// <param name="routine">The coroutine to start.</param>
        /// <returns>The Coroutine object representing the started coroutine.</returns>
        public static Coroutine StartGlobalCoroutine(IEnumerator routine) =>
            GlobalBehaviour.StartCoroutine(routine);

        /// <summary>
        /// Invokes an action after a specified delay using a coroutine on the global MonoBehaviour instance.
        /// </summary>
        /// <param name="action">The action to invoke.</param>
        /// <param name="delay">The delay before invoking the action.</param>
        /// <returns>The Coroutine object representing the delayed invocation.</returns>
        public static Coroutine InvokeDelayedGlobal(UnityAction action, float delay)
        {
            if (action == null)
                return null;

            if (delay < MinInvokeDelay)
            {
                action.Invoke();
                return null;
            }

            return StartGlobalCoroutine(InvokeDelayed(action, delay));
        }

        /// <summary>
        /// Stops a coroutine running on the global MonoBehaviour instance.
        /// </summary>
        /// <param name="coroutine">Reference to the coroutine to stop.</param>
        public static void StopGlobalCoroutine(ref Coroutine coroutine)
        {
            if (coroutine != null)
            {
                GlobalBehaviour.StopCoroutine(coroutine);
                coroutine = null;
            }
        }

        /// <summary>
        /// Stops a coroutine running on a specific MonoBehaviour instance.
        /// </summary>
        /// <param name="parent">The MonoBehaviour instance running the coroutine.</param>
        /// <param name="routine">Reference to the coroutine to stop.</param>
        public static void StopCoroutine(MonoBehaviour parent, ref Coroutine routine)
        {
            if (routine != null)
            {
                parent.StopCoroutine(routine);
                routine = null;
            }
        }

        /// <summary>
        /// Starts a coroutine on a specific MonoBehaviour instance, replacing any existing coroutine.
        /// </summary>
        public static void StartOrReplaceCoroutine(MonoBehaviour parent, IEnumerator routine, ref Coroutine coroutine)
        {
            StopCoroutine(parent, ref coroutine);
            coroutine = parent.StartCoroutine(routine);
        }

        /// <summary>
        /// Starts a coroutine on a specific MonoBehaviour instance, replacing any existing coroutine.
        /// </summary>
        public static void StartOrReplaceCoroutineSafe(MonoBehaviour parent, IEnumerator routine, ref Coroutine coroutine)
        {
            if (parent != null && parent.isActiveAndEnabled)
                StartOrReplaceCoroutine(parent, routine, ref coroutine);
        }

        /// <summary>
        /// Invokes an action after a specified delay on a specific MonoBehaviour instance.
        /// </summary>
        public static Coroutine InvokeDelayed(MonoBehaviour parent, UnityAction action, float delay)
        {
            if (delay < MinInvokeDelay)
            {
                action.Invoke();
                return null;
            }

            return parent.StartCoroutine(InvokeDelayed(action, delay));
        }

        /// <summary>
        /// Invokes an action after a specified delay on a specific MonoBehaviour instance.
        /// </summary>
        public static Coroutine InvokeDelayedSafe(MonoBehaviour parent, UnityAction action, float delay)
        {
            return action == null || parent == null || !parent.isActiveAndEnabled
                ? null
                : InvokeDelayed(parent, action, delay);
        }

        /// <summary>
        /// Invokes an action with a parameter after a specified delay on a specific MonoBehaviour instance.
        /// </summary>
        public static Coroutine InvokeDelayed<T>(MonoBehaviour parent, UnityAction<T> action, T value, float delay)
        {
            if (delay < MinInvokeDelay)
            {
                action.Invoke(value);
                return null;
            }

            return parent.StartCoroutine(InvokeDelayed(action, value, delay));
        }

        /// <summary>
        /// Invokes an action with a parameter after a specified delay on a specific MonoBehaviour instance.
        /// </summary>
        public static Coroutine InvokeDelayedSafe<T>(MonoBehaviour parent, UnityAction<T> action, T value, float delay)
        {
            return action == null || parent == null || !parent.isActiveAndEnabled
                ? null
                : InvokeDelayed(parent, action, value, delay);
        }

        /// <summary>
        /// Invokes an action on the next frame on a specific MonoBehaviour instance.
        /// </summary>
        public static Coroutine InvokeNextFrame(MonoBehaviour parent, UnityAction action)
            => parent.StartCoroutine(InvokeNextFrame(action));

        /// <summary>
        /// Invokes an action on the next frame on a specific MonoBehaviour instance.
        /// </summary>
        public static Coroutine InvokeNextFrameSafe(MonoBehaviour parent, UnityAction action)
        {
            return action == null || parent == null || !parent.isActiveAndEnabled
                ? null
                : InvokeNextFrame(parent, action);
        }

        /// <summary>
        /// Invokes an action with a parameter on the next frame on a specific MonoBehaviour instance.
        /// </summary>
        public static Coroutine InvokeNextFrame<T>(MonoBehaviour parent, UnityAction<T> action, T value)
            => parent.StartCoroutine(InvokeNextFrame(action, value));

        /// <summary>
        /// Invokes an action with a parameter on the next frame on a specific MonoBehaviour instance.
        /// </summary>
        public static Coroutine InvokeNextFrameSafe<T>(MonoBehaviour parent, UnityAction<T> action, T value)
        {
            return action == null || parent == null || !parent.isActiveAndEnabled
                ? null
                : InvokeNextFrame(parent, action, value);
        }

        /// <summary>
        /// Coroutine to invoke an action after a specified delay.
        /// </summary>
        /// <param name="action">The action to be invoked.</param>
        /// <param name="delay">The delay before invoking the action.</param>
        /// <returns>An IEnumerator representing the coroutine.</returns>
        public static IEnumerator InvokeDelayed(UnityAction action, float delay)
        {
            yield return new WaitForTime(delay);
            action.Invoke();
        }
        
        public static IEnumerator InvokeAfter(IEnumerator routine, UnityAction action)
        {
            yield return routine;
            action.Invoke();
        }

        /// <summary>
        /// Coroutine to invoke an action with a parameter after a specified delay.
        /// </summary>
        /// <typeparam name="T">The type of the parameter for the action.</typeparam>
        /// <param name="action">The action to be invoked.</param>
        /// <param name="value">The parameter value for the action.</param>
        /// <param name="delay">The delay before invoking the action.</param>
        /// <returns>An IEnumerator representing the coroutine.</returns>
        public static IEnumerator InvokeDelayed<T>(UnityAction<T> action, T value, float delay)
        {
            yield return new WaitForTime(delay);
            action.Invoke(value);
        }

        /// <summary>
        /// Coroutine to invoke an action on the next frame.
        /// </summary>
        /// <param name="action">The action to be invoked.</param>
        /// <returns>An IEnumerator representing the coroutine.</returns>
        public static IEnumerator InvokeNextFrame(UnityAction action)
        {
            yield return null;
            action.Invoke();
        }

        /// <summary>
        /// Coroutine to invoke an action with a parameter on the next frame.
        /// </summary>
        /// <typeparam name="T">The type of the parameter for the action.</typeparam>
        /// <param name="action">The action to be invoked.</param>
        /// <param name="value">The parameter value for the action.</param>
        /// <returns>An IEnumerator representing the coroutine.</returns>
        public static IEnumerator InvokeNextFrame<T>(UnityAction<T> action, T value)
        {
            yield return null;
            action.Invoke(value);
        }

        /// <summary>
        /// Internal MonoBehaviour class used for global coroutine management.
        /// </summary>
        private sealed class GlobalMonoBehaviour : MonoBehaviour
        {
            /// <summary>
            /// Callback method called when the GameObject is destroyed.
            /// </summary>
            private void OnDestroy()
            {
                // Reset global references
                _hasGlobalMonoBehaviour = false;
                _globalMonoBehaviour = null;

                // Ensure the GameObject associated with this MonoBehaviour is destroyed
                if (gameObject != null)
                    Destroy(gameObject);
            }
        }
    }
}