using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// Fase 1 del sistema de animación 3P: la aritmética que convierte la velocidad reconstruida de
    /// un proxy en las coordenadas del BlendTree direccional.
    ///
    /// Se prueba AQUÍ y no en el feeder porque el feeder vive en Assembly-CSharp, que un asmdef de
    /// tests no puede referenciar. Lo que queda fuera de estos tests, y se declara: el muestreo del
    /// Transform, el suavizado y la escritura al Animator — eso pide un proxy vivo en Play.
    /// </summary>
    public sealed class ProxyLocomotionMathTests
    {
        // Los mismos números que RemoteAvatarPrefabBuilder.WireLocomotionFeeder hornea en el prefab.
        private const float Deadzone = 0.1f;
        private const float Walk = 1.5f;
        private const float Run = 4.5f;

        private static Vector2 Tiers(Vector3 localVelocity) =>
            ProxyLocomotionMath.VelocityToTiers(localVelocity, Deadzone, Walk, Run);

        [Test]
        public void SpeedBelowDeadzoneIsIdle()
        {
            Assert.AreEqual(0f, ProxyLocomotionMath.SpeedToTier(0f, Deadzone, Walk, Run));
            Assert.AreEqual(0f, ProxyLocomotionMath.SpeedToTier(Deadzone, Deadzone, Walk, Run));
        }

        [Test]
        public void WalkSpeedIsWalkTierAndRunSpeedIsRunTier()
        {
            Assert.AreEqual(ProxyLocomotionMath.WalkTier,
                ProxyLocomotionMath.SpeedToTier(Walk, Deadzone, Walk, Run), 1e-4f);
            Assert.AreEqual(ProxyLocomotionMath.RunTier,
                ProxyLocomotionMath.SpeedToTier(Run, Deadzone, Walk, Run), 1e-4f);
        }

        [Test]
        public void SpeedAboveRunClampsToRunTier()
        {
            Assert.AreEqual(ProxyLocomotionMath.RunTier,
                ProxyLocomotionMath.SpeedToTier(Run * 10f, Deadzone, Walk, Run), 1e-4f);
        }

        /// <summary>
        /// El invariante del que depende que los anillos del árbol estén a radio 1 y 3: el módulo del
        /// vector ES el tier escalar. Si alguien re-escala uno de los dos lados, esto se pone rojo.
        /// </summary>
        [Test]
        public void VectorMagnitudeEqualsScalarTier([Values(0.05f, 0.5f, 1.5f, 3f, 4.5f, 9f)] float speed)
        {
            float scalar = ProxyLocomotionMath.SpeedToTier(speed, Deadzone, Walk, Run);

            // Da igual el ángulo: se comprueba en una diagonal, que es donde un error de
            // normalización (dividir por la componente en vez de por el módulo) se vería.
            var diagonal = new Vector3(speed / Mathf.Sqrt(2f), 0f, speed / Mathf.Sqrt(2f));
            Assert.AreEqual(scalar, Tiers(diagonal).magnitude, 1e-3f);
        }

        [Test]
        public void ForwardWalkIsPositiveY()
        {
            Vector2 t = Tiers(new Vector3(0f, 0f, Walk));
            Assert.AreEqual(0f, t.x, 1e-4f);
            Assert.AreEqual(ProxyLocomotionMath.WalkTier, t.y, 1e-4f);
        }

        /// <summary>Retroceder es el caso que ANTES de esta fase era indistinguible de avanzar.</summary>
        [Test]
        public void BackwardWalkIsNegativeY()
        {
            Vector2 t = Tiers(new Vector3(0f, 0f, -Walk));
            Assert.AreEqual(0f, t.x, 1e-4f);
            Assert.AreEqual(-ProxyLocomotionMath.WalkTier, t.y, 1e-4f);
        }

        [Test]
        public void StrafeRightIsPositiveXAndLeftIsNegative()
        {
            Assert.AreEqual(ProxyLocomotionMath.WalkTier, Tiers(new Vector3(Walk, 0f, 0f)).x, 1e-4f);
            Assert.AreEqual(-ProxyLocomotionMath.WalkTier, Tiers(new Vector3(-Walk, 0f, 0f)).x, 1e-4f);
        }

        [Test]
        public void DiagonalKeepsBothAxesEqual()
        {
            float component = Run / Mathf.Sqrt(2f);
            Vector2 t = Tiers(new Vector3(component, 0f, component));

            Assert.AreEqual(t.x, t.y, 1e-4f);
            Assert.AreEqual(ProxyLocomotionMath.RunTier, t.magnitude, 1e-3f);
        }

        /// <summary>
        /// Por debajo de la zona muerta la dirección es ruido del lerp de red, no intención: tiene
        /// que salir cero EXACTO, no un vector unitario diminuto que gire el pie de apoyo.
        /// </summary>
        [Test]
        public void BelowDeadzoneReturnsExactZeroVector()
        {
            Assert.AreEqual(Vector2.zero, Tiers(new Vector3(0.03f, 0f, -0.04f)));
            Assert.AreEqual(Vector2.zero, Tiers(Vector3.zero));
        }

        /// <summary>La Y del mundo es del salto (ProxyJumpFeeder), no de este árbol.</summary>
        [Test]
        public void VerticalVelocityIsIgnored()
        {
            Vector2 flat = Tiers(new Vector3(0f, 0f, Walk));
            Vector2 falling = Tiers(new Vector3(0f, -12f, Walk));

            Assert.AreEqual(flat.x, falling.x, 1e-4f);
            Assert.AreEqual(flat.y, falling.y, 1e-4f);
        }
    }
}
