using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(CanvasGroup))]
    [AddComponentMenu("Polymind Games/User Interface/Panels/Panel")]
    public class CanvasUIPanel : UIPanel
    {
        [SerializeField, Title("Canvas")]
        private CanvasShowMode _canvasShowMode = CanvasShowMode.Everything;

        [SerializeField]
        private AudioData _showAudio = new(null);

        [SerializeField]
        private AudioData _hideAudio = new(null);
        
        private CanvasGroup _canvasGroup;

        protected CanvasGroup CanvasGroup => _canvasGroup;
        
        protected override void OnVisibilityChanged(bool show)
        {
            if ((_canvasShowMode & CanvasShowMode.EnableInteractable) == CanvasShowMode.EnableInteractable)
                _canvasGroup.interactable = show;

            if ((_canvasShowMode & CanvasShowMode.BlockRaycasts) == CanvasShowMode.BlockRaycasts)
                _canvasGroup.blocksRaycasts = show;

            if ((_canvasShowMode & CanvasShowMode.AlphaToOne) == CanvasShowMode.AlphaToOne)
                _canvasGroup.alpha = show ? 1f : 0f;
            
            AudioManager.Instance.PlayClip2D(show ? _showAudio : _hideAudio, 1f, AudioChannel.UI);
        }

        protected virtual void Awake()
        {
            _canvasGroup = GetComponent<CanvasGroup>();
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.alpha = 0f;
        }

#if UNITY_EDITOR
        protected virtual void Reset()
        {
            _canvasGroup = gameObject.GetOrAddComponent<CanvasGroup>();
        }
#endif
        
        #region Internal Types
        [Flags]
        private enum CanvasShowMode
        {
            None = 0,
            EnableInteractable = 1,
            BlockRaycasts = 2,
            AlphaToOne = 4,
            Everything = EnableInteractable | BlockRaycasts | AlphaToOne
        }
        #endregion
    }
}