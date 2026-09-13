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
            ctx.Instance.hideFlags = HideFlags.HideAndDontSave;
            ctx.Instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var t in ctx.Instance.GetComponentsInChildren<Transform>(true))
                if (t.name == HandInteractionRig.AnimatorNodeName || t.name == HandInteractionRig.RootBoneName)
                    t.gameObject.SetActive(true);
            try
            {
                ctx.Rig = HandInteractionRig.Read(ctx.Instance, profile.modelNodeName, profile.gripMeshNodeName);
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
                if (!baseLayerClips.Contains(original)) continue;
                var effective = overrides.TryGetValue(original, out var ov) ? ov : original;
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
                           $"{rig.Obstacles.Count} pieza(s) obstáculo; portadora {(ctx.Carrier != null ? ctx.Carrier.Suffix : "ninguna")}.");

            // Primero la portadora (o la derecha si no hay), después la otra: la segunda mide contra la primera.
            var first = ctx.Carrier ?? rig.R;
            var second = first == rig.R ? rig.L : rig.R;
            var firstSol = SolveHand(ctx, first, null, log);
            var secondSol = SolveHand(ctx, second, first, log);
            // Deja la primera puesta otra vez (la búsqueda de la segunda restaura su fotograma base).
            if (firstSol?.Fingers != null) HandInteractionRig.WriteFingers(first, firstSol.Fingers);

            solved.Right = first.Right ? firstSol : secondSol;
            solved.Left = first.Right ? secondSol : firstSol;
            solved.Log = log.ToString();
            return solved;
        }

        private static HandGripSolution SolveHand(Context ctx, HandSide side, HandSide other, StringBuilder log)
        {
            var target = ctx.Profile.Hand(side.Right);
            var rig = ctx.Rig;
            switch (target.role)
            {
                case HandRole.Keep:
                    log.AppendLine($"mano {side.Suffix}: se conserva la del clip base.");
                    return null;
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
            using var ctx = Open(profile);
            if (string.IsNullOrEmpty(profile.outputFolder))
                throw new HandInteractionException("OUTPUT_FOLDER_NOT_SET", "el perfil no tiene outputFolder");
            if (write) BackroomsEditorFolders.EnsureFolder(profile.outputFolder);

            var distinct = ctx.Pairs.Select(pr => pr.effective).Distinct().ToList();
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
                if (sol == null || !sol.HasArmPose || sol.Metrics.cost <= profile.maxNaturalCost) continue;
                string msg = $"mano {(sol.Right ? "R" : "L")}: la mejor pose cuesta {sol.Metrics.cost:0.0} (tope {profile.maxNaturalCost:0.0}): " +
                             $"{string.Join(", ", sol.Metrics.costBreakdown)}. Opciones: kind OneHand; mover la portadora con su horneador " +
                             "para dejar sitio; o bake con force=true si se acepta tal cual.";
                result.warnings.Add("NO_NATURAL_GRIP: " + msg);
                if (write && !force)
                {
                    AnimationMode.StopAnimationMode();
                    throw new HandInteractionException("NO_NATURAL_GRIP", msg + "\n" + report);
                }
            }

            var built = new List<AnimationClip>();
            for (int i = 0; i < distinct.Count; i++)
                built.Add(BuildClip(ctx, bases[i], solved, report));

            if (!write)
            {
                // Se cierra AnimationMode (lo abrió Solve) y se posa con el clip recién construido.
                AnimationMode.StopAnimationMode();
                var idleBuilt = built[distinct.IndexOf(ctx.IdleEffective)];
                idleBuilt.SampleAnimation(ctx.Rig.Animator.gameObject, 0f);
                ctx.Rig.Animator.localPosition = Vector3.zero;
                ctx.Rig.Animator.localRotation = Quaternion.identity;
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
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            WritePrefab(ctx, remap, solved);

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
        private static AnimationClip BuildClip(Context ctx, AnimationClip source, Solved solved, StringBuilder report)
        {
            var rig = ctx.Rig;
            var p = ctx.Profile;
            // Quien abre AnimationMode lo cierra: aquí sólo si no venía abierto (Solve lo deja abierto).
            bool ownsAnimationMode = !AnimationMode.InAnimationMode();
            if (ownsAnimationMode) AnimationMode.StartAnimationMode();
            bool loop = AnimationUtility.GetAnimationClipSettings(source).loopTime;
            int frames = Mathf.Max(1, Mathf.RoundToInt(source.length * HandInteractionRig.FrameRate));
            var list = new List<HandFrame>(frames + 1);
            int clamps = 0;
            float minWeight = 1f, maxWeight = 0f;

            for (int k = 0; k <= frames; k++)
            {
                float t = Mathf.Min(source.length, k / HandInteractionRig.FrameRate);
                rig.Sample(source, loop && k == frames ? 0f : t);
                float distance = (rig.GripMesh.position - solved.IdleGripPosition).magnitude;
                float proximity = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(p.secondaryFullWeightDistance, p.secondaryZeroWeightDistance, distance));

                foreach (bool right in new[] { true, false })
                {
                    var side = rig.Side(right);
                    var sol = right ? solved.Right : solved.Left;
                    var target = p.Hand(right);
                    if (sol == null || target.role == HandRole.Keep) continue;

                    if (target.role == HandRole.Relaxed || side == ctx.Carrier)
                    {
                        BlendFingers(side, sol.Fingers, target.weight);
                        continue;
                    }

                    float w = target.weight * proximity;
                    minWeight = Mathf.Min(minWeight, w); maxWeight = Mathf.Max(maxWeight, w);
                    if (w <= 1e-4f) continue;

                    Vector3 baseShoulder = side.Upper.position, baseElbow = side.Fore.position, baseHand = side.Hand.position;
                    Quaternion baseRot = side.Hand.rotation;
                    Vector3 gripPos = rig.GripMesh.TransformPoint(sol.HandPosInGrip);
                    Quaternion gripRot = rig.GripMesh.rotation * sol.HandRotInGrip;
                    Vector3 pole = HandGripSolver.PoleFor(rig, side, sol.Pole, baseElbow, baseShoulder, baseHand);

                    side.Upper.position = baseShoulder + sol.ShoulderShiftWorld * w;
                    if (!HandInteractionRig.SolveTwoBone(side, Vector3.Lerp(baseHand, gripPos, w), Quaternion.Slerp(baseRot, gripRot, w), pole)
                        && w > 0.99f) clamps++;
                    HandInteractionRig.DistributeForearmTwist(side);
                    BlendFingers(side, sol.Fingers, w);
                }
                list.Add(rig.Capture(t));
            }
            if (ownsAnimationMode) AnimationMode.StopAnimationMode();
            report.AppendLine($"clip '{source.name}': {source.length:0.00} s{(loop ? ", bucle" : "")}; peso secundaria {minWeight:0.00}–{maxWeight:0.00}; " +
                              $"alcance recortado con peso completo en {clamps} fotograma(s).");
            return rig.BuildClip(list, source.length, loop);
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
