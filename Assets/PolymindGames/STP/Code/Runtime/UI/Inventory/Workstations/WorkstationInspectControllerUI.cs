using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames.UserInterface
{
    public interface IWorkstationInspector
    {
        Type WorkstationType { get; }

        void Inspect(IWorkstation workstation);
        void EndInspection();
    }

    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault1)]
    public sealed class WorkstationInspectControllerUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull]
        private UIPanel _defaultPanel;
        
        private readonly Dictionary<Type, IWorkstationInspector> _workstationInspectors = new();
        private IInventoryInspectionManagerCC _inventoryInspector;
        private IWorkstationInspector _activeInspector;


        protected override void Awake()
        {
            base.Awake();
            InitializeWorkstations();
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            _inventoryInspector = character.GetCC<IInventoryInspectionManagerCC>();
            _inventoryInspector.InspectionStarted += OnInspectionStarted;
            _inventoryInspector.InspectionEnded += OnInspectionEnded;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            _inventoryInspector.InspectionStarted -= OnInspectionStarted;
            _inventoryInspector.InspectionEnded -= OnInspectionEnded;
        }

        private void InitializeWorkstations()
        {
            foreach (IWorkstationInspector inspector in gameObject.GetComponentsInFirstChildren<IWorkstationInspector>())
            {
                if (!_workstationInspectors.ContainsKey(inspector.WorkstationType))
                {
                    _workstationInspectors.Add(inspector.WorkstationType, inspector);
                    inspector.EndInspection();
                }
            }
        }

        private void OnInspectionStarted()
        {
            var workstation = _inventoryInspector.Workstation;
            if (workstation != null && _workstationInspectors.TryGetValue(workstation.GetType(), out IWorkstationInspector objInspector))
            {
                objInspector.Inspect(workstation);
                _activeInspector = objInspector;
            }
            else
            {
                _defaultPanel.Show();
            }
        }

        private void OnInspectionEnded()
        {
            _activeInspector?.EndInspection();
            _activeInspector = null;
            _defaultPanel.Hide();
        }
    }
}