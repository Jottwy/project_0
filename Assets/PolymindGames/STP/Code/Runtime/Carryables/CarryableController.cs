using System.Collections.Generic;
using PolymindGames.InputSystem;
using PolymindGames.SaveSystem;
using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Controller responsible for carrying and managing carriable items.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault1)]
    [RequireCharacterComponent(typeof(IWieldablesControllerCC), typeof(IConstructableBuilderCC))]
    public sealed class CarryableController : CharacterBehaviour, ICarryableControllerCC, ISaveableComponent
    {
        [SerializeField]
        private InputContext _carryContext;
        
        [SpaceArea]
        [SerializeField, SceneObjectOnly, NotNull]
        private WieldableCarryable _wieldableCarryable;

        [SerializeField, Range(0.5f, 5f)]
        private float _wieldableHolsterSpeed = 5f;
        
        [SerializeField, Title("Audio")]
        private AudioData _carryAudio = new(null);

        [SerializeField]
        private AudioData _dropAudio = new(null);

        private readonly Stack<CarryablePickup> _pickups = new();
        private IWieldablesControllerCC _controller;

        /// <summary>
        /// Gets a value indicating whether the character is currently carrying an object.
        /// </summary>
        public bool IsCarrying => _wieldableCarryable.Wieldable.State != WieldableStateType.Hidden;
        
        /// <summary>
        /// Gets a value indicating whether the character is currently carrying an object.
        /// </summary>
        public bool HasCarryables => _pickups.Count > 0;

        /// <summary>
        /// Gets the number of objects currently being carried.
        /// </summary>
        public int CarryCount => _pickups.Count;

        /// <summary>
        /// Gets the definition of the currently carried object.
        /// </summary>
        private CarryableDefinition ActiveDefinition => _pickups.TryPeek(out var pickup) ? pickup.Definition : null;
        
        /// <summary>
        /// Event raised when carrying an object starts.
        /// </summary>
        public event UnityAction<CarryableDefinition> ObjectCarryStarted;

        /// <summary>
        /// Event raised when carrying an object stops.
        /// </summary>
        public event UnityAction ObjectCarryStopped;

        /// <summary>
        /// Tries to carry the given carryable object.
        /// </summary>
        /// <param name="pickup">The carryable object to carry.</param>
        /// <returns>True if the object can be carried; otherwise, false.</returns>
        public bool TryCarryObject(CarryablePickup pickup)
        {
            if (!HasCarryables)
            {
                if (TryStartObjectCarry(pickup))
                {
                    CarryObject(pickup);
                    return true;
                }
            }
            else
            {
                if (_pickups.Count != ActiveDefinition.MaxCarryCount && ActiveDefinition == pickup.Definition)
                {
                    CarryObject(pickup);
                    return true;
                }

            }

            return false;
        }
        
        /// <summary>
        /// Attempts to use the carried object.
        /// </summary>
        public void UseCarriedObject()
        {
            // If not carrying an object or in the process of holstering, do nothing.
            if (!HasCarryables || _controller.State == WieldableControllerState.Holstering)
                return;

            // Peek at the topmost object on the stack without removing it.
            var pickup = _pickups.Peek();
            
            // Try using the topmost object.
            if (pickup.TryUse(Character))
            {
                _wieldableCarryable.RemoveCarryable(pickup);
                
                // If the object was successfully used, remove it from the stack.
                _pickups.Pop();

                // If no longer carrying any objects, stop carrying.
                if (!HasCarryables)
                    StopObjectCarry();
            }
        }

        /// <summary>
        /// Drops a specified number of carried objects.
        /// </summary>
        /// <param name="amount">Number of objects to drop.</param>
        public void DropCarriedObjects(int amount)
        {
            // If not carrying any objects or in the process of holstering, do nothing.
            if (!HasCarryables || _controller.State == WieldableControllerState.Holstering)
                return;

            // Attempt to drop the specified number of carried objects.
            TryDropCarryable(amount);
            
            // If no longer carrying any objects, stop carrying.
            if (!HasCarryables)
                StopObjectCarry();
        }

        /// <summary>
        /// Attempts to start carrying the given object.
        /// </summary>
        /// <returns>True if the object can be carried; otherwise, false.</returns>
        private bool TryStartObjectCarry(CarryablePickup pickup)
        {
            // Try to start carrying the object.
            if (_controller.TryEquipWieldable(_wieldableCarryable.Wieldable, _wieldableHolsterSpeed))
            {
                _controller.HolsteringStarted += ForceDropAllCarryables;
                InputManager.Instance.PushContext(_carryContext);
                ObjectCarryStarted?.Invoke(pickup.Definition);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Stops carrying objects.
        /// </summary>
        private void StopObjectCarry()
        {
            _controller.HolsteringStarted -= ForceDropAllCarryables;
            _controller.TryHolsterWieldable(_wieldableCarryable.Wieldable);
            InputManager.Instance.PopContext(_carryContext);
            ObjectCarryStopped?.Invoke();
        }
        
        private void CarryObject(CarryablePickup pickup)
        {
            _pickups.Push(pickup);
            Character.Audio.PlayClip(_carryAudio, BodyPoint.Torso);
            pickup.OnPickUp(Character);
            _wieldableCarryable.AddCarryable(pickup);
        }
        
        /// <summary>
        /// Attempts to drop a specified number of carried objects.
        /// </summary>
        /// <param name="amount">Number of objects to drop.</param>
        /// <returns>True if objects were dropped, false otherwise.</returns>
        private bool TryDropCarryable(int amount)
        {
            // If the specified amount is invalid or not carrying any objects, return false.
            if (amount <= 0 || !HasCarryables)
                return false;

            // Play the drop audio.
            Character.Audio.PlayClip(_dropAudio, BodyPoint.Torso);
            
            // Drop the specified number of carried objects.
            int i = 0;
            do
            {
                var pickup = _pickups.Pop();
                _wieldableCarryable.RemoveCarryable(pickup);
                pickup.OnDrop(Character);

                var definition = pickup.Definition;
                var dropPoint = Character.GetTransformOfBodyPoint(BodyPoint.Head);
                Vector3 dropForce = dropPoint.TransformVector(definition.DropForce);
                Character.ThrowObject(pickup.Rigidbody, dropForce, definition.DropTorque);

                ++i;
            } while (i < amount && HasCarryables);

            return true;
        }

        private void ForceDropAllCarryables(IWieldable wieldable)
        {
            // If carrying objects, attempt to drop them all and stop carrying.
            if (_wieldableCarryable.Wieldable == wieldable && TryDropCarryable(CarryCount))
            {
                StopObjectCarry();
            }
        }

        protected override void OnBehaviourStart(ICharacter character)
        {
            _controller = character.GetCC<IWieldablesControllerCC>();
            _controller.RegisterWieldable(_wieldableCarryable.Wieldable);
            character.HealthManager.Death += OnDeath;
        }

        protected override void OnBehaviourDestroy(ICharacter character)
        {
            character.HealthManager.Death -= OnDeath;
            if (HasCarryables)
            {
                InputManager.Instance.PopContext(_carryContext);
            }
        }

        private void OnDeath(in DamageArgs args) => ForceDropAllCarryables(_wieldableCarryable.Wieldable);

        #region Save & Load
        [Serializable]
        private sealed class SaveData
        {
            public DataIdReference<CarryableDefinition> CarryDef;
            public int CarryCount;
        }

        void ISaveableComponent.LoadMembers(object data)
        {
            var saveData = (SaveData)data;
            if (saveData.CarryDef.IsNull)
                return;

            CoroutineUtility.InvokeDelayed(this, CarryObjects, 0.01f);

            void CarryObjects()
            {
                var pickupPrefab = saveData.CarryDef.Def.Pickup;
                for (int i = 0; i < saveData.CarryCount; i++)
                {
                    var pickupInstance = Instantiate(pickupPrefab);
                    TryCarryObject(pickupInstance);
                }
            }
        }

        object ISaveableComponent.SaveMembers() => new SaveData
        {
            CarryDef = ActiveDefinition,
            CarryCount = HasCarryables ? CarryCount : 0
        };
        #endregion
    }
}