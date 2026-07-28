using BackroomsSurvival.Gameplay.GridWorld;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Single, motor-independent transmitter of the LOCAL player's pose over IPC (~30 Hz).
    /// Replaces the deleted PlayerPoseSender / LocalPoseSendGate / MovementReconciler stack.
    ///
    /// Three hard lessons baked into the design (do not regress):
    ///   1. Reads <c>_motor.transform.position</c>, NEVER the Player root. Only the
    ///      CharacterControllerMotor moves its own transform when walking; the Player root
    ///      stays at spawn / origin (see CharacterControllerMotor.cs:236,282).
    ///   2. Re-resolves the motor LIVE every frame, never caches it only once. The STP rig
    ///      is rebuilt at runtime (triggered by CharacterClothing.Start's NRE), which destroys
    ///      a cached motor; we revalidate and re-find on cache-miss.
    ///   3. ONE sender. No "yield to another component" handshake (that deadlocked the old
    ///      system). This component lives on its own DontDestroyOnLoad object, independent of
    ///      the player GameObject, so it is immune to the rig rebuild that was the root cause.
    /// </summary>
    public sealed class PlayerPoseTransmitter : MonoBehaviour
    {
        private static PlayerPoseTransmitter _instance;

        // Read-only diagnostics mirror for NetworkDebugHud (no behavioural coupling).
        public static bool IsSending { get; private set; }
        public static Vector3 LastSent { get; private set; }

        private const float SendHz = 30f;
        private const float SendInterval = 1f / SendHz; // ~33 ms (single sender; backend read loop handles this easily)

        private CharacterControllerMotor _motor;
        private ICharacter _character;       // cached parent character (look handler + inventory source)
        private ILookHandlerCC _lookHandler; // ADR-021: source of the local camera pitch
        // ADR-022: cached inventory equipment containers (Head/Torso/Legs/Feet) + reusable read buffer.
        private IItemContainer _headEquip, _torsoEquip, _legsEquip, _feetEquip;
        private bool _equipmentResolved;
        private readonly int[] _equipment = new int[4];
        // ADR-023: cached wieldable inventory + holster container, source of the held item ID.
        private IWieldableInventoryCC _wieldableInv;
        private IItemContainer _holster;
        private bool _heldResolved;
        // ADR-024: cached local health manager + monotonic hit-reaction counter. Unlike every other
        // field (read per-pose), this is PUSHED by the IHealthManager.DamageReceived event — incremented
        // on each real local damage event (ReceiveDamage). We never poll Health: the StatInterpolator
        // (ADR-009) writes it via SetHealthSilent (no event), so polling the delta would false-flinch on
        // reconciliation. Subscribed when the character resolves, unsubscribed on rig rebuild / OnDestroy.
        private IHealthManager _health;
        private byte _hitSeq;
        private float _sendAccum;
        private uint _inputSeq;
        private uint _clientTick;
        private Vector3 _prevPos;
        private bool _hasPrev;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[PlayerPoseTransmitter]");
            _instance = go.AddComponent<PlayerPoseTransmitter>();
            DontDestroyOnLoad(go);
        }

        // Hard singleton: even if a second instance is created by any path (a duplicate scene
        // load, a baked component, a manual AddComponent), only one survives → never doubles the
        // IPC send rate.
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        private void Update()
        {
            // Lesson 2: re-resolve every frame (cheap cache-hit path, full re-find on miss).
            ResolveMotor();

            float frameDt = Time.unscaledDeltaTime;
            _sendAccum += frameDt;
            if (_sendAccum < SendInterval)
                return;

            // ADR-025 respawn hallazgo-1 fix (client leg): while an authoritative reposition is
            // pending (snap window armed, snap not yet applied), do NOT re-assert the stale local
            // pose — the server trusts client-authoritative positions, so one stale report lands
            // the respawn on the OLD pose instead of the honored safe spawn (observed: honored
            // (22.5,…) but the player ended at the STP scene spawn the transmitter re-reported).
            // Bounded by the snap window (0.35 s max, no retries); _sendAccum keeps accruing so
            // the first post-snap frame sends immediately, and _hasPrev restarts the finite-diff
            // velocity so that first send doesn't export a huge snap-crossing velocity (which the
            // server speed-cap would reject).
            if (AuthoritativePoseApplier.SnapPending)
            {
                _hasPrev = false;
                return;
            }

            float dt = _sendAccum; // elapsed since last send → drives the finite-diff velocity
            _sendAccum = 0f;

            // Lesson 1 + SKIP: no motor → do NOT send. Never report the Player root or (0,1.8,0).
            if (_motor == null)
            {
                IsSending = false;
                _hasPrev = false; // restart the finite difference after a gap / rig rebuild
                _lookHandler = null; // the look handler is a sibling of the motor; re-resolve next time
                // ADR-022: the rig rebuild invalidates the character/inventory too — drop the cached
                // containers so they re-resolve against the fresh character on the next valid frame.
                _character = null;
                _headEquip = _torsoEquip = _legsEquip = _feetEquip = null;
                _equipmentResolved = false;
                // ADR-023: drop the cached wieldable inventory/holster too — re-resolve next valid frame.
                _wieldableInv = null;
                _holster = null;
                _heldResolved = false;
                // ADR-024: the rig rebuild destroys the health manager — unsubscribe and drop it so we
                // re-subscribe against the fresh character next valid frame (no leaked stale handler).
                UnsubscribeHealth();
                return;
            }

            Vector3 pos = _motor.transform.position;

            // Minimal inline gate: discard the literal origin only (defensive — with the
            // motor present pos is already valid). No separate LocalPoseSendGate.
            if (pos == Vector3.zero)
            {
                IsSending = false;
                _hasPrev = false;
                return;
            }

            float yaw = _motor.transform.eulerAngles.y;

            Vector3 vel = Vector3.zero;
            if (_hasPrev && dt > 0f)
                vel = (pos - _prevPos) / dt;
            _prevPos = pos;
            _hasPrev = true;

            // move_state only drives server-side run-stamina drain (the speed cap is always the
            // sprint cap), so a simple idle/walk classification is sufficient and harmless.
            Vector3 horiz = new Vector3(vel.x, 0f, vel.z);
            byte moveState = horiz.magnitude > 0.1f ? (byte)1 : (byte)0;

            // ADR-020: report the LOCAL crouch state, derived from the motor height. STP's native
            // CharacterCrouchState lowers Motor.Height when crouching; we only READ it (never apply,
            // never reimplement — rule #3). Relayed cosmetically to peers; not authoritative.
            var motorCC = (IMotorCC)_motor;
            bool crouch = motorCC.Height < motorCC.DefaultHeight - 0.05f;

            // ADR-021: report the LOCAL camera pitch (degrees). ViewAngles.x is the vertical
            // look angle, already clamped by CharacterLookHandler. The look handler is a sibling
            // component on the same character as the motor; resolve it lazily (it may be null for
            // a frame after a rig rebuild) and report 0 (looking forward) until it appears.
            if (_character == null)
                _character = _motor.GetComponentInParent<ICharacter>();
            if (_lookHandler == null && _character != null)
                _lookHandler = _character.GetCC<ILookHandlerCC>();
            float pitch = _lookHandler != null ? _lookHandler.ViewAngles.x : 0f;

            // ADR-024: subscribe to the LOCAL health manager once it resolves, so DamageReceived
            // bumps the hit-reaction counter (cosmetic; relayed to peers, not authoritative).
            if (_health == null && _character != null)
            {
                _health = _character.HealthManager;
                if (_health != null)
                    _health.DamageReceived += OnDamageReceived;
            }

            // ADR-022: report the LOCAL worn clothing item IDs read from the inventory equipment
            // slots. STP's CharacterClothing applies them locally; we only READ (rule #3). Relayed
            // cosmetically to peers, not authoritative.
            ReadEquipment();

            // ADR-023: report the LOCAL held wieldable item ID, read from the holster's selected
            // slot. STP's WieldableInventory applies the equip locally; we only READ (rule #3).
            int heldItem = ReadHeldItem();

            // ADR-026 (enmienda 2026-07-06, Opción C): the wire's Y convention is
            // "feet + PlayerBaseY" — the same one the backend's spawn/floor-fallback and the
            // phantom natively emit, and the one RemotePlayerManager un-does on the receiving
            // side (root.y = wireY − PlayerBaseY). The motor's transform is FEET-pivoted, so
            // compensate at the source; sending the raw feet Y would make the proxy double-
            // subtract and sink the body ~1.8 m into the floor. XZ and velocity stay raw.
            Vector3 wirePos = pos;
            wirePos.y += GridConstants.PlayerBaseY;

            bool sent = false;
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                ipc.SendPlayerInput(_inputSeq, _clientTick, wirePos, vel, moveState, pitch, yaw, 0, crouch, _equipment, heldItem, _hitSeq);
                _inputSeq++;
                _clientTick++;
                LastSent = wirePos;
                sent = true;
            }

            IsSending = sent;
        }

        /// <summary>
        /// Finds the LOCAL motor live. Revalidates the cache (Unity's overloaded == reports a
        /// destroyed motor as null, so a rig rebuild forces a re-find) and excludes remote
        /// avatars (anything under a RemotePlayerManager hierarchy).
        /// </summary>
        private void ResolveMotor()
        {
            if (_motor != null)
                return;

            var motors = FindObjectsByType<CharacterControllerMotor>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < motors.Length; i++)
            {
                var m = motors[i];
                if (m.GetComponentInParent<RemotePlayerManager>() != null)
                    continue; // remote avatar, not the local player

                _motor = m;
                return;
            }

            _motor = null;
        }

        /// <summary>
        /// ADR-022: fills <see cref="_equipment"/> with the 4 worn clothing item IDs in protocol
        /// order [Head, Torso, Legs, Feet] (0 = empty slot). Resolves the inventory equipment
        /// containers lazily off the cached character; re-resolves after a rig rebuild (motor loss).
        /// </summary>
        private void ReadEquipment()
        {
            if (!_equipmentResolved)
            {
                var inventory = _character?.Inventory;
                if (inventory != null)
                {
                    _headEquip = inventory.FindContainer(ItemContainerFilters.WithTag(ItemConstants.HeadEquipmentTag));
                    _torsoEquip = inventory.FindContainer(ItemContainerFilters.WithTag(ItemConstants.TorsoEquipmentTag));
                    _legsEquip = inventory.FindContainer(ItemContainerFilters.WithTag(ItemConstants.LegsEquipmentTag));
                    _feetEquip = inventory.FindContainer(ItemContainerFilters.WithTag(ItemConstants.FeetEquipmentTag));
                    _equipmentResolved = true;
                }
            }

            _equipment[0] = SlotItemId(_headEquip);
            _equipment[1] = SlotItemId(_torsoEquip);
            _equipment[2] = SlotItemId(_legsEquip);
            _equipment[3] = SlotItemId(_feetEquip);
        }

        // First-slot item id of an equipment container (0 = empty / missing container).
        private static int SlotItemId(IItemContainer container)
        {
            if (container == null || container.SlotsCount == 0)
                return 0;
            return container.GetItemAtIndex(0).Item?.Id ?? 0;
        }

        /// <summary>
        /// ADR-023: the LOCAL held wieldable item ID — the item in the holster's selected slot
        /// (0 = empty hands / nothing equipped). Resolves the wieldable inventory CC and the
        /// holster container (tagged <c>WieldableTag</c>) lazily off the cached character;
        /// re-resolves after a rig rebuild (motor loss). STP applies the equip; we only READ.
        /// </summary>
        private int ReadHeldItem()
        {
            if (!_heldResolved)
            {
                if (_character != null)
                {
                    _wieldableInv = _character.GetCC<IWieldableInventoryCC>();
                    _holster = _character.Inventory?.FindContainer(
                        ItemContainerFilters.WithTag(ItemConstants.WieldableTag));
                    // Resolved once both are present; otherwise retry next frame.
                    _heldResolved = _wieldableInv != null && _holster != null;
                }
            }

            if (_wieldableInv == null || _holster == null)
                return 0;

            int index = _wieldableInv.SelectedIndex;
            if (index < 0 || index >= _holster.SlotsCount)
                return 0;
            return _holster.GetItemAtIndex(index).Item?.Id ?? 0;
        }

        /// <summary>
        /// ADR-024: bump the monotonic hit-reaction counter on each real local damage event. The
        /// signature matches DamageReceivedDelegate (in DamageArgs). `damage` is the (negative)
        /// health delta; any DamageReceived is a hit, so we increment unconditionally (wrapping).
        ///
        /// ADR-025 Slice B: ALSO report the damage to the authoritative backend, so server health
        /// tracks local damage and the server owns the resulting death/respawn. DamageReceived
        /// only fires on REAL local damage (falls, hazards — ReceiveDamage): server-driven damage
        /// (starvation, entities, phantom) reaches this client via SetHealthSilent, which never
        /// raises this event → no double-count, no feedback loop. Same invariant as hit_seq.
        /// </summary>
        private void OnDamageReceived(float damage, in DamageArgs args)
        {
            _hitSeq++;
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
                ipc.SendReportDamage(Mathf.Abs(damage), args.DamageType.ToString());
        }

        // Drop the health subscription (rig rebuild / teardown). Safe to call when not subscribed.
        private void UnsubscribeHealth()
        {
            if (_health != null)
                _health.DamageReceived -= OnDamageReceived;
            _health = null;
        }

        private void OnDestroy()
        {
            UnsubscribeHealth(); // ADR-024: never leak the DamageReceived handler past teardown.
            if (_instance == this)
                _instance = null;
        }
    }
}
