using System.Runtime.CompilerServices;
using UnityEngine.UI;
using UnityEngine;
using System;
using JetBrains.Annotations;

namespace PolymindGames.UserInterface
{
    [Serializable]
    [UsedImplicitly]
    public sealed class ColorTintFeedback : SelectableButtonFeedback
    {
        [SerializeField, UnityEngine.NotNull]
        private Graphic _targetGraphic;

        [SerializeField]
        private Color _normalColor = Color.grey;

        [SerializeField]
        private Color _highlightedColor = Color.grey;

        [SerializeField]
        private Color _pressedColor = Color.grey;

        [SerializeField]
        private Color _selectedColor = Color.grey;

        [SerializeField]
        private Color _disabledColor = Color.grey;

        [SerializeField, Range(0.01f, 1f)]
        private float _fadeDuration = 0.1f;

        public override void OnNormal(bool instant) => SetColor(_normalColor, instant);
        public override void OnHighlighted(bool instant) => SetColor(_highlightedColor, instant);
        public override void OnSelected(bool instant) => SetColor(_selectedColor, instant);
        public override void OnPressed(bool instant) => SetColor(_pressedColor, instant);
        public override void OnDisabled(bool instant) => SetColor(_disabledColor, instant);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetColor(in Color color, bool instant)
        {
            _targetGraphic.CrossFadeColor(color, instant ? 0f : _fadeDuration, true, true);
        }

        public override void Initialize(SelectableButton selectable)
        {
            base.Initialize(selectable);
            SetColor(selectable.IsInteractable ? _normalColor : _disabledColor, true);
        }

        #region Editor
#if UNITY_EDITOR
        public override void Validate_EditorOnly(SelectableButton selectable)
        {
            if (_targetGraphic == null || !_targetGraphic.transform.IsChildOf(selectable.transform))
                _targetGraphic = selectable.GetComponentInChildren<Graphic>();

            Initialize(selectable);
        }
#endif
        #endregion
    }
}