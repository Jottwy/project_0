using PolymindGames.WorldManagement;
using PolymindGames.SaveSystem;
using UnityEngine.Events;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.ResourceHarvesting
{
    /// <summary>
    /// Represents a gatherable object in the game that can be damaged, respawned, and reset.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class HarvestableResource : MonoBehaviour, ISaveableComponent, IPoolableListener, IHarvestableResource
    {
        [SerializeField, InLineEditor]
        [Tooltip("Defines the properties of the harvestable resource, such as health, type, and regeneration settings.")]
        private HarvestableResourceDefinition _resourceDefinition;

        [SerializeField, SpaceArea(3f)] 
        private bool _disableHitboxOnKilled = true;
        
        [Line]
        [SerializeField, ChildObjectOnly]
        private GameObject _unharvestedObject;
        
        [SerializeField, ChildObjectOnly]
        private GameObject _partiallyHarvestedObject;
        
        [SerializeField, ChildObjectOnly]
        private GameObject _fullyHarvestedObject;

        [SpaceArea]
        [LabelByChild(nameof(EventEntry.EventType))]
        [SerializeField, ReorderableList(ListStyle.Lined)]
        [Tooltip("List of event entries that define event behaviors.")]
        private EventEntry[] _events;

        private float _remainingHarvestAmount = 1f;
        private HarvestableState _currentState;
        private int _remainingRespawnDays;
        private Collider _hitboxCollider;

        /// <inheritdoc/>
        public HarvestableResourceDefinition ResourceDefinition => _resourceDefinition;
        public float RemainingHarvestAmount => _remainingHarvestAmount;
        public HarvestableState HarvestableState => _currentState;

        public event DamageReceivedDelegate Harvested;
        public event DamageReceivedDelegate FullyHarvested;
        public event UnityAction<IHarvestableResource> Respawned;

        /// <inheritdoc/>
        public Bounds GetHarvestBounds()
        {
            var worldBounds = _resourceDefinition.HarvestBounds;
            worldBounds.center += transform.position;
            return worldBounds;
        }

        /// <inheritdoc/>
        public bool CanBeHarvested(float harvestPower, Vector3 worldPosition)
        {
            return _currentState is not HarvestableState.FullyHarvested &&
                   _resourceDefinition.IsHarvestPowerSufficient(harvestPower) &&
                   IsWithinHarvestBounds(worldPosition);
        }

        /// <inheritdoc/>
        public bool TryHarvest(float harvestPower, float amountToHarvest, in DamageArgs args)
        {
            if (!CanBeHarvested(harvestPower, args.HitPoint))
                return false;

            _remainingHarvestAmount = Mathf.Max(0f, _remainingHarvestAmount - Mathf.Abs(amountToHarvest));
            var targetState = _remainingHarvestAmount < 0.001f
                ? HarvestableState.FullyHarvested
                : HarvestableState.PartiallyHarvested;

            SetState(targetState);
            
            RaiseEvent(HarvestableEventType.Harvested);
            Harvested?.Invoke(amountToHarvest, in args);

            if (targetState == HarvestableState.FullyHarvested)
            {
                FullyHarvested?.Invoke(amountToHarvest, args);
                RaiseEvent(HarvestableEventType.FullyHarvested);
            }

            return true;
        }

        private bool IsWithinHarvestBounds(Vector3 point)
        {
            var bounds = GetHarvestBounds();
            transform.GetPositionAndRotation(out var position, out var rotation);
            return bounds.IsPointInsideRotatedBounds(rotation, position, point);
        }

        private void RaiseEvent(HarvestableEventType eventType)
        {
            foreach (var eventEntry in _events)
            {
                if (eventEntry.EventType == eventType)
                {
                    eventEntry.Event.Invoke();
                    return;
                }
            }
        }

        /// <summary>
        /// Sets the state of the resource and updates its visuals and respawn timer accordingly.
        /// </summary>
        protected virtual void SetState(HarvestableState newState)
        {
            if (_currentState == newState)
                return;

            // Toggle hitbox if needed
            if (_disableHitboxOnKilled)
                _hitboxCollider.enabled = newState != HarvestableState.FullyHarvested;

            // Manage state object visibility
            var previousObject = GetTargetStateObject(_currentState);
            var newObject = GetTargetStateObject(newState);

            if (previousObject != newObject)
            {
                if (previousObject != null)
                    previousObject.SetActive(false);
                
                if (newObject != null)
                    newObject.SetActive(true);
            }

            _currentState = newState;

            if (newState == HarvestableState.Unharvested)
            {
                _remainingHarvestAmount = 1f;
                UpdateRespawnDays(0);
            }
            else if (newState == HarvestableState.FullyHarvested)
            {
                int baseRespawnDays = _resourceDefinition.RespawnDays;
                if (baseRespawnDays > 0)
                {
                    // If day is more than halfway over, add one extra day
                    bool isLateInDay = World.Instance.Time.DayTime > 0.5f;
                    int finalRespawnDays = baseRespawnDays + (isLateInDay ? 1 : 0);
                    UpdateRespawnDays(finalRespawnDays);
                }
            }
        }

        private GameObject GetTargetStateObject(HarvestableState state)
        {
            return state switch
            {
                HarvestableState.NotReady => null,
                HarvestableState.Unharvested => _unharvestedObject,
                HarvestableState.PartiallyHarvested => _partiallyHarvestedObject,
                HarvestableState.FullyHarvested => _fullyHarvestedObject,
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
            };
        }

        /// <summary>
        /// Updates the respawn timer for the resource.
        /// </summary>
        private void UpdateRespawnDays(int days)
        {
            var time = World.Instance.Time;

            if (_remainingRespawnDays == days)
                return;

            if (_remainingRespawnDays == 0 && days > 0)
            {
                time.DayChanged += OnDayChanged;
            }
            else if (_remainingRespawnDays > 0 && days == 0)
            {
                time.DayChanged -= OnDayChanged;
            }

            _remainingRespawnDays = days;
        }

        /// <summary>
        /// Handles the day change event and updates the resource's respawn status.
        /// </summary>
        private void OnDayChanged(int day, int daysPassed)
        {
            UpdateRespawnDays(Mathf.Max(_remainingRespawnDays - daysPassed, 0));
            if (_remainingRespawnDays == 0)
            {
                SetState(HarvestableState.Unharvested);
                RaiseEvent(HarvestableEventType.Respawned);
                Respawned?.Invoke(this);
            }
        }

        private void Awake()
        {
            _hitboxCollider = GetComponent<Collider>();
            SetState(HarvestableState.Unharvested);
        }

        private void OnDestroy() => UpdateRespawnDays(0);

        #region Pooling Methods
        /// <summary>
        /// Called when the resource is acquired from the pool.
        /// </summary>
        public void OnAcquired() => SetState(HarvestableState.Unharvested);

        /// <summary>
        /// Called when the resource is returned to the pool.
        /// </summary>
        public void OnReleased() => UpdateRespawnDays(0);
        #endregion

        #region Save & Load
        [Serializable]
        private sealed class SaveData
        {
            public HarvestableState State;
            public int RemainingRespawnDays;
            public float RemainingHarvestAmount;
        }

        void ISaveableComponent.LoadMembers(object data)
        {
            var saveData = (SaveData)data;
            _remainingRespawnDays = saveData.RemainingRespawnDays;
            _remainingHarvestAmount = saveData.RemainingHarvestAmount;
            SetState(saveData.State);
        }

        object ISaveableComponent.SaveMembers() => new SaveData()
        {
            State = _currentState,
            RemainingRespawnDays = _remainingRespawnDays,
            RemainingHarvestAmount = _remainingHarvestAmount
        };
        #endregion

        #region Internal Types
        private enum HarvestableEventType
        {
            Harvested,
            FullyHarvested,
            Respawned
        }

        [Serializable]
        private struct EventEntry
        {
            [Tooltip("The type of event this entry responds to.")]
            public HarvestableEventType EventType;

            [SpaceArea]
            [Tooltip("The Unity event that will be invoked when this entry's conditions are met.")]
            public UnityEvent Event;
        }
        #endregion
        
        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            gameObject.SetLayersInChildren(LayerConstants.DynamicObject);
        }

        private void OnDrawGizmosSelected()
        {
            if (Event.current.type != EventType.Repaint || _resourceDefinition == null)
                return;
            
            Bounds gatherBounds = _resourceDefinition.HarvestBounds;
            gatherBounds.center += transform.position;

            var oldColor = Handles.color;
            var oldMatrix = Handles.matrix;

            Handles.color = new Color(1f, 0f, 0f, 0.5f);
            Handles.matrix = Matrix4x4.TRS(gatherBounds.center, transform.rotation, gatherBounds.size);

            Handles.CubeHandleCap(0, Vector3.zero, Quaternion.identity, 1f, EventType.Repaint);

            Handles.color = oldColor;
            Handles.matrix = oldMatrix;

            Handles.Label(gatherBounds.center, "Harvest Bounds");
        }
#endif
        #endregion
    }
}