#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using BackroomsSurvival.Gameplay.HandInteraction;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools.HandInteraction
{
    /// <summary>
    /// Un brazo de primera persona del rig del vendor (FP_Arms): hombro, antebrazo, mano, torsión y
    /// quince falanges. Los signos de cierre son los del rig (ADR-133 enm. 1) y se COMPRUEBAN en
    /// <see cref="HandInteractionRig.MeasureCloseSigns"/>.
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

    /// <summary>Parte del modelo que no es la superficie activa (la manivela): los dedos no pueden atravesarla.</summary>
    internal sealed class HandObstacle
    {
        public Transform Transform;
        public Bounds LocalBounds;
        public string Name;
    }

    /// <summary>
    /// UNA superficie que se agarra: un tramo de malla aproximado por su perfil de radio a lo largo de un eje
    /// (percentil 90 por tramo, como los horneadores). La malla de agarre del perfil es una (eje +Y, entera);
    /// el pomo de la manivela es otra (eje +Z de la manivela, sólo su cuarto de arriba). El «reloj» gira
    /// alrededor del eje con 0° en <see cref="RefL"/> (el +Z local cuando el eje es +Y).
    /// </summary>
    internal sealed class HandGripSurface
    {
        public Transform T;
        public string Name;
        public Vector3 AxisL, RefL, OriginL;
        public float MinT, MaxT;
        public float[] Radius;

        public float Scale => Mathf.Max(1e-5f, T.lossyScale.y);
        public float LengthMeters => (MaxT - MinT) * Scale;
        public Vector3 AxisWorld => T.TransformDirection(AxisL).normalized;

        public static HandGripSurface Build(Transform t, Mesh mesh, Vector3 axisLocal, float minY01, float maxY01)
        {
            var axis = axisLocal.sqrMagnitude < 1e-8f ? Vector3.up : axisLocal.normalized;
            var b = mesh.bounds;
            float yMin = Mathf.Lerp(b.min.y, b.max.y, Mathf.Clamp01(Mathf.Min(minY01, maxY01)));
            float yMax = Mathf.Lerp(b.min.y, b.max.y, Mathf.Clamp01(Mathf.Max(minY01, maxY01)));
            var region = mesh.vertices.Where(v => v.y >= yMin - 1e-6f && v.y <= yMax + 1e-6f).ToList();
            if (region.Count < 8)
                throw new HandInteractionException("GRIP_PART_EMPTY", $"'{t.name}': el tramo {minY01:0.00}–{maxY01:0.00} no tiene vértices");

            // Con el eje +Y y la malla entera, el eje pasa por el origen local: igual que el perfil de siempre.
            bool legacy = axis == Vector3.up && minY01 <= 0f && maxY01 >= 1f;
            Vector3 origin = Vector3.zero;
            if (!legacy)
            {
                foreach (var v in region) origin += v;
                origin /= region.Count;
                origin -= axis * Vector3.Dot(origin, axis);
            }
            var s = new HandGripSurface
            {
                T = t, Name = t.name, AxisL = axis, OriginL = origin,
                RefL = Vector3.ProjectOnPlane(Mathf.Abs(Vector3.Dot(axis, Vector3.forward)) < 0.9f ? Vector3.forward : Vector3.up, axis).normalized,
            };
            const int bins = 36;
            float minT = float.MaxValue, maxT = float.MinValue;
            foreach (var v in region)
            {
                float tt = Vector3.Dot(v - origin, axis);
                minT = Mathf.Min(minT, tt);
                maxT = Mathf.Max(maxT, tt);
            }
            s.MinT = minT;
            s.MaxT = maxT;
            var lists = new List<float>[bins];
            for (int i = 0; i < bins; i++) lists[i] = new List<float>();
            float span = Mathf.Max(1e-5f, maxT - minT);
            foreach (var v in region)
            {
                Vector3 d = v - origin;
                float tt = Vector3.Dot(d, axis);
                int bin = Mathf.Clamp(Mathf.FloorToInt((tt - minT) / span * bins), 0, bins - 1);
                lists[bin].Add((d - axis * tt).magnitude);
            }
            s.Radius = new float[bins];
            for (int i = 0; i < bins; i++)
            {
                if (lists[i].Count == 0) { s.Radius[i] = i > 0 ? s.Radius[i - 1] : 0f; continue; }
                lists[i].Sort();
                s.Radius[i] = lists[i][Mathf.Clamp((int)(lists[i].Count * 0.9f), 0, lists[i].Count - 1)];
            }
            return s;
        }

        /// <summary>Radio LOCAL a una coordenada local del eje.</summary>
        public float RadiusAtT(float t)
        {
            float span = Mathf.Max(1e-5f, MaxT - MinT);
            float f = Mathf.Clamp01((t - MinT) / span) * (Radius.Length - 1);
            int i = Mathf.Clamp(Mathf.FloorToInt(f), 0, Radius.Length - 2);
            return Mathf.Lerp(Radius[i], Radius[i + 1], f - i);
        }

        public float TAt(float along01) => Mathf.Lerp(MinT, MaxT, along01);
        public float RadiusAtAlong(float along01) => RadiusAtT(TAt(along01)) * Scale;
        public Vector3 PointAt(float along01) => T.TransformPoint(OriginL + AxisL * TAt(along01));

        public Vector3 Radial(float clockDegrees)
            => T.TransformDirection(Quaternion.AngleAxis(clockDegrees, AxisL) * RefL).normalized;

        /// <summary>Coordenadas locales de un punto de mundo: a lo largo del eje y distancia al eje.</summary>
        public void Local(Vector3 world, out float t, out float r)
        {
            Vector3 d = T.InverseTransformPoint(world) - OriginL;
            t = Vector3.Dot(d, AxisL);
            r = (d - AxisL * t).magnitude;
        }

        public float Along01(Vector3 world)
        {
            Local(world, out float t, out _);
            return Mathf.InverseLerp(MinT, MaxT, t);
        }

        /// <summary>Distancia con signo a la piel, en metros (positivo fuera).</summary>
        public float Gap(Vector3 world)
        {
            Local(world, out float t, out float r);
            float radius = RadiusAtT(Mathf.Clamp(t, MinT, MaxT));
            float local;
            if (t < MinT || t > MaxT)
            {
                float beyond = t < MinT ? MinT - t : t - MaxT;
                float outward = Mathf.Max(0f, r - radius);
                local = Mathf.Sqrt(outward * outward + beyond * beyond);
            }
            else local = r - radius;
            return local * Scale;
        }
    }

    /// <summary>
    /// La instancia de medida de un wieldable: esqueleto, objeto y superficies. Todo en espacio de mundo
    /// con la raíz del prefab en el origen y sin rotar, así que «mundo» = espacio del jugador
    /// (x derecha, y arriba, z delante). Las poses de mano se guardan relativas a <see cref="GripMesh"/>
    /// (el marco del objeto); lo que se agarra en cada momento es <see cref="Surface"/>.
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
        public HandSide R, L;
        public Transform[] Skeleton;
        public string[] Paths;
        public readonly List<HandObstacle> Parts = new();
        public HandGripSurface MainSurface;
        /// <summary>La superficie que agarra la mano que se está resolviendo o midiendo.</summary>
        public HandGripSurface Surface;
        private readonly Dictionary<string, HandGripSurface> _partSurfaces = new();

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
            foreach (var m in meshes)
                rig.Parts.Add(new HandObstacle { Transform = m.transform, LocalBounds = m.sharedMesh.bounds, Name = m.name });
            rig.MainSurface = HandGripSurface.Build(grip.transform, grip.sharedMesh, Vector3.up, 0f, 1f);
            rig.Surface = rig.MainSurface;

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

        // ─── Nodo del modelo y piezas que giran ──────────────────────────────────────────────────

        private Transform _nodeParent;
        private Vector3 _nodeLocalPos, _nodeLocalScale;
        private Quaternion _nodeLocalRot;
        public bool NodeDetached { get; private set; }

        /// <summary>
        /// Suelta el modelo de su mano conservando su pose de MUNDO: así la portadora se puede mover sin arrastrar el
        /// objeto. <see cref="ReattachNode"/> lo devuelve con su offset ORIGINAL, que es con el que los clips base lo llevan.
        /// </summary>
        public void DetachNode()
        {
            if (NodeDetached) return;
            _nodeParent = Node.parent;
            _nodeLocalPos = Node.localPosition;
            _nodeLocalRot = Node.localRotation;
            _nodeLocalScale = Node.localScale;
            Node.SetParent(InstanceRoot, true);
            // Se COMPRUEBA: Unity rechaza el cambio de padre dentro de una instancia de prefab sin lanzar nada.
            if (Node.parent != InstanceRoot)
                throw new HandInteractionException("NODE_DETACH_FAILED",
                    $"el modelo '{Node.name}' sigue colgando de '{Node.parent?.name}': ¿instancia de prefab sin desempaquetar?");
            NodeDetached = true;
        }

        public void ReattachNode()
        {
            if (!NodeDetached) return;
            Node.SetParent(_nodeParent, false);
            Node.localPosition = _nodeLocalPos;
            Node.localRotation = _nodeLocalRot;
            Node.localScale = _nodeLocalScale;
            NodeDetached = false;
        }

        private sealed class SweptPart
        {
            public HandObstacle Box;
            public Vector3 AxisL;
            public float Clearance;
            /// <summary>La pieza en coordenadas cilíndricas sobre su eje: (distancia al eje, altura sobre el eje). Una
            /// vuelta completa convierte cada vértice en un círculo, y la distancia de un punto a ese círculo sólo
            /// depende de SU distancia al eje y SU altura: exacta en los 360° y sin barrer fases.</summary>
            public Vector2[] Profile;
        }

        private readonly List<SweptPart> _swept = new();

        /// <summary>Si está activo, <c>Measure</c> castiga cualquier articulación dentro de la órbita de las piezas que giran.</summary>
        public bool SweptCheckEnabled;

        public bool HasSweptParts => _swept.Count > 0;

        public void AddSweptPart(string nodeName, Vector3 axisLocal, float clearance, float minY01 = 0f, float maxY01 = 1f)
        {
            var box = Parts.FirstOrDefault(p => p.Name == nodeName)
                ?? throw new HandInteractionException("SWEPT_PART_NOT_FOUND", $"el modelo no tiene la pieza que gira '{nodeName}'");
            var axis = axisLocal.sqrMagnitude < 1e-8f ? Vector3.forward : axisLocal.normalized;
            var mesh = box.Transform.GetComponent<MeshFilter>().sharedMesh;
            // LOS VÉRTICES, no la caja. Con la caja girando, la placa del brazo de la manivela era un disco macizo
            // pegado al costado y cualquier dedo que rodease el cuerpo «chocaba»; el criterio real
            // (`TheKnobOrbitClearsTheRightHand`) es la distancia a la pieza de verdad. Submuestreada a ~300 puntos y
            // girada UNA vez aquí: la búsqueda sólo compara distancias.
            // Rejilla de 1,5 mm en (r, z): miles de vértices se quedan en ~100 puntos distintos.
            const float cell = 0.0015f;
            var cells = new SortedDictionary<long, Vector2>();
            float scale = Mathf.Max(1e-5f, box.Transform.lossyScale.x);
            var bounds = mesh.bounds;
            float yMin = Mathf.Lerp(bounds.min.y, bounds.max.y, Mathf.Clamp01(Mathf.Min(minY01, maxY01)));
            float yMax = Mathf.Lerp(bounds.min.y, bounds.max.y, Mathf.Clamp01(Mathf.Max(minY01, maxY01)));
            foreach (var v in mesh.vertices)
            {
                if (v.y < yMin - 1e-6f || v.y > yMax + 1e-6f) continue;
                float z = Vector3.Dot(v, axis);
                float r = (v - axis * z).magnitude;
                long key = ((long)Mathf.RoundToInt(r * scale / cell) << 32) ^ (uint)Mathf.RoundToInt(z * scale / cell);
                if (!cells.ContainsKey(key)) cells[key] = new Vector2(r, z);
            }
            _swept.Add(new SweptPart { Box = box, AxisL = axis, Clearance = clearance, Profile = cells.Values.ToArray() });
        }

        /// <summary>
        /// La menor holgura (distancia al volumen que barre la pieza en una vuelta completa MENOS la holgura exigida) de
        /// un punto a las piezas que giran. El pivote es el origen local de la pieza, como en runtime.
        /// </summary>
        public float SweptGap(Vector3 world)
        {
            float worst = float.MaxValue;
            foreach (var s in _swept)
            {
                Vector3 local = s.Box.Transform.InverseTransformPoint(world);
                float scale = Mathf.Max(1e-5f, s.Box.Transform.lossyScale.x);
                float z = Vector3.Dot(local, s.AxisL);
                float r = (local - s.AxisL * z).magnitude;
                float best = float.MaxValue;
                foreach (var p in s.Profile)
                {
                    float dr = r - p.x, dz = z - p.y;
                    float d = dr * dr + dz * dz;
                    if (d < best) best = d;
                }
                worst = Mathf.Min(worst, Mathf.Sqrt(best) * scale - s.Clearance);
            }
            return worst;
        }

        // ─── Superficie activa ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Activa la superficie que agarra <paramref name="target"/>: la malla del perfil si no nombra pieza, o esa
        /// pieza con su eje y su tramo (el pomo). Con <c>null</c>, la principal (la portadora siempre va ahí).
        /// </summary>
        public void UseSurfaceFor(HandGripTarget target)
        {
            if (target == null || string.IsNullOrEmpty(target.gripPartNodeName)) { Surface = MainSurface; return; }
            string key = $"{target.gripPartNodeName}|{target.gripPartAxis}|{target.gripPartMinY01}|{target.gripPartMaxY01}";
            if (!_partSurfaces.TryGetValue(key, out var surface))
            {
                var part = Node.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == target.gripPartNodeName && m.sharedMesh != null)
                    ?? throw new HandInteractionException("GRIP_PART_NOT_FOUND",
                        $"el modelo '{Node.name}' no tiene la pieza '{target.gripPartNodeName}' con malla");
                surface = HandGripSurface.Build(part.transform, part.sharedMesh, target.gripPartAxis, target.gripPartMinY01, target.gripPartMaxY01);
                _partSurfaces[key] = surface;
            }
            Surface = surface;
        }

        public void UseMainSurface() => Surface = MainSurface;

        public float LengthMeters => Surface.LengthMeters;

        /// <summary>Eje de la superficie activa, en mundo.</summary>
        public Vector3 Axis => Surface.AxisWorld;

        /// <summary>Punto del eje a la fracción <paramref name="along"/> (0 un extremo, 1 el otro), en mundo.</summary>
        public Vector3 AxisPoint(float along) => Surface.PointAt(along);

        public float RadiusAtAlong(float along) => Surface.RadiusAtAlong(along);

        /// <summary>Dirección radial del reloj en mundo alrededor del eje de la superficie activa.</summary>
        public Vector3 Radial(float clockDegrees) => Surface.Radial(clockDegrees);

        /// <summary>
        /// Distancia con signo a la piel del objeto en metros (positivo fuera): la superficie activa con su perfil
        /// real y, como obstáculos, la malla principal por su perfil (si la activa es otra) y las demás piezas
        /// por su caja. Un dedo sobre el pomo no puede atravesar ni el cuerpo ni el brazo de la manivela.
        /// </summary>
        public float SurfaceGap(Vector3 world)
        {
            float gap = Surface.Gap(world);
            if (Surface != MainSurface) gap = Mathf.Min(gap, MainSurface.Gap(world));
            foreach (var o in Parts)
            {
                if (o.Transform == MainSurface.T || o.Transform == Surface.T) continue;
                gap = Mathf.Min(gap, BoxGap(o, world));
            }
            // La órbita de lo que gira NO entra aquí: probado como obstáculo para los dedos, toda palma y falange del lado de
            // la manivela contaba como «dentro» en cualquier agarre y la búsqueda saltaba a poses de coste 71 (medido).
            // Se queda como coste duro en Measure.
            return gap;
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

        /// <summary>La torsión de la mano sobre el antebrazo (+Y). Flexión y desviación NO salen de aquí: se miden
        /// con vectores del cuerpo en <c>HandGripSolver.Measure</c>.</summary>
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
