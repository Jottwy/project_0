using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Wieldable Animator")]
    public sealed class WieldableAnimator : MonoBehaviour, IAnimatorController
    {
        [SerializeField]
        [Tooltip("The Animator component.")]
        private Animator _animator;

        [SerializeField, SpaceArea]
        [Tooltip("The animation override clips.")]
        private AnimationOverrideClips _clips;

        private Renderer[] _renderers;
        private float _holsterSpeed;

        public AnimationOverrideClips OverrideClips => _clips;
        public Animator Animator => _animator;

        public bool IsAnimating
        {
            get => _animator.speed != 0f;
            set => _animator.speed = value ? 1f : 0f;
        }

        public bool IsVisible
        {
            get => true;
            set { }
        }

        public void SetFloat(int id, float value)
            => _animator.SetFloat(id, id == AnimationConstants.HolsterSpeed ? _holsterSpeed * value : value);

        public void SetBool(int id, bool value) => _animator.SetBool(id, value);
        public void SetInteger(int id, int value) => _animator.SetInteger(id, value);
        public void SetTrigger(int id) => _animator.SetTrigger(id);
        public void ResetTrigger(int id) => _animator.ResetTrigger(id);

        private void Awake()
        {
            if (_clips.Controller != null)
                _animator.runtimeAnimatorController = _clips.OverrideController;
            
            _holsterSpeed = GetHolsterSpeed();
        }

        private void OnEnable()
        {
            foreach (var parameter in _clips.DefaultParameters)
                parameter.TriggerParameter(_animator);
        }

        private float GetHolsterSpeed()
        {
            var defaultParams = _clips.DefaultParameters;
            foreach (var trigger in defaultParams)
            {
                if (trigger.Hash == AnimationConstants.HolsterSpeed)
                    return trigger.Value;
            }

            return 1f;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();

            if (_clips != null && _clips.Controller != null)
            {
                var controller = _clips.Controller;
                if (_animator != null)
                    _animator.runtimeAnimatorController = controller;
            }

            if (Application.isPlaying)
                _holsterSpeed = GetHolsterSpeed();
        }
#endif
    }
}