using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-068 decisión 4 — "pintar encima" solo está garantizado si cada pintada nueva sobre la
    /// MISMA pared se despega un poco más que la anterior. A la misma distancia del muro dos quads
    /// solapados hacen z-fighting y quién tapa a quién lo decide el azar del depth buffer.
    ///
    /// Lo que se prueba aquí es el CONTADOR de apilado a través de la purga por distancia, que es
    /// donde se rompía: la purga vaciaba el contador entero aunque solo hubiera soltado las
    /// pintadas lejanas, así que las paredes cercanas —con sus quads todavía puestos a su
    /// profundidad— volvían a empezar en cero.
    ///
    /// Necesita el <c>MonoBehaviour</c> y no solo una función pura porque el estado corrompido
    /// vive en el contador del componente, no en lo que se ve de la escena.
    /// </summary>
    [TestFixture]
    public class SprayStackingTests
    {
        private const int Near = 0;
        private const int Far = 10;   // > KeepRadiusChunks (3) desde el origen
        private const byte Layer = 0;

        private GameObject _host;
        private SprayRenderer _renderer;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("SprayRendererUnderTest");
            _renderer = _host.AddComponent<SprayRenderer>();
        }

        [TearDown]
        public void TearDown()
        {
            // DestroyImmediate dispara OnDisable, que suelta texturas y materiales: sin esto cada
            // test dejaría sus 256 KB por pintada colgados en el proceso del editor.
            if (_host != null) Object.DestroyImmediate(_host);

            // `SprayRenderer.OnEnable` engancha al IPCClient, y el getter de ese singleton FABRICA
            // el GameObject si no existe. Es un efecto secundario real del componente, no del test.
            var client = Object.FindFirstObjectByType<IPCClient>();
            if (client != null) Object.DestroyImmediate(client.gameObject);
        }

        private static SprayMsg Spray(uint id, int cx, int cz) => new SprayMsg
        {
            id = id,
            cx = cx,
            cz = cz,
            layer = Layer,
            sizeX = 1f,
            sizeY = 1f,
            strokes = new[]
            {
                new SprayStrokeMsg { color = 1, width = 4, points = new byte[] { 128, 128 } }
            }
        };

        /// <summary>
        /// El caso que se rompía. Dos pintadas cerca y una lejos; la purga se lleva la lejana y el
        /// contador de la pared CERCANA tiene que seguir donde estaba — sus dos quads siguen
        /// puestos a profundidad 0 y 1, así que la siguiente pintada de esa pared debe salir a 2.
        /// </summary>
        [Test]
        public void APartialSweepLeavesTheSurvivingWallsCounterAlone()
        {
            _renderer.Show(Spray(1, Near, Near));
            _renderer.Show(Spray(2, Near, Near));
            _renderer.Show(Spray(3, Far, Far));

            Assert.AreEqual(3, _renderer.LiveCount, "las tres se materializan");
            Assert.AreEqual(2, _renderer.NextStackDepthOn(Near, Near, Layer));

            _renderer.SweepFarAwayFrom(Vector3.zero);

            Assert.AreEqual(2, _renderer.LiveCount, "solo cae la lejana");
            Assert.AreEqual(2, _renderer.NextStackDepthOn(Near, Near, Layer),
                "la pared cercana conserva su apilado: sus quads siguen puestos");
        }

        /// <summary>
        /// La otra mitad del contrato: la pared que SÍ se soltó tiene que olvidarse. Al rehidratar
        /// el chunk las pintadas vuelven en el mismo orden y se vuelven a escalonar desde cero; si
        /// el contador sobreviviera, cada ida y vuelta las despegaría más del muro hasta verse
        /// flotando.
        /// </summary>
        [Test]
        public void ASweptWallForgetsItsStacking()
        {
            _renderer.Show(Spray(1, Far, Far));
            Assert.AreEqual(1, _renderer.NextStackDepthOn(Far, Far, Layer));

            _renderer.SweepFarAwayFrom(Vector3.zero);

            Assert.AreEqual(0, _renderer.LiveCount);
            Assert.AreEqual(0, _renderer.NextStackDepthOn(Far, Far, Layer),
                "la pared soltada vuelve a empezar en cero al rehidratarse");
        }

        /// <summary>
        /// El efecto observable, no el contador: dos pintadas de la misma pared NO pueden acabar
        /// en el mismo plano. Se mide sobre los quads reales, que es lo que ve el depth buffer.
        /// </summary>
        [Test]
        public void TwoSpraysOnOneWallNeverEndUpCoplanar()
        {
            _renderer.Show(Spray(1, Near, Near));
            _renderer.Show(Spray(2, Near, Near));

            var first = FindQuad(1);
            var second = FindQuad(2);
            Assert.IsNotNull(first, "la primera pintada tiene quad");
            Assert.IsNotNull(second, "la segunda pintada tiene quad");

            Assert.AreNotEqual(first.position.z, second.position.z,
                "coplanares = z-fighting, que es justo lo que ADR-068 decisión 4 prohíbe");
        }

        /// <summary>
        /// Y el mismo efecto DESPUÉS de una purga parcial, que es el camino por el que volvía el
        /// z-fighting: la tercera pintada de la pared cercana no puede caer encima de la primera.
        /// </summary>
        [Test]
        public void APartialSweepDoesNotBringBackCoplanarSprays()
        {
            _renderer.Show(Spray(1, Near, Near));
            _renderer.Show(Spray(2, Near, Near));
            _renderer.Show(Spray(3, Far, Far));

            _renderer.SweepFarAwayFrom(Vector3.zero);
            _renderer.Show(Spray(4, Near, Near));

            var first = FindQuad(1);
            var fourth = FindQuad(4);
            Assert.IsNotNull(fourth, "la pintada de después de la purga tiene quad");
            Assert.AreNotEqual(first.position.z, fourth.position.z,
                "tras la purga la pintada nueva volvía a profundidad 0, encima de la primera");
        }

        private Transform FindQuad(uint id) => _host.transform.Find($"Spray_{id}");
    }
}
