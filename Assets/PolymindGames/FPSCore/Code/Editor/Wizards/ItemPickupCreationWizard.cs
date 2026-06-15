using PolymindGames.SurfaceSystem;
using PolymindGames.SaveSystem;
using PolymindGames.Editor;
using UnityEngine;

namespace PolymindGames.InventorySystem.Editor
{
    public sealed class ItemPickupCreationWizard : AssetCreationWizard
    {
        [SerializeField]
        private ItemDefinition _definition;

        [SerializeField, SpaceArea]
        private GameObject _model;

        [SerializeField]
        [TypeConstraint(typeof(ItemPickup), TypeGrouping = TypeGrouping.ByFlatName)]
        private SerializedType _pickupType = new(typeof(ItemPickup));

        [SerializeField, Range(1, 24)]
        private int _count = 1;

        [SerializeField]
        private AudioData _pickUpAudio = new(null);

        [SerializeField, SpaceArea]
        [TypeConstraint(typeof(Collider))]
        private SerializedType _colliderType = new(typeof(BoxCollider));

        [SerializeField]
        private PhysicsMaterial _surface;

        [SerializeField]
        private MaterialEffectConfig _interactEffect;

        public override string ValidateSettings()
        {
            if (_definition == null)
                return "Definition is null";
    
            if (_model == null)
                return "Model is null";
    
            if (_pickupType == null)
                return "Pickup type is null";
    
            if (_colliderType == null)
                return "Collider type is null";
    
            return string.Empty;
        }

        public override void CreateAsset()
        {
            var gameObject = Instantiate(_model);
            gameObject.SetLayersInChildren(LayerConstants.Interactable);

            var collider = gameObject.GetOrAddDerivedComponent<Collider>(_colliderType);
            collider.sharedMaterial = _surface;

            if (collider is MeshCollider meshCollider)
                meshCollider.convex = true;

            gameObject.GetOrAddComponent<Rigidbody>().mass = _definition.Weight;

            if (_interactEffect != null)
            {
                gameObject.GetOrAddComponent<MaterialEffect>()
                    .SetFieldValue("_defaultEffect", _interactEffect);
            }

            gameObject.GetOrAddComponent<SaveableObject>();
            gameObject.GetOrAddComponent<SurfaceImpactHandler>();
            gameObject.GetOrAddComponent<Interactable>();

            // Add the item pickup component..
            var pickup = gameObject.GetAddOrSwapComponent<ItemPickup>(_pickupType);
            pickup.SetFieldValue("_item", new DataIdReference<ItemDefinition>(_definition.Id));
            pickup.SetFieldValue("_minCount", _count);
            pickup.SetFieldValue("_maxCount", _count);
            pickup.SetFieldValue("_addAudio", _pickUpAudio);

            SaveGameObjectWithName(gameObject, "Pickup");
        }

        protected override string GetCreationFolderName() => _definition.Name;
    }
}