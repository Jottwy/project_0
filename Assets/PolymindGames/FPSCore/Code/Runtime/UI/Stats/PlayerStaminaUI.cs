using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/user-interface/behaviours/ui_vitals#player-stamina-ui")]
    public sealed class PlayerStaminaUI : CharacterUIBehaviour
    {
        [SerializeField]
        [Tooltip("The canvas group used to fade the stamina bar in & out.")]
        private CanvasGroup _canvasGroup;

        [SerializeField]
        [Tooltip("The stamina bar image, the fill amount will be modified based on the current stamina value.")]
        private Image _staminaBar;

        [SerializeField, Line]
        private bool _hideAfterDelay;
        
        [SerializeField, Range(1f, 10f)]
        [ShowIf(nameof(_hideAfterDelay), true)]
        [Tooltip("Represents how much time it takes for the stamina bar to start fading after not decreasing.")]
        private float _hideDuration = 4f;

        [SerializeField, Range(0f, 25f)]
        [ShowIf(nameof(_hideAfterDelay), true)]
        [Tooltip("How fast will the stamina bar alpha fade in & out.")]
        private float _alphaLerpSpeed = 4f;

        private IStaminaManagerCC _stamina;
        private float _lastStaminaValue = 1f;
        private float _hideTime;

        protected override void OnCharacterAttached(ICharacter character)
        {
            _stamina = Character.GetCC<IStaminaManagerCC>();
            if (_stamina == null)
            {
                Debug.LogWarning("[PlayerStaminaUI] No IStaminaManagerCC found on character.");
                return;
            }

            if (!_hideAfterDelay)
            {
                _canvasGroup.alpha = 1f;
            }
        }

        private void FixedUpdate()
        {
            if (_stamina == null)
                return;

            float stamina = _stamina.Stamina;
            _staminaBar.fillAmount = stamina / _stamina.MaxStamina;

            if (_hideAfterDelay)
            {
                float targetAlpha = _hideTime > Time.time ? 1f : 0f;
                _canvasGroup.alpha = Mathf.Lerp(_canvasGroup.alpha, targetAlpha, _alphaLerpSpeed * Time.fixedDeltaTime);

                if (stamina < _lastStaminaValue)
                    _hideTime = Time.time + _hideDuration;

                _lastStaminaValue = stamina;
            }
        }
    }
}