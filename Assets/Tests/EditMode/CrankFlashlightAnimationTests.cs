using System.Collections.Generic;
using NUnit.Framework;
using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-133 enm. 1 — las animaciones propias de la linterna, medidas en vez de miradas. Joel
    /// pidió la mano «al milímetro», y un milímetro no se comprueba con una captura: se muestrea
    /// el clip sobre el prefab y se mide la distancia de cada falange a la piel del tubo, dónde
    /// cae el puño respecto del ojo, y si el pomo entra en la mano izquierda a cada fase de la
    /// vuelta. Todo lo que aquí es un número era una decisión del horneador
    /// (<c>BackroomsCrankFlashlightPoseBaker</c>): si él cambia, esto dice si sigue valiendo.
    ///
    /// Las constantes de geometría (punto de agarre izquierdo, cómo se localiza el pomo en la
    /// malla de la manivela) están DUPLICADAS del horneador a propósito: los tests no pueden
    /// referenciar el ensamblado del editor, y un cambio en una sin la otra sale en rojo aquí,
    /// que es lo que se quiere.
    /// </summary>
    [TestFixture]
    public class CrankFlashlightAnimationTests
    {
        private const string PrefabPath = "Assets/Prefabs/Wieldables/BR_Wieldable_CrankFlashlight.prefab";
        private const string AnimFolder = "Assets/Art/Items/CrankFlashlight/Anim";
        private const string IdleClipPath = AnimFolder + "/BR_CrankFlashlight_Idle.anim";
        private const string EquipClipPath = AnimFolder + "/BR_CrankFlashlight_Equip.anim";
        private const string HolsterClipPath = AnimFolder + "/BR_CrankFlashlight_Holster.anim";
        private const string CrankClipPath = AnimFolder + "/BR_CrankFlashlight_Crank.anim";
        private const string ControllerPath = AnimFolder + "/BR_CrankFlashlight.controller";
        private const string BodyMeshPath = "Assets/Art/Items/CrankFlashlight/BR_CrankFlashlight_Body_Mesh.asset";
        private const string CrankMeshPath = "Assets/Art/Items/CrankFlashlight/BR_CrankFlashlight_Crank_Mesh.asset";
        private const string CrankLayerName = "Crank";

        /// <summary>Del horneador: reposo de la manivela y su eje de giro, en local del nodo Crank.</summary>
        private static readonly Quaternion CrankRest = Quaternion.Euler(0f, 90f, 0f) * Quaternion.AngleAxis(270f, Vector3.forward);
        private static readonly Vector3 CrankSpinAxis = Vector3.forward;

        private static readonly string[] Fingers = { "Index", "Middle", "Ring", "Pinky" };

        private static T Load<T>(string path) where T : Object => UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);

        private static string Bake => "Ejecuta 'Backrooms/Linterna/Hornear animaciones'.";

        [Test]
        public void TheFourClipsExistAndOnlyIdleAndCrankLoop()
        {
            var idle = Load<AnimationClip>(IdleClipPath);
            var equip = Load<AnimationClip>(EquipClipPath);
            var holster = Load<AnimationClip>(HolsterClipPath);
            var crank = Load<AnimationClip>(CrankClipPath);
            Assert.IsNotNull(idle, "falta el idle horneado. " + Bake);
            Assert.IsNotNull(equip, "falta el equipar horneado. " + Bake);
            Assert.IsNotNull(holster, "falta el enfundar horneado. " + Bake);
            Assert.IsNotNull(crank, "falta la cuerda horneada. " + Bake);

            Assert.IsTrue(idle.isLooping, "el idle tiene que ser un bucle o la mano se congela al acabar");
            Assert.IsTrue(crank.isLooping, "la cuerda tiene que ser un bucle: una vuelta por ciclo");
            Assert.IsFalse(equip.isLooping, "equipar no es un bucle");
            Assert.IsFalse(holster.isLooping, "enfundar no es un bucle");
            Assert.Greater(idle.length, 1f, "un idle de menos de un segundo es un tic, no un balanceo");
            Assert.That(crank.length, Is.EqualTo(1f).Within(0.05f),
                "la cuerda dura una vuelta a 1 vuelta/s: la fase del Animator ES el ángulo de la manivela");
        }

        [Test]
        public void TheControllerCarriesACrankLayerAtZeroWeightWithTheCrankClip()
        {
            var controller = Load<UnityEditor.Animations.AnimatorController>(ControllerPath);
            var crank = Load<AnimationClip>(CrankClipPath);
            Assert.IsNotNull(controller, "falta el controller propio. " + Bake);
            Assert.IsNotNull(crank);

            UnityEditor.Animations.AnimatorControllerLayer layer = null;
            foreach (var l in controller.layers) if (l.name == CrankLayerName) layer = l;
            Assert.IsNotNull(layer, $"el controller no tiene la capa '{CrankLayerName}'");
            Assert.AreEqual(0f, layer.defaultWeight, "la capa de cuerda nace a peso 0: la sube el wieldable");
            Assert.AreEqual(UnityEditor.Animations.AnimatorLayerBlendingMode.Override, layer.blendingMode);

            bool hasState = false;
            foreach (var s in layer.stateMachine.states)
                if (s.state.motion == crank) hasState = true;
            Assert.IsTrue(hasState, "la capa de cuerda no reproduce el clip de cuerda");
            Assert.AreEqual("Base Layer", controller.layers[0].name,
                "la capa base sigue siendo la del Template_Tool: equipar/enfundar/usar dependen de ella");
        }

        [Test]
        public void ThePrefabPlaysOurControllerAndOverridesIdleWithOurs()
        {
            var prefab = Load<GameObject>(PrefabPath);
            var controller = Load<UnityEditor.Animations.AnimatorController>(ControllerPath);
            var idle = Load<AnimationClip>(IdleClipPath);
            Assert.IsNotNull(prefab); Assert.IsNotNull(controller); Assert.IsNotNull(idle);

            var wieldableAnimator = prefab.GetComponentInChildren<WieldableAnimator>(true);
            Assert.IsNotNull(wieldableAnimator, "el prefab no tiene WieldableAnimator");
            var so = new UnityEditor.SerializedObject(wieldableAnimator);
            Assert.AreSame(controller, so.FindProperty("_clips._controller").objectReferenceValue,
                "el WieldableAnimator no apunta al controller propio: la capa de cuerda no existiría en juego");

            var pairs = so.FindProperty("_clips._clips");
            AnimationClip idleOverride = null;
            for (int i = 0; i < pairs.arraySize; i++)
            {
                var e = pairs.GetArrayElementAtIndex(i);
                var original = e.FindPropertyRelative("Original").objectReferenceValue as AnimationClip;
                if (original != null && original.name.ToLowerInvariant().Contains("idle"))
                    idleOverride = e.FindPropertyRelative("Override").objectReferenceValue as AnimationClip;
            }
            Assert.AreSame(idle, idleOverride, "el override de Idle no es el idle horneado");
            Assert.AreEqual(controller.animationClips.Length, pairs.arraySize,
                "la lista de overrides no casa con los clips del controller: el inspector la regeneraría vacía");

            var animator = prefab.GetComponentInChildren<Animator>(true);
            Assert.AreSame(controller, animator.runtimeAnimatorController, "el Animator del viewmodel no lleva el controller propio");
        }

        [Test]
        public void TheModelHangsFromTheRightHand()
        {
            var prefab = Load<GameObject>(PrefabPath);
            var node = FindChild(prefab.transform, "BR_CrankFlashlightModel");
            Assert.IsNotNull(node, "sin nodo del modelo");
            Assert.AreEqual("Hand.R", node.parent.name,
                "puño y tubo son una pieza: el modelo cuelga de Hand.R, no del hueso de la antorcha");
        }

        [Test]
        public void InIdleTheLensPointsForwardAndTheFistSitsBelowTheEye()
        {
            using var posed = new PosedInstance(IdleClipPath, 0f);
            var camera = posed.Bone("Camera");
            var root = posed.Bone("Root");
            var node = posed.Bone("BR_CrankFlashlightModel");
            var hand = posed.Bone("Hand.R");

            Vector3 lens = root.InverseTransformDirection(node.up);
            Assert.Less(Vector3.Angle(lens, Vector3.forward), 12f,
                $"la lente apunta a {lens} en espacio del root: más de 12° del frente");

            Vector3 fist = root.InverseTransformPoint(hand.position) - root.InverseTransformPoint(camera.position);
            Assert.That(fist.y, Is.InRange(-0.30f, -0.06f), $"el puño está a {fist.y:F2} m del ojo en vertical: ni bajo ni a la altura del ojo");
            Assert.That(fist.x, Is.InRange(0.0f, 0.30f), $"el puño está a {fist.x:F2} m a la derecha del ojo");
            Assert.That(fist.z, Is.InRange(0.25f, 0.55f), $"el puño está a {fist.z:F2} m por delante del ojo");
        }

        /// <summary>
        /// «Al milímetro»: cada dedo toca el tubo y ninguno lo atraviesa. Se mide la distancia del
        /// hueso de cada falange al eje del tubo contra su radio: un hueso a menos de 4 mm de la
        /// piel está DENTRO del dedo que lo rodea, y un dedo cuyo hueso más cercano queda a más de
        /// 16 mm de la piel no la toca.
        /// </summary>
        [Test]
        public void InIdleEveryRightFingerTouchesTheBodyWithoutSinkingIn()
        {
            var body = Load<Mesh>(BodyMeshPath);
            Assert.IsNotNull(body);
            float radius = body.bounds.extents.x;
            float halfLength = body.bounds.extents.y;

            using var posed = new PosedInstance(IdleClipPath, 0f);
            var node = posed.Bone("BR_CrankFlashlightModel");
            var failures = new List<string>();

            foreach (var finger in Fingers)
            {
                float closest = float.MaxValue;
                foreach (var p in FingerPoints(posed, finger, "R"))
                {
                    Vector3 local = node.InverseTransformPoint(p);
                    if (Mathf.Abs(local.y) > halfLength) continue; // fuera del tubo por los extremos
                    float gap = Mathf.Sqrt(local.x * local.x + local.z * local.z) - radius;
                    closest = Mathf.Min(closest, gap);
                }
                if (closest == float.MaxValue) failures.Add($"{finger}: ninguna falange sobre el tubo");
                else if (closest < 0.003f) failures.Add($"{finger}: hueso a {closest * 1000f:F1} mm de la piel, dentro del tubo");
                else if (closest > 0.016f) failures.Add($"{finger}: hueso a {closest * 1000f:F1} mm de la piel, no la toca");
            }

            // El pulgar aparte: su base la deja el vendor, y sólo se le pide que la yema llegue.
            float thumb = float.MaxValue;
            foreach (var p in FingerPoints(posed, "Thumb", "R"))
            {
                Vector3 local = node.InverseTransformPoint(p);
                if (Mathf.Abs(local.y) > halfLength) continue;
                thumb = Mathf.Min(thumb, Mathf.Sqrt(local.x * local.x + local.z * local.z) - radius);
            }
            if (thumb < 0.003f) failures.Add($"Thumb: hueso a {thumb * 1000f:F1} mm de la piel, dentro del tubo");
            else if (thumb > 0.022f) failures.Add($"Thumb: hueso a {thumb * 1000f:F1} mm de la piel, no la toca");

            Assert.IsEmpty(failures, string.Join("; ", failures));
        }

        [Test]
        public void WhileCrankingTheLeftHandRidesTheKnob()
        {
            var crankMesh = Load<Mesh>(CrankMeshPath);
            Assert.IsNotNull(crankMesh);
            Vector3 knobLocal = KnobLocal(crankMesh);
            var failures = new List<string>();

            foreach (float phase in new[] { 0f, 0.25f, 0.5f, 0.75f })
            {
                using var posed = new PosedInstance(CrankClipPath, phase);
                var crank = posed.Bone("Crank");
                crank.localRotation = CrankRest * Quaternion.AngleAxis(phase * 360f, CrankSpinAxis);
                Vector3 knob = crank.TransformPoint(knobLocal);
                // Lo que ENCIERRA la mano cerrada: la media de las segundas falanges de los cuatro
                // dedos y la yema del pulgar (mismo centro que mide el horneador). Los dedos rodean
                // el pomo, no lo atraviesan, así que ese centro queda a un dedo del eje del pomo.
                Vector3 enclosed = Vector3.zero;
                foreach (var f in Fingers) enclosed += posed.Bone($"{f}.2.L").position;
                var thumb3 = posed.Bone("Thumb.3.L");
                enclosed += thumb3.TransformPoint(0f, thumb3.localPosition.magnitude * 0.9f, 0f);
                enclosed /= 5f;
                float error = (knob - enclosed).magnitude;
                if (error > 0.020f)
                    failures.Add($"fase {phase:P0}: el pomo queda a {error * 1000f:F0} mm del centro de la mano izquierda cerrada");
            }
            Assert.IsEmpty(failures, string.Join("; ", failures));
        }

        /// <summary>
        /// El pomo describe un círculo pegado al tubo, y el tubo está dentro del puño derecho: en
        /// ninguna fase el pomo puede pasar por una falange. Se recorre la vuelta entera con la
        /// derecha en su pose de cuerda a fase 0 (su bamboleo es de un grado).
        /// </summary>
        [Test]
        public void TheKnobOrbitClearsTheRightHand()
        {
            var crankMesh = Load<Mesh>(CrankMeshPath);
            Vector3 knobLocal = KnobLocal(crankMesh);
            using var posed = new PosedInstance(CrankClipPath, 0f);
            var crank = posed.Bone("Crank");

            var points = new List<(string name, Vector3 p)>();
            foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
                foreach (var p in FingerPoints(posed, finger, "R"))
                    points.Add((finger, p));

            float worst = float.MaxValue; string worstName = "";
            for (int deg = 0; deg < 360; deg += 10)
            {
                crank.localRotation = CrankRest * Quaternion.AngleAxis(deg, CrankSpinAxis);
                Vector3 knob = crank.TransformPoint(knobLocal);
                foreach (var (name, p) in points)
                {
                    float d = (knob - p).magnitude;
                    if (d < worst) { worst = d; worstName = $"{name} a {deg}°"; }
                }
            }
            // 8 mm de pomo + 8,5 mm de dedo: por debajo de 17 mm se tocan.
            Assert.Greater(worst, 0.017f,
                $"el pomo pasa a {worst * 1000f:F0} mm del hueso de {worstName}: atraviesa la mano derecha");
        }

        // ─── utilería ────────────────────────────────────────────────────────────────────────────

        private static IEnumerable<Vector3> FingerPoints(PosedInstance posed, string finger, string side)
        {
            var j2 = posed.Bone($"{finger}.2.{side}");
            var j3 = posed.Bone($"{finger}.3.{side}");
            yield return j2.position;
            yield return Vector3.Lerp(j2.position, j3.position, 0.5f);
            yield return j3.position;
            float tip = j3.localPosition.magnitude * 0.9f;
            yield return j3.TransformPoint(0f, tip * 0.5f, 0f);
            yield return j3.TransformPoint(0f, tip, 0f);
        }

        /// <summary>Del horneador: el pomo es el centroide del cuarto más lejano del eje.</summary>
        private static Vector3 KnobLocal(Mesh crank)
        {
            float maxY = crank.bounds.max.y;
            float cut = maxY - crank.bounds.size.y * 0.25f;
            Vector3 sum = Vector3.zero; int n = 0;
            foreach (var v in crank.vertices) { if (v.y < cut) continue; sum += v; n++; }
            return n > 0 ? sum / n : new Vector3(0f, maxY, 0f);
        }

        private static Transform FindChild(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        /// <summary>Una instancia del prefab con un clip muestreado en una fracción de su duración.</summary>
        private sealed class PosedInstance : System.IDisposable
        {
            private readonly GameObject _instance;

            public PosedInstance(string clipPath, float fraction)
            {
                var prefab = Load<GameObject>(PrefabPath);
                var clip = Load<AnimationClip>(clipPath);
                Assert.IsNotNull(prefab, "falta el prefab del wieldable");
                Assert.IsNotNull(clip, $"falta el clip '{clipPath}'. {Bake}");

                _instance = Object.Instantiate(prefab);
                _instance.hideFlags = HideFlags.HideAndDontSave;
                _instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                foreach (var t in _instance.GetComponentsInChildren<Transform>(true))
                    if (t.name == "ViewModel" || t.name == "Root") t.gameObject.SetActive(true);

                var animator = _instance.GetComponentInChildren<Animator>(true);
                Assert.IsNotNull(animator, "el prefab no tiene Animator");
                clip.SampleAnimation(animator.gameObject, clip.length * Mathf.Clamp01(fraction));
            }

            public Transform Bone(string name)
            {
                var t = FindChild(_instance.transform, name);
                Assert.IsNotNull(t, $"falta '{name}' en el prefab");
                return t;
            }

            public void Dispose() => Object.DestroyImmediate(_instance);
        }
    }
}
