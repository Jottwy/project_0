using PolymindGames.WieldableSystem;
using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class FirearmModeUI : CharacterUIBehaviour
    {
        [SerializeField]
        [Tooltip("A UI text component that's used for displaying the currently selected fire mode.")]
        private TextMeshProUGUI _modeNameText;

        [SerializeField]
        private Image _modeIconImage;

        [SerializeField, Title("Animation")]
        private Animation _animation;
        
        [SerializeField]
        [Tooltip("The animation for showing the fire mode UI.")]
        private AnimationClip _showAnimation;

        [SerializeField]
        [Tooltip("The animation for updating the fire mode UI.")]
        private AnimationClip _updateAnimation;

        [SerializeField]
        [Tooltip("The animation for hiding the fire mode UI.")]
        private AnimationClip _hideAnimation;

        private IWieldablesControllerCC _wieldablesController;
        private IFirearmIndexModeHandler _modesHandler;
        private bool _isAnimationActive;
        
        protected override void OnCharacterAttached(ICharacter character)
        {
            _wieldablesController = character.GetCC<IWieldablesControllerCC>();
            _wieldablesController.EquippingStopped += OnWieldablesEquipped;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            if (_modesHandler != null)
            {
                _modesHandler.ModeChanged -= OnSelectedModChanged;
                _modesHandler = null;
            }
        }

        private void OnWieldablesEquipped(IWieldable wieldable)
        {
            if (_modesHandler != null)
            {
                _modesHandler.ModeChanged -= OnSelectedModChanged;
                _modesHandler = null;
            }

            if (wieldable?.gameObject != null &&
                wieldable.gameObject.TryGetComponent<IFirearmIndexModeHandler>(out var newModesHandler))
            {
                _modesHandler = newModesHandler;
                _modesHandler.ModeChanged += OnSelectedModChanged;
                SetDisplayedMod(_modesHandler.CurrentMode);

                if (!_isAnimationActive)
                {
                    _animation.PlayClip(_showAnimation);
                    _isAnimationActive = true;
                }
            }
            else
            {
                if (_isAnimationActive)
                {
                    _animation.PlayClip(_hideAnimation);
                    _isAnimationActive = false;
                }
            }
        }

        private void OnSelectedModChanged(FirearmAttachment attachment)
        {
            SetDisplayedMod(attachment);
            _animation.PlayClip(_updateAnimation);
        }

        private void SetDisplayedMod(FirearmAttachment attachment)
        {
            if (_modeNameText != null)
            {
                _modeNameText.text = attachment.Name;
            }

            if (_modeIconImage != null)
            {
                _modeIconImage.sprite = attachment.Icon;
                transform.localScale = Vector3.one * attachment.IconSize;
            }
        }
    }
}