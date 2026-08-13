using BackroomsSurvival.Gameplay.GridWorld;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.MovementSystem;
using PolymindGames.WieldableSystem;
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
        // ADR-009: source of the local run state — the ONLY thing that makes the server drain
        // stamina (game_loop::apply_client_authoritative_move gates on move_state == 2).
        private IMovementControllerCC _movement;
        // Lean (Q/E): source of the local body-lean state, relayed as two bits of `buttons`. STP's
        // BodyLeanHandler owns the local effect (camera roll + side offset); we only READ its
        // resulting state, never the raw input — so a lean the vendor REFUSED (obstruction, speed
        // cap, cooldown) is never reported as one.
        private IBodyLeanHandlerCC _leanHandler;
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
        // ADR-042: cached lights of the ACTIVE wieldable. We do not ask "is this a torch?" — we ask
        // "is any Light under the equipped wieldable enabled?", which is exact rather than heuristic
        // because LightEffect.OnEnable/OnDisable write Light.enabled directly. Consequence on
        // purpose: a lighter/flare/flashlight works the day it exists, with no table and no wire
        // change. The cache is keyed on the wieldable REFERENCE (re-resolved when it changes), so an
        // equip/holster/rig-rebuild re-scans and nothing per-frame walks the hierarchy.
        private IWieldablesControllerCC _wieldablesController;
        private Object _lightsCachedFor;
        private Light[] _wieldableLights = System.Array.Empty<Light>();
        // ADR-044: melee swings are SAMPLED, not subscribed — the only place in this family where
        // that is true, and it is forced: MeleeWeapon exposes no swing event (its public surface is
        // IsUsing, UseBlocker and Use), unlike IFirearmTrigger.Shoot. Adding one would mean editing
        // vendor code, which is forbidden. Sampling the rising edge at 30 Hz is correct by MARGIN,
        // not by luck: an attack cycle lasts on the order of half a second, more than an order of
        // magnitude above the 33 ms sampling period.
        private bool _wasSwinging;
        private byte _meleeSeq;
        // ADR-049: carry state is HYBRID, and both halves are forced by the vendor's public surface.
        // The definition comes from the ObjectCarryStarted/Stopped EVENTS because
        // CarryableController.ActiveDefinition is private and ICarryableControllerCC does not expose
        // it. The count is SAMPLED because there is no per-unit event at all — TryCarryObject stacks
        // silently. Sampling the count is safe by margin: picking up is a deliberate human action,
        // orders of magnitude slower than the 30 Hz send.
        private ICarryableControllerCC _carryController;
        private int _carryDef;
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
                _movement = null;    // idem: the movement controller dies with the rig
                _leanHandler = null; // idem: the body-lean handler dies with the rig too
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
                // ADR-042: the rig rebuild destroys the wieldables controller and every wieldable
                // instance under it — drop the controller and the light cache so both re-resolve.
                _wieldablesController = null;
                _lightsCachedFor = null;
                _wieldableLights = System.Array.Empty<Light>();
                // ADR-044: the wieldable instance died with the rig. Clear the swing edge so the
                // first sample against the fresh weapon cannot be read as a swing that never happened.
                _wasSwinging = false;
                // ADR-049: the carry controller died with the rig too. Unsubscribe and forget the
                // cached definition — CarryableController.OnBehaviourDestroy does NOT raise
                // ObjectCarryStopped, so without this the id would survive its own controller.
                UnsubscribeCarry();
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
            // sprint cap). It MUST report 2 while sprinting: the backend gates the whole drain on
            // `input.move_state == 2`, so the previous idle/walk-only classification meant stamina
            // never dropped while connected — the StatInterpolator faithfully displayed a bar that
            // the server was regenerating and never draining.
            //
            // The run state is READ off STP's own movement controller (rule #3: never reimplement
            // the vendor's sprint conditions). The character is a parent of the motor; resolve it
            // lazily here — the pitch block below reuses the same cached reference.
            if (_character == null)
                _character = _motor.GetComponentInParent<ICharacter>();
            if (_movement == null && _character != null)
                _movement = _character.GetCC<IMovementControllerCC>();

            Vector3 horiz = new Vector3(vel.x, 0f, vel.z);
            bool moving = horiz.magnitude > 0.1f;
            // Guard on `moving` too: STP keeps the Run state active for the tail of a stop, and a
            // standing player must not bleed stamina.
            bool running = moving && _movement != null && _movement.ActiveState == MovementStateType.Run;
            byte moveState = running ? (byte)2 : moving ? (byte)1 : (byte)0;

            // ADR-020: report the LOCAL crouch state, derived from the motor height. STP's native
            // CharacterCrouchState lowers Motor.Height when crouching; we only READ it (never apply,
            // never reimplement — rule #3). Relayed cosmetically to peers; not authoritative.
            var motorCC = (IMotorCC)_motor;
            bool crouch = motorCC.Height < motorCC.DefaultHeight - 0.05f;

            // ADR-021: report the LOCAL camera pitch (degrees). ViewAngles.x is the vertical
            // look angle, already clamped by CharacterLookHandler. The look handler is a sibling
            // component on the same character as the motor; resolve it lazily (it may be null for
            // a frame after a rig rebuild) and report 0 (looking forward) until it appears.
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

            // ADR-042: report whether the equipped wieldable is emitting light, and the shot counter
            // the NoiseReporter keeps (one local Shoot event, two independent outputs — see there).
            // Both are pure READS, like every other field here (rule #3: the transmitter reports,
            // it never applies).
            bool lightOn = ReadLightOn();
            byte fireSeq = NoiseReporter.ShotCounter;

            // ADR-044: sustained states as a bitfield (aiming/reloading) + the sampled melee edge.
            // Reads only — STP owns the local effect (rule #3).
            int buttons = ReadButtons();
            SampleMeleeSwing();

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
                byte carryCount = SampleCarryCount();
                ipc.SendPlayerInput(_inputSeq, _clientTick, wirePos, vel, moveState, pitch, yaw,
                    (ushort)buttons, crouch, _equipment, heldItem, _hitSeq, lightOn, fireSeq, _meleeSeq,
                    _carryDef, carryCount);
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
        /// avatars (anything under a RemotePlayerManager hierarchy) — shared scan in
        /// <see cref="LocalPlayerLocator.Find{T}"/>.
        /// </summary>
        private void ResolveMotor()
        {
            if (_motor != null)
                return;

            _motor = LocalPlayerLocator.Find<CharacterControllerMotor>();
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
        /// ADR-042: true when the ACTIVE wieldable is emitting light. Deliberately item-agnostic —
        /// we never ask "is this the torch?", we ask whether any <see cref="Light"/> under the
        /// equipped wieldable is enabled. That is exact, not a heuristic: STP's
        /// <c>LightEffect.OnEnable/OnDisable</c> write <c>Light.enabled</c>, so the component IS the
        /// switch. Nothing inside PolymindGames is modified or wrapped; this is a read from outside.
        ///
        /// <c>GetComponentsInChildren(true)</c> includes INACTIVE children on purpose (a torch's
        /// flame object can start disabled), so the per-frame test also has to check
        /// <c>activeInHierarchy</c> — an enabled Light on a disabled object emits nothing.
        /// </summary>
        private bool ReadLightOn()
        {
            if (_wieldablesController == null && _character != null)
                _wieldablesController = _character.GetCC<IWieldablesControllerCC>();
            if (_wieldablesController == null)
                return false;

            // A DESTROYED wieldable still hands back a non-null interface reference (C# `?.` does
            // not see Unity's fake-null), and touching `.gameObject` on it throws
            // MissingReferenceException — so test the Unity lifetime first. NullWieldable is not a
            // UnityEngine.Object, so it falls through and is caught by the null-gameObject branch.
            var wieldable = _wieldablesController.ActiveWieldable;
            bool destroyed = wieldable is Object uo && uo == null;
            var go = (wieldable == null || destroyed) ? null : wieldable.gameObject;
            if (go == null)
            {
                _lightsCachedFor = null;
                _wieldableLights = System.Array.Empty<Light>();
                return false;
            }

            if (!ReferenceEquals(_lightsCachedFor, go))
            {
                _lightsCachedFor = go;
                _wieldableLights = go.GetComponentsInChildren<Light>(true);
            }

            for (int i = 0; i < _wieldableLights.Length; i++)
            {
                var l = _wieldableLights[i];
                if (l != null && l.enabled && l.gameObject.activeInHierarchy)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// ADR-044: the peer's SUSTAINED cosmetic states, packed into the (until now dead) `buttons`
        /// field. Both are plain property reads off the active wieldable — <c>IsAiming</c> on the
        /// firearm's aim handler and <c>IsReloading</c> on the firearm itself — so nothing is
        /// subscribed and nothing can go stale across a weapon swap or a rig rebuild.
        ///
        /// Non-firearms report neither, which is correct rather than a gap: a torch has no sights
        /// and no magazine, so the honest answer for both bits is false.
        ///
        /// The lean bits (Q/E) are read FIRST and unconditionally, because they belong to the body
        /// and not to whatever is in the hands: leaning with empty hands or a torch is exactly as
        /// legitimate as leaning with a rifle. That is why the firearm test below is an early
        /// `return bits` and not the early `return 0` it used to be.
        /// </summary>
        private int ReadButtons()
        {
            int bits = 0;

            // Lean is the sustained state ADR-044 anticipated: it rides the free bits of `buttons`,
            // so it costs no wire field and no schema bump. Read off the vendor's handler (which has
            // already applied its own obstruction / speed / cooldown rules), never off the raw axis.
            if (_leanHandler == null && _character != null)
                _leanHandler = _character.GetCC<IBodyLeanHandlerCC>();
            if (_leanHandler != null)
            {
                switch (_leanHandler.LeanState)
                {
                    case BodyLeanState.Left: bits |= RemoteButtons.LeanLeft; break;
                    case BodyLeanState.Right: bits |= RemoteButtons.LeanRight; break;
                }
            }

            var wieldable = ActiveWieldable();
            if (wieldable is not IFirearm firearm)
                return bits;

            var aim = firearm.AimHandler;
            if (aim != null && aim.IsAiming)
                bits |= RemoteButtons.Aiming;
            // Via the magazine, not the concrete Firearm: `IsReloading` lives on
            // IFirearmReloadableMagazine, and going through the interface keeps this working for any
            // IFirearm implementation, not just the vendor's concrete class.
            var magazine = firearm.ReloadableMagazine;
            if (magazine != null && magazine.IsReloading)
                bits |= RemoteButtons.Reloading;
            return bits;
        }

        /// <summary>
        /// ADR-044: bumps the melee counter on the RISING edge of <c>MeleeWeapon.IsUsing</c>. The one
        /// sampled signal in the family — see the field comment for why an event is not available.
        /// The edge is cleared whenever the wieldable is not a melee weapon, so holstering mid-swing
        /// and re-equipping cannot manufacture a phantom second swing.
        /// </summary>
        private void SampleMeleeSwing()
        {
            var melee = ActiveWieldable() as MeleeWeapon;
            bool swinging = melee != null && melee.IsUsing;

            if (swinging && !_wasSwinging)
                unchecked { _meleeSeq++; }

            _wasSwinging = swinging;
        }

        /// <summary>
        /// ADR-049: how many carryables are on the local player's shoulder right now, resolving and
        /// subscribing to the controller on the way if needed.
        ///
        /// The count is read live and the definition arrives by event, so the two can disagree for
        /// exactly one case: a late subscribe, where the player was already carrying before this
        /// component resolved the controller and the "started" event has therefore already passed.
        /// That reports (0, 0) rather than a count with no id — a peer briefly empty-handed is a
        /// cosmetic miss, an id invented to fill the gap is a wrong model in someone's hands.
        /// </summary>
        private byte SampleCarryCount()
        {
            if (_carryController == null && _character != null)
            {
                _carryController = _character.GetCC<ICarryableControllerCC>();
                if (_carryController != null)
                {
                    _carryController.ObjectCarryStarted += OnCarryStarted;
                    _carryController.ObjectCarryStopped += OnCarryStopped;
                }
            }

            if (_carryController == null || _carryDef == 0)
                return 0;

            int count = _carryController.CarryCount;
            return count <= 0 ? (byte)0 : (byte)Mathf.Min(count, byte.MaxValue);
        }

        private void OnCarryStarted(CarryableDefinition definition) =>
            _carryDef = definition != null ? definition.Id : 0;

        private void OnCarryStopped() => _carryDef = 0;

        /// <summary>Drops the carry subscription and the cached id together — they are only ever
        /// valid as a pair, and the vendor does not raise "stopped" when its controller dies.</summary>
        private void UnsubscribeCarry()
        {
            if (_carryController != null)
            {
                _carryController.ObjectCarryStarted -= OnCarryStarted;
                _carryController.ObjectCarryStopped -= OnCarryStopped;
                _carryController = null;
            }

            _carryDef = 0;
        }

        /// <summary>The live active wieldable, or null. Shares the controller cache with
        /// <see cref="ReadLightOn"/> and applies the same destroyed-instance guard: a destroyed
        /// wieldable still hands back a non-null interface reference, and C# `?.` does not see
        /// Unity's fake-null.</summary>
        private IWieldable ActiveWieldable()
        {
            if (_wieldablesController == null && _character != null)
                _wieldablesController = _character.GetCC<IWieldablesControllerCC>();
            if (_wieldablesController == null)
                return null;

            var wieldable = _wieldablesController.ActiveWieldable;
            if (wieldable is Object uo && uo == null)
                return null;
            return wieldable;
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
            UnsubscribeCarry(); // ADR-049: same, for the two carry events.
            if (_instance == this)
                _instance = null;
        }
    }
}
