using System;
using BackroomsSurvival.Gameplay.GridWorld;
using PolymindGames;
using PolymindGames.BuildingSystem;
using PolymindGames.SaveSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// The leaf that hangs in a placed <see cref="GridDoorFrameOpening"/> — the door frame's
    /// counterpart to <see cref="GridPanelBuildingPiece"/>'s drywall sheet. It cannot be found the
    /// panel's way (raycast onto the piece it clads into) because a door frame's opening is
    /// deliberately walkable: a ray aimed straight through it hits nothing. Resolved instead via
    /// <see cref="GridDoorFrameRegistry"/> — proximity plus a coarse facing check.
    ///
    /// Fifth <see cref="BuildingPiece"/> subclass in this project, for the same reason the other three
    /// needed one: <see cref="GroupBuildingPiece"/> demands a socket, <see cref="FreeBuildingPiece"/>
    /// drops wherever the ray lands unsnapped. Both are sealed. Zero vendor source is edited — the
    /// vendor's own <c>Door</c> component (open/close, damage-to-open, audio) sits on this piece's
    /// ROOT unmodified and does the swinging; this class only owns placement.
    ///
    /// Pivot is the HINGE, not the centre or the floor the other pieces use: <c>Door</c> swings by
    /// rotating <c>transform.localRotation</c> on whatever GameObject it lives on, so this piece's own
    /// root IS the swing axis and the visual leaf sits offset sideways from it.
    /// </summary>
    [RequireComponent(typeof(MaterialEffect))]
    public sealed class GridDoorLeafBuildingPiece : BuildingPiece, ISaveableComponent
    {
        [SerializeField, Range(1f, 10f)]
        [Tooltip("How far the player can reach to hang a leaf. Shorter than the wall's 7 m for the " +
                 "same reason the drywall panel's is: you hang a door standing at the frame.")]
        private float _buildRange = 4f;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("Minimum dot(camera.forward, direction-to-opening) to count as \"looking at it\". " +
                 "Coarse on purpose — there is nothing to raycast against in an open doorway.")]
        private float _minFacingDot = 0.5f;

        // Parked below the world while not near a frame, same convention as the drywall panel and
        // StpBuildingPlacementWatcher.RearmPreview.
        private const float ParkedY = -1000f;

        // The obstruction probe's bottom edge stops here instead of at the leaf's own floor-level
        // bottom (Y=0): the floor slab sits right there too (~0.08 m thick, centred on the floor
        // plane), so a probe reaching down to it self-denies EVERY attempt against the floor itself.
        // Same fix, same reason, as GridWallBuildingPiece.IsSlotObstructed's BandBottom — confirmed
        // live (obstructed by 'Slab', 2026-08-18).
        private const float FloorClearance = 0.2f;

        // Shrinks the obstruction probe slightly inside the leaf's own footprint so the frame's own
        // jamb/header colliders — flush against these exact edges — never self-deny the slot. Same
        // trick as the wall's 2.35 half-length and the panel's CellProbeMargin.
        private const float ProbeMargin = 0.02f;

        private int _placementMask;
        private bool _placementMaskResolved;

        /// <summary>Always standalone: a leaf belongs to its frame's one slot, not to an STP structure.</summary>
        public override IBuildingPieceGroup ParentGroup => null;

        /// <summary>No sockets. Nothing ever snaps to a leaf — the frame's opening is the only anchor.</summary>
        public override ReadOnlySpan<Socket> GetSockets() => null;

        public override Vector3 GetCenter() => GetWorldBounds().center;

        /// <summary>
        /// Places the leaf wherever the preview currently is. The <paramref name="socket"/> the
        /// controller found is ignored — this piece never snaps to sockets, and its pose was already
        /// resolved against the frame's opening by <see cref="UpdatePlacement"/>. Refusing unless the
        /// preview is ALLOWED is the same guard every other piece here uses.
        /// </summary>
        public override bool TryPlace(Socket socket)
        {
            if (State != BuildingPieceState.InPlacementAllowed)
                return false;

            SetState(Constructable.IsConstructed ? BuildingPieceState.Constructed : BuildingPieceState.Placed);
            return true;
        }

        /// <summary>
        /// Ignores every vendor-supplied argument, same as the wall and the panel: this piece is
        /// found via <see cref="GridDoorFrameRegistry"/>, not the vendor's free-placement raycast.
        /// </summary>
        public override void UpdatePlacement(Vector3 position, Quaternion rotation, Socket socket, bool hasSurface)
        {
            if (!TryResolveHinge(out var hingePosition, out var hingeRotation, out var opening))
            {
                transform.position = new Vector3(0f, ParkedY, 0f);
                SetState(BuildingPieceState.InPlacementDenied);
                return;
            }

            transform.SetPositionAndRotation(hingePosition, hingeRotation);

            // Reservation first and cheapest: covers the window where a slot is logically taken but
            // physically still empty because the placement is already on its way to the host.
            bool allowed = !GridWallReservations.IsReserved(hingePosition, hingeRotation.eulerAngles.y)
                           && !IsFootprintObstructed(opening)
                           && !this.IsCollidingWithCharacters();
            SetState(allowed ? BuildingPieceState.InPlacementAllowed : BuildingPieceState.InPlacementDenied);
        }

        private bool TryResolveHinge(out Vector3 position, out Quaternion rotation, out GridDoorFrameOpening opening)
        {
            position = default;
            rotation = Quaternion.identity;
            opening = null;

            var camera = UnityUtility.CachedMainCamera;
            if (camera == null)
                return false;

            opening = GridDoorFrameRegistry.FindNearestFacing(camera.transform.position, camera.transform.forward,
                _buildRange, _minFacingDot);
            if (opening == null)
                return false;

            var frameTransform = opening.Frame.transform;
            position = frameTransform.TransformPoint(opening.HingeLocalPosition);
            rotation = frameTransform.rotation;
            return true;
        }

        /// <summary>
        /// True when this frame's opening is already filled — another leaf, or (defensively) anything
        /// else sitting in the footprint. The frame's own jamb/header colliders sit flush against this
        /// footprint's edges and must be excluded, exactly like the drywall panel excludes the wall it
        /// clads onto.
        /// </summary>
        private bool IsFootprintObstructed(GridDoorFrameOpening opening)
        {
            var size = opening.LeafSize;

            // Band starts at FloorClearance, not 0 — see the constant's own comment. Clamped so a
            // very short leaf can never invert into a negative-height band.
            float bandBottom = Mathf.Min(FloorClearance, size.y * 0.5f);
            float bandHeight = size.y - bandBottom;

            var centre = transform.TransformPoint(new Vector3(size.x * 0.5f, bandBottom + bandHeight * 0.5f, 0f));
            var extents = new Vector3(
                Mathf.Max(0.01f, size.x * 0.5f - ProbeMargin),
                Mathf.Max(0.01f, bandHeight * 0.5f - ProbeMargin),
                size.z * 0.5f + ProbeMargin);

            int count = PhysicsUtility.OverlapBoxOptimized(centre, extents, transform.rotation, out var colliders,
                PlacementMask);
            for (int i = 0; i < count; i++)
            {
                var collider = colliders[i];
                if (collider == null || HasCollider(collider) || opening.Frame.HasCollider(collider))
                    continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Vendor free-placement mask widened with every per-layer geometry layer. Resolved once,
        /// same reasoning as the wall and the panel.
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
        // Same contract as every other piece here: the piece is standalone, so its whole persistent
        // state is the visual state enum. The network replicator does not use this path; it is here
        // so STP's own save system round-trips a leaf like any other free piece. The vendor Door
        // component on this same root has its OWN ISaveableComponent for open/closed — unrelated and
        // untouched by this one.
        void ISaveableComponent.LoadMembers(object data) => State = (BuildingPieceState)data;
        object ISaveableComponent.SaveMembers() => State;
        #endregion
    }
}
