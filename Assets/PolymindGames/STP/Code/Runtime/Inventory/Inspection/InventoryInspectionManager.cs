using PolymindGames.InventorySystem;
using System.Collections.Generic;
using PolymindGames.InputSystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Handles any type of inventory inspection (e.g. Backpack, external containers etc.)
    /// </summary>
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/player/modules-and-behaviours/inventory#inventory-inspect-manager-module")]
    public sealed class InventoryInspectionManager : CharacterBehaviour, IInventoryInspectionManagerCC
    {
        [SerializeField]
        private InputContext _inventoryContext;

        [SerializeField, Range(0f, 10f)]
        [Tooltip("How often can the inventory inspection be toggled (e.g. open/close backpack).")]
        private float _toggleThreshold = 0.35f;

        private readonly List<IItemContainer> _inspectedContainers = new();
        private float _nextAllowedToggleTime;

        public bool IsInspecting { get; private set; }
        public IWorkstation Workstation { get; private set; }
        public IReadOnlyList<IItemContainer> InspectedContainers => _inspectedContainers;

        public event UnityAction InspectionStarted;
        public event UnityAction InspectionPostStarted;
        public event UnityAction InspectionEnded;
    
        public void StartInspection(IWorkstation workstation)
        {
            bool isSameWorkstation = workstation != null && Workstation == workstation;

            if (IsInspecting || Time.time < _nextAllowedToggleTime || isSameWorkstation)
                return;

            Workstation = workstation;

            IsInspecting = true;
            _nextAllowedToggleTime = Time.time + _toggleThreshold;

            UnityUtility.UnlockCursor();
            InputManager.Instance.PushEscapeCallback(StopInspection);
            InputManager.Instance.PushContext(_inventoryContext);

            if (Workstation != null)
            {
                Workstation.BeginInspection();
                _inspectedContainers.AddRange(Workstation.GetContainers());
            }

            InspectionStarted?.Invoke();
            InspectionPostStarted?.Invoke();
        }

        public void StopInspection()
        {
            if (!IsInspecting)
                return;

            Workstation?.EndInspection();
            _inspectedContainers.Clear();

            IsInspecting = false;
            _nextAllowedToggleTime = Time.time + _toggleThreshold;

            UnityUtility.LockCursor();
            InputManager.Instance.PopEscapeCallback(StopInspection);
            InputManager.Instance.PopContext(_inventoryContext);

            InspectionEnded?.Invoke();
            Workstation = null;
        }

        public void InspectContainer(IItemContainer container)
        {
            if (!_inspectedContainers.Contains(container))
                _inspectedContainers.Add(container);
        }

        public void RemoveContainerFromInspection(IItemContainer container) => _inspectedContainers.Remove(container);

        protected override void OnBehaviourStart(ICharacter character) => character.HealthManager.Death += OnDeath;
        protected override void OnBehaviourDestroy(ICharacter character) => character.HealthManager.Death -= OnDeath;
        private void OnDeath(in DamageArgs args) => StopInspection();
    }
}