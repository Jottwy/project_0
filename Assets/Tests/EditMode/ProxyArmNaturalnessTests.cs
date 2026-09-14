using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// Naturalidad del brazo del proxy, port de la de primera persona (2026-09-14). Brazos de juguete en el espacio
    /// del cuerpo: +X derecha, +Y arriba, +Z frente. Queda fuera, y se declara: elegir la mejor pose sobre el
    /// esqueleto real — lo cubre la captura del arnés con estas mismas medidas en el log.
    /// </summary>
    public sealed class ProxyArmNaturalnessTests
    {
        private static readonly Vector3 Shoulder = new Vector3(0.18f, 1.40f, 0f);

        // Brazo derecho con el antebrazo hacia delante y la mano de apretón: palma hacia la línea media (−X),
        // nudillos del meñique abajo e índice arriba.
        private static ProxyArmNaturalness.Measure Handshake(Vector3 metaDir, Vector3 palm)
        {
            Vector3 elbow = Shoulder + new Vector3(0f, -0.30f, 0f);
            Vector3 wrist = elbow + new Vector3(0f, 0f, 0.27f);
            Vector3 middle = wrist + metaDir.normalized * 0.09f;
            Vector3 knuckleAxis = Vector3.up; // del meñique (abajo) al índice (arriba)
            return ProxyArmNaturalness.Take(Shoulder, elbow, wrist, middle, palm, knuckleAxis, Vector3.right, Vector3.up, true);
        }

        [Test]
        public void ANeutralHandshakeCostsNothing()
        {
            var m = Handshake(Vector3.forward, Vector3.left);
            Assert.AreEqual(0f, m.Flexion, 0.5f);
            Assert.AreEqual(0f, m.Ulnar, 0.5f);
            Assert.AreEqual(0f, m.ForearmRotation, 0.5f);
            Assert.AreEqual(90f, m.ElbowAngle, 0.5f);
            Assert.AreEqual(0f, ProxyArmNaturalness.Cost(m), 1e-3f);
        }

        [Test]
        public void AHangingArmCostsNothingInThirdPerson()
        {
            // El brazo colgando del idle: codo por encima de la muñeca, que en tercera persona es lo normal.
            Vector3 elbow = Shoulder + new Vector3(0f, -0.30f, 0f);
            // Codo a ~150°: colgando pero no bloqueado (a 169° lo castiga, con razón, «codo-bloqueado»).
            Vector3 wrist = elbow + new Vector3(0f, -0.22f, 0.12f);
            Vector3 middle = wrist + (wrist - elbow).normalized * 0.09f;
            var m = ProxyArmNaturalness.Take(Shoulder, elbow, wrist, middle, Vector3.left, Vector3.forward,
                Vector3.right, Vector3.up, true);
            Assert.AreEqual(0f, ProxyArmNaturalness.Cost(m), 1e-3f);
        }

        [Test]
        public void BendingTowardsThePalmIsFlexionAndTowardsThePinkyIsUlnar()
        {
            var flex = Handshake(Quaternion.AngleAxis(-40f, Vector3.up) * Vector3.forward, Vector3.left);
            Assert.Greater(flex.Flexion, 35f, "doblar hacia la palma (−X) es flexión");
            Assert.AreEqual(0f, flex.Ulnar, 1f);

            var ulnar = Handshake(Quaternion.AngleAxis(30f, Vector3.right) * Vector3.forward, Vector3.left);
            Assert.Greater(ulnar.Ulnar, 25f, "doblar hacia el meñique (abajo) es cubital");
            Assert.AreEqual(0f, ulnar.Flexion, 1f);
        }

        [Test]
        public void PalmUpIsSupinationAndPalmDownIsPronation()
        {
            Assert.AreEqual(90f, Handshake(Vector3.forward, Vector3.up).ForearmRotation, 0.5f);
            Assert.AreEqual(-90f, Handshake(Vector3.forward, Vector3.down).ForearmRotation, 0.5f);
        }

        [Test]
        public void AForcedWristCostsMoreThanAComfortableOne()
        {
            float comfy = ProxyArmNaturalness.Cost(Handshake(Quaternion.AngleAxis(-10f, Vector3.up) * Vector3.forward, Vector3.left));
            float forced = ProxyArmNaturalness.Cost(Handshake(Quaternion.AngleAxis(-60f, Vector3.up) * Vector3.forward, Vector3.left));
            Assert.AreEqual(0f, comfy, 1e-3f);
            Assert.Greater(forced, 5f);
        }

        [Test]
        public void SharingTheTwistMovesTheForearmButNotTheHand()
        {
            var root = new GameObject("elbow");
            try
            {
                var lower = root.transform;
                var hand = new GameObject("hand").transform;
                hand.SetParent(lower, false);
                hand.localPosition = new Vector3(0f, 0f, 0.27f);
                hand.rotation = Quaternion.AngleAxis(-80f, Vector3.forward); // mano girada 80° sobre el antebrazo

                Quaternion handBefore = hand.rotation;
                Vector3 untwisted = Vector3.left;
                Vector3 palm = handBefore * Vector3.left;
                ProxyArmNaturalness.ShareForearmTwist(lower, hand, palm, untwisted);

                Assert.Less(Quaternion.Angle(handBefore, hand.rotation), 0.01f, "la mano no cambia en mundo");
                Assert.AreEqual(40f, Quaternion.Angle(Quaternion.identity, lower.rotation), 0.5f, "el antebrazo se lleva la mitad");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
