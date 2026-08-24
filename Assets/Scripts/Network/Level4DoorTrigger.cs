using BackroomsSurvival.Gameplay;
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
    ///
    /// Carries a bright emissive cube (<see cref="MaterialHelper.MakeEmissive"/>) so it can be
    /// found on foot during playtest — the trigger itself is invisible geometry, and without a
    /// marker Joel walked right past it (2026-08-24). TEMPORARY: replace with a real door prefab
    /// once E6 gives the region authored content to hang one on.
    /// </summary>
    public sealed class Level4DoorTrigger : MonoBehaviour
    {
        private Level4Door _door;
        private float _radius;
        private long _nextRequestId = 1;

        private CharacterControllerMotor _motor;
        private bool _playerInside;
        private Transform _marker;

        public void Configure(Level4Door door, float radius)
        {
            _door = door;
            _radius = radius;
            SpawnMarker(door, radius);
        }

        private void SpawnMarker(Level4Door door, float radius)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"Level4DoorMarker_{door}";
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            // Visible from across a room, small enough not to block the walk-through.
            go.transform.localScale = Vector3.one * Mathf.Max(1.5f, radius);

            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            // Entry = cyan (Level 0 -> Level 4), Return = magenta (Level 4 -> Level 0) — same
            // "which way" mnemonic a real door's frame color would carry later.
            Color color = door == Level4Door.Entry
                ? new Color(0.2f, 0.9f, 0.95f)
                : new Color(0.95f, 0.2f, 0.85f);
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = MaterialHelper.MakeEmissive(color, 2.5f);

            _marker = go.transform;
        }

        private void Update()
        {
            // Motion draws the eye in an otherwise static, unlit room — cheap and reversible;
            // remove along with the cube once a real door prefab exists (E6).
            if (_marker != null)
                _marker.Rotate(Vector3.up, 45f * Time.deltaTime, Space.World);

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
