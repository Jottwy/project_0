using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Gameplay.GridWorld;
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
        private float _nextProxyLogTime;
        private float _nextUpdateLogTime;
        private IPCClient _ipc;

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

        /// Comma-joined remote ids for the MPTRACE logs. Called only from the throttled/cold log
        /// paths — it used to be built unconditionally on every state update and thrown away.
        private static string JoinIds(List<RemotePlayerMsg> remotePlayers)
        {
            return string.Join(",", remotePlayers.ConvertAll(r => r.id.ToString()));
        }

        public void UpdateFromWorldState(List<RemotePlayerMsg> remotePlayers)
        {
            if (remotePlayers == null)
                return;

            int selfId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;

            if (Time.unscaledTime >= _nextReceiveLogTime)
            {
                Debug.Log($"[RemotePlayerManager] remote count={remotePlayers.Count}");
                Debug.Log($"MPTRACE step=K event=remote_player_manager_receive self_id={selfId} sender_id=<none> assigned_id=<none> peer_id=<none> endpoint=<unity> peer_count=<unknown> remote_players_count={remotePlayers.Count} remote_players_ids=[{JoinIds(remotePlayers)}]");
                _nextReceiveLogTime = Time.unscaledTime + 2f;
            }

            // Per-proxy diagnostic logs used to run EVERY frame per remote (one string alloc plus
            // a GetComponentsInChildren each). Same 2 s cadence as the receive log above: the
            // traces stay available, the per-frame cost does not.
            bool logProxy = Time.unscaledTime >= _nextProxyLogTime;
            if (logProxy)
                _nextProxyLogTime = Time.unscaledTime + 2f;

            _idsThisFrame.Clear();

            foreach (var rp in remotePlayers)
            {
                if (rp == null)
                    continue;

                if (selfId > 0 && rp.id == selfId)
                {
                    if (logProxy)
                        Debug.Log($"[RemotePlayerManager] ignored local id={rp.id}");
                    continue;
                }

                _idsThisFrame.Add(rp.id);

                // Bug fix (remote proxy floating): the backend relays the player-pivot Y
                // (transform sits PlayerBaseY above the floor — see GridConstants.PlayerBaseY),
                // but the RemotePlayerAvatar root pivot is at the FEET. Drop the pose to the floor
                // by subtracting PlayerBaseY so the feet land on the rendered floor; ProxyGroundingHook
                // then only absorbs the small render/backend residual (< groundSnapMax).
                Vector3 groundedPosition = rp.position;
                groundedPosition.y -= GridConstants.PlayerBaseY;

                if (!_active.TryGetValue(rp.id, out var view))
                {
                    Debug.Log(
                        $"MPTRACE step=PVP event=remote_proxy_create_begin remote_id={rp.id} name={rp.name} " +
                        $"pos=({groundedPosition.x:F2},{groundedPosition.y:F2},{groundedPosition.z:F2})");
                    view = Acquire(rp.id, rp.name);
                    _active[rp.id] = view;
                    if (view.root != null)
                    {
                        view.root.position = groundedPosition;
                        view.root.rotation = Quaternion.Euler(0f, rp.rotation, 0f);
                    }
                    Debug.Log(
                        $"[RemotePlayerManager] spawned id={rp.id}, name={rp.name}, " +
                        $"pos={groundedPosition}");
                    Debug.Log($"MPTRACE step=K event=remote_player_manager_spawn self_id={selfId} sender_id=<none> assigned_id=<none> peer_id={rp.id} endpoint=<unity> peer_count=<unknown> remote_players_count={remotePlayers.Count} remote_players_ids=[{JoinIds(remotePlayers)}]");
                }
                else if (logProxy)
                {
                    int handlerCount = view.root != null
                        ? view.root.GetComponentsInChildren<RemotePvpHitbox>(true).Length
                        : 0;
                    Debug.Log(
                        $"MPTRACE step=PVP event=remote_proxy_update remote_id={rp.id} " +
                        $"root={(view.root != null ? view.root.name : "<null>")} pvp_hitboxes={handlerCount}");
                }

                view.targetPosition = groundedPosition;
                view.targetRotation = rp.rotation;
                view.animationState = string.IsNullOrWhiteSpace(rp.animation) ? "idle" : rp.animation;
                view.crouch = rp.crouch; // ADR-020
                view.pitch = rp.pitch;   // ADR-021
                view.equipment = rp.equipment; // ADR-022 (rp is fresh per parse → no aliasing)
                view.heldItem = rp.heldItem; // ADR-023
                view.hitSeq = rp.hitSeq; // ADR-024
                view.revealed = rp.revealed; // ADR-038
                view.lightOn = rp.lightOn; // ADR-042
                view.fireSeq = rp.fireSeq; // ADR-042
                view.buttons = rp.buttons; // ADR-044
                view.meleeSeq = rp.meleeSeq; // ADR-044
                view.vocalSeq = rp.vocalSeq; // ADR-048
                view.vocalKind = rp.vocalKind; // ADR-048
                view.carryDef = rp.carryDef; // ADR-049
                view.carryCount = rp.carryCount; // ADR-049
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
            }

            // Mismo gate que el espejo del log del backend (ver NetworkInitializer
            // .VerboseBackendLog): son dos Debug.Log POR PEER, y aunque van estrangulados a
            // uno cada 2 s, cada uno arrastra su stack trace y se suman al mismo Editor.log
            // que reventó el editor. Con BACKROOMS_VERBOSE_LOG=1 vuelven.
            if (NetworkInitializer.VerboseBackendLog &&
                _active.Count > 0 && Time.unscaledTime >= _nextUpdateLogTime)
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
            ResetCosmetics(view); // ADR-022..ADR-049: recycled proxy starts clean (root just re-activated above)
            view.lastSeenTime = Time.unscaledTime;

            if (view.root != null)
                view.root.name = $"RemotePlayer_{id}";

            if (view.root != null)
            {
                Debug.Log(
                    $"MPTRACE step=PVP event=remote_proxy_acquire remote_id={id} " +
                    $"root={view.root.name} active={view.root.gameObject.activeSelf}");
                RemotePvpHitbox.Install(view.root.gameObject, id);
            }

            ConfigureNameText(view.nameTag, FormatNameTag(id, playerName));

            return view;
        }

        private void Release(RemotePlayerView view)
        {
            if (view == null)
                return;

            view.id = -1;
            ResetCosmetics(view); // ADR-022..ADR-049
            view.targetPosition = Vector3.zero;
            view.targetRotation = 0f;
            view.yawVelocity = 0f; // [C]

            if (view.nameTag != null)
                view.nameTag.text = string.Empty;

            if (view.root != null)
                view.root.gameObject.SetActive(false);

            _pool.Enqueue(view);
        }

        /// <summary>
        /// Returns the 16 cosmetic relay fields of a pooled view to their defaults. Single shared
        /// point for the two reset sites required by the pose-relay convention (rule #5, reset on
        /// Acquire/Release): Acquire uses it so a proxy recycled out of the pool carries nothing
        /// over, Release uses it so a proxy handed back to the pool stores nothing stale.
        ///
        /// PLAIN FIELD-BY-FIELD ASSIGNMENTS ON PURPOSE — do not collapse this into an object or
        /// struct initializer: a dropped line relays 0 forever.
        ///
        /// Deliberately does NOT touch id, root, nameTag, targetPosition, targetRotation,
        /// yawVelocity or lastSeenTime: those differ between the two call sites and stay assigned
        /// there.
        /// </summary>
        private static void ResetCosmetics(RemotePlayerView view)
        {
            view.animationState = "idle";
            view.crouch = false;
            view.pitch = 0f;
            view.equipment = new int[4]; // ADR-022: no stale clothing on a recycled proxy
            view.heldItem = 0; // ADR-023: no stale held item on a recycled proxy
            view.hitSeq = 0; // ADR-024: no stale hit counter on a recycled proxy (hook re-arms its sentinel)
            view.dead = false; // ADR-028 post-E3: the pooled SetActive(false) is the pool's, not the flag's
            view.revealed = false; // ADR-038: no stale real form on a recycled proxy (hook restores its materials)
            view.vocalSeq = 0; // ADR-048: no stale scream counter (hook re-arms its sentinel)
            view.vocalKind = 0; // ADR-048
            view.lightOn = false; // ADR-042: no stale torch glow on a recycled proxy
            view.fireSeq = 0; // ADR-042: no stale shot counter (hook re-arms its sentinel)
            view.buttons = 0; // ADR-044: a recycled proxy is neither aiming nor reloading
            view.meleeSeq = 0; // ADR-044: no stale swing counter (hook re-arms its sentinel)
            view.carryDef = 0; // ADR-049: a recycled proxy must not inherit the last owner's planks
            view.carryCount = 0; // ADR-049
        }

        private RemotePlayerView CreateView()
        {
            GameObject go;

            if (remotePlayerPrefab != null)
                go = Instantiate(remotePlayerPrefab);
            else
                go = CreateDefaultAvatar();

            DisableLocalOnlyComponents(go);
            ConfigureProxyRendering(go);
            go.transform.SetParent(transform, false);

            var view = new RemotePlayerView
            {
                root = go.transform,
                nameTag = CreateNameTag(go.transform),
                targetPosition = go.transform.position,
                targetRotation = go.transform.eulerAngles.y,
                animationState = "idle"
            };

            return view;
        }

        /// <summary>
        /// A remote proxy must never depend on Unity's culling to be correct.
        ///
        /// The vendor rig ships every SkinnedMeshRenderer with <c>updateWhenOffscreen = false</c>
        /// (12 of them in MTP_PlayerViewer) and its Animator in <c>CullUpdateTransforms</c>. That
        /// combination is fine for a locally-driven character, but a proxy is posed by hooks that
        /// run in LateUpdate — and ProxyGroundingHook shifts the Pelvis ADDITIVELY, on the stated
        /// assumption that "the Animator re-writes Pelvis first, so nothing accumulates". The
        /// moment the Animator is culled it stops re-writing, that assumption breaks, and the
        /// offset accumulates on a bone the renderer bounds are anchored to. The bounds drift out
        /// of the frustum, which keeps the proxy culled, which keeps it drifting: the mesh
        /// disappears and does not come back while you look at it, yet the NameTag (a runtime
        /// MeshRenderer with its own bounds, no skinning) keeps rendering. That is exactly the
        /// reported symptom.
        ///
        /// Cost is a per-frame bounds recompute for a handful of proxies. The vendor itself applies
        /// this same pair as the standard remedy (WieldableAnimatorEditor.cs:90/99). Done in code
        /// rather than baked into the prefab so it also covers the capsule fallback and survives
        /// any future re-bake — and so STP's own prefab is never edited.
        /// </summary>
        private static void ConfigureProxyRendering(GameObject root)
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;

            foreach (var anim in root.GetComponentsInChildren<Animator>(true))
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
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
        public TextMeshPro nameTag;
        public Vector3 targetPosition;
        public float targetRotation;
        // [C] SmoothDampAngle state for the yaw smoothing (degrees/sec); reset on spawn/release.
        public float yawVelocity;
        // ADR-011: scalar transient-action channel, read by ProxyPickupHook, which edge-detects the
        // transition into "pickup" and fires the Pickup trigger. The domain the backend actually
        // emits is exactly "idle" | "walk_slow" | "pickup" (sync.rs::broadcast_player_update).
        // LOCOMOTION DOES NOT GO THROUGH HERE, and there is nothing to drive from it: ADR-013 derives
        // it from velocity into MovementSpeed, and the ADR-012 controller only has the states
        // Movement/Jump/Pickup. Do not re-add a CrossFade switch over this string: it would be dead
        // twice over — no Idle/Walk/Run/Attack state exists to fade to, and three of those four
        // strings are never sent in the first place. The manager that tried it logged
        // "Animator.GotoState: State could not be found" with a full stack trace, per proxy, per frame.
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
        // ADR-038: cosmetic real-form flag (read by ProxyRevealHook). Always false for a real
        // player — only the robapieles (ADR-016) is ever sent with it true.
        public bool revealed;
        // ADR-048: cosmetic vocalisation counter (read by ProxyVocalHook); 0 = never vocalised.
        // Always 0 for a real player — only the robapieles (ADR-016) is ever sent with it set.
        public int vocalSeq;
        // ADR-048: which voice the last bump was. Only meaningful alongside vocalSeq.
        public int vocalKind;
        // ADR-042: cosmetic "held wieldable is lit" flag (read by ProxyLightHook).
        public bool lightOn;
        // ADR-042: cosmetic shot counter (read by ProxyFireAudioHook); 0 = never fired.
        public int fireSeq;
        // ADR-044: cosmetic sustained-state bits (read by ProxyStanceHook) — see RemoteButtons.
        public int buttons;
        // ADR-044: cosmetic melee-swing counter (read by ProxyMeleeHook); 0 = never swung.
        public int meleeSeq;
        // ADR-049: cosmetic carry state (read by ProxyCarryHook) — the CarryableDefinition id on the
        // shoulder, 0 = empty hands, and how many units. A LEVEL: the hook rebuilds when either
        // changes, so unlike the counters above there is no sentinel and no delta.
        public int carryDef;
        public int carryCount;
        public float lastSeenTime;
    }

    public sealed class BillboardNameTag : MonoBehaviour
    {
        private Camera _cam;
        private Transform _camTransform;

        private void LateUpdate()
        {
            // Camera.main is a tag lookup; it ran once per name tag PER FRAME. Cache it and
            // re-resolve only when the cached camera is gone or disabled (scene reload / camera
            // swap), which is the way STP hands over between cameras.
            if (_cam == null || !_cam.isActiveAndEnabled)
            {
                _cam = Camera.main;
                _camTransform = _cam != null ? _cam.transform : null;
            }

            if (_camTransform == null)
                return;

            Vector3 direction = transform.position - _camTransform.position;

            if (direction.sqrMagnitude < 0.0001f)
                return;

            transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }
    }
}
