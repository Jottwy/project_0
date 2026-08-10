using System.Collections.Generic;
using PolymindGames.InventorySystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-028 amendment (world chests, "Opción B"): the HOST seeds a handful of supply chests —
    /// concentrated, better loot than StpItemSpawner's loose caches (which stay alive in
    /// parallel; chests are ADDITIVE). Each chest is one `spawn_world_chest` IPC action
    /// (position raycast against the RENDERED world + loot rolled client-side, trust-the-client
    /// like report_death_loot); the backend stores it as a `world.corpses` entry with
    /// `is_chest=true`, so ALL the corpse machinery (CorpseList mirror to joiners,
    /// take_corpse_item + P2P hop, despawn-on-empty) is reused untouched. CorpseSpawner renders
    /// it as a crate instead of a ragdoll.
    ///
    /// Structure mirrors StpItemSpawner: host-gate, warmup, per-chest retry while the host
    /// explores (a chest whose candidate lands outside the ~150 m rendered window stays pending).
    /// request_id per chest is a FIXED high-namespace constant + chest index — stable across
    /// re-sends/reconnects (that is what makes the server-side dedupe effective) and outside the
    /// small incremental id space world_interact uses on the same processed_interactions set.
    /// Self-bootstraps; fully removable.
    /// </summary>
    public sealed class StpChestSpawner : MonoBehaviour
    {
        private static StpChestSpawner _instance;

        public string gameplayScene = "STP_Showcase";
        [Min(0f)] public float warmupSeconds = 2f;

        // TODO(balance): abundance test (2026-07-07) — bumped 4→16 (~x4) to test chest density.
        // One-shot seeding: each chest is one spawn_world_chest action processed once; visible_corpses
        // is radius-filtered so only the few near the player are ever relayed at once (broadcast stays
        // bounded). Not a tuned final value. See ADR-028 amendment.
        private const int ChestCount = 16;
        private const float ScatterMinRadius = 5f;
        private const float ScatterMaxRadius = 200f;
        // Placement attempts + ray geometry live in LootPlacement (shared with the item and
        // carryable spawners); the ray origin MUST stay under LAYER_HEIGHT — see the note there.

        private const float RetryIntervalSeconds = 10f;
        private const float RetryWindowSeconds = 180f;

        // High-namespace base for the per-chest request_id (see class doc). Arbitrary constant,
        // stable across sessions by design.
        private const long RequestIdBase = 0x43_48_45_53_54L << 8; // "CHEST" << 8

        // Chest loot rolls — richer than a cache: guaranteed weapon, plus medical/consumables/
        // materials. Pools espejan las de ChunkLootRoll (mismos nombres autorados); si allí se
        // recorta el catálogo, aquí también, o el cofre sigue sirviendo lo que el mundo ya no da.
        // TODO(balance): placeholder composition/quantities.
        private static readonly string[] ConsumablePool =
        {
            "Apple", "Cooked Meat", "Raw Meat", "Energy Bar", "Small Food Can", "Large Food Can", "Water Bottle"
        };
        private static readonly string[] MedicalPool = { "Antibiotics", "Medicinal Corn" };
        // RECORTE DE CATÁLOGO VENDOR (2026-08-10): AmmoPool eliminada — sin rifle ni arco, la
        // munición era basura de inventario. Su stack del cofre pasa a material (ver RollChestLoot).
        private static readonly string[] MaterialPool =
        {
            "Stick", "Rope", "Cloth", "Leather", "Metal Shard", "Stone Shard", "Feather", "Duct Tape", "Wooden Torch"
        };
        // Mismo recorte que ChunkLootRoll.WeaponPool: fuera armas de fuego/caza y kit de cazador.
        private static readonly string[] WeaponPool =
        {
            "Bone Club", "Steel Pickaxe"
        };

        private float _warmupEnd;
        private bool _warmedUp;
        private int _pendingChestCount = ChestCount;
        private int _chestsConfirmedSoFar;
        private float _nextAttemptAt;
        private float _retryDeadline;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpChestSpawner]");
            _instance = go.AddComponent<StpChestSpawner>();
            DontDestroyOnLoad(go);
        }

        private void OnEnable()
        {
            _warmupEnd = Time.unscaledTime + warmupSeconds;
        }

        private void Update()
        {
            if (_pendingChestCount <= 0)
                return; // every chest seeded, or the retry window gave up on the rest

            var init = NetworkInitializer.Instance;
            if (init == null || init.CurrentRole != NetworkInitializer.Role.Host)
                return;

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            if (SceneManager.GetActiveScene().name != gameplayScene)
                return;

            if (!_warmedUp)
            {
                if (Time.unscaledTime < _warmupEnd)
                    return;
                _warmedUp = true;
                _retryDeadline = Time.unscaledTime + RetryWindowSeconds;
                _nextAttemptAt = Time.unscaledTime;
            }

            if (Time.unscaledTime < _nextAttemptAt)
                return;

            if (Time.unscaledTime > _retryDeadline)
            {
                Debug.Log($"[StpChestSpawner] retry window elapsed with {_pendingChestCount} chest(s) never finding walkable ground; giving up on them.");
                _pendingChestCount = 0;
                return;
            }

            _nextAttemptAt = Time.unscaledTime + RetryIntervalSeconds;

            var cam = Camera.main;
            if (cam == null)
                return;

            int seededThisRound = 0;
            int attemptsThisRound = _pendingChestCount;
            for (int c = 0; c < attemptsThisRound; c++)
            {
                if (!LootPlacement.TryFindWalkablePoint(cam.transform.position, ScatterMinRadius, ScatterMaxRadius, out Vector3 pos))
                    continue; // stays pending

                _pendingChestCount--;
                int chestIndex = _chestsConfirmedSoFar++;
                var loot = RollChestLoot();
                if (loot.Count == 0)
                {
                    Debug.LogWarning("[StpChestSpawner] rolled an empty chest (all item names unresolved?); skipped.");
                    continue;
                }

                ipc.SendSpawnWorldChest(RequestIdBase + chestIndex, pos, loot);
                seededThisRound++;
                Debug.Log($"[StpChestSpawner] seeded chest #{chestIndex} at {pos:F1} with {loot.Count} stacks.");
            }

            if (seededThisRound > 0 && _pendingChestCount <= 0)
                Debug.Log("[StpChestSpawner] all chests seeded.");
        }

        /// <summary>
        /// One chest's contents: guaranteed weapon, 1–2 medical, 3 consumables, 4 materials
        /// (9–10 stacks — a cache is 6 loose single items). Unresolved item names are skipped with
        /// a warning, mirroring StpItemSpawner's tolerance. (El "~8–9" anterior ya estaba mal antes
        /// del recorte: 1 + 1..2 + 3 + 2 son 7–8, no 8–9.)
        /// Recorte de catálogo vendor (2026-08-10): los 2 stacks de munición pasaron a material
        /// (2→4 rolls) en vez de desaparecer, para no adelgazar el cofre — sigue siendo más rico
        /// que una cache, que es lo que justifica su existencia.
        /// </summary>
        private static List<CorpseLootStack> RollChestLoot()
        {
            var loot = new List<CorpseLootStack>();
            AddRoll(loot, WeaponPool, 1, 1, 1);
            AddRoll(loot, MedicalPool, Random.Range(1, 3), 1, 1);
            AddRoll(loot, ConsumablePool, 3, 1, 2);
            AddRoll(loot, MaterialPool, 4, 1, 3);
            return loot;
        }

        private static void AddRoll(List<CorpseLootStack> loot, string[] pool, int rolls, int minQty, int maxQty)
        {
            for (int i = 0; i < rolls; i++)
            {
                string name = pool[Random.Range(0, pool.Length)];
                var def = ItemDefinition.GetWithName(name);
                if (def == null)
                {
                    Debug.LogWarning($"[StpChestSpawner] item '{name}' not found in ItemDefinition DB; skipped.");
                    continue;
                }
                loot.Add(new CorpseLootStack
                {
                    itemId = def.Id,
                    quantity = Random.Range(minQty, maxQty + 1)
                });
            }
        }

        /// <summary>
        /// Same walkable check as StpItemSpawner/StpCarryableSpawner (deliberately duplicated,
        /// same scope-containment note as theirs): raycast down against the rendered floor's
        /// GeoMask, ray origin kept under the 4 m layer ceiling.
        /// </summary>
        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
