using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-077 enm. 4 — el agarre HORNEADO del destornillador y del bote de spray, medido.
    ///
    /// Joel lo dijo en dos frases que son las dos pruebas de aquí: «¿quién coge un destornillador
    /// con dos manos?» y «la mano sigue sin adaptarse, hay que mover los dedos para que agarre».
    /// Colocar la malla no basta: los dedos son huesos animados y venían del clip del donante (el
    /// hacha, de DOS manos; la antorcha, de otro diámetro). El horneador
    /// (<c>BackroomsToolPoseBaker</c>) cierra cada falange por contacto contra la malla real y
    /// recoge la izquierda; esto comprueba el resultado sin abrir el editor.
    ///
    /// Las constantes están DUPLICADAS del horneador a propósito: los tests no ven el ensamblado
    /// del editor, y un cambio en uno sin el otro sale en rojo aquí, que es lo que se quiere.
    /// </summary>
    [TestFixture]
    public class ToolGripTests
    {
        private const string Bake = "Rehornear con «Backrooms ▸ Screwdriver|Spray ▸ Hornear pose y dedos».";

        private const float FingerSkin = 0.0085f;
        private static readonly string[] Wrappers = { "Index", "Middle", "Ring", "Pinky" };

        private sealed class Tool
        {
            public string Name, PrefabPath, NodeName, IdleClipPath;
            public bool OneHanded;
            public bool IndexOnNozzle;
        }

        private static readonly Tool Screwdriver = new()
        {
            Name = "destornillador",
            PrefabPath = "Assets/Prefabs/Wieldables/BR_Wieldable_Screwdriver.prefab",
            NodeName = "BR_ScrewdriverModel",
            IdleClipPath = "Assets/Art/Items/Screwdriver/Anim/BR_Screwdriver_Idle.anim",
            OneHanded = true,
        };

        private static readonly Tool SprayCan = new()
        {
            Name = "bote de spray",
            PrefabPath = "Assets/Prefabs/Wieldables/BR_Wieldable_SprayCan.prefab",
            NodeName = "BR_SprayCanModel",
            IdleClipPath = "Assets/Art/Items/SprayCan/Anim/BR_SprayCan_Idle.anim",
            IndexOnNozzle = true,
        };

        private static Tool[] Tools => new[] { Screwdriver, SprayCan };

        [Test]
        public void CadaHerramientaTieneSusClipsPropiosYElWieldableLosUsa()
        {
            foreach (var tool in Tools)
            {
                var idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(tool.IdleClipPath);
                Assert.IsNotNull(idle, $"{tool.Name}: falta el clip de idle horneado. {Bake}");
                Assert.Greater(idle.length, 0.1f, $"{tool.Name}: clip vacío");

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(tool.PrefabPath);
                Assert.IsNotNull(prefab, tool.PrefabPath);
                var animator = prefab.GetComponentInChildren<PolymindGames.WieldableSystem.WieldableAnimator>(true);
                Assert.IsNotNull(animator, $"{tool.Name}: sin WieldableAnimator");

                var so = new SerializedObject(animator);
                var pairs = so.FindProperty("_clips._clips");
                bool used = false;
                for (int i = 0; pairs != null && i < pairs.arraySize; i++)
                    if (pairs.GetArrayElementAtIndex(i).FindPropertyRelative("Override").objectReferenceValue == idle)
                        used = true;
                Assert.IsTrue(used, $"{tool.Name}: el clip horneado existe pero el wieldable sigue con el del donante. {Bake}");
            }
        }

        /// <summary>
        /// LOS DEDOS TOCAN EL OBJETO. Se muestrea el idle horneado y se mide la yema de los cuatro
        /// dedos que rodean contra la superficie real (perfil de radio de la malla, como el
        /// horneador). Antes del horneado quedaban a 42-68 mm — una garra al lado del objeto.
        /// </summary>
        [Test]
        public void LosDedosQueRodeanTocanElObjeto()
        {
            foreach (var tool in Tools)
            {
                using var posed = new PosedInstance(tool, 0f);
                var node = posed.Bone(tool.NodeName);
                var mesh = node.GetComponentInChildren<MeshFilter>(true)?.sharedMesh;
                Assert.IsNotNull(mesh, $"{tool.Name}: el nodo no trae malla");

                foreach (var finger in Wrappers)
                {
                    // El índice del bote NO rodea: va sobre el pulsador, y tiene su propia prueba.
                    if (tool.IndexOnNozzle && finger == "Index") continue;

                    Vector3 tip = posed.FingerTip(finger + ".3.R");
                    float gap = SurfaceGap(node, mesh, tip) - FingerSkin;
                    Assert.That(gap, Is.InRange(-0.004f, 0.012f),
                        $"{tool.Name}: la yema del dedo '{finger}' está a {gap * 1000f:0.0} mm de la piel del objeto " +
                        $"(negativo = dentro). No lo agarra. {Bake}");
                }
            }
        }

        /// <summary>El índice del bote descansa en el PULSADOR, arriba: es el dedo que aprieta.</summary>
        [Test]
        public void ElIndiceDelBoteVaSobreElPulsador()
        {
            using var posed = new PosedInstance(SprayCan, 0f);
            var node = posed.Bone(SprayCan.NodeName);
            var mesh = node.GetComponentInChildren<MeshFilter>(true)?.sharedMesh;
            Assert.IsNotNull(mesh);

            Vector3 tipLocal = node.InverseTransformPoint(posed.FingerTip("Index.3.R"));
            float top = mesh.bounds.max.y;
            Assert.That(tipLocal.y, Is.GreaterThan(top - 0.045f),
                $"la yema del índice está {(top - tipLocal.y) * 100f:0.0} cm por debajo de la punta de la lata: " +
                $"no llega al pulsador. {Bake}");

            // Y sin metérsele dentro: el atajo por el interior de la lata ya pasó una vez.
            float radial = Mathf.Sqrt(tipLocal.x * tipLocal.x + tipLocal.z * tipLocal.z);
            Assert.Greater(radial, 0.002f, "la yema del índice está clavada en el eje de la lata");
        }

        /// <summary>
        /// UN DESTORNILLADOR SE COGE CON UNA MANO. Hereda los clips del hacha, que es de dos, así
        /// que el horneador recoge la izquierda: aquí se comprueba que se quedó lejos del objeto.
        /// </summary>
        [Test]
        public void ElDestornilladorNoSeCogeConLasDosManos()
        {
            using var posed = new PosedInstance(Screwdriver, 0f);
            var node = posed.Bone(Screwdriver.NodeName);
            var mesh = node.GetComponentInChildren<MeshFilter>(true)?.sharedMesh;
            Assert.IsNotNull(mesh);

            foreach (var finger in Wrappers)
            {
                Vector3 tip = posed.FingerTip(finger + ".3.L");
                float gap = SurfaceGap(node, mesh, tip);
                Assert.Greater(gap, 0.05f,
                    $"la yema izquierda de '{finger}' está a {gap * 100f:0.0} cm del destornillador: " +
                    $"lo sigue agarrando con las dos manos. {Bake}");
            }
        }

        /// <summary>Distancia con signo a la piel del objeto, contra su perfil de radio por tramos —
        /// la misma cuenta que hace el horneador (un mango es más gordo que su vástago).</summary>
        private static float SurfaceGap(Transform node, Mesh mesh, Vector3 worldPoint)
        {
            const int bins = 36;
            var b = mesh.bounds;
            float span = Mathf.Max(1e-5f, b.size.y);
            var radii = new float[bins];
            var lists = new System.Collections.Generic.List<float>[bins];
            for (int i = 0; i < bins; i++) lists[i] = new System.Collections.Generic.List<float>();
            foreach (var v in mesh.vertices)
            {
                int bin = Mathf.Clamp(Mathf.FloorToInt((v.y - b.min.y) / span * bins), 0, bins - 1);
                lists[bin].Add(Mathf.Sqrt(v.x * v.x + v.z * v.z));
            }
            for (int i = 0; i < bins; i++)
            {
                if (lists[i].Count == 0) { radii[i] = i > 0 ? radii[i - 1] : 0f; continue; }
                lists[i].Sort();
                radii[i] = lists[i][Mathf.Clamp((int)(lists[i].Count * 0.9f), 0, lists[i].Count - 1)];
            }

            Vector3 l = node.InverseTransformPoint(worldPoint);
            float r = Mathf.Sqrt(l.x * l.x + l.z * l.z);
            float f = Mathf.Clamp01((Mathf.Clamp(l.y, b.min.y, b.max.y) - b.min.y) / span) * (bins - 1);
            int j = Mathf.Clamp(Mathf.FloorToInt(f), 0, bins - 2);
            float radius = Mathf.Lerp(radii[j], radii[j + 1], f - j);

            if (l.y < b.min.y || l.y > b.max.y)
            {
                float beyond = l.y < b.min.y ? b.min.y - l.y : l.y - b.max.y;
                float outward = Mathf.Max(0f, r - radius);
                return Mathf.Sqrt(outward * outward + beyond * beyond);
            }
            return r - radius;
        }

        /// <summary>Una instancia del prefab con el clip horneado muestreado.</summary>
        private sealed class PosedInstance : System.IDisposable
        {
            private readonly GameObject _instance;

            public PosedInstance(Tool tool, float fraction)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(tool.PrefabPath);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(tool.IdleClipPath);
                Assert.IsNotNull(prefab, tool.PrefabPath);
                Assert.IsNotNull(clip, $"{tool.Name}: falta '{tool.IdleClipPath}'. {Bake}");

                _instance = Object.Instantiate(prefab);
                _instance.hideFlags = HideFlags.HideAndDontSave;
                _instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                foreach (var t in _instance.GetComponentsInChildren<Transform>(true))
                    if (t.name == "ViewModel" || t.name == "Root") t.gameObject.SetActive(true);

                var animator = _instance.GetComponentInChildren<Animator>(true);
                Assert.IsNotNull(animator, $"{tool.Name}: el prefab no tiene Animator");
                clip.SampleAnimation(animator.gameObject, clip.length * Mathf.Clamp01(fraction));
            }

            public Transform Bone(string name)
            {
                var t = _instance.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == name);
                Assert.IsNotNull(t, $"falta '{name}' en el prefab");
                return t;
            }

            /// <summary>La yema: la última falange más su propio largo, como mide el horneador.</summary>
            public Vector3 FingerTip(string lastPhalanx)
            {
                var t = Bone(lastPhalanx);
                float length = t.localPosition.magnitude * 0.9f;
                return t.TransformPoint(0f, length, 0f);
            }

            public void Dispose() => Object.DestroyImmediate(_instance);
        }
    }
}
