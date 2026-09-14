using BackroomsSurvival.Gameplay.Medical;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>
    /// ADR-149: el cuerpo por zonas del jugador local. Sin backend decide en local (zona, lesión, sangrado, cojera); con
    /// backend (R2a) es un espejo de <c>body_state</c> y tratar manda <c>treat_zone</c>. R2c: se monta solo en cualquier
    /// escena (la de pruebas trae el suyo con la ropa al lado) y la venda por brazo de <see cref="PlayerMedicalState"/> se
    /// deriva de él (<see cref="BodyMedicalBridge"/>), así que vendarse trata la zona real del cuerpo.
    /// </summary>
    public sealed class BackroomsBodyPrototype : MonoBehaviour
    {
        [SerializeField]
        private ItemDefinition _bandage;

        [SerializeField]
        private ItemDefinition _splint;

        /// <summary>El cuerpo del jugador de esta escena. Estático para que la vista Heridas no necesite referencia.</summary>
        public static BodyState Local { get; } = new BodyState();

        public static BackroomsBodyPrototype Instance { get; private set; }

        private Player _player;
        private IHealthManager _health;
        private IMovementControllerCC _movement;
        private IInventory _inventory;
        private readonly byte[] _serverZones = new byte[BodyZones.Count];
        private float _pendingBleed;
        private bool _applyingBleed;
        private IPCClient _ipc;
        private uint _draws;

        private BackroomsGarmentPrototype _garments;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        /// <summary>ADR-149 R2c: en toda escena, si la escena no trae el suyo (la de pruebas sí, con la ropa por zonas).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("[BackroomsBody]");
            go.AddComponent<BackroomsBodyPrototype>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            _garments = GetComponent<BackroomsGarmentPrototype>();
            if (_bandage == null) _bandage = Resources.Load<ItemDefinition>("Definitions/Item/BR_Bandage");
            if (_splint == null) _splint = Resources.Load<ItemDefinition>("Definitions/Item/BR_Splint");
        }

        private void OnDestroy()
        {
            Unbind();
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                // ADR-149 R2a: con backend manda el servidor; aquí solo se espeja.
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
                Local.Clear();
                Debug.Log("[Cuerpo] backend conectado: el cuerpo pasa a espejo de body_state (ADR-149 R2a).");
            }
            if (_player == null && !TryBind()) return;
            if (_ipc != null) return;

            _pendingBleed += Local.Tick(Time.deltaTime);
            if (_pendingBleed >= 1f)
            {
                float amount = _pendingBleed;
                _pendingBleed = 0f;
                _applyingBleed = true;
                _health.ReceiveDamage(amount, new DamageArgs(DamageType.Undefined));
                _applyingBleed = false;
            }
        }

        private bool TryBind()
        {
            var players = Player.AllPlayers;
            if (players.Count == 0) return false;
            var player = players[0];
            if (player.HealthManager == null || player.Inventory == null || !player.TryGetCC(out IMovementControllerCC movement)) return false;

            _player = player;
            _health = player.HealthManager;
            _inventory = player.Inventory;
            _movement = movement;
            _health.DamageReceived += OnDamageReceived;
            _health.Respawn += OnRespawn;
            _movement.SpeedModifier.AddModifier(GetSpeed);
            Local.Clear();
            // ADR-149 R2c: la venda por brazo deja de decidir por su cuenta y se deriva del cuerpo.
            var medical = PlayerMedicalState.Local;
            medical.BodyDriven = true;
            medical.BandageOverride = TreatArm;
            Local.Changed += OnBodyChanged;
            BodyMedicalBridge.Sync(Local, medical);
            return true;
        }

        private void OnGameEvent(GameEventMsg ev)
        {
            if (BodyStateMirror.TryRead(ev, _serverZones, out _, out _)) Local.ApplyRaw(_serverZones);
        }

        private void Unbind()
        {
            if (_ipc != null)
            {
                _ipc.RemoveEventListener(OnGameEvent);
                _ipc = null;
            }
            if (_health != null)
            {
                _health.DamageReceived -= OnDamageReceived;
                _health.Respawn -= OnRespawn;
            }
            if (_movement != null) _movement.SpeedModifier.RemoveModifier(GetSpeed);
            if (_player != null)
            {
                Local.Changed -= OnBodyChanged;
                PlayerMedicalState.Local.BodyDriven = false;
                PlayerMedicalState.Local.BandageOverride = null;
            }
            _player = null;
            _health = null;
            _movement = null;
            _inventory = null;
            Local.Clear();
        }

        private void OnRespawn() => Local.Clear();

        private void OnBodyChanged(BodyZone zone) => BodyMedicalBridge.Sync(Local, PlayerMedicalState.Local);

        /// <summary>La venda del juego (BandageWieldable) trata la zona abierta más grave de ese brazo. Ella gasta el objeto.</summary>
        private bool TreatArm(BodyPartSide side)
        {
            if (!BodyMedicalBridge.TryPickArmZone(Local, side, PlayerMedicalState.Local.RestrictToLeftArm, out var zone)) return false;
            if (_ipc != null)
            {
                _ipc.SendTreatZone((int)zone, (int)BodyTreatment.Bandage);
                return true;
            }
            return Local.Treat(zone, BodyTreatment.Bandage);
        }

        private float GetSpeed() => Local.LegSpeedMultiplier();

        private void OnDamageReceived(float damage, in DamageArgs args)
        {
            // Con backend la lesión la decide el servidor con el report_damage que ya manda PlayerPoseTransmitter.
            if (_applyingBleed || _player == null || _ipc != null) return;
            float amount = Mathf.Abs(damage);
            var zone = args.HitPoint != Vector3.zero
                ? BodyZoneResolver.FromLocalPoint(_player.transform.InverseTransformPoint(args.HitPoint))
                : BodyZoneResolver.Draw(args.DamageType, (uint)Time.frameCount + ++_draws);
            // ADR-149 enm. 1: la prenda más exterior que cubre la zona para parte del golpe (sin backend se devuelve en el
            // mismo frame) y se rompe; la lesión se calcula sobre lo que pasa.
            float protection = _garments != null ? _garments.Absorb(zone, amount, args.DamageType) : 0f;
            if (protection > 0f)
            {
                float stopped = amount * protection;
                _applyingBleed = true;
                _health.RestoreHealth(stopped);
                _applyingBleed = false;
                amount -= stopped;
            }
            var injury = Local.ApplyDamage(zone, amount, args.DamageType);
            if (injury != BodyInjury.None)
                Debug.Log($"[Cuerpo] {amount:0.#} de {args.DamageType} en {BodyZones.Label(zone)}: {injury}");
        }

        /// <summary>Trata la zona con lo que pida su lesión, gastando una unidad del inventario. Devuelve el aviso para la UI.</summary>
        public string TryTreat(BodyZone zone)
        {
            if (_player == null) return "Sin jugador";
            var injury = Local.InjuryOf(zone);
            if (injury == BodyInjury.None) return $"{BodyZones.Label(zone)}: nada que tratar";
            var treatment = BodyState.TreatmentFor(injury);
            if (!Local.CanTreat(zone, treatment))
                return treatment == BodyTreatment.Splint ? "Ya tiene férula" : "Ya está vendado";
            var item = treatment == BodyTreatment.Splint ? _splint : _bandage;
            string what = treatment == BodyTreatment.Splint ? "una férula" : "una venda";
            if (item == null || _inventory.RemoveItemsById(item.Id, 1) < 1) return $"Necesitas {what}";
            if (_ipc != null)
            {
                _ipc.SendTreatZone((int)zone, (int)treatment);
                return treatment == BodyTreatment.Splint ? $"Férula en {BodyZones.Label(zone)}" : $"Vendado: {BodyZones.Label(zone)}";
            }
            Local.Treat(zone, treatment);
            return treatment == BodyTreatment.Splint ? $"Férula en {BodyZones.Label(zone)}" : $"Vendado: {BodyZones.Label(zone)}";
        }
    }
}
