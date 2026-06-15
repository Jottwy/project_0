using UnityEngine;
using System;
using JetBrains.Annotations;
using TMPro;

namespace PolymindGames.UserInterface
{
    [Serializable]
    [UsedImplicitly]
    public sealed class TextFeedback : SelectableButtonFeedback
    {
        [SerializeField]
        private TextMeshProUGUI _text;

        [SerializeField, Range(0, 100)]
        private int _selectedFontSize = 15;

        [SerializeField]
        private Color _selectedTextColor = Color.white;

        private FontStyles _originalFontStyle;
        private Color _originalTextColor;
        private float _originalFontSize;

        public override void OnSelected(bool instant)
        {
            _text.fontStyle = FontStyles.Bold;
            _text.fontSize = _selectedFontSize;
            _text.color = _selectedTextColor;
        }

        public override void OnDeselected(bool instant)
        {
            _text.fontStyle = _originalFontStyle;
            _text.fontSize = _originalFontSize;
            _text.color = _originalTextColor;
        }

        public override void Initialize(SelectableButton selectable)
        {
            _originalFontSize = _text.fontSize;
            _originalTextColor = _text.color;
            _originalFontStyle = _text.fontStyle;
        }
    }
}