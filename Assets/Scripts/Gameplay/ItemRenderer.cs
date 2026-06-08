using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    public sealed class ItemRenderer : MonoBehaviour
    {
        [Header("Visuals")]
        public float bobAmplitude = 0.15f;
        public float bobSpeed = 2f;
        public float spinSpeed = 45f;

        private readonly Dictionary<uint, GameObject> _pool = new Dictionary<uint, GameObject>();
        private readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();

        private void Start()
        {
            _mats["metal"]    = MaterialHelper.MakeEmissive(new Color(0.70f, 0.72f, 0.75f), 0.5f);
            _mats["circuit"]  = MaterialHelper.MakeEmissive(new Color(0.15f, 0.60f, 0.30f), 0.5f);
            _mats["battery"]  = MaterialHelper.MakeEmissive(new Color(0.90f, 0.80f, 0.10f), 0.5f);
            _mats["cable"]    = MaterialHelper.MakeEmissive(new Color(0.20f, 0.20f, 0.25f), 0.5f);
            _mats["food"]     = MaterialHelper.MakeEmissive(new Color(0.85f, 0.55f, 0.25f), 0.5f);
            _mats["water"]    = MaterialHelper.MakeEmissive(new Color(0.30f, 0.60f, 0.90f), 0.5f);
            _mats["medicine"] = MaterialHelper.MakeEmissive(new Color(0.90f, 0.30f, 0.30f), 0.5f);
            _mats["tool"]     = MaterialHelper.MakeEmissive(new Color(0.50f, 0.50f, 0.50f), 0.5f);
        }

        private void LateUpdate()
        {
            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null) return;

            var alive = new HashSet<uint>();

            foreach (var iv in state.visibleItems)
            {
                alive.Add(iv.id);

                if (!_pool.TryGetValue(iv.id, out var go))
                {
                    go = SpawnItem(iv);
                    _pool[iv.id] = go;
                }

                // Floating bob + spin.
                float bob = Mathf.Sin(Time.time * bobSpeed + iv.id * 0.7f) * bobAmplitude;
                go.transform.position = iv.position + Vector3.up * (0.4f + bob);
                go.transform.Rotate(Vector3.up, spinSpeed * Time.deltaTime, Space.World);
            }

            // Remove items no longer visible.
            var stale = new List<uint>();
            foreach (var kv in _pool)
            {
                if (!alive.Contains(kv.Key))
                {
                    var netObj = kv.Value != null ? kv.Value.GetComponent<NetworkWorldObject>() : null;
                    if (netObj != null)
                        netObj.active = false;
                    Debug.Log($"MPTRACE step=AK event=unity_world_object_hidden target_id={kv.Key} kind=item revision={state.worldRevision}");
                    Destroy(kv.Value);
                    stale.Add(kv.Key);
                }
            }
            foreach (uint k in stale) _pool.Remove(k);
        }

        private GameObject SpawnItem(ItemViewMsg iv)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"Item_{iv.id}_{iv.itemType}";
            go.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
            go.transform.position = iv.position + Vector3.up * 0.4f;
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                collider.isTrigger = true;

            var netObj = go.AddComponent<NetworkWorldObject>();
            netObj.id = iv.id;
            netObj.kind = "item";
            netObj.active = true;

            if (_mats.TryGetValue(iv.itemType, out var mat))
                go.GetComponent<Renderer>().sharedMaterial = mat;
            else
                go.GetComponent<Renderer>().sharedMaterial = MaterialHelper.MakeEmissive(Color.magenta, 0.5f);

            return go;
        }

        private void OnDestroy()
        {
            foreach (var go in _pool.Values) if (go != null) Destroy(go);
            _pool.Clear();
        }
    }
}
