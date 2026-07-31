using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Building
{
    /// <summary>
    /// Short-lived record of grid-wall slots this client has ALREADY asked the host for but whose
    /// replicated piece has not come back yet.
    ///
    /// Why it has to exist: placing is host-authoritative — the local preview is destroyed on the
    /// spot and the real wall only appears once the host echoes it back (see
    /// <c>StpBuildingPlacementWatcher</c>). For the round-trip's duration the slot is physically
    /// empty, so <c>GridWallBuildingPiece</c>'s overlap probe reports it free and the player can
    /// place a second wall into the very same slot. That was harmless while build mode closed after
    /// every placement; the moment the preview re-arms so walls can be chained, a fast double-tap on
    /// a joiner (where the round-trip is a real RTT, not a couple of frames) stacks two walls.
    ///
    /// Scope, stated plainly: this closes the SELF double-place. It does nothing about two different
    /// players targeting the same slot at the same instant — no client can see another client's
    /// in-flight request. That race needs host-side rejection and is not addressed here.
    /// </summary>
    public static class GridWallReservations
    {
        /// <summary>
        /// How long a slot stays reserved. Generous against any plausible RTT on purpose: once the
        /// replicated wall does arrive, the piece's own overlap probe denies the slot anyway, so an
        /// over-long reservation costs nothing while a short one reopens the duplicate window.
        /// </summary>
        private const float TtlSeconds = 2f;

        // Same quantisation as the host's stp_pose_cell (Q_POS = 0.25 m, Q_YAW = 1°) so both sides
        // name a slot identically. Grid poses land on exact multiples of 2.5 m and on yaw 0/90, so
        // the rounding is never ambiguous here — the shared constants are about keeping the two
        // definitions of "same slot" from drifting apart, not about tolerance.
        private const float PositionQuantum = 0.25f;
        private const float YawQuantum = 1f;

        // Swept in Reserve() rather than on a timer: entries are only ever added by a deliberate
        // human placement, so the dictionary stays in the single digits in practice.
        private const int SweepThreshold = 32;

        private static readonly Dictionary<(int, int, int, int), float> _expiryByCell = new();
        private static readonly List<(int, int, int, int)> _sweepScratch = new();

        /// <summary>Marks the slot as taken until the host's copy arrives (or the TTL lapses).</summary>
        public static void Reserve(Vector3 position, float yaw)
        {
            if (_expiryByCell.Count >= SweepThreshold)
                Sweep();

            _expiryByCell[Cell(position, yaw)] = Time.time + TtlSeconds;
        }

        /// <summary>True while a placement this client already sent is still in flight for this slot.</summary>
        public static bool IsReserved(Vector3 position, float yaw)
        {
            var cell = Cell(position, yaw);
            if (!_expiryByCell.TryGetValue(cell, out float expiry))
                return false;

            if (Time.time < expiry)
                return true;

            _expiryByCell.Remove(cell);
            return false;
        }

        /// <summary>Drops every lapsed entry. Only called from <see cref="Reserve"/>.</summary>
        private static void Sweep()
        {
            _sweepScratch.Clear();
            float now = Time.time;
            foreach (var pair in _expiryByCell)
            {
                if (now >= pair.Value)
                    _sweepScratch.Add(pair.Key);
            }

            foreach (var cell in _sweepScratch)
                _expiryByCell.Remove(cell);
        }

        private static (int, int, int, int) Cell(Vector3 position, float yaw) => (
            Mathf.RoundToInt(position.x / PositionQuantum),
            Mathf.RoundToInt(position.y / PositionQuantum),
            Mathf.RoundToInt(position.z / PositionQuantum),
            QuantizedYaw(yaw));

        /// <summary>
        /// Yaw folded into [0, 360) BEFORE quantising, then wrapped again after.
        ///
        /// Not paranoia: the reserving side passes <c>transform.rotation.eulerAngles.y</c> while the
        /// querying side passes the snapper's literal 0 or 90. A quaternion round-trip can hand back
        /// 359.99998 where 0 went in, and the naive quantisation turns that into cell 360 — a key
        /// that matches nothing, so the reservation silently does nothing at all. Wrapping makes the
        /// two sides agree by construction instead of by luck.
        /// </summary>
        private static int QuantizedYaw(float yaw)
        {
            const int StepsPerTurn = (int)(360f / YawQuantum);
            return Mathf.RoundToInt(Mathf.Repeat(yaw, 360f) / YawQuantum) % StepsPerTurn;
        }
    }
}
