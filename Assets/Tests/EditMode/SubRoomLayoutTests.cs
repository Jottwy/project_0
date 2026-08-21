using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Lo que hay que garantizar de una partición en estancias: que el borde entre dos sale UNA vez
    /// y marcado como compartido (es donde va el vano), y que el borde exterior de la sala no lleva
    /// tabique. Un borde emitido dos veces deja dos tabiques espalda contra espalda con un resquicio
    /// impasable entre ellos — el mismo fallo que obliga a dejar un tile entre dos salas.
    /// </summary>
    [TestFixture]
    public class SubRoomLayoutTests
    {
        private static SubRoomLayout.Partition P(int x, int z, int w, int d) =>
            new SubRoomLayout.Partition { tileX = x, tileZ = z, tilesX = w, tilesZ = d };

        private static List<SubRoomLayout.Wall> Walls(int tilesX, int tilesZ,
            params SubRoomLayout.Partition[] parts)
        {
            var into = new List<SubRoomLayout.Wall>();
            SubRoomLayout.Walls(parts, tilesX, tilesZ, into);
            return into;
        }

        [Test]
        public void TwoAdjacentRoomsShareExactlyOneWall()
        {
            // Sala de 4×2 partida por la mitad: un solo tabique de largo 2 en x=2, compartido.
            var walls = Walls(4, 2, P(0, 0, 2, 2), P(2, 0, 2, 2));

            Assert.AreEqual(1, walls.Count);
            Assert.IsTrue(walls[0].shared, "el borde entre dos estancias tiene que ir marcado");
            Assert.IsTrue(walls[0].alongZ);
            Assert.AreEqual(2f, walls[0].length);
            Assert.AreEqual(2f, walls[0].center.x, 1e-4f);
            Assert.AreEqual(0, walls[0].roomA);
            Assert.AreEqual(1, walls[0].roomB);
        }

        /// <summary>
        /// Cada tabique dice QUÉ dos estancias separa. Sin eso, el ancho del vano solo se puede
        /// tomar global para todo el piso, y entonces una estancia sellada tapia también los vanos
        /// de las que no toca: tres estancias en fila, y sellar la tercera dejaba sin puerta a las
        /// dos primeras.
        /// </summary>
        [Test]
        public void EachWallNamesThePairItSeparates()
        {
            // Tres en fila: 0|1 y 1|2. Ninguna pareja incluye a la vez a la 0 y a la 2.
            var walls = Walls(6, 2, P(0, 0, 2, 2), P(2, 0, 2, 2), P(4, 0, 2, 2));

            Assert.AreEqual(2, walls.Count);
            foreach (var w in walls)
            {
                Assert.IsTrue(w.shared);
                int lo = UnityEngine.Mathf.Min(w.roomA, w.roomB);
                int hi = UnityEngine.Mathf.Max(w.roomA, w.roomB);
                Assert.AreEqual(1, hi - lo, "un tabique solo separa estancias VECINAS");
            }
        }

        /// <summary>Un lado que da al vacío no nombra estancia: es -1, y ahí no va vano.</summary>
        [Test]
        public void SolidWallsNameTheVoidAsMinusOne()
        {
            var walls = Walls(4, 4, P(1, 1, 2, 2));
            foreach (var w in walls)
            {
                Assert.IsFalse(w.shared);
                Assert.IsTrue(w.roomA < 0 || w.roomB < 0, "un lado tiene que ser el vacío");
            }
        }

        [Test]
        public void RoomFillingTheWholeFootprintHasNoWalls()
        {
            Assert.AreEqual(0, Walls(3, 3, P(0, 0, 3, 3)).Count);
        }

        /// <summary>Una estancia suelta en medio: sus cuatro lados son tabique, y NINGUNO
        /// compartido — al otro lado no hay estancia, así que un vano ahí no comunica nada.</summary>
        [Test]
        public void IsolatedRoomWallsAreNotShared()
        {
            var walls = Walls(4, 4, P(1, 1, 2, 2));

            Assert.AreEqual(4, walls.Count);
            foreach (var w in walls)
            {
                Assert.IsFalse(w.shared);
                Assert.AreEqual(2f, w.length);
            }
        }

        /// <summary>El borde exterior de la sala nunca lleva tabique: ahí ya está su propia pared, y
        /// doblarla lo dejaría clavado dentro del muro.</summary>
        [Test]
        public void RoomFlushWithTheEdgeGetsNoWallThere()
        {
            // Estancia pegada a la esquina (0,0) de una sala de 4×4: solo dos lados interiores.
            var walls = Walls(4, 4, P(0, 0, 2, 2));
            Assert.AreEqual(2, walls.Count);
            foreach (var w in walls) Assert.IsFalse(w.shared);
        }

        /// <summary>Un borde en parte compartido y en parte contra el vacío se PARTE: el vano tiene
        /// que caer donde de verdad hay estancia a los dos lados, no sobre el trozo ciego.</summary>
        [Test]
        public void PartlySharedBorderSplitsIntoSharedAndSolid()
        {
            // Izquierda 2×4; derecha solo 2×2, así que la mitad de arriba del borde da al vacío.
            var walls = Walls(4, 4, P(0, 0, 2, 4), P(2, 0, 2, 2));

            var atSeam = new List<SubRoomLayout.Wall>();
            foreach (var w in walls)
                if (w.alongZ && UnityEngine.Mathf.Abs(w.center.x - 2f) < 1e-4f) atSeam.Add(w);

            Assert.AreEqual(2, atSeam.Count, "el borde tiene que partirse en dos tramos");
            int shared = 0, solid = 0;
            foreach (var w in atSeam)
            {
                Assert.AreEqual(2f, w.length);
                if (w.shared) shared++; else solid++;
            }
            Assert.AreEqual(1, shared);
            Assert.AreEqual(1, solid);
        }

        /// <summary>Cuatro cuadrantes: las dos costuras se parten en cuatro tramos, uno por pareja
        /// de estancias, y todos compartidos. Partir por pareja es lo que deja el vano de cada
        /// tramo en SU centro; fundidas en dos tabiques largos, el vano caería justo en el cruce
        /// con el tabique perpendicular. Y si un borde saliera dos veces, un cuadrante quedaría con
        /// dos tabiques pegados y sin paso.</summary>
        [Test]
        public void FourQuadrantsSplitSeamsByPair()
        {
            var walls = Walls(4, 4,
                P(0, 0, 2, 2), P(2, 0, 2, 2), P(0, 2, 2, 2), P(2, 2, 2, 2));

            Assert.AreEqual(4, walls.Count, "dos costuras, cada una partida en dos tramos");
            float total = 0f;
            foreach (var w in walls)
            {
                Assert.IsTrue(w.shared);
                Assert.AreEqual(2f, w.length, "cada tramo separa UN par de estancias, no dos");
                total += w.length;
            }
            Assert.AreEqual(8f, total, 1e-4f);
        }
    }
}
