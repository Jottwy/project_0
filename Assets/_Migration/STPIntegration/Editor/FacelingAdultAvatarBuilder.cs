#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// ADR-094 — builds the adult faceling's REMOTE PROXY prefab by cloning the already-baked
    /// <c>RemotePlayerAvatar</c> (RemoteAvatarPrefabBuilder's own output) instead of re-deriving
    /// colliders, hit-reaction, locomotion and grounding from the vendor rig a second time. A
    /// faceling never crouches, holds an item, wears clothing, melees or sprays (ADR-094: it only
    /// ever perceives "alguien cerca" / "me han pegado") — every hook the clone inherits for those
    /// stays wired but permanently idle, which is cheaper and lower-risk than re-deriving a second
    /// 20-step build for a species that never drives most of it.
    ///
    /// UNLIKE the robapieles' "RealForm" (toggled by <c>revealed</c>), this body is ALWAYS active
    /// and the vendor's own human mesh is disabled for good — a faceling never disguises, so there
    /// is no state to toggle (ADR-094: "los facelings no se disfrazan, así que lo dicen siempre").
    /// </summary>
    public static class FacelingAdultAvatarBuilder
    {
        private const string BasePrefabPath =
            "Assets/_Migration/STPIntegration/Resources/RemotePlayerAvatar.prefab";
        // Under Resources (not the plain Facelings/ folder FacelingRealFormBuilder uses) because
        // RemoteAvatarProvider finds this one via Resources.Load at runtime — the baked BODY does
        // not need that (it is only ever loaded by direct asset path, at bake time).
        private const string OutputDir = "Assets/_Migration/STPIntegration/Resources/Facelings";
        public const string OutputPath = OutputDir + "/FacelingAdultAvatar.prefab";
        private const string BodyChildName = "FacelingBody";

        [MenuItem("Backrooms/Facelings/Build Adult Avatar Prefab")]
        public static void Build()
        {
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BasePrefabPath);
            if (basePrefab == null)
            {
                Debug.LogError($"[FacelingAdultAvatarBuilder] Base prefab not found: '{BasePrefabPath}'. " +
                    "Run 'Backrooms ▸ Build Remote Avatar Prefab' first.");
                return;
            }

            var bodyPrefab = FacelingRealFormBuilder.BuildOrGet();
            if (bodyPrefab == null)
            {
                Debug.LogError("[FacelingAdultAvatarBuilder] No faceling body built — " +
                    "see FacelingRealFormBuilder's own errors above.");
                return;
            }

            if (!Directory.Exists(OutputDir))
            {
                Directory.CreateDirectory(OutputDir);
                AssetDatabase.Refresh();
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                HideDefaultBody(instance);
                NestFacelingBody(instance, bodyPrefab);
                DisableRevealHook(instance);

                PrefabUtility.SaveAsPrefabAsset(instance, OutputPath, out bool ok);
                if (ok)
                {
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[FacelingAdultAvatarBuilder] Saved '{OutputPath}'. " +
                        "RemoteAvatarProvider will Resources.Load it at runtime for species==1 peers.");
                }
                else
                {
                    Debug.LogError("[FacelingAdultAvatarBuilder] SaveAsPrefabAsset failed.");
                }
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        // The vendor mesh stays off — a faceling shows ONLY its own body. Matched by renderer, not
        // by name, so this does not depend on knowing the vendor's exact hierarchy. Called BEFORE
        // `NestFacelingBody`, so on a re-run this also hides a stale FacelingBody from a previous
        // bake — harmless, since `NestFacelingBody` destroys and recreates it active right after.
        private static void HideDefaultBody(GameObject root)
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.gameObject.SetActive(false);
        }

        private static void NestFacelingBody(GameObject root, GameObject bodyPrefab)
        {
            var existing = root.transform.Find(BodyChildName);
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            var body = (GameObject)PrefabUtility.InstantiatePrefab(bodyPrefab, root.transform);
            body.name = BodyChildName;
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            body.SetActive(true);

            // Same T-pose defence RemoteAvatarPrefabBuilder.WireRealForm needed: re-stamp the
            // controller on the INSTANCE after nesting, in case instancing lost the runtime binding.
            var bodyAnimator = body.GetComponentInChildren<Animator>(true);
            if (bodyAnimator != null)
            {
                var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                    ProxyAnimatorControllerBuilder.OutputPath);
                if (controller != null)
                {
                    bodyAnimator.runtimeAnimatorController = controller;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(bodyAnimator);
                }
                else
                {
                    Debug.LogError("[FacelingAdultAvatarBuilder] No proxy controller found — " +
                        "the faceling WILL T-pose.");
                }
            }
        }

        // Not needed (a faceling never disguises) and left enabled it would look for a
        // "_realFormBody" this prefab never wires — disabled rather than removed, in case a later
        // slice wants a reveal-shaped toggle back.
        private static void DisableRevealHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyRevealHook>();
            if (hook != null)
                hook.enabled = false;
        }
    }
}
#endif
