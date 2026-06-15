using PolymindGames.SaveSystem;
using PolymindGames.Editor;
using UnityEngine;
using UnityEditor;
using System;

namespace PolymindGames.ResourceHarvesting.Editor
{
    public sealed class HarvestableCreationWizard : AssetCreationWizard
    {
        [SerializeField]
        private string _harvestableName;

        [SerializeField]
        private GameObject _harvestableVisuals;

        [SerializeField, TypeConstraint(typeof(Collider), TypeGrouping = TypeGrouping.ByFlatName)]
        private SerializedType _colliderType = new(typeof(CapsuleCollider));
        
        [SerializeField]
        private PhysicsMaterial _surface;
        
        [SerializeField]
        private HarvestableType _harvestableType;

        [SerializeField, IgnoreParent, BeginGroup, EndGroup]
        [ShowIf(nameof(_harvestableType), HarvestableType.ChoppableTree)]
        private ComplexTreeSettings _choppableTree = new();

        public override void Reset()
        {
        }

        public override string ValidateSettings()
        {
            if (_colliderType == null)
                return "Collider type is null";
    
            if (_choppableTree.DamagedSource == null)
                return "ChoppableTree damaged source is null";
    
            if (string.IsNullOrEmpty(_harvestableName))
                return "Harvestable Resource name is null or empty";
    
            if (_harvestableType != HarvestableType.ChoppableTree)
                return "Harvestable type is not ChoppableTree";
    
            return string.Empty;
        }

        public override void CreateAsset()
        {
            GameObject damaged = _harvestableType switch
            {
                HarvestableType.ChoppableTree => _choppableTree.CreateEngagedObject(),
                HarvestableType.Simple => null,
                _ => throw new ArgumentOutOfRangeException()
            };

            var definitionAsset = CreateDefinition(damaged);
            CreateHarvestable(definitionAsset);
        }

        private void CreateHarvestable(HarvestableResourceDefinition definition)
        {
            var instance = new GameObject(_harvestableName);
            
            var collider = (Collider)instance.AddComponent(_colliderType.Type);
            collider.sharedMaterial = _surface;
            
            instance.AddComponent<SaveableObject>();
            
            var visuals = Instantiate(_harvestableVisuals, instance.transform);
            
            var harvestable = instance.AddComponent<HarvestableResource>();
            harvestable.SetFieldValue("_definition", definition);
            harvestable.SetFieldValue("_dormant", visuals);
            
            instance.SetLayersInChildren(LayerConstants.DynamicObject); 

            SaveGameObjectWithName(instance.gameObject, "Harvestable_");
        }

        private HarvestableResourceDefinition CreateDefinition(GameObject damaged)
        {
            var instance = CreateInstance<HarvestableResourceDefinition>();
            instance.name = ObjectNames.NicifyVariableName(_harvestableName);
            instance.SetFieldValue("_harvestableName", _harvestableName);
            instance.SetFieldValue("_damagedPrefab", damaged);
            return SaveScriptableObjectWithName(instance, "Harvestable_");
        }

        protected override string GetCreationFolderName() => _harvestableName;

        private enum HarvestableType
        {
            ChoppableTree,
            
            [Tooltip("Not Implemented")]
            Simple
        }
        
        [Serializable]
        private class ComplexTreeSettings
        {
            public GameObject DamagedSource;
            
            [Help("Prefab or fbx file that contains the harvestable pieces.")]
            public GameObject HarvestablePieces;

            public GameObject Stump;

            public GameObject CreateEngagedObject()
            {
                return null;
            }
        }
    }
}