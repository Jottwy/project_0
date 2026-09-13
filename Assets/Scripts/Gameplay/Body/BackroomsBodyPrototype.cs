using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>
    /// ADR-149 rebanada R0: el cuerpo por zonas en LOCAL. Escucha el daño del jugador, decide la zona
    /// (<see cref="BodyZoneResolver"/>), abre la lesión, sangra, frena por las piernas y trata con venda o férula del
    /// inventario. Condiciones de prototipo (como ADR-147): solo lo monta <c>BR_InventoryTest</c>. Con backend conectado
    /// (ADR-149 R2a) deja de decidir: el cuerpo es un espejo de <c>body_state</c> y tratar manda <c>treat_zone</c>. No toca
    /// <c>PlayerMedicalState</c> ni los bits 7/8 de la venda por brazo: eso es R2c.
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

        private void Awake()
        {
            Instance = this;
            _garments = GetComponent<BackroomsGarmentPrototype>();
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
            _player = null;
            _health = null;
            _movement = null;
            _inventory = null;
            Local.Clear();
        }

        private void OnRespawn() => Local.Clear();

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
