using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    [DisallowMultipleComponent]
    public sealed class TriggerInteractable : MonoBehaviour, IHoverableInteractable
    {
        [SerializeField]
        [Tooltip("Interactable text (could be used as a name), shows up in the UI when looking at this object.")]
        private string _interactTitle;

        [SerializeField, Multiline]
        [Tooltip("Interactable description, shows up in the UI when looking at this object.")]
        private string _interactDescription;
        
        [SerializeField, Range(0f, 10f), Title("Settings")]
        [Tooltip("How long should the player sit in the trigger of this object (in seconds) to interact with it.")]
        private float _holdDuration; 
        
        [SerializeField, Title("Effects")]
        private MaterialEffect _materialEffect;
        
        private Vector3 _centerOffset;

        public bool InteractionEnabled => false;
        public float HoldDuration => _holdDuration;
        public Vector3 CenterOffset => _centerOffset;

        public string Title
        {
            get => _interactTitle;
            set
            {
                _interactTitle = value;
                DescriptionChanged?.Invoke();
            }
        }

        public string Description
        {
            get => _interactDescription;
            set
            {
                _interactDescription = value;
                DescriptionChanged?.Invoke();
            }
        }
        
        public event InteractEventHandler Interacted;
        public event HoverEventHandler HoverStarted;
        public event HoverEventHandler HoverEnded;
        public event UnityAction DescriptionChanged;

        public void OnInteract(ICharacter character) => Interacted?.Invoke(this, character);
        
        public void OnHoverStart(ICharacter character)
        {
            HoverStarted?.Invoke(this, character);
            if (_materialEffect != null)
                _materialEffect.EnableEffect();
        }

        public void OnHoverEnd(ICharacter character)
        {
            HoverEnded?.Invoke(this, character);
            if (_materialEffect != null)
                _materialEffect.DisableEffect();
        }
        
        private void OnTriggerEnter(Collider other)
        {
            if (other.gameObject.layer == LayerConstants.Character && other.TryGetComponent(out ICharacter character))
                OnInteract(character);
        }
        
        private void Awake()
        {
            var col = GetComponent<Collider>();
            _centerOffset = col.bounds.center;
        }
        
#if UNITY_EDITOR
        private void Reset()
        {
            gameObject.layer = LayerConstants.Interactable;
            
            if (_materialEffect == null)
                _materialEffect = GetComponentInChildren<MaterialEffect>();
            
            if (_materialEffect == null)
                _materialEffect = gameObject.AddComponent<MaterialEffect>();
        }
#endif
    }
}
