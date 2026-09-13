#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BackroomsSurvival.Gameplay.HandInteraction;
using PolymindGames.WieldableSystem;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BackroomsSurvival.EditorTools.HandInteraction
{
    [Serializable]
    public sealed class HandBakeResult
    {
        public bool written;
        public string report;
        public List<string> clips = new();
        public List<HandGripMetrics> hands = new();
        public List<string> warnings = new();
    }

    /// <summary>
    /// Hornea un <see cref="HandInteractionProfile"/> sobre su wieldable.
    ///
    /// CAPAS, en este orden y en cada fotograma: (1) el clip BASE —el que el wieldable ya usaba, copiado
    /// intacto la primera vez, así que rehornear nunca apila—; (2) el objeto va donde ese clip lo lleva,
    /// porque cuelga de su mano portadora; (3) la mano secundaria va por IK a su objetivo en el espacio
    /// del objeto, con peso que baja a 0 cuando el objeto se aleja de su sitio del idle (equipar,
    /// enfundar); (4) los dedos se cierran una vez por contacto y se repiten (sin tembleque). El
    /// balanceo procedural del vendor va encima, en runtime, y mueve brazos y objeto como una pieza.
    ///
    /// Sólo se hornea la capa BASE del controller: las demás (la cuerda de la linterna) son acciones
    /// propias que siguen siendo de su horneador.
    /// </summary>
    internal static class HandInteractionBaker
    {
        public const string MarkerPrefix = "HandTarget.";

        internal sealed class Context : IDisposable
        {
            public HandInteractionProfile Profile;
            public GameObject Prefab;
            public GameObject Instance;
            public HandInteractionRig Rig;
            public HandSide Carrier;
            public readonly List<(AnimationClip original, AnimationClip effective)> Pairs = new();
            /// <summary>Clips de las OTRAS capas (la cuerda): sólo se rehornean cuando la portadora se rehace.</summary>
            public readonly List<(AnimationClip original, AnimationClip effective)> OtherLayerPairs = new();
            public AnimationClip IdleEffective;

            public void Dispose()
            {
                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
                if (Instance != null) Object.DestroyImmediate(Instance);
            }
        }

        /// <summary>Instancia oculta del wieldable, rig leído y clips efectivos de la capa base.</summary>
        public static Context Open(HandInteractionProfile profile)
        {
            if (profile.wieldablePrefab == null)
                throw new HandInteractionException("PREFAB_NOT_SET", "el perfil no apunta a ningún prefab de wieldable");
            var ctx = new Context { Profile = profile, Prefab = profile.wieldablePrefab };
            ctx.Instance = (GameObject)PrefabUtility.InstantiatePrefab(ctx.Prefab);
            // DESEMPAQUETADA: dentro de una instancia de prefab Unity NO deja cambiar el padre de un hijo (sólo lo
            // dice en el log), y rehacer la portadora necesita soltar el modelo de la mano. Medido: sin esto el
            // modelo seguía en Hand.R y toda la búsqueda medía la relación mano-objeto de siempre. Esta copia es
            // de medida; el prefab se escribe aparte con LoadPrefabContents.
            PrefabUtility.UnpackPrefabInstance(ctx.Instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            ctx.Instance.hideFlags = HideFlags.HideAndDontSave;
            ctx.Instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var t in ctx.Instance.GetComponentsInChildren<Transform>(true))
                if (t.name == HandInteractionRig.AnimatorNodeName || t.name == HandInteractionRig.RootBoneName)
                    t.gameObject.SetActive(true);
            try
            {
                // Ángulo de reposo de PRUEBA: antes de leer el rig, para que piezas, obstáculos y superficies lo vean.
                if (!string.IsNullOrEmpty(profile.sweptPartNodeName) && Mathf.Abs(profile.sweptPartTrialRestDegrees) > 1e-3f)
                {
                    var swept = ctx.Instance.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == profile.sweptPartNodeName)
                        ?? throw new HandInteractionException("SWEPT_PART_NOT_FOUND", $"el modelo no tiene la pieza que gira '{profile.sweptPartNodeName}'");
                    var axis = profile.sweptPartAxis.sqrMagnitude < 1e-8f ? Vector3.forward : profile.sweptPartAxis.normalized;
                    swept.localRotation *= Quaternion.AngleAxis(profile.sweptPartTrialRestDegrees, axis);
                }
                ctx.Rig = HandInteractionRig.Read(ctx.Instance, profile.modelNodeName, profile.gripMeshNodeName);
                if (!string.IsNullOrEmpty(profile.sweptPartNodeName))
                    ctx.Rig.AddSweptPart(profile.sweptPartNodeName, profile.sweptPartAxis, profile.sweptClearanceMeters,
                        profile.sweptPartMinY01, profile.sweptPartMaxY01);
                HandInteractionRig.MeasureCloseSigns(ctx.Rig.R);
                HandInteractionRig.MeasureCloseSigns(ctx.Rig.L);
                ctx.Carrier = IsDescendant(ctx.Rig.Node, ctx.Rig.R.Hand) ? ctx.Rig.R
                    : IsDescendant(ctx.Rig.Node, ctx.Rig.L.Hand) ? ctx.Rig.L : null;
                ReadPairs(ctx);
            }
            catch
            {
                ctx.Dispose();
                throw;
            }
            return ctx;
        }

        public static bool IsDescendant(Transform t, Transform ancestor)
        {
            for (var p = t; p != null; p = p.parent)
                if (p == ancestor) return true;
            return false;
        }

        private static void ReadPairs(Context ctx)
        {
            var wa = ctx.Instance.GetComponentInChildren<WieldableAnimator>(true)
                ?? throw new HandInteractionException("NO_WIELDABLE_ANIMATOR", "el prefab no tiene WieldableAnimator");
            var so = new SerializedObject(wa);
            var controller = so.FindProperty("_clips._controller")?.objectReferenceValue as AnimatorController
                ?? throw new HandInteractionException("NO_CONTROLLER", "el WieldableAnimator no tiene controller");

            var baseLayerClips = new HashSet<AnimationClip>();
            CollectClips(controller.layers[0].stateMachine, baseLayerClips);

            var pairs = so.FindProperty("_clips._clips");
            var overrides = new Dictionary<AnimationClip, AnimationClip>();
            for (int i = 0; pairs != null && i < pairs.arraySize; i++)
            {
                var e = pairs.GetArrayElementAtIndex(i);
                var o = e.FindPropertyRelative("Original").objectReferenceValue as AnimationClip;
                var v = e.FindPropertyRelative("Override").objectReferenceValue as AnimationClip;
                if (o != null && v != null) overrides[o] = v;
            }
            // Orden estable: el del controller (regla 13: nada sale de iterar un HashSet).
            foreach (var original in controller.animationClips.Distinct())
            {
                var effective = overrides.TryGetValue(original, out var ov) ? ov : original;
                if (!baseLayerClips.Contains(original))
                {
                    ctx.OtherLayerPairs.Add((original, effective));
                    continue;
                }
                ctx.Pairs.Add((original, effective));
                if (ctx.IdleEffective == null && original.name.ToLowerInvariant().Contains("idle")) ctx.IdleEffective = effective;
            }
            if (ctx.IdleEffective == null)
                throw new HandInteractionException("NO_IDLE_CLIP", "la capa base del controller no tiene un clip de idle");
        }

        private static void CollectClips(AnimatorStateMachine sm, HashSet<AnimationClip> into)
        {
            foreach (var s in sm.states) CollectMotion(s.state.motion, into);
            foreach (var child in sm.stateMachines) CollectClips(child.stateMachine, into);
        }

        private static void CollectMotion(Motion motion, HashSet<AnimationClip> into)
        {
            if (motion is AnimationClip clip) into.Add(clip);
            else if (motion is BlendTree tree)
                foreach (var c in tree.children) CollectMotion(c.motion, into);
        }

        // ─── Clips base ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// La fuente de cada clip efectivo. La PRIMERA vez se copia intacto a <c>outputFolder/Base</c>; las
        /// siguientes se lee esa copia. Así el resultado de un horneado nunca es la entrada del siguiente
        /// (muestrear lo propio apilaría desplazamientos, ADR-133 enm. 1).
        /// </summary>
        private static AnimationClip BaseFor(Context ctx, AnimationClip effective, bool write, List<string> warnings)
        {
            var p = ctx.Profile;
            int index = Array.IndexOf(p.bakedClips ?? Array.Empty<AnimationClip>(), effective);
            if (index >= 0 && index < p.baseClips.Length && p.baseClips[index] != null) return p.baseClips[index];
            // LA MARCA VA EN EL CLIP, no sólo en el perfil: si el perfil se perdió o es otro (revert, borrado, un
            // segundo perfil sobre el mismo wieldable), sin esto un horneado se tomaría por base limpia y el
            // siguiente apilaría IK sobre IK sin decir nada.
            if (IsBakedByTool(effective))
                throw new HandInteractionException("BASE_IS_BAKED",
                    $"'{AssetDatabase.GetAssetPath(effective)}' ya es un horneado de esta herramienta ({BakedBy(effective)}) y este perfil " +
                    "no tiene su base. Restaurar el clip original (git o su horneador propio) o usar el perfil que lo horneó.");
            if (!write) return effective; // previsualizar sin copiar: el efectivo aún no es nuestro

            string folder = $"{p.outputFolder}/Base";
            BackroomsEditorFolders.EnsureFolder(folder);
            string path = $"{folder}/{effective.name}_Base.anim";
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null)
            {
                warnings.Add($"BASE_REUSED: '{path}' ya existía y se usa como base de '{effective.name}'.");
                return existing;
            }
            var copy = Object.Instantiate(effective);
            copy.name = effective.name + "_Base";
            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }

        private const string BakedMarker = "HandInteraction:baked";

        internal static bool IsBakedByTool(AnimationClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            if (!path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) return false;
            var importer = AssetImporter.GetAtPath(path);
            return importer != null && (importer.userData ?? "").StartsWith(BakedMarker, StringComparison.Ordinal);
        }

        private static string BakedBy(AnimationClip clip)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip));
            string guid = (importer?.userData ?? "").Replace(BakedMarker + ":", "");
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? $"perfil {guid} que ya no existe" : $"perfil '{path}'";
        }

        /// <summary>Marca en el .meta del clip (userData del importador) qué perfil lo horneó.</summary>
        private static void MarkBaked(AnimationClip clip, HandInteractionProfile profile)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip));
            if (importer == null) return;
            string marker = BakedMarker + ":" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(profile));
            if (importer.userData == marker) return;
            importer.userData = marker;
            importer.SaveAndReimport();
        }

        /// <summary>Dónde se escribe el resultado: encima del clip efectivo si es un .anim del proyecto (conserva
        /// GUID y referencias), o un asset nuevo si el efectivo es del vendor o está dentro de un FBX.</summary>
        private static string OutputPathFor(Context ctx, AnimationClip effective, out bool inPlace)
        {
            string path = AssetDatabase.GetAssetPath(effective);
            inPlace = path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) &&
                      !path.StartsWith("Assets/PolymindGames/", StringComparison.Ordinal) &&
                      !path.Contains("/Base/");
            return inPlace ? path : $"{ctx.Profile.outputFolder}/{ctx.Prefab.name}_{effective.name}.anim";
        }

        // ─── Resolver ────────────────────────────────────────────────────────────────────────────

        internal sealed class Solved
        {
            public HandGripSolution Right, Left;
            /// <summary>La portadora se rehízo: el modelo cambia de offset bajo la mano y se hornean todas las capas.</summary>
            public bool CarrierRegrip;
            public HandGripSolution Carrier;
            public Vector3 IdleGripPosition;
            public Quaternion ObjectRotationInView;
            public string Log;
        }

        public static Solved Solve(Context ctx, AnimationClip idleBase)
        {
            var p = ctx.Profile;
            var rig = ctx.Rig;
            var log = new StringBuilder();
            AnimationMode.StartAnimationMode();
            rig.Sample(idleBase, 0f);

            if (rig.R.SignCheckFailed || rig.L.SignCheckFailed)
                log.AppendLine("AVISO SIGN_CHECK_FAILED: los signos de cierre del rig no cuadran con la palma medida.");

            var solved = new Solved
            {
                IdleGripPosition = rig.GripMesh.position,
                ObjectRotationInView = Quaternion.Inverse(rig.InstanceRoot.rotation) * rig.GripMesh.rotation,
            };
            log.AppendLine($"objeto: {rig.LengthMeters * 100f:0.0} cm de largo, radio medio {rig.RadiusAtAlong(0.5f) * 1000f:0} mm, " +
                           $"{rig.Parts.Count - 1} pieza(s) además de la malla de agarre; portadora {(ctx.Carrier != null ? ctx.Carrier.Suffix : "ninguna")}.");

            // Primero la portadora (o la derecha si no hay), después la otra: la segunda mide contra la primera.
            var first = ctx.Carrier ?? rig.R;
            var second = first == rig.R ? rig.L : rig.R;
            var firstSol = SolveHand(ctx, first, null, log);
            solved.CarrierRegrip = ctx.Carrier != null && first == ctx.Carrier && p.Hand(first.Right).role == HandRole.Regrip;
            if (solved.CarrierRegrip) solved.Carrier = firstSol;
            // Con la portadora rehecha la segunda se mide contra la mano NUEVA (SolveCarrierRegrip la deja puesta).
            var secondSol = SolveHand(ctx, second, first, log);
            // Deja la primera puesta otra vez (la búsqueda de la segunda restaura su fotograma base).
            if (firstSol?.Fingers != null) HandInteractionRig.WriteFingers(first, firstSol.Fingers);
            rig.ReattachNode();

            solved.Right = first.Right ? firstSol : secondSol;
            solved.Left = first.Right ? secondSol : firstSol;
            solved.Log = log.ToString();
            return solved;
        }

        private static HandGripSolution SolveHand(Context ctx, HandSide side, HandSide other, StringBuilder log)
        {
            var target = ctx.Profile.Hand(side.Right);
            // Cada mano agarra SU superficie (el pomo, el cuerpo); la portadora, siempre la principal.
            ctx.Rig.UseSurfaceFor(side == ctx.Carrier ? null : target);
            // Una secundaria que BUSCA agarre en otra cosa tampoco puede entrar en la órbita de lo que gira. La que agarra
            // la propia pieza, sí, y una copiada (Reference) puede venir de la cuerda, con la mano sobre el pomo. La
            // portadora rehecha activa la comprobación por su cuenta.
            bool orbit = side != ctx.Carrier && ctx.Rig.HasSweptParts && target.role == HandRole.Grip &&
                         target.gripPartNodeName != ctx.Profile.sweptPartNodeName;
            if (orbit) ctx.Rig.SweptCheckEnabled = true;
            try { return SolveHandOnSurface(ctx, side, other, target, log); }
            finally
            {
                if (orbit) ctx.Rig.SweptCheckEnabled = false;
                ctx.Rig.UseMainSurface();
            }
        }

        private static HandGripSolution SolveHandOnSurface(Context ctx, HandSide side, HandSide other, HandGripTarget target, StringBuilder log)
        {
            var rig = ctx.Rig;
            switch (target.role)
            {
                case HandRole.Keep:
                    log.AppendLine($"mano {side.Suffix}: se conserva la del clip base.");
                    return null;
                case HandRole.Regrip:
                    if (side != ctx.Carrier)
                        throw new HandInteractionException("ROLE_REGRIP_NOT_CARRIER",
                            $"Regrip es para la mano que lleva el objeto; la {side.Suffix} no lo lleva (usar Grip)");
                    return HandGripSolver.SolveCarrierRegrip(rig, side, target, ctx.Profile.maxShoulderShiftMeters, other, log);
                case HandRole.Reference:
                    if (side == ctx.Carrier)
                        throw new HandInteractionException("ROLE_REFERENCE_CARRIER",
                            $"la mano {side.Suffix} lleva el objeto: copiar su pose de otro clip lo arrastraría. Usar Keep.");
                    return HandGripSolver.SolveFromReference(rig, side, target, other, log);
                case HandRole.Relaxed:
                    HandGripSolver.RelaxFingers(side);
                    log.AppendLine($"mano {side.Suffix}: dedos relajados sobre el brazo del clip base.");
                    return new HandGripSolution { Right = side.Right, Fingers = HandInteractionRig.ReadFingers(side) };
                default:
                    if (side == ctx.Carrier) return HandGripSolver.SolveCarrierFingers(rig, side, target, log);
                    if (ctx.Profile.kind == HandInteractionKind.OneHand)
                        throw new HandInteractionException("ROLE_KIND_MISMATCH",
                            $"perfil de UNA mano con la mano {side.Suffix} (no portadora) en Grip: usa TwoHand o Relaxed");
                    // Si la otra mano se conserva, sus dedos son los del clip: cuentan igual para no pisarse.
                    return HandGripSolver.SolveSecondary(rig, side, target, ctx.Profile.maxShoulderShiftMeters, other, log);
            }
        }

        // ─── Hornear ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Con <paramref name="write"/> en false no toca ningún asset: resuelve, mide y devuelve la
        /// instancia posada en el idle recién construido (para capturas).</summary>
        public static HandBakeResult Bake(HandInteractionProfile profile, bool write, Action<Context> posedPreview = null, bool force = false)
        {
            var result = new HandBakeResult();
            if (write && Mathf.Abs(profile.sweptPartTrialRestDegrees) > 1e-3f)
                throw new HandInteractionException("REST_TRIAL_NOT_APPLIED",
                    $"sweptPartTrialRestDegrees = {profile.sweptPartTrialRestDegrees:0.#}: es un reposo de PRUEBA que el modelo no tiene. " +
                    "Aplicarlo con el horneador del objeto y volver a 0 antes de hornear.");
            if (Mathf.Abs(profile.sweptPartTrialRestDegrees) > 1e-3f)
                result.warnings.Add($"REST_TRIAL: '{profile.sweptPartNodeName}' medida girada {profile.sweptPartTrialRestDegrees:0.#}° sobre su reposo.");
            using var ctx = Open(profile);
            if (string.IsNullOrEmpty(profile.outputFolder))
                throw new HandInteractionException("OUTPUT_FOLDER_NOT_SET", "el perfil no tiene outputFolder");
            if (write) BackroomsEditorFolders.EnsureFolder(profile.outputFolder);

            bool regripRequested = ctx.Carrier != null && profile.Hand(ctx.Carrier.Right).role == HandRole.Regrip;
            // El offset del modelo bajo la portadora es ENTRADA de la búsqueda, como los clips base, y un Regrip lo
            // reescribe en el prefab. Se busca siempre con el ORIGINAL: medido, tras hornear un Regrip la misma pose
            // (coste 4,46) pasó a costar 186 porque el objeto se colocaba con el brazo base y el offset nuevo, y cinco
            // iteraciones de ajuste persiguieron un objeto que estaba en otro sitio.
            Vector3 prefabNodePos = ctx.Rig.Node.localPosition;
            Quaternion prefabNodeRot = ctx.Rig.Node.localRotation;
            if (profile.hasBaseNodeLocal)
            {
                ctx.Rig.Node.localPosition = profile.baseNodeLocalPosition;
                ctx.Rig.Node.localRotation = profile.baseNodeLocalRotation;
            }
            var baseLayerEffective = new HashSet<AnimationClip>(ctx.Pairs.Select(pr => pr.effective));
            bool followRequested = ctx.Carrier != null && Follows(profile, profile.Hand(!ctx.Carrier.Right));
            // Con la portadora rehecha también se hornean las demás capas (la cuerda), o durante la acción el objeto
            // volvería a colgar del offset viejo; si un Regrip anterior ya las tocó, también, para devolverlas a su
            // base al quitarlo; y si una mano sigue la pieza que gira, su clip de acción es suyo.
            // Orden estable: capa base primero, en el orden del controller.
            var distinct = ctx.Pairs.Select(pr => pr.effective)
                .Concat(regripRequested || profile.hasBaseNodeLocal || followRequested
                    ? ctx.OtherLayerPairs.Select(pr => pr.effective) : Enumerable.Empty<AnimationClip>())
                .Distinct().ToList();
            // UN CLIP QUE NO ANIMA EL BRAZO DE LA PORTADORA NO SE HORNEA. Los `Template_Attack` del vendor son plantillas casi
            // vacías: horneados, el ataque salía con 64–81° de brazo inventado a partir de lo que quedara del muestreo anterior
            // (medido en el destornillador). Se quedan con su override de siempre.
            if (ctx.Carrier != null)
            {
                string[] armBones = { ctx.Carrier.Upper.name, ctx.Carrier.Fore.name, ctx.Carrier.Hand.name };
                var skipped = distinct.Where(c => c != ctx.IdleEffective && !AnimationUtility.GetCurveBindings(c)
                    .Any(b => armBones.Any(n => b.path.EndsWith("/" + n) || b.path == n))).ToList();
                foreach (var c in skipped)
                {
                    distinct.Remove(c);
                    result.warnings.Add($"CLIP_WITHOUT_ARM: '{c.name}' no anima el brazo de la portadora: no se hornea.");
                }
            }
            if (followRequested && !distinct.Any(c => IsActionClip(ctx, c)))
                result.warnings.Add($"SWEPT_ACTION_CLIP_NOT_FOUND: ningún clip del controller es '{profile.sweptPartActionClipPath}': " +
                                    "la mano agarra la pieza en reposo pero no la sigue al girar.");
            // Se resuelve SIN copiar: antes del primer horneado el clip efectivo ES la base. La copia se hace
            // sólo si el veredicto deja escribir (antes quedaba un Base/ huérfano tras un NO_NATURAL_GRIP).
            var bases = distinct.Select(e => BaseFor(ctx, e, false, result.warnings)).ToList();
            var idleBase = bases[distinct.IndexOf(ctx.IdleEffective)];

            var solved = Solve(ctx, idleBase);
            var report = new StringBuilder(solved.Log);
            if (solved.Right?.Metrics != null) result.hands.Add(solved.Right.Metrics);
            if (solved.Left?.Metrics != null) result.hands.Add(solved.Left.Metrics);

            // EL VEREDICTO: la búsqueda siempre devuelve SU mejor pose, aunque ninguna sea natural. Escribir eso
            // en el prefab es justo lo que no se quiere; se dice por qué y qué cambiar.
            foreach (var sol in new[] { solved.Right, solved.Left })
            {
                if (sol == null || !sol.HasArmPose) continue;
                string msg;
                if (profile.Hand(sol.Right).role == HandRole.Reference)
                {
                    // Una pose COPIADA ya la validó su horneador: sólo la frenan los fallos duros de reproducirla aquí
                    // (el hombro adelantado de un rig 1P sin torso no se ve y no cuenta).
                    var m = sol.Metrics;
                    bool hard = m.reachClamped || m.handOverlapMm < 15f || m.targetErrorMm > 5f ||
                                m.costBreakdown.Any(c => c.StartsWith("muñeca-tope") || c.StartsWith("antebrazo-tope"));
                    if (!hard) continue;
                    msg = $"mano {(sol.Right ? "R" : "L")}: la pose de referencia no se reproduce bien: alcance recortado {m.reachClamped}, " +
                          $"manos a {m.handOverlapMm:0} mm, desvío {m.targetErrorMm:0.0} mm [{string.Join(", ", m.costBreakdown)}].";
                }
                else
                {
                    if (sol.Metrics.cost <= profile.maxNaturalCost) continue;
                    msg = $"mano {(sol.Right ? "R" : "L")}: la mejor pose cuesta {sol.Metrics.cost:0.0} (tope {profile.maxNaturalCost:0.0}): " +
                          $"{string.Join(", ", sol.Metrics.costBreakdown)}. Opciones: kind OneHand; mover la portadora con su horneador " +
                          "para dejar sitio; o bake con force=true si se acepta tal cual.";
                }
                result.warnings.Add("NO_NATURAL_GRIP: " + msg);
                if (write && !force)
                {
                    AnimationMode.StopAnimationMode();
                    throw new HandInteractionException("NO_NATURAL_GRIP", msg + "\n" + report);
                }
            }

            var built = new List<AnimationClip>();
            for (int i = 0; i < distinct.Count; i++)
                built.Add(BuildClip(ctx, bases[i], solved, report, secondaryToo: baseLayerEffective.Contains(distinct[i]),
                    actionClip: IsActionClip(ctx, distinct[i])));

            if (!write)
            {
                // Se cierra AnimationMode (lo abrió Solve) y se posa con el clip recién construido.
                AnimationMode.StopAnimationMode();
                var idleBuilt = built[distinct.IndexOf(ctx.IdleEffective)];
                idleBuilt.SampleAnimation(ctx.Rig.Animator.gameObject, 0f);
                ctx.Rig.Animator.localPosition = Vector3.zero;
                ctx.Rig.Animator.localRotation = Quaternion.identity;
                if (solved.CarrierRegrip)
                {
                    // El offset nuevo del modelo sólo vive en el prefab al escribir: en la previsualización se pone a mano.
                    ctx.Rig.Node.localPosition = solved.Carrier.NodeLocalPos;
                    ctx.Rig.Node.localRotation = solved.Carrier.NodeLocalRot;
                }
                posedPreview?.Invoke(ctx);
                foreach (var c in built) Object.DestroyImmediate(c);
                StoreResolved(profile, solved, persist: false);
                result.report = report.ToString();
                return result;
            }

            // Escritura: bases (sólo la primera vez), clips, overrides, marcadores, perfil.
            AnimationMode.StopAnimationMode();
            bases = distinct.Select(e => BaseFor(ctx, e, true, result.warnings)).ToList();
            var baked = new AnimationClip[distinct.Count];
            var remap = new Dictionary<AnimationClip, AnimationClip>();
            for (int i = 0; i < distinct.Count; i++)
            {
                string path = OutputPathFor(ctx, distinct[i], out bool inPlace);
                string name = Path.GetFileNameWithoutExtension(path);
                baked[i] = HandInteractionRig.SaveClip(built[i], path, name);
                remap[distinct[i]] = baked[i];
                result.clips.Add(path + (inPlace ? " (encima del clip existente)" : " (nuevo)"));
            }
            // Base y horneados se registran (y los clips se MARCAN) ANTES del prefab: si WritePrefab falla, los clips
            // ya están escritos encima y el siguiente intento tiene que saber de dónde partir.
            foreach (var clip in baked) MarkBaked(clip, profile);
            profile.baseClips = bases.ToArray();
            profile.bakedClips = baked;
            if (solved.CarrierRegrip && !profile.hasBaseNodeLocal)
            {
                profile.hasBaseNodeLocal = true;
                profile.baseNodeLocalPosition = prefabNodePos;
                profile.baseNodeLocalRotation = prefabNodeRot;
            }
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            WritePrefab(ctx, remap, solved);

            // Sin Regrip, WritePrefab ya devolvió el offset base y las capas de acción salieron de su base: la copia
            // deja de hacer falta (si no, esas capas se rehornearían para siempre).
            if (!solved.CarrierRegrip) profile.hasBaseNodeLocal = false;
            profile.lastBakeUtc = DateTime.UtcNow.ToString("o");
            StoreResolved(profile, solved, persist: true);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            result.written = true;
            result.report = report.ToString();
            return result;
        }

        private static void StoreResolved(HandInteractionProfile profile, Solved solved, bool persist)
        {
            void Store(HandGripTarget t, HandGripSolution s)
            {
                if (s == null || s.Metrics == null) { t.resolved = false; return; }
                t.resolved = true;
                t.resolvedAlongAxis = s.HasArmPose ? s.Along : t.alongAxis;
                t.resolvedIndexTowardTip = s.HasArmPose ? s.IndexTowardTip : t.indexTowardTip;
                t.resolvedClockDegrees = s.Clock;
                t.resolvedTiltDegrees = s.Tilt;
                t.resolvedPalmOffsetMeters = s.PalmOffset;
                t.resolvedCost = s.Metrics.cost;
                t.resolvedShoulderShift = s.ShoulderShiftWorld;
                t.resolvedPole = s.Pole;
            }
            Store(profile.rightHand, solved.Right);
            Store(profile.leftHand, solved.Left);
            profile.resolvedObjectRotationInView = solved.ObjectRotationInView;
            profile.hasResolvedView = true;
            if (persist) EditorUtility.SetDirty(profile);
        }

        /// <summary>
        /// Un clip resuelto a partir de su base. La mano secundaria agarra con peso completo mientras el objeto
        /// está cerca de su sitio del idle y vuelve al clip base a medida que se aleja: así equipar y enfundar
        /// traen y se llevan la mano con el objeto sin estirar el brazo hasta fuera de la pantalla.
        /// </summary>
        /// <summary>Lo más que la mano que sigue la pieza se desliza por ella hacia fuera, en metros.</summary>
        private const float MaxFollowSlide = 0.012f;

        /// <summary>La mano agarra la pieza que gira y el perfil nombra su clip de acción: la sigue en ese clip.</summary>
        private static bool Follows(HandInteractionProfile p, HandGripTarget t)
            => !string.IsNullOrEmpty(p.sweptPartActionClipPath) && !string.IsNullOrEmpty(p.sweptPartNodeName) &&
               t.role == HandRole.Grip && t.gripPartNodeName == p.sweptPartNodeName;

        private static bool IsActionClip(Context ctx, AnimationClip effective)
        {
            string want = ctx.Profile.sweptPartActionClipPath;
            if (string.IsNullOrEmpty(want) || effective == null) return false;
            return ctx.OtherLayerPairs.Concat(ctx.Pairs).Any(pr => pr.effective == effective &&
                (AssetDatabase.GetAssetPath(pr.effective) == want || AssetDatabase.GetAssetPath(pr.original) == want));
        }

        private static AnimationClip BuildClip(Context ctx, AnimationClip source, Solved solved, StringBuilder report, bool secondaryToo,
            bool actionClip = false)
        {
            var rig = ctx.Rig;
            var p = ctx.Profile;
            // EL CLIP DE ACCIÓN GIRA LA PIEZA con su fase, como en runtime (`reposo · AngleAxis(fase · 360°)`), y la mano que
            // la agarra va con el centro de lo que agarra SIN girar con ella: el pomo rueda dentro de los dedos. El
            // horneador propio de la linterna fijaba una orientación de mano por constantes y dejaba la muñeca a ~80° de
            // desviación en TODA la vuelta (medido en 8 fases); aquí la orientación es la del agarre natural en reposo.
            bool action = actionClip && rig.SweptTransform != null;
            float worstFollowCost = -1f, maxFollowRoll = 0f, maxFollowSlide = 0f;
            string worstFollow = "";
            // Quien abre AnimationMode lo cierra: aquí sólo si no venía abierto (Solve lo deja abierto).
            bool ownsAnimationMode = !AnimationMode.InAnimationMode();
            if (ownsAnimationMode) AnimationMode.StartAnimationMode();
            bool loop = AnimationUtility.GetAnimationClipSettings(source).loopTime;
            int frames = Mathf.Max(1, Mathf.RoundToInt(source.length * HandInteractionRig.FrameRate));
            var list = new List<HandFrame>(frames + 1);
            int clamps = 0;
            float minWeight = 1f, maxWeight = 0f, maxNodeDriftMm = 0f;
            var carrierSol = ctx.Carrier == null ? null : ctx.Carrier.Right ? solved.Right : solved.Left;

            void PoseFrame(int k, float t)
            {
                rig.Sample(source, loop && k == frames ? 0f : t);
                if (action)
                    rig.SweptTransform.localRotation = rig.SweptRest *
                        Quaternion.AngleAxis(360f * (loop && k == frames ? 0f : t) / Mathf.Max(1e-4f, source.length), rig.SweptAxisL);
            }

            // EL GIRO Y EL DESLIZAMIENTO DE LA MANO QUE SIGUE LA PIEZA se eligen en un pase previo y se SUAVIZAN sobre la
            // vuelta antes de aplicarlos. Elegidos fotograma a fotograma alternaban entre opciones de coste parecido: la mano
            // giraba 10–39° por fotograma (medido en el clip de cuerda).
            HandSide followSide = null;
            HandGripSolution followSol = null;
            HandGripTarget followTarget = null;
            float[] plannedRoll = null, plannedSlide = null;
            if (action && ctx.Carrier != null)
            {
                bool otherRight = !ctx.Carrier.Right;
                followSol = otherRight ? solved.Right : solved.Left;
                followTarget = p.Hand(otherRight);
                if (followSol != null && followSol.HasArmPose && Follows(p, followTarget)) followSide = rig.Side(otherRight);
            }
            if (followSide != null)
            {
                plannedRoll = new float[frames + 1];
                plannedSlide = new float[frames + 1];
                float prevRoll = 0f, prevSlide = 0f;
                for (int k = 0; k <= frames; k++)
                {
                    PoseFrame(k, Mathf.Min(source.length, k / HandInteractionRig.FrameRate));
                    if (solved.CarrierRegrip && carrierSol != null) PoseCarrierRegrip(ctx, ctx.Carrier, carrierSol, out _, out _);
                    var place = FollowPlacer(ctx, followSide, followSol, followTarget, !ctx.Carrier.Right);
                    float bestScore = float.MaxValue;
                    // Tope de 12 mm: deslizando hasta 18 mm el pomo quedaba a 21 mm del centro de los dedos cerrados
                    // (WhileCrankingTheLeftHandRidesTheKnob exige 20).
                    for (float roll = -90f; roll <= 90.01f; roll += 5f)
                    for (float slide = 0f; slide <= MaxFollowSlide + 1e-4f; slide += 0.003f)
                    {
                        var cm = place(roll, slide);
                        float score = cm.cost + Mathf.Pow((roll - prevRoll) / 30f, 2f) + Mathf.Pow(roll / 90f, 2f) * 0.5f +
                                      Mathf.Pow((slide - prevSlide) / 0.006f, 2f) + slide / 0.015f * 0.3f;
                        if (score < bestScore) { bestScore = score; plannedRoll[k] = roll; plannedSlide[k] = slide; }
                    }
                    prevRoll = plannedRoll[k];
                    prevSlide = plannedSlide[k];
                    rig.UseMainSurface();
                    rig.ReattachNode();
                }
                SmoothSeries(plannedRoll, loop, 4);
                SmoothSeries(plannedSlide, loop, 4);

                // Suavizar aparta la mano de lo elegido y puede acercarla a la otra (medido: manos a ~6 mm a fase 0,67). Segundo
                // pase: se reelige CERCA de lo suavizado, con correa corta, y se suaviza otra vez, más ligero.
                var refinedRoll = new float[frames + 1];
                var refinedSlide = new float[frames + 1];
                for (int k = 0; k <= frames; k++)
                {
                    PoseFrame(k, Mathf.Min(source.length, k / HandInteractionRig.FrameRate));
                    if (solved.CarrierRegrip && carrierSol != null) PoseCarrierRegrip(ctx, ctx.Carrier, carrierSol, out _, out _);
                    var place = FollowPlacer(ctx, followSide, followSol, followTarget, !ctx.Carrier.Right);
                    float r0 = plannedRoll[k], s0 = plannedSlide[k], bestScore = float.MaxValue;
                    refinedRoll[k] = r0;
                    refinedSlide[k] = s0;
                    for (float dr = -20f; dr <= 20.01f; dr += 2.5f)
                    for (float ds = -0.003f; ds <= 0.00301f; ds += 0.001f)
                    {
                        float roll = Mathf.Clamp(r0 + dr, -90f, 90f), slide = Mathf.Clamp(s0 + ds, 0f, MaxFollowSlide);
                        var cm = place(roll, slide);
                        float score = cm.cost + Mathf.Pow(dr / 10f, 2f) + Mathf.Pow(ds / 0.002f, 2f);
                        if (score < bestScore) { bestScore = score; refinedRoll[k] = roll; refinedSlide[k] = slide; }
                    }
                    rig.UseMainSurface();
                    rig.ReattachNode();
                }
                SmoothSeries(refinedRoll, loop, 2);
                SmoothSeries(refinedSlide, loop, 2);
                plannedRoll = refinedRoll;
                plannedSlide = refinedSlide;
            }

            // LA TORSIÓN DEL ANTEBRAZO ES CONTINUA dentro del clip. Medida suelta en (−180°, 180°], al cruzar ±180° cambia de
            // signo y los huesos de torsión dan media vuelta en un fotograma: medido 160° en ForearmTwist.3.L dos veces por
            // vuelta de cuerda y 164° al equipar. La mano no se movía; el antebrazo temblaba.
            HandInteractionRig.BeginTwistContinuity(rig.R);
            HandInteractionRig.BeginTwistContinuity(rig.L);

            for (int k = 0; k <= frames; k++)
            {
                float t = Mathf.Min(source.length, k / HandInteractionRig.FrameRate);
                PoseFrame(k, t);
                float distance = (rig.GripMesh.position - solved.IdleGripPosition).magnitude;
                float proximity = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(p.secondaryFullWeightDistance, p.secondaryZeroWeightDistance, distance));

                // La portadora primero: si se rehace, el modelo se suelta y se queda donde este clip lo lleva.
                bool carrierRight = ctx.Carrier == null || ctx.Carrier.Right;
                foreach (bool right in new[] { carrierRight, !carrierRight })
                {
                    var side = rig.Side(right);
                    var sol = right ? solved.Right : solved.Left;
                    var target = p.Hand(right);
                    if (sol == null || target.role == HandRole.Keep) continue;

                    if (solved.CarrierRegrip && side == ctx.Carrier)
                    {
                        if (PoseCarrierRegrip(ctx, side, sol, out bool clamped, out float driftMm) && clamped) clamps++;
                        maxNodeDriftMm = Mathf.Max(maxNodeDriftMm, driftMm);
                        continue;
                    }

                    if (side == followSide)
                    {
                        var place = FollowPlacer(ctx, side, sol, target, right);
                        float roll = plannedRoll[k], slide = plannedSlide[k];
                        maxFollowRoll = Mathf.Max(maxFollowRoll, Mathf.Abs(roll));
                        maxFollowSlide = Mathf.Max(maxFollowSlide, slide);
                        var fm = place(roll, slide);
                        if (fm.reachClamped) clamps++;
                        if (fm.cost > worstFollowCost)
                        {
                            worstFollowCost = fm.cost;
                            worstFollow = $"fase {k / (float)frames:0.00} (giro {roll:+0;-0}°, fuera {slide * 1000f:0} mm): [{string.Join(", ", fm.costBreakdown)}]";
                        }
                        rig.UseMainSurface();
                        continue;
                    }

                    if (target.role == HandRole.Relaxed || side == ctx.Carrier)
                    {
                        if (secondaryToo || side == ctx.Carrier) BlendFingers(side, sol.Fingers, target.weight);
                        continue;
                    }
                    if (!secondaryToo) continue; // la otra mano de una capa de acción (la cuerda) es de su horneador

                    float w = target.weight * proximity;
                    minWeight = Mathf.Min(minWeight, w); maxWeight = Mathf.Max(maxWeight, w);
                    if (w <= 1e-4f) continue;

                    Vector3 baseShoulder = side.Upper.position, baseElbow = side.Fore.position, baseHand = side.Hand.position;
                    Quaternion baseRot = side.Hand.rotation;
                    Vector3 gripPos = rig.GripMesh.TransformPoint(sol.HandPosInGrip);
                    Quaternion gripRot = rig.GripMesh.rotation * sol.HandRotInGrip;
                    Vector3 pole = sol.Pole == 3 ? sol.PoleWorld : HandGripSolver.PoleFor(rig, side, sol.Pole, baseElbow, baseShoulder, baseHand);

                    side.Upper.position = baseShoulder + sol.ShoulderShiftWorld * w;
                    if (!HandInteractionRig.SolveTwoBone(side, Vector3.Lerp(baseHand, gripPos, w), Quaternion.Slerp(baseRot, gripRot, w), pole)
                        && w > 0.99f) clamps++;
                    HandInteractionRig.DistributeForearmTwist(side);
                    BlendFingers(side, sol.Fingers, w);
                }
                list.Add(rig.Capture(t));
                rig.ReattachNode(); // el siguiente muestreo tiene que llevar el modelo con el offset VIEJO
            }
            HandInteractionRig.EndTwistContinuity(rig.R);
            HandInteractionRig.EndTwistContinuity(rig.L);
            if (action) rig.SweptTransform.localRotation = rig.SweptRest;
            if (ownsAnimationMode) AnimationMode.StopAnimationMode();
            if (worstFollowCost >= 0f)
                report.AppendLine($"clip '{source.name}': la mano sigue a '{p.sweptPartNodeName}' en la vuelta (giro máximo sobre su eje " +
                                  $"{maxFollowRoll:0}°, deslizada hasta {maxFollowSlide * 1000f:0} mm hacia fuera); peor coste de brazo " +
                                  $"{worstFollowCost:0.00} en la {worstFollow}");
            report.AppendLine($"clip '{source.name}': {source.length:0.00} s{(loop ? ", bucle" : "")}; peso secundaria {minWeight:0.00}–{maxWeight:0.00}; " +
                              $"alcance recortado con peso completo en {clamps} fotograma(s)" +
                              (solved.CarrierRegrip ? $"; el modelo se aparta como mucho {maxNodeDriftMm:0.0} mm de su offset nuevo." : "."));
            if (solved.CarrierRegrip && maxNodeDriftMm > 5f)
                report.AppendLine($"AVISO REGRIP_DRIFT: en '{source.name}' el objeto se separa hasta {maxNodeDriftMm:0.0} mm de la mano rehecha " +
                                  "(el brazo no llega a donde el clip lleva el objeto): subir maxShoulderShiftMeters o acercar la pose al agarre original.");
            return rig.BuildClip(list, source.length, loop);
        }

        /// <summary>La portadora rehecha en el fotograma ya muestreado: suelta el modelo y va por IK a su pose en él.
        /// Devuelve true; <paramref name="clamped"/> dice si el brazo no llegó.</summary>
        private static bool PoseCarrierRegrip(Context ctx, HandSide side, HandGripSolution sol, out bool clamped, out float driftMm)
        {
            var rig = ctx.Rig;
            rig.DetachNode();
            Vector3 cShoulder = side.Upper.position, cElbow = side.Fore.position, cHand = side.Hand.position;
            Vector3 target = rig.GripMesh.TransformPoint(sol.HandPosInGrip);
            // Con la mano nueva en otro sitio del objeto, en los clips de brazo estirado (los ataques del vendor) el hombro del
            // clip ya no llega: el destornillador se separaba 17–19 mm de la mano en 12–31 fotogramas (medido). Se adelanta
            // lo justo, con el mismo tope que la búsqueda.
            Vector3 shoulder = cShoulder + sol.ShoulderShiftWorld;
            side.Upper.position = shoulder;
            float reach = (side.Fore.position - side.Upper.position).magnitude + (side.Hand.position - side.Fore.position).magnitude - 0.004f;
            Vector3 to = target - shoulder;
            if (to.magnitude > reach)
                shoulder += to.normalized * Mathf.Min(ctx.Profile.maxShoulderShiftMeters, to.magnitude - reach + 0.002f);
            side.Upper.position = shoulder;
            clamped = !HandInteractionRig.SolveTwoBone(side, target,
                rig.GripMesh.rotation * sol.HandRotInGrip, HandGripSolver.PoleFor(rig, side, sol.Pole, cElbow, cShoulder, cHand));
            HandInteractionRig.DistributeForearmTwist(side);
            HandInteractionRig.WriteFingers(side, sol.Fingers);
            // Si el IK es exacto, el offset del modelo bajo la mano es el mismo en todos los fotogramas.
            driftMm = (side.Hand.InverseTransformPoint(rig.Node.position) - sol.NodeLocalPos).magnitude * side.Hand.lossyScale.x * 1000f;
            return true;
        }

        /// <summary>
        /// Coloca la mano que sigue la pieza que gira, en el fotograma ya muestreado, con un giro sobre el eje del pomo y un
        /// deslizamiento hacia fuera a lo largo de él (ninguno cambia el contacto de los dedos: el pomo es un cilindro sobre
        /// ese eje), el hombro adelantado lo justo si no llega, y la mide. Deja activa la superficie de la pieza.
        /// </summary>
        private static Func<float, float, HandGripMetrics> FollowPlacer(Context ctx, HandSide side, HandGripSolution sol, HandGripTarget target, bool right)
        {
            var rig = ctx.Rig;
            var p = ctx.Profile;
            rig.UseSurfaceFor(target);
            Vector3 knob = rig.AxisPoint(sol.Along);
            Vector3 spin = rig.Axis;
            Vector3 delta = knob - rig.GripMesh.TransformPoint(sol.PartPointInGrip);
            Vector3 restPos = rig.GripMesh.TransformPoint(sol.HandPosInGrip) + delta;
            Quaternion restRot = rig.GripMesh.rotation * sol.HandRotInGrip;
            Vector3 baseShoulder = rig.InstanceRoot.TransformPoint(sol.ShoulderInRoot);
            var work = JsonUtility.FromJson<HandGripTarget>(JsonUtility.ToJson(target));
            work.alongAxis = sol.Along;
            // Hacia FUERA del cuerpo: con el giro solo, la palma seguía 28 mm dentro a fase 0,23 (medido).
            Vector3 outward = rig.MainSurface.Gap(knob + spin * 0.01f) >= rig.MainSurface.Gap(knob - spin * 0.01f) ? spin : -spin;
            return (roll, slide) =>
            {
                var r = Quaternion.AngleAxis(roll, spin);
                Vector3 handTarget = knob + r * (restPos - knob) + outward * slide;
                Vector3 shoulder = baseShoulder;
                side.Upper.position = shoulder;
                float reach = (side.Fore.position - side.Upper.position).magnitude + (side.Hand.position - side.Fore.position).magnitude - 0.01f;
                Vector3 to = handTarget - shoulder;
                if (to.magnitude > reach) shoulder += to.normalized * Mathf.Min(p.maxShoulderShiftMeters, to.magnitude - reach + 0.005f);
                side.Upper.position = shoulder;
                bool ok = HandInteractionRig.SolveTwoBone(side, handTarget, r * restRot, sol.FollowPoleWorld);
                HandInteractionRig.DistributeForearmTwist(side);
                HandInteractionRig.WriteFingers(side, sol.Fingers);
                return HandGripSolver.Measure(rig, side, work, sol.Clock, !ok, shoulder - baseShoulder, 0f, rig.Side(!right),
                    checkFingers: false, checkView: false, checkBodyContact: false);
            };
        }

        /// <summary>Media gaussiana (radio <paramref name="radius"/> fotogramas, sigma la mitad) de una serie por fotograma; en
        /// bucle, circular (el último fotograma repite el primero).</summary>
        private static void SmoothSeries(float[] v, bool loop, int radius)
        {
            int n = loop ? v.Length - 1 : v.Length;
            if (n < 3 || radius < 1) return;
            var src = (float[])v.Clone();
            float twoSigma2 = 2f * Mathf.Pow(radius * 0.5f, 2f);
            for (int i = 0; i < n; i++)
            {
                float sum = 0f, wsum = 0f;
                for (int d = -radius; d <= radius; d++)
                {
                    int j = i + d;
                    if (loop) j = ((j % n) + n) % n;
                    else if (j < 0 || j >= n) continue;
                    float w = Mathf.Exp(-(d * d) / twoSigma2);
                    sum += src[j] * w;
                    wsum += w;
                }
                v[i] = sum / wsum;
            }
            if (loop) v[n] = v[0];
        }

        private static void BlendFingers(HandSide side, Quaternion[][] target, float w)
        {
            if (target == null) return;
            for (int f = 0; f < side.Fingers.Length; f++)
                for (int j = 0; j < side.Fingers[f].Length; j++)
                    side.Fingers[f][j].localRotation = Quaternion.Slerp(side.Fingers[f][j].localRotation, target[f][j], Mathf.Clamp01(w));
        }

        /// <summary>
        /// Overrides que apuntaban al clip viejo → al horneado (los que se escribieron encima ya apuntan), y los
        /// marcadores <c>HandTarget.R/L</c> bajo la malla de agarre, con tag EditorOnly: sirven para ver y
        /// localizar el objetivo en el prefab; la fuente de verdad sigue siendo el perfil.
        /// </summary>
        private static void WritePrefab(Context ctx, Dictionary<AnimationClip, AnimationClip> remap, Solved solved)
        {
            string prefabPath = AssetDatabase.GetAssetPath(ctx.Prefab);
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var wa = root.GetComponentInChildren<WieldableAnimator>(true);
                var so = new SerializedObject(wa);
                var controller = (AnimatorController)so.FindProperty("_clips._controller").objectReferenceValue;
                var pairs = so.FindProperty("_clips._clips");
                var originals = controller.animationClips;
                // La lista tiene que tener EXACTAMENTE controller.animationClips.Length pares, sin deduplicar, o el
                // inspector del vendor la regenera vacía (ADR-133 enm. 1). Se conserva lo que había.
                var current = new Dictionary<int, AnimationClip>();
                for (int i = 0; i < pairs.arraySize; i++)
                    current[i] = pairs.GetArrayElementAtIndex(i).FindPropertyRelative("Override").objectReferenceValue as AnimationClip;
                pairs.arraySize = originals.Length;
                for (int i = 0; i < originals.Length; i++)
                {
                    var e = pairs.GetArrayElementAtIndex(i);
                    e.FindPropertyRelative("Original").objectReferenceValue = originals[i];
                    current.TryGetValue(i, out var over);
                    var effective = over != null ? over : originals[i];
                    if (remap.TryGetValue(effective, out var baked) && baked != effective)
                        e.FindPropertyRelative("Override").objectReferenceValue = baked;
                    else
                        e.FindPropertyRelative("Override").objectReferenceValue = over;
                }
                so.ApplyModifiedPropertiesWithoutUndo();

                if (solved.CarrierRegrip)
                {
                    // Un solo escritor del nodo (ADR-077 enm. 5): con la portadora rehecha, el offset lo pone este horneado.
                    var node = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == ctx.Profile.modelNodeName)
                        ?? throw new HandInteractionException("MODEL_NODE_NOT_FOUND", "el prefab ha perdido el nodo del modelo");
                    node.localPosition = solved.Carrier.NodeLocalPos;
                    node.localRotation = solved.Carrier.NodeLocalRot;
                }
                else if (ctx.Profile.hasBaseNodeLocal)
                {
                    // Se quitó el Regrip: los clips vuelven a salir de su base, el offset también.
                    var node = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == ctx.Profile.modelNodeName)
                        ?? throw new HandInteractionException("MODEL_NODE_NOT_FOUND", "el prefab ha perdido el nodo del modelo");
                    node.localPosition = ctx.Profile.baseNodeLocalPosition;
                    node.localRotation = ctx.Profile.baseNodeLocalRotation;
                }

                Transform grip = root.GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(t => t.name == (string.IsNullOrEmpty(ctx.Profile.gripMeshNodeName) ? ctx.Rig.GripMesh.name : ctx.Profile.gripMeshNodeName)
                                         && IsUnderNamed(t, ctx.Profile.modelNodeName));
                if (grip != null)
                {
                    WriteMarker(grip, "R", solved.Right);
                    WriteMarker(grip, "L", solved.Left);
                }
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool ok);
                if (!ok) throw new HandInteractionException("PREFAB_SAVE_FAILED", $"Unity no guardó '{prefabPath}' (mirar Editor.log)");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static bool IsUnderNamed(Transform t, string name)
        {
            for (var p = t; p != null; p = p.parent)
                if (p.name == name) return true;
            return false;
        }

        private static void WriteMarker(Transform grip, string suffix, HandGripSolution sol)
        {
            var existing = grip.Find(MarkerPrefix + suffix);
            // `Quaternion ==` de Unity compara por producto escalar: con un cuaternión a cero nunca da cierto.
            if (sol == null || !sol.HasArmPose)
            {
                if (existing != null) Object.DestroyImmediate(existing.gameObject);
                return;
            }
            var go = existing != null ? existing.gameObject : new GameObject(MarkerPrefix + suffix);
            go.tag = "EditorOnly";
            go.transform.SetParent(grip, false);
            go.transform.localPosition = sol.HandPosInGrip;
            go.transform.localRotation = sol.HandRotInGrip;
            go.transform.localScale = Vector3.one;
        }
    }
}
#endif
