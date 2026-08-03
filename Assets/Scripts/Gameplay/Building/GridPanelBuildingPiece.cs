using System;
using BackroomsSurvival.Gameplay.GridWorld;
using PolymindGames;
using PolymindGames.BuildingSystem;
using PolymindGames.SaveSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// An infill sheet the player clads into one cell of a placed <see cref="GridWallBuildingPiece"/>
    /// — a drywall panel today, a plank or a grate later. It exists only on a frame: aim anywhere
    /// else and it has no pose at all.
    ///
    /// Fourth <see cref="BuildingPiece"/> subclass, for the same reason the wall needed a third one.
    /// The vendor pair is unusable here: <see cref="GroupBuildingPiece"/> demands a socket and a grid
    /// wall deliberately publishes none (<c>GetSockets() =&gt; null</c> — the grid IS the anchor), and
    /// <see cref="FreeBuildingPiece"/> drops the piece wherever the ray lands. Both are <c>sealed</c>.
    /// Zero vendor source is edited.
    ///
    /// Replication is inherited whole, with no wire change: <c>StpBuildingPlacementWatcher</c> reports
    /// <c>Definition.Id</c> + world position + yaw, and position + yaw is exactly what a panel needs
    /// to be rebuilt. The host does not pose-cell dedup standalone pieces (only <c>is_group</c> ones),
    /// so nothing upstream has to learn that cells exist.
    ///
    /// DECLARED LIMIT: a panel does not know its frame after placement. The wire carries no
    /// parent-child relation, so demolishing a frame leaves its panels hanging in the air. Separate
    /// slice; it needs either a wire field or a client-side re-parent sweep, and both deserve their
    /// own decision.
    /// </summary>
    [RequireComponent(typeof(MaterialEffect))]
    public sealed class GridPanelBuildingPiece : BuildingPiece, ISaveableComponent
    {
        [SerializeField, Range(1f, 10f)]
        [Tooltip("How far the player can reach to clad a cell. Deliberately shorter than the wall's " +
                 "7 m: you fill a frame standing at it, and a long reach makes it easy to clad the " +
                 "far side of a room by accident.")]
        private float _buildRange = 4f;

        [SerializeField, Range(0f, 0.5f)]
        [Tooltip("Half this panel's own thickness. The pivot sits on the frame face pushed out by " +
                 "this much, so the sheet lands against the bars instead of half-sunk in them.")]
        private float _faceOffset = 0.067f;

        [SerializeField, Range(1, 10)]
        [Tooltip("How many grid cells wide this piece is. The frame's grid stays at 1 m cells (the " +
                 "only subdivision that divides its 5 × 4 m span exactly); a piece spans as many as " +
                 "its real size needs. A drywall sheet is 2 × 2.")]
        private int _footprintColumns = 2;

        [SerializeField, Range(1, 8)]
        [Tooltip("How many grid cells tall this piece is. See _footprintColumns.")]
        private int _footprintRows = 2;

        // Parking spot for the ghost while the player is not aiming at a frame. Same value the
        // survival book and StpBuildingPlacementWatcher.RearmPreview use to spawn a preview out of
        // sight, so a panel with nowhere to go behaves like one that has not been aimed yet.
        private const float ParkedY = -1000f;

        // ── Cell obstruction probe ───────────────────────────────────────────────
        // A thin slab hugging the OUTSIDE of the frame face, over this cell only.
        //
        // It cannot be the panel's own bounds: a future plank of a different thickness on the same
        // cell has to be caught too, and every clad panel — whatever its thickness — starts at the
        // frame face. So the probe starts there and reaches outward far enough to cross any of them.
        private const float ProbeNear = 0.005f;
        private const float ProbeDepth = 0.15f;

        // Shrink each cell edge by this much. Neighbouring cells share a boundary exactly, so a
        // full-size probe would report the panel next door as an obstruction and only every other
        // cell could ever be filled. Same trick as the wall's 2.35 half-length against its true 2.5.
        private const float CellProbeMargin = 0.05f;

        private int _placementMask;
        private bool _placementMaskResolved;

        /// <summary>Always standalone: a panel belongs to the frame's grid, not to an STP structure.</summary>
        public override IBuildingPieceGroup ParentGroup => null;

        /// <summary>No sockets. Nothing ever snaps to a panel — the frame's grid is the only anchor.</summary>
        public override ReadOnlySpan<Socket> GetSockets() => null;

        /// <summary>Bounds centre: unlike the wall, a panel's pivot is mid-cell, not on the floor.</summary>
        public override Vector3 GetCenter() => GetWorldBounds().center;

        /// <summary>
        /// Places the panel wherever the preview currently is. The <paramref name="socket"/> the
        /// controller found is ignored — this piece never snaps to sockets, and its pose was already
        /// resolved against the frame's grid by <see cref="UpdatePlacement"/>. Refusing unless the
        /// preview is ALLOWED is what makes that resolution authoritative; same guard the wall and
        /// <see cref="FreeBuildingPiece"/> use.
        /// </summary>
        public override bool TryPlace(Socket socket)
        {
            if (State != BuildingPieceState.InPlacementAllowed)
                return false;

            SetState(Constructable.IsConstructed ? BuildingPieceState.Constructed : BuildingPieceState.Placed);
            return true;
        }

        /// <summary>
        /// Overrides the vendor's free-placement pose entirely; the arguments are all discarded.
        /// <c>CharacterBuildController.GetFreePlacement</c> casts against <c>FreePlacementMask</c>,
        /// which omits the DynamicObject layer where macro layer 2 renders, so we re-cast against
        /// <c>FreePlacementMask | GridChunkBuilder.GeoMask</c> and resolve the cell ourselves.
        ///
        /// Not aiming at a frame parks the ghost far below the world rather than freezing it in
        /// place. That difference from the wall is deliberate and is the "no permanent preview"
        /// behaviour: a wall with no valid slot still makes sense hovering at its last one, whereas a
        /// drywall sheet floating unattached in a corridor reads as a bug. Aim at a frame and it
        /// appears on the cell; look away and it is gone.
        /// </summary>
        public override void UpdatePlacement(Vector3 position, Quaternion rotation, Socket socket, bool hasSurface)
        {
            if (!TryResolveCell(out var pose, out var frame))
            {
                transform.position = new Vector3(0f, ParkedY, 0f);
                SetState(BuildingPieceState.InPlacementDenied);
                return;
            }

            // Pose first: IsCollidingWithCharacters reads GetWorldBounds(), which is derived from the
            // live transform, so it must already be on the cell under test.
            transform.SetPositionAndRotation(pose.Position, Quaternion.Euler(0f, pose.Yaw, 0f));

            // Reservation first and cheapest: it covers the window where a cell is logically taken
            // but physically still empty, because the placement is already on its way to the host and
            // the replicated panel has not landed yet.
            bool allowed = !GridWallReservations.IsReserved(pose.Position, pose.Yaw)
                           && !IsCellObstructed(pose, frame)
                           && !this.IsCollidingWithCharacters();
            SetState(allowed ? BuildingPieceState.InPlacementAllowed : BuildingPieceState.InPlacementDenied);
        }

        /// <summary>
        /// Casts the aim ray and resolves the cell it lands on. False = not aiming at a placed frame,
        /// or aiming at one but off its two cell-bearing faces.
        /// </summary>
        private bool TryResolveCell(out GridPanelPose pose, out GridWallBuildingPiece frame)
        {
            pose = default;
            frame = null;

            var camera = UnityUtility.CachedMainCamera;
            if (camera == null)
                return false;

            var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!Physics.Raycast(ray, out var hit, _buildRange, PlacementMask, QueryTriggerInteraction.Ignore))
                return false;

            // The preview's own colliders are disabled from its first state transition onward, but
            // not on the very first frame — never snap the ghost to itself.
            if (HasCollider(hit.collider))
                return false;

            frame = hit.collider.GetComponentInParent<GridWallBuildingPiece>();

            // IsPlaced is Placed OR Constructed, so this also rejects ANOTHER piece's ghost: a wall
            // preview the player is holding must not be claddable, or the panel would snap to a frame
            // that may never be placed.
            if (frame == null || !frame.IsPlaced)
            {
                frame = null;
                return false;
            }

            frame.transform.GetPositionAndRotation(out var pivot, out var rotation);
            return GridPanelSnap.TrySnap(pivot, rotation.eulerAngles.y, frame.GridColumns, frame.GridRows,
                _footprintColumns, _footprintRows, hit.point, hit.normal, _faceOffset, out pose);
        }

        /// <summary>
        /// True when the cells this piece would cover are already clad, or something else is sitting
        /// in them. Probes the WHOLE occupied rect, not one cell — a 2 × 2 sheet has to be denied by a
        /// 1 × 1 patch anywhere under it, including a corner it only partly overlaps.
        ///
        /// The frame being filled is excluded explicitly and it has to be: its collider is the full
        /// 5 × 4 × 0.2 slot, so it overlaps every cell of its own grid and no panel could ever be
        /// placed. Only that one frame is excluded — a procedural wall, a perpendicular frame at a
        /// corner, or another player's panel all still deny the space.
        /// </summary>
        private bool IsCellObstructed(in GridPanelPose pose, GridWallBuildingPiece frame)
        {
            var orientation = Quaternion.Euler(0f, pose.Yaw, 0f);
            var outward = orientation * Vector3.forward;
            var centre = pose.SlotCentre + outward * (ProbeNear + ProbeDepth * 0.5f);

            var extents = new Vector3(
                Mathf.Max(0.01f, pose.Size.x * 0.5f - CellProbeMargin),
                Mathf.Max(0.01f, pose.Size.y * 0.5f - CellProbeMargin),
                ProbeDepth * 0.5f);

            int count = PhysicsUtility.OverlapBoxOptimized(centre, extents, orientation, out var colliders, PlacementMask);
            for (int i = 0; i < count; i++)
            {
                var collider = colliders[i];
                if (collider == null || HasCollider(collider) || frame.HasCollider(collider))
                    continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Vendor free-placement mask widened with every per-layer geometry layer. Resolved once:
        /// <c>BuildingManager.Instance</c> is a singleton asset and <c>GeoMask</c> is a static
        /// bitmask, so neither can change during a session. Same reasoning as the wall's.
        /// </summary>
        private int PlacementMask
        {
            get
            {
                if (!_placementMaskResolved)
                {
                    int vendorMask = BuildingManager.Instance != null
                        ? BuildingManager.Instance.FreePlacementMask.value
                        : 0;
                    _placementMask = vendorMask | GridChunkBuilder.GeoMask;
                    _placementMaskResolved = true;
                }

                return _placementMask;
            }
        }

        #region Save & Load
        // Same contract as the wall and FreeBuildingPiece: the piece is standalone, so its whole
        // persistent state is the visual state enum. The network replicator does not use this path
        // (it forces the state on the spawned instance directly); it is here so STP's own save system
        // round-trips a panel like any other free piece.
        void ISaveableComponent.LoadMembers(object data) => State = (BuildingPieceState)data;
        object ISaveableComponent.SaveMembers() => State;
        #endregion
    }
}
