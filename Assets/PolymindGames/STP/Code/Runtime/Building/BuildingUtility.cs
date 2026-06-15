using System.Collections.Generic;
using UnityEngine;

namespace PolymindGames.BuildingSystem
{
    public static class BuildingUtility
    {
        private static readonly List<BuildRequirement> _buildRequirements = new(4);

        public static bool IsCollidingWithCharacters(this BuildingPiece piece)
        {
            if (piece.ParentGroup == null)
                return IsCollidingWithMask(piece, LayerConstants.CharacterMask);

            // Check if there's any characters colliding with this preview.
            foreach (var buildingPiece in piece.ParentGroup.BuildingPieces)
            {
                if (IsCollidingWithMask(buildingPiece, LayerConstants.CharacterMask))
                    return true;
            }

            return false;
        }

        public static bool IsFullBuilt(this IBuildingPieceGroup group)
        {
            foreach (var piece in group.BuildingPieces)
            {
                if (!piece.Constructable.IsConstructed)
                    return false;
            }

            return true;
        }

        public static bool IsCollidingWithMask(this BuildingPiece buildingPiece, int mask)
        {
            int count = PhysicsUtility.OverlapBoxOptimized(buildingPiece.GetWorldBounds(), buildingPiece.transform.rotation, out var colliders, mask);

            for (int i = 0; i < count; i++)
            {
                var collider = colliders[i];

                // If we hit a terrain, ignore it.
                if (buildingPiece.HasCollider(collider) || collider is TerrainCollider)
                    continue;

                return true;
            }

            return false;
        }

        public static bool IsCollidingWithMask(this BuildingPiece buildingPiece, Socket socket, int mask)
        {
            int size = PhysicsUtility.OverlapBoxOptimized(buildingPiece.GetWorldBounds(), buildingPiece.transform.rotation, out var colliders, mask);
            bool hasSocket = socket != null;

            for (int i = 0; i < size; i++)
            {
                var collider = colliders[i];

                // If we hit a terrain, ignore it.
                if (buildingPiece.HasCollider(collider) || collider is TerrainCollider)
                    continue;

                if (hasSocket && collider.TryGetComponent<BuildingPiece>(out var collidedPiece))
                {
                    // If we hit another building piece with the same parent group as the valid socket, ignore it.
                    if (collidedPiece.ParentGroup == socket.ParentBuildingPiece.ParentGroup)
                        continue;
                }

                return true;
            }

            return false;
        }

        public static bool IsPartOfSameGroup(this IConstructable first, IConstructable second)
        {
            if (ReferenceEquals(first, null) || ReferenceEquals(second, null) || first.BuildingPiece.ParentGroup == null)
                return false;

            return ReferenceEquals(first.BuildingPiece.ParentGroup, second.BuildingPiece.ParentGroup);
        }

        public static IReadOnlyList<BuildRequirement> GetAllBuildRequirements(this IBuildingPieceGroup group)
        {
            _buildRequirements.Clear();

            foreach (var buildingPiece in group.BuildingPieces)
            {
                var constructable = buildingPiece.Constructable;

                foreach (var requirement in constructable.GetBuildRequirements())
                {
                    int indexOfExisting = IndexOfExisting(_buildRequirements, requirement.BuildMaterialId);

                    if (indexOfExisting != -1)
                    {
                        _buildRequirements[indexOfExisting] = new BuildRequirement(_buildRequirements[indexOfExisting].BuildMaterial,
                            _buildRequirements[indexOfExisting].RequiredAmount + requirement.RequiredAmount,
                            _buildRequirements[indexOfExisting].CurrentAmount + requirement.CurrentAmount);
                    }
                    else
                        _buildRequirements.Add(requirement);
                }
            }

            return _buildRequirements;

            static int IndexOfExisting(List<BuildRequirement> requirements, int id)
            {
                for (int i = 0; i < requirements.Count; i++)
                {
                    var requirement = requirements[i];
                    if (requirement.BuildMaterialId == id)
                        return i;
                }

                return -1;
            }
        }
    }
}