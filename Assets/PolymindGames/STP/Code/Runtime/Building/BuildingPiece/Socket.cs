using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames.BuildingSystem
{
    /// <summary>
    /// Represents a socket where a building piece can be attached.
    /// </summary>
    [Serializable]
    public sealed class Socket
    {
        [SerializeField]
        [Tooltip("The local offset of the socket relative to the parent building piece")]
        private Vector3 _offset; 

        [SerializeField, SpaceArea]
        [ReorderableList(ListStyle.Lined, elementLabel: "Offset")]
        [Tooltip("Additional offsets for specific categories of building pieces")]
        private BuildingPieceOffset[] _offsets; 

        private readonly List<DataIdReference<BuildingPieceCategoryDefinition>> _occupiedSpaces = new();
        private BuildingPiece _buildingPiece;

        /// <summary>
        /// Gets the parent building piece of this socket.
        /// </summary>
        public BuildingPiece ParentBuildingPiece => _buildingPiece;

        /// <summary>
        /// Gets the transform of the parent building piece.
        /// </summary>
        public Transform ParentTransform => _buildingPiece.transform;

        /// <summary>
        /// Gets the world position of this socket.
        /// </summary>
        public Vector3 WorldPosition => ParentTransform.TransformPoint(_offset);

        /// <summary>
        /// Gets the local position of this socket.
        /// </summary>
        public Vector3 LocalPosition => _offset;

        /// <summary>
        /// Initializes the socket with the parent building piece.
        /// </summary>
        /// <param name="buildingPiece">The parent building piece.</param>
        public void Init(BuildingPiece buildingPiece) => _buildingPiece = buildingPiece;

        /// <summary>
        /// Gets the building piece offset associated with the specified category.
        /// </summary>
        /// <param name="category">The category of the building piece.</param>
        /// <returns>The building piece offset.</returns>
        public BuildingPieceOffset GetBuildingPieceOffset(DataIdReference<BuildingPieceCategoryDefinition> category)
        {
            foreach (BuildingPieceOffset offset in _offsets)
            {
                if (offset.Category == category)
                    return offset;
            }

            return null; // Return null if no offset is found for the specified category
        }
        
        /// <summary>
        /// Occupies the specified spaces by adding them to the list of occupied spaces.
        /// </summary>
        /// <param name="spacesToOccupy">The spaces to occupy.</param>
        public void OccupySpaces(DataIdReference<BuildingPieceCategoryDefinition>[] spacesToOccupy)
        {
            foreach (var space in spacesToOccupy)
            {
                if (!_occupiedSpaces.Contains(space))
                    _occupiedSpaces.Add(space);
            }
        }
        
        /// <summary>
        /// Occupies spaces of this socket based on the occupied spaces of another socket.
        /// </summary>
        /// <param name="socket">The socket whose occupied spaces to occupy.</param>
        public void OccupySpaces(Socket socket)
        {
            foreach (var space in socket._occupiedSpaces)
            {
                if (!_occupiedSpaces.Contains(space))
                    _occupiedSpaces.Add(space);
            }
        }

        /// <summary>
        /// Unoccupies the specified spaces by removing them from the list of occupied spaces.
        /// </summary>
        /// <param name="spacesToUnoccupy">The spaces to unoccupy.</param>
        public void UnoccupySpaces(DataIdReference<BuildingPieceCategoryDefinition>[] spacesToUnoccupy)
        {
            foreach (var space in spacesToUnoccupy)
                _occupiedSpaces.Remove(space);
        }

        /// <summary>
        /// Checks if this socket supports the attachment of the specified building piece.
        /// </summary>
        /// <param name="buildingPiece">The building piece to check.</param>
        /// <returns>True if the socket supports the building piece; otherwise, false.</returns>
        public bool SupportsBuildingPiece(BuildingPiece buildingPiece)
        {
            var requiredSpace = new DataIdReference<BuildingPieceCategoryDefinition>(buildingPiece.Definition.ParentGroup.Id);

            foreach (var offset in _offsets)
            {
                if (offset.Category == requiredSpace)
                    return !_occupiedSpaces.Contains(requiredSpace);
            }

            return false;
        }

        #region Editor
#if UNITY_EDITOR
        public void DrawGizmos(Transform transform)
        {
            var oldMatrix = Gizmos.matrix;

            Gizmos.color = new Color(.1f, 1f, .1f, 0.65f);
            Gizmos.matrix = Matrix4x4.TRS(transform.TransformPoint(_offset), transform.rotation, Vector3.one * 0.2f);

            Gizmos.DrawCube(Vector3.zero, Vector3.one);

            Gizmos.matrix = oldMatrix;

            UnityEditor.Handles.color = new Color(0.3f, 0.4f, 0.3f, 0.2f);
            UnityEditor.Handles.SphereHandleCap(24, transform.TransformPoint(_offset), Quaternion.identity, 0.6f, EventType.Repaint);
        }
#endif
        #endregion

        #region Internal Types
        [Serializable]
        public sealed class BuildingPieceOffset
        {
            [SerializeField, DataReference(HasLabel = false, NullElement = "")]
            public DataIdReference<BuildingPieceCategoryDefinition> Category;

            [SerializeField]
            public Vector3 PositionOffset;

            [SerializeField]
            public Vector3 RotationOffset;
        }
        #endregion
    }
}