using UnityEngine;
using UnityEngine.UI;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(RectTransform))]
    public sealed class BasicCrosshairUI : CrosshairBehaviourUI
    {
        [SerializeField, Range(0f, 1000f), Title("Size")]
        [Tooltip("Maximum size of the crosshair based on accuracy.")]
        private float _maxAccuracySize;

        [SerializeField, Range(0f, 1000f)]
        [Tooltip("Minimum size of the crosshair based on accuracy.")]
        private float _minAccuracySize;

        private Image[] _crosshairImages;
        private RectTransform _rectTransform;

        public override void SetSize(float accuracy, float scale)
        {
            float size = Mathf.LerpUnclamped(_minAccuracySize, _maxAccuracySize, Mathf.Clamp01(accuracy));
            _rectTransform.sizeDelta = new Vector2(size, size);
            _rectTransform.localScale = new Vector3(scale, scale, scale);
        }

        public override void SetColor(Color color)
        {
            foreach (Image image in _crosshairImages)
                image.color = color;
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