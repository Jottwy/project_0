using PolymindGames.ProceduralMotion;
using PolymindGames.InventorySystem;
using PolymindGames.WieldableSystem;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class WieldableDurabilityUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull]
        [Tooltip("The UI text component used for displaying wieldable durability.")]
        private TextMeshProUGUI _text;
        
        [SerializeField, NotNull]
        private CanvasRenderer _canvasRenderer;

        [SerializeField, SpaceArea]
        [Tooltip("The gradient used to represent the durability color.")]
        private Gradient _color;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("The percentage at which the durability color should change to indicate low durability.")]
        private float _activatePercent = 0.35f;

        private const string LowDurability = "Low Durability";
        private const string NoDurability = "No Durability";

        private ItemProperty _durabilityProperty;
        private bool _isVisible;

        protected override void OnCharacterAttached(ICharacter character)
        {
            _canvasRenderer.SetAlpha(0f);
            character.GetCC<IWieldablesControllerCC>().EquippingStopped += OnWieldableEquipped;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            character.GetCC<IWieldablesControllerCC>().EquippingStopped -= OnWieldableEquipped;

            if (_durabilityProperty != null)
            {
                _durabilityProperty.Changed -= OnDurabilityPropertyOnChanged;
                _durabilityProperty = null;
            }
        }

        private void OnWieldableEquipped(IWieldable wieldable)
        {
            // Unsubscribe from previous durability property
            if (_durabilityProperty != null)
            {
                _durabilityProperty.Changed -= OnDurabilityPropertyOnChanged;
                _durabilityProperty = null;
            }

            if (wieldable?.gameObject != null &&
                wieldable.gameObject.TryGetComponent(out IWieldableItem wieldableItem) &&
                (wieldableItem.Slot.GetItem()?.TryGetProperty(ItemConstants.Durability, out var durabilityProperty) ?? false))
            {
                _durabilityProperty = durabilityProperty;
                durabilityProperty.Changed += OnDurabilityPropertyOnChanged;
                OnDurabilityPropertyOnChanged(durabilityProperty);
            }
            else
            {
                SetVisibility(false);
            }
        }

        private void OnDurabilityPropertyOnChanged(ItemProperty property)
        {
            bool isVisible = property.Float < _activatePercent;

            SetVisibility(isVisible);

            if (isVisible)
                UpdateText(property.Float);
        }

        private void UpdateText(float percent)
        {
            _text.color = _color.Evaluate(percent);
            _text.text = Mathf.Approximately(percent, 0f) ? NoDurability : LowDurability;
        }

        private void SetVisibility(bool value)
        {
            if (_isVisible == value)
                return;

            _canvasRenderer.ClearTweens();
            _canvasRenderer.TweenCanvasRendererAlpha(value ? 1f : 0f, 0.3f)
                .SetUnscaledTime(true)
                .AutoReleaseWithParent(true);

            _isVisible = value;
        }
    }
}
