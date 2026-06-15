using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    [DisallowMultipleComponent, SelectionBase]
    public class Interactable : MonoBehaviour, IHoverableInteractable
    {
        [SerializeField]
        [Tooltip("Interactable text (could be used as a name), shows up in the UI when looking at this object.")]
        private string _interactTitle;

        [SerializeField, Multiline]
        [Tooltip("Interactable description, shows up in the UI when looking at this object.")]
        private string _interactDescription;
        
        [SerializeField, Range(0f, 10f), SpaceArea]
        [Tooltip("For how many seconds should the Player hold the interact button to interact with this object.")]
        private float _holdDuration;

        [SerializeField]
        private Vector3 _centerOffset;

        [SerializeField]
        private MaterialEffect _materialEffect;

        private Vector3 _adjustedCenterOffset;

        /// <inheritdoc/>
        public string Title
        {
            get => _interactTitle;
            set
            {
                if (ReferenceEquals(_interactTitle, value))
                    return;
                
                _interactTitle = value;
                DescriptionChanged?.Invoke();
            }
        }

        /// <inheritdoc/>
        public string Description
        {
            get => _interactDescription;
            set
            {
                if (ReferenceEquals(_interactDescription, value))
                    return;
                
                _interactDescription = value;
                DescriptionChanged?.Invoke();
            }
        }

        /// <inheritdoc/>
        public bool InteractionEnabled => enabled;
        
        /// <inheritdoc/>
        public float HoldDuration => _holdDuration;
        
        /// <inheritdoc/>
        public Vector3 CenterOffset => _adjustedCenterOffset;

        /// <inheritdoc/>
        public event InteractEventHandler Interacted;
        
        /// <inheritdoc/>
        public event HoverEventHandler HoverStarted;
        
        /// <inheritdoc/>
        public event HoverEventHandler HoverEnded;
        
        /// <inheritdoc/>
        public event UnityAction DescriptionChanged;

        /// <inheritdoc/>
        public void OnInteract(ICharacter character)
        {
            Interacted?.Invoke(this, character);
        }

        /// <inheritdoc/>
        public void OnHoverStart(ICharacter character)
        {
            HoverStarted?.Invoke(this, character);
            if (_materialEffect != null)
                _materialEffect.EnableEffect();
        }

        /// <inheritdoc/>
        public void OnHoverEnd(ICharacter character)
        {
            HoverEnded?.Invoke(this, character);
            if (_materialEffect != null)
                _materialEffect.DisableEffect();
        }

        private void Awake()
        {
            _adjustedCenterOffset = TryGetComponent(out Collider col)
                ? transform.InverseTransformPoint(col.bounds.center) + _centerOffset
                : _centerOffset;
        }

        #region Editor
#if UNITY_EDITOR
        protected virtual void Reset()
        {
            gameObject.layer = LayerConstants.Interactable;
            
            if (_materialEffect == null)
                _materialEffect = GetComponentInChildren<MaterialEffect>();
            
            if (_materialEffect == null)
                _materialEffect = gameObject.AddComponent<MaterialEffect>();
        }

        private void OnDrawGizmosSelected()
        {
            _adjustedCenterOffset = TryGetComponent(out Collider col)
                ? transform.InverseTransformPoint(col.bounds.center) + _centerOffset
                : _centerOffset;

            Gizmos.color = new Color(1f, 1f, 0.5f, 0.6f);
            Gizmos.DrawSphere(transform.TransformPoint(_adjustedCenterOffset), 0.075f);
            Gizmos.color = Color.white;
        }
#endif
        #endregion
    }
}
