using UnityEngine;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(Animator))]
    [AddComponentMenu("Polymind Games/User Interface/Panels/Animated Panel")]
    public class AnimatedUIPanel : CanvasUIPanel
    {
        [SerializeField, Range(0f, 10f), Title("Animation")]
        private float _showSpeed = 1f;

        [SerializeField, Range(0f, 10f)]
        private float _hideSpeed = 1f;
        
        private static readonly int _animHide = Animator.StringToHash("Hide");
        private static readonly int _animHideSpeed = Animator.StringToHash("Hide Speed");
        private static readonly int _animShow = Animator.StringToHash("Show");
        private static readonly int _animShowSpeed = Animator.StringToHash("Show Speed");

        private Coroutine _hideRoutine;
        private Animator _animator;

        private const float DisableAnimatorDelay = 1f;

        protected override void OnVisibilityChanged(bool show)
        {
            base.OnVisibilityChanged(show);

            bool rebind = _animator.enabled == false;
            
            CoroutineUtility.StopCoroutine(this, ref _hideRoutine);
            _animator.enabled = true;
            
            if (!show)
            {
                _animator.SetFloat(_animHideSpeed, _hideSpeed);
                _animator.SetTrigger(_animHide);
                _hideRoutine = CoroutineUtility.InvokeDelayed(this, DisableAnimator, DisableAnimatorDelay);
            }
            else
            {
                _animator.SetFloat(_animShowSpeed, _showSpeed);
                _animator.SetTrigger(_animShow);
                
                if (rebind)
                    _animator.Rebind();
            }
        }

        protected override void Awake()
        {
            base.Awake();

            _animator = GetComponent<Animator>();
            _animator.fireEvents = false;
            _animator.keepAnimatorStateOnDisable = true;
            _animator.writeDefaultValuesOnDisable = false;
            _animator.enabled = false;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            CoroutineUtility.StopCoroutine(this, ref _hideRoutine);
        }

        private void DisableAnimator()
        {
            _animator.enabled = false;
            _hideRoutine = null;
        }

#if UNITY_EDITOR
        protected override void Reset()
        {
            base.Reset();
            _animator = GetComponent<Animator>();
        }
#endif
    }
}