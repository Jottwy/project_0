using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    public abstract class WorkstationInspectorBaseUI<T> : CharacterUIBehaviour, IWorkstationInspector where T : class, IWorkstation
    {
        [SerializeField, SceneObjectOnly]
        private TextMeshProUGUI _stationNameText;

        [SerializeField, NotNull]
        private UIPanel _stationPanel;
        
        public Type WorkstationType => typeof(T);
        protected T Workstation { get; private set; }
        
        protected TextMeshProUGUI StationNameText => _stationNameText;
        protected UIPanel StationPanel => _stationPanel;

        public void Inspect(IWorkstation workstation)
        {
            Workstation = (T)workstation;

            if (_stationNameText != null)
                _stationNameText.text = workstation.Name;
            
            _stationPanel.Show();
            
            OnInspectionStarted(Workstation);
        }

        public void EndInspection()
        {
            if (Workstation != null)
                OnInspectionEnded(Workstation);

            Workstation = null;
            _stationPanel.Hide();
        }

        protected abstract void OnInspectionStarted(T workstation);
        protected abstract void OnInspectionEnded(T workstation);

        private void Reset()
        {
            _stationPanel = GetComponent<UIPanel>();
            _stationNameText = GetComponentInChildren<TextMeshProUGUI>();
        }
    }
}