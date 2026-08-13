using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-068 S3 — la geometría del lienzo. Es lo único del slice que puede fallar EN SILENCIO:
    /// un eje invertido no revienta nada, dibuja en espejo, y eso no se ve compilando ni en un
    /// log. Estos tests lo fijan contra el mismo convenio que usa el renderizador.
    /// </summary>
    [TestFixture]
    public class SprayCanvasTests
    {
        /// <summary>
        /// EL TEST QUE IMPORTA. La U del lienzo tiene que crecer hacia la derecha de quien LEE la
        /// pintada. Se deriva la derecha del lector de forma independiente (producto vectorial
        /// desde su dirección de mirada) y se compara: si alguien "arregla" el signo de RightOf
        /// sin tocar la malla del renderizador, esto se cae.
        /// </summary>
        [Test]
        public void CanvasRightMatchesTheRightHandOfWhoeverReadsIt()
        {
            foreach (float yaw in new[] { 0f, 90f, 180f, -90f, 37f })
            {
                // La pintada MIRA hacia yaw; el lector está delante, mirando hacia ella.
                Vector3 facing = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                Vector3 readerForward = -facing;
                Vector3 readerRight = Vector3.Cross(Vector3.up, readerForward);

                Vector3 canvasRight = SprayCanvas.RightOf(yaw);

                Assert.AreEqual(1f, Vector3.Dot(canvasRight.normalized, readerRight.normalized), 1e-3f,
                    $"la U debe crecer hacia la derecha del lector (yaw {yaw})");
            }
        }

        [Test]
        public void YawPointsWhereTheWallNormalPoints()
        {
            // Normal hacia +Z ⇒ la pintada mira a +Z ⇒ yaw 0.
            Assert.AreEqual(0f, SprayCanvas.YawFromNormal(Vector3.forward), 1e-3f);
            Assert.AreEqual(90f, SprayCanvas.YawFromNormal(Vector3.right), 1e-3f);
            Assert.AreEqual(180f, Mathf.Abs(SprayCanvas.YawFromNormal(Vector3.back)), 1e-3f);
            Assert.AreEqual(-90f, SprayCanvas.YawFromNormal(Vector3.left), 1e-3f);
        }

        /// <summary>
        /// Una normal casi vertical (suelo, techo, o un collider redondeado) no puede producir
        /// NaN: un NaN colado en el yaw lo rechazaría el host, pero después de que el jugador
        /// haya pintado un trazo entero.
        /// </summary>
        [Test]
        public void AVerticalNormalDoesNotProduceNaN()
        {
            float yaw = SprayCanvas.YawFromNormal(Vector3.up);
            Assert.IsFalse(float.IsNaN(yaw));
        }

        [Test]
        public void TheCentreOfTheCanvasIsTheMiddleOfTheGrid()
        {
            SprayCanvas.WorldToCanvas(new Vector3(10f, 2f, 5f), new Vector3(10f, 2f, 5f),
                90f, 1f, 1f, out byte u, out byte v);

            // 0.5 sobre 0..255 cae en 128 (redondeo al alza del punto medio exacto 127,5).
            Assert.AreEqual(128, u);
            Assert.AreEqual(128, v);
        }

        /// <summary>
        /// Mover el punto hacia la derecha del lector debe SUBIR la U, y hacia arriba subir la V.
        /// Es el mismo convenio de arriba visto ya cuantizado.
        /// </summary>
        [Test]
        public void MovingRightRaisesUAndMovingUpRaisesV()
        {
            const float yaw = 90f;
            var centre = new Vector3(10f, 2f, 5f);

            Vector3 right = SprayCanvas.RightOf(yaw);
            SprayCanvas.WorldToCanvas(centre + right * 0.3f, centre, yaw, 1f, 1f, out byte u, out byte v);
            Assert.Greater(u, 128, "hacia la derecha del lector, la U sube");
            Assert.AreEqual(128, v, "y la V no se mueve");

            SprayCanvas.WorldToCanvas(centre + Vector3.up * 0.3f, centre, yaw, 1f, 1f, out u, out v);
            Assert.Greater(v, 128, "hacia arriba, la V sube");
            Assert.AreEqual(128, u);
        }

        /// <summary>
        /// Salirse del lienzo CLAMPEA, no envuelve ni desborda. Envolver haría que un gesto
        /// amplio reapareciera por el lado contrario, que es el mismo artefacto que el
        /// renderizador evita con wrapMode Clamp.
        /// </summary>
        [Test]
        public void PaintingPastTheEdgeClampsInsteadOfWrapping()
        {
            const float yaw = 0f;
            var centre = Vector3.zero;
            Vector3 right = SprayCanvas.RightOf(yaw);

            SprayCanvas.WorldToCanvas(centre + right * 50f, centre, yaw, 1f, 1f, out byte u, out byte v);
            Assert.AreEqual(255, u);

            SprayCanvas.WorldToCanvas(centre - right * 50f, centre, yaw, 1f, 1f, out u, out v);
            Assert.AreEqual(0, u);

            SprayCanvas.WorldToCanvas(centre + Vector3.up * 50f, centre, yaw, 1f, 1f, out u, out v);
            Assert.AreEqual(255, v);
        }

        [Test]
        public void QuantizeReachesBothEndsOfTheGrid()
        {
            Assert.AreEqual(0, SprayCanvas.Quantize(0f));
            Assert.AreEqual(255, SprayCanvas.Quantize(1f), "el borde superior tiene que ser alcanzable");
            Assert.AreEqual(0, SprayCanvas.Quantize(-5f));
            Assert.AreEqual(255, SprayCanvas.Quantize(5f));
            Assert.AreEqual(0, SprayCanvas.Quantize(float.NaN), "un NaN no puede colarse al wire");
        }

        /// <summary>
        /// El filtro de "esto es una pared y la tengo a mano". El alcance se comprueba en cliente
        /// aunque el host lo revalide: así el jugador se entera al APUNTAR y no después de pintar
        /// un trazo que desaparece sin explicación.
        /// </summary>
        [Test]
        public void OnlyWallsWithinArmsReachArePaintable()
        {
            var from = Vector3.zero;

            Assert.IsTrue(SprayCanvas.IsPaintableWall(from, new Vector3(0f, 0f, 2f), Vector3.back),
                "pared vertical a 2 m");

            Assert.IsFalse(SprayCanvas.IsPaintableWall(from, new Vector3(0f, 0f, 20f), Vector3.back),
                "la misma pared a 20 m queda fuera de alcance");

            Assert.IsFalse(SprayCanvas.IsPaintableWall(from, new Vector3(0f, -1f, 1f), Vector3.up),
                "el suelo no es una pared");

            Assert.IsFalse(SprayCanvas.IsPaintableWall(from, new Vector3(0f, 2f, 1f), Vector3.down),
                "el techo tampoco");
        }

        /// <summary>
        /// La tolerancia se subió de ~20° a ~37° tras el playtest: con 20° la parte baja de los
        /// muros dejaba de responder, porque ahí la normal que devuelve el collider no sale
        /// limpia. Lo que NO puede pasar es que se cuele el suelo — el arreglo tenía que aflojar,
        /// no abrir la puerta.
        /// </summary>
        [Test]
        public void ASlantedWallIsPaintableButTheFloorStillIsNot()
        {
            var from = Vector3.zero;
            var at = new Vector3(0f, 0f, 2f);

            // Pared inclinada ~30°: normal con algo de componente vertical.
            var slanted = new Vector3(0f, 0.5f, -1f).normalized;
            Assert.IsTrue(SprayCanvas.IsPaintableWall(from, at, slanted),
                "un muro inclinado 30° sigue siendo un muro");

            Assert.IsFalse(SprayCanvas.IsPaintableWall(from, at, Vector3.up),
                "pero el suelo no entra por aflojar la tolerancia");
            Assert.IsFalse(SprayCanvas.IsPaintableWall(from, at, Vector3.down),
                "ni el techo");
        }

        /// <summary>
        /// La pintura se cobra por DISTANCIA y no por número de puntos: si no, pintar despacio
        /// (más muestras para el mismo trazo) costaría más que pintar rápido, que es justo al
        /// revés de lo que cualquiera espera.
        /// </summary>
        [Test]
        public void PaintIsChargedByDistanceNotBySampleCount()
        {
            // Cruzar el lienzo entero de 2 m de ancho cuesta 2 m, se haga en un paso o en veinte.
            float oneStep = SprayCanvas.CanvasStepMeters(0, 128, 255, 128, 2f, 1f);
            Assert.AreEqual(2f, oneStep, 1e-3f);

            float manySteps = 0f;
            for (int i = 0; i < 255; i++)
                manySteps += SprayCanvas.CanvasStepMeters((byte)i, 128, (byte)(i + 1), 128, 2f, 1f);
            Assert.AreEqual(oneStep, manySteps, 1e-3f, "el mismo trazo debe costar lo mismo");
        }

        /// <summary>
        /// Los topes espejados del backend. Si el backend los baja y esto no, el cliente manda
        /// pintadas que se rechazan enteras — y el jugador ve desaparecer lo que acaba de pintar.
        /// </summary>
        [Test]
        public void TheMirroredCapsMatchTheBackend()
        {
            Assert.AreEqual(32, SprayCanvas.MaxStrokes);
            Assert.AreEqual(512, SprayCanvas.MaxPoints);
            Assert.AreEqual(2.0f, SprayCanvas.MaxCanvasMeters, 1e-4f);
            Assert.AreEqual(0.1f, SprayCanvas.MinCanvasMeters, 1e-4f);
            Assert.AreEqual(5.0f, SprayCanvas.MaxPlaceDistance, 1e-4f);
            Assert.AreEqual(256, SprayCanvas.Grid);
        }
    }
}
