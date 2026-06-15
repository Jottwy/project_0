using UnityEngine;
using System;
using PolymindGames.SaveSystem;

namespace PolymindGames.BuildingSystem
{
    [RequireComponent(typeof(MaterialEffect))]
    public sealed class FreeBuildingPiece : BuildingPiece, ISaveableComponent
    {
        public override IBuildingPieceGroup ParentGroup => null;
        
        public override bool TryPlace(Socket socket)
        {
            if (State != BuildingPieceState.InPlacementAllowed)
                return false;

            var targetState = Constructable.IsConstructed ? BuildingPieceState.Constructed : BuildingPieceState.Placed; 
            SetState(targetState);
            return true;
        }

        public override Vector3 GetCenter()
        {
            var worldBounds = GetWorldBounds();
            Vector3 center = worldBounds.center;
            return new Vector3(center.x, center.y - worldBounds.extents.y, center.z);
        }

        public override ReadOnlySpan<Socket> GetSockets() => null;
        
        public override void UpdatePlacement(Vector3 position, Quaternion rotation, Socket socket, bool hasSurface)
        {
            bool isPlacementAllowed = hasSurface && !this.IsCollidingWithMask(BuildingManager.Instance.OverlapCheckMask);
            var state = isPlacementAllowed ? BuildingPieceState.InPlacementAllowed : BuildingPieceState.InPlacementDenied;
            
            SetState(state);
            transform.SetPositionAndRotation(position, rotation);
        }
        
        #region Save & Load
        void ISaveableComponent.LoadMembers(object data)
        {
            State = (BuildingPieceState)data;
        }

        object ISaveableComponent.SaveMembers()
        {
            return State;
        }
        #endregion
    }
}