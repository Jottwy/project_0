using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// Agarre medido de los objetos en la mano del proxy (paso 1 de la linterna, 2026-09-14). Se prueba la
    /// geometría sobre manos y cadenas montadas a mano. Queda fuera, y se declara: los nombres de hueso del
    /// esqueleto real y el aspecto final — eso lo cubre la captura del arnés de poses.
    /// </summary>
    public sealed class ProxyGripSolverTests
    {
        // Mano derecha de juguete: dedos hacia +Z, índice hacia +X, palma hacia −Y (el pulgar por debajo).
        private static readonly Vector3 Wrist = Vector3.zero;
        private static readonly Vector3 IndexK = new Vector3(0.035f, 0f, 0.09f);
        private static readonly Vector3 MiddleK = new Vector3(0.012f, 0f, 0.095f);
        private static readonly Vector3 PinkyK = new Vector3(-0.035f, 0f, 0.08f);
        private static readonly Vector3 ThumbPalmSide = new Vector3(0.03f, -0.025f, 0.03f);

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        [Test]
        public void TheFrameFollowsTheKnucklesAndThePalmFacesTheThumb()
        {
            var f = ProxyGripSolver.Frame(Wrist, IndexK, MiddleK, PinkyK, ThumbPalmSide);
            Assert.Greater(Vector3.Dot(f.KnuckleAxis, Vector3.right), 0.98f);
            Assert.Greater(Vector3.Dot(f.FingerAxis, Vector3.forward), 0.98f);
            Assert.Greater(Vector3.Dot(f.PalmNormal, Vector3.down), 0.98f);
            Assert.AreEqual(0.0707f, f.Width, 0.001f);
        }

        [Test]
        public void ThePalmSideFlipsWithTheThumbNotWithTheHand()
        {
            var thumbOtherSide = new Vector3(ThumbPalmSide.x, -ThumbPalmSide.y, ThumbPalmSide.z);
            var f = ProxyGripSolver.Frame(Wrist, IndexK, MiddleK, PinkyK, thumbOtherSide);
            Assert.Greater(Vector3.Dot(f.PalmNormal, Vector3.up), 0.98f);
        }

        [Test]
        public void TheCylinderLiesAlongTheKnucklesARadiusPlusSkinIntoThePalm()
        {
            var f = ProxyGripSolver.Frame(Wrist, IndexK, MiddleK, PinkyK, ThumbPalmSide);
            const float radius = 0.02f;
            ProxyGripSolver.PlaceCylinder(f, radius, 0.03f, out var pos, out var rot);

            Vector3 axis = rot * Vector3.up;
            Assert.Greater(Vector3.Dot(axis, f.KnuckleAxis), 0.999f, "el +Y del objeto va hacia el índice");
            Assert.Greater(Vector3.Dot(rot * Vector3.right, f.PalmNormal), 0.999f, "el +X del objeto sale de la palma");

            Vector3 gripPoint = pos + axis * 0.03f;
            Vector3 expected = f.KnuckleCentre + f.PalmNormal * (radius + ProxyGripSolver.SkinMetres);
            Assert.Less(Vector3.Distance(gripPoint, expected), 1e-4f);
        }

        [Test]
        public void ATiltedCylinderCrossesTheFistDiagonallyButStaysOnThePalm()
        {
            var f = ProxyGripSolver.Frame(Wrist, IndexK, MiddleK, PinkyK, ThumbPalmSide);
            const float radius = 0.02f;
            ProxyGripSolver.PlaceCylinder(f, radius, 0.03f, out var pos, out var rot, 30f);

            Vector3 axis = rot * Vector3.up;
            Assert.AreEqual(30f, Vector3.Angle(axis, f.KnuckleAxis), 0.01f, "en diagonal por el ángulo pedido");
            Assert.AreEqual(90f, Vector3.Angle(axis, f.PalmNormal), 0.01f, "sin salirse del plano de la palma");

            Vector3 gripPoint = pos + axis * 0.03f;
            Vector3 expected = f.KnuckleCentre + f.PalmNormal * (radius + ProxyGripSolver.SkinMetres);
            Assert.Less(Vector3.Distance(gripPoint, expected), 1e-4f, "el punto agarrado no se mueve");
        }

        [Test]
        public void WithACrankTheHandGripsAheadOfItAndNeverPastTheEnd()
        {
            float grip = ProxyGripSolver.GripAlongAxis(0f, 0.09f, 0.07f, true, -0.0206f, 0.01f);
            Assert.AreEqual(-0.0206f + 0.035f + 0.01f, grip, 1e-4f);

            float clamped = ProxyGripSolver.GripAlongAxis(0f, 0.09f, 0.07f, true, 0.08f, 0.01f);
            Assert.AreEqual(0.09f - 0.035f, clamped, 1e-4f);

            Assert.AreEqual(0.01f, ProxyGripSolver.GripAlongAxis(0.01f, 0.09f, 0.07f, false, 0f, 0f), 1e-5f);
        }

        private (Transform upper, Transform lower, Transform end) Arm()
        {
            _root = new GameObject("shoulder");
            var upper = _root.transform;
            var lower = new GameObject("elbow").transform;
            lower.SetParent(upper, false);
            lower.localPosition = new Vector3(0f, -0.30f, 0f);
            var end = new GameObject("wrist").transform;
            end.SetParent(lower, false);
            end.localPosition = new Vector3(0f, -0.27f, 0f);
            return (upper, lower, end);
        }

        [Test]
        public void TwoBoneIkReachesAReachableTargetWithTheElbowTowardsThePole()
        {
            var (upper, lower, end) = Arm();
            Vector3 target = new Vector3(0f, -0.35f, 0.25f);
            Vector3 pole = new Vector3(0f, -0.3f, -0.5f);
            ProxyGripSolver.TwoBoneIk(upper, lower, end, target, pole);

            Assert.Less(Vector3.Distance(end.position, target), 0.001f);
            Assert.AreEqual(0.30f, Vector3.Distance(upper.position, lower.position), 1e-4f, "el hueso no se estira");
            Assert.Less(lower.position.z, 0.1f, "el codo va hacia atrás, del lado del polo");
        }

        [Test]
        public void TwoBoneIkPointsStraightAtAnUnreachableTarget()
        {
            var (upper, lower, end) = Arm();
            Vector3 target = new Vector3(0f, 0f, 2f);
            ProxyGripSolver.TwoBoneIk(upper, lower, end, target, Vector3.down);

            Assert.AreEqual(0.57f, Vector3.Distance(upper.position, end.position), 0.002f);
            Assert.Greater(Vector3.Dot((end.position - upper.position).normalized, Vector3.forward), 0.999f);
        }

        private Transform[] Finger(Vector3 knuckle)
        {
            _root = new GameObject("hand");
            var chain = new Transform[3];
            Transform parent = _root.transform;
            for (int i = 0; i < 3; i++)
            {
                chain[i] = new GameObject("phalanx" + i).transform;
                chain[i].SetParent(parent, false);
                chain[i].localPosition = i == 0 ? knuckle : new Vector3(0f, 0f, 0.035f);
                parent = chain[i];
            }
            return chain;
        }

        [TestCase(1f)]
        [TestCase(-1f)]
        public void TheFingerClosesTowardsTheObjectUntilItTouchesWhicheverSideItIs(float side)
        {
            // Dedo recto hacia +Z; el cilindro, eje X, por debajo (side 1) o por encima (side −1).
            var chain = Finger(Vector3.zero);
            const float radius = 0.02f;
            Vector3 axisPoint = new Vector3(0f, -side * (radius + 0.012f), 0.03f);

            float before = Closest(chain, axisPoint, radius);
            float angle = ProxyGripSolver.CurlToTouch(chain, Vector3.right, axisPoint, Vector3.right, radius);
            float after = Closest(chain, axisPoint, radius);

            Assert.AreNotEqual(0f, angle, "el dedo tiene que cerrar");
            Assert.Less(after, before, "cerrar acerca el dedo al objeto");
            Assert.Greater(after, -0.001f, "ninguna falange atraviesa el objeto");
            Assert.Less(after, 0.006f, "el dedo llega a tocar, no se queda a medio camino");
        }

        // Lo más cerca que queda del cilindro cualquier articulación (salvo el nudillo) o la yema.
        private static float Closest(Transform[] chain, Vector3 axisPoint, float radius)
        {
            float best = ProxyGripSolver.DistanceToCylinder(ProxyGripSolver.Tip(chain), axisPoint, Vector3.right, radius);
            for (int i = 1; i < chain.Length; i++)
                best = Mathf.Min(best, ProxyGripSolver.DistanceToCylinder(chain[i].position, axisPoint, Vector3.right, radius));
            return best;
        }
    }
}
