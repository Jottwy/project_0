using PolymindGames.SaveSystem;
using PolymindGames.Editor;
using UnityEngine;

namespace PolymindGames.BuildingSystem.Editor
{
    public sealed class BuildingPieceCreationWizard : AssetCreationWizard
    {
        [SerializeField]
        private BuildingPieceDefinition _definition;

        [SerializeField]
        private GameObject _model;
        
        [SerializeField]
        [TypeConstraint(typeof(BuildingPiece), TypeGrouping = TypeGrouping.ByFlatName)]
        private SerializedType _buildingPieceType = new(typeof(BuildingPiece));

        [SerializeField]
        [TypeConstraint(typeof(Collider), TypeGrouping = TypeGrouping.ByFlatName)]
        private SerializedType _colliderType = new(typeof(BoxCollider));

        [SerializeField]
        private bool _addMaterialEffect = true;
        
        [SerializeField]
        private bool _isSaveable = true;


        public override string ValidateSettings()
        {
            if (_definition == null)
                return "Definition is null";
        
            if (_model == null)
                return "Model is null";
        
            if (_buildingPieceType?.Type == null)
                return "Building piece type is not set or invalid";
        
            if (_colliderType?.Type == null)
                return "Collider type is not set or invalid";
        
            return string.Empty;
        }

        public override void CreateAsset()
        {
            var gameObject = Instantiate(_model);
            gameObject.AddComponent(_colliderType.Type);
            
            if (_isSaveable) gameObject.GetOrAddComponent<SaveableObject>();
            if (_addMaterialEffect) gameObject.GetOrAddComponent<MaterialEffect>();

            var buildable = gameObject.GetAddOrSwapComponent<BuildingPiece>(_buildingPieceType.Type);
            buildable.SetFieldValue("_definition", _definition);
        }

        protected override string GetCreationFolderName() => _definition.Name;
    }
}