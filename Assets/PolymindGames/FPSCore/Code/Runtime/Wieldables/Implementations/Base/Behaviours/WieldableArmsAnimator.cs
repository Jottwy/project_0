using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Wieldable Arms Animator")]
    public sealed class WieldableArmsAnimator : MonoBehaviour, IAnimatorController
    {
        [SerializeField]
        private WieldableAnimator _wieldableAnimator;
        
        [SerializeField]
        [Tooltip("The animation override clips.")]
        private AnimationOverrideClips _clips;

        private IWieldableArmsHandlerCC _armsHandler; 
        private bool _wasGeometryVisible;
        private IWieldable _wieldable;
        private Animator _animator;
        private float _holsterSpeed;

        public bool IsAnimating
        {
            get => _animator.speed != 0f;
            set => _animator.speed = value ? 1f : 0f;
        }

        public bool IsVisible
        {
            get => _armsHandler.IsVisible;
            set => _armsHandler.IsVisible = value;
        }

        public void SetFloat(int id, float value)
            => _animator.SetFloat(id, id == AnimationConstants.HolsterSpeed ? _holsterSpeed * value : value);
        
        public void SetBool(int id, bool value) => _animator.SetBool(id, value);
        public void SetInteger(int id, int value) => _animator.SetInteger(id, value);
        public void SetTrigger(int id) => _animator.SetTrigger(id);
        public void ResetTrigger(int id) => _animator.ResetTrigger(id);

        private void Awake()
        {
            _wieldable = GetComponentInParent<IWieldable>();
            _armsHandler = _wieldable.Character.GetCC<IWieldableArmsHandlerCC>();
            _animator = _armsHandler.Animator;

            if (_armsHandler == null)
            {
                Debug.LogWarning("No arms handler character component found.");
                enabled = false;
                return;
            }

            _holsterSpeed = GetHolsterSpeed();
            _wasGeometryVisible = _wieldable.IsGeometryVisible;
            _armsHandler.IsVisible = _wasGeometryVisible;
        }

        private void LateUpdate()
        {
            bool isGeometryVisible = _wieldable.IsGeometryVisible;
            if (isGeometryVisible != _wasGeometryVisible)
            {
                _armsHandler.IsVisible = isGeometryVisible;
                _wasGeometryVisible = isGeometryVisible;
            }
        }

        private void OnEnable()
        {
            _armsHandler?.EnableArms();
            var defaultParameters = _wieldableAnimator != null ? _wieldableAnimator.OverrideClips.DefaultParameters : _clips.DefaultParameters;
            _animator.runtimeAnimatorController = _clips.OverrideController;
            foreach (var parameter in defaultParameters)
                parameter.TriggerParameter(_animator);
        }

        private void OnDisable()
        {
            _armsHandler?.DisableArms();
        }
        
        private float GetHolsterSpeed()
        {
            var defaultParameters = _wieldableAnimator != null ? _wieldableAnimator.OverrideClips.DefaultParameters : _clips.DefaultParameters;
            foreach (var parameter in defaultParameters)
            {
                if (parameter.Hash == AnimationConstants.HolsterSpeed)
                    return parameter.Value;
            }

            return 1f;
        }

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            if (_clips != null && TryGetComponent<WieldableAnimator>(out var animator))
                _clips.Controller = animator.OverrideClips.Controller;
        }

        private void OnValidate()
        {
            if (_wieldableAnimator == null)
                _wieldableAnimator = GetComponent<WieldableAnimator>();
            
            if (Application.isPlaying)
                _holsterSpeed = GetHolsterSpeed();
        }
#endif
        #endregion
    }
}