using UnityEngine;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Interface for controlling animation parameters in a Unity animator without accessing data.
    /// </summary>
    public interface IAnimatorController
    {
        /// <summary>
        /// Gets or sets a value indicating whether the animator is currently animating.
        /// </summary>
        bool IsAnimating { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the geometry controlled by the animator is visible.
        /// </summary>
        bool IsVisible { get; set; }

        /// <summary>
        /// Sets a floating-point parameter in the animator identified by its ID.
        /// </summary>
        /// <param name="id">The ID of the parameter.</param>
        /// <param name="value">The value to set.</param>
        void SetFloat(int id, float value);

        /// <summary>
        /// Sets a boolean parameter in the animator identified by its ID.
        /// </summary>
        /// <param name="id">The ID of the parameter.</param>
        /// <param name="value">The value to set.</param>
        void SetBool(int id, bool value);

        /// <summary>
        /// Sets an integer parameter in the animator identified by its ID.
        /// </summary>
        /// <param name="id">The ID of the parameter.</param>
        /// <param name="value">The value to set.</param>
        void SetInteger(int id, int value);

        /// <summary>
        /// Triggers an animation in the animator identified by its ID.
        /// </summary>
        /// <param name="id">The ID of the trigger.</param>
        void SetTrigger(int id);

        /// <summary>
        /// Resets an animation trigger in the animator identified by its ID.
        /// </summary>
        /// <param name="id">The ID of the trigger.</param>
        void ResetTrigger(int id);
    }

    public static class AnimatorExtensions
    {
        public static void SetParameter(this IAnimatorController animator, AnimatorControllerParameterType paramType, int hash, float value)
        {
            switch (paramType)
            {
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(hash, value);
                    break;
                case AnimatorControllerParameterType.Trigger:
                    animator.SetTrigger(hash);
                    break;
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(hash, value > 0f);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(hash, (int)value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public static void SetFloat(this IAnimatorController animator, string param, float value)
        {
            int id = Animator.StringToHash(param);
            animator.SetFloat(id, value);
        }

        public static void SetBool(this IAnimatorController animator, string param, bool value)
        {
            int id = Animator.StringToHash(param);
            animator.SetBool(id, value);
        }

        public static void SetInteger(this IAnimatorController animator, string param, int value)
        {
            int id = Animator.StringToHash(param);
            animator.SetInteger(id, value);
        }

        public static void SetTrigger(this IAnimatorController animator, string param)
        {
            int id = Animator.StringToHash(param);
            animator.SetTrigger(id);
        }

        public static void ResetTrigger(this IAnimatorController animator, string param)
        {
            int id = Animator.StringToHash(param);
            animator.ResetTrigger(id);
        }
    }

    public sealed class NullAnimator : IAnimatorController
    {
        public static readonly NullAnimator Instance = new();

        public bool IsAnimating { get => false; set { } }
        public bool IsVisible  { get => false; set { } }
        public void SetFloat(int id, float value) { }
        public void SetBool(int id, bool value) { }
        public void SetInteger(int id, int value) { }
        public void SetTrigger(int id) { }
        public void ResetTrigger(int id) { }
    }

    public sealed class MultiAnimator : IAnimatorController
    {
        private readonly IAnimatorController[] _animators;

        public MultiAnimator(IAnimatorController[] animators)
        {
            _animators = animators;
        }

        public bool IsAnimating
        {
            get => _animators.Length > 0 && _animators[0].IsAnimating;
            set
            {
                foreach (var animator in _animators)
                    animator.IsAnimating = value;
            }
        }

        public bool IsVisible
        {
            get => _animators.Length > 0 && _animators[0].IsVisible;
            set
            {
                foreach (var animator in _animators)
                    animator.IsVisible = value;
            }
        }

        public void SetFloat(int id, float value)
        {
            foreach (var animator in _animators)
                animator.SetFloat(id, value);
        }

        public void SetBool(int id, bool value)
        {
            foreach (var animator in _animators)
                animator.SetBool(id, value);
        }

        public void SetInteger(int id, int value)
        {
            foreach (var animator in _animators)
                animator.SetInteger(id, value);
        }

        public void SetTrigger(int id)
        {
            foreach (var animator in _animators)
                animator.SetTrigger(id);
        }

        public void ResetTrigger(int id)
        {
            foreach (var animator in _animators)
                animator.ResetTrigger(id);
        }
    }
}