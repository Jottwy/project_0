using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class ThrowablesUI : CharacterUIBehaviour
    {
        [SerializeField]
        private Image _throwableIcon;

        [SerializeField]
        private TextMeshProUGUI _throwableCountText;

        private IWieldableThrowableHandlerCC _throwableHandler;

        protected override void OnCharacterAttached(ICharacter character)
        {
            _throwableHandler = character.GetCC<IWieldableThrowableHandlerCC>();
            _throwableHandler.ThrowableIndexChanged += OnThrowableIndexChanged;
            _throwableHandler.ThrowableCountChanged += OnThrowableCountChanged;

            OnThrowableIndexChanged();
            OnThrowableCountChanged();
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            _throwableHandler.ThrowableIndexChanged -= OnThrowableIndexChanged;
            _throwableHandler.ThrowableCountChanged -= OnThrowableCountChanged;
            _throwableHandler = null;
        }

        private void OnThrowableIndexChanged()
        {
            var throwable = _throwableHandler.GetThrowableAtIndex(_throwableHandler.SelectedIndex);

            if (throwable != null)
                _throwableIcon.sprite = throwable.DisplayIcon != null ? throwable.DisplayIcon : null;
        }

        private void OnThrowableCountChanged()
        {
            int throwableCount = _throwableHandler.GetThrowableCountAtIndex(_throwableHandler.SelectedIndex);
            _throwableCountText.text = throwableCount.ToString();
        }
    }
}