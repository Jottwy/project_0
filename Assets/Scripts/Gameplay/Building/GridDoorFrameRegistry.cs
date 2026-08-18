using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// Every <see cref="GridDoorFrameOpening"/> currently loaded, so a door leaf can find the frame
    /// the player is standing near WITHOUT a raycast. Every other clad-onto-a-placed-piece lookup in
    /// this project (<see cref="GridPanelBuildingPiece"/> onto a wall) works by raycasting and reading
    /// the component off whatever the ray hits — but a door frame's opening is deliberately walkable,
    /// so a ray aimed straight through it hits nothing. Same static-helper shape as
    /// <see cref="GridWallReservations"/>.
    /// </summary>
    public static class GridDoorFrameRegistry
    {
        private static readonly List<GridDoorFrameOpening> _openings = new();

        public static void Register(GridDoorFrameOpening opening)
        {
            if (!_openings.Contains(opening))
                _openings.Add(opening);
        }

        public static void Unregister(GridDoorFrameOpening opening) => _openings.Remove(opening);

        /// <summary>
        /// Nearest PLACED (or constructed) frame within <paramref name="maxDistance"/> of
        /// <paramref name="from"/> whose opening <paramref name="forward"/> is roughly pointed at, or
        /// null. The facing gate is a coarse dot-product against the opening's centre, not a raycast —
        /// standing near a doorway and glancing generally at it is meant to be enough.
        /// </summary>
        public static GridDoorFrameOpening FindNearestFacing(Vector3 from, Vector3 forward, float maxDistance,
            float minDot)
        {
            GridDoorFrameOpening best = null;
            float bestSqrDistance = maxDistance * maxDistance;

            for (int i = 0; i < _openings.Count; i++)
            {
                var opening = _openings[i];
                if (opening == null || !opening.Frame.IsPlaced)
                    continue;

                var openingCentre = opening.transform.TransformPoint(opening.HingeLocalPosition +
                    new Vector3(opening.LeafSize.x * 0.5f, opening.LeafSize.y * 0.5f, 0f));
                var toOpening = openingCentre - from;
                float sqrDistance = toOpening.sqrMagnitude;
                if (sqrDistance > bestSqrDistance)
                    continue;

                if (Vector3.Dot(forward, toOpening.normalized) < minDot)
                    continue;

                bestSqrDistance = sqrDistance;
                best = opening;
            }

            return best;
        }
    }
}
