using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// El libro abierto del vecino (bit BookOpen, 2026-09-14): dónde va, hacia dónde miran sus páginas y dónde caen
    /// las manos. Queda fuera, y se declara: la malla horneada y el aspecto final — eso lo cubre el arnés de poses.
    /// </summary>
    public sealed class ProxyBookHoldTests
    {
        // Cuerpo mirando a +Z con los hombros a 1,42 m.
        private static readonly Vector3 ShoulderL = new Vector3(-0.18f, 1.42f, 0f);
        private static readonly Vector3 ShoulderR = new Vector3(0.18f, 1.42f, 0f);

        private static void Pose(out Vector3 centre, out Quaternion rotation) =>
            ProxyBookHold.BookPose(ShoulderL, ShoulderR, Vector3.forward, Vector3.up, out centre, out rotation);

        [Test]
        public void TheBookSitsInFrontOfTheChestBelowTheShoulders()
        {
            Pose(out Vector3 centre, out _);
            Assert.AreEqual(1.42f - ProxyBookHold.BelowShoulders, centre.y, 1e-4f);
            Assert.AreEqual(ProxyBookHold.InFront, centre.z, 1e-4f);
            Assert.AreEqual(0f, centre.x, 1e-4f);
        }

        [Test]
        public void TheOpenPagesFaceTheEyes()
        {
            Pose(out Vector3 centre, out Quaternion rotation);
            Vector3 eyes = new Vector3(0f, 1.42f + ProxyBookHold.EyesAboveShoulders, ProxyBookHold.EyesInFront);
            Vector3 pages = rotation * Vector3.forward;
            Assert.Greater(Vector3.Dot(pages, (eyes - centre).normalized), 0.999f);
            Assert.Greater(pages.y, 0.5f, "las páginas miran hacia arriba");
            Assert.Less(pages.z, 0f, "y hacia el lector, no hacia delante");
        }

        [Test]
        public void TheTopOfThePageLeansAwayFromTheReader()
        {
            Pose(out _, out Quaternion rotation);
            Vector3 top = rotation * Vector3.up;
            Assert.Greater(top.y, 0f);
            Assert.Greater(top.z, 0f);
            Assert.AreEqual(0f, top.x, 1e-4f, "sin alabeo: el lomo queda en el plano de simetría");
        }

        [Test]
        public void EachHandTakesTheEdgeOnItsOwnSide()
        {
            Pose(out _, out Quaternion rotation);
            Assert.Greater(ProxyBookHold.SideAxis(rotation, Vector3.right, true).x, 0.99f);
            Assert.Less(ProxyBookHold.SideAxis(rotation, Vector3.right, false).x, -0.99f);
            // Aunque el libro venga girado medio vuelta sobre su normal, cada mano sigue en su lado.
            Quaternion flipped = rotation * Quaternion.AngleAxis(180f, Vector3.forward);
            Assert.Greater(ProxyBookHold.SideAxis(flipped, Vector3.right, true).x, 0.99f);
        }

        [Test]
        public void TheKnucklesGoBehindTheCoverInsideTheEdgeWithTheFingersTowardTheSpine()
        {
            Pose(out Vector3 centre, out Quaternion rotation);
            Vector3 side = ProxyBookHold.SideAxis(rotation, Vector3.right, true);
            const float half = 0.15f, back = 0.02f, inset = 0.03f;
            var flat = new ProxyBookHold.Cover { HalfWidth = half, BackZAtSpine = -back, BackSlope = 0f };
            var grip = ProxyBookHold.Grip(centre, rotation, side, flat, inset, 0f, 0f);
            Vector3 toReader = rotation * Vector3.forward;

            Assert.AreEqual(-(back + ProxyBookHold.KnuckleBehindMetres), Vector3.Dot(grip.Knuckles - centre, toReader), 1e-4f);
            Assert.AreEqual(half - inset, Vector3.Dot(grip.Knuckles - centre, side), 1e-4f);
            Assert.Greater(Vector3.Dot(grip.Palm, toReader), 0.999f, "la palma contra la tapa");
            Assert.Greater(Vector3.Dot(grip.Fingers, -side), 0.999f, "los dedos hacia el lomo");
        }

        [Test]
        public void TheSpreadTurnsTheFingersOnThePalm()
        {
            Pose(out Vector3 centre, out Quaternion rotation);
            Vector3 side = ProxyBookHold.SideAxis(rotation, Vector3.right, true);
            var flat = new ProxyBookHold.Cover { HalfWidth = 0.15f, BackZAtSpine = -0.02f, BackSlope = 0f };
            var grip = ProxyBookHold.Grip(centre, rotation, side, flat, 0.03f, 0f, 30f);
            Assert.AreEqual(30f, Vector3.Angle(grip.Fingers, -side), 0.01f);
            Assert.AreEqual(0f, Vector3.Dot(grip.Fingers, grip.Palm), 1e-4f, "girar sobre la palma no la levanta");
        }

        [Test]
        public void TheBackCoverOfAnOpenBookIsMeasuredAsAV()
        {
            // Libro de juguete: tapa de atrás z = −0,03 + 0,1·|x|, páginas 1 cm por delante y un lomo grueso en x≈0.
            var vertices = new System.Collections.Generic.List<Vector3>();
            for (float x = -0.15f; x <= 0.1501f; x += 0.005f)
            {
                float back = -0.03f + 0.1f * Mathf.Abs(x);
                vertices.Add(new Vector3(x, 0.1f, back));
                vertices.Add(new Vector3(x, -0.1f, back + 0.01f));
            }
            vertices.Add(new Vector3(0f, 0f, -0.06f));

            var cover = ProxyBookHold.MeasureCover(vertices.ToArray());
            Assert.AreEqual(0.15f, cover.HalfWidth, 1e-3f);
            Assert.AreEqual(0.1f, cover.HalfHeight, 1e-3f);
            Assert.AreEqual(0.1f, cover.BackSlope, 0.01f);
            Assert.AreEqual(-0.03f, cover.BackZAtSpine, 0.002f);
            Assert.AreEqual(0.1f, cover.FrontSlope, 0.01f, "las páginas siguen la misma V");
            Assert.AreEqual(-0.02f, cover.FrontZAtSpine, 0.002f);
        }

        [Test]
        public void OnASlopedCoverTheHandLiesAlongTheCover()
        {
            Pose(out Vector3 centre, out Quaternion rotation);
            Vector3 side = ProxyBookHold.SideAxis(rotation, Vector3.right, true);
            Vector3 toReader = rotation * Vector3.forward;
            var cover = new ProxyBookHold.Cover { HalfWidth = 0.15f, BackZAtSpine = -0.03f, BackSlope = 0.2f };
            var grip = ProxyBookHold.Grip(centre, rotation, side, cover, 0.05f, 0f, 0f);

            // La mano pegada a la tapa: sobre la recta de la tapa, a la carne del nudillo por detrás.
            Vector3 onCover = centre + side * 0.10f + toReader * (-0.03f + 0.2f * 0.10f);
            Assert.AreEqual(ProxyBookHold.KnuckleBehindMetres, Vector3.Distance(grip.Knuckles, onCover), 1e-4f);
            Assert.AreEqual(0f, Vector3.Dot(grip.Fingers, grip.Palm), 1e-4f, "los dedos van por la tapa");
            Assert.Less(Vector3.Dot(grip.Fingers, toReader), 0f, "hacia el lomo la tapa se aleja del lector: los dedos también");
            Assert.Less(Vector3.Dot(grip.Palm, side), 0f, "la palma se inclina con la tapa");
        }

        [Test]
        public void AnElbowHangingUnderTheBookCostsNothing()
        {
            var elbow = new Vector3(0.22f, 1.12f, 0.05f);
            var wrist = new Vector3(0.16f, 1.20f, 0.25f);
            Assert.AreEqual(0f, ProxyBookHold.ElbowCost(ShoulderR, elbow, wrist, Vector3.right, Vector3.up, true), 1e-4f);
        }

        [Test]
        public void ChickenWingElbowsCostTheSameOnEitherSide()
        {
            // La primera captura: codo abierto y a la altura de la muñeca.
            float right = ProxyBookHold.ElbowCost(ShoulderR, new Vector3(0.45f, 1.20f, 0.05f), new Vector3(0.20f, 1.20f, 0.25f),
                Vector3.right, Vector3.up, true);
            float left = ProxyBookHold.ElbowCost(ShoulderL, new Vector3(-0.45f, 1.20f, 0.05f), new Vector3(-0.20f, 1.20f, 0.25f),
                Vector3.right, Vector3.up, false);
            Assert.Greater(right, 5f);
            Assert.AreEqual(right, left, 1e-4f);
        }

        [Test]
        public void AHandPointInsideTheBookPiercesItAndOneOutsideTheEdgeDoesNot()
        {
            var cover = new ProxyBookHold.Cover { HalfWidth = 0.15f, HalfHeight = 0.12f, BackZAtSpine = -0.02f, BackSlope = 0f };
            Vector3 c = Vector3.zero;
            Quaternion r = Quaternion.identity;
            Assert.AreEqual(0.01f, ProxyBookHold.PierceDepth(new Vector3(0.10f, 0f, -0.01f), c, r, cover), 1e-4f, "entre la tapa y las páginas");
            Assert.AreEqual(-0.02f, ProxyBookHold.PierceDepth(new Vector3(0.10f, 0f, -0.04f), c, r, cover), 1e-4f, "detrás de la tapa");
            Assert.AreEqual(-0.03f, ProxyBookHold.PierceDepth(new Vector3(0.18f, 0f, 0f), c, r, cover), 1e-4f, "fuera del canto");
            Assert.AreEqual(-0.01f, ProxyBookHold.PierceDepth(new Vector3(0f, 0.13f, 0f), c, r, cover), 1e-4f, "por encima del borde");

            // En el libro colocado de verdad: el mismo punto, llevado al mundo.
            Pose(out Vector3 centre, out Quaternion rotation);
            Vector3 world = centre + rotation * new Vector3(0.10f, 0f, -0.01f);
            Assert.AreEqual(0.01f, ProxyBookHold.PierceDepth(world, centre, rotation, cover), 1e-4f);
        }

        [Test]
        public void FingertipsThatReachTheOtherHalfOfTheBookCost()
        {
            Vector3 side = Vector3.right;
            Assert.AreEqual(0f, ProxyBookHold.SpineCost(new Vector3(0.10f, 0f, 0f), Vector3.zero, side), 1e-5f, "en su mitad");
            Assert.AreEqual(0f, ProxyBookHold.SpineCost(new Vector3(ProxyBookHold.SpineMargin, 0.05f, 0f), Vector3.zero, side), 1e-5f);
            float crossing = (ProxyBookHold.SpineMargin + 0.02f) / 0.02f;
            Assert.AreEqual(crossing * crossing, ProxyBookHold.SpineCost(new Vector3(-0.02f, 0f, 0f), Vector3.zero, side), 1e-3f, "cruzando el lomo");
        }

        [Test]
        public void OnlyPointsInsideTheThicknessOfTheBookAreInsideIt()
        {
            var cover = new ProxyBookHold.Cover
            {
                HalfWidth = 0.15f, HalfHeight = 0.12f, BackZAtSpine = -0.02f, BackSlope = 0f, FrontZAtSpine = 0f, FrontSlope = 0f,
            };
            Vector3 c = Vector3.zero;
            Quaternion r = Quaternion.identity;
            Assert.AreEqual(0.005f, ProxyBookHold.InsideDepth(new Vector3(0.10f, 0f, -0.005f), c, r, cover), 1e-4f, "entre tapa y páginas");
            Assert.Less(ProxyBookHold.InsideDepth(new Vector3(0.10f, 0f, 0.006f), c, r, cover), 0f, "apoyado sobre la página no cuenta");
            Assert.Less(ProxyBookHold.InsideDepth(new Vector3(0.10f, 0f, -0.03f), c, r, cover), 0f, "detrás de la tapa");
            Assert.Less(ProxyBookHold.InsideDepth(new Vector3(0.16f, 0f, -0.01f), c, r, cover), 0f, "fuera del canto, a la altura del grosor");
        }

        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        [TestCase(1f)]
        [TestCase(-1f)]
        public void AFingerCurledIntoTheCoverOpensUntilItIsBehindIt(float side)
        {
            // Dedo hacia +Z con el nudillo centímetro y medio detrás de la tapa (plano y=0, normal side·Y) y cerrado 25°
            // hacia ella, como lo deja el clip de reposo.
            _root = new GameObject("hand");
            var chain = new Transform[3];
            Transform parent = _root.transform;
            for (int i = 0; i < 3; i++)
            {
                chain[i] = new GameObject("phalanx" + i).transform;
                chain[i].SetParent(parent, false);
                chain[i].localPosition = i == 0 ? new Vector3(0f, -0.015f * side, 0f) : new Vector3(0f, 0f, 0.035f);
                parent = chain[i];
            }
            Vector3 normal = Vector3.up * side;
            ProxyGripSolver.Curl(chain, Vector3.right, -25f * side);
            Assert.Greater(Vector3.Dot(ProxyGripSolver.Tip(chain), normal), 0f, "de partida la yema atraviesa la tapa");

            float applied = ProxyBookHold.OpenBehindPlane(chain, Vector3.right, Vector3.zero, normal, ProxyBookHold.FingerClearance);

            Assert.AreEqual(side, Mathf.Sign(applied), "abre hacia fuera de la tapa, no cierra más");
            Assert.That(Mathf.Abs(applied), Is.InRange(20f, 35f));
            for (int i = 1; i < chain.Length; i++)
                Assert.LessOrEqual(Vector3.Dot(chain[i].position, normal), -ProxyBookHold.FingerClearance + 1e-4f);
            Assert.LessOrEqual(Vector3.Dot(ProxyGripSolver.Tip(chain), normal), -ProxyBookHold.FingerClearance + 1e-4f);
        }

        [Test]
        public void TheThumbTipReachesAPointOnThePageTurningOnTwoAxes()
        {
            // Pulgar recto hacia +Z, RÍGIDO: los dos ejes giran sólo la base, el resto la sigue sin flexión propia — un
            // pulgar torcido por cada falange se veía como un tornillo (Joel, 14-09). Con la cadena rígida la yema sólo
            // puede llegar a puntos sobre la esfera que barre la base: el objetivo se construye ahí, girando la yema de
            // reposo los mismos dos ejes a unos ángulos conocidos, para que sea EXACTAMENTE alcanzable.
            _root = new GameObject("thumb");
            var chain = new Transform[3];
            Transform parent = _root.transform;
            for (int i = 0; i < 3; i++)
            {
                chain[i] = new GameObject("phalanx" + i).transform;
                chain[i].SetParent(parent, false);
                chain[i].localPosition = i == 0 ? new Vector3(0f, -0.01f, 0f) : new Vector3(0f, 0f, 0.03f);
                parent = chain[i];
            }
            Vector3 pivot = chain[0].position;
            Vector3 restOffset = ProxyGripSolver.Tip(chain) - pivot;
            Quaternion wanted = Quaternion.AngleAxis(-35f, Vector3.up) * Quaternion.AngleAxis(25f, Vector3.right);
            var target = pivot + wanted * restOffset;
            float before = Vector3.Distance(ProxyGripSolver.Tip(chain), target);

            ProxyBookHold.ReachWithTip(chain, Vector3.right, Vector3.up, target, ProxyBookHold.MaxThumbDegrees,
                ProxyBookHold.ThumbStepDegrees, out float a, out float b);

            float after = Vector3.Distance(ProxyGripSolver.Tip(chain), target);
            Assert.Less(after, 0.003f, $"de {before:0.000} a {after:0.000}");
            Assert.AreNotEqual(0f, a, "gira sobre el primer eje");
            Assert.AreNotEqual(0f, b, "y sobre el segundo");
        }

        [Test]
        public void ChiralityRebuildsTheKnuckleLineOfEitherHand()
        {
            // Mano derecha de juguete (la de ProxyGripSolverTests): dedos +Z, índice +X, pulgar por −Y.
            var right = ProxyGripSolver.Frame(Vector3.zero, new Vector3(0.035f, 0f, 0.09f), new Vector3(0.012f, 0f, 0.095f),
                new Vector3(-0.035f, 0f, 0.08f), new Vector3(0.03f, -0.025f, 0.03f));
            // Su espejo en X.
            var left = ProxyGripSolver.Frame(Vector3.zero, new Vector3(-0.035f, 0f, 0.09f), new Vector3(-0.012f, 0f, 0.095f),
                new Vector3(0.035f, 0f, 0.08f), new Vector3(-0.03f, -0.025f, 0.03f));

            float cr = ProxyBookHold.Chirality(right);
            float cl = ProxyBookHold.Chirality(left);
            Assert.AreNotEqual(cr, cl);
            Assert.Greater(Vector3.Dot(ProxyBookHold.KnuckleAxis(right.FingerAxis, right.PalmNormal, cr), right.KnuckleAxis), 0.99f);
            Assert.Greater(Vector3.Dot(ProxyBookHold.KnuckleAxis(left.FingerAxis, left.PalmNormal, cl), left.KnuckleAxis), 0.99f);
        }
    }
}
