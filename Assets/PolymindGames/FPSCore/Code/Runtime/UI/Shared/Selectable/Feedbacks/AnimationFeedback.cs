using UnityEngine;
using System;
using JetBrains.Annotations;

namespace PolymindGames.UserInterface
{
    [Serializable]
    [UsedImplicitly]
    public sealed class AnimationFeedback : SelectableButtonFeedback
    {
        [SerializeField, UnityEngine.NotNull]
        private Animator _animator;

        private static readonly int _disabled = Animator.StringToHash("Disabled");
        private static readonly int _highlight = Animator.StringToHash("Highlighted");
        private static readonly int _normal = Animator.StringToHash("Normal");
        private static readonly int _pressed = Animator.StringToHash("Pressed");
        private static readonly int _selected = Animator.StringToHash("Selected");
        
        public override void OnNormal(bool instant) => _animator.SetTrigger(_normal);
        public override void OnHighlighted(bool instant) => _animator.SetTrigger(_highlight);
        public override void OnSelected(bool instant) => _animator.SetTrigger(_selected);
        public override void OnPressed(bool instant) => _animator.SetTrigger(_pressed);
        public override void OnDisabled(bool instant) => _animator.SetTrigger(_disabled);

        #region Editor
#if UNITY_EDITOR
        public override void Validate_EditorOnly(SelectableButton selectable)
        {
            if (_animator == null)
                _animator = selectable.GetComponent<Animator>();
        }
#endif
        #endregion
    }
}