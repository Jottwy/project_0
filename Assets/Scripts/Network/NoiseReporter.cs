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
        /// <summary>
        /// ADR-041 §2 / ADR-047 D6: one row per ranged weapon. The table lives HERE, on the Unity
        /// side, because loudness belongs with the weapon definitions — putting it in Rust would
        /// duplicate that data and drift the moment a weapon is added.
        /// </summary>
        [System.Serializable]
        private struct WeaponLoudness
        {
            [Tooltip("Wieldable GameObject name, matched case-insensitively as a prefix (Unity's " +
                     "\"(Clone)\" suffix is stripped before comparing).")]
            public string WieldableName;

            [Min(0f)] public float Loudness;
        }

        [Header("Loudness (metres of audible radius)")]
        /// <remarks>
        /// Defaults live in code, not only in the inspector, and that is deliberate:
        /// <c>GameBootstrap.EnsureComponent&lt;NoiseReporter&gt;()</c> adds this component at
        /// RUNTIME, so there is no authored instance for anyone to fill in. A serialized-only table
        /// would silently be empty in every real session. Authoring still wins if the component is
        /// ever placed in a scene by hand.
        /// </remarks>
        [Tooltip("Per-weapon audible radius. Anything not listed falls back to the default below.")]
        [SerializeField]
        private WeaponLoudness[] _weaponLoudness =
        {
            // .30-30 lever rifle: carries for kilometres in the open, and a long corridor acts as
            // a waveguide. The backend clamps it (PHANTOM_NOISE_MAX_LOUDNESS).
            new WeaponLoudness { WieldableName = "STP_Wieldable_Marlin336", Loudness = 500f },
            // A bow is the STEALTH option and used to shout exactly as loud as the rifle, which
            // made the quiet weapon pointless. A bowstring is a soft snap, not a report.
            new WeaponLoudness { WieldableName = "STP_Wieldable_WoodenBow", Loudness = 25f },
        };

        [Tooltip("Radius used when the equipped weapon is not in the table above. Firearm-loud on " +
                 "purpose: a new gun that nobody remembered to list should still be heard.")]
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
        private ICharacter _character;
        private IWieldablesControllerCC _wieldablesController;
        private float _nextScanAt;

        private void OnDisable() => Unsubscribe();

        private void OnDestroy() => Unsubscribe();

        private void Update()
        {
            if (Time.unscaledTime < _nextScanAt)
                return;
            _nextScanAt = Time.unscaledTime + Mathf.Max(0.1f, _rescanInterval);

            var character = ResolveCharacter();
            var root = character?.transform;
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
            // Assigned AFTER any Unsubscribe: that call clears the cached rig, so setting these
            // first would have them wiped on the very tick the new rig appeared.
            _character = character;

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

            float loudness = ResolveLoudness();
            if (loudness <= 0f)
                return;
            if (!IPCClient.TryGetInstance(out var ipc))
                return;
            // The muzzle is close enough to the player for a stimulus whose whole point is that it
            // is imprecise; using the character position avoids reaching into weapon internals.
            var at = _characterRoot != null ? _characterRoot.position : transform.position;
            ipc.SendReportNoise(at, loudness);
        }

        /// <summary>
        /// ADR-047 D6: the audible radius of the weapon that just fired.
        ///
        /// Read from the ACTIVE wieldable rather than from the trigger that raised the event: STP's
        /// <see cref="IFirearmTrigger.Shoot"/> is a parameterless <c>UnityAction</c>, so the sender
        /// is not available, and the equipped wieldable is the one that fired by definition. Same
        /// read-only path <c>PlayerPoseTransmitter.ReadLightOn</c> already uses (rule #3: we report,
        /// we never apply).
        /// </summary>
        private float ResolveLoudness()
        {
            if (_weaponLoudness == null || _weaponLoudness.Length == 0)
                return _firearmLoudness;

            if (_wieldablesController == null && _character != null)
                _wieldablesController = _character.GetCC<IWieldablesControllerCC>();
            if (_wieldablesController == null)
                return _firearmLoudness;

            // A DESTROYED wieldable still hands back a non-null interface reference (C# `?.` does
            // not see Unity's fake-null), and touching `.gameObject` on it throws — so test the
            // Unity lifetime first, exactly as PlayerPoseTransmitter does.
            var wieldable = _wieldablesController.ActiveWieldable;
            bool destroyed = wieldable is Object uo && uo == null;
            var go = (wieldable == null || destroyed) ? null : wieldable.gameObject;
            if (go == null)
                return _firearmLoudness;

            string name = go.name;
            for (int i = 0; i < _weaponLoudness.Length; i++)
            {
                var row = _weaponLoudness[i];
                if (!string.IsNullOrEmpty(row.WieldableName) &&
                    name.StartsWith(row.WieldableName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return row.Loudness;
                }
            }

            return _firearmLoudness;
        }

        /// <summary>
        /// The LOCAL character. `GetComponentInParent` finds nothing in the normal wiring — this
        /// component is added at runtime onto GameBootstrap's own object, which is not under the
        /// player — so the GameMode fallback is the path that actually resolves. It is kept first
        /// anyway for the case where someone drops this component on the rig by hand.
        /// </summary>
        private ICharacter ResolveCharacter()
        {
            var character = GetComponentInParent<ICharacter>();
            if (character == null && GameMode.HasInstance)
                character = GameMode.Instance.LocalPlayer;
            return character;
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
            // Dropped together with the rig they were resolved from: a rebuild (death, respawn)
            // hands out new component instances, and a cached one would be stale or destroyed.
            _character = null;
            _wieldablesController = null;
        }
    }
}
