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
                        float gap = posed.Gap(posed.Tip($"{finger}.3.{s}")) - FingerSkin;
                        if (gap < -0.006f || gap > 0.018f)
                            failures.Add($"{p.name} {s}: yema de '{finger}' a {gap * 1000f:0.0} mm de la piel (negativo = dentro)");
                    }
                    float thumb = posed.Gap(posed.Tip($"Thumb.3.{s}")) - ThumbSkin;
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

            public Posed(HandInteractionProfile p)
            {
                _instance = Object.Instantiate(p.wieldablePrefab);
                _instance.hideFlags = HideFlags.HideAndDontSave;
                _instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                foreach (var t in _instance.GetComponentsInChildren<Transform>(true))
                    if (t.name == "ViewModel" || t.name == "Root") t.gameObject.SetActive(true);

                var idle = Overrides(p.wieldablePrefab).FirstOrDefault(o => o.original.name.ToLowerInvariant().Contains("idle")).effective;
                Assert.IsNotNull(idle, $"{p.name}: sin clip de idle");
                var animator = _instance.GetComponentInChildren<Animator>(true);
                idle.SampleAnimation(animator.gameObject, 0f);
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
