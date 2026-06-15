using PolymindGames.SaveSystem;
using UnityEngine;
using System;

namespace PolymindGames.BuildingSystem
{
    public enum BuildingPieceCenterPoint
    {
        Bottom,
        Center,
        Top
    }
    
    public sealed class GroupBuildingPiece : BuildingPiece, ISaveableComponent
    {
        [SerializeField, Title("Sockets")]
        private bool _requiresSockets;

        [SerializeField, DataReference(NullElement = "")]
        [ReorderableList(ListStyle.Lined, HasLabels = false, Foldable = false)]
        private DataIdReference<BuildingPieceCategoryDefinition>[] _spacesToOccupy;

        [SerializeField]
        [ReorderableList(ListStyle.Lined, elementLabel: "Socket")]
        private Socket[] _sockets;
        
        private IBuildingPieceGroup _parentGroup;   
        
        public override IBuildingPieceGroup ParentGroup => _parentGroup;
        
        public override bool TryPlace(Socket socket)
        {
            if (State != BuildingPieceState.InPlacementAllowed || (_requiresSockets && socket == null))
                return false;
            
            _parentGroup ??= socket != null
                ? socket.ParentBuildingPiece.ParentGroup
                : Instantiate(BuildingManager.Instance.DefaultGroupPrefab, transform.position, transform.rotation);
            
            _parentGroup.AddBuildingPiece(this);
            if (_parentGroup.BuildingPieces.Count > 1)
                OccupyAdjacentSockets();
            
            var targetState = Constructable.IsConstructed ? BuildingPieceState.Constructed : BuildingPieceState.Placed; 
            SetState(targetState);
            return true;
        }

        public override Vector3 GetCenter()
        {
            var bounds = GetWorldBounds();
            return BuildingManager.Instance.GroupPieceCenterPoint switch
            {
                BuildingPieceCenterPoint.Bottom => bounds.center.With(y: bounds.min.y + 0.1f),
                BuildingPieceCenterPoint.Center => bounds.center + new Vector3(0f, -0.1f, 0f),
                BuildingPieceCenterPoint.Top => bounds.center.With(y: bounds.max.y - 0.1f),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        public override ReadOnlySpan<Socket> GetSockets() => _sockets;

        public override void UpdatePlacement(Vector3 position, Quaternion rotation, Socket socket, bool hasSurface)
        {
            bool isPlacementAllowed;

            if (socket != null)
                isPlacementAllowed = !this.IsCollidingWithMask(socket, BuildingManager.Instance.OverlapCheckMask);
            else
                isPlacementAllowed = !_requiresSockets && hasSurface && !this.IsCollidingWithMask(BuildingManager.Instance.OverlapCheckMask);

            var state = isPlacementAllowed ? BuildingPieceState.InPlacementAllowed : BuildingPieceState.InPlacementDenied;
            
            SetState(state);
            transform.SetPositionAndRotation(position, rotation);
        }

        protected override void SetConstructedState()
        {
            base.SetConstructedState();
            
            // Avoids playing effects when fading the screen.
            if (Time.timeSinceLevelLoad > 1f)
            {
                if (!ReferenceEquals(_parentGroup, null) && _parentGroup.IsFullBuilt())
                    BuildingManager.Instance.FullBuildEffect.PlayAtPosition(transform.position, Quaternion.identity);
            }
        }

        protected override void Awake()
        {
            base.Awake();
            foreach (var socket in _sockets)
                socket.Init(this);
        }

        private void OnDestroy()
        {
            if (_parentGroup == null || UnityUtility.IsQuitting)
                return;
            
            ClearAdjacentSockets();
            _parentGroup.RemoveBuildingPiece(this);
        }

        /// <summary>
        /// Occupies adjacent sockets based on the current building piece's position and rotation.
        /// </summary>
        private void OccupyAdjacentSockets()
        {
            // Expand the world bounds slightly to ensure coverage
            var worldBounds = GetWorldBounds();
            worldBounds.Expand(0.15f);

            // Get the position and rotation of the current building piece
            transform.GetPositionAndRotation(out var position, out var rotation);

            // Loop through each building piece in the parent group
            foreach (var piece in _parentGroup.BuildingPieces)
            {
                // Skip the current building piece
                if (piece == this)
                    continue;
                
                // Loop through each socket of the current building piece
                foreach (var socket in piece.GetSockets())
                {
                    // Check if the socket's world position is inside the expanded world bounds
                    if (!worldBounds.IsPointInsideRotatedBounds(rotation, position, socket.WorldPosition))
                        continue;
                    
                    // Occupy the spaces of the socket with the spaces to occupy of the current building piece
                    socket.OccupySpaces(_spacesToOccupy);

                    // Loop through each socket of the current building piece again
                    foreach (var baseSocket in GetSockets())
                    {
                        // If the distance between sockets is less than a minimum threshold, occupy the spaces of the base socket with the spaces of the other socket
                        const float MinDistanceBetweenSockets = 0.125f;
                        if (Vector3.Distance(socket.WorldPosition, baseSocket.WorldPosition) < MinDistanceBetweenSockets)
                        {
                            baseSocket.OccupySpaces(socket);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Clears occupied spaces in adjacent sockets based on the provided building piece.
        /// </summary>
        private void ClearAdjacentSockets()
        {
            // Expand the world bounds of the provided building piece slightly to ensure coverage
            var worldBounds = GetWorldBounds();
            worldBounds.size += Vector3.one * 0.05f;

            // Loop through each building piece in the parent group
            foreach (var piece in _parentGroup.BuildingPieces)
            {
                // Skip the current building piece
                if (piece == this)
                    continue;
                
                // Loop through each socket of the current building piece
                foreach (var socket in piece.GetSockets())
                {
                    // Check if the socket's world position is inside the expanded world bounds of the provided building piece
                    if (worldBounds.Contains(socket.WorldPosition))
                        socket.UnoccupySpaces(_spacesToOccupy);
                }
            }
        }
        
        #region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            _parentGroup = GetComponentInParent<IBuildingPieceGroup>();
            _parentGroup.AddBuildingPiece(this);
            OccupyAdjacentSockets();

            State = (BuildingPieceState)data;
        }

        object ISaveableComponent.SaveMembers()
        {
            return State;
        }
        #endregion
        
        #region Editor
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (_sockets != null)
            {
                Transform trs = transform;
                foreach (var socket in _sockets)
                    socket.DrawGizmos(trs);
            }
        }
#endif
	    #endregion
    }
}