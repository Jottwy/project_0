using UnityEngine;
using System.Linq;
using System;

namespace PolymindGames.BuildingSystem
{
    /// <summary>
    /// Definition for a building piece used in construction.
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Building/Building Piece Definition", fileName = "BuildingPiece_")]
    public sealed class BuildingPieceDefinition : GroupMemberDefinition<BuildingPieceDefinition, BuildingPieceCategoryDefinition>
    {
        [SerializeField, SpritePreview, SpaceArea]
        [Tooltip("The icon representing the building piece.")]
        private Sprite _icon;

        [SerializeField]
        [Tooltip("The prefab of the building piece.")]
        private BuildingPiece _prefab;

        [SerializeField, Multiline]
        [Tooltip("A description of the building piece.")]
        private string _description;

        [SerializeField, NotNull, Title("Effects")]
        [Tooltip("Effects played when placing the building piece.")]
        private EffectPairConfig _placeEffects;

        [SerializeField, NotNull]
        [Tooltip("Effects played when constructing the building piece.")]
        private EffectPairConfig _constructEffects;

        private static BuildingPieceDefinition[] _groupPiecesDefinitions;

        /// <summary>
        /// Gets the icon representing the building piece.
        /// </summary>
        public override Sprite Icon => _icon;

        /// <summary>
        /// Gets the description of the building piece.
        /// </summary>
        public override string Description => _description;

        /// <summary>
        /// Gets the prefab of the building piece.
        /// </summary>
        public BuildingPiece Prefab => _prefab;

        /// <summary>
        /// Gets the effects played when placing the building piece.
        /// </summary>
        public EffectPairConfig PlaceEffects => _placeEffects;

        /// <summary>
        /// Gets the effects played when constructing the building piece.
        /// </summary>
        public EffectPairConfig ConstructEffects => _constructEffects;

        /// <summary>
        /// Gets an array of all group building piece definitions.
        /// </summary>
        public static BuildingPieceDefinition[] GroupBuildingPiecesDefinitions
        {
            get
            {
                return _groupPiecesDefinitions ??= GetGroupBuildingPieces();

                static BuildingPieceDefinition[] GetGroupBuildingPieces()
                {
                    return Definitions.Where(def => def.HasParentGroup && def.Prefab is GroupBuildingPiece).ToArray();
                }
            }
        }

        /// <summary>
        /// Gets the next group building piece definition relative to the specified definition.
        /// </summary>
        /// <param name="definition">The current building piece definition.</param>
        /// <param name="next">True to get the next building piece definition; false to get the previous one.</param>
        /// <returns>The next group building piece definition.</returns>
        public static BuildingPieceDefinition GetNextGroupBuildingPiece(BuildingPieceDefinition definition, bool next)
        {
            var buildingPieces = GroupBuildingPiecesDefinitions;

            int index = Mathf.Max(Array.IndexOf(buildingPieces, definition), 0);
            index = (int)Mathf.Repeat(index + (next ? 1 : -1), buildingPieces.Length);

            return buildingPieces[index];
        }

        #region Editor
#if UNITY_EDITOR
        public override void Validate_EditorOnly(in ValidationContext validationContext)
        {
            base.Validate_EditorOnly(in validationContext);

            if (validationContext.Trigger is ValidationTrigger.Duplicated)
                _prefab = null;
        }
#endif
        #endregion
    }
}