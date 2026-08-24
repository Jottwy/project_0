using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-093 E3 — a walk-through trigger that sends <see cref="IPCClient.SendLevel4Door"/>
    /// when the LOCAL player crosses it. Deliberately proximity-polled (same idiom as
    /// <c>AuthoritativePoseApplier.ResolveMotor</c>/<see cref="LocalPlayerLocator"/>) instead of
    /// a physics <c>OnTriggerEnter</c>: remote avatars are cosmetic proxies under
    /// <see cref="RemotePlayerManager"/> and may or may not carry a real collider, so a physics
    /// trigger risks firing for the wrong player. Polling the LOCAL motor's own position side-
    /// steps that entirely.
    ///
    /// Instantiated (not scene-authored) by <see cref="GameBootstrap"/> — no prefab, no .unity
    /// edit. Two instances exist today: one near the Level 0 spawn (Entry) and one at a FIXED
    /// anchor inside the Level 4 reserve's world-space center (Return) — the return anchor is a
    /// placeholder until authored room content (E6 of docs/LEVEL4-ROADMAP.md) gives the region
    /// something to hang a real door off.
    /// </summary>
    public sealed class Level4DoorTrigger : MonoBehaviour
    {
        private Level4Door _door;
        private float _radius;
        private long _nextRequestId = 1;

        private CharacterControllerMotor _motor;
        private bool _playerInside;

        public void Configure(Level4Door door, float radius)
        {
            _door = door;
            _radius = radius;
        }

        private void Update()
        {
            ResolveMotor();
            if (_motor == null)
                return;

            // Horizontal-only: a door is crossed by walking into its footprint, not by matching
            // its exact Y — the trigger anchor's height is a placeholder (see class doc) anyway.
            Vector3 delta = _motor.transform.position - transform.position;
            float sqrDist = delta.x * delta.x + delta.z * delta.z;
            bool inside = sqrDist <= _radius * _radius;

            if (inside && !_playerInside)
            {
                IPCClient.Instance?.SendLevel4Door(_door, _nextRequestId++);
            }
            _playerInside = inside;
        }

        /// <summary>Mirror of AuthoritativePoseApplier.ResolveMotor — Unity's overloaded ==
        /// reports a destroyed motor as null, so a rig rebuild forces a re-find.</summary>
        private void ResolveMotor()
        {
            if (_motor != null)
                return;
            _motor = LocalPlayerLocator.Find<CharacterControllerMotor>();
        }
    }
}
