using System;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu("Polymind Games/Wieldables/Wieldable Movement Animation")]
    public class WieldableMovementAnimation : MonoBehaviour
    {
        [SerializeField]
        [ReorderableList, LabelByChild(nameof(AnimationTrigger.State))]
        private AnimationTrigger[] _animationTriggers;

        private IMovementControllerCC _movement;
        private IWieldable _wieldable;

        private void Awake()
        {
            _wieldable = GetComponentInParent<IWieldable>();
            foreach (var trigger in _animationTriggers)
                trigger.Initialize(_wieldable.Animator);
        }

        private void OnEnable()
        {
            if (_wieldable.Character.TryGetCC(out _movement))
            {
                AnimationTrigger activeAnim = null;
                var activeState = _movement.ActiveState;
                foreach (var anim in _animationTriggers)
                {
                    _movement.AddStateTransitionListener(anim.State, anim.Trigger, anim.Transition);

                    if (anim.State == activeState && anim.Transition == MovementStateTransitionType.Enter)
                        activeAnim = anim;
                }

                if (activeAnim != null)
                    CoroutineUtility.InvokeDelayed(this, () => activeAnim.Trigger(activeAnim.State), 0.1f);
            }
        }

        private void OnDisable()
        {
            if (_wieldable.Character.TryGetCC(out _movement))
            {
                foreach (var anim in _animationTriggers)
                    _movement.RemoveStateTransitionListener(anim.State, anim.Trigger, anim.Transition);
            }
        }

        #region Internal Types
        [Serializable]
        private sealed class AnimationTrigger
        {
            public MovementStateType State;
            public MovementStateTransitionType Transition;

            [SerializeField, ReorderableList()]
            private AnimatorParameterTrigger[] _parameters = Array.Empty<AnimatorParameterTrigger>();

            private IAnimatorController _animator;

            public void Initialize(IAnimatorController animator) => _animator = animator;

            public void Trigger(MovementStateType stateType)
            {
                foreach (var parameter in _parameters)
                    _animator.SetParameter(parameter.Type, parameter.Hash, parameter.Value);
            }
        }
        #endregion
    }
}