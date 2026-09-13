#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools.HandInteraction
{
    /// <summary>
    /// Un brazo de primera persona del rig del vendor (FP_Arms): hombro, antebrazo, mano, torsión y
    /// quince falanges. Los signos de cierre NO se escriben a mano: <see cref="HandInteractionRig.MeasureCloseSigns"/>
    /// los mide desde la neutra, que es donde la medida es inequívoca (ADR-077 enm. 5: con el dedo muy
    /// cerrado la yema vuelve a subir y el signo sale al revés).
    /// </summary>
    internal sealed class HandSide
    {
        public bool Right;
        public string Suffix => Right ? "R" : "L";
        public Transform Upper, Fore, Hand;
        public Transform[] Twist;
        /// <summary>[dedo][falange]; dedo 0 = pulgar, 1..4 = índice..meñique.</summary>
        public Transform[][] Fingers;
        public float[] TipLength;
        /// <summary>Signo del giro sobre +X local que CIERRA los cuatro dedos.</summary>
        public float FingerCloseSign = -1f;
        /// <summary>Signo del giro sobre +Z local que cierra el pulgar.</summary>
        public float ThumbCloseSign = 1f;

        // El «marco de agarre» de ESTA mano, medido en su espacio local con un puño genérico:
        // dónde queda el hueco que encierran los dedos, hacia dónde va la línea de nudillos
        // (índice → meñique) y hacia dónde mira la palma.
        public Vector3 EnclosedLocal, KnuckleLineLocal, PalmNormalLocal;
        public float PalmDepthLocal;
        public bool SignCheckFailed;
    }

    /// <summary>Parte del modelo que no es la malla de agarre (la manivela): los dedos no pueden atravesarla.</summary>
    internal sealed class HandObstacle
    {
        public Transform Transform;
        public Bounds LocalBounds;
        public string Name;
    }

    /// <summary>
    /// La instancia de medida de un wieldable: esqueleto, objeto y superficie. Todo en espacio de mundo
    /// con la raíz del prefab en el origen y sin rotar, así que «mundo» = espacio del jugador
    /// (x derecha, y arriba, z delante) salvo por el hueso de la cámara.
    /// </summary>
    internal sealed class HandInteractionRig
    {
        public const string AnimatorNodeName = "ViewModel";
        public const string RootBoneName = "Root";
        public const string CameraBoneName = "Camera";
        public const float FrameRate = 30f;

        /// <summary>Radio de una falange: del hueso a la piel (ADR-133 enm. 1).</summary>
        public const float FingerSkin = 0.0085f;
        public const float ThumbSkin = 0.0105f;

        /// <summary>Topes de flexión por articulación, de la base a la punta: los de una mano real.</summary>
        public static readonly float[] FingerCaps = { 90f, 105f, 75f };
        public static readonly float[] ThumbCaps = { 50f, 55f, 60f };

        private static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Pinky" };

        public GameObject Instance;
        public Transform InstanceRoot, Animator, Root, Camera;
        public Transform Node, GripMesh;
        public Mesh GripMeshAsset;
        public HandSide R, L;
        public Transform[] Skeleton;
        public string[] Paths;
        public float[] RadiusProfile;
        public float ProfileMinY, ProfileMaxY;
        public readonly List<HandObstacle> Obstacles = new();

        public HandSide Side(bool right) => right ? R : L;

        // ─── Lectura ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lee el rig de una instancia. Lanza <see cref="HandInteractionException"/> con código estable si
        /// falta algo: quien llama (la API) lo convierte en error estructurado.
        /// </summary>
        public static HandInteractionRig Read(GameObject instance, string modelNodeName, string gripMeshNodeName)
        {
            var byName = new Dictionary<string, Transform>();
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name)) byName[t.name] = t;

            Transform Need(string n, string code) => byName.TryGetValue(n, out var t) ? t
                : throw new HandInteractionException(code, $"falta '{n}' en el prefab");

            var rig = new HandInteractionRig
            {
                Instance = instance,
                InstanceRoot = instance.transform,
                Animator = Need(AnimatorNodeName, "RIG_NOT_FOUND"),
                Root = Need(RootBoneName, "RIG_NOT_FOUND"),
                Camera = Need(CameraBoneName, "RIG_NOT_FOUND"),
            };
            rig.R = ReadSide(true, Need);
            rig.L = ReadSide(false, Need);

            if (string.IsNullOrEmpty(modelNodeName))
                throw new HandInteractionException("MODEL_NODE_NOT_FOUND", "el perfil no dice qué nodo es el modelo");
            rig.Node = Need(modelNodeName, "MODEL_NODE_NOT_FOUND");

            var meshes = rig.Node.GetComponentsInChildren<MeshFilter>(true).Where(m => m.sharedMesh != null).ToList();
            MeshFilter grip = string.IsNullOrEmpty(gripMeshNodeName)
                ? meshes.FirstOrDefault()
                : meshes.FirstOrDefault(m => m.name == gripMeshNodeName);
            if (grip == null)
                throw new HandInteractionException("GRIP_MESH_NOT_FOUND",
                    $"el nodo '{modelNodeName}' no trae la malla de agarre '{gripMeshNodeName}'");
            rig.GripMesh = grip.transform;
            rig.GripMeshAsset = grip.sharedMesh;
            foreach (var m in meshes)
            {
                if (m == grip) continue;
                rig.Obstacles.Add(new HandObstacle { Transform = m.transform, LocalBounds = m.sharedMesh.bounds, Name = m.name });
            }

            var skeleton = new List<Transform>();
            foreach (var t in rig.Root.GetComponentsInChildren<Transform>(true))
            {
                bool underModel = false;
                for (var p = t; p != null; p = p.parent)
                    if (p == rig.Node) { underModel = true; break; }
                if (!underModel) skeleton.Add(t);
            }
            rig.Skeleton = skeleton.ToArray();
            rig.Paths = rig.Skeleton.Select(t => AnimationUtility.CalculateTransformPath(t, rig.Animator)).ToArray();

            rig.ReadRadiusProfile();
            return rig;
        }

        private static HandSide ReadSide(bool right, Func<string, string, Transform> need)
        {
            string s = right ? "R" : "L";
            var side = new HandSide
            {
                Right = right,
                Upper = need($"UpperArm.{s}", "RIG_NOT_FOUND"),
                Fore = need($"Forearm.{s}", "RIG_NOT_FOUND"),
                Hand = need($"Hand.{s}", "RIG_NOT_FOUND"),
                Twist = new[] { need($"ForearmTwist.2.{s}", "RIG_NOT_FOUND"), need($"ForearmTwist.3.{s}", "RIG_NOT_FOUND"),
                                need($"ForearmTwist.4.{s}", "RIG_NOT_FOUND") },
                Fingers = FingerNames.Select(f => new[]
                {
                    need($"{f}.1.{s}", "RIG_NOT_FOUND"), need($"{f}.2.{s}", "RIG_NOT_FOUND"), need($"{f}.3.{s}", "RIG_NOT_FOUND"),
                }).ToArray(),
            };
            // La última falange no tiene hijo que diga su largo: la anterior, un poco más corta.
            side.TipLength = side.Fingers.Select(c => c[2].localPosition.magnitude * 0.9f).ToArray();
            return side;
        }

        /// <summary>Radio de la malla de agarre por tramo de su +Y (percentil 90 por bin), como los horneadores.</summary>
        private void ReadRadiusProfile()
        {
            const int bins = 36;
            var vertices = GripMeshAsset.vertices;
            var lists = new List<float>[bins];
            for (int i = 0; i < bins; i++) lists[i] = new List<float>();
            var b = GripMeshAsset.bounds;
            ProfileMinY = b.min.y;
            ProfileMaxY = b.max.y;
            float span = Mathf.Max(1e-5f, b.size.y);
            foreach (var v in vertices)
            {
                int bin = Mathf.Clamp(Mathf.FloorToInt((v.y - b.min.y) / span * bins), 0, bins - 1);
                lists[bin].Add(Mathf.Sqrt(v.x * v.x + v.z * v.z));
            }
            RadiusProfile = new float[bins];
            for (int i = 0; i < bins; i++)
            {
                if (lists[i].Count == 0) { RadiusProfile[i] = i > 0 ? RadiusProfile[i - 1] : 0f; continue; }
                lists[i].Sort();
                RadiusProfile[i] = lists[i][Mathf.Clamp((int)(lists[i].Count * 0.9f), 0, lists[i].Count - 1)];
            }
        }

        /// <summary>Radio de la malla en metros a una altura LOCAL de la malla.</summary>
        public float RadiusAtLocal(float y)
        {
            float span = Mathf.Max(1e-5f, ProfileMaxY - ProfileMinY);
            float f = Mathf.Clamp01((y - ProfileMinY) / span) * (RadiusProfile.Length - 1);
            int i = Mathf.Clamp(Mathf.FloorToInt(f), 0, RadiusProfile.Length - 2);
            return Mathf.Lerp(RadiusProfile[i], RadiusProfile[i + 1], f - i);
        }

        public float MeshScale => Mathf.Max(1e-5f, GripMesh.lossyScale.y);
        public float LengthMeters => (ProfileMaxY - ProfileMinY) * MeshScale;

        /// <summary>Eje del objeto en mundo (hacia la punta).</summary>
        public Vector3 Axis => GripMesh.up;

        /// <summary>Punto del eje a la fracción <paramref name="along"/> (0 culata, 1 punta), en mundo.</summary>
        public Vector3 AxisPoint(float along)
            => GripMesh.TransformPoint(new Vector3(0f, Mathf.Lerp(ProfileMinY, ProfileMaxY, along), 0f));

        public float RadiusAtAlong(float along) => RadiusAtLocal(Mathf.Lerp(ProfileMinY, ProfileMaxY, along)) * MeshScale;

        /// <summary>Dirección radial del reloj en mundo: 0° = +Z del objeto, 90° = +X.</summary>
        public Vector3 Radial(float clockDegrees)
            => GripMesh.TransformDirection(Quaternion.AngleAxis(clockDegrees, Vector3.up) * Vector3.forward).normalized;

        /// <summary>Distancia con signo a la piel del objeto en metros (positivo fuera): perfil de radio
        /// de la malla de agarre y cajas de las demás piezas.</summary>
        public float SurfaceGap(Vector3 world)
        {
            float gap = ProfileGap(world);
            foreach (var o in Obstacles)
                gap = Mathf.Min(gap, BoxGap(o, world));
            return gap;
        }

        public float ProfileGap(Vector3 world)
        {
            Vector3 l = GripMesh.InverseTransformPoint(world);
            float r = Mathf.Sqrt(l.x * l.x + l.z * l.z);
            float radius = RadiusAtLocal(Mathf.Clamp(l.y, ProfileMinY, ProfileMaxY));
            float local;
            if (l.y < ProfileMinY || l.y > ProfileMaxY)
            {
                float beyond = l.y < ProfileMinY ? ProfileMinY - l.y : l.y - ProfileMaxY;
                float outward = Mathf.Max(0f, r - radius);
                local = Mathf.Sqrt(outward * outward + beyond * beyond);
            }
            else local = r - radius;
            return local * MeshScale;
        }

        public static float BoxGap(HandObstacle o, Vector3 world)
        {
            Vector3 l = o.Transform.InverseTransformPoint(world) - o.LocalBounds.center;
            Vector3 q = new(Mathf.Abs(l.x) - o.LocalBounds.extents.x, Mathf.Abs(l.y) - o.LocalBounds.extents.y,
                Mathf.Abs(l.z) - o.LocalBounds.extents.z);
            float outside = new Vector3(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f), Mathf.Max(q.z, 0f)).magnitude;
            float inside = Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
            return (outside + inside) * Mathf.Max(1e-5f, o.Transform.lossyScale.x);
        }

        // ─── Pose ────────────────────────────────────────────────────────────────────────────────

        public void Sample(AnimationClip clip, float t)
        {
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(Animator.gameObject, clip, t);
            AnimationMode.EndSampling();
            // El nodo del Animator no se mueve: si el muestreo tocó RootT/RootQ, vuelve a su sitio.
            Animator.localPosition = Vector3.zero;
            Animator.localRotation = Quaternion.identity;
        }

        public HandFrame Capture(float time)
        {
            var f = new HandFrame { Time = time, Pos = new Vector3[Skeleton.Length], Rot = new Quaternion[Skeleton.Length] };
            for (int i = 0; i < Skeleton.Length; i++)
            {
                f.Pos[i] = Skeleton[i].localPosition;
                f.Rot[i] = Skeleton[i].localRotation;
            }
            return f;
        }

        public void Apply(HandFrame f)
        {
            for (int i = 0; i < Skeleton.Length; i++)
            {
                Skeleton[i].localPosition = f.Pos[i];
                Skeleton[i].localRotation = f.Rot[i];
            }
        }

        public static void SetNeutralFingers(HandSide side)
        {
            foreach (var chain in side.Fingers)
                foreach (var joint in chain)
                    joint.localRotation = Quaternion.identity;
        }

        public static Quaternion[][] ReadFingers(HandSide side)
            => side.Fingers.Select(c => c.Select(j => j.localRotation).ToArray()).ToArray();

        public static void WriteFingers(HandSide side, Quaternion[][] joints)
        {
            for (int f = 0; f < side.Fingers.Length; f++)
                for (int j = 0; j < side.Fingers[f].Length; j++)
                    side.Fingers[f][j].localRotation = joints[f][j];
        }

        public static Vector3 EndOf(HandSide side, int finger, int joint)
        {
            var chain = side.Fingers[finger];
            return joint + 1 < chain.Length ? chain[joint + 1].position : chain[joint].TransformPoint(0f, side.TipLength[finger], 0f);
        }

        public static Vector3 Tip(HandSide side, int finger) => EndOf(side, finger, 2);

        /// <summary>
        /// Mide los signos de cierre y el marco de agarre de una mano, DESDE LA NEUTRA. Deja los dedos
        /// en neutra al salir. La mano puede estar en cualquier pose: todo se guarda en su espacio local.
        /// </summary>
        public static void MeasureCloseSigns(HandSide side)
        {
            SetNeutralFingers(side);
            // LOS SIGNOS SON LOS DEL RIG, medidos en ADR-133 enm. 1 y ADR-077 enm. 5: los cuatro dedos
            // cierran sobre −X en las dos manos; el pulgar derecho sobre +Z y el izquierdo sobre −Z.
            // Deducirlos «de lo que acerca la yema a la palma» falló en el 80 % de los dedos. Aquí
            // sólo se COMPRUEBA, a 20° desde recto (donde no hay vuelta posible), y se avisa si un rig
            // distinto no los cumple: `SignCheckFailed` sale en el informe.
            side.FingerCloseSign = -1f;
            side.ThumbCloseSign = side.Right ? 1f : -1f;

            Vector3 knuckles = Vector3.zero;
            for (int f = 1; f < 5; f++) knuckles += side.Hand.InverseTransformPoint(side.Fingers[f][0].position);
            knuckles /= 4f;
            Vector3 indexK = side.Hand.InverseTransformPoint(side.Fingers[1][0].position);
            Vector3 pinkyK = side.Hand.InverseTransformPoint(side.Fingers[4][0].position);
            Vector3 across = (pinkyK - indexK).normalized;

            // El lado de la palma: la yema del pulgar queda por el lado palmar del plano que forman la
            // línea de nudillos y la dirección de los dedos. Cerrar un dedo 20° mueve su yema hacia ese
            // lado; con el signo equivocado, hacia el dorso. (Girar el MCP de un dedo recto acerca la
            // yema a la muñeca EN LOS DOS SENTIDOS, así que la distancia a la muñeca no distingue nada.)
            Vector3 fingerDir = (side.Hand.InverseTransformPoint(Tip(side, 2)) - knuckles).normalized;
            Vector3 planeNormal = Vector3.Cross(across, fingerDir).normalized;
            float thumbSide = Vector3.Dot(side.Hand.InverseTransformPoint(Tip(side, 0)) - knuckles, planeNormal);
            int agree = 0;
            for (int f = 1; f < 5; f++)
            {
                var joint = side.Fingers[f][0];
                Vector3 before = side.Hand.InverseTransformPoint(Tip(side, f));
                joint.localRotation = Quaternion.AngleAxis(20f * side.FingerCloseSign, Vector3.right);
                Vector3 after = side.Hand.InverseTransformPoint(Tip(side, f));
                joint.localRotation = Quaternion.identity;
                if (Vector3.Dot(after - before, planeNormal) * thumbSide > 0f) agree++;
            }
            side.SignCheckFailed = Mathf.Abs(thumbSide) > 0.003f && agree < 3;

            // El marco de agarre: puño genérico al 55 % y el hueco que encierran las falanges medias.
            CloseCoupled(side, 0.55f, includeThumb: false);
            Vector3 enclosed = Vector3.zero;
            for (int f = 1; f < 5; f++)
                enclosed += side.Hand.InverseTransformPoint((side.Fingers[f][1].position + side.Fingers[f][2].position) * 0.5f);
            enclosed /= 4f;
            SetNeutralFingers(side);

            side.EnclosedLocal = enclosed;
            side.KnuckleLineLocal = across;
            side.PalmNormalLocal = Vector3.ProjectOnPlane(enclosed - knuckles, across).normalized;
            if (side.PalmNormalLocal.sqrMagnitude < 1e-6f)
                throw new HandInteractionException("GRIP_FRAME_DEGENERATE",
                    $"la mano {side.Suffix} no encierra nada al cerrar el puño: no se puede medir su palma");
            side.PalmDepthLocal = Vector3.Dot(enclosed - knuckles, side.PalmNormalLocal);
        }

        /// <summary>Cierre ACOPLADO de los dedos a una fracción de su tope: MCP, PIP y DIP a la vez, como
        /// los mueve el tendón. Desde la rotación actual de cada falange.</summary>
        public static void CloseCoupled(HandSide side, float fraction, bool includeThumb)
        {
            for (int f = includeThumb ? 0 : 1; f < 5; f++)
            {
                bool thumb = f == 0;
                var caps = thumb ? ThumbCaps : FingerCaps;
                var axis = thumb ? Vector3.forward : Vector3.right;
                float sign = thumb ? side.ThumbCloseSign : side.FingerCloseSign;
                for (int j = thumb ? 1 : 0; j < 3; j++)
                    side.Fingers[f][j].localRotation *= Quaternion.AngleAxis(caps[j] * fraction * sign, axis);
            }
        }

        // ─── IK ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// IK analítico de dos huesos (ADR-133 enm. 1): los huesos apuntan por +Y a su hijo y el codo es
        /// bisagra sobre la Z local (−Z en el derecho, +Z en el izquierdo). Devuelve false si recortó alcance.
        /// </summary>
        public static bool SolveTwoBone(HandSide side, Vector3 target, Quaternion handRotation, Vector3 pole)
        {
            Transform upper = side.Upper, fore = side.Fore, hand = side.Hand;
            Vector3 s = upper.position;
            float a = (fore.position - upper.position).magnitude;
            float b = (hand.position - fore.position).magnitude;
            Vector3 toTarget = target - s;
            float d = toTarget.magnitude;
            bool reachable = true;
            float dMax = a + b - 0.002f;
            if (d > dMax) { d = dMax; reachable = false; }
            d = Mathf.Max(d, Mathf.Abs(a - b) + 0.002f);
            Vector3 u = toTarget.normalized;

            Vector3 poleDir = Vector3.ProjectOnPlane(pole, u).normalized;
            if (poleDir.sqrMagnitude < 1e-6f) poleDir = Vector3.ProjectOnPlane(Vector3.down, u).normalized;

            float cosAlpha = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float alpha = Mathf.Acos(cosAlpha);
            Vector3 upperDir = (u * Mathf.Cos(alpha) + poleDir * Mathf.Sin(alpha)).normalized;
            Vector3 elbow = s + upperDir * a;
            Vector3 foreDir = (s + u * d - elbow).normalized;

            Vector3 bendNormal = Vector3.Cross(upperDir, foreDir);
            if (bendNormal.sqrMagnitude < 1e-8f) bendNormal = Vector3.Cross(upperDir, poleDir);
            bendNormal.Normalize();
            Vector3 zAxis = side.Right ? -bendNormal : bendNormal;

            upper.rotation = Quaternion.LookRotation(zAxis, upperDir);
            fore.rotation = Quaternion.LookRotation(zAxis, foreDir);
            hand.rotation = handRotation;
            return reachable;
        }

        public static float TwistDegrees(Quaternion local)
        {
            var t = new Quaternion(0f, local.y, 0f, local.w);
            if (t.y == 0f && t.w == 0f) return 0f;
            t.Normalize();
            float angle = 2f * Mathf.Atan2(t.y, t.w) * Mathf.Rad2Deg;
            if (angle > 180f) angle -= 360f;
            if (angle < -180f) angle += 360f;
            return angle;
        }

        public static void DistributeForearmTwist(HandSide side)
        {
            float angle = TwistDegrees(Quaternion.Inverse(side.Fore.rotation) * side.Hand.rotation);
            float handLength = side.Hand.localPosition.magnitude;
            foreach (var tw in side.Twist)
            {
                float k = 0.9f * (tw.localPosition.magnitude / Mathf.Max(1e-4f, handLength));
                tw.localRotation = Quaternion.AngleAxis(angle * k, Vector3.up);
            }
        }

        /// <summary>La muñeca en ángulos con nombre: torsión sobre el antebrazo (+Y) y el columpio que queda,
        /// partido en flexión/extensión (X) y desviación (Z).</summary>
        public static void WristAngles(HandSide side, out float twist, out float flexion, out float deviation)
        {
            var local = Quaternion.Inverse(side.Fore.rotation) * side.Hand.rotation;
            twist = TwistDegrees(local);
            var swing = local * Quaternion.Inverse(Quaternion.AngleAxis(twist, Vector3.up));
            swing.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) { angle = 360f - angle; axis = -axis; }
            if (float.IsNaN(axis.x)) { flexion = 0f; deviation = 0f; return; }
            flexion = axis.x * angle;
            deviation = axis.z * angle;
        }

        // ─── Clips ───────────────────────────────────────────────────────────────────────────────

        public AnimationClip BuildClip(List<HandFrame> frames, float length, bool loop)
        {
            var clip = new AnimationClip { frameRate = FrameRate };
            for (int i = 0; i < Skeleton.Length; i++)
            {
                var px = new AnimationCurve(); var py = new AnimationCurve(); var pz = new AnimationCurve();
                var rx = new AnimationCurve(); var ry = new AnimationCurve(); var rz = new AnimationCurve(); var rw = new AnimationCurve();
                Quaternion previous = frames[0].Rot[i];
                foreach (var f in frames)
                {
                    var q = f.Rot[i];
                    if (Quaternion.Dot(q, previous) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    previous = q;
                    px.AddKey(f.Time, f.Pos[i].x); py.AddKey(f.Time, f.Pos[i].y); pz.AddKey(f.Time, f.Pos[i].z);
                    rx.AddKey(f.Time, q.x); ry.AddKey(f.Time, q.y); rz.AddKey(f.Time, q.z); rw.AddKey(f.Time, q.w);
                }
                string path = Paths[i];
                Set(clip, path, "m_LocalPosition.x", Squash(px)); Set(clip, path, "m_LocalPosition.y", Squash(py));
                Set(clip, path, "m_LocalPosition.z", Squash(pz));
                Set(clip, path, "m_LocalRotation.x", Squash(rx)); Set(clip, path, "m_LocalRotation.y", Squash(ry));
                Set(clip, path, "m_LocalRotation.z", Squash(rz)); Set(clip, path, "m_LocalRotation.w", Squash(rw));
            }
            clip.EnsureQuaternionContinuity();
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            settings.loopBlend = false;
            settings.stopTime = length;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        /// <summary>Una curva que no cambia se guarda con dos claves (ADR-133 enm. 1: 26 MB → 4,7).</summary>
        private static AnimationCurve Squash(AnimationCurve curve)
        {
            if (curve.length < 3) return curve;
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < curve.length; i++)
            {
                float v = curve[i].value;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (max - min > 1e-5f) return curve;
            var first = curve[0]; var last = curve[curve.length - 1];
            return new AnimationCurve(new Keyframe(first.time, first.value), new Keyframe(last.time, first.value));
        }

        private static void Set(AnimationClip clip, string path, string property, AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
            }
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), property), curve);
        }

        /// <summary>Escribe sobre el asset existente (conserva GUID y referencias) o lo crea.</summary>
        public static AnimationClip SaveClip(AnimationClip clip, string path, string name)
        {
            clip.name = name;
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(clip, existing);
                existing.name = name;
                UnityEngine.Object.DestroyImmediate(clip);
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(clip, path);
            return clip;
        }
    }

    internal sealed class HandFrame
    {
        public float Time;
        public Vector3[] Pos;
        public Quaternion[] Rot;
    }

    /// <summary>Fallo con código estable: la API lo devuelve como error estructurado.</summary>
    internal sealed class HandInteractionException : Exception
    {
        public readonly string Code;
        public HandInteractionException(string code, string message) : base(message) { Code = code; }
    }
}
#endif
