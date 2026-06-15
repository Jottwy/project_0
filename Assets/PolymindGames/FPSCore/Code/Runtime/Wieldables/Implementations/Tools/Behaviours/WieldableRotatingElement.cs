using PolymindGames.InventorySystem;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Wieldable Rotating Element")]
    public sealed class WieldableRotatingElement : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("The Transform component representing the compass rose.")]
        private Transform _compassRose;

        [SerializeField]
        [Tooltip("The axis around which the compass rose will rotate.")]
        private Vector3 _rotationAxis = new(0, 0, 1);

        [SerializeField, Range(0f, 100f)]
        [Tooltip("The speed at which the compass rose rotates around its axis.")]
        private float _rotationSpeed = 3f;

        [SerializeField, SpaceArea]
        [Tooltip("The item property definition used to track durability.")]
        private DataIdReference<ItemPropertyDefinition> _durabilityProperty;

        private IWieldableItem _wieldableItem;
        private ItemProperty _durability;
        private Transform _root;
        private float _angle;
        
        private void OnAttachedSlotChanged(SlotReference slot)
        {
            if (slot.TryGetItem(out var item))
            {
                _durability = item.GetProperty(_durabilityProperty);
                _root = _wieldableItem.Wieldable.Character.transform;
                enabled = true;
            }
            else
            {
                _durability = null;
                enabled = false;
            }
        }
        
        private void Awake()
        {
            _wieldableItem = GetComponentInParent<IWieldableItem>();

            if (_wieldableItem == null)
            {
                Debug.LogError($"No wieldable item found for {GetType().Name}!", gameObject);
                return;
            }

            _wieldableItem.AttachedSlotChanged += OnAttachedSlotChanged;
            OnAttachedSlotChanged(_wieldableItem.Slot);
        }

        private void LateUpdate()
        {
            if (_durability.Float < 0.01f)
                return;

            _angle = UpdateRoseAngle(_angle, _rotationSpeed * Time.deltaTime);
            _compassRose.localRotation = Quaternion.AngleAxis(_angle, _rotationAxis);
        }

        private float UpdateRoseAngle(float angle, float delta)
        {
            angle = Mathf.LerpAngle(angle, Vector3.SignedAngle(_root.forward, Vector3.forward, Vector3.up), delta);
            angle = Mathf.Repeat(angle, 360f);

            return angle;
        }
    }
}