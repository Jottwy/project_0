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
        [Min(0f)] public float positionSmoothing = 12f;
        [Min(0f)] public float rotationSmoothing = 10f;

        [Header("Name Tag")]
        [Min(0f)] public float nameTagHeight = 2.2f;
        [Min(0.1f)] public float nameTagFontSize = 3f;

        [Header("Default Avatar")]
        public Color defaultAvatarColor = new Color(0.3f, 0.6f, 1f, 1f);

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

            if (remotePlayers.Count > 0 && Time.unscaledTime >= _nextReceiveLogTime)
            {
                Debug.Log($"[RemotePlayerManager] Remote player received: count={remotePlayers.Count}");
                _nextReceiveLogTime = Time.unscaledTime + 2f;
            }

            _idsThisFrame.Clear();

            foreach (var rp in remotePlayers)
            {
                if (rp == null)
                    continue;

                _idsThisFrame.Add(rp.id);

                if (!_active.TryGetValue(rp.id, out var view))
                {
                    view = Acquire(rp.id, rp.name);
                    _active[rp.id] = view;
                    Debug.Log(
                        $"[RemotePlayerManager] Remote player spawned: id={rp.id}, name={rp.name}, " +
                        $"pos={rp.position}");
                }

                view.targetPosition = rp.position;
                view.targetRotation = rp.rotation;
                view.animationState = string.IsNullOrWhiteSpace(rp.animation) ? "idle" : rp.animation;

                if (view.nameTag != null && view.nameTag.text != rp.name)
                    view.nameTag.text = string.IsNullOrWhiteSpace(rp.name) ? $"Player {rp.id}" : rp.name;
            }

            _toRemove.Clear();

            foreach (var kvp in _active)
            {
                if (!_idsThisFrame.Contains(kvp.Key))
                    _toRemove.Add(kvp.Key);
            }

            foreach (var id in _toRemove)
            {
                if (_active.TryGetValue(id, out var view))
                    Release(view);

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

                float rotT = 1f - Mathf.Exp(-Mathf.Max(0f, rotationSmoothing) * dt);
                float currentY = view.root.eulerAngles.y;
                float newY = Mathf.LerpAngle(currentY, view.targetRotation, rotT);
                view.root.rotation = Quaternion.Euler(0f, newY, 0f);

                ApplyAnimation(view);
            }

            if (_active.Count > 0 && Time.unscaledTime >= _nextUpdateLogTime)
            {
                Debug.Log($"[RemotePlayerManager] Remote player updated: active={_active.Count}");
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
            view.animationState = "idle";

            if (view.root != null)
                view.root.name = $"RemotePlayer_{id}";

            ConfigureNameText(view.nameTag, string.IsNullOrWhiteSpace(playerName) ? $"Player {id}" : playerName);

            return view;
        }

        private void Release(RemotePlayerView view)
        {
            if (view == null)
                return;

            view.id = -1;
            view.animationState = "idle";
            view.targetPosition = Vector3.zero;
            view.targetRotation = 0f;

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

            return tmp;
        }

        private void ConfigureNameText(TMP_Text text, string displayName)
        {
            if (text == null)
                return;

            text.text = displayName;
            text.fontSize = nameTagFontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;

            // Sustituye TMP_Text.enableWordWrapping obsoleto.
            text.textWrappingMode = TextWrappingModes.NoWrap;

            text.overflowMode = TextOverflowModes.Overflow;
            text.enableAutoSizing = false;
            text.raycastTarget = false;

            if (text is TextMeshPro textMeshPro)
                textMeshPro.sortingOrder = 100;
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
        public string animationState = "idle";
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
