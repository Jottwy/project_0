using System;
using TMPro;
using UnityEngine;

namespace PolymindGames
{
    [RequireComponent(typeof(TextMeshProUGUI))]
    public sealed class TMPTextSizer : MonoBehaviour
    {
        [SerializeField]
        private bool _resizeTextObject = true;

        [SerializeField]
        private Vector2 _padding;

        [SerializeField]
        private Vector2 _maxSize = new(1000, float.PositiveInfinity);

        [SerializeField]
        private Vector2 _minSize;

        [SerializeField]
        private Mode _controlAxes = Mode.Both;
        
        private RectTransform _textRectTransform;
        private RectTransform _selfRectTransform;
        private TextMeshProUGUI _text;
        private Mode _lastControlAxes;
        private bool _forceRefresh;
        private Vector2 _lastSize;
        private string _lastText;

        private float MinX
        {
            get
            {
                if ((_controlAxes & Mode.Horizontal) != 0) return _minSize.x;
                return _selfRectTransform.rect.width - _padding.x;
            }
        }

        private float MinY
        {
            get
            {
                if ((_controlAxes & Mode.Vertical) != 0) return _minSize.y;
                return _selfRectTransform.rect.height - _padding.y;
            }
        }

        private float MaxX
        {
            get
            {
                if ((_controlAxes & Mode.Horizontal) != 0) return _maxSize.x;
                return _selfRectTransform.rect.width - _padding.x;
            }
        }

        private float MaxY
        {
            get
            {
                if ((_controlAxes & Mode.Vertical) != 0) return _maxSize.y;
                return _selfRectTransform.rect.height - _padding.y;
            }
        }

        // Forces a size recalculation on next Update
        public void Refresh()
        {
            _forceRefresh = true;
            _textRectTransform = _text.rectTransform;
            _selfRectTransform = GetComponent<RectTransform>();
        }

        private void Awake() => _text = GetComponent<TextMeshProUGUI>();
        private void OnEnable() => Refresh();

        private void Update()
        {
            if (_forceRefresh
                || _text.text != _lastText
                || _lastSize != _selfRectTransform.rect.size
                || _controlAxes != _lastControlAxes)
            {
                Vector2 preferredSize = _text.GetPreferredValues(MaxX, MaxY);
                preferredSize.x = Mathf.Clamp(preferredSize.x, MinX, MaxX);
                preferredSize.y = Mathf.Clamp(preferredSize.y, MinY, MaxY);
                preferredSize += _padding;

                if ((_controlAxes & Mode.Horizontal) != 0)
                {
                    _selfRectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, preferredSize.x);
                    if (_resizeTextObject)
                        _textRectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, preferredSize.x);
                }
                if ((_controlAxes & Mode.Vertical) != 0)
                {
                    _selfRectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, preferredSize.y);
                    if (_resizeTextObject)
                        _textRectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, preferredSize.y);
                }

                _lastText = _text.text;
                _lastSize = _selfRectTransform.rect.size;
                _lastControlAxes = _controlAxes;
                _forceRefresh = false;
            }
        }

#if UNITY_EDITOR
        private void OnValidate() => _forceRefresh = true;
#endif
        #region Internal Types
        [Flags]
        private enum Mode
        {
            None = 0,
            Horizontal = 0x1,
            Vertical = 0x2,
            Both = Horizontal | Vertical
        }
        #endregion
    }
}