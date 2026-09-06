#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using BackroomsSurvival.Gameplay;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration.EditorTools
{
    /// <summary>
    /// Builds <c>ProxyLocomotionController.controller</c> in _Migration/ — a custom
    /// AnimatorController that REPLACES the vendor AnimatorOverrideController on the remote-player
    /// avatar. A custom controller is the only way to add states (jump, pickup) that an
    /// AnimatorOverrideController cannot.
    ///
    /// LA LOCOMOCIÓN ES DIRECCIONAL desde la fase 1 del sistema 3P (2026-09-03): árbol anidado
    /// Crouched → {Standing, Crouching}, cada uno 2D FreeformDirectional sobre (MoveX, MoveY). El
    /// desglose y el porqué están en <see cref="AddMovementState"/>. El escalar "MovementSpeed" se
    /// declara igual —lo escribe <see cref="ProxyLocomotionFeeder"/> y lo reenvía ProxyRevealHook al
    /// cuerpo real del robapieles— aunque el árbol ya no mezcle sobre él.
    ///
    /// EL RIG ES HUMANOID, no Generic: <c>MaleSurvivor.fbx</c> importa como Human/CreateFromThisModel
    /// y los FBX de RemoteAvatar/Animations como Human/CopyFromOther (los .meta mandan; varios
    /// doc-comments de los hooks afirman lo contrario y están obsoletos). Por eso los clips
    /// retargetean solos al robapieles y a los facelings, y por eso un futuro strafe izquierdo puede
    /// salir del derecho con <c>mirror</c>.
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
        // Fase 1 del sistema 3P (2026-09-03): los dos ejes de la locomoción DIRECCIONAL, en espacio
        // local del avatar (X = su derecha, Y = su frente) y con el módulo en la escala de tiers de
        // siempre. Los escribe ProxyLocomotionFeeder; MovementSpeed se sigue escribiendo aparte.
        private const string MoveXParam = "MoveX";
        private const string MoveYParam = "MoveY";
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

        // Radios de los anillos del árbol direccional, EN LA MISMA ESCALA que el escalar
        // MovementSpeed (0 / 1 / 3). No son números nuevos: salen de ProxyLocomotionMath, que es
        // quien produce el vector, para que el árbol y el feeder no puedan desincronizarse — el
        // invariante es |(MoveX,MoveY)| == MovementSpeed.
        private const float IdleThreshold = ProxyLocomotionMath.IdleTier;
        private const float WalkThreshold = ProxyLocomotionMath.WalkTier;
        private const float RunThreshold = ProxyLocomotionMath.RunTier;

        // Los dos ejes de crouch del árbol raíz (1D sobre "Crouched").
        private const float StandingThreshold = 0f;
        private const float CrouchingThreshold = 1f;

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
            // Se declara aunque el árbol ya no mezcle sobre él: ProxyLocomotionFeeder lo escribe y
            // ProxyRevealHook lo reenvía al Animator del cuerpo real. Quitarlo dejaría a los dos
            // escribiendo un parámetro inexistente (un warning por frame y por peer).
            controller.AddParameter(MovementSpeedParam, AnimatorControllerParameterType.Float);

            AddMovementState(controller, idle, walk, run);

            AddJumpState(controller);
            AddPickupState(controller);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ProxyAnimatorControllerBuilder] Built '{OutputPath}' " +
                      "(locomoción DIRECCIONAL: Crouched → {Standing, Crouching}, cada uno 2D " +
                      "FreeformDirectional sobre MoveX/MoveY con anillos walk@1 y run@3, atrás por " +
                      "timeScale −1 y strafe en placeholder; + full-body Jump; + full-body Pickup; " +
                      "todo en Base Layer).");
            return controller;
        }

        /// <summary>
        /// Locomotion state (Base Layer default). FASE 1 DEL SISTEMA 3P (2026-09-03): pasa de un
        /// árbol 2D (rapidez × crouch) a un árbol ANIDADO, porque hacen falta tres ejes y un
        /// BlendTree de Unity solo tiene dos.
        ///
        ///   Movement            1D Simple sobre "Crouched"
        ///     ├── Standing      2D FreeformDirectional (X=MoveX, Y=MoveY)   ← umbral 0
        ///     └── Crouching     2D FreeformDirectional (X=MoveX, Y=MoveY)   ← umbral 1
        ///
        /// POR QUÉ DIRECCIONAL Y NO ESCALAR. El árbol anterior solo sabía "cuán rápido", así que un
        /// peer que retrocedía o que se desplazaba de lado reproducía la caminata hacia delante y se
        /// deslizaba. Con el vector, cada cuadrante tiene su sitio: la RAPIDEZ es el módulo (anillo
        /// walk a radio 1, run a radio 3 — los mismos números de siempre) y la DIRECCIÓN es el
        /// ángulo. Las diagonales no necesitan clip propio: FreeformDirectional interpola entre los
        /// vecinos del anillo, que es literalmente para lo que existe ese modo.
        ///
        /// EL CROUCH SIGUE SIENDO ORTOGONAL, igual que en ADR-020: los dos subárboles se alimentan de
        /// los MISMOS MoveX/MoveY, así que agacharse no re-calibra la locomoción, solo cambia el juego
        /// de clips. Pickup/Jump siguen siendo estados Any-State que se llevan el cuerpo entero un
        /// momento y vuelven aquí AGACHADOS si lo estaban (Crouched no se toca).
        ///
        /// DEGRADACIÓN: sin clips de crouch, se construye un único árbol direccional y se omite el
        /// parámetro Crouched (ProxyCrouchHook se queda inerte por su propia sonda), exactamente como
        /// antes de esta fase.
        /// </summary>
        private static void AddMovementState(AnimatorController controller, AnimationClip idle,
            AnimationClip walk, AnimationClip run)
        {
            var crouchIdle = LoadClipFromFbx(CrouchIdleFbxPath);
            var crouchWalk = LoadClipFromFbx(CrouchedWalkFbxPath);
            bool hasCrouch = crouchIdle != null && crouchWalk != null;

            controller.AddParameter(MoveXParam, AnimatorControllerParameterType.Float);
            controller.AddParameter(MoveYParam, AnimatorControllerParameterType.Float);

            controller.CreateBlendTreeInController("Movement", out BlendTree root, 0);

            if (!hasCrouch)
            {
                Debug.LogWarning("[ProxyAnimatorControllerBuilder] Crouch clip(s) missing " +
                    $"(idle={(crouchIdle != null)} walk={(crouchWalk != null)}); building directional " +
                    "locomotion WITHOUT crouch (Crouched param omitted → ProxyCrouchHook stays inert).");
                FillDirectionalTree(root, "Movement", idle, walk, run);
                return;
            }

            controller.AddParameter(CrouchedParam, AnimatorControllerParameterType.Float);
            root.name = "Movement";
            root.blendType = BlendTreeType.Simple1D;
            root.blendParameter = CrouchedParam;
            root.useAutomaticThresholds = false;

            // CreateBlendTreeChild deja el subárbol como sub-asset del propio controller. Construirlo
            // con `new BlendTree()` lo dejaría huérfano y el .controller guardado saldría con un hijo
            // nulo — un T-pose sin ningún error en consola, que es el modo de fallo caro de este
            // pipeline (ya pasó con un controller nulo horneado sobre el cuerpo real, ProxyRevealHook).
            var standing = root.CreateBlendTreeChild(StandingThreshold);
            FillDirectionalTree(standing, "Standing", idle, walk, run);

            // El anillo de correr agachado reutiliza el clip de andar agachado: no hay clip de correr
            // en cuclillas, y a esa velocidad la lectura correcta es "anda agachado rápido".
            var crouching = root.CreateBlendTreeChild(CrouchingThreshold);
            FillDirectionalTree(crouching, "Crouching", crouchIdle, crouchWalk, crouchWalk);
        }

        /// <summary>
        /// Rellena un árbol 2D FreeformDirectional con idle en el centro y dos anillos de cuatro
        /// direcciones (walk a radio 1, run a radio 3).
        ///
        /// DE DÓNDE SALEN LOS CLIPS QUE NO TENEMOS, y esto es una decisión, no un descuido:
        ///  · ATRÁS = el mismo clip a <c>timeScale −1</c>. Una caminata humanoide reproducida al revés
        ///    ES un backpedal creíble: los pies retroceden y los brazos acompañan. Cuesta cero arte.
        ///  · LATERAL = PLACEHOLDER, el clip de frente. No hay forma honesta de sintetizar un strafe a
        ///    partir de una caminata frontal (espejarla sigue dando una caminata frontal), así que se
        ///    deja el mismo clip que se veía ANTES de esta fase: cero regresión, y el hueco queda
        ///    cableado para el día que llegue el clip — cambiar <see cref="StrafePlaceholder"/> por el
        ///    clip real es una línea, y el izquierdo sale del derecho con <c>mirror = true</c> (los
        ///    clips son Humanoid, así que el espejo es gratis).
        /// </summary>
        private static void FillDirectionalTree(BlendTree tree, string name, AnimationClip idle,
            AnimationClip walk, AnimationClip run)
        {
            tree.name = name;
            tree.blendType = BlendTreeType.FreeformDirectional2D;
            tree.blendParameter = MoveXParam;
            tree.blendParameterY = MoveYParam;

            var children = new List<ChildMotion>
            {
                MakeChild(idle, Vector2.zero, 1f),
            };
            AddDirectionRing(children, walk, WalkThreshold);
            AddDirectionRing(children, run, RunThreshold);
            tree.children = children.ToArray();
        }

        /// <summary>
        /// Los cuatro puntos cardinales de un anillo de radio <paramref name="radius"/>, en el orden
        /// frente / derecha / izquierda / atrás.
        /// </summary>
        private static void AddDirectionRing(List<ChildMotion> children, AnimationClip clip, float radius)
        {
            children.Add(MakeChild(clip, new Vector2(0f, radius), 1f));            // frente
            children.Add(MakeChild(StrafePlaceholder(clip), new Vector2(radius, 0f), 1f));   // derecha
            children.Add(MakeChild(StrafePlaceholder(clip), new Vector2(-radius, 0f), 1f));  // izquierda
            children.Add(MakeChild(clip, new Vector2(0f, -radius), -1f));          // atrás (invertido)
        }

        /// <summary>
        /// EL ÚNICO PUNTO donde se decide qué se ve al desplazarse de lado. Hoy devuelve el clip
        /// frontal porque no existe ninguno de strafe en el proyecto (ver la auditoría
        /// docs/AUDIT-ANIMATION-2026-09-03.md §7: hay NUEVE clips de cuerpo entero en total).
        /// </summary>
        private static AnimationClip StrafePlaceholder(AnimationClip forwardClip) => forwardClip;

        private static ChildMotion MakeChild(Motion motion, Vector2 position, float timeScale)
        {
            return new ChildMotion
            {
                motion = motion,
                position = position,
                timeScale = timeScale,
                cycleOffset = 0f,
                mirror = false,
                // Ignorado en un árbol que no sea Direct. Se pone el mismo literal que escribe la
                // propia Unity al crear un hijo ("Blend", verificable en el hijo que mete
                // CreateBlendTreeChild unas líneas más arriba) para que el asset salga uniforme y
                // nadie lea un nombre de parámetro real donde no se usa ninguno.
                directBlendParameter = "Blend",
            };
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
