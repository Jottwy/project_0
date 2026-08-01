using System.Collections.Generic;
using PolymindGames;
using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-041: reports a NOISE to this client's own backend when the local player fires, so a
    /// gunshot becomes something the robapieles can hear and travel to.
    ///
    /// Until now a shot did not exist for the AI at all: ADR-029 only reports hit CANDIDATES, so
    /// missing produced no event and firing had no cost. What travels is "a noise happened HERE,
    /// audible for N metres" — never the player's position. The backend blurs it by distance and
    /// investigates the area, which is what separates "it heard you" from "it knows where you are".
    ///
    /// EXTERNAL HOOK, never an edit inside STP: it subscribes to the vendor's own
    /// <see cref="IFirearmTrigger.Shoot"/> event. Vendor prefabs and code are untouched, so a
    /// package update cannot silently drop this. Triggers are re-scanned on a slow cadence rather
    /// than tracked through equip events — a handful of components on one rig, and it survives rig
    /// rebuilds and weapon swaps without knowing anything about how STP equips things.
    ///
    /// The loudness TABLE lives here on purpose (ADR-041): keeping it in Rust would duplicate data
    /// that belongs to Unity's weapon definitions and drift the moment a weapon is added.
    /// Removable: delete the file and shots simply stop attracting the phantom.
    /// </summary>
    public sealed class NoiseReporter : MonoBehaviour
    {
        [Header("Loudness (metres of audible radius)")]
        [Tooltip("Default radius for a firearm shot. A rifle carries for kilometres in the open, " +
                 "and a long corridor acts as a waveguide, so 500 m is conservative rather than " +
                 "generous. The backend clamps it.")]
        [SerializeField, Min(0f)] private float _firearmLoudness = 500f;

        [Tooltip("How often to re-scan the local rig for firearm triggers (seconds).")]
        [SerializeField, Min(0.1f)] private float _rescanInterval = 0.5f;

        /// <summary>
        /// ADR-042: monotonic shot counter (wrapping), read by <see cref="PlayerPoseTransmitter"/>
        /// and relayed as <c>fire_seq</c> so observers can play the gunshot on this peer's proxy.
        ///
        /// It rides THIS component's subscription on purpose. A second scanner over the same
        /// <see cref="IFirearmTrigger.Shoot"/> events would not just be duplication — it would be a
        /// second 0.5 s rescan clock that can disagree with this one about which weapon is equipped.
        /// One local event, two independent outputs: <c>report_noise</c> to our own backend (an AI
        /// stimulus, ADR-041) and this counter to the pose relay (cosmetic, ADR-042). Static because
        /// the transmitter lives on its own DontDestroyOnLoad object and must not depend on finding
        /// this instance; the proxy hook carries a sentinel, so the value it starts from is irrelevant.
        /// </summary>
        public static byte ShotCounter { get; private set; }

        private readonly List<IFirearmTrigger> _subscribed = new List<IFirearmTrigger>();
        private Transform _characterRoot;
        private float _nextScanAt;

        private void OnDisable() => Unsubscribe();

        private void OnDestroy() => Unsubscribe();

        private void Update()
        {
            if (Time.unscaledTime < _nextScanAt)
                return;
            _nextScanAt = Time.unscaledTime + Mathf.Max(0.1f, _rescanInterval);

            var root = ResolveCharacterRoot();
            if (root == null)
            {
                Unsubscribe(); // rig gone (death, rebuild): drop stale handlers
                return;
            }
            if (root != _characterRoot)
            {
                Unsubscribe();
                _characterRoot = root;
            }

            // Subscribe to any trigger we are not already on. Only the equipped weapon raises
            // Shoot, so covering all of them costs nothing and needs no equip tracking.
            var triggers = root.GetComponentsInChildren<IFirearmTrigger>(true);
            foreach (var t in triggers)
            {
                if (t == null || _subscribed.Contains(t))
                    continue;
                t.Shoot += OnShoot;
                _subscribed.Add(t);
            }
        }

        private void OnShoot()
        {
            // ADR-042: bumped BEFORE the loudness gate — a silenced weapon (loudness 0) still fires
            // and observers must still hear it; only the AI stimulus is meant to be suppressible.
            // Wraps at 255 by design (unchecked); the observer reads deltas, never absolutes.
            unchecked { ShotCounter++; }

            if (_firearmLoudness <= 0f)
                return;
            if (!IPCClient.TryGetInstance(out var ipc))
                return;
            // The muzzle is close enough to the player for a stimulus whose whole point is that it
            // is imprecise; using the character position avoids reaching into weapon internals.
            var at = _characterRoot != null ? _characterRoot.position : transform.position;
            ipc.SendReportNoise(at, _firearmLoudness);
        }

        private Transform ResolveCharacterRoot()
        {
            var character = GetComponentInParent<ICharacter>();
            if (character == null && GameMode.HasInstance)
                character = GameMode.Instance.LocalPlayer;
            return character?.transform;
        }

        private void Unsubscribe()
        {
            foreach (var t in _subscribed)
            {
                if (t != null)
                    t.Shoot -= OnShoot;
            }
            _subscribed.Clear();
            _characterRoot = null;
        }
    }
}
