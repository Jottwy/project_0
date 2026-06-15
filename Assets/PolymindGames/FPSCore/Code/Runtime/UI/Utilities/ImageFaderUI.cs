using PolymindGames.ProceduralMotion;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    [Serializable]
    public sealed class ImageFaderUI
    {
        [SerializeField, NotNull]
        private Image _image;

        [SerializeField]
        private EaseType _fadeEaseType;

        [SerializeField, Range(0f, 1f)]
        private float _minimumAlpha = 0.4f;

        [SerializeField, Range(0f, 100f)]
        private float _fadeInDuration = 0.3f;

        [SerializeField, Range(0f, 10f)]
        private float _fadePauseDuration = 0.5f;

        [SerializeField, Range(0f, 100f)]
        private float _fadeOutDuration = 0.3f;

        private Coroutine _fadeCoroutine;

        public bool IsCurrentlyFading { get; private set; }
        public float CurrentAlpha => _image.color.a;
        public Image Image => _image;

        public void StartFadeCycle(MonoBehaviour parent, float targetAlpha, int repeatCount = 1)
        {
            if (repeatCount < 1)
                return;

#if DEBUG
            if (_image == null)
                throw new NullReferenceException("[ImageFader] - The image to fade is not assigned!");
#endif

            targetAlpha = Mathf.Clamp(targetAlpha, _minimumAlpha, 1f);
            CoroutineUtility.StartOrReplaceCoroutine(parent, FadeCycleCoroutine(targetAlpha, repeatCount), ref _fadeCoroutine);
        }

        public void FadeTo(MonoBehaviour parent, float targetAlpha)
        {
#if DEBUG
            if (_image == null)
                throw new NullReferenceException("[ImageFader] - The image to fade is not assigned!");
#endif

            float fadeDuration = targetAlpha > _image.color.a ? _fadeOutDuration : _fadeInDuration;
            CoroutineUtility.StartOrReplaceCoroutine(parent, FadeToAlpha(targetAlpha, fadeDuration), ref _fadeCoroutine);
        }

        private IEnumerator FadeCycleCoroutine(float targetAlpha, int repeatCount)
        {
            IsCurrentlyFading = true;
            while (repeatCount-- > 0)
            {
                yield return FadeToAlpha(targetAlpha, _fadeInDuration);
                yield return new WaitForTime(_fadePauseDuration);
                float endAlpha = (repeatCount > 0) ? _minimumAlpha : 0f;
                yield return FadeToAlpha(endAlpha, _fadeOutDuration);
                yield return new WaitForTime(_fadePauseDuration);
            }
            IsCurrentlyFading = false;
        }

        private IEnumerator FadeToAlpha(float targetAlpha, float duration)
        {
            float startTime = Time.time;
            Color initialColor = _image.color;
            Color targetColor = initialColor.WithAlpha(targetAlpha);
            float fadeDuration = duration * Mathf.Abs(targetAlpha - initialColor.a);
            fadeDuration = Mathf.Max(0.01f, fadeDuration);

            float t = 0f;
            while (t < 1f)
            {
                t = (Time.time - startTime) / fadeDuration;
                _image.color = Color.Lerp(initialColor, targetColor, Easer.Apply(_fadeEaseType, Mathf.Clamp01(t)));
                yield return null;
            }

            _image.color = targetColor;
        }
    }
}