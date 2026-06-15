using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    public sealed class TransformTween : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("If true, the tween will play automatically on start.")]
        private bool _playOnStart;

        [SerializeField]
        private bool _unscaledTime = false;

        [SerializeField, SpaceArea]
        [Tooltip("If true, the tween will use the current value as the start value instead of the one defined.")]
        private bool _useCurrentValueAsStart;

        [SerializeField]
        [Tooltip("If true, the end value will be relative to the current value.")]
        private bool _useRelativeEndValue;

        [SpaceArea]
        [SerializeField]
        [Tooltip("If true, the tween will alternate between start and end values (Yoyo effect).")]
        private bool _yoyo;
        
        [SerializeField]
        [Tooltip("The number of times the tween will loop.")]
        private int _loopCount = 0;

        [SpaceArea]
        [SerializeField, ReorderableList, LabelByChild(nameof(TweenComponent.Type))]
        [Tooltip("A list of tween components to apply to the transform.")]
        private TweenComponent[] _tweens;

        private Transform _cachedTransform;

        /// <summary>
        /// Starts the tween animation based on the specified settings, optionally in reverse order.
        /// </summary>
        /// <param name="reverse">Indicates whether to play the tween in reverse, from end to start.</param>
        public void PlayTween(bool reverse = false)
        {
            _cachedTransform.ClearTweens();

            foreach (var component in _tweens)
            {
                // Determine the start value based on the option
                var startValue = _useCurrentValueAsStart
                    ? GetCurrentValueForTweenType(component.Type)
                    : reverse ? GetEndValueForTweenType(component) : component.StartValue;

                // Determine the end value based on the option
                var endValue = reverse ? component.StartValue : component.EndValue;
                if (_useRelativeEndValue)
                    endValue = GetRelativeEndValueForTweenType(component, endValue);

                // Handle the tween based on the type
                if (component.Type == TweenType.Rotate)
                {
                    _cachedTransform.TweenLocalRotation(Quaternion.Euler(endValue), component.Duration)
                        .SetDelay(component.Delay)
                        .SetEasing(component.EaseType)
                        .SetUnscaledTime(_unscaledTime)
                        .SetStartValue(Quaternion.Euler(startValue))
                        .SetLoops(_loopCount, _yoyo);
                }
                else
                {
                    (component.Type is TweenType.Move
                        ? _cachedTransform.TweenLocalPosition(endValue, component.Duration)
                        : _cachedTransform.TweenLocalScale(endValue, component.Duration))
                        .SetDelay(component.Delay)
                        .SetEasing(component.EaseType)
                        .SetStartValue(startValue)
                        .SetUnscaledTime(_unscaledTime)
                        .SetLoops(_loopCount, _yoyo);
                }
            }
        }

        /// <summary>
        /// Stops the tween animation and resets the transform properties according to the specified behavior.
        /// </summary>
        /// <param name="behavior">The behavior when stopping the tween (e.g., keeping current value or resetting).</param>
        public void StopTween(TweenResetBehavior behavior = TweenResetBehavior.KeepCurrentValue)
        {
            _cachedTransform.ClearTweens(behavior);
        }
        
        private Vector3 GetCurrentValueForTweenType(TweenType type)
        {
            return type switch
            {
                TweenType.Move => _cachedTransform.localPosition,
                TweenType.Rotate => _cachedTransform.localRotation.eulerAngles,
                TweenType.Scale => _cachedTransform.localScale,
                _ => Vector3.zero
            };
        }

        private Vector3 GetRelativeEndValueForTweenType(TweenComponent component, Vector3 endValue)
        {
            return component.Type switch
            {
                TweenType.Move => _cachedTransform.localPosition + endValue,
                TweenType.Scale => _cachedTransform.localScale + endValue,
                _ => endValue
            };
        }

        private Vector3 GetEndValueForTweenType(TweenComponent component)
        {
            return component.Type switch
            {
                TweenType.Move => component.EndValue,
                TweenType.Rotate => component.EndValue,
                TweenType.Scale => component.EndValue,
                _ => Vector3.zero
            };
        }

        private void Awake() => _cachedTransform = transform;

        private void OnEnable()
        {
            if (_playOnStart)
                PlayTween();
        }
        
        private void OnDisable() => StopTween();

        #region Internal Types
        [Serializable]
        private struct TweenComponent
        {
            [Tooltip("Specifies the type of tween to apply.")]
            public TweenType Type;

            [Tooltip("The starting value of the tween, depending on the type (position, rotation, or scale).")]
            public Vector3 StartValue;

            [Tooltip("The target value of the tween, depending on the type (position, rotation, or scale).")]
            public Vector3 EndValue;

            [Range(0f, 10f)]
            [Tooltip("The delay before the tween starts.")]
            public float Delay;

            [Range(0f, 100f)]
            [Tooltip("The duration of the tween.")]
            public float Duration;

            [Tooltip("The easing type to use for the tween.")]
            public EaseType EaseType;
        }

        private enum TweenType
        {
            Move = 0,
            Rotate = 1,
            Scale = 2
        }
        #endregion
    }
}