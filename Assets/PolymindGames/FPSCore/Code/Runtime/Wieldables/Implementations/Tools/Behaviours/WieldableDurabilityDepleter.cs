using PolymindGames.InventorySystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireComponent(typeof(IWieldable))]
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Wieldable Durability Depleter")]
    public sealed class WieldableDurabilityDepleter : MonoBehaviour
    {
        [SerializeField, Suffix(" / sec")]
        private float _depletion = 1f;

        [SerializeField, Title("Events")]
        private UnityEvent _onDurabilityDepleted;

        [SerializeField]
        private UnityEvent _onDurabilityRestored;

        private IWieldableItem _wieldableItem;
        private ItemProperty _durability;
        private bool _durabilityDepleted;
        private float _durabilityToDeplete;
        private float _lastDurability;

        private void Awake()
        {
            _wieldableItem = GetComponentInParent<IWieldableItem>();

            if (_wieldableItem == null)
            {
                Debug.LogError($"No wieldable item found for {GetType().Name}!", gameObject);
                return;
            }

            _wieldableItem.AttachedSlotChanged += OnSlotChanged;
            OnSlotChanged(_wieldableItem.Slot);
        }
        
        private void OnSlotChanged(SlotReference slot)
        {
            if (slot.TryGetItem(out var item))
            {
                _durability = item.GetProperty(ItemConstants.Durability);
                _durabilityDepleted = _durability != null && _durability.Float < 0.01f;
                (_durabilityDepleted ? _onDurabilityDepleted : _onDurabilityRestored).Invoke();
                enabled = true;
            }
            else
            {
                enabled = false;
            }
        }

        private void FixedUpdate()
        {
            _durabilityToDeplete += _depletion * Time.fixedDeltaTime;

            if (_durabilityToDeplete >= _depletion)
            {
                // On Durability increased && previously fully depleted
                if (_durabilityDepleted && _durability.Float > _lastDurability)
                {
                    _durabilityDepleted = false;
                    _onDurabilityRestored.Invoke();
                }

                _durability.Float = Mathf.Max(_durability.Float - _durabilityToDeplete, 0f);

                _lastDurability = _durability.Float;
                _durabilityToDeplete = 0f;

                // On Depleted Durability
                if (!_durabilityDepleted && _lastDurability <= 0.001f)
                {
                    _durabilityDepleted = true;
                    _onDurabilityDepleted.Invoke();
                }
            }
        }
    }
}