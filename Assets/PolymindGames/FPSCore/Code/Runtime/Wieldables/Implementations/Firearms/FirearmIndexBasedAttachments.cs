using PolymindGames.InventorySystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireComponent(typeof(IFirearm))]
    [AddComponentMenu("Polymind Games/Wieldables/Firearms/Index-Based Attachments")]
    public sealed class FirearmIndexBasedAttachments : MonoBehaviour, IFirearmIndexModeHandler
    {
        [SerializeField, Range(0f, 10f)]
        [Tooltip("The cooldown duration between mode changes.")]
        private float _changeModeCooldown = 0.35f;

        [SerializeField]
        [Tooltip("The audio data for the mode change.")]
        private AudioData _changeModeAudio = new(null);

        [SerializeField]
        [Tooltip("Determines if there's an animation for mode change.")]
        private bool _changeModeAnimation = true;

        [SerializeField, Title("Modes")]
        [Tooltip("Reference to the property definition identifying the mode index.")]
        private DataIdReference<ItemPropertyDefinition> _indexProperty;

        [SerializeField, ReorderableList(ListStyle.Lined, HasLabels = false)]
        [Tooltip("The array of firearm attachment behaviors, which can represent a single attachment or a group of attachments as a 'Firearm Mode'.")]
        private FirearmAttachment[] _attachments;

        private ItemProperty _attachedProperty;
        private IWieldableItem _wieldableItem;
        private IFirearm _firearm;
        private int _selectedIndex;
        private float _toggleTimer;

        /// <inheritdoc/>
        public FirearmAttachment CurrentMode => _attachments[_selectedIndex];

        /// <inheritdoc/>
        public IFirearm Firearm => _firearm;

        /// <inheritdoc/>
        public event UnityAction<FirearmAttachment> ModeChanged;

        /// <inheritdoc/>
        public void ToggleNextMode()
        {
            if (!CanToggleMode())
                return;

            _toggleTimer = Time.time + _changeModeCooldown;

            int currentIndex = _attachedProperty.Integer;
            int nextIndex = FindNextValidAttachment(currentIndex);

            if (nextIndex == currentIndex)
                return;

            _wieldableItem.Wieldable.Audio.PlayClip(_changeModeAudio, BodyPoint.Hands);

            if (_changeModeAnimation)
                _wieldableItem.Wieldable.Animator.SetTrigger(AnimationConstants.ChangeMode);

            AttachConfiguration(nextIndex);
            ModeChanged?.Invoke(CurrentMode);
        }

        /// <summary>
        /// Finds the next valid attachment mode that can be applied.
        /// </summary>
        /// <param name="current">The current attachment index.</param>
        /// <returns>The index of the next valid attachment, or the current index if none are valid.</returns>
        private int FindNextValidAttachment(int current)
        {
            int total = _attachments.Length;

            for (int i = 1; i < total; i++)
            {
                int nextIndex = (current + i) % total;
                if (_attachments[nextIndex].CanAttach())
                    return nextIndex;
            }

            return current; // No valid attachment found, return the current index
        }

        /// <summary>
        /// Determines whether the firearm mode can be toggled.
        /// </summary>
        /// <returns>True if the mode can be changed, otherwise false.</returns>
        private bool CanToggleMode()
        {
            return _attachedProperty != null
                && Time.time >= _toggleTimer
                && !_firearm.ReloadableMagazine.IsReloading
                && !_firearm.AimHandler.IsAiming
                && !_firearm.Trigger.IsTriggerHeld;
        }

        /// <summary>
        /// Applies the attachment configuration at the specified index.
        /// </summary>
        /// <param name="newIndex">The index of the attachment to apply.</param>
        private void AttachConfiguration(int newIndex)
        {
            if (_selectedIndex != newIndex)
            {
                _attachments[_selectedIndex].Detach();
                _selectedIndex = newIndex;
                _attachments[_selectedIndex].Attach();
                _attachedProperty.Integer = newIndex;
            }
        }

        /// <summary>
        /// Handles changes to the equipped item and updates the attachment configuration accordingly.
        /// </summary>
        /// <param name="slot">The newly equipped item.</param>
        private void OnAttachedSlotChanged(SlotReference slot)
        {
            if (_attachedProperty != null)
                _attachedProperty.Changed -= OnPropertyChanged;

            if (slot.GetItem()?.TryGetProperty(_indexProperty, out _attachedProperty) ?? false)
            {
                AttachConfiguration(_attachedProperty.Integer);
                _attachedProperty.Changed += OnPropertyChanged;
            }
        }
        
        private void OnPropertyChanged(ItemProperty property) => AttachConfiguration(property.Integer);

        private void Awake()
        {
            _firearm = GetComponent<IFirearm>();

            if (_firearm == null)
            {
                Debug.LogError($"No firearm found for {GetType().Name}!", gameObject);
                return;
            }

            _wieldableItem = GetComponentInParent<IWieldableItem>();

            if (_wieldableItem == null)
            {
                Debug.LogError($"No wieldable item found for {GetType().Name}!", gameObject);
                return;
            }

            _wieldableItem.AttachedSlotChanged += OnAttachedSlotChanged;
            OnAttachedSlotChanged(_wieldableItem.Slot);
        }

#if UNITY_EDITOR
        private void Reset()
        {
            _indexProperty = ItemConstants.FirearmMode;
        }
#endif
    }
}