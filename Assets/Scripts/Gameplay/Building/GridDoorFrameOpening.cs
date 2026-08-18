using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// Marks a <see cref="GridWallBuildingPiece"/> as a door frame and describes the one fixed slot a
    /// <see cref="GridDoorLeafBuildingPiece"/> can hang in — unlike the wall's generic column/row grid
    /// a <see cref="GridPanelBuildingPiece"/> clads onto, a door frame has exactly one opening, so
    /// there is nothing to snap against: just this.
    /// </summary>
    [RequireComponent(typeof(GridWallBuildingPiece))]
    public sealed class GridDoorFrameOpening : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Hinge edge of the opening, in this frame's own local space (root at floor height, " +
                 "X = 0 centred on the frame). The leaf's root lands exactly here.")]
        private Vector3 _hingeLocalPosition = new(-0.45f, 0f, 0f);

        [SerializeField]
        [Tooltip("Width x height x thickness the leaf is authored to fill this opening.")]
        private Vector3 _leafSize = new(0.9f, 2.5f, 0.05f);

        private GridWallBuildingPiece _frame;

        /// <summary>The frame this opening belongs to. Cached; both live on the same GameObject.</summary>
        public GridWallBuildingPiece Frame => _frame != null ? _frame : (_frame = GetComponent<GridWallBuildingPiece>());

        public Vector3 HingeLocalPosition => _hingeLocalPosition;
        public Vector3 LeafSize => _leafSize;

        // Registered for the lifetime of the GameObject, not gated on the frame's own placement
        // state — GridDoorFrameRegistry.FindNearestFacing filters on Frame.IsPlaced itself, so a
        // ghost/preview frame is invisible to it without this component needing to know why.
        private void OnEnable() => GridDoorFrameRegistry.Register(this);
        private void OnDisable() => GridDoorFrameRegistry.Unregister(this);
    }
}
