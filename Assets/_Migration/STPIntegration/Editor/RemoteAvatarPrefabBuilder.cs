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
    /// ADR-023) + ProxyHitReactionHook (procedural spine recoil flinch from rp.hitSeq, ADR-024) +
    /// ProxyRevealHook (shows the robapieles' real form while rp.revealed, ADR-038 — V2 hides the
    /// disguise renderers and activates the nested "RealForm" body wired by WireRealForm) on the root. ADR-022 also sets CharacterClothing._attachToCharacter = false so the proxy never
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
                WireCarryHook(instance); // ADR-049 — after the held-item hook, whose hand it defers to
                WireHitReactionHook(instance);
                WireRevealHook(instance);
                WireVocalHook(instance);
                WireGrabHook(instance);
                WireRealForm(instance);
                WireFootstepHook(instance);
                SeedRevealedSteps(instance);
                WireLightHook(instance);
                WireFireAudioHook(instance);
                WireDamageAudioHook(instance);
                WireMeleeHook(instance);
                WireStanceHook(instance);
                WireLeanHook(instance);
                WireSprayHook(instance);

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
        /// <summary>
        /// The controller object handed back by the rebuild, kept for the rest of THIS bake.
        ///
        /// This is the whole fix. `WireAnimatorController` runs first and works because it uses the
        /// object `BuildOrRebuild()` RETURNS; every later step asked the AssetDatabase for it by
        /// path instead, and got null — the asset was deleted and recreated moments earlier and the
        /// database does not have it back inside the same frame. Even a forced synchronous import
        /// does not rescue it. So the later steps must be handed the same object, not sent to look
        /// it up again.
        /// </summary>
        private static RuntimeAnimatorController _bakedController;

        private static void WireAnimatorController(GameObject root)
        {
            var controller = ProxyAnimatorControllerBuilder.BuildOrRebuild();
            _bakedController = controller; // may be null; LoadProxyController falls back to the DB
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
        /// <summary>
        /// The proxy controller asset, forcing a synchronous import when the AssetDatabase has not
        /// caught up yet.
        ///
        /// EARNED THE HARD WAY: `WireAnimatorController` runs FIRST in the bake and rebuilds the
        /// controller with `DeleteAsset` + `CreateAnimatorControllerAtPath`. Every later step that
        /// asks for the controller can therefore get NULL — not because the build failed, but
        /// because the database has not re-imported it inside the same frame. Two things then get
        /// a null stamped onto them (the runtime binder, and the nested real-form body), and the
        /// symptom does not show until something T-poses in a play-test. It is intermittent, which
        /// is why an earlier bake of the very same code produced a clean prefab.
        /// </summary>
        private static RuntimeAnimatorController LoadProxyController()
        {
            // The object from this bake FIRST. Measured, not assumed: with only the path lookups
            // below, this bake still serialised `m_Controller: {fileID: 0}` onto the nested body
            // and null onto the runtime binder, while the root Animator — the one step that uses
            // the returned object — came out correct.
            if (_bakedController != null)
                return _bakedController;

            var c = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                ProxyAnimatorControllerBuilder.OutputPath);
            if (c != null)
                return c;

            AssetDatabase.ImportAsset(ProxyAnimatorControllerBuilder.OutputPath,
                ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                ProxyAnimatorControllerBuilder.OutputPath);
        }

        private static void WireControllerBinder(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            var controller = LoadProxyController();
            if (controller == null)
                Debug.LogError("[RemoteAvatarPrefabBuilder] Proxy controller asset not found for the " +
                    "runtime binder; the BUILD will T-pose. Did the controller build succeed?");

            var binder = root.GetComponent<ProxyControllerBinder>();
            if (binder == null)
                binder = root.AddComponent<ProxyControllerBinder>();

            var so = new SerializedObject(binder);
            var animProp = so.FindProperty("_animator");
            if (animProp != null)
                animProp.objectReferenceValue = animator;
            var ctrlProp = so.FindProperty("_controller");
            // Never write a null OVER a good reference: a transient database miss would silently
            // undo the one guarantee this binder exists to provide, and the failure would only
            // surface in a player build.
            if (ctrlProp != null && controller != null)
                ctrlProp.objectReferenceValue = controller;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Guaranteed binding (BUG ① fix): operate on the SAVED variant asset directly, immune to
        // the instance-override quirk and the controller's GUID churn. Loads the just-saved prefab,
        // points its Animator at the custom controller, and re-saves only if it wasn't already bound.
        private static void EnsureControllerBound()
        {
            var controller = LoadProxyController();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
            if (controller == null || prefab == null)
            {
                Debug.LogError("[RemoteAvatarPrefabBuilder] Cannot verify the saved prefab's Animator " +
                    "binding — the proxy WILL T-pose. controller=" + (controller == null ? "null" : "ok") +
                    " prefab=" + (prefab == null ? "null" : "ok"));
                return;
            }

            // EVERY Animator, not just the first. This used to take `GetComponentInChildren` and so
            // only ever checked the ROOT (the disguise) — which is exactly why a null on the nested
            // real-form body survived the bake and T-posed the revealed creature through a play-test.
            // Both bodies run the SAME controller by design (ADR-038 V2: the reveal costs no
            // animation work precisely because the real form retargets the proxy's own clips), so
            // binding all of them is the correct rule and not a blunt instrument.
            var animators = prefab.GetComponentsInChildren<Animator>(true);
            int fixedUp = 0;
            foreach (var animator in animators)
            {
                if (animator == null || animator.runtimeAnimatorController == controller)
                    continue;
                animator.runtimeAnimatorController = controller;
                fixedUp++;
            }

            if (fixedUp > 0)
            {
                PrefabUtility.SavePrefabAsset(prefab);
                Debug.Log($"[RemoteAvatarPrefabBuilder] Re-bound {fixedUp} Animator(s) on the saved asset " +
                          "to ProxyLocomotionController (the instance assignment had not stuck).");
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

        // ADR-049: mirror of WireHeldItemHook for the carry hook. Left hand, matching
        // WieldableCarrySettings.TargetSocket on the carryable definitions and staying clear of the
        // right hand that WireHeldItemHook just claimed. Idempotent, add-if-missing.
        private static void WireCarryHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyCarryHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyCarryHook>();

            var carryPoses = LoadOrCreateCarryPoseSet();

            var so = new SerializedObject(hook);
            var boneProp = so.FindProperty("_handBoneName");
            if (boneProp != null)
                boneProp.stringValue = "Hand.L";
            var posesProp = so.FindProperty("_carryPoses");
            if (posesProp != null)
                posesProp.objectReferenceValue = carryPoses;
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

        // Mirror of WireHitReactionHook for the reveal hook (ADR-038). NOTHING is baked here: the
        // hook caches the skinned renderers at runtime, and its only serialized field is the
        // real-form Material, deliberately LEFT UNTOUCHED — null falls back to a generated pale
        // skinless stand-in, and once a material is authored and assigned by hand a re-bake must
        // preserve it (same rule as the GripPoseSet asset below). Add-if-missing, nothing else.
        private static void WireRevealHook(GameObject root)
        {
            if (root.GetComponent<ProxyRevealHook>() == null)
                root.AddComponent<ProxyRevealHook>();
        }

        /// <summary>
        /// The procedural grab: both arms reach for whoever it is killing. All defaults live in the
        /// component, so there is nothing to wire beyond adding it — same shape as WireRevealHook.
        /// </summary>
        private static void WireGrabHook(GameObject root)
        {
            if (root.GetComponent<ProxyGrabHook>() == null)
                root.AddComponent<ProxyGrabHook>();
        }

        /// <summary>
        /// The revealed creature's own footfalls. Seeded here rather than left to the inspector for
        /// the same reason as the voice banks: a wired-but-silent feature cannot be told apart from
        /// a broken one during a play-test.
        /// </summary>
        private static void SeedRevealedSteps(GameObject root)
        {
            var hook = root.GetComponent<ProxyFootstepHook>();
            if (hook == null)
                return;

            var steps = LoadVoiceClips("PhantomStep_");
            if (steps.Length == 0)
            {
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] No PhantomStep_* clips in " +
                    PhantomRealFormBuilder.ScreamDir + " — the revealed creature keeps human footsteps.");
                return;
            }

            var so = new SerializedObject(hook);
            var arr = so.FindProperty("_revealedSteps");
            if (arr == null)
                return;

            arr.arraySize = steps.Length;
            for (int i = 0; i < steps.Length; i++)
                arr.GetArrayElementAtIndex(i).objectReferenceValue = steps[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// ADR-048: the creature's voice, driven by the <c>vocal_seq</c> counter instead of by the
        /// reveal flag — the whole point being that it can vocalise WITHOUT dropping its disguise,
        /// which ADR-038 forbids expressing through <c>revealed</c>.
        ///
        /// Wires bank 0 (reveal scream) from the same clips <see cref="PhantomRealFormBuilder"/>
        /// already generates, so the migrated scream keeps working with no new audio to author. The
        /// other three banks are left EMPTY on purpose: an unauthored voice is silent, never a
        /// missing-reference error, so the search shriek and the noise grunt light up the day
        /// somebody drops clips in without touching code.
        /// </summary>
        private static void WireVocalHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyVocalHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyVocalHook>();

            var so = new SerializedObject(hook);
            var voices = so.FindProperty("_voices");
            if (voices == null)
                return;

            if (voices.arraySize < 9)
                voices.arraySize = 9;

            // Bank → filename prefix. SELECTED BY PREFIX AND NOT "everything in the folder", which
            // is what this did while only the screams existed: the moment a second voice landed in
            // the same directory, a blanket load would have put grunts and breaths into the reveal
            // scream too. The folder is a folder, not a bank.
            //
            // Bank 1 (search shriek) still points at the screams DELIBERATELY: it is the same kind
            // of sound and shipping it silent would make the first play-test unable to tell "the
            // feature is broken" from "nobody authored a clip".
            var banks = new[]
            {
                "PhantomScream_",        // 0 reveal
                "PhantomScream_",        // 1 search shriek (placeholder, same family)
                "PhantomVoice_Grunt",    // 2 noise reaction, close by
                "PhantomVoice_Breath",   // 3 stalking breath
                "PhantomVoice_Answer",   // 4 the long-range answer to a gunshot
                "PhantomVoice_Sated",    // 5 after a kill
                // ADR-050. Both ship UNAUTHORED: there is no clip family in the repo for either, and
                // the rule above applies — an empty bank is silent, and silence is honest where a
                // borrowed sound would be a lie. Drop `PhantomVoice_Moan*` / `PhantomVoice_Winded*`
                // into the audio folder and re-bake, and they light up with no code change.
                "PhantomVoice_Moan",     // 6 the hungry moan
                "PhantomVoice_Winded",   // 7 out of breath mid-charge
                // ADR-051. Points at the SCREAMS deliberately, the same way bank 1 does: the sound
                // of something coming apart is the same family, and shipping the warning silent
                // would make the first play-test unable to tell "the tell is broken" from "nobody
                // authored a clip". Swap for its own family when one exists.
                "PhantomScream_",        // 8 the warning, a beat before the skin tears
            };

            for (int bank = 0; bank < banks.Length && bank < voices.arraySize; bank++)
            {
                var found = LoadVoiceClips(banks[bank]);
                var clips = voices.GetArrayElementAtIndex(bank).FindPropertyRelative("Clips");
                if (clips == null)
                    continue;

                // An unauthored bank is left EMPTY rather than filled with something approximate:
                // an empty bank is silent, and silence is honest. A wrong sound is not.
                clips.arraySize = found.Length;
                for (int i = 0; i < found.Length; i++)
                    clips.GetArrayElementAtIndex(i).objectReferenceValue = found[i];

                if (found.Length == 0)
                    Debug.LogWarning($"[RemoteAvatarPrefabBuilder] ADR-048 voice bank {bank} " +
                        $"('{banks[bank]}*') has no clips in {PhantomRealFormBuilder.ScreamDir} — that voice is silent.");
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Every AudioClip in the voice folder whose FILE NAME starts with `prefix`, sorted so a
        /// re-bake never reshuffles the array (FindAssets order is not guaranteed, and a diff that
        /// churns on every bake hides the one that matters).
        /// </summary>
        private static AudioClip[] LoadVoiceClips(string prefix)
        {
            var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { PhantomRealFormBuilder.ScreamDir });
            var paths = new System.Collections.Generic.List<string>(guids.Length);
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileName(path).StartsWith(prefix, System.StringComparison.Ordinal))
                    paths.Add(path);
            }
            paths.Sort(System.StringComparer.Ordinal);

            var clips = new System.Collections.Generic.List<AudioClip>(paths.Count);
            foreach (var path in paths)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null)
                    clips.Add(clip);
            }
            return clips.ToArray();
        }

        // ADR-038 V2: nests the robapieles' real-form body (its own rig, its own Animator) as an
        // INACTIVE child and hands it to the reveal hook. It has to live on the shared avatar prefab
        // rather than on some phantom-only prefab: ADR-016's whole invariant is that the client cannot
        // tell a phantom from a player until the backend says so, so every proxy carries the body and
        // only a revealed one ever activates it. Inactive it costs a skeleton's worth of transforms.
        // Rebuilt (destroy + re-instantiate) each bake so the child can never be a stale nested prefab.
        private const string RealFormChildName = "RealForm";

        private static void WireRealForm(GameObject root)
        {
            var existing = root.transform.Find(RealFormChildName);
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            var bodyPrefab = PhantomRealFormBuilder.BuildOrGet();
            if (bodyPrefab == null)
            {
                Debug.LogWarning("[RemoteAvatarPrefabBuilder] ADR-038 V2: no real-form body built; the " +
                    "reveal falls back to the V1 material swap (see PhantomRealFormBuilder's errors).");
                return;
            }

            var body = (GameObject)PrefabUtility.InstantiatePrefab(bodyPrefab, root.transform);
            body.name = RealFormChildName;
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            // localScale is deliberately NOT reset: it is authored on the body prefab.
            body.SetActive(false);

            // RE-STAMP THE BODY'S CONTROLLER. This is the T-pose fix.
            //
            // PhantomRealForm.prefab has the right controller on its own asset, but instantiating it
            // HERE — after WireAnimatorController deleted and recreated that controller — resolved
            // the reference to null on the instance, and SaveAsPrefabAsset then serialised the null
            // as an override (`m_Controller: {fileID: 0}`) that OVERRIDES the good value underneath.
            // The revealed creature came out in T-pose while every real player animated fine, which
            // is exactly the shape of the bug reported in play-test.
            //
            // `GetComponentInChildren<Animator>(true)` is the SAME lookup ProxyRevealHook uses to
            // find `_bodyAnimator`, deliberately: two different lookups here would be two things
            // that can disagree about which Animator is the body's.
            var bodyAnimator = body.GetComponentInChildren<Animator>(true);
            if (bodyAnimator != null)
            {
                var bodyController = LoadProxyController();
                if (bodyController == null)
                {
                    Debug.LogError("[RemoteAvatarPrefabBuilder] ADR-038 V2: no proxy controller for the " +
                        "real-form body — the revealed creature WILL T-pose.");
                }
                else
                {
                    bodyAnimator.runtimeAnimatorController = bodyController;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(bodyAnimator);
                }
            }

            var hook = root.GetComponent<ProxyRevealHook>();
            if (hook == null)
                return;

            var so = new SerializedObject(hook);
            var bodyProp = so.FindProperty("_realFormBody");
            if (bodyProp != null)
                bodyProp.objectReferenceValue = body;
            ClearScreamClips(so);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ADR-050: the bake CLEARS this array instead of seeding it, and that inversion is the fix
        // for a duplicated scream.
        //
        // ADR-048 moved the reveal sound to the backend: it emits `vocal_kind = 0` on entering
        // SPRINT and ProxyVocalHook plays it, so every client hears it at the same instant instead
        // of each one inferring it from its own reception of the `revealed` level. ProxyRevealHook
        // kept its own Scream() as a fallback for prefabs not yet re-baked with the vocal hook, and
        // its comment asks for `_screamClips` to be left EMPTY once that hook is wired — but this
        // builder never applied it. WireVocalHook and WireRevealHook run in the same bake, three
        // lines apart, and the baked prefab ended up with 5 clips here and the same three GUIDs in
        // ProxyVocalHook's bank 0. Result: on the ordinary reveal path (Stalk into Sprint) BOTH
        // sources fired on the same transition, from two AudioSources with different cutoffs.
        //
        // Clearing rather than "seed only when empty" on purpose: what is in there today is
        // machine-seeded, not chosen by hand, so there is no authored decision to protect.
        private static void ClearScreamClips(SerializedObject hookSo)
        {
            var clips = hookSo.FindProperty("_screamClips");
            if (clips == null || clips.arraySize == 0)
                return;
            clips.ClearArray();
        }

        // ADR-042: adds the footstep hook. It reads NO networked field — position and the world under
        // the proxy are all it needs — so unlike its siblings there is nothing here to bind to a view;
        // we only bake the gait/volume tuning so it starts calibrated instead of at Unity defaults.
        // The speed bands are deliberately the SAME numbers WireLocomotionFeeder bakes: if the two
        // drifted apart, a peer could be playing the run animation while emitting walk footsteps.
        // Idempotent, add-if-missing.
        private static void WireFootstepHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyFootstepHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyFootstepHook>();

            var so = new SerializedObject(hook);
            SetFeederFloat(so, "_walkStride", 0.85f);
            SetFeederFloat(so, "_runStride", 1.35f);
            SetFeederFloat(so, "_deadzoneSpeed", 0.1f);
            SetFeederFloat(so, "_walkSpeed", 1.5f);
            SetFeederFloat(so, "_runSpeed", 4.5f);
            // Play-test tuning (2026-08-02): peers were audible from absurdly far. A footstep needs a
            // HARD cutoff, not a thin tail that never ends — and stealth depends on it being short-range.
            SetFeederFloat(so, "_minDistance", 1.5f);
            SetFeederFloat(so, "_maxDistance", 22f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ADR-042: adds the held-light hook and bakes the bone name it hangs the Light off. Only the
        // bone is baked: the colour/intensity/range are torch-warm defaults that Joel is expected to
        // calibrate in play-test, and re-baking must NOT stomp that calibration — same rule as the
        // GripPoseSet asset and the ProxyRevealHook material. Idempotent, add-if-missing.
        private static void WireLightHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyLightHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyLightHook>();

            var so = new SerializedObject(hook);
            var boneProp = so.FindProperty("_handBoneName");
            if (boneProp != null)
                boneProp.stringValue = "Hand.R";
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ADR-042: adds the gunshot hook and binds the audio set. Same idempotence rule as the
        // GripPoseSet: an existing asset is referenced AS-IS, never reseeded, so the clips Joel drags
        // in survive every re-bake.
        private static void WireFireAudioHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyFireAudioHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyFireAudioHook>();

            var so = new SerializedObject(hook);
            var setProp = so.FindProperty("_audioSet");
            if (setProp != null)
                setProp.objectReferenceValue = LoadOrCreateAudioSet();
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ADR-044: adds the melee-swing hook. Bones (UpperArm.R / LowerArm.R on the MaleSurvivor rig)
        // are resolved by name at runtime; only the arc tuning is baked. Idempotent.
        private static void WireMeleeHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyMeleeHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyMeleeHook>();

            var so = new SerializedObject(hook);
            SetFeederFloat(so, "_magnitude", 70f);
            SetFeederFloat(so, "_duration", 0.35f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ADR-044: adds the aim/reload stance hook. Same shape as WireMeleeHook — bones by name at
        // runtime, only the pose tuning baked. Nothing here declares which BIT means what: that lives
        // once in RemoteButtons, so the builder cannot drift from the transmitter. Idempotent.
        private static void WireStanceHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyStanceHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyStanceHook>();

            var so = new SerializedObject(hook);
            SetFeederFloat(so, "_aimArmRaise", 38f);
            SetFeederFloat(so, "_aimForearmTuck", 22f);
            SetFeederFloat(so, "_reloadArmDrop", 30f);
            SetFeederFloat(so, "_reloadOffHandSwing", 45f);
            SetFeederFloat(so, "_blendTime", 0.18f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Adds the body-lean hook (Q/E). Same shape as WireStanceHook — bones by name at runtime,
        // only the pose tuning baked — and for the same reason it needs no wire work: lean rides the
        // free bits of `buttons` that ADR-044 left for exactly this. Which bit means what is NOT
        // declared here; it lives once in RemoteButtons. Idempotent.
        private static void WireLeanHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyLeanHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyLeanHook>();

            var so = new SerializedObject(hook);
            SetFeederFloat(so, "_leanAngle", 20f);
            SetFeederFloat(so, "_headAngle", 6f);
            SetFeederFloat(so, "_blendTime", 0.15f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ADR-068 fase A: el chorro y el siseo de un peer que está pintando. Misma forma que
        // WireLeanHook y por el mismo motivo no toca wire: viaja en otro bit libre de `buttons`.
        // El bit no se declara aquí — vive una sola vez en RemoteButtons, así que el builder no
        // puede desincronizarse del transmisor. Idempotente.
        private static void WireSprayHook(GameObject root)
        {
            var hook = root.GetComponent<ProxySprayHook>();
            if (hook == null)
                hook = root.AddComponent<ProxySprayHook>();

            var so = new SerializedObject(hook);
            SetFeederFloat(so, "_speed", 7f);
            SetFeederFloat(so, "_lifetime", 0.22f);
            SetFeederFloat(so, "_size", 0.025f);
            SetFeederFloat(so, "_rate", 70f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ADR-044: adds the pain-grunt hook and bakes FPSCore's own damage clips into it. Unlike the
        // gunshot (ADR-042), this one CAN be seeded automatically: the clips already ship with the
        // project, so the effect works from the first bake with nothing to author. Only seeded when
        // the array is EMPTY — a re-bake must never overwrite clips chosen by hand, same rule as the
        // GripPoseSet asset and the ProxyRevealHook material.
        private const string DamageClipDir = "Assets/PolymindGames/FPSCore/Audio/SFX/Damage";

        private static void WireDamageAudioHook(GameObject root)
        {
            var hook = root.GetComponent<ProxyDamageAudioHook>();
            if (hook == null)
                hook = root.AddComponent<ProxyDamageAudioHook>();

            var so = new SerializedObject(hook);
            var clips = so.FindProperty("_hurtClips");
            if (clips != null && clips.arraySize == 0)
            {
                string[] names = { "FPS_Human_GenericDamage1", "FPS_Human_GenericDamage2", "FPS_Human_GenericDamage3" };
                foreach (var name in names)
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{DamageClipDir}/{name}.wav");
                    if (clip == null)
                    {
                        Debug.LogWarning($"[RemoteAvatarPrefabBuilder] ADR-044: missing {name}.wav — " +
                                         "remote pain grunts will be that much less varied.");
                        continue;
                    }
                    clips.InsertArrayElementAtIndex(clips.arraySize);
                    clips.GetArrayElementAtIndex(clips.arraySize - 1).objectReferenceValue = clip;
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ADR-042: the per-weapon fire clips live as a ScriptableObject, created EMPTY on purpose.
        // Nothing in the project can mint a correct default: the clip a firearm plays is a private
        // field on the first-person prefab (see RemoteWieldableAudioSet's doc for why neither
        // reflection nor GetWieldableWithId is a valid route). Until a clip is dragged in, peers fire
        // silently — declared, not hidden.
        private const string AudioSetPath = OutputDir + "/RemoteWieldableAudioSet.asset";

        private static RemoteWieldableAudioSet LoadOrCreateAudioSet()
        {
            var existing = AssetDatabase.LoadAssetAtPath<RemoteWieldableAudioSet>(AudioSetPath);
            if (existing != null)
                return existing;

            var set = ScriptableObject.CreateInstance<RemoteWieldableAudioSet>();
            AssetDatabase.CreateAsset(set, AudioSetPath);
            AssetDatabase.SaveAssets();
            Debug.LogWarning("[RemoteAvatarPrefabBuilder] ADR-042: created an EMPTY " +
                             $"{AudioSetPath}. Remote gunshots stay SILENT until a clip is assigned " +
                             "(defaultFireClip alone is enough to make every weapon audible).");
            return set;
        }

        // ADR-049: same contract as the grip set — the stack placement lives as a ScriptableObject so
        // it is live-editable during Play, and a re-bake REUSES an existing asset instead of
        // re-seeding it. Calibrating four plank offsets by hand and then losing them to the next bake
        // is exactly the trap the grip set already avoids.
        private const string CarryPoseSetPath = OutputDir + "/CarryPoseSet.asset";

        private static CarryPoseSet LoadOrCreateCarryPoseSet()
        {
            var existing = AssetDatabase.LoadAssetAtPath<CarryPoseSet>(CarryPoseSetPath);
            if (existing != null)
                return existing;

            // The field initialisers on CarryPoseSet already stack four planks a hand's width apart,
            // which is visible out of the box and wrong in the details — the point is that it renders
            // something to calibrate against, not that the numbers are right.
            var set = ScriptableObject.CreateInstance<CarryPoseSet>();

            if (!Directory.Exists(OutputDir))
                Directory.CreateDirectory(OutputDir);
            AssetDatabase.CreateAsset(set, CarryPoseSetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[RemoteAvatarPrefabBuilder] Created default CarryPoseSet at '{CarryPoseSetPath}'.");
            return set;
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
