using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-068 S3 — el gesto y el ajuste del lienzo. Estos tests existen por un fallo REAL
    /// reportado en juego: con el lienzo fijado en el primer impacto, el trazo dejaba de seguir
    /// a la mira y se apelmazaba contra un borde invisible, desplazado de donde se apuntaba.
    /// La causa era clampear cada muestra contra un lienzo de poco más de un metro; el arreglo
    /// es guardar en mundo y ajustar el lienzo a lo pintado.
    /// </summary>
    [TestFixture]
    public class SprayGestureTests
    {
        private const float Yaw = 90f;

        private static SprayGesture Stroke(params Vector3[] points)
        {
            var g = new SprayGesture();
            g.SetWall(Yaw);
            g.BeginStroke();
            foreach (var p in points) g.Add(p);
            g.EndStroke();
            return g;
        }

        /// <summary>El punto medio de un trazo recto cae en el centro del lienzo ajustado.</summary>
        [Test]
        public void TheFittedCanvasIsCentredOnWhatWasPainted()
        {
            Vector3 right = SprayCanvas.RightOf(Yaw);
            Vector3 a = new Vector3(10f, 2f, 5f);
            Vector3 b = a + right * 0.8f;

            var g = Stroke(a, b);
            Assert.IsTrue(g.TryFit(out var centre, out float sx, out float sy));

            Assert.AreEqual(0f, Vector3.Distance(centre, (a + b) * 0.5f), 1e-3f,
                "el centro del lienzo es el centro del trazo");
            Assert.Greater(sx, 0.8f, "y el ancho lo cubre entero, con margen");
        }

        /// <summary>
        /// LA REGRESIÓN. Un gesto de 1,6 m — más ancho que el lienzo fijo de 1,2 m que tenía la
        /// primera versión — debe proyectarse con sus extremos SEPARADOS. Antes los dos caían en
        /// 0 y 255 pegados al canto, que es lo que se veía como "se amontona y no sigue la mira".
        /// </summary>
        [Test]
        public void AWideGestureIsNotSmearedAgainstTheEdges()
        {
            Vector3 right = SprayCanvas.RightOf(Yaw);
            Vector3 a = new Vector3(10f, 2f, 5f);
            Vector3 mid = a + right * 0.8f;
            Vector3 b = a + right * 1.6f;

            var g = Stroke(a, mid, b);
            Assert.IsTrue(g.TryFit(out var centre, out float sx, out float sy));

            var bytes = g.ProjectStroke(0, centre, sx, sy);
            byte u0 = bytes[0], u1 = bytes[2], u2 = bytes[4];

            Assert.Less(u0, u1, "los tres puntos deben quedar ORDENADOS y separados");
            Assert.Less(u1, u2);
            Assert.Greater(u2 - u0, 200, "y ocupar casi todo el lienzo, no apelmazarse");
            // El del medio, en el medio: si el lienzo estuviera mal centrado se iría a un lado.
            Assert.AreEqual(128, u1, 4, "el punto central cae en el centro del lienzo");
        }

        /// <summary>
        /// El tope de 2 m se avisa ANTES de pasarse, para poder cerrar la pintada y empezar otra
        /// en vez de clampear. Clampear era exactamente el bug.
        /// </summary>
        [Test]
        public void ExceedingTheCanvasIsFlaggedBeforeItHappens()
        {
            Vector3 right = SprayCanvas.RightOf(Yaw);
            Vector3 a = new Vector3(10f, 2f, 5f);

            var g = new SprayGesture();
            g.SetWall(Yaw);
            g.BeginStroke();
            g.Add(a);
            g.Add(a + right * 1.0f);

            Assert.IsFalse(g.WouldExceedCanvas(a + right * 1.5f), "1,5 m aún cabe");
            Assert.IsTrue(g.WouldExceedCanvas(a + right * 3f), "3 m no, y hay que avisar antes");
        }

        [Test]
        public void TheFittedCanvasNeverLeavesTheRangeTheHostAccepts()
        {
            Vector3 right = SprayCanvas.RightOf(Yaw);
            Vector3 a = Vector3.zero;

            // Enorme: se acota al máximo.
            var big = Stroke(a, a + right * 50f, a + Vector3.up * 50f);
            Assert.IsTrue(big.TryFit(out _, out float bx, out float by));
            Assert.LessOrEqual(bx, SprayCanvas.MaxCanvasMeters);
            Assert.LessOrEqual(by, SprayCanvas.MaxCanvasMeters);

            // Un toque: se acota al mínimo, no a cero (un lienzo de 0 no se puede rasterizar).
            var dot = Stroke(a);
            Assert.IsTrue(dot.TryFit(out _, out float dx, out float dy));
            Assert.GreaterOrEqual(dx, SprayCanvas.MinCanvasMeters);
            Assert.GreaterOrEqual(dy, SprayCanvas.MinCanvasMeters);
        }

        /// <summary>Un toque suelto tiene que dejar marca: un trazo de un punto es válido.</summary>
        [Test]
        public void ASingleDabStillCounts()
        {
            var g = Stroke(new Vector3(3f, 1.5f, 7f));
            Assert.AreEqual(1, g.StrokeCount);
            Assert.AreEqual(1, g.PointCount);
            Assert.IsTrue(g.TryFit(out _, out _, out _));
        }

        /// <summary>
        /// La previa dibuja el trazo ABIERTO, así que el conteo tiene que incluirlo. Si no, el
        /// jugador no vería nada hasta soltar — que es medio bug original.
        /// </summary>
        [Test]
        public void TheOpenStrokeIsVisibleToThePreview()
        {
            var g = new SprayGesture();
            g.SetWall(Yaw);
            g.BeginStroke();
            g.Add(Vector3.zero);
            g.Add(Vector3.up * 0.2f);

            Assert.AreEqual(0, g.StrokeCount, "todavía no está cerrado");
            Assert.AreEqual(1, g.TotalStrokesIncludingOpen, "pero la previa sí lo ve");
            Assert.AreEqual(4, g.ProjectStroke(0, Vector3.zero, 1f, 1f).Length);
        }

        [Test]
        public void TheWallIsFixedByTheFirstHitAndNeverMovesAgain()
        {
            var g = new SprayGesture();
            g.SetWall(30f);
            g.SetWall(200f);
            Assert.AreEqual(30f, g.Yaw, 1e-3f, "una pintada vive en UNA pared");
        }

        [Test]
        public void ClearingLeavesItReadyForTheNextSpray()
        {
            var g = Stroke(Vector3.zero, Vector3.up);
            g.Clear();

            Assert.IsTrue(g.IsEmpty);
            Assert.AreEqual(0, g.StrokeCount);
            Assert.IsFalse(g.HasYaw, "la pared siguiente puede ser otra");
            Assert.IsFalse(g.TryFit(out _, out _, out _));
        }

        /// <summary>
        /// Los puntos salen SIEMPRE en pares (X e Y intercalados): el host rechaza un blob de
        /// longitud impar, y el rechazo llegaría después de que el jugador pintara el trazo.
        /// </summary>
        [Test]
        public void ProjectedStrokesAlwaysHaveAnEvenNumberOfBytes()
        {
            var g = Stroke(Vector3.zero, Vector3.up * 0.3f, Vector3.up * 0.6f);
            g.TryFit(out var centre, out float sx, out float sy);
            Assert.AreEqual(0, g.ProjectStroke(0, centre, sx, sy).Length % 2);
        }
    }
}
