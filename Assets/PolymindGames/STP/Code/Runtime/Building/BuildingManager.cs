using UnityEngine;

namespace PolymindGames.BuildingSystem
{
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    [CreateAssetMenu(menuName = CreateMenuPath + "Building Manager", fileName = nameof(BuildingManager))]
    public sealed partial class BuildingManager : Manager<BuildingManager>
    {
        [SerializeField]
        private BuildingPieceDefinition _defaultBuildingPiece;
        
        [SerializeField, Title("Group Building Piece")]
        private BuildingPieceCenterPoint _groupPieceCenterPoint = BuildingPieceCenterPoint.Center;
        
        [SerializeField, PrefabObjectOnly]
        private BuildingPieceGroup _defaultGroupPrefab;
        
        [SerializeField, Title("Masks")]
        private LayerMask _freePlacementMask;
        
        [SerializeField]
        private LayerMask _overlapCheckMask;

        [SerializeField, Title("Materials")]
        private MaterialEffectConfig _placementAllowedMaterial;

        [SerializeField]
        private MaterialEffectConfig _placementDeniedMaterialEffect;

        [SerializeField, Title("Audio")]
        private EffectPairConfig _fullBuildEffect;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void Init() => LoadOrCreateInstance();

        public LayerMask FreePlacementMask => _freePlacementMask;
        public LayerMask OverlapCheckMask => _overlapCheckMask;
        public EffectPairConfig FullBuildEffect => _fullBuildEffect;
        public BuildingPieceGroup DefaultGroupPrefab => _defaultGroupPrefab;
        public BuildingPieceDefinition DefaultBuildingPiece => _defaultBuildingPiece;
        public BuildingPieceCenterPoint GroupPieceCenterPoint => _groupPieceCenterPoint;
        public MaterialEffectConfig PlacementAllowedMaterialEffect => _placementAllowedMaterial;
        public MaterialEffectConfig PlacementDeniedMaterialEffect => _placementDeniedMaterialEffect;
    }
}