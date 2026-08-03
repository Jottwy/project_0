using BackroomsSurvival.Gameplay.GridWorld;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>Result of snapping an aim point to one cell of a grid wall's infill grid.</summary>
    public readonly struct GridPanelPose
    {
        /// <summary>World pivot of the panel: the cell centre, pushed off the frame face by its own half-thickness.</summary>
        public readonly Vector3 Position;

        /// <summary>The cell centre ON the frame's face plane. Anchor for the obstruction probe.</summary>
        public readonly Vector3 SlotCentre;

        /// <summary>
        /// Frame yaw on the front face, frame yaw + 180 on the back one. The 180 is not cosmetic —
        /// see the class remarks on why the face has to live in the yaw.
        /// </summary>
        public readonly float Yaw;

        /// <summary>Lowest-numbered cell the piece occupies along the frame's local +X, 0-based.</summary>
        public readonly int Column;

        /// <summary>Lowest-numbered cell the piece occupies along local +Y (0 = floor row), 0-based.</summary>
        public readonly int Row;

        /// <summary>True when the cell is on the frame's local +Z side.</summary>
        public readonly bool FrontFace;

        /// <summary>World size of the whole occupied rect — one cell for a 1 × 1 piece, four for 2 × 2.</summary>
        public readonly Vector2 Size;

        public GridPanelPose(Vector3 position, Vector3 slotCentre, float yaw, int column, int row,
            bool frontFace, Vector2 size)
        {
            Position = position;
            SlotCentre = slotCentre;
            Yaw = yaw;
            Column = column;
            Row = row;
            FrontFace = frontFace;
            Size = size;
        }
    }

    /// <summary>
    /// Snaps an aim point on a placed <see cref="GridWallBuildingPiece"/> onto one cell of that
    /// frame's infill grid, so a drywall sheet lands flush inside the bars instead of wherever the
    /// ray happened to land. No <c>MonoBehaviour</c>, no scene, no physics — pure function,
    /// headless-testable, same discipline as <see cref="GridWallSnap"/>.
    ///
    /// The grid is derived from the frame's COLLIDER (5 × 4 × 0.2, centred at half height), not from
    /// its mesh. The Steel Frame Grid mesh measures 4.749 × 3.712 and using it would leave every
    /// panel a quarter of a metre short on each side, so player infill would stop lining up with the
    /// procedural walls that share the same tile edge.
    ///
    /// WHY THE FACE IS ENCODED IN THE YAW. A cell takes a panel on each side independently, and the
    /// two sit ±(0.1 + half-thickness) from the frame plane — about 0.17 m apart in world space.
    /// <see cref="GridWallReservations"/> quantises position at 0.25 m (matching the host's
    /// <c>stp_pose_cell</c>), so both faces of one cell collapse into the SAME key and the second
    /// panel would be denied for the whole 2 s TTL of the first. Yaw is quantised at 1°, so front
    /// and back land on distinct keys. It is also the semantically right answer: a clad panel faces
    /// away from the frame, which is exactly what yaw + 180 expresses.
    /// </summary>
    public static class GridPanelSnap
    {
        /// <summary>Frame span along its own local X (5 m) — the wall's collider length.</summary>
        public const float SlotLength = GridWallSnap.TileSize;

        /// <summary>Frame span along its own local Y (4 m) — the wall's collider height.</summary>
        public const float SlotHeight = GridWallSnap.LayerHeight;

        /// <summary>Half the frame's 0.2 m thickness: the distance from its pivot plane to a face.</summary>
        public const float SlotHalfThickness = GridVisualConstants.WallThickness * 0.5f;

        /// <summary>
        /// Below this |local normal z| the ray hit the frame's top or its 0.2 m side edge, not one of
        /// the two faces that carry cells. 0.5 rather than something tighter because a BoxCollider
        /// returns an exact axis normal — anything ambiguous is genuinely not a face hit.
        /// </summary>
        private const float FaceNormalThreshold = 0.5f;

        /// <summary>
        /// Slack on the frame's outer rim. A ray aimed at the very edge of the collider comes back
        /// exactly on the boundary, and rejecting it would make the outermost centimetre of the frame
        /// silently unbuildable. Inside the tolerance the index is clamped, which is still
        /// deterministic.
        /// </summary>
        private const float EdgeTolerance = 0.01f;

        /// <summary>Size of one cell for the given subdivision. 5 × 4 gives the 1 × 1 m square cell.</summary>
        public static Vector2 CellSize(int columns, int rows) =>
            new(SlotLength / Mathf.Max(1, columns), SlotHeight / Mathf.Max(1, rows));

        /// <summary>
        /// Resolves where a piece of <paramref name="footprintColumns"/> × <paramref name="footprintRows"/>
        /// cells lands when aimed at <paramref name="hitPoint"/> on the frame whose pivot is
        /// <paramref name="framePivot"/> and whose yaw is <paramref name="frameYaw"/>.
        ///
        /// WHY THE FOOTPRINT IS SEPARATE FROM THE GRID. The frame is 5 × 4 m and a drywall sheet wants
        /// roughly 2 m, but 5 is not a multiple of 2 — so ANY subdivision sized to the sheet leaves a
        /// remainder (5/2 = 2.5, 5/3 = 1.67). Keeping the grid at 1 m, which divides both 5 and 4
        /// exactly, and letting a piece span several cells moves that remainder out of the geometry and
        /// into the design: a 2 × 2 sheet tiles the frame four times and leaves a 1 × 4 m strip, which
        /// is a slot for a different piece rather than a gap nothing can fill.
        ///
        /// <paramref name="faceOffset"/> is the piece's own half-thickness: the caller knows it, this
        /// function must not assume every infill piece is as thick as a drywall sheet.
        ///
        /// Returns false — leaving <paramref name="pose"/> at default — when the hit is not on one of
        /// the two cell-bearing faces, or lands outside the frame altogether.
        /// </summary>
        public static bool TrySnap(Vector3 framePivot, float frameYaw, int columns, int rows,
            int footprintColumns, int footprintRows,
            Vector3 hitPoint, Vector3 hitNormal, float faceOffset, out GridPanelPose pose)
        {
            pose = default;

            if (columns < 1 || rows < 1)
                return false;

            // A footprint larger than the frame is clamped rather than refused: a piece that covers the
            // whole face is a coherent thing to author, and refusing would be an invisible dead end.
            int spanColumns = Mathf.Clamp(footprintColumns, 1, columns);
            int spanRows = Mathf.Clamp(footprintRows, 1, rows);

            // Into the frame's own space. Yaw-only on purpose: GridWallSnap only ever produces 0 or
            // 90, and a full inverse transform would silently absorb any tilt a corrupted save gave
            // the frame instead of letting the panel land visibly wrong.
            var inverse = Quaternion.Euler(0f, -frameYaw, 0f);
            var local = inverse * (hitPoint - framePivot);
            var localNormal = inverse * hitNormal;

            if (Mathf.Abs(localNormal.z) < FaceNormalThreshold)
                return false;

            bool frontFace = localNormal.z > 0f;

            float u = local.x + SlotLength * 0.5f;
            float v = local.y;
            if (u < -EdgeTolerance || u > SlotLength + EdgeTolerance ||
                v < -EdgeTolerance || v > SlotHeight + EdgeTolerance)
            {
                return false;
            }

            float cellWidth = SlotLength / columns;
            float cellHeight = SlotHeight / rows;
            int aimedColumn = Mathf.Clamp(Mathf.FloorToInt(u / cellWidth), 0, columns - 1);
            int aimedRow = Mathf.Clamp(Mathf.FloorToInt(v / cellHeight), 0, rows - 1);

            // Free cell-by-cell placement: the piece is centred on the aimed cell as closely as an
            // integer footprint allows (exactly, for odd spans; biased one cell up-right for even
            // ones), so a sheet can be offset by a single cell and its joints deliberately staggered
            // against the row below.
            //
            // Then clamped so the span always fits inside the frame. Clamping rather than refusing is
            // what makes the ghost stick to the edge instead of blinking out as the crosshair sweeps
            // past the last column — you can still see what you are about to build.
            int column = Mathf.Clamp(aimedColumn - (spanColumns - 1) / 2, 0, columns - spanColumns);
            int row = Mathf.Clamp(aimedRow - (spanRows - 1) / 2, 0, rows - spanRows);

            float sign = frontFace ? 1f : -1f;
            float localX = -SlotLength * 0.5f + (column + spanColumns * 0.5f) * cellWidth;
            float localY = (row + spanRows * 0.5f) * cellHeight;

            var rotation = Quaternion.Euler(0f, frameYaw, 0f);
            var slotCentre = framePivot + rotation * new Vector3(localX, localY, sign * SlotHalfThickness);
            var position = framePivot + rotation * new Vector3(localX, localY, sign * (SlotHalfThickness + faceOffset));

            pose = new GridPanelPose(
                position,
                slotCentre,
                Mathf.Repeat(frontFace ? frameYaw : frameYaw + 180f, 360f),
                column,
                row,
                frontFace,
                new Vector2(spanColumns * cellWidth, spanRows * cellHeight));
            return true;
        }
    }
}
