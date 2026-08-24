using BackroomsSurvival.Gameplay; // MaterialHelper
using BackroomsSurvival.Net;      // RemotePlayerManager
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Supplies <see cref="RemotePlayerManager"/> with the avatar prefab to spawn for remote
    /// peers.
    ///
    /// Preferred avatar: the real STP humanoid — a gameplay-disabled VARIANT of the vendor
    /// MTP_PlayerViewer, generated once into Resources via "Backrooms ▸ Build Remote Avatar
    /// Prefab" (see RemoteAvatarPrefabBuilder). If that asset is absent it falls back to a
    /// bright unlit capsule+head, so remotes are always visible (no regression).
    ///
    /// TIMING (fix for "spawn at origin"): the prefab MUST be on RemotePlayerManager BEFORE
    /// its first world_state arrives, or the first avatar is the default capsule snapped to an
    /// early pose ([RemotePlayerManager.cs:103]). Two cooperating mechanisms ensure this:
    ///   * RemotePlayerManagerGuarantor calls <see cref="EnsureTemplate"/> in the SAME frame it
    ///     creates the manager (deterministic, before the manager's first Update/subscription);
    ///   * this component's Update also assigns it, as a fallback for a manager created by
    ///     GameBootstrap.
    /// Self-bootstraps; fully removable (delete the file -> capsule fallback returns).
    /// </summary>
    public sealed class RemoteAvatarProvider : MonoBehaviour
    {
        /// <summary>Resources path (no extension) of the MTP_PlayerViewer-based avatar prefab.</summary>
        public const string ResourcePath = "RemotePlayerAvatar";

        /// <summary>ADR-094: Resources path of the adult faceling's own proxy prefab. Absent until
        /// "Backrooms ▸ Facelings ▸ Build Adult Avatar Prefab" has been run — species==1 peers fall
        /// back to <see cref="ResourcePath"/> until then (RemotePlayerManager.CreateView). Lives in
        /// a "Facelings" subfolder of the SAME Resources dir as the default avatar (still has to be
        /// under a literal "Resources" folder for Resources.Load to find it at all).</summary>
        public const string FacelingAdultResourcePath = "Facelings/FacelingAdultAvatar";

        // Distinct, bright colour; unlit so the dim Backrooms can never hide the fallback.
        private static readonly Color AvatarColor = new Color(0.20f, 0.85f, 1f, 1f);

        private static RemoteAvatarProvider _instance;
        private static GameObject _sharedTemplate;
        private static GameObject _sharedFacelingAdultTemplate;
        private static bool _facelingAdultLogged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[RemoteAvatarProvider]");
            _instance = go.AddComponent<RemoteAvatarProvider>();
            DontDestroyOnLoad(go);
        }

        /// <summary>
        /// Returns the avatar prefab/template: the real STP mesh from Resources if present,
        /// otherwise the capsule fallback. Idempotent and callable before this component's own
        /// bootstrap (used by RemotePlayerManagerGuarantor for same-frame assignment).
        /// </summary>
        public static GameObject EnsureTemplate()
        {
            if (_sharedTemplate != null)
                return _sharedTemplate;

            var prefab = Resources.Load<GameObject>(ResourcePath);
            _sharedTemplate = prefab != null ? prefab : BuildFallbackTemplate();

            Debug.Log(prefab != null
                ? $"[RemoteAvatarProvider] Using STP avatar prefab Resources/{ResourcePath}."
                : "[RemoteAvatarProvider] Resources/RemotePlayerAvatar not found; using capsule fallback. " +
                  "Run 'Backrooms ▸ Build Remote Avatar Prefab' to generate the STP mesh avatar.");

            return _sharedTemplate;
        }

        /// <summary>
        /// ADR-094: the adult faceling's own template, or null if it has not been built yet. UNLIKE
        /// <see cref="EnsureTemplate"/> this has NO capsule fallback — a faceling with no baked body
        /// should read as "a player" (RemotePlayerManager.CreateView's own fallback), not as a
        /// bright cyan capsule, which would look like a bug rather than a pending asset.
        /// </summary>
        public static GameObject EnsureFacelingAdultTemplate()
        {
            if (_sharedFacelingAdultTemplate != null)
                return _sharedFacelingAdultTemplate;

            _sharedFacelingAdultTemplate = Resources.Load<GameObject>(FacelingAdultResourcePath);

            if (!_facelingAdultLogged)
            {
                _facelingAdultLogged = true;
                Debug.Log(_sharedFacelingAdultTemplate != null
                    ? $"[RemoteAvatarProvider] Using faceling adult prefab Resources/{FacelingAdultResourcePath}."
                    : $"[RemoteAvatarProvider] Resources/{FacelingAdultResourcePath} not found; species==1 " +
                      "peers fall back to the default avatar. Run 'Backrooms ▸ Facelings ▸ Build Adult Avatar " +
                      "Prefab' to generate it.");
            }

            return _sharedFacelingAdultTemplate;
        }

        private void Update()
        {
            // Fallback assignment for a manager created outside the guarantor (e.g. GameBootstrap).
            var rpm = FindFirstObjectByType<RemotePlayerManager>();
            if (rpm == null)
                return;

            var template = EnsureTemplate();
            if (rpm.remotePlayerPrefab != template)
                rpm.remotePlayerPrefab = template;

            var facelingTemplate = EnsureFacelingAdultTemplate();
            if (facelingTemplate != null && rpm.facelingAdultPrefab != facelingTemplate)
                rpm.facelingAdultPrefab = facelingTemplate;
        }

        // ─── Capsule fallback (used only when the STP prefab asset is absent) ───

        private static GameObject BuildFallbackTemplate()
        {
            // INACTIVE holder so the template is never rendered itself, yet keeps
            // activeSelf == true (Instantiate preserves activeSelf, so clones activate when
            // RemotePlayerManager reparents them under its active transform).
            var holder = new GameObject("[RemoteAvatarTemplate]");
            holder.SetActive(false);
            DontDestroyOnLoad(holder);

            var root = new GameObject("RemotePlayerAvatar");
            root.transform.SetParent(holder.transform, false);

            var col = root.AddComponent<CapsuleCollider>();
            col.height = 1.9f;
            col.radius = 0.35f;
            col.center = new Vector3(0f, 0.95f, 0f);
            var rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            StripCollider(body);
            SetMaterial(body, MaterialHelper.MakeUnlit(AvatarColor));

            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, 2.05f, 0f);
            head.transform.localScale = Vector3.one * 0.55f;
            StripCollider(head);
            SetMaterial(head, MaterialHelper.MakeUnlit(AvatarColor));

            return root;
        }

        private static void StripCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c != null)
                Destroy(c);
        }

        private static void SetMaterial(GameObject go, Material mat)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null)
                r.sharedMaterial = mat;
        }
    }
}
