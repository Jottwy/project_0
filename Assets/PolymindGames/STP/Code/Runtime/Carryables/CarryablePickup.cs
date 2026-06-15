using PolymindGames.SaveSystem;
using UnityEngine.Rendering;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [RequireComponent(typeof(IHoverableInteractable), typeof(Rigidbody))]
    public sealed class CarryablePickup : MonoBehaviour
    {
        [SerializeField]
        [DataReference(NullElement = "", HasAssetReference = true)]
        [Tooltip("The corresponding carryable definition.")]
        private DataIdReference<CarryableDefinition> _definition;
        
        private MeshRenderer _renderer;
        private Rigidbody _rigidbody;
        private Collider _collider;
        private bool _isPickedUp;

        public CarryableDefinition Definition => _definition.Def;
        public Rigidbody Rigidbody => _rigidbody;
        
        public bool TryUse(ICharacter character)
        {
            foreach (var action in GetComponents<ICarryableAction>())
            {
                if (action.TryDoAction(character))
                {
                    Release();
                    return true;
                }
            }

            return false;
        }
        
        public void OnPickUp(ICharacter character = null)
        {
            if (TryGetComponent(out MaterialEffect materialEffect))
                materialEffect.DisableEffect();
            
            _rigidbody.isKinematic = true;
            _rigidbody.interpolation = RigidbodyInterpolation.None;
            _collider.enabled = false;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            
            if (TryGetComponent(out SaveableObject saveableObject))
                saveableObject.IsSaveable = false;
        }

        public void OnDrop(ICharacter character = null)
        {
            _rigidbody.isKinematic = false;
            _collider.enabled = true;
            _renderer.shadowCastingMode = ShadowCastingMode.On;

            if (TryGetComponent(out SaveableObject saveableObject))
                saveableObject.IsSaveable = true;
        }

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
            _renderer = GetComponent<MeshRenderer>();
        }

        private void Start()
        {
            var interactable = GetComponent<IHoverableInteractable>();
            interactable.Interacted += PickUpCarryable;
            
            if (string.IsNullOrEmpty(interactable.Title))
                interactable.Title = "Carry";
            
            if (string.IsNullOrEmpty(interactable.Description))
                interactable.Description = _definition.Name;
        }

        private void PickUpCarryable(IInteractable interactable, ICharacter character)
        {
            if (character.TryGetCC(out ICarryableControllerCC objectCarry))
                objectCarry.TryCarryObject(this);
        }

        private void Reset()
        {
            if (!gameObject.HasComponent<IInteractable>())
                gameObject.AddComponent<Interactable>();
        }

        // TODO: Implement pooling
        private void Release()
        {
            Destroy(gameObject);
        }
    }
}