using PolymindGames.WorldManagement;
using PolymindGames.InventorySystem;
using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine.Events;
using UnityEngine;
using UnityEngine.Serialization;

namespace PolymindGames
{
    /// <summary>
    /// Convert the cooking to item actions.
    /// </summary>
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/interaction/interactable/demo-interactables")]
    public sealed class CookingStation : Workstation, ISaveableComponent
    {
        [Title("Cooking")]
        [SerializeField, Range(1, 10)]
        [Tooltip("How many cooking spots this campfire has.")]
        private int _cookingSpots = 3;

        [SerializeField]
        private bool _allowStacking = false;

        [SpaceArea]
        [SerializeField, Range(0f, 10f)]
        private float _cookingStartDuration = 2.5f;

        [SerializeField, Range(0f, 10f)]
        private float _cookingStopDuration = 1.5f;
        
        [SerializeField, Range(1f, 1000f)]
        private int _maxFuelTime = 500;
        
        [SerializeField, Range(0.01f, 10f)]
        [Tooltip("Multiplies the effects of any fuel added.")]
        private float _cookingDurationMod = 1f;

        [SerializeField, Range(0.01f, 10f)]
        [Tooltip("Multiplies the cooking speed.")]
        private float _cookingSpeedMod = 1f;

        [SerializeField, SpaceArea]
        [Tooltip("The property that tells the campfire how cooked an item is.")]
        private DataIdReference<ItemPropertyDefinition> _cookedAmountProperty;

        [SerializeField, Title("Audio")]
        private AudioData _startCookingAudio = new(null);

        [SerializeField]
        private AudioData _stopCookingAudio = new(null);
        
        [FormerlySerializedAs("_fuelAddAudio"),SerializeField]
        private AudioData _addFuelAudio = new(null);

        private readonly IItemContainer[] _containers = new IItemContainer[1];
        private CookingSlot[] _cookingSlots;
        private bool _isCookingActive;
        private int _cookingTimeLeft;

        /// <summary>
        /// Gets a value indicating whether cooking is currently active.
        /// </summary>
        public bool IsCookingActive => _isCookingActive;

        /// <summary>
        /// Gets the cooking progress, represented as the ratio of remaining cooking time to maximum fuel time.
        /// </summary>
        public float CookingProgress => ((float)_maxFuelTime - _cookingTimeLeft) / _maxFuelTime;

        public int CookingTimeLeft => _cookingTimeLeft;
        
        /// <summary>
        /// Event triggered when cooking starts.
        /// </summary>
        public event UnityAction CookingStarted;

        /// <summary>
        /// Event triggered when cooking progress is updated.
        /// </summary>
        public event UnityAction CookingUpdated;

        /// <summary>
        /// Event triggered when cooking is stopped.
        /// </summary>
        public event UnityAction CookingStopped;

        /// <summary>
        /// Event triggered when fuel is added to the cooking process.
        /// </summary>
        public event UnityAction<int> FuelAdded;

        public override IReadOnlyList<IItemContainer> GetContainers()
        {
            _containers[0] ??= GenerateDefaultContainer();
            return _containers;
        }

        /// <summary>
        /// Queues the start of the cooking process after a delay.
        /// </summary>
        /// <returns>The duration of the delay before the cooking process starts.</returns>
        public float QueueStartCooking()
        {
            if (_isCookingActive)
                return 0f;
            
            // Play audio for queuing the cooking process
            AudioManager.Instance.PlayClip3D(_startCookingAudio, transform.position);

            // Invoke the StartCooking method after the specified delay
            CoroutineUtility.InvokeDelayed(this, StartCooking, _cookingStartDuration);

            return _cookingStartDuration;
        }

        /// <summary>
        /// Queues the stop of the cooking process after a delay.
        /// </summary>
        /// <returns>The duration of the delay before the cooking process stops.</returns>
        public float QueueStopCooking()
        {
            if (!_isCookingActive)
                return 0f;

            // Invoke the StopCooking method after the specified delay
            CoroutineUtility.InvokeDelayed(this, () => StopCooking(), _cookingStopDuration);

            return _cookingStopDuration;
        }
        
        /// <summary>
        /// Stops all coroutine queues.
        /// </summary>
        public void CancelQueues() => StopAllCoroutines();

        /// <summary>
        /// Starts the cooking process.
        /// </summary>
        public void StartCooking()
        {
            // Check if cooking is already active
            if (_isCookingActive)
            {
                Debug.LogError("Cooking is already active.");
                return;
            }

            // Start cooking
            _isCookingActive = true;

            // Trigger event for cooking started
            CookingStarted?.Invoke();

            // Subscribe to minute change event for updating cooking
            World.Instance.Time.MinuteChanged += MinuteChanged;
        }

        /// <summary>
        /// Adds fuel to the cooking process.
        /// </summary>
        /// <param name="fuelDuration">The duration of fuel to add for cooking, in game minutes.</param>
        public void AddFuel(int fuelDuration)
        {
            // Check if cooking is active
            if (!_isCookingActive)
            {
                Debug.LogError("Cooking is not active.");
                return;
            }

            // Increment cooking time left by the adjusted fuel duration
            _cookingTimeLeft = Mathf.Min(_maxFuelTime, _cookingTimeLeft + (int)(fuelDuration * _cookingDurationMod));

            // Trigger event for fuel added
            FuelAdded?.Invoke(fuelDuration);
            
            // Play audio for adding fuel
            AudioManager.Instance.PlayClip3D(_addFuelAudio, transform.position);
        }

