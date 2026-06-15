using System;
using PolymindGames.ProceduralMotion;
using UnityEngine;
using UnityEngine.UI;

namespace PolymindGames.UserInterface
{
    public sealed class AdvancedCrosshairUI : CrosshairBehaviourUI
    {
        [SerializeField, Range(0f, 1000f), Title("Size")]
        [Tooltip("Maximum size of the crosshair based on accuracy.")]
        private float _maxAccuracySize;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("Minimum size of the crosshair based on accuracy.")]
        private float _minAccuracySize;

        [SerializeField, Title("Kicking")]
        [Tooltip("Ease type for the crosshair kick animation.")]
        private EaseType _kickEase = EaseType.QuadInOut;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("Threshold for crosshair kick.")]
        private float _kickThreshold = 0.1f;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("Size of the crosshair kick.")]
        private float _kickSize = 35;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("Duration of the crosshair kick animation.")]
        private float _kickDuration = 0.35f;

        private RectTransform _rectTransform;
        private Image[] _crosshairImages;
        private float _lastAccuracy = 1f;
        private float _kickTime;
        private float _scale = 1f;
        private float _size = 1f;

        public override void SetSize(float accuracy, float scale)
        {
            float size = Mathf.LerpUnclamped(_minAccuracySize, _maxAccuracySize, Mathf.Clamp01(accuracy)) + GetKick(accuracy);

            if (Math.Abs(_size - size) > 0.001f)
            {
                _rectTransform.sizeDelta = new Vector2(size, size);
                _size = size;
            }

            if (Math.Abs(_scale - scale) > 0.001f)
            {
                _rectTransform.localScale = new Vector3(scale, scale, scale);
                _scale = scale;
            }
        }

        public override void SetColor(Color color)
        {
            foreach (Image image in _crosshairImages)
                image.color = color;
        }

        private float GetKick(float accuracy)
        {
            if (_lastAccuracy - accuracy > _kickThreshold)
                _kickTime = 1f;

            _kickTime = Mathf.Clamp01(_kickTime - Time.deltaTime * (1 / _kickDuration));
            _lastAccuracy = accuracy;
            return _kickSize * Easer.Apply(_kickEase, _kickTime);
        }

        private void Awake()
        {
            _rectTransform = GetComponent<RectTransform>();
            _crosshairImages = GetComponentsInChildren<Image>();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            UnityUtility.SafeOnValidate(this, () => ((RectTransform)transform).sizeDelta = new Vector2(_maxAccuracySize, _maxAccuracySize));
        }
#endif
    }
}