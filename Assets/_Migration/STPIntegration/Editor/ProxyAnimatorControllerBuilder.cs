#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// Builds <c>ProxyLocomotionController.controller</c> in _Migration/ — a custom
    /// AnimatorController that REPLACES the vendor AnimatorOverrideController on the remote-player
    /// avatar. It reproduces the vendor locomotion EXACTLY (1D-Simple BlendTree on a single Float
    /// "MovementSpeed", children idle@0 / walk@1 / run@3) so <see cref="ProxyLocomotionFeeder"/>
    /// keeps driving it unchanged (SetFloat("MovementSpeed", tier)). A custom controller is the
    /// only way to later add states (jump) that an AnimatorOverrideController cannot.
    ///
    /// FASE 1 = locomotion only (parity). Jump is added in FASE 2 by <see cref="AddJumpState"/>
    /// (kept separate so the locomotion controller can be play-tested for parity in isolation).
    ///
    /// Clips are loaded by path/type, not hard-coded fileIDs: the three Mixamo FBXs expose their
    /// clip as a sub-asset named "mixamo.com", so we pick the first AnimationClip in each. The
    /// idle is the real vendor clip (referenced read-only, never modified).
    ///
    /// Editor-only, lives under _Migration/. Vendor and Rust untouched.
    /// </summary>
    public static class ProxyAnimatorControllerBuilder
    {
        public const string OutputPath =
            "Assets/_Migration/STPIntegration/RemoteAvatar/ProxyLocomotionController.controller";

        private const string IdleClipPath =
            "Assets/PolymindGames/STP/Art/Models/Characters/MaleSurvivor/MaleSurvivor_Idle.anim";
        private const string WalkFbxPath =
            "Assets/_Migration/STPIntegration/RemoteAvatar/Animations/Standard Walk.fbx";
        private const string RunFbxPath =
            "Assets/_Migration/STPIntegration/RemoteAvatar/Animations/Standard Run.fbx";

        private const string MovementSpeedParam = "MovementSpeed";
        private const string JumpParam = "Jump";

        private const string JumpFbxPath =
            "Assets/_Migration/STPIntegration/RemoteAvatar/Animations/Jumping.fbx";

        private const string PickupParam = "Pickup";
        private const string PickupFbxPath =
            "Assets/_Migration/STPIntegration/RemoteAvatar/Animations/Picking Up.fbx";
        // Single source of truth for the pickup playback speed: the Pickup state's speed AND the
        // baked gesture duration (clip ÷ speed) both derive from it, so changing it here keeps the
        // local input-lock in sync on the next rebuild.
        public const float PickupSpeed = 2f;

        // ADR-020 (real clips): crouch locomotion. Same Generic + MaleSurvivor-skeleton format as the
        // other clips (NO Humanoid retarget — the whole proxy pipeline plays Generic clips by matching
        // bone names). Crouch blends as the Y axis over the locomotion BlendTree, driven by Crouched.
        private const string CrouchedParam = "Crouched";
        private const string CrouchIdleFbxPath =
            "Assets/_Migration/STPIntegration/RemoteAvatar/Animations/CrouchIdle.fbx";
        private const string CrouchedWalkFbxPath =
            "Assets/_Migration/STPIntegration/RemoteAvatar/Animations/CrouchedWalking.fbx";

        // Vendor BlendTree thresholds (kept identical for locomotion parity).
        private const float IdleThreshold = 0f;
        private const float WalkThreshold = 1f;
        private const float RunThreshold = 3f;

        [MenuItem("Backrooms/Build Proxy Animator Controller")]
        public static void BuildMenu() => BuildOrRebuild();

        /// <summary>
        /// Creates (or rebuilds from scratch) the locomotion-only controller and returns it.
        /// Deterministic: deletes any existing asset first so a rebuild never accumulates stale
        /// states/orphaned sub-assets. Returns null (with an error logged) if a clip is missing.
        /// NOTE: a rebuild assigns a fresh asset GUID, so after running this standalone you must
        /// re-run "Backrooms ▸ Build Remote Avatar Prefab" to re-point the variant at it. The
        /// prefab builder calls this internally, so the normal workflow needs only that one menu.
        /// </summary>
        public static AnimatorController BuildOrRebuild()
        {
            var idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(IdleClipPath);
            var walk = LoadClipFromFbx(WalkFbxPath);
            var run = LoadClipFromFbx(RunFbxPath);

            if (idle == null || walk == null || run == null)
            {
                Debug.LogError("[ProxyAnimatorControllerBuilder] Missing locomotion clip(s): " +
                    $"idle={(idle != null)} walk={(walk != null)} run={(run != null)}. Aborted.");
                return null;
            }

            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(OutputPath) != null)
                AssetDatabase.DeleteAsset(OutputPath);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(OutputPath);
            controller.AddParameter(MovementSpeedParam, AnimatorControllerParameterType.Float);

            AddMovementState(controller, idle, walk, run);

            AddJumpState(controller);
            AddPickupState(controller);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ProxyAnimatorControllerBuilder] Built '{OutputPath}' " +
                      "(locomotion idle@0 / walk@1 / run@3 on MovementSpeed, 2D crouch blend on Crouched; " +
                      "+ full-body Jump; + full-body Pickup; all on Base Layer).");
            return controller;
        }

        // Locomotion state (Base Layer default). With the crouch clips present it's a 2D
        // FreeformCartesian BlendTree (X=MovementSpeed, Y=Crouched 0..1): standing idle/walk/run at
        // Y=0, crouch idle/walk at Y=1 — so crouch blends ORTHOGONALLY over the SAME velocity-derived
        // MovementSpeed (ProxyCrouchHook drives Crouched). Pickup/Jump are Any-State states that take
        // over full-body briefly and return here STILL crouched (Crouched unchanged) → pickup+crouch
        // coexist (ADR-020 invariant). Falls back to the vendor 1D locomotion if a crouch clip is
        // missing (Crouched param omitted → ProxyCrouchHook no-ops).
        private static void AddMovementState(AnimatorController controller, AnimationClip idle,
            AnimationClip walk, AnimationClip run)
        {
            var crouchIdle = LoadClipFromFbx(CrouchIdleFbxPath);
            var crouchWalk = LoadClipFromFbx(CrouchedWalkFbxPath);
            bool hasCrouch = crouchIdle != null && crouchWalk != null;

            controller.CreateBlendTreeInController("Movement", out BlendTree tree, 0);
            tree.blendParameter = MovementSpeedParam;

            if (hasCrouch)
            {
                controller.AddParameter(CrouchedParam, AnimatorControllerParameterType.Float);
                tree.blendType = BlendTreeType.FreeformCartesian2D;
                tree.blendParameterY = CrouchedParam;
                tree.AddChild(idle, new Vector2(IdleThreshold, 0f));
                tree.AddChild(walk, new Vector2(WalkThreshold, 0f));
                tree.AddChild(run, new Vector2(RunThreshold, 0f));
                tree.AddChild(crouchIdle, new Vector2(IdleThreshold, 1f));
                tree.AddChild(crouchWalk, new Vector2(WalkThreshold, 1f));
                // No crouch-run clip: running while crouched reuses crouch-walk (reads correctly).
                tree.AddChild(crouchWalk, new Vector2(RunThreshold, 1f));
            }
            else
            {
                Debug.LogWarning("[ProxyAnimatorControllerBuilder] Crouch clip(s) missing " +
                    $"(idle={(crouchIdle != null)} walk={(crouchWalk != null)}); building 1D locomotion " +
                    "WITHOUT crouch (Crouched param omitted → ProxyCrouchHook stays inert).");
                tree.blendType = BlendTreeType.Simple1D;
                tree.useAutomaticThresholds = false;
                tree.AddChild(idle, IdleThreshold);
                tree.AddChild(walk, WalkThreshold);
                tree.AddChild(run, RunThreshold);
            }
        }

        // FASE 2: full-body Jump on top of the locomotion controller. Any State → Jump on the
        // "Jump" trigger, and Jump → Movement (default state) on exit time so it never sticks.
        // Driven externally by ProxyJumpFeeder (vertical-velocity edge), client-only — no Rust/flag.
        private static void AddJumpState(AnimatorController controller)
        {
            var jumpClip = LoadClipFromFbx(JumpFbxPath);
            if (jumpClip == null)
            {
                Debug.LogWarning("[ProxyAnimatorControllerBuilder] Jumping.fbx clip missing; " +
                    "Jump state skipped (locomotion still built).");
                return;
            }

            controller.AddParameter(JumpParam, AnimatorControllerParameterType.Trigger);

            var stateMachine = controller.layers[0].stateMachine;
            var jumpState = stateMachine.AddState("Jump");
            jumpState.motion = jumpClip;

            var toJump = stateMachine.AddAnyStateTransition(jumpState);
            toJump.AddCondition(AnimatorConditionMode.If, 0f, JumpParam);
            toJump.duration = 0.08f;
            toJump.hasExitTime = false;
            toJump.canTransitionToSelf = false;

            // Back to locomotion (the default Movement blend-tree state) so Jump never sticks.
            var backToLocomotion = jumpState.AddTransition(stateMachine.defaultState);
            backToLocomotion.hasExitTime = true;
            backToLocomotion.exitTime = 0.8f;
            backToLocomotion.duration = 0.15f;
        }

        // Full-body Pickup on the Base Layer — exact mirror of AddJumpState. The PickUp clip bends
        // the legs to reach the ground, so masking it to the upper body broke the gesture (half
        // crouch); it must play full-body. Any State → Pickup on the "Pickup" trigger; Pickup →
        // Movement (default state) on exit time so it never sticks. Driven by ProxyPickupHook
        // (rp.animation == "pickup"). No extra layers, no mask. writeDefaultValues left at the
        // default (ON) like the Jump state — safe on an unmasked Base Layer state.
        private static void AddPickupState(AnimatorController controller)
        {
            var pickupClip = LoadClipFromFbx(PickupFbxPath);
            if (pickupClip == null)
            {
                Debug.LogWarning("[ProxyAnimatorControllerBuilder] Picking Up.fbx clip missing; " +
                    "Pickup state skipped (locomotion still built).");
                return;
            }

            controller.AddParameter(PickupParam, AnimatorControllerParameterType.Trigger);

            var stateMachine = controller.layers[0].stateMachine;
            var pickupState = stateMachine.AddState("Pickup");
            pickupState.motion = pickupClip;
            pickupState.speed = PickupSpeed; // x2 → the gesture lasts ~half the clip

            var toPickup = stateMachine.AddAnyStateTransition(pickupState);
            toPickup.AddCondition(AnimatorConditionMode.If, 0f, PickupParam);
            toPickup.duration = 0.1f;
            toPickup.hasExitTime = false;
            toPickup.canTransitionToSelf = false;

            // Back to locomotion (the default Movement blend-tree state) so Pickup never sticks.
            var backToLocomotion = pickupState.AddTransition(stateMachine.defaultState);
            backToLocomotion.hasExitTime = true;
            backToLocomotion.exitTime = 0.9f;
            backToLocomotion.duration = 0.15f;
        }

        /// <summary>
        /// Pickup gesture length in seconds (clip length ÷ <see cref="PickupSpeed"/>), used by
        /// RemoteAvatarPrefabBuilder to bake ProxyPickupHook.GestureDuration. Editor-only.
        /// </summary>
        public static float GetPickupGestureDuration()
        {
            var clip = LoadClipFromFbx(PickupFbxPath);
            return (clip != null && PickupSpeed > 0f) ? clip.length / PickupSpeed : 0.6f;
        }

        private static AnimationClip LoadClipFromFbx(string fbxPath)
        {
            return AssetDatabase.LoadAllAssetsAtPath(fbxPath)
                .OfType<AnimationClip>()
                .FirstOrDefault(c => c != null && !c.name.StartsWith("__preview__"));
        }
    }
}
#endif