        /// <summary>
        /// Stops the cooking process.
        /// </summary>
        public void StopCooking(bool playAudio = true)
        {
            // Check if cooking is active
            if (!_isCookingActive)
            {
                Debug.LogError("Cooking is not active.");
                return;
            }

            // Reset end timer and mark cooking as inactive
            _cookingTimeLeft = 0;
            _isCookingActive = false;

            // Unsubscribe from minute change event for cooking updates
            World.Instance.Time.MinuteChanged -= MinuteChanged;

            // Trigger event for cooking stopped
            CookingStopped?.Invoke();
            
            // Play audio for stopping the cooking process
            if (playAudio)
                AudioManager.Instance.PlayClip3D(_stopCookingAudio, transform.position);
        }

        private void MinuteChanged(int totalMinutes, int passedMinutes)
        {
            // If the time went backwards, return (this can happen in the editor)
            if (passedMinutes < 0)
                return;
            
            // Update cooking progress
            UpdateCooking(passedMinutes);

            // Trigger event for cooking progress updated
            CookingUpdated?.Invoke();
            
            // Check if cooking time has run out and stop cooking if so
            _cookingTimeLeft -= passedMinutes;
            if (_cookingTimeLeft <= 0)
                StopCooking();
        }
        
        private IItemContainer GenerateDefaultContainer()
        {
            // Create a new item container for the cooking station
            var container = new ItemContainer.Builder()
                .WithName(nameof(CookingStation))
                .WithSize(_cookingSpots)
                .WithAllowStacking(_allowStacking)
                .WithRestriction(PropertyContainerRestriction.Create(_cookedAmountProperty))
                .Build();
            
            // Subscribe to the container changed event
            container.Changed += OnCookingContainerChanged;
            return container;
        }
        
        private void OnCookingContainerChanged()
        {
            var container = _containers[0];
            for (int i = 0; i < _cookingSpots; ++i)
                UpdateCookingSlot(_cookingSlots[i], container.GetItemAtIndex(i));
        }

        private void UpdateCookingSlot(CookingSlot cookingSlot, ItemStack stack)
        {
            cookingSlot.Stack = stack;
            cookingSlot.Data = stack.Item?.Definition.GetDataOfType<CookData>();
            cookingSlot.Property = stack.Item?.GetProperty(_cookedAmountProperty);
        }

        private void UpdateCooking(int passedMinutes)
        {
            // Get the cooking container
            var cookingContainer = GetContainers()[0];
            int slotIndex = 0;
            
            foreach (var cookingSlot in _cookingSlots)
            {
                // Skip if cooking is not allowed in the slot
                if (!cookingSlot.CanCook)
                    continue;

                var cookingProperty = cookingSlot.Property;
                var cookData = cookingSlot.Data;
                int stackCount = cookingSlot.Stack.Count;
                
                // Calculate cooking progress based on passed time
                float cookingProgress = 1f / cookData.CookTimeInMinutes / stackCount * passedMinutes * _cookingSpeedMod;
                cookingProperty.Float = Mathf.Clamp01(cookingProperty.Float + cookingProgress);

                // Check if cooking is completed
                if (cookingProperty.Float > 0.999f)
                {
                    // Replace the item with the cooked output, if available
                    var stack = cookData.CookedOutput.IsNull
                        ? ItemStack.Null
                        : new ItemStack(new Item(cookData.CookedOutput.Def), stackCount);
                    
                    cookingContainer.SetItemAtIndex(slotIndex, stack);
                }

                ++slotIndex;
            }
        }
        
        private void OnDestroy()
        {
            if (_isCookingActive && !UnityUtility.IsQuitting)
                StopCooking(false);
        }

        private void Awake() => _cookingSlots = CreateCookingSlots();

        private CookingSlot[] CreateCookingSlots()
        {
            var slots = new CookingSlot[_cookingSpots];
            for (int i = 0; i < _cookingSpots; i++)
                slots[i] = new CookingSlot();
            return slots;
        }

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_cookedAmountProperty.IsNull)
            {
                UnityUtility.SafeOnValidate(this, () =>
                {
                    _cookedAmountProperty = new DataIdReference<ItemPropertyDefinition>(ItemPropertyDefinition.GetWithName("Cooked Amount"));
                });
            }
        }
#endif
        #endregion
        
        #region Internal Types
        private sealed class CookingSlot
        {
            public CookData Data;
            public ItemStack Stack;
            public ItemProperty Property;

            public bool CanCook => Property != null && Data != null;
        }
        #endregion

        #region Save & Load
        private sealed class SaveData
        {
            public ItemContainer Container;
            public int CookTimeLeft;
        }

        void ISaveableComponent.LoadMembers(object data)
        {
            var saveData = (SaveData)data;
            var container = saveData.Container;

            if (container == null)
                return;
            
            _containers[0] = container;
            container.InitializeAfterDeserialization(null, null);
            container.Changed += OnCookingContainerChanged;

            if (saveData.CookTimeLeft <= 0)
                return;

            _cookingTimeLeft = saveData.CookTimeLeft;
            StartCooking();
        }

        object ISaveableComponent.SaveMembers()
        {
            return new SaveData
            {
                Container = _containers?[0] as ItemContainer,
                CookTimeLeft = CookingTimeLeft
            };
        }

        #endregion
    }
}