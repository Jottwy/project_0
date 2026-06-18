#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// Builds <c>Resources/RemotePlayerAvatar.prefab</c> as a VARIANT of the vendor
    /// MTP_PlayerViewer, with gameplay-only components disabled so it is safe to spawn as a
    /// remote avatar. The vendor prefab is never modified — a variant only stores overrides.
    /// Idempotent / re-runnable.
    ///
    /// KEEP enabled: SkinnedMeshRenderer, Animator, CapsuleCollider, CharacterAnimator,
    /// CharacterClothing (the visible/animated mesh).
    /// DISABLE (not delete): Player, HealthManager, Inventory, CharacterDeathHandler,
    /// CharacterDamageHandler, CharacterAudioPlayer (local gameplay logic).
    /// ADD: ProxyLocomotionFeeder (drives MovementSpeed) + ProxyJumpFeeder (fires Jump from
    /// vertical velocity) + ProxyPickupHook (fires Pickup from rp.animation=="pickup") on the root.
    /// REPLACE: the inherited vendor AnimatorOverrideController with the custom _Migration
    /// ProxyLocomotionController (see ProxyAnimatorControllerBuilder), built fresh each rebuild.
    ///
    /// Output lives under Resources so RemoteAvatarProvider can Resources.Load it at runtime
    /// (the vendor prefab is not under a Resources folder, and a self-bootstrapped component
    /// cannot hold an inspector reference).
    /// </summary>
    public static class RemoteAvatarPrefabBuilder
    {
        private const string BasePrefabPath =
            "Assets/PolymindGames/STP/Prefabs/Characters/MTP_PlayerViewer.prefab";
        private const string OutputDir = "Assets/_Migration/STPIntegration/Resources";
        private const string OutputPath = OutputDir + "/RemotePlayerAvatar.prefab";

        // Matched by Component.GetType().Name, so no compile-time dependency on STP types.
        private static readonly HashSet<string> DisableByTypeName = new HashSet<string>
        {
            "Player",
            "HealthManager",
            "Inventory",
            "CharacterDeathHandler",
            "CharacterDamageHandler",
            "CharacterAudioPlayer",
        };

        [MenuItem("Backrooms/Build Remote Avatar Prefab")]
        public static void Build()
        {
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BasePrefabPath);
            if (basePrefab == null)
            {
                Debug.LogError($"[RemoteAvatarPrefabBuilder] Base prefab not found: {BasePrefabPath}");
                return;
            }

            if (!Directory.Exists(OutputDir))
            {
                Directory.CreateDirectory(OutputDir);
                AssetDatabase.Refresh();
            }

            // Instantiating the base prefab and saving the instance produces a VARIANT.
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                var disabled = new List<string>();
                var kept = new List<string>();
                foreach (var comp in instance.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null)
                        continue;
                    string typeName = comp.GetType().Name;
                    if (comp is Behaviour b && DisableByTypeName.Contains(typeName))
                    {
                        b.enabled = false;
                        disabled.Add(typeName);
                    }
                }

                WireAnimatorController(instance);
                WireLocomotionFeeder(instance);
                WireJumpFeeder(instance);
                WirePickupHook(instance);

                PrefabUtility.SaveAsPrefabAsset(instance, OutputPath, out bool ok);
                if (ok)
                {
                    AssetDatabase.SaveAssets();
                    EnsureControllerBound();
                    Debug.Log($"[RemoteAvatarPrefabBuilder] Saved variant '{OutputPath}'. Disabled: " +
                              (disabled.Count > 0 ? string.Join(", ", disabled) : "<none of the listed types found>") +
                              ". RemoteAvatarProvider will Resources.Load it at runtime.");
                }
                else
                {
                    Debug.LogError("[RemoteAvatarPrefabBuilder] SaveAsPrefabAsset failed.");
                }
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        // Replace the inherited vendor AnimatorOverrideController with the custom _Migration
        // controller (built fresh here so a rebuild always re-points the variant at the current
        // GUID). Keeps ProxyLocomotionFeeder's SetFloat("MovementSpeed") path intact.
        private static void WireAnimatorController(GameObject root)
        {
            var controller = ProxyAnimatorControllerBuilder.BuildOrRebuild();
            if (controller == null)
            {
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] Proxy controller not built; " +
                    "keeping the inherited vendor controller.");
                return;
            }

            var animator = root.GetComponent<Animator>();
            if (animator == null)
            {
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] No Animator on the variant root; " +
                    "cannot assign the proxy controller.");
                return;
            }

            animator.runtimeAnimatorController = controller;
            // The runtimeAnimatorController setter on an inherited (variant) Animator is NOT
            // auto-recorded as a prefab override by SaveAsPrefabAsset (unlike the m_Enabled edits
            // above), so the variant kept binding the vendor controller. Force-record it.
            PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
        }

        // Guaranteed binding (BUG ① fix): operate on the SAVED variant asset directly, immune to
        // the instance-override quirk and the controller's GUID churn. Loads the just-saved prefab,
        // points its Animator at the custom controller, and re-saves only if it wasn't already bound.
        private static void EnsureControllerBound()
        {
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                ProxyAnimatorControllerBuilder.OutputPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
            if (controller == null || prefab == null)
                return;

            var animator = prefab.GetComponentInChildren<Animator>(true);
            if (animator == null)
                return;

            if (animator.runtimeAnimatorController != controller)
            {
                animator.runtimeAnimatorController = controller;
                PrefabUtility.SavePrefabAsset(prefab);
                Debug.Log("[RemoteAvatarPrefabBuilder] Re-bound the variant Animator to the custom " +
                          "ProxyLocomotionController on the saved asset (the instance assignment had not stuck).");
            }
        }

        // Wire the proxy locomotion feeder onto the root (the GameObject that holds the
        // Animator) so every rebuild ships it pre-configured — no manual re-add after a
        // regenerate. Idempotent: never adds a second one.
        private static void WireLocomotionFeeder(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null)
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] No Animator on the variant root; " +
                    "ProxyLocomotionFeeder will resolve it at runtime (or stay idle if absent).");

            var feeder = root.GetComponent<ProxyLocomotionFeeder>();
            if (feeder == null)
                feeder = root.AddComponent<ProxyLocomotionFeeder>();

            // Set calibrated starting values + the Animator reference via SerializedObject, so the
            // feeder's serialized fields stay private (no runtime API added just for this build).
            var so = new SerializedObject(feeder);
            SetFeederFloat(so, "_deadzoneSpeed", 0.1f);
            SetFeederFloat(so, "_walkSpeed", 1.5f);
            SetFeederFloat(so, "_runSpeed", 4.5f);
            SetFeederFloat(so, "_smoothTime", 0.12f);
            SetFeederFloat(so, "_teleportDistance", 2.0f);
            var animProp = so.FindProperty("_animator");
            if (animProp != null)
                animProp.objectReferenceValue = animator;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Mirror of WireLocomotionFeeder for the full-body jump feeder. Idempotent; ships the
        // calibrated defaults so a rebuild keeps controller + BOTH feeders.
        private static void WireJumpFeeder(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null)
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] No Animator on the variant root; " +
                    "ProxyJumpFeeder will resolve it at runtime (or stay inert if absent).");

            var feeder = root.GetComponent<ProxyJumpFeeder>();
            if (feeder == null)
                feeder = root.AddComponent<ProxyJumpFeeder>();

            var so = new SerializedObject(feeder);
            SetFeederFloat(so, "_jumpVelocityUp", 2.5f);
            SetFeederFloat(so, "_landVelocity", -0.5f);
            SetFeederFloat(so, "_verticalTeleportDistance", 3.0f);
            var animProp = so.FindProperty("_animator");
            if (animProp != null)
                animProp.objectReferenceValue = animator;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Mirror of the Wire* feeders for the pickup hook. Also bakes GestureDuration (pickup clip ÷
        // speed) so LocalPickupInputLock reads the real gesture length off this Resources prefab.
        private static void WirePickupHook(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null)
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] No Animator on the variant root; " +
                    "ProxyPickupHook will resolve it at runtime (or stay inert if absent).");

            var hook = root.GetComponent<ProxyPickupHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyPickupHook>();

            var so = new SerializedObject(hook);
            var animProp = so.FindProperty("_animator");
            if (animProp != null)
                animProp.objectReferenceValue = animator;
            SetFeederFloat(so, "_gestureDuration", ProxyAnimatorControllerBuilder.GetPickupGestureDuration());
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetFeederFloat(SerializedObject so, string field, float value)
        {
            var prop = so.FindProperty(field);
            if (prop != null)
                prop.floatValue = value;
            else
                Debug.LogWarning($"[RemoteAvatarPrefabBuilder] ProxyLocomotionFeeder field '{field}' not found; skipped.");
        }
    }
}
#endif
