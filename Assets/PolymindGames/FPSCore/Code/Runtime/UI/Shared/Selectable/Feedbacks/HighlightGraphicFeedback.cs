using System;
using JetBrains.Annotations;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [Serializable]
    [UsedImplicitly]
    public sealed class HighlightGraphicFeedback : SelectableButtonFeedback
    {
        [SerializeField, UnityEngine.NotNull]
        private Image _selectionGraphic;

        [SerializeField]
        private Color _selectedColor;

        [SerializeField]
        private Color _highlightedColor;

        public override void OnNormal(bool instant)
        {
            _selectionGraphic.enabled = false;
        }

        public override void OnHighlighted(bool instant)
        {
            _selectionGraphic.enabled = true;
            _selectionGraphic.color = _highlightedColor;
        }

        public override void OnSelected(bool instant)
        {
            _selectionGraphic.enabled = true;
            _selectionGraphic.color = _selectedColor;
        }

#if UNITY_EDITOR
        public override void Validate_EditorOnly(SelectableButton selectable)
        {
            if (_selectionGraphic == null)
                _selectionGraphic = selectable.GetComponentInChildren<Image>();
        }
#endif
    }
}