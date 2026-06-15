using TMPro;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(TextMeshProUGUI))]
    public sealed class FPSCounterUI : MonoBehaviour
    {
        [SerializeField, Range(30f, 1000f)]
        private float _requiredFPS;

        [SerializeField]
        private Gradient _colorGradient;
        
        private int _currentFps;
        private int _fpsAccumulator;
        private float _fpsNextPeriod;
        private TextMeshProUGUI _text;

        private const float FPSMeasurePeriod = 0.5f;
        private const string Display = "{0} FPS";

        private void Start()
        {
            _fpsNextPeriod = Time.realtimeSinceStartup + FPSMeasurePeriod;
            _text = GetComponent<TextMeshProUGUI>();
        }

        private void Update()
        {
            // Measure average frames per second
            _fpsAccumulator++;

            if (Time.realtimeSinceStartup > _fpsNextPeriod)
            {
                _currentFps = (int)(_fpsAccumulator / FPSMeasurePeriod);
                _fpsAccumulator = 0;
                _fpsNextPeriod += FPSMeasurePeriod;

                _text.text = string.Format(Display, _currentFps);
                _text.color = _colorGradient.Evaluate(_currentFps / _requiredFPS);
            }
        }
    }
}