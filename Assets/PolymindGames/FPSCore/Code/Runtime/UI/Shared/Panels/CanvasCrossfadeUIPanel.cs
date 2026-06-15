using PolymindGames.ProceduralMotion;
using PolymindGames.UserInterface;
using UnityEngine;

namespace PolymindGames
{
    public class CanvasCrossfadeUIPanel : CanvasUIPanel
    {
        [SerializeField, Title("Animation")]
        private bool _useUnscaledTime = false;

        [SerializeField]
        private EaseType _easeType = EaseType.SineInOut;

        [SerializeField, Range(0f, 5f)]
        private float _showDuration = 0.3f;

        [SerializeField, Range(0f, 5f)]
        private float _hideDuration = 0.3f;
        
        protected override void OnVisibilityChanged(bool show)
        {
            base.OnVisibilityChanged(show);
            
            CanvasGroup.ClearTweens();
            CanvasGroup.TweenCanvasGroupAlpha(show ? 1f : 0f, show ? _showDuration : _hideDuration)
                .SetEasing(_easeType)
                .SetUnscaledTime(_useUnscaledTime);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            CanvasGroup.ClearTweens();
        }
    }
}
