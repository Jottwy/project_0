using System.Collections.Generic;
using System.Linq;
using BackroomsSurvival.Gameplay.HandInteraction;
using NUnit.Framework;
using PolymindGames.WieldableSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Puerta GENÉRICA de la herramienta de interacción mano-objeto: recorre todos los
    /// <see cref="HandInteractionProfile"/> horneados del proyecto y mide el resultado en disco. Un objeto
    /// nuevo preparado con la herramienta entra aquí sin escribir un test propio.
    ///
    /// Las medidas están DUPLICADAS del editor a propósito (los tests no ven Assembly-CSharp-Editor, igual que
    /// <c>ToolGripTests</c>): un cambio en un lado sin el otro sale en rojo.
    /// </summary>
    [TestFixture]
    public class HandInteractionProfileTests
    {
        private const string Rebake = "Rehornear: tools/dev/HandInteraction.ps1 -Command bake -Profile <ruta> (o Tools ▸ Interaction Authoring).";
        private const float FingerSkin = 0.0085f;
        private const float ThumbSkin = 0.0105f;
        private static readonly string[] Wrappers = { "Index", "Middle", "Ring", "Pinky" };

        private static IEnumerable<HandInteractionProfile> BakedProfiles()
            => AssetDatabase.FindAssets("t:HandInteractionProfile")
                .Select(g => AssetDatabase.LoadAssetAtPath<HandInteractionProfile>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(p => p != null && !string.IsNullOrEmpty(p.lastBakeUtc));

        [Test]
        public void UnPerfilNuevoNoTocaNingunaMano()
        {
            var p = ScriptableObject.CreateInstance<HandInteractionProfile>();
            try
            {
                Assert.AreEqual(HandRole.Keep, p.rightHand.role, "la portadora nace conservada: moverla arrastraría el objeto");
                Assert.AreEqual(HandRole.Relaxed, p.leftHand.role);
                Assert.AreEqual(HandInteractionKind.OneHand, p.kind);
                Assert.Less(p.secondaryFullWeightDistance, p.secondaryZeroWeightDistance, "la mezcla por distancia tiene que ir de lleno a cero");
            }
            finally { Object.DestroyImmediate(p); }
        }

        [Test]
        public void CadaPerfilHorneadoApuntaAClipsQueElWieldableUsa()
        {
            var profiles = BakedProfiles().ToList();
            if (profiles.Count == 0) Assert.Ignore("no hay perfiles horneados");
            foreach (var p in profiles)
            {
                Assert.IsNotNull(p.wieldablePrefab, $"{p.name}: sin prefab");
                Assert.IsNotEmpty(p.bakedClips, $"{p.name}: horneado sin clips. {Rebake}");
                Assert.AreEqual(p.baseClips.Length, p.bakedClips.Length, $"{p.name}: base y horneados desparejados");
                var used = Overrides(p.wieldablePrefab).Select(o => o.effective).ToList();
                foreach (var c in p.bakedClips)
                {
                    Assert.IsNotNull(c, $"{p.name}: falta un clip horneado. {Rebake}");
                    Assert.Contains(c, used, $"{p.name}: '{c.name}' existe pero el wieldable no lo usa. {Rebake}");
                }
                foreach (var b in p.baseClips)
                {
                    Assert.IsNotNull(b, $"{p.name}: falta un clip BASE: sin él rehornear apilaría sobre lo horneado");
                    Assert.IsFalse(p.bakedClips.Contains(b), $"{p.name}: '{b.name}' es base y horneado a la vez: rehornear apilaría");
                }
            }
        }

        [Test]
        public void LaManoSecundariaRodeaElObjetoSinAtravesarlo()
        {
            var profiles = BakedProfiles().ToList();
            if (profiles.Count == 0) Assert.Ignore("no hay perfiles horneados");
            var failures = new List<string>();
            foreach (var p in profiles)
            {
                using var posed = new Posed(p);
                foreach (bool right in new[] { true, false })
                {
                    var target = right ? p.rightHand : p.leftHand;
                    string s = right ? "R" : "L";
                    if (target.role != HandRole.Grip || posed.CarrierIs(s)) continue;

                    foreach (var finger in Wrappers)
                    {
                        if (finger == "Index" && target.fingers == HandFingerStyle.IndexExtended) continue;
                        float gap = posed.GapFor(target, posed.Tip($"{finger}.3.{s}")) - FingerSkin;
                        if (gap < -0.006f || gap > 0.018f)
                            failures.Add($"{p.name} {s}: yema de '{finger}' a {gap * 1000f:0.0} mm de la piel (negativo = dentro)");
                    }
                    float thumb = posed.GapFor(target, posed.Tip($"Thumb.3.{s}")) - ThumbSkin;
                    if (thumb < -0.004f) failures.Add($"{p.name} {s}: el pulgar entra {-thumb * 1000f:0.0} mm");
                }
            }
            Assert.IsEmpty(failures, string.Join("\n", failures) + "\n" + Rebake);
        }

        [Test]
        public void LasFalangesDeLaManoSecundariaSonPosibles()
        {
            var profiles = BakedProfiles().ToList();
            if (profiles.Count == 0) Assert.Ignore("no hay perfiles horneados");
            var failures = new List<string>();
            foreach (var p in profiles)
            {
                using var posed = new Posed(p);
                foreach (bool right in new[] { true, false })
                {
                    var target = right ? p.rightHand : p.leftHand;
                    string s = right ? "R" : "L";
                    if (target.role != HandRole.Grip || posed.CarrierIs(s)) continue;
                    foreach (var finger in Wrappers)
                        for (int j = 1; j <= 3; j++)
                        {
                            var joint = posed.Bone($"{finger}.{j}.{s}");
                            joint.localRotation.ToAngleAxis(out float angle, out Vector3 axis);
                            if (angle > 180f) { angle = 360f - angle; axis = -axis; }
                            if (angle < 3f) continue;
                            // Los dedos flexionan sobre −X de su hueso partiendo de la neutra (ADR-077 enm. 5).
                            float flexion = -axis.x * angle;
                            if (flexion < -6f) failures.Add($"{p.name}: '{joint.name}' doblada {-flexion:0}° hacia atrás (hiperextensión)");
                            if (flexion > 110f) failures.Add($"{p.name}: '{joint.name}' flexiona {flexion:0}°");
                        }
                }
            }
            Assert.IsEmpty(failures, string.Join("\n", failures) + "\n" + Rebake);
        }

        /// <summary>
        /// Una mano con rol Reference ES la pose de su clip de referencia (la izquierda sobre el pomo, copiada de la
        /// cuerda): misma posición respecto del objeto y mismos dedos en el idle horneado.
        /// </summary>
        [Test]
        public void LaManoDeReferenciaCopiaSuClip()
        {
            var profiles = BakedProfiles().Where(p => p.rightHand.role == HandRole.Reference || p.leftHand.role == HandRole.Reference).ToList();
            if (profiles.Count == 0) Assert.Ignore("no hay perfiles horneados con una mano de referencia");
            var failures = new List<string>();
            foreach (var p in profiles)
                foreach (bool right in new[] { true, false })
                {
                    var target = right ? p.rightHand : p.leftHand;
                    if (target.role != HandRole.Reference) continue;
                    string s = right ? "R" : "L";
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(target.referenceClipPath);
                    Assert.IsNotNull(clip, $"{p.name}: falta el clip de referencia '{target.referenceClipPath}'");

                    using var baked = new Posed(p);
                    using var reference = new Posed(p, clip, target.referenceTime);
                    float drift = (baked.HandInGrip(s) - reference.HandInGrip(s)).magnitude;
                    if (drift > 0.006f) failures.Add($"{p.name} {s}: la mano se aparta {drift * 1000f:0.0} mm de su referencia");
                    foreach (var f in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
                        for (int j = 1; j <= 3; j++)
                        {
                            float angle = Quaternion.Angle(baked.Bone($"{f}.{j}.{s}").localRotation, reference.Bone($"{f}.{j}.{s}").localRotation);
                            if (angle > 3f) failures.Add($"{p.name} {s}: '{f}.{j}' difiere {angle:0.0}° de la referencia");
                        }
                }
            Assert.IsEmpty(failures, string.Join("\n", failures) + "\n" + Rebake);
        }

        [Test]
        public void LasDosManosNoSePisan()
        {
            var profiles = BakedProfiles().Where(p => p.kind == HandInteractionKind.TwoHand).ToList();
            if (profiles.Count == 0) Assert.Ignore("no hay perfiles a dos manos horneados");
            foreach (var p in profiles)
            {
                using var posed = new Posed(p);
                float closest = float.MaxValue;
                foreach (var a in posed.HandPoints("R"))
                    foreach (var b in posed.HandPoints("L"))
                        closest = Mathf.Min(closest, (a - b).magnitude);
                Assert.Greater(closest, 0.015f, $"{p.name}: las manos quedan a {closest * 1000f:0} mm entre articulaciones. {Rebake}");
            }
        }

        private static List<(AnimationClip original, AnimationClip effective)> Overrides(GameObject prefab)
        {
            var list = new List<(AnimationClip, AnimationClip)>();
            var wa = prefab.GetComponentInChildren<WieldableAnimator>(true);
            Assert.IsNotNull(wa, $"{prefab.name}: sin WieldableAnimator");
            var pairs = new SerializedObject(wa).FindProperty("_clips._clips");
            for (int i = 0; pairs != null && i < pairs.arraySize; i++)
            {
                var e = pairs.GetArrayElementAtIndex(i);
                var o = e.FindPropertyRelative("Original").objectReferenceValue as AnimationClip;
                var v = e.FindPropertyRelative("Override").objectReferenceValue as AnimationClip;
                if (o != null) list.Add((o, v != null ? v : o));
            }
            return list;
        }

        /// <summary>El wieldable con su idle efectivo muestreado en t = 0.</summary>
        private sealed class Posed : System.IDisposable
        {
            private readonly GameObject _instance;
            private readonly Transform _grip, _node;
            private readonly float[] _radii = new float[36];
            private readonly Bounds _bounds;

            public Posed(HandInteractionProfile p, AnimationClip clip = null, float time = 0f)
            {
                _instance = Object.Instantiate(p.wieldablePrefab);
                _instance.hideFlags = HideFlags.HideAndDontSave;
                _instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                foreach (var t in _instance.GetComponentsInChildren<Transform>(true))
                    if (t.name == "ViewModel" || t.name == "Root") t.gameObject.SetActive(true);

                var idle = clip != null ? clip
                    : Overrides(p.wieldablePrefab).FirstOrDefault(o => o.original.name.ToLowerInvariant().Contains("idle")).effective;
                Assert.IsNotNull(idle, $"{p.name}: sin clip de idle");
                var animator = _instance.GetComponentInChildren<Animator>(true);
                idle.SampleAnimation(animator.gameObject, clip != null ? Mathf.Clamp(time, 0f, clip.length) : 0f);
                animator.transform.localPosition = Vector3.zero;
                animator.transform.localRotation = Quaternion.identity;

                _node = Bone(p.modelNodeName);
                var meshes = _node.GetComponentsInChildren<MeshFilter>(true).Where(m => m.sharedMesh != null).ToList();
                var grip = string.IsNullOrEmpty(p.gripMeshNodeName) ? meshes.First() : meshes.First(m => m.name == p.gripMeshNodeName);
                _grip = grip.transform;
                var mesh = grip.sharedMesh;
                _bounds = mesh.bounds;
                var lists = Enumerable.Range(0, 36).Select(_ => new List<float>()).ToArray();
                float span = Mathf.Max(1e-5f, _bounds.size.y);
                foreach (var v in mesh.vertices)
                    lists[Mathf.Clamp(Mathf.FloorToInt((v.y - _bounds.min.y) / span * 36), 0, 35)].Add(Mathf.Sqrt(v.x * v.x + v.z * v.z));
                for (int i = 0; i < 36; i++)
                {
                    if (lists[i].Count == 0) { _radii[i] = i > 0 ? _radii[i - 1] : 0f; continue; }
                    lists[i].Sort();
                    _radii[i] = lists[i][Mathf.Clamp((int)(lists[i].Count * 0.9f), 0, lists[i].Count - 1)];
                }
            }

            /// <summary>La muñeca en el espacio de la malla de agarre, en metros.</summary>
            public Vector3 HandInGrip(string s)
                => Vector3.Scale(_grip.InverseTransformPoint(Bone($"Hand.{s}").position), _grip.lossyScale);

            public bool CarrierIs(string s)
            {
                for (var t = _node; t != null; t = t.parent)
                    if (t.name == $"Hand.{s}") return true;
                return false;
            }

            public Transform Bone(string name)
            {
                var t = _instance.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == name);
                Assert.IsNotNull(t, $"falta '{name}'");
                return t;
            }

            public Vector3 Tip(string lastPhalanx)
            {
                var t = Bone(lastPhalanx);
                return t.TransformPoint(0f, t.localPosition.magnitude * 0.9f, 0f);
            }

            public IEnumerable<Vector3> HandPoints(string s)
            {
                yield return Bone($"Hand.{s}").position;
                foreach (var f in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
                {
                    for (int j = 1; j <= 3; j++) yield return Bone($"{f}.{j}.{s}").position;
                    yield return Tip($"{f}.3.{s}");
                }
            }

            /// <summary>
            /// Distancia a la piel de lo que agarra ESA mano: la malla principal o, si el objetivo nombra una pieza
            /// (el pomo), el perfil de esa pieza sobre su eje y su tramo. Duplicado del editor a propósito.
            /// </summary>
            public float GapFor(HandGripTarget target, Vector3 world)
            {
                if (string.IsNullOrEmpty(target.gripPartNodeName)) return Gap(world);
                var part = _node.GetComponentsInChildren<MeshFilter>(true).First(m => m.name == target.gripPartNodeName);
                var mesh = part.sharedMesh;
                var axis = target.gripPartAxis.sqrMagnitude < 1e-8f ? Vector3.up : target.gripPartAxis.normalized;
                var b = mesh.bounds;
                float yMin = Mathf.Lerp(b.min.y, b.max.y, Mathf.Min(target.gripPartMinY01, target.gripPartMaxY01));
                float yMax = Mathf.Lerp(b.min.y, b.max.y, Mathf.Max(target.gripPartMinY01, target.gripPartMaxY01));
                var region = mesh.vertices.Where(v => v.y >= yMin - 1e-6f && v.y <= yMax + 1e-6f).ToList();
                Vector3 origin = Vector3.zero;
                foreach (var v in region) origin += v;
                origin /= Mathf.Max(1, region.Count);
                origin -= axis * Vector3.Dot(origin, axis);
                float minT = region.Min(v => Vector3.Dot(v - origin, axis)), maxT = region.Max(v => Vector3.Dot(v - origin, axis));
                const int bins = 36;
                var lists = Enumerable.Range(0, bins).Select(_ => new List<float>()).ToArray();
                float span = Mathf.Max(1e-5f, maxT - minT);
                foreach (var v in region)
                {
                    Vector3 d = v - origin;
                    float tt = Vector3.Dot(d, axis);
                    lists[Mathf.Clamp(Mathf.FloorToInt((tt - minT) / span * bins), 0, bins - 1)].Add((d - axis * tt).magnitude);
                }
                var radii = new float[bins];
                for (int i = 0; i < bins; i++)
                {
                    if (lists[i].Count == 0) { radii[i] = i > 0 ? radii[i - 1] : 0f; continue; }
                    lists[i].Sort();
                    radii[i] = lists[i][Mathf.Clamp((int)(lists[i].Count * 0.9f), 0, lists[i].Count - 1)];
                }
                Vector3 l = part.transform.InverseTransformPoint(world) - origin;
                float t = Vector3.Dot(l, axis), r = (l - axis * t).magnitude;
                float fIndex = Mathf.Clamp01((Mathf.Clamp(t, minT, maxT) - minT) / span) * (bins - 1);
                int j = Mathf.Clamp(Mathf.FloorToInt(fIndex), 0, bins - 2);
                float radius = Mathf.Lerp(radii[j], radii[j + 1], fIndex - j);
                float local;
                if (t < minT || t > maxT)
                {
                    float beyond = t < minT ? minT - t : t - maxT;
                    float outward = Mathf.Max(0f, r - radius);
                    local = Mathf.Sqrt(outward * outward + beyond * beyond);
                }
                else local = r - radius;
                return local * part.transform.lossyScale.y;
            }

            /// <summary>Distancia con signo a la piel por el perfil de radio de la malla de agarre, en metros.</summary>
            public float Gap(Vector3 world)
            {
                Vector3 l = _grip.InverseTransformPoint(world);
                float r = Mathf.Sqrt(l.x * l.x + l.z * l.z);
                float span = Mathf.Max(1e-5f, _bounds.size.y);
                float f = Mathf.Clamp01((Mathf.Clamp(l.y, _bounds.min.y, _bounds.max.y) - _bounds.min.y) / span) * 35;
                int i = Mathf.Clamp(Mathf.FloorToInt(f), 0, 34);
                float radius = Mathf.Lerp(_radii[i], _radii[i + 1], f - i);
                float local;
                if (l.y < _bounds.min.y || l.y > _bounds.max.y)
                {
                    float beyond = l.y < _bounds.min.y ? _bounds.min.y - l.y : l.y - _bounds.max.y;
                    float outward = Mathf.Max(0f, r - radius);
                    local = Mathf.Sqrt(outward * outward + beyond * beyond);
                }
                else local = r - radius;
                return local * _grip.lossyScale.y;
            }

            public void Dispose() => Object.DestroyImmediate(_instance);
        }
    }
}
