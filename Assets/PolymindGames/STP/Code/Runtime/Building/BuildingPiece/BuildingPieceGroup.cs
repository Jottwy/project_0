using System.Collections.Generic;
using UnityEngine;
using System;
using PolymindGames.SaveSystem;

namespace PolymindGames.BuildingSystem
{
    public sealed class BuildingPieceGroup : MonoBehaviour, IBuildingPieceGroup, ISaveableComponent
    {
        private readonly List<BuildingPiece> _buildingPieces = new();

        private Bounds _worldBounds;
        

        public IReadOnlyList<BuildingPiece> BuildingPieces  => _buildingPieces;
        
        /// <summary>
        /// Adds a building piece to the group.
        /// </summary>
        /// <param name="buildingPiece">The building piece to add.</param>
        public void AddBuildingPiece(BuildingPiece buildingPiece)
        {
#if DEBUG
            if (buildingPiece == null)
            {
                Debug.LogError("Building piece is null.");
                return;
            }

            if (_buildingPieces.Contains(buildingPiece))
            {
                Debug.LogWarning("Building piece is already part of this structure.", buildingPiece.gameObject);
                return;
            }
#endif
            
            SetNameOfBuildingPiece(buildingPiece, _buildingPieces.Count);
            buildingPiece.transform.SetParent(transform);
            
            _buildingPieces.Add(buildingPiece);

            if (_buildingPieces.Count == 1)
                _worldBounds = buildingPiece.GetWorldBounds();
            else
                _worldBounds.Encapsulate(buildingPiece.GetWorldBounds());
        }

        /// <summary>
        /// Removes a building piece from the group.
        /// </summary>
        /// <param name="buildingPiece">The building piece to remove.</param>
        public void RemoveBuildingPiece(BuildingPiece buildingPiece)
        {
#if DEBUG
            if (buildingPiece == null)
            {
                Debug.LogError("Building piece is null.");
                return;
            }
#endif
            if (_buildingPieces.Remove(buildingPiece))
            {
                if (_buildingPieces.Count == 0)
                {
                    Destroy(gameObject);
                    return;
                }

                _worldBounds = _buildingPieces[0].GetWorldBounds();
                for (int i = 1; i < _buildingPieces.Count; i++)
                    _worldBounds.Encapsulate(_buildingPieces[i].GetWorldBounds());
            }
        }

        /// <summary>
        /// Clears all building pieces from the group.
        /// </summary>
        public void Clear()
        {
            _buildingPieces.Clear();
            Destroy(gameObject);
        }

        public Bounds GetWorldBounds() => _worldBounds;

        private static void SetNameOfBuildingPiece(BuildingPiece buildingPiece, int index) =>
            buildingPiece.gameObject.name = buildingPiece.Definition.Name + index;

        #region Save & Load
        [Serializable]
        private sealed class SaveData
        {
            public int BuildingPieceId;
            public Vector3 Position;
            public Vector3 Rotation;
        }

        void ISaveableComponent.LoadMembers(object data)
        {
            var allSaveData = (SaveData[])data;

            // Load buildables into structure.
            for (int i = 0; i < allSaveData.Length; i++)
            {
                var saveData = allSaveData[i];
                var prefab = BuildingPieceDefinition.GetWithId(saveData.BuildingPieceId).Prefab;
                var instance = Instantiate(prefab, saveData.Position, Quaternion.Euler(saveData.Rotation), transform);
                SetNameOfBuildingPiece(instance, i);
            }
        }

        object ISaveableComponent.SaveMembers()
        {
            var saveData = new SaveData[BuildingPieces.Count];
            for (int i = 0; i < BuildingPieces.Count; i++)
            {
                var piece = BuildingPieces[i];
                piece.transform.GetPositionAndRotation(out var position, out var rotation);
                saveData[i] = new SaveData
                {
                    BuildingPieceId = piece.Definition.Id,
                    Position = position,
                    Rotation = rotation.eulerAngles,
                };
            }

            return saveData;
        }
        #endregion
    }
}