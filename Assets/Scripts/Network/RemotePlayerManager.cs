using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using TMPro;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    public sealed class RemotePlayerManager : MonoBehaviour
    {
        [Header("Prefab Settings")]
        [Tooltip("If null, a default capsule is created at runtime.")]
        public GameObject remotePlayerPrefab;

        [Header("Interpolation")]
        [Min(0f)] public float positionSmoothing = 22f;

        [Tooltip("DEPRECATED (kept only so existing scenes don't lose a serialized value): the old " +
                 "exponential yaw factor. Yaw now uses SmoothDampAngle with rotationSmoothTime; this " +
                 "field is unused.")]
        [Min(0f)] public float rotationSmoothing = 18f;

        [Tooltip("Yaw smoothing as seconds-to-target (SmoothDampAngle). Lower = snappier.")]
        [Min(0f)] public float rotationSmoothTime = 0.1f;

        [Tooltip("If the yaw error exceeds this (degrees) the proxy SNAPS instead of sweeping " +
                 "(respawn / chunk displacement / instant 180° turns).")]
        [Min(0f)] public float yawSnapThreshold = 120f;

        [Header("Name Tag")]
        [Min(0f)] public float nameTagHeight = 2.2f;
        [Min(0.1f)] public float nameTagFontSize = 3f;

        [Header("Default Avatar")]
        public Color defaultAvatarColor = new Color(0.3f, 0.6f, 1f, 1f);
        public Color remoteMarkerColor = new Color(0.1f, 0.95f, 1f, 1f);
        [Min(0f)] public float missingRemoteGraceSeconds = 3f;

        private readonly Dictionary<int, RemotePlayerView> _active = new Dictionary<int, RemotePlayerView>();
        private readonly Queue<RemotePlayerView> _pool = new Queue<RemotePlayerView>();
        private readonly HashSet<int> _idsThisFrame = new HashSet<int>();
        private readonly List<int> _toRemove = new List<int>();
        private float _nextReceiveLogTime;
        private float _nextUpdateLogTime;
        private IPCClient _ipc;

        private static readonly int AnimIdle = Animator.StringToHash("Idle");
        private static readonly int AnimWalk = Animator.StringToHash("Walk");
        private static readonly int AnimRun = Animator.StringToHash("Run");
        private static readonly int AnimAttack = Animator.StringToHash("Attack");

        public IReadOnlyDictionary<int, RemotePlayerView> ActivePlayers => _active;
        public int ActiveCount => _active.Count;
        public int PoolCount => _pool.Count;

        private void OnDisable()
        {
            if (_ipc != null)
                _ipc.RemoveStateListener(OnWorldState);

            _ipc = null;
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
                UpdateFromWorldState(state.remotePlayers);
        }

        public void UpdateFromWorldState(List<RemotePlayerMsg> remotePlayers)
        {
            if (remotePlayers == null)
                return;

            int selfId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
            var ids = remotePlayers.ConvertAll(r => r.id.ToString());

            if (Time.unscaledTime >= _nextReceiveLogTime)
            {
                Debug.Log($"[RemotePlayerManager] remote count={remotePlayers.Count}");
                Debug.Log($"MPTRACE step=K event=remote_player_manager_receive self_id={selfId} sender_id=<none> assigned_id=<none> peer_id=<none> endpoint=<unity> peer_count=<unknown> remote_players_count={remotePlayers.Count} remote_players_ids=[{string.Join(",", ids)}]");
                _nextReceiveLogTime = Time.unscaledTime + 2f;
            }

            _idsThisFrame.Clear();

            foreach (var rp in remotePlayers)
            {
                if (rp == null)
                    continue;

                if (selfId > 0 && rp.id == selfId)
                {
                    Debug.Log($"[RemotePlayerManager] ignored local id={rp.id}");
                    continue;
                }

                _idsThisFrame.Add(rp.id);

                if (!_active.TryGetValue(rp.id, out var view))
                {
                    view = Acquire(rp.id, rp.name);
                    _active[rp.id] = view;
                    if (view.root != null)
                    {
                        view.root.position = rp.position;
                        view.root.rotation = Quaternion.Euler(0f, rp.rotation, 0f);
                    }
                    Debug.Log(
                        $"[RemotePlayerManager] spawned id={rp.id}, name={rp.name}, " +
                        $"pos={rp.position}");
                    Debug.Log($"MPTRACE step=K event=remote_player_manager_spawn self_id={selfId} sender_id=<none> assigned_id=<none> peer_id={rp.id} endpoint=<unity> peer_count=<unknown> remote_players_count={remotePlayers.Count} remote_players_ids=[{string.Join(",", ids)}]");
                }

                view.targetPosition = rp.position;
                view.targetRotation = rp.rotation;
                view.animationState = string.IsNullOrWhiteSpace(rp.animation) ? "idle" : rp.animation;
                view.crouch = rp.crouch; // ADR-020
                view.pitch = rp.pitch;   // ADR-021
                view.equipment = rp.equipment; // ADR-022 (rp is fresh per parse → no aliasing)
                view.heldItem = rp.heldItem; // ADR-023
                view.hitSeq = rp.hitSeq; // ADR-024
                // ADR-028 post-E3: hide the standing proxy while its owner is dead (the corpse
                // is the visible body); it reappears at the respawn position on dead→false.
                // Change-detected so SetActive only fires on the edge.
                if (view.dead != rp.dead)
                {
                    view.dead = rp.dead;
                    if (view.root != null)
                        view.root.gameObject.SetActive(!rp.dead);
                }
                view.lastSeenTime = Time.unscaledTime;

                string nameTagText = FormatNameTag(rp.id, rp.name);
                if (view.nameTag != null && view.nameTag.text != nameTagText)
                    view.nameTag.text = nameTagText;
            }

            _toRemove.Clear();

            foreach (var kvp in _active)
            {
                if (!_idsThisFrame.Contains(kvp.Key) &&
                    Time.unscaledTime - kvp.Value.lastSeenTime >= missingRemoteGraceSeconds)
                    _toRemove.Add(kvp.Key);
            }

            foreach (var id in _toRemove)
            {
                if (_active.TryGetValue(id, out var view))
                {
                    Debug.Log($"[RemotePlayerManager] despawned id={id} reason=missing_grace_elapsed");
                    Release(view);
                }

                _active.Remove(id);
            }
        }

        private void Update()
        {
            TrySubscribe();

            float dt = Time.deltaTime;

            foreach (var kvp in _active)
            {
                var view = kvp.Value;

                if (view == null || view.root == null)
                    continue;

                float posT = 1f - Mathf.Exp(-Mathf.Max(0f, positionSmoothing) * dt);
                view.root.position = Vector3.Lerp(view.root.position, view.targetPosition, posT);

                // [C] Critically-damped yaw (SmoothDampAngle) — less lag in sustained turns than the
                // old exponential lerp, no overshoot. Snap past a large error so respawn / chunk
                // displacement / instant 180° turns don't sweep the long way around.
                float currentY = view.root.eulerAngles.y;
                float newY;
                if (Mathf.Abs(Mathf.DeltaAngle(currentY, view.targetRotation)) > yawSnapThreshold)
                {
                    newY = view.targetRotation;
                    view.yawVelocity = 0f;
                }
                else
                {
                    newY = Mathf.SmoothDampAngle(currentY, view.targetRotation,
                        ref view.yawVelocity, rotationSmoothTime, Mathf.Infinity, dt);
                }
                view.root.rotation = Quaternion.Euler(0f, newY, 0f);

                ApplyAnimation(view);
            }

            if (_active.Count > 0 && Time.unscaledTime >= _nextUpdateLogTime)
            {
                foreach (var kvp in _active)
                {
                    var view = kvp.Value;
                    if (view != null)
                    {
                        Debug.Log($"[RemotePlayerManager] updated id={kvp.Key} pos={view.targetPosition}");
                        int selfId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
                        Debug.Log($"MPTRACE step=U event=remote_transform_apply self_id={selfId} remote_id={kvp.Key} pos=({view.targetPosition.x:F2},{view.targetPosition.y:F2},{view.targetPosition.z:F2})");
                    }
                }
                _nextUpdateLogTime = Time.unscaledTime + 2f;
            }
        }

        private RemotePlayerView Acquire(int id, string playerName)
        {
            RemotePlayerView view;

            if (_pool.Count > 0)
            {
                view = _pool.Dequeue();

                if (view.root != null)
                    view.root.gameObject.SetActive(true);
                else
                    view = CreateView();
            }
            else
            {
                view = CreateView();
            }

            view.id = id;
            view.targetPosition = view.root != null ? view.root.position : Vector3.zero;
            view.targetRotation = view.root != null ? view.root.eulerAngles.y : 0f;
            view.yawVelocity = 0f; // [C] no carry-over from a recycled view
            view.animationState = "idle";
            view.crouch = false;
            view.pitch = 0f;
            view.equipment = new int[4]; // ADR-022: no stale clothing on a recycled proxy
            view.heldItem = 0; // ADR-023: no stale held item on a recycled proxy
            view.hitSeq = 0; // ADR-024: no stale hit counter on a recycled proxy (hook re-arms its sentinel)
            view.dead = false; // ADR-028 post-E3: recycled proxy starts visible (root just re-activated above)
            view.lastSeenTime = Time.unscaledTime;

            if (view.root != null)
                view.root.name = $"RemotePlayer_{id}";

            ConfigureNameText(view.nameTag, FormatNameTag(id, playerName));

            return view;
        }

        private void Release(RemotePlayerView view)
        {
            if (view == null)
                return;

            view.id = -1;
            view.animationState = "idle";
            view.crouch = false;
            view.pitch = 0f;
            view.equipment = new int[4]; // ADR-022
            view.heldItem = 0; // ADR-023
            view.hitSeq = 0; // ADR-024
            view.dead = false; // ADR-028 post-E3: pooled SetActive(false) is the pool's, not the flag's
            view.targetPosition = Vector3.zero;
            view.targetRotation = 0f;
            view.yawVelocity = 0f; // [C]

            if (view.nameTag != null)
                view.nameTag.text = string.Empty;

            if (view.root != null)
                view.root.gameObject.SetActive(false);

            _pool.Enqueue(view);
        }

        private RemotePlayerView CreateView()
        {
            GameObject go;

            if (remotePlayerPrefab != null)
                go = Instantiate(remotePlayerPrefab);
            else
                go = CreateDefaultAvatar();

            DisableLocalOnlyComponents(go);
            go.transform.SetParent(transform, false);

            var view = new RemotePlayerView
            {
                root = go.transform,
                animator = go.GetComponentInChildren<Animator>(),
                nameTag = CreateNameTag(go.transform),
                targetPosition = go.transform.position,
                targetRotation = go.transform.eulerAngles.y,
                animationState = "idle"
            };

            return view;
        }

        private GameObject CreateDefaultAvatar()
        {
            var root = new GameObject("RemotePlayer");

            var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "Visual";
            capsule.transform.SetParent(root.transform, false);
            capsule.transform.localPosition = new Vector3(0f, 1f, 0f);

            var col = capsule.GetComponent<Collider>();
            if (col != null)
                SafeDestroy(col);

            var renderer = capsule.GetComponent<Renderer>();
            if (renderer != null)
            {
                // Evita Shader.Find directo. Usa el helper robusto contra magenta en build.
                renderer.sharedMaterial = MaterialHelper.MakeLit(defaultAvatarColor);
            }

            return root;
        }

        private GameObject CreateRemoteMarker(Transform parent)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "RemoteMarker";
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = new Vector3(0f, nameTagHeight + 0.35f, 0f);
            marker.transform.localScale = Vector3.one * 0.18f;

            var col = marker.GetComponent<Collider>();
            if (col != null)
                SafeDestroy(col);

            var renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = MaterialHelper.MakeLit(remoteMarkerColor);

            return marker;
        }

        private static void DisableLocalOnlyComponents(GameObject root)
        {
            if (root == null)
                return;

            foreach (var controller in root.GetComponentsInChildren<PlayerController>(true))
                controller.enabled = false;

            foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                camera.enabled = false;

            foreach (var listener in root.GetComponentsInChildren<AudioListener>(true))
                listener.enabled = false;

            foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == null)
                    continue;

                string typeName = behaviour.GetType().Name;
                if (typeName == "PlayerInput")
                    behaviour.enabled = false;
            }
        }

        private TextMeshPro CreateNameTag(Transform parent)
        {
            var tagGo = new GameObject("NameTag");
            tagGo.transform.SetParent(parent, false);
            tagGo.transform.localPosition = new Vector3(0f, nameTagHeight, 0f);

            var tmp = tagGo.AddComponent<TextMeshPro>();
            ConfigureNameText(tmp, string.Empty);

            var rectTransform = tmp.GetComponent<RectTransform>();
            if (rectTransform != null)
                rectTransform.sizeDelta = new Vector2(4f, 1f);

            tagGo.AddComponent<BillboardNameTag>();
            CreateRemoteMarker(parent);

            return tmp;
        }

        private void ConfigureNameText(TMP_Text text, string displayName)
        {
            if (text == null)
                return;

            text.text = displayName;
            text.fontSize = nameTagFontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.color = new Color(0.85f, 1f, 1f, 1f);
            text.outlineColor = Color.black;
            text.outlineWidth = 0.18f;

            // Sustituye TMP_Text.enableWordWrapping obsoleto.
            text.textWrappingMode = TextWrappingModes.NoWrap;

            text.overflowMode = TextOverflowModes.Overflow;
            text.enableAutoSizing = false;
            text.raycastTarget = false;

            if (text is TextMeshPro textMeshPro)
                textMeshPro.sortingOrder = 100;
        }

        private static string FormatNameTag(int id, string playerName)
        {
            string displayName = string.IsNullOrWhiteSpace(playerName) ? $"Player {id}" : playerName;
            return $"{displayName}\nID {id}";
        }

        private static void ApplyAnimation(RemotePlayerView view)
        {
            if (view == null || view.animator == null)
                return;

            switch (view.animationState)
            {
                case "walk":
                    view.animator.CrossFade(AnimWalk, 0.15f);
                    break;

                case "run":
                    view.animator.CrossFade(AnimRun, 0.15f);
                    break;

                case "attack":
                    view.animator.CrossFade(AnimAttack, 0.15f);
                    break;

                default:
                    view.animator.CrossFade(AnimIdle, 0.15f);
                    break;
            }
        }

        private void OnDestroy()
        {
            OnDisable();

            foreach (var kvp in _active)
            {
                var view = kvp.Value;

                if (view != null && view.root != null)
                    SafeDestroy(view.root.gameObject);
            }

            _active.Clear();

            while (_pool.Count > 0)
            {
                var view = _pool.Dequeue();

                if (view != null && view.root != null)
                    SafeDestroy(view.root.gameObject);
            }

            _idsThisFrame.Clear();
            _toRemove.Clear();
        }

        private static void SafeDestroy(Object obj)
        {
            if (obj == null)
                return;

            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }
    }

    public class RemotePlayerView
    {
        public int id = -1;
        public Transform root;
        public Animator animator;
        public TextMeshPro nameTag;
        public Vector3 targetPosition;
        public float targetRotation;
        // [C] SmoothDampAngle state for the yaw smoothing (degrees/sec); reset on spawn/release.
        public float yawVelocity;
        public string animationState = "idle";
        // ADR-020: cosmetic crouch state for this proxy (read by ProxyCrouchHook).
        public bool crouch;
        // ADR-021: cosmetic camera pitch in degrees (read by ProxyPitchHook).
        public float pitch;
        // ADR-022: cosmetic worn clothing item IDs [Head, Torso, Legs, Feet] (read by ProxyClothingHook).
        public int[] equipment = new int[4];
        // ADR-023: cosmetic held item ID (read by ProxyHeldItemHook); 0 = empty hands.
        public int heldItem;
        // ADR-024: cosmetic hit-reaction counter (read by ProxyHitReactionHook); 0 = never hit.
        public int hitSeq;
        // ADR-028 post-E3: cosmetic dead flag (server-derived on the peer's backend). While true
        // the proxy root is hidden — the peer's corpse (CorpseSpawner) is the visible body.
        public bool dead;
        public float lastSeenTime;
    }

    public sealed class BillboardNameTag : MonoBehaviour
    {
        private void LateUpdate()
        {
            var cam = Camera.main;

            if (cam == null)
                return;

            Vector3 direction = transform.position - cam.transform.position;

            if (direction.sqrMagnitude < 0.0001f)
                return;

            transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }
    }
}
