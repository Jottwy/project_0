using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    public sealed class EntityRenderer : MonoBehaviour
    {
        [Header("Interpolation")]
        [Tooltip("Lerp speed for smoothing 10hz updates to 60fps.")]
        public float lerpSpeed = 12f;

        private readonly Dictionary<uint, EntityVisual> _pool = new Dictionary<uint, EntityVisual>();
        private Material _lurkerMat, _crawlerMat, _shadowMat, _deadMat;
        private Material _hpBgMat, _hpFillMat, _eyeMat;

        private sealed class EntityVisual
        {
            public GameObject go;
            public Vector3 targetPos;
            public float targetRot;
            public string lastState;
            public string entityType;
            public Renderer bodyRenderer;
            public Transform healthBar;
            public Transform healthFill;
        }

        private void Start()
        {
            _lurkerMat = MaterialHelper.MakeLit(new Color(0.55f, 0.35f, 0.20f));
            _crawlerMat = MaterialHelper.MakeLit(new Color(0.25f, 0.50f, 0.20f));
            _shadowMat = MaterialHelper.MakeTransparent(new Color(0.15f, 0.05f, 0.25f, 0.3f));
            _deadMat = MaterialHelper.MakeLit(new Color(0.3f, 0.3f, 0.3f));
            _hpBgMat = MaterialHelper.MakeLit(new Color(0.15f, 0.15f, 0.15f));
            _hpFillMat = MaterialHelper.MakeLit(new Color(0.85f, 0.15f, 0.15f));
            _eyeMat = MaterialHelper.MakeEmissive(new Color(1f, 0.9f, 0.7f), 3f);
        }

        private void LateUpdate()
        {
            if (!IPCClient.TryGetInstance(out var ipc))
                return;

            var state = ipc.LatestState;
            if (state == null) return;

            var alive = new HashSet<uint>();

            foreach (var ev in state.visibleEntities)
            {
                alive.Add(ev.id);

                if (!_pool.TryGetValue(ev.id, out var vis))
                {
                    vis = SpawnVisual(ev);
                    _pool[ev.id] = vis;
                }

                vis.targetPos = ev.position;
                vis.targetRot = ev.rotation;

                float t = 1f - Mathf.Exp(-lerpSpeed * Time.deltaTime);
                vis.go.transform.position = Vector3.Lerp(
                    vis.go.transform.position, vis.targetPos, t);
                vis.go.transform.rotation = Quaternion.Slerp(
                    vis.go.transform.rotation,
                    Quaternion.Euler(0f, vis.targetRot, 0f), t);

                UpdateVisualState(vis, ev);
            }

            var stale = new List<uint>();
            foreach (var kv in _pool)
            {
                if (!alive.Contains(kv.Key))
                {
                    Destroy(kv.Value.go);
                    stale.Add(kv.Key);
                }
            }
            foreach (uint k in stale) _pool.Remove(k);
        }

        private EntityVisual SpawnVisual(EntityViewMsg ev)
        {
            var root = new GameObject($"Entity_{ev.id}_{ev.entityType}");
            root.transform.position = ev.position;

            // Body capsule — scaled by entity type
            float bodyScale = ev.entityType switch
            {
                "crawler" => 0.7f,
                "shadow" => 1.2f,
                _ => 1.0f
            };

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = Vector3.one * bodyScale;
            body.transform.localPosition = new Vector3(0f, bodyScale, 0f);
            body.GetComponent<Renderer>().sharedMaterial = GetMaterialForType(ev.entityType);
            Destroy(body.GetComponent<Collider>());

            // Health bar (billboard)
            float barY = bodyScale * 2.2f + 0.3f;
            var hpRoot = new GameObject("HealthBar");
            hpRoot.transform.SetParent(root.transform, false);
            hpRoot.transform.localPosition = new Vector3(0f, barY, 0f);

            var hpBg = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hpBg.name = "HPBg";
            hpBg.transform.SetParent(hpRoot.transform, false);
            hpBg.transform.localScale = new Vector3(1.2f, 0.14f, 0.05f);
            hpBg.GetComponent<Renderer>().sharedMaterial = _hpBgMat;
            Destroy(hpBg.GetComponent<Collider>());

            var hpFill = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hpFill.name = "HPFill";
            hpFill.transform.SetParent(hpRoot.transform, false);
            hpFill.transform.localScale = new Vector3(1.2f, 0.12f, 0.06f);
            hpFill.GetComponent<Renderer>().sharedMaterial = _hpFillMat;
            Destroy(hpFill.GetComponent<Collider>());

            // Eyes (Lurker and Crawler only)
            if (ev.entityType != "shadow")
            {
                CreateEye(body.transform, new Vector3(-0.15f, 0.35f, 0.45f), bodyScale);
                CreateEye(body.transform, new Vector3(0.15f, 0.35f, 0.45f), bodyScale);
            }

            return new EntityVisual
            {
                go = root,
                targetPos = ev.position,
                targetRot = ev.rotation,
                lastState = ev.state,
                entityType = ev.entityType,
                bodyRenderer = body.GetComponent<Renderer>(),
                healthBar = hpRoot.transform,
                healthFill = hpFill.transform,
            };
        }

        private void CreateEye(Transform parent, Vector3 localPos, float scale)
        {
            var eye = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            eye.name = "Eye";
            eye.transform.SetParent(parent, false);
            eye.transform.localPosition = localPos;
            eye.transform.localScale = Vector3.one * (0.12f / scale);
            eye.GetComponent<Renderer>().sharedMaterial = _eyeMat;
            Destroy(eye.GetComponent<Collider>());
        }

        private void UpdateVisualState(EntityVisual vis, EntityViewMsg ev)
        {
            // Health bar fill
            float hp = Mathf.Clamp01(ev.healthPct);
            vis.healthFill.localScale = new Vector3(1.2f * hp, 0.12f, 0.06f);
            vis.healthFill.localPosition = new Vector3((hp - 1f) * 0.6f, 0f, 0f);

            // Billboard health bar toward camera
            if (Camera.main != null)
                vis.healthBar.LookAt(vis.healthBar.position + Camera.main.transform.forward);

            // Dead state
            if (ev.state == "dead")
            {
                vis.bodyRenderer.sharedMaterial = _deadMat;
                vis.go.transform.localScale = new Vector3(1f, 0.3f, 1f); // flatten
            }
            else
            {
                if (vis.lastState == "dead")
                {
                    vis.bodyRenderer.sharedMaterial = GetMaterialForType(vis.entityType);
                    vis.go.transform.localScale = Vector3.one;
                }
            }
            vis.lastState = ev.state;

            // Shadow type: fade in at close range
            if (vis.entityType == "shadow" && ev.state != "dead" && Camera.main != null)
            {
                float dist = Vector3.Distance(Camera.main.transform.position, vis.go.transform.position);
                float alpha = dist < 8f ? Mathf.InverseLerp(8f, 3f, dist) : 0f;
                // Update material alpha
                var mat = vis.bodyRenderer.material;
                var c = mat.color;
                c.a = Mathf.Lerp(0.05f, 0.85f, alpha);
                mat.color = c;
                vis.bodyRenderer.material = mat;
            }
        }

        private Material GetMaterialForType(string type) => type switch
        {
            "crawler" => _crawlerMat,
            "shadow" => _shadowMat,
            _ => _lurkerMat
        };

        private void OnDestroy()
        {
            foreach (var v in _pool.Values) if (v.go != null) Destroy(v.go);
            _pool.Clear();
        }
    }
}
