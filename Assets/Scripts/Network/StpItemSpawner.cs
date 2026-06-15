using System.Collections.Generic;
using PolymindGames.InventorySystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase 1: HOST-ONLY authoritative spawner of a fixed test ring of STP items.
    /// Resolves item definitions by NAME at runtime (robust, no hardcoded ids), assigns
    /// each a monotonic network instance id, and registers the list with the backend via
    /// the IPC `set_stp_items` action. The backend relays it to joiners; StpItemReplicator
    /// spawns the actual STP pickups on every instance from world_state.stp_items.
    /// Joiners never decide items themselves (and the backend ignores set_stp_items from a
    /// non-host, so a joiner can't diverge the world even if this runs). One-shot.
    /// </summary>
    public sealed class StpItemSpawner : MonoBehaviour
    {
        private static StpItemSpawner _instance;

        [Tooltip("STP item names (no STP_ prefix) for the test ring; resolved to def_id at runtime.")]
        public string[] itemNames = { "Apple", "Metal Shard", "Stick", "Rope", "Cooked Meat" };

        [Tooltip("Only spawn once the host is in this scene (avoids placing items in the menu).")]
        public string gameplayScene = "STP_Showcase";

        [Min(0.5f)] public float ringRadius = 3f;

        private bool _sent;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpItemSpawner]");
            _instance = go.AddComponent<StpItemSpawner>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (_sent)
                return;

            var init = NetworkInitializer.Instance;
            if (init == null || init.CurrentRole != NetworkInitializer.Role.Host)
                return;

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            if (SceneManager.GetActiveScene().name != gameplayScene)
                return;

            var cam = Camera.main;
            if (cam == null)
                return;

            Vector3 center = cam.transform.position;
            center.y -= 1.6f; // approximate floor under the eyes

            var specs = new List<StpItemSpec>();
            uint nextId = 1;
            int n = Mathf.Max(1, itemNames.Length);
            for (int i = 0; i < itemNames.Length; i++)
            {
                var def = ItemDefinition.GetWithName(itemNames[i]);
                if (def == null)
                {
                    Debug.LogWarning($"[StpItemSpawner] item '{itemNames[i]}' not found in ItemDefinition DB; skipped.");
                    continue;
                }

                float ang = i * Mathf.PI * 2f / n;
                Vector3 pos = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * ringRadius;
                specs.Add(new StpItemSpec
                {
                    id = nextId++,
                    defId = def.Id,
                    count = 1,
                    position = pos,
                    rotation = 0f
                });
            }

            if (specs.Count == 0)
                return; // DB not ready / no names resolved — retry next frame

            ipc.SendSetStpItems(specs);
            _sent = true;
            Debug.Log($"[StpItemSpawner] sent {specs.Count} STP items to backend (ring around {center:F1}).");
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
