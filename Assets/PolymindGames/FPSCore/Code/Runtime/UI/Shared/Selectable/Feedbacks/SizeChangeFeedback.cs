using System;
using JetBrains.Annotations;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [Serializable]
    [UsedImplicitly]
    public sealed class SizeChangeFeedback : SelectableButtonFeedback
    {
        [SerializeField]
        private RectTransform _transform;
        
        [SerializeField, Range(0f, 10f)]
        private float _highlightedSize = 1f;
        
        [SerializeField, Range(0f, 10f)]
        private float _pressedSize = 1f;
        
        [SerializeField, Range(0f, 10f)]
        private float _selectedSize = 1f;

        public override void OnNormal(bool instant) => _transform.localScale = Vector3.one;
        public override void OnPressed(bool instant) => _transform.localScale = Vector3.one * _pressedSize;
        public override void OnHighlighted(bool instant) =>  _transform.localScale = Vector3.one * _highlightedSize;
        public override void OnSelected(bool instant) =>  _transform.localScale = Vector3.one * _selectedSize;
        
#if UNITY_EDITOR
        public override void Validate_EditorOnly(SelectableButton selectable)
        {
            if (_transform == null)
                _transform = selectable.GetComponent<RectTransform>();
        }
#endif
    }
}
