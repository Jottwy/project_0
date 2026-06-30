#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using PolymindGames;
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
    /// vertical velocity) + ProxyPickupHook (fires Pickup from rp.animation=="pickup") +
    /// ProxyCrouchHook (drives the Crouched blend from rp.crouch, ADR-020) + ProxyPitchHook
    /// (tilts head/neck/spine from rp.pitch, ADR-021) + ProxyGroundingHook (offsets Hips so the
    /// feet rest on the rendered floor, [D] slice 1) + ProxyClothingHook (drives CharacterClothing
    /// from rp.equipment, ADR-022) + ProxyHeldItemHook (attaches the held wieldable's pickup mesh
    /// to Hand.R from rp.heldItem, with per-category placement + finger grip from a GripPoseSet,
    /// ADR-023) + ProxyHitReactionHook (procedural spine recoil flinch from rp.hitSeq, ADR-024) on
    /// the root. ADR-022 also sets CharacterClothing._attachToCharacter = false so the proxy never
    /// binds to its disabled inventory.
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
                WireControllerBinder(instance);
                WireLocomotionFeeder(instance);
                WireJumpFeeder(instance);
                WirePickupHook(instance);
                WireCrouchHook(instance);
                WirePitchHook(instance);
                WireGroundingHook(instance);
                WireClothingHook(instance);
                WireHeldItemHook(instance);
                WireHitReactionHook(instance);

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

        // BUG 1 fix (T-pose in the player BUILD, not the editor): make the custom controller a HARD build
        // dependency + bind it at runtime. The variant's m_Controller override alone did NOT survive the
        // build (the controller's only reference was that override — outside Resources — so it was stripped
        // / the override didn't apply → Animator fell back to the inherited vendor controller → T-pose).
        // ProxyControllerBinder holds a serialized RuntimeAnimatorController reference (never stripped) and
        // assigns it in Awake (binding independent of the variant override). Editor Play already worked via
        // the override; this adds the build guarantee.
        private static void WireControllerBinder(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                ProxyAnimatorControllerBuilder.OutputPath);
            if (controller == null)
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] Proxy controller asset not found for the " +
                    "runtime binder; a build may T-pose. Did the controller build succeed?");

            var binder = root.GetComponent<ProxyControllerBinder>();
            if (binder == null)
                binder = root.AddComponent<ProxyControllerBinder>();

            var so = new SerializedObject(binder);
            var animProp = so.FindProperty("_animator");
            if (animProp != null)
                animProp.objectReferenceValue = animator;
            var ctrlProp = so.FindProperty("_controller");
            if (ctrlProp != null)
                ctrlProp.objectReferenceValue = controller;
            so.ApplyModifiedPropertiesWithoutUndo();
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

        // Mirror of WirePickupHook for the crouch hook (ADR-020). Sets the Animator reference + the
        // calibrated lerp speed; the hook drives the controller's "Crouched" blend param. Idempotent.
        private static void WireCrouchHook(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null)
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] No Animator on the variant root; " +
                    "ProxyCrouchHook will resolve it at runtime (or stay inert if absent).");

            var hook = root.GetComponent<ProxyCrouchHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyCrouchHook>();

            var so = new SerializedObject(hook);
            var animProp = so.FindProperty("_animator");
            if (animProp != null)
                animProp.objectReferenceValue = animator;
            SetFeederFloat(so, "_lerpSpeed", 10f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Mirror of WireCrouchHook for the pitch hook (ADR-021). The hook resolves the rig bones by
        // name at runtime; here we only bake the calibrated tuning fields. Idempotent.
        private static void WirePitchHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyPitchHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyPitchHook>();

            var so = new SerializedObject(hook);
            SetFeederFloat(so, "_lerpSpeed", 10f);
            SetFeederFloat(so, "_spineLeanThreshold", 45f);
            SetFeederFloat(so, "_maxSpineLean", 30f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Mirror of WirePitchHook for the body-grounding hook ([D] slice 1). The hook resolves the
        // Hips bone by name at runtime; here we only bake the calibrated tuning fields. Idempotent.
        private static void WireGroundingHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyGroundingHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyGroundingHook>();

            var so = new SerializedObject(hook);
            SetFeederFloat(so, "_rayUp", 1.0f);
            SetFeederFloat(so, "_rayDown", 3.0f);
            SetFeederFloat(so, "_groundSnapMax", 0.5f);
            SetFeederFloat(so, "_airborneFade", 1.5f);
            SetFeederFloat(so, "_smoothTime", 0.1f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ADR-022: adds the clothing hook (drives CharacterClothing.SetClothing from the networked
        // equipment IDs) AND sets the proxy's CharacterClothing._attachToCharacter = false so it never
        // tries to bind to the disabled proxy inventory — a null equipment container in
        // AttachToCharacter would NRE silently. Idempotent.
        private static void WireClothingHook(GameObject root)
        {
            if (root.GetComponent<ProxyClothingHook>() == null)
                root.AddComponent<ProxyClothingHook>();

            var clothing = root.GetComponentInChildren<CharacterClothing>(true);
            if (clothing == null)
            {
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] No CharacterClothing under the variant; " +
                    "ProxyClothingHook will stay inert (no wardrobe to drive).");
                return;
            }

            var so = new SerializedObject(clothing);
            var attachProp = so.FindProperty("_attachToCharacter");
            if (attachProp != null)
            {
                attachProp.boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
                // CharacterClothing is inherited from the base MTP_PlayerViewer; force-record the
                // override so SaveAsPrefabAsset persists it on the variant (cf. WireAnimatorController).
                PrefabUtility.RecordPrefabInstancePropertyModifications(clothing);
            }
            else
            {
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] CharacterClothing._attachToCharacter not found; " +
                    "the proxy may attempt an inventory bind (potential NRE).");
            }
        }

        // ADR-023: adds the held-item hook (instantiates the held wieldable's pickup mesh under
        // Hand.R from the networked held item ID) and wires its per-category GripPoseSet (Slice 2:
        // model placement + finger curl by item category). The hook resolves the hand/finger bones
        // by name at runtime; here we bake the bone name + the GripPoseSet reference. Idempotent —
        // an existing GripPoseSet asset is referenced as-is (calibration preserved), not reseeded.
        private static void WireHeldItemHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyHeldItemHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyHeldItemHook>();

            var gripPoses = LoadOrCreateGripPoseSet();

            var so = new SerializedObject(hook);
            var boneProp = so.FindProperty("_handBoneName");
            if (boneProp != null)
                boneProp.stringValue = "Hand.R";
            var gripProp = so.FindProperty("_gripPoses");
            if (gripProp != null)
                gripProp.objectReferenceValue = gripPoses;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Mirror of WirePitchHook for the hit-reaction hook (ADR-024). The hook resolves the spine
        // bones by name at runtime + reads the networked hit counter; here we only bake the
        // calibrated recoil tuning fields. Idempotent.
        private static void WireHitReactionHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyHitReactionHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyHitReactionHook>();

            var so = new SerializedObject(hook);
            SetFeederFloat(so, "_magnitude", 18f);
            SetFeederFloat(so, "_recoverTime", 0.3f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ADR-023 Slice 2: the per-category grip config lives as a ScriptableObject (live-editable
        // during Play). Created once with sensible default buckets (Melee/Firearms/Tools + fallback);
        // re-bakes reuse the existing asset so play-test calibration is never overwritten.
        private const string GripPoseSetPath = OutputDir + "/GripPoseSet.asset";

        private static GripPoseSet LoadOrCreateGripPoseSet()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GripPoseSet>(GripPoseSetPath);
            if (existing != null)
                return existing;

            var set = ScriptableObject.CreateInstance<GripPoseSet>();
            set.fingerBendAxis = Vector3.forward;
            set.grips = new[]
            {
                // Default curls give a visible grip out of the box; calibrate in play-test.
                NewGrip("Melee", fingerCurl: 55f, thumbCurl: 40f),
                NewGrip("Firearms", fingerCurl: 50f, thumbCurl: 35f),
                NewGrip("Tools", fingerCurl: 35f, thumbCurl: 25f),
                NewGrip("", fingerCurl: 40f, thumbCurl: 30f), // fallback (empty category name)
            };

            if (!Directory.Exists(OutputDir))
                Directory.CreateDirectory(OutputDir);
            AssetDatabase.CreateAsset(set, GripPoseSetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[RemoteAvatarPrefabBuilder] Created default GripPoseSet at '{GripPoseSetPath}'.");
            return set;
        }

        private static GripPoseSet.CategoryGrip NewGrip(string category, float fingerCurl, float thumbCurl)
        {
            return new GripPoseSet.CategoryGrip
            {
                categoryName = category,
                modelLocalPosition = Vector3.zero,
                modelLocalEuler = Vector3.zero,
                modelLocalScale = Vector3.one,
                fingerCurl = fingerCurl,
                thumbCurl = thumbCurl,
            };
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
