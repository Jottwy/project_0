using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Phase 6.7 — minimal debug consumer for WorldState.vertical_debug_markers.
    /// Draws each backend virtual vertical node as a translucent, collider-free
    /// cube tinted per kind. Render-as-debug only: no collision, no traversal,
    /// no gameplay interaction. Markers are created/reused by id and removed
    /// when the backend stops sending them.
    /// </summary>
    public sealed class VerticalDebugMarkerRenderer : MonoBehaviour
    {
        [Tooltip("Master toggle. Disable to hide all vertical debug boxes.")]
        public bool showMarkers = true;

        [Range(0.05f, 1f)] public float markerAlpha = 0.3f;

        private readonly Dictionary<uint, MarkerView> _active = new Dictionary<uint, MarkerView>();
        private readonly HashSet<uint> _idsThisFrame = new HashSet<uint>();
        private readonly List<uint> _toRemove = new List<uint>();
        private readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>();
        private Transform _root;
        private IPCClient _ipc;

        private sealed class MarkerView
        {
            public GameObject go;
            public string kind;
        }

        private void OnDisable()
        {
            if (_ipc != null)
                _ipc.RemoveStateListener(OnWorldState);

            _ipc = null;
        }

        private void Update()
        {
            TrySubscribe();

            if (_root != null && _root.gameObject.activeSelf != showMarkers)
                _root.gameObject.SetActive(showMarkers);
        }

        private void TrySubscribe()
        {
            if (_ipc != null || !IPCClient.TryGetInstance(out var ipc))
                return;

            _ipc = ipc;
            _ipc.AddStateListener(OnWorldState);
        }

        private void OnWorldState(WorldStateMsg state)
        {
            if (state != null)
                ApplyMarkers(state.verticalDebugMarkers);
        }

        public void ApplyMarkers(List<VerticalDebugMarkerMsg> markers)
        {
            int created = 0, updated = 0;
            _idsThisFrame.Clear();

            if (markers != null)
            {
                foreach (var marker in markers)
                {
                    if (marker == null || !_idsThisFrame.Add(marker.id))
                        continue;

                    if (_active.TryGetValue(marker.id, out var view) && view.kind != marker.kind)
                    {
                        // Kind changed (should not happen for a fixed seed) — rebuild.
                        Destroy(view.go);
                        _active.Remove(marker.id);
                        view = null;
                    }

                    if (view == null || view.go == null)
                    {
                        view = new MarkerView { go = CreateMarkerObject(marker), kind = marker.kind };
                        _active[marker.id] = view;
                        created++;
                    }
                    else
                    {
                        updated++;
                    }

                    ApplyTransform(view.go.transform, marker);
                }
            }

            _toRemove.Clear();
            foreach (var pair in _active)
            {
                if (!_idsThisFrame.Contains(pair.Key))
                    _toRemove.Add(pair.Key);
            }

            foreach (uint id in _toRemove)
            {
                if (_active.TryGetValue(id, out var view) && view.go != null)
                    Destroy(view.go);
                _active.Remove(id);
            }

            if (created > 0 || _toRemove.Count > 0)
            {
                Debug.Log(
                    $"[VerticalDebugMarkerRenderer] markers={_idsThisFrame.Count} " +
                    $"created={created} updated={updated} removed={_toRemove.Count}");
            }
        }

        private GameObject CreateMarkerObject(VerticalDebugMarkerMsg marker)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"VerticalDebugMarker_{marker.kind}_{marker.id}";
            go.transform.SetParent(GetRoot(), worldPositionStays: true);

            // Debug-only: never collide, never interact.
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = GetMaterial(marker.kind);
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return go;
        }

        private static void ApplyTransform(Transform t, VerticalDebugMarkerMsg marker)
        {
            Vector3 size = marker.worldMax - marker.worldMin;
            size.x = Mathf.Max(0.01f, size.x);
            size.y = Mathf.Max(0.01f, size.y);
            size.z = Mathf.Max(0.01f, size.z);
            t.position = (marker.worldMin + marker.worldMax) * 0.5f;
            t.localScale = size;
        }

        private Transform GetRoot()
        {
            if (_root == null)
            {
                var rootGo = new GameObject("VerticalDebugMarkers");
                rootGo.transform.SetParent(transform, worldPositionStays: false);
                _root = rootGo.transform;
            }

            return _root;
        }

        private Material GetMaterial(string kind)
        {
            string key = kind ?? "";
            if (_materials.TryGetValue(key, out var mat) && mat != null)
                return mat;

            Color color = ColorForKind(key);
            color.a = markerAlpha;
            mat = MaterialHelper.MakeTransparent(color);
            _materials[key] = mat;
            return mat;
        }

        private static Color ColorForKind(string kind)
        {
            switch (kind)
            {
                case "stair": return new Color(0.2f, 0.9f, 0.3f);   // green
                case "ramp": return new Color(0.95f, 0.85f, 0.2f);  // yellow
                case "shaft": return new Color(0.95f, 0.25f, 0.2f); // red
                case "atrium": return new Color(0.2f, 0.7f, 0.95f); // cyan
                case "sealed_upper": return new Color(0.8f, 0.8f, 0.9f); // pale grey-blue
                default: return new Color(0.9f, 0.4f, 0.9f);        // magenta = unknown
            }
        }
    }
}
