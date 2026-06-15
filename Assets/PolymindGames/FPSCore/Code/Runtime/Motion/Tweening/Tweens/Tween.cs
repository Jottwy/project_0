using Object = UnityEngine.Object;
using System.Collections;
using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    /// <summary>
    /// Base class for tweening a value of type <typeparamref name="T"/> over time.
    /// </summary>
    /// <typeparam name="T">The type of value to be tweened (must be a struct).</typeparam>
    public abstract class Tween<T> : ITween where T : struct
    {
        private float _direction = 1f;
        private float _progress;
        private float _duration = 1f;
        private float _delay;

        private bool _isPlaying;
        private int _loopCount;
        private bool _yoyo;
        private bool _autoRelease;
        private bool _autoReleaseWithParent;
        private bool _unscaledTime;

        private T _startValue;
        private T _endValue;
        private Object _parent;

        private Action<T> _onUpdate;
        private Action _onComplete;
        private EaseType _easeType = EaseType.SineInOut;

        /// <summary>
        /// Checks if the tween is currently playing.
        /// </summary>
        public bool IsPlaying() => _isPlaying;

        /// <summary>
        /// Gets the parent object attached to the tween.
        /// </summary>
        public Object GetParent() => _parent;

        /// <summary>
        /// Gets the starting value of the tween.
        /// </summary>
        public T GetStartValue() => _startValue;

        /// <summary>
        /// Gets the interpolated current value of the tween.
        /// </summary>
        public T GetCurrentValue() => InterpolateValue(in _startValue, in _endValue, Easer.Apply(_easeType, _progress));

        /// <summary>
        /// Gets the end value of the tween.
        /// </summary>
        public T GetEndValue() => _endValue;

        /// <summary>
        /// Starts the tween.
        /// </summary>
        public Tween<T> Start()
        {
            if (_isPlaying || _duration <= 0f)
                return this;

            _progress = 0f;
            _direction = 1f;
            _isPlaying = true;

            TweenManager.Instance.RegisterTween(this);
            return this;
        }

        /// <summary>
        /// Stops the tween and removes it from the manager.
        /// </summary>
        public Tween<T> Stop()
        {
            if (!_isPlaying)
                return this;

            _isPlaying = false;
            TweenManager.Instance.UnregisterTween(this);
            return this;
        }

        /// <summary>
        /// Pauses the tween without stopping it completely.
        /// </summary>
        public Tween<T> Pause()
        {
            _isPlaying = false;
            return this;
        }

        /// <summary>
        /// Resumes the tween from where it was paused.
        /// </summary>
        public Tween<T> Resume()
        {
            if (_isPlaying || _progress == 0f)
                return this;

            _isPlaying = true;
            return this;
        }

        /// <summary>
        /// Restarts the tween from the beginning.
        /// </summary>
        public Tween<T> Restart()
        {
            _progress = 0f;
            _direction = 1f;
            return Start();
        }

        /// <summary>
        /// Sets whether the tween should be automatically released when it finishes.
        /// </summary>
        public Tween<T> AutoRelease(bool enabled)
        {
            _autoRelease = enabled;
            return this;
        }

        /// <summary>
        /// Attaches the tween to a specific parent object.
        /// </summary>
        /// <param name="parent">The parent object to attach to.</param>
        public Tween<T> AttachTo(Object parent)
        {
            if (parent == null)
                throw new ArgumentNullException(nameof(parent));

            var prevParent = _parent;
            _parent = parent;
            TweenManager.Instance.AssignTweenParent(this, prevParent);
            return this;
        }

        /// <summary>
        /// Sets whether the tween should be released when its parent is destroyed.
        /// </summary>
        public Tween<T> AutoReleaseWithParent(bool enabled)
        {
            _autoReleaseWithParent = enabled;
            return this;
        }

        /// <summary>
        /// Releases the tween, stopping and resetting it.
        /// </summary>
        public void Release(TweenResetBehavior behavior = TweenResetBehavior.KeepCurrentValue)
        {
            if (!_isPlaying)
                return;
 
            Stop();
            Reset(behavior);
        }
        
        /// <summary>
        /// Resets the tween to its default state and updates its value based on the provided behavior.
        /// Depending on the <paramref name="behavior"/>, it can either reset to the start or end value.
        /// </summary>
        public void Reset(TweenResetBehavior behavior = TweenResetBehavior.KeepCurrentValue)
        {
            // Only reset if the tween has already played and has an update handler
            if (_progress == 0f || _onUpdate == null)
                return;

            // Apply the specified behavior to reset the value before resetting the tween
            if (behavior == TweenResetBehavior.ResetToStartValue)
                _onUpdate?.Invoke(_startValue);
            else if (behavior == TweenResetBehavior.ResetToEndValue)
                _onUpdate?.Invoke(_endValue);

            _progress = 0f;
            _direction = 1f;
            _isPlaying = false;
            _duration = 1f;
            _loopCount = 0;
            _yoyo = false;
            _parent = null;
            _autoRelease = false;
            _autoReleaseWithParent = false;
            _unscaledTime = false;
            _easeType = EaseType.SineInOut;
            _onUpdate = null;
            _onComplete = null;
        }

        /// <summary>
        /// Updates the tween over time.
        /// </summary>
        public void Tick()
        {
            if (!_isPlaying)
                return;

            float deltaTime = _unscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

            if (_delay > 0f)
            {
                _delay -= deltaTime;
                return;
            }

            if (_autoReleaseWithParent && _parent == null)
            {
                Release();
                return;
            }

            _progress += (_direction * deltaTime) / _duration;
            bool didReachEnd = false;

            if (_progress >= 1f)
            {
                _progress = 1f;
                if (_yoyo) _direction = -1f;
                else didReachEnd = true;
            }
            else if (_yoyo && _progress <= 0f)
            {
                _progress = 0f;
                _direction = 1f;
                didReachEnd = true;
            }

            _onUpdate?.Invoke(GetCurrentValue());

            if (didReachEnd)
            {
                if (_loopCount > 0)
                {
                    _progress = 0f;
                    _loopCount--;
                }
                else
                {
                    Stop();
                    _onComplete?.Invoke();
                    if (_autoRelease) Release();
                }
            }
        }

        /// <summary>
        /// Sets the end value of the tween.
        /// </summary>
        /// <param name="value">The end value to set.</param>
        /// <param name="overwriteFrom">If true and the tween has already started, the start value will be set to the current value.</param>
        public Tween<T> SetEndValue(T value, bool overwriteFrom = true)
        {
            if (overwriteFrom && _progress > 0.001f)
                _startValue = GetCurrentValue();

            _endValue = value;
            _progress = 0f;
            return this;
        }

        /// <summary>
        /// Sets the start value of the tween.
        /// </summary>
        /// <param name="value">The start value to set.</param>
        public Tween<T> SetStartValue(T value)
        {
            _startValue = value;
            return this;
        }

        /// <summary>
        /// Sets the duration of the tween.
        /// </summary>
        /// <param name="duration">The duration in seconds. Must be greater than 0.</param>
        public Tween<T> SetDuration(float duration)
        {
            _duration = Mathf.Max(duration, 0.001f);
            return this;
        }

        /// <summary>
        /// Sets the delay before the tween starts.
        /// </summary>
        /// <param name="delay">The delay in seconds. Must be 0 or greater.</param>
        public Tween<T> SetDelay(float delay)
        {
            _delay = Mathf.Max(delay, 0f);
            return this;
        }

        /// <summary>
        /// Sets the progress of the tween manually.
        /// </summary>
        /// <param name="time">A value between 0 (start) and 1 (end).</param>
        /// <param name="instantUpdate">If true, updates the tween's value immediately if it is playing.</param>
        public Tween<T> SetProgress(float time, bool instantUpdate = false)
        {
            _progress = Mathf.Clamp01(time);

            if (instantUpdate && _isPlaying)
                _onUpdate?.Invoke(GetCurrentValue());

            return this;
        }

        /// <summary>
        /// Sets the number of times the tween should loop.
        /// </summary>
        /// <param name="loopCount">The number of loops (0 for no looping).</param>
        /// <param name="yoyo">If true, the tween will reverse direction each loop.</param>
        public Tween<T> SetLoops(int loopCount, bool yoyo = false)
        {
            _loopCount = Mathf.Max(loopCount, 0);
            _yoyo = yoyo;
            return this;
        }

        /// <summary>
        /// Sets the easing function for the tween.
        /// </summary>
        /// <param name="ease">The easing type to use.</param>
        public Tween<T> SetEasing(EaseType ease)
        {
            _easeType = ease;
            return this;
        }

        /// <summary>
        /// Sets whether the tween should use unscaled time.
        /// </summary>
        /// <param name="enabled">If true, the tween will ignore time scaling.</param>
        public Tween<T> SetUnscaledTime(bool enabled)
        {
            _unscaledTime = enabled;
            return this;
        }

        /// <summary>
        /// Registers an action to be called when the tween completes.
        /// </summary>
        /// <param name="action">The action to invoke on completion.</param>
        public Tween<T> OnComplete(Action action)
        {
            if (action == null)
                return this;

            _onComplete += action;
            return this;
        }

        /// <summary>
        /// Registers an action to be called when the tween updates.
        /// </summary>
        /// <param name="action">The action to invoke on update, receiving the current tween value.</param>
        public Tween<T> OnUpdate(Action<T> action)
        {
            if (action == null)
                return this;

            _onUpdate += action;
            return this;
        }

        /// <summary>
        /// Gets the total time the tween will run, including delays and loops.
        /// </summary>
        public float GetTotalTime()
        {
            float duration = _duration + _delay;

            if (_loopCount > 0)
                duration *= _loopCount;

            if (_yoyo)
                duration *= 2f;

            return duration;
        }

        /// <summary>
        /// Waits for the tween to complete before continuing execution.
        /// </summary>
        public IEnumerator WaitForCompletion()
        {
            while (IsPlaying())
                yield return null;
        }

        /// <summary>
        /// Interpolates between the start and end values based on progress.
        /// Must be implemented by derived classes.
        /// </summary>
        /// <param name="startValue">The starting value.</param>
        /// <param name="endValue">The ending value.</param>
        /// <param name="progress">The normalized progress of the tween (0 to 1).</param>
        protected abstract T InterpolateValue(in T startValue, in T endValue, float progress);
    }
}