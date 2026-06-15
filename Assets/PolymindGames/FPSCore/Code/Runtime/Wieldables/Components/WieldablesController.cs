using System.Collections.Generic;
using System.Collections;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Controller responsible for equipping and holstering wieldables.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/player/modules-and-behaviours/wieldable#wieldables-controller-module")]
    public sealed class WieldablesController : CharacterBehaviour, IWieldablesControllerCC
    {
        [SerializeField]
        [Tooltip("The parent of the spawned wieldables.")]
        private Transform _spawnRoot;

        [SerializeField, DisableInPlayMode, SceneObjectOnly]
        [Tooltip("The wieldable equipped when equipping a NULL wieldable. (e.g., arms/unarmed).")]
        private Wieldable _defaultWieldable;

        private readonly List<IWieldable> _registeredWieldables = new();
        private readonly List<WieldableEntry> _equipStack = new();
        private WieldableControllerState _state;
        private Coroutine _updateCoroutine;
        private IWieldable _activeWieldable;
        private IWieldable _nullWieldable;
        private int _activeWieldableId;
        private float _holsterSpeed = 1f;

        /// <inheritdoc/>
        public WieldableControllerState State => _state;
        
        /// <inheritdoc/>
        public Transform WieldablesRoot => _spawnRoot != null ? _spawnRoot : transform;
        
        /// <inheritdoc/>
        public IWieldable ActiveWieldable => _activeWieldable is NullWieldable ? null : _activeWieldable;
        
        private WieldableEntry TopWieldable => _equipStack[^1];

        private float HolsterSpeed
        {
            get => _holsterSpeed;
            set => _holsterSpeed = Mathf.Clamp(value, IWieldable.MinHolsterSpeed, IWieldable.MaxHolsterSpeed);
        }

        /// <inheritdoc/>
        public event WieldableEquipDelegate HolsteringStarted;
        
        /// <inheritdoc/>
        public event WieldableEquipDelegate HolsteringStopped;
        
        /// <inheritdoc/>
        public event WieldableEquipDelegate EquippingStarted;
        
        /// <inheritdoc/>
        public event WieldableEquipDelegate EquippingStopped;

        /// <inheritdoc/>
        public IWieldable RegisterWieldable(IWieldable wieldable, bool disable = true)
        {
            if (wieldable == null || _registeredWieldables.Contains(wieldable))
                return wieldable;

            wieldable = InstantiateIfNeeded(wieldable);
            _registeredWieldables.Add(wieldable);
            wieldable.SetCharacter(Character);

            if (disable && wieldable is MonoBehaviour)
            {
                var wieldableGameObject = wieldable.gameObject;
                if (!wieldableGameObject.activeSelf)
                {
                    wieldableGameObject.SetActive(true);
                }
                
                wieldableGameObject.SetActive(false);
            }

            return wieldable;
        }

        /// <inheritdoc/>
        public bool UnregisterWieldable(IWieldable wieldable, bool destroy = false)
        {
            if (!ValidateWieldableRegistered(wieldable) || !TryHolsterWieldable(wieldable, 5f))
                return false;

            _registeredWieldables.Remove(wieldable);
            wieldable.SetCharacter(null);

            if (destroy)
            {
                Destroy(wieldable.gameObject);
            }

            return true;
        }

        /// <inheritdoc/>
        public bool TryEquipWieldable(IWieldable wieldable, float holsterSpeed = 1f, UnityAction equipCallback = null)
        {
            wieldable ??= _nullWieldable;

            if (!ValidateWieldableRegistered(wieldable))
                return false;

            int indexOfEntry = _equipStack.Count > 1
                ? _equipStack.FindIndex(1, entry => entry.Wieldable == wieldable)
                : -1;

            // Case 1: Wieldable not in the equip stack.
            if (indexOfEntry == -1)
                _equipStack.Add(new WieldableEntry(wieldable, equipCallback));

            // Case 2: Wieldable already equipped or will be equipped.
            else if (indexOfEntry == _equipStack.Count - 1)
                return false;

            // Case 3: Move the wieldable to the top of the equip stack.
            else
            {
                _equipStack.RemoveAt(indexOfEntry);
                _equipStack.Add(new WieldableEntry(wieldable, equipCallback));
            }

            HolsterSpeed = holsterSpeed;
            UpdateActiveWieldable();

            return true;
        }

        /// <inheritdoc/>
        public bool TryHolsterWieldable(IWieldable wieldable, float holsterSpeed = 1f)
        {
            int index = _equipStack.FindIndex(0, entry => entry.Wieldable == wieldable);

            if (index <= 0)
                return false;

            _equipStack.RemoveAt(index);

            if (index == _equipStack.Count)
            {
                HolsterSpeed = holsterSpeed;
                UpdateActiveWieldable();
            }

            return true;
        }

        /// <inheritdoc/>
        public void HolsterAll()
        {
            if (_equipStack.Count > 1)
            {
                _equipStack.RemoveRange(1, _equipStack.Count - 2);
                UpdateActiveWieldable();
            }
        }

        protected override void OnBehaviourStart(ICharacter character)
        {
            // Register the default wieldable or create a NullWieldable if none exists.
            _nullWieldable = _defaultWieldable != null
                ? RegisterWieldable(_defaultWieldable)
                : RegisterWieldable(new NullWieldable());

            // Try to equip a null wieldable.
            TryEquipWieldable(null);
        }

        /// <summary>
        /// Instantiates the wieldable if it is a prefab; otherwise, returns the original.
        /// </summary>
        private IWieldable InstantiateIfNeeded(IWieldable wieldable)
        {
            // Check if the wieldable's game object is a prefab and instantiate it if so.
            if (wieldable.gameObject != null && wieldable.gameObject.IsPrefab())
            {
                return Instantiate(wieldable.gameObject, WieldablesRoot.position, WieldablesRoot.rotation, WieldablesRoot)
                    .GetComponent<IWieldable>();
            }

            // Return the original wieldable.
            return wieldable;
        }

        /// <summary>
        /// Updates the active wieldable if needed, starting the equipping coroutine.
        /// </summary>
        private void UpdateActiveWieldable()
        {
            // Restart the coroutine if the holster speed is set to the max value (instant holster).
            if (_updateCoroutine != null && _state == WieldableControllerState.Equipping && HolsterSpeed >= IWieldable.MaxHolsterSpeed)
            {
                CoroutineUtility.StopCoroutine(this, ref _updateCoroutine);
                EquippingStopped?.Invoke(_activeWieldable);
            }

            // Start or continue the coroutine to update the active wieldable.
            _updateCoroutine ??= StartCoroutine(UpdateEquipping());
        }

        /// <summary>
        /// Manages the equipping and holstering of wieldables via a coroutine.
        /// </summary>
        private IEnumerator UpdateEquipping()
        {
            do
            {
                // Check if an active wieldable needs to be holstered.
                if (_activeWieldable != null && TopWieldable.UniqueId != _activeWieldableId)
                {
                    _state = WieldableControllerState.Holstering;
                    HolsteringStarted?.Invoke(_activeWieldable);

                    yield return _activeWieldable.Holster(HolsterSpeed);

                    HolsteringStopped?.Invoke(_activeWieldable);
                    _activeWieldable = null;
                    _activeWieldableId = 0;
                }

                // Check if the top wieldable is different from the active one.
                if (TopWieldable.Wieldable != _activeWieldable)
                {
                    _state = WieldableControllerState.Equipping;

                    var entry = TopWieldable;
                    _activeWieldable = entry.Wieldable;
                    _activeWieldableId = entry.UniqueId;

                    EquippingStarted?.Invoke(_activeWieldable);

                    if (entry.EquipCallback != null)
                        CoroutineUtility.InvokeNextFrame(this, entry.EquipCallback);
                    
                    yield return _activeWieldable.Equip();
                    
                    EquippingStopped?.Invoke(_activeWieldable);
                }

            } while (TopWieldable.UniqueId != _activeWieldableId); // Continue until the active ID matches.

            _state = WieldableControllerState.None;
            _updateCoroutine = null;
        }

        /// <summary>
        /// Validates if the specified wieldable is registered.
        /// </summary>
        /// <param name="wieldable">The wieldable to validate.</param>
        /// <returns>True if the wieldable is registered; otherwise, false.</returns>
        private bool ValidateWieldableRegistered(IWieldable wieldable)
        {
            if (_registeredWieldables.Contains(wieldable))
                return true;

            string wieldableName = wieldable != null
                ? wieldable.gameObject != null ? wieldable.gameObject.name : wieldable.ToString()
                : "Null";

            Debug.LogError($"The wieldable: ''{wieldableName}'' is not registered.");
            return false;
        }

        #region Internal Types
        private readonly struct WieldableEntry
        {
            public readonly int UniqueId;
            public readonly IWieldable Wieldable;
            public readonly UnityAction EquipCallback;

            public WieldableEntry(IWieldable wieldable, UnityAction equipCallback)
            {
                Wieldable = wieldable;
                EquipCallback = equipCallback;
                UniqueId = Random.Range(int.MinValue, int.MaxValue);
            }
        }
        #endregion
    }
}