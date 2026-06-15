using System;
using JetBrains.Annotations;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [Serializable]
    [UsedImplicitly]
    public sealed class SpriteSwapFeedback : SelectableButtonFeedback
    {
        [SerializeField, UnityEngine.NotNull]
        private Image _image;

        [SerializeField, HideInInspector]
        private Sprite _normalSprite;

        [SerializeField]
        private Sprite _highlightedSprite;

        [SerializeField]
        private Sprite _selectedSprite;

        [SerializeField]
        private Sprite _pressedSprite;
        
        public override void OnNormal(bool instant) => _image.sprite = _normalSprite;
        public override void OnHighlighted(bool instant) => _image.sprite = _highlightedSprite;
        public override void OnSelected(bool instant) => _image.sprite = _selectedSprite;
        public override void OnPressed(bool instant) => _image.sprite = _pressedSprite;

#if UNITY_EDITOR
        public override void Validate_EditorOnly(SelectableButton selectable)
        {
            if (_image == null)
                _image = selectable.GetComponent<Image>();
        }
#endif
    }
}