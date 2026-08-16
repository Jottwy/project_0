using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-078 — el trazo en vivo hacia los demás. Lo que se prueba aquí es la ARITMÉTICA que
    /// cruza el cable, que es lo único que puede romperse en silencio: si los milímetros se
    /// deshacen mal, el que mira ve un garabato en otro sitio y nadie ve un error.
    /// </summary>
    [TestFixture]
    public class SprayDraftTests
    {
        private const float Yaw = 90f;

        /// <summary>
        /// IDA Y VUELTA: los puntos que se mandan en milímetros vuelven al mismo sitio en metros.
        /// La tolerancia es 1 mm porque ése es el paso de la cuantización, y está dos órdenes de
        /// magnitud por debajo del grosor de la boquilla.
        /// </summary>
        [Test]
        public void PointsSurviveTheTripThroughMillimetres()
        {
            var sent = new SprayGesture();
            sent.SetWall(Yaw);
            sent.BeginStroke();

            var world = new[]
            {
                new Vector3(10f, 2f, 5f),
                new Vector3(10f, 2.2f, 5.31f),
                new Vector3(10f, 1.85f, 5.62f),
            };
            foreach (var p in world) sent.Add(p);

            Assert.IsTrue(sent.TryGetAnchor(out var anchor));
            var bytes = sent.OpenStrokeToMillimetres(0, 64, anchor);
            Assert.IsNotNull(bytes);
            Assert.AreEqual(world.Length * 4, bytes.Length, "cuatro bytes por punto");

            // Y el camino de vuelta, que es el que corre en la máquina del que mira.
            var received = new SprayGesture();
            received.SetWall(Yaw);
            received.BeginStroke();
            for (int i = 0; i < world.Length; i++)
            {
                short u = (short)(bytes[i * 4] | (bytes[i * 4 + 1] << 8));
                short v = (short)(bytes[i * 4 + 2] | (bytes[i * 4 + 3] << 8));
                received.AddFromMillimetres(anchor, u, v);
            }

            Assert.AreEqual(world.Length, received.PointCount);
            Assert.IsTrue(received.TryFit(out var centre, out _, out _));
            Assert.IsTrue(sent.TryFit(out var sentCentre, out _, out _));
            Assert.AreEqual(sentCentre.x, centre.x, 0.002f);
            Assert.AreEqual(sentCentre.y, centre.y, 0.002f);
            Assert.AreEqual(sentCentre.z, centre.z, 0.002f);
        }

        /// <summary>
        /// El emisor manda SOLO lo nuevo. Sin esto cada paquete llevaría el trazo entero y la
        /// fase B costaría lo que ADR-078 rechazó por escrito en su alternativa (A).
        /// </summary>
        [Test]
        public void OnlyTheNewPointsGoOutOnEachPacket()
        {
            var g = new SprayGesture();
            g.SetWall(Yaw);
            g.BeginStroke();
            g.Add(new Vector3(10f, 2f, 5f));
            g.Add(new Vector3(10f, 2f, 5.1f));
            Assert.IsTrue(g.TryGetAnchor(out var anchor));

            var first = g.OpenStrokeToMillimetres(0, 64, anchor);
            Assert.AreEqual(2 * 4, first.Length);

            g.Add(new Vector3(10f, 2f, 5.2f));
            var second = g.OpenStrokeToMillimetres(2, 64, anchor);
            Assert.AreEqual(1 * 4, second.Length, "solo el punto que no se habia mandado");

            Assert.IsNull(g.OpenStrokeToMillimetres(3, 64, anchor),
                "sin puntos nuevos no se gasta un paquete en decir 'sigo aqui'");
        }

        /// <summary>
        /// El tope por paquete (ADR-078 decisión 6) no es decoración: sin él, un pico de lag
        /// acumula puntos y manda un datagrama que no cruza, justo cuando la red ya va mal.
        /// </summary>
        [Test]
        public void APacketNeverCarriesMorePointsThanTheCap()
        {
            var g = new SprayGesture();
            g.SetWall(Yaw);
            g.BeginStroke();
            for (int i = 0; i < 200; i++) g.Add(new Vector3(10f, 2f, 5f + i * 0.01f));
            Assert.IsTrue(g.TryGetAnchor(out var anchor));

            var bytes = g.OpenStrokeToMillimetres(0, 64, anchor);
            Assert.AreEqual(64 * 4, bytes.Length, "64 puntos y ni uno mas");
        }

        /// <summary>
        /// LA PREVIA ES MULTISLOT desde ADR-078. Con una sola ranura, dos jugadores pintando en
        /// la misma sala se pisaban el trazo — y el síntoma habría sido "a veces no se ve pintar
        /// al otro", que es de los que se persiguen media sesión.
        /// </summary>
        [Test]
        public void TwoPaintersKeepTheirOwnPreview()
        {
            var go = new GameObject("SprayRendererTest");
            var renderer = go.AddComponent<SprayRenderer>();
            try
            {
                renderer.ShowPreview(SprayRenderer.LocalPreviewKey, StubSpray());
                renderer.ShowPreview(1234L, StubSpray());
                Assert.AreEqual(2, renderer.PreviewCount, "la local y la del otro conviven");

                renderer.ClearPreview(1234L);
                Assert.AreEqual(1, renderer.PreviewCount, "retirar una no se lleva la otra");

                renderer.Clear();
                Assert.AreEqual(0, renderer.PreviewCount, "Clear se lleva TODAS, no solo la local");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static SprayMsg StubSpray() => new SprayMsg
        {
            id = 0,
            cx = 0,
            cz = 0,
            layer = 0,
            lx = 5f,
            ly = 2f,
            lz = 5f,
            yaw = Yaw,
            sizeX = 1f,
            sizeY = 1f,
            strokes = new[]
            {
                new SprayStrokeMsg { color = 2, width = 4, points = new byte[] { 10, 10, 200, 200 } },
            },
        };
    }
}
