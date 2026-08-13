using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase 3 (Option B): syncs STP's NATIVE drops (inventory "Drop" button + UI drag-out) with
    /// zero vendor edits. Both native paths end in a vendor DropAction that instantiates a local
    /// <see cref="ItemPickup"/> next to the dropper. This watcher detects MY freshly-dropped
    /// pickup, reads its item, DESTROYS it, and asks the host to spawn the replicated version
    /// (SendStpDrop → process_stp_drop → stp_items → Phase 1 relay → spawn with the Phase 2 gate).
    /// The 1-frame local pickup → host echo round-trip is a tolerable flicker for pre-alpha.
    ///
    /// Anti-false-adoption: a per-scene WARMUP records every pre-existing ItemPickup into a
    /// seen-set (Showcase props, items others dropped), so only brand-new pickups that appear
    /// NEAR me AFTER warmup are adopted. Replicated pickups (Phase 1/others' drops) carry
    /// <see cref="NetworkItemInstance"/> and are never adopted. Self-bootstraps; fully removable.
    /// </summary>
    public sealed class StpNativeDropWatcher : MonoBehaviour
    {
        private static StpNativeDropWatcher _instance;

        [Tooltip("Only adopt pickups within this distance of the local player (a native drop lands next to me).")]
        [Min(0.5f)] public float adoptRadius = 3f;

        [Tooltip("Record everything already in the scene for this long before adopting anything (anti-Showcase).")]
        [Min(0f)] public float warmupSeconds = 2f;

        [Min(0.02f)] public float scanInterval = 0.1f;
        public string gameplayScene = "STP_Showcase";

        [Tooltip("ADR-070: impulse given to a drop the vendor released with no velocity of its own, " +
                 "so it is tossed away from the chest instead of dead-dropping onto the player's feet.")]
        [Min(0f)] public float fallbackTossSpeed = DefaultFallbackTossSpeed;

        // El default del inspector y el que usa SynthesizeTossVelocity cuando no hay instancia viva
        // (el vuelco del restaurador puede correr antes de que este watcher arranque) son el MISMO
        // número, y por eso es una constante y no dos literales.
        private const float DefaultFallbackTossSpeed = 2.2f;

        private readonly HashSet<ItemPickup> _seen = new HashSet<ItemPickup>();
        // GameObject InstanceIDs already adopted — committed BEFORE the request is sent so a
        // re-scan or a slow/laggy frame can never send the same drop twice (dedup layer 1).
        private readonly HashSet<int> _adopted = new HashSet<int>();
        // static: lo comparte StpPickupController vía MintDropId — ver el doc de ese método.
        private static uint _dropCounter;
        private float _warmupEnd;
        private float _nextScan;
        private bool _warmedUp;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpNativeDropWatcher]");
            _instance = go.AddComponent<StpNativeDropWatcher>();
            DontDestroyOnLoad(go);
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            ArmWarmup();
        }

        private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        // Re-arm the warmup + clear the seen-set whenever a scene loads, so the items that come
        // in WITH the gameplay scene (Showcase) are recorded fresh and never adopted.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ArmWarmup();

        private void ArmWarmup()
        {
            _warmedUp = false;
            _warmupEnd = Time.unscaledTime + warmupSeconds;
            _seen.Clear();
            _adopted.Clear();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextScan)
                return;
            _nextScan = Time.unscaledTime + scanInterval;

            // Only act in the gameplay scene (avoids menu/other scenes).
            if (SceneManager.GetActiveScene().name != gameplayScene)
                return;

            var pickups = FindObjectsByType<ItemPickup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            // Warmup: record everything that already exists; adopt nothing.
            if (!_warmedUp)
            {
                for (int i = 0; i < pickups.Length; i++)
                    _seen.Add(pickups[i]);

                if (Time.unscaledTime >= _warmupEnd)
                    _warmedUp = true;
                return;
            }

            // HasInstance, not `Instance != null`: el getter de MonoSingleton del vendor emite un
            // Debug.LogError CON TRAZA cada vez que se lee sin instancia. Aquí el guard de escena de
            // arriba ya evitaba el spam del menú, así que esto es prevención: en la escena de
            // gameplay sin GameMode (carga, teardown) volvería a loguear por escaneo.
            var character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
            if (character == null)
                return; // no local player → can't judge "near me"; record nothing, retry next scan
            Vector3 localPos = character.transform.position;

            for (int i = 0; i < pickups.Length; i++)
            {
                var p = pickups[i];
                if (p == null)
                    continue;

                int iid = p.gameObject.GetInstanceID();
                if (_adopted.Contains(iid) || _seen.Contains(p))
                    continue;

                // Replicated pickups (Phase 1 / others' drops) carry NetworkItemInstance — even in
                // the 1-frame window before their vendor ItemPickup is destroyed. Never adopt those.
                if (p.GetComponent<NetworkItemInstance>() != null)
                {
                    _seen.Add(p);
                    continue;
                }

                // Only MY fresh native drop: a brand-new vendor pickup that landed next to me.
                if ((p.transform.position - localPos).sqrMagnitude > adoptRadius * adoptRadius)
                {
                    _seen.Add(p); // a far pickup that appeared (e.g. a loot spawner) — record, don't adopt
                    continue;
                }

                var stack = p.AttachedItem;
                if (!stack.HasItem())
                    continue; // DropAction instantiates then AttachItem — retry next scan (don't mark)

                if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                    continue; // can't sync yet; leave it so a later scan retries

                // Commit dedup BEFORE destroying/sending so the same drop can never go twice.
                _adopted.Add(iid);
                _seen.Add(p);

                int defId = stack.Item.Definition.Id;
                int count = Mathf.Max(1, stack.Count);
                long dropId = NextDropId();
                // ADR-070 decision 1: send the HAND position, unsnapped. The floor raycast that used
                // to run here is gone on purpose — where the object comes to rest is the host's call
                // now, and pre-snapping it was half of why drops looked dead (the other half being
                // the replicator freezing the Rigidbody).
                Vector3 hand = p.transform.position;
                Vector3 velocity = ReadTossVelocity(p, character);
                float yaw = p.transform.eulerAngles.y;

                Destroy(p.gameObject); // immediate (this frame) — closes the double-detection window
                ipc.SendStpDrop(dropId, defId, count, hand, yaw, velocity);
                Debug.Log($"[StpNativeDropWatcher] adopted native drop drop_id={dropId} def_id={defId} x{count} hand={hand:F2} vel={velocity:F2} → host.");
            }
        }

        /// <summary>
        /// ADR-070: the impulse to hand the host. PREFERS the velocity the vendor's own DropAction
        /// already gave the Rigidbody — inheriting the real toss beats inventing one, and it means a
        /// future vendor tweak to drop force carries over for free. Only when that reads as zero (the
        /// drop landed on a frame before the force, or the prefab has no body) do we synthesize one,
        /// away from the chest and slightly up, so the object arcs instead of dead-dropping onto the
        /// player's own feet where they cannot see it.
        /// </summary>
        private Vector3 ReadTossVelocity(ItemPickup pickup, ICharacter character)
        {
            var rb = pickup.GetComponentInChildren<Rigidbody>();
            if (rb != null && rb.linearVelocity.sqrMagnitude > 0.01f)
                return rb.linearVelocity;

            return SynthesizeTossVelocity(character);
        }

        /// <summary>
        /// ADR-070: el impulso de un vuelco que NO nace de un pickup del vendor — el sobrante de una
        /// recogida que no cupo (<see cref="StpPickupController"/>) y lo que el restaurador no pudo
        /// colocar (<c>InventoryRestorer</c>). Ahí no hay `Rigidbody` del que leer la velocidad real,
        /// porque el item nunca llegó a materializarse en local: hay que sintetizarla.
        ///
        /// `static` y compartida por la misma razón que <see cref="MintDropId"/>: si un día se ajusta
        /// la fuerza del vuelco, se ajusta para los tres caminos a la vez. Dos copias de esta fórmula
        /// es garantizar que un día el sobrante sale despedido distinto que el drop de mano.
        /// </summary>
        public static Vector3 SynthesizeTossVelocity(ICharacter character)
        {
            float speed = _instance != null ? _instance.fallbackTossSpeed : DefaultFallbackTossSpeed;

            Vector3 forward = character != null ? character.transform.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.forward;

            return (forward.normalized + Vector3.up * 0.35f) * speed;
        }

        private long NextDropId() => MintDropId();

        /// <summary>
        /// Globally-unique per logical drop: NET_ID-prefixed counter, so two clients never colisionan
        /// y el host puede deduplicar (capa 3).
        ///
        /// `static` y compartido A PROPÓSITO: <see cref="StpPickupController"/> también devuelve
        /// items al mundo (el sobrante de una concesión que no cupo) y necesita ids de este mismo
        /// espacio. Con un contador propio, ambos empezarían en 1 bajo el mismo prefijo de NET_ID y
        /// los ids chocarían — el host deduplica por id, así que la colisión no daría error: se
        /// TRAGARÍA el segundo drop en silencio, que es justo la pérdida de items que se está
        /// arreglando.
        /// </summary>
        public static long MintDropId()
        {
            int netId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
            return (long)Mathf.Max(1, netId) * 1000000000L + (++_dropCounter);
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
