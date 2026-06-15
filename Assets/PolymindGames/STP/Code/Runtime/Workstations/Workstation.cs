using PolymindGames.InventorySystem;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames
{
    [RequireComponent(typeof(IHoverableInteractable))]
    public abstract class Workstation : MonoBehaviour, IWorkstation
    {
        [SerializeField]
        private InspectionSettings _openStationSettings;
        
        [SerializeField]
        private InspectionSettings _closeStationSettings;
        
        private IHoverableInteractable _interactable;

        public virtual IReadOnlyList<IItemContainer> GetContainers() => Array.Empty<ItemContainer>();
        public virtual string Name => _interactable.Title;

        void IWorkstation.BeginInspection()
        {
            AudioManager.Instance.PlayClip3D(_openStationSettings.Audio, transform.position);
            _openStationSettings.Event.Invoke();
        }

        void IWorkstation.EndInspection()
        {
            AudioManager.Instance.PlayClip3D(_closeStationSettings.Audio, transform.position);
            _closeStationSettings.Event.Invoke();
        }

        protected virtual void Start()
        {
            _interactable = GetComponent<IHoverableInteractable>();
            _interactable.Interacted += StartInspection;
        }
        
        private void StartInspection(IInteractable interactable, ICharacter character)
        {
            if (character.TryGetCC(out IInventoryInspectionManagerCC inspection))
                inspection.StartInspection(this);
        }

#if UNITY_EDITOR
        private void Reset()
        {
            if (!gameObject.HasComponent<IInteractable>())
                gameObject.AddComponent<Interactable>();
        }
#endif

        #region Internal Types
        [Serializable]
        protected struct InspectionSettings
        {
            public AudioData Audio;
            
            [SpaceArea(3f)]
            public UnityEvent Event; 
        }
        #endregion
    }
}