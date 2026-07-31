using System;
using BackroomsSurvival.Gameplay.GridWorld;
using PolymindGames;
using PolymindGames.BuildingSystem;
using PolymindGames.SaveSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// A 5 × 4 × 0.2 wall the player builds straight onto the PROCEDURAL floor — no STP foundation,
    /// no sockets, no platform first. It snaps to the same tile-edge slots the generated walls use
    /// (<see cref="GridWallSnap"/>), so a player-built panel is flush with the world instead of
    /// floating at an arbitrary angle.
    ///
    /// Why a third <see cref="BuildingPiece"/> subclass instead of reusing a vendor one:
    /// <see cref="GroupBuildingPiece"/> refuses to place without a socket (<c>_requiresSockets</c>)
    /// and the procedural floor has none — it is not a BuildingPiece at all. <see cref="FreeBuildingPiece"/>
    /// would place anywhere the ray lands, unsnapped. Both are <c>sealed</c>, so neither can be
    /// extended; <see cref="BuildingPiece"/> is abstract and is the intended extension point. Zero
    /// vendor source is edited — this type lives entirely in the BackroomsSurvival assembly.
    ///
    /// Networking: replication is inherited for free. <c>StpBuildingPlacementWatcher</c> reports the
    /// placement by <c>Definition.Id</c> + world pose, and <c>StpBuildingReplicator</c> respawns it
    /// from the same definition on every client — neither of them knows or cares which BuildingPiece
    /// subclass it is. The snapped pose is derived deterministically from the aim point, so every
    /// instance lands on the identical world position.
    /// </summary>
    [RequireComponent(typeof(MaterialEffect))]
    public sealed class GridWallBuildingPiece : BuildingPiece, ISaveableComponent
    {
        [SerializeField, Range(1f, 20f)]
        [Tooltip("How far the player can reach to place a wall. Mirrors the vendor controller's own " +
                 "BuildRange (7 on STP_Player) — that value is private to its BuildSettings struct, " +
                 "so it cannot be read and has to be kept in step by hand.")]
        private float _buildRange = 7f;

        // ── Obstruction probe ────────────────────────────────────────────────────
        // A box on the wall's own slot: if anything solid is already there, the slot is taken.
        //
        // The vertical band is the load-bearing choice. It covers only the LOWER half of the wall
        // (0.2 m to 2.0 m above the floor) and that is deliberate, not a safety margin:
        //  · below 0.2 m sits the floor slab (0.08 m thick, centred on the floor plane) — every
        //    single placement would self-deny if the band reached it;
        //  · above 2.0 m live the ceiling decorations, and they must NOT block a wall: the Pieza B
        //    pipes hang at 3.75 m and the hanging ceiling panels lower still, so a band reaching
        //    the ceiling would refuse to wall off any corridor that happens to have plumbing;
        //  · a lintel (the half-wall variant, clear below 2.2 m) is likewise not an obstruction —
        //    the passage under it is exactly what a player would want to seal.
        // Anything that genuinely blocks the slot — a full-height wall, a knee wall, another
        // player's panel — occupies this band by construction.
        private const float BandBottom = 0.2f;
        private const float BandTop = 2.0f;

        // Half-length 2.35 instead of the true 2.5: a wall on a PERPENDICULAR edge of the same tile
        // reaches 0.1 m past the corner (it is 0.2 m thick, centred on its own edge line), so a
        // full-length probe would report every corner neighbour as an obstruction and make it
        // impossible to close a right angle. 2.35 clears that overhang by 0.05 m while still
        // overlapping any panel sharing THIS edge along its whole span.
        private const float HalfLength = 2.35f;

        // Half-thickness 0.08 against the wall's true 0.1: keeps the probe strictly inside the slot
        // so a panel on the NEXT tile edge (5 m away) can never be caught, while a duplicate on this
        // very edge — centred identically — overlaps completely.
        private const float HalfThickness = 0.08f;

        // A hit surface counts as "floor" above this normal.y. Only floors give an exact layer by
        // rounding; see GridWallSnap.LayerAt.
        private const float FloorNormalThreshold = 0.7f;

        private int _placementMask;
        private bool _placementMaskResolved;

        /// <summary>Always standalone: this wall belongs to the world grid, not to an STP structure.</summary>
        public override IBuildingPieceGroup ParentGroup => null;

        /// <summary>
        /// No sockets. Other pieces never snap to a grid wall — the grid itself is the anchor, so a
        /// second wall placed beside this one is aligned by <see cref="GridWallSnap"/>, not by us.
        /// </summary>
        public override ReadOnlySpan<Socket> GetSockets() => null;

        /// <summary>Bottom-centre, matching <see cref="FreeBuildingPiece"/> (the pivot is on the floor).</summary>
        public override Vector3 GetCenter()
        {
            var bounds = GetWorldBounds();
            var centre = bounds.center;
            return new Vector3(centre.x, centre.y - bounds.extents.y, centre.z);
        }

        /// <summary>
        /// Places the wall wherever the preview currently is. The <paramref name="socket"/> the
        /// controller found is ignored: this piece never snaps to sockets, and its pose was already
        /// resolved against the grid by <see cref="UpdatePlacement"/>. Refusing unless the preview
        /// is in the ALLOWED state is what makes that resolution authoritative — the same guard
        /// <see cref="FreeBuildingPiece"/> uses.
        /// </summary>
        public override bool TryPlace(Socket socket)
        {
            if (State != BuildingPieceState.InPlacementAllowed)
                return false;

            SetState(Constructable.IsConstructed ? BuildingPieceState.Constructed : BuildingPieceState.Placed);
            return true;
        }

        /// <summary>
        /// Overrides the vendor's free-placement pose entirely. <paramref name="position"/> /
        /// <paramref name="rotation"/> / <paramref name="hasSurface"/> come from
        /// <c>CharacterBuildController.GetFreePlacement</c>, which returns the raw ray hit against
        /// <c>FreePlacementMask</c> — a mask that omits the DynamicObject layer, where macro layer 2
        /// renders, so honouring it would make that whole layer unbuildable. We re-cast against
        /// <c>FreePlacementMask | GridChunkBuilder.GeoMask</c> instead and snap the result.
        ///
        /// On a failed resolve the transform is left alone and only the state goes to DENIED: the
        /// ghost freezes at its last valid slot, which reads as "not here" far better than a panel
        /// teleporting to some fallback point every frame the player looks at open air.
        /// </summary>
        public override void UpdatePlacement(Vector3 position, Quaternion rotation, Socket socket, bool hasSurface)
        {
            if (!TryResolveGridPose(out var pose))
            {
                SetState(BuildingPieceState.InPlacementDenied);
                return;
            }

            // Pose first: IsCollidingWithCharacters reads GetWorldBounds(), which is derived from the
            // live transform, so it must already be at the slot under test.
            transform.SetPositionAndRotation(pose.Position, Quaternion.Euler(0f, pose.Yaw, 0f));

            // The reservation check comes first and is the cheapest: it covers the window where a
            // slot is logically taken but physically still empty, because the placement is already
            // on its way to the host and the replicated wall has not landed yet.
            bool allowed = !GridWallReservations.IsReserved(pose.Position, pose.Yaw)
                           && !IsSlotObstructed(pose)
                           && !this.IsCollidingWithCharacters();
            SetState(allowed ? BuildingPieceState.InPlacementAllowed : BuildingPieceState.InPlacementDenied);
        }

        /// <summary>Casts the aim ray and snaps the hit to a grid slot. False = nothing in reach.</summary>
        private bool TryResolveGridPose(out GridWallPose pose)
        {
            pose = default;

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

            pose = GridWallSnap.Snap(hit.point, hit.normal.y > FloorNormalThreshold);
            return true;
        }

        /// <summary>
        /// True when something solid already occupies this slot: a generated wall, a knee wall, or a
        /// panel another player built. See the band constants above for why the probe covers only
        /// the lower half of the wall.
        ///
        /// This exists because the vendor overlap check cannot do the job here: <c>OverlapCheckMask</c>
        /// omits the Default layer, and macro layer 0 — where the player actually lives — renders
        /// onto exactly that layer. Left to the vendor, every wall on layer 0 would be placeable
        /// straight through the existing geometry.
        /// </summary>
        private bool IsSlotObstructed(in GridWallPose pose)
        {
            float floorY = pose.Layer * GridWallSnap.LayerHeight;
            var centre = new Vector3(pose.Position.x, floorY + (BandBottom + BandTop) * 0.5f, pose.Position.z);
            var extents = new Vector3(HalfLength, (BandTop - BandBottom) * 0.5f, HalfThickness);
            var orientation = Quaternion.Euler(0f, pose.Yaw, 0f);

            int count = PhysicsUtility.OverlapBoxOptimized(centre, extents, orientation, out var colliders, PlacementMask);
            for (int i = 0; i < count; i++)
            {
                var collider = colliders[i];
                if (collider == null || HasCollider(collider))
                    continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Vendor free-placement mask widened with every per-layer geometry layer. Resolved once:
        /// <c>BuildingManager.Instance</c> is a singleton asset and <c>GeoMask</c> is a static
        /// bitmask, so neither can change during a session.
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
        // Same contract as FreeBuildingPiece: the piece is standalone, so its whole persistent state
        // is the visual state enum. The network replicator does not use this path (it forces the
        // state on the spawned instance directly); it is here so STP's own save system round-trips
        // a grid wall like any other free piece.
        void ISaveableComponent.LoadMembers(object data) => State = (BuildingPieceState)data;
        object ISaveableComponent.SaveMembers() => State;
        #endregion
    }
}
