using BackroomsSurvival.Net;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.Gameplay
{
    public sealed class SanityEffects : MonoBehaviour
    {
        public bool enableDevFreezeSurvival;

        private Canvas _canvas;
        private Image _vignetteImage;
        private Image _distortionImage;
        private float _baseFov;
        private Camera _cam;
        private float _shakeTimer;
        private float _pulsePhase;

        private void Start()
        {
            enableDevFreezeSurvival = EnvFlagEnabled("DEV_FREEZE_SURVIVAL");
            if (enableDevFreezeSurvival)
                Debug.Log("DEV_FREEZE_SURVIVAL active: sanity visual effects disabled");

            _cam = Camera.main;
            if (_cam != null) _baseFov = _cam.fieldOfView;

            _canvas = new GameObject("SanityCanvas").AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 90;
            DontDestroyOnLoad(_canvas.gameObject);

            // Vignette overlay (dark edges)
            _vignetteImage = CreateOverlay("Vignette", new Color(0f, 0f, 0f, 0f));
            // Distortion tint (purple/red noise)
            _distortionImage = CreateOverlay("Distortion", new Color(0.3f, 0f, 0.2f, 0f));
        }

        private Image CreateOverlay(string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private void Update()
        {
            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null || state.localPlayer == null) return;

            if (enableDevFreezeSurvival)
            {
                ClearEffects();
                return;
            }

            float sanity = state.localPlayer.stats.sanity;
            _pulsePhase += Time.deltaTime;

            if (sanity >= 50f)
            {
                // Normal — no effects.
                ClearEffects();
                return;
            }

            // intensity: 0 at sanity=50, 1 at sanity=0.
            float intensity = 1f - (sanity / 50f);

            // Vignette: dark border grows with insanity.
            float vigAlpha = Mathf.Lerp(0f, 0.5f, intensity);
            _vignetteImage.color = new Color(0f, 0f, 0f, vigAlpha);

            // Purple distortion overlay pulses.
            float pulse = Mathf.Sin(_pulsePhase * 2f) * 0.5f + 0.5f;
            float distAlpha = Mathf.Lerp(0f, 0.15f, intensity) * Mathf.Lerp(0.7f, 1f, pulse);
            _distortionImage.color = new Color(0.3f, 0f, 0.2f, distAlpha);

            // Severe (sanity < 20): camera shake + FOV wobble.
            if (sanity < 20f && _cam != null)
            {
                float severe = 1f - (sanity / 20f);
                float shake = severe * 0.3f;
                _cam.transform.localRotation *= Quaternion.Euler(
                    Random.Range(-shake, shake),
                    Random.Range(-shake, shake),
                    0f);
                _cam.fieldOfView = _baseFov + Mathf.Sin(_pulsePhase * 3f) * severe * 5f;
            }
            else
            {
                ResetFov();
            }
        }

        private void ResetFov()
        {
            if (_cam != null && Mathf.Abs(_cam.fieldOfView - _baseFov) > 0.01f)
                _cam.fieldOfView = _baseFov;
        }

        private void ClearEffects()
        {
            if (_vignetteImage != null)
                _vignetteImage.color = new Color(0f, 0f, 0f, 0f);
            if (_distortionImage != null)
                _distortionImage.color = new Color(0.3f, 0f, 0.2f, 0f);
            ResetFov();
        }

        private static bool EnvFlagEnabled(string name)
        {
            string value = System.Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value == "1" ||
                   value.Equals("true", System.StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", System.StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("on", System.StringComparison.OrdinalIgnoreCase);
        }

        private void OnDestroy()
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
        }
    }
}
