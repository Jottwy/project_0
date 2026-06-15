using PolymindGames.InventorySystem;
using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(SelectableButton))]
    public sealed class ItemActionUI : MonoBehaviour
    {
        [SerializeField, NotNull]
        private SelectableButton _button;

        [SerializeField, NotNull]
        private Image _iconImage;

        [SerializeField, NotNull]
        private TextMeshProUGUI _nameText;

        private Coroutine _actionCoroutine;
        private ItemAction _itemAction;
        private ICharacter _character;
        private SlotReference _slot;

        public void SetAction(ItemAction itemAction, SlotReference itemSlot, ICharacter character)
        {
            // If nothing changed, return..
            if (_slot == itemSlot)
                return;
            
            _slot = itemSlot;
            if (itemSlot.IsValid())
            {
                _itemAction = itemAction;
                _character = character;
                _iconImage.sprite = _itemAction.ActionIcon;
                _nameText.text = _itemAction.ActionName;
                gameObject.SetActive(CanBeEnabled());
            }
            else
                gameObject.SetActive(false);
        }

        private void Start()
        {
            gameObject.SetActive(false);
            _button.Clicked += StartAction;
        }

        private void StartAction(SelectableButton buttonSelectable)
        {
            if (!_slot.IsValid() || _itemAction == null)
                return;

            if (_character == null)
            {
                Debug.LogWarning("This behaviour is not attached to a character.", gameObject);
                return;
            }

            (var coroutine, float duration) = _itemAction.Perform(_character, _slot, _slot.GetStack());
            _actionCoroutine = coroutine;

            if (duration > 0.01f)
            {
                string actionVerb = _itemAction.ActionVerb;
                var aParams = new CustomActionArgs(actionVerb + "...", duration, true, null, CancelAction);
                ActionManagerUI.Instance.StartAction(aParams);
            }
        }
        
        private void CancelAction() => _itemAction.CancelAction(ref _actionCoroutine, _character);

        private bool CanBeEnabled() =>
               _slot.IsValid()
            && _slot.HasItem()
            && _itemAction != null
            && _itemAction.CanPerform(_character, _slot.GetStack());

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            _button = GetComponent<SelectableButton>();
            _iconImage = GetComponentInChildren<Image>();
            _nameText = GetComponentInChildren<TextMeshProUGUI>();
        }
#endif
        #endregion
    }
}