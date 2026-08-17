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

        // RECORTE 2026-08-17 (prueba de escasez): 16 → 4, deshaciendo el test de abundancia de
        // 2026-07-07 que lo había subido ×4. One-shot seeding: cada cofre es una acción
        // spawn_world_chest procesada una sola vez, así que estos 4 son TODOS los cofres de la
        // partida entera y se siembran alrededor del HOST al arrancar (radio 5–200 m) — no hay
        // re-siembra ni cofres lejos de ese punto. Con el agua de almendras chest-only, esos 4
        // cofres son la ÚNICA fuente de agua del juego: ver la nota de RollChestLoot.
        private const int ChestCount = 4;
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
        //
        // EXCEPCIÓN DELIBERADA — "Almond Water" NO está en ChunkLootRoll.ConsumablePool ni en
        // StpItemSpawner.ConsumablePool a propósito (decisión de Joel, no descuido): es el
        // objeto de lore más importante del juego (ADR-030 amendment) y solo debe salir de
        // cofres — loot concentrado que YA se lee como "encontrado", no de las cachés/chunks
        // sueltas del mundo. A 8 entradas equiprobables sale en ~1/8 de los slots de consumible
        // de un cofre.
        //
        // RECORTE TOTAL DE CATÁLOGO (2026-08-17), en paridad con ChunkLootRoll: un cofre solo
        // sirve "Almond Water" y "Spray Can". Las listas completas se conservan comentadas justo
        // debajo de cada una porque aquí NO hay un bool-puerta como el de ChunkLootRoll: el cofre
        // elige pool por llamada a AddRoll, así que el recorte se hace en RollChestLoot y estas
        // listas son lo único que habría que reescribir de memoria al revertir.
        //
        // Pool completa previa al recorte:
        //   "Apple", "Cooked Meat", "Raw Meat", "Energy Bar", "Small Food Can", "Large Food Can",
        //   "Water Bottle", "Almond Water"
        private static readonly string[] ConsumablePool = { "Almond Water" };
        // MedicalPool RETIRADA por el recorte: era { "Antibiotics", "Medicinal Corn" }. Se borra
        // la declaración en vez de dejarla huérfana porque un array privado que nadie lee dispara
        // CS0414 y ensucia el compile-check de las cuatro asambleas.
        // RECORTE DE CATÁLOGO VENDOR (2026-08-10): AmmoPool eliminada — sin rifle ni arco, la
        // munición era basura de inventario. Su stack del cofre pasa a material (ver RollChestLoot).
        // ADR-064 (DIFERIDO): los 4 materiales de crafteo entran aquí a la vez que en
        // ChunkLootRoll.MaterialPool y a la vez que se generan sus assets — si divergen, el cofre
        // sirve lo que el mundo no da (o al revés). Ver la nota larga en ChunkLootRoll.
        // ADR-068 S4: el bote también en los cofres, por lo mismo que está en el suelo. Se
        // mantiene en PARIDAD con ChunkLootRoll.MaterialPool a propósito: que el cofre sirva un
        // catálogo distinto del mundo es una decisión que se toma a la vez para las dos listas,
        // no un descuido de una.
        // Pool completa previa al recorte de 2026-08-17:
        //   "Stick", "Rope", "Cloth", "Leather", "Metal Shard", "Stone Shard", "Feather",
        //   "Duct Tape", "Wooden Torch", "Spray Can"
        private static readonly string[] MaterialPool = { "Spray Can" };
        // WeaponPool RETIRADA por el recorte, misma razón que MedicalPool: era
        // { "Bone Club", "Steel Pickaxe" } — lo que quedaba tras el recorte de catálogo vendor de
        // 2026-08-10, que ya había sacado armas de fuego/caza y el kit de cazador.

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
        /// One chest's contents. SEGUNDO RECORTE 2026-08-17 (pedido de Joel, tras ver el primero
        /// en juego): **exactamente UNA agua de almendras por cofre**, y el TOTAL de objetos del
        /// cofre sale del reparto 80% uno / 15% dos / 5% tres. O sea: 4 de cada 5 cofres son una
        /// botella y nada más; el sobrante (0–2 objetos) son botes de spray.
        ///
        /// Antes de este recorte eran 2 stacks de 1–2 aguas + 1 bote; antes del primer recorte,
        /// 7–8 stacks con arma garantizada, médicos, consumibles y materiales.
        ///
        /// El reparto es el MISMO que el de las cachés del mundo (ChunkLootRoll.RollItemCount) y
        /// está duplicado a propósito en vez de compartido: aquel es puro y determinista por chunk
        /// (DeterministicRng sembrado con worldSeed+coord, tiene que ser reproducible entre
        /// recargas), y este es una tirada de sesión con UnityEngine.Random sobre un cofre que solo
        /// se siembra una vez. Compartir el helper obligaría a arrastrar el rng determinista hasta
        /// aquí para nada. Si se toca uno, tocar el otro.
        ///
        /// ⚠ CUENTA DE AGUA DE TODA LA PARTIDA: 4 cofres × 1 botella = **4 aguas de almendras**,
        /// sembradas de una sola vez alrededor del host y sin re-siembra. No hay ninguna otra
        /// fuente (ChunkLootRoll solo da botes de spray por la enmienda de ADR-030). Contra el
        /// drenaje de sed actual del backend (−0,07/s ⇒ ~24 min de barra llena) y los +50..70 de
        /// sed por botella, eso cubre del orden de **45 min a 1 h de sed por partida**. Es un
        /// número deliberadamente brutal, elegido por Joel. Si el playtest sale injugable, el
        /// orden de los diales es: primero bajar el drenaje del backend, después subir ChestCount
        /// — NO volver a meter agua en las cachés del mundo, que es lo que la enmienda de ADR-030
        /// decidió a propósito.
        ///
        /// Unresolved item names are skipped with a warning, mirroring StpItemSpawner's tolerance.
        /// </summary>
        private static List<CorpseLootStack> RollChestLoot()
        {
            var loot = new List<CorpseLootStack>();
            AddRoll(loot, ConsumablePool, 1, 1, 1); // exactamente 1 agua, sin rango

            int extra = RollObjectCount() - 1; // el agua ya ocupa el primer objeto del reparto
            if (extra > 0)
                AddRoll(loot, MaterialPool, extra, 1, 1);
            return loot;
        }

        /// <summary>Reparto de tamaño de contenedor: 80% un objeto, 15% dos, 5% tres. Espejo de
        /// <c>ChunkLootRoll.RollItemCount</c> — ver la nota de RollChestLoot sobre por qué está
        /// duplicado en vez de compartido.</summary>
        private static int RollObjectCount()
        {
            float r = Random.value;
            if (r < 0.05f) return 3;
            if (r < 0.20f) return 2;
            return 1;
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
