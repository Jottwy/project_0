using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(CanvasGroup))]
    [DefaultExecutionOrder(-1)]
    public sealed class ScopeUI : MonoBehaviour
    {
        private CanvasGroup _canvasGroup;
        
        public void Show(float showDuration)
        {
            gameObject.SetActive(true);
            _canvasGroup.ClearTweens();
            _canvasGroup.TweenCanvasGroupAlpha(1f, showDuration)
                .SetEasing(EaseType.CubicIn);
        }

        public void Hide(float hideDuration)
        {
            _canvasGroup.ClearTweens();
            _canvasGroup.TweenCanvasGroupAlpha(0f, hideDuration)
                .SetEasing(EaseType.CubicIn)
                .OnComplete(() => gameObject.SetActive(false));
        }

        public void SetZoomLevel(int level) { }

        private void Awake() => _canvasGroup = GetComponent<CanvasGroup>();
        private void OnDestroy() => _canvasGroup.ClearTweens();
    }
}