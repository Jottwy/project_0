using System.Collections.Generic;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Que la malla autorada caiga EXACTAMENTE sobre su propia colisión, en los cuatro giros.
    ///
    /// Es la comprobación que ninguna captura puede dar. Una malla corrida medio metro respecto a
    /// sus cajas se ve perfectamente normal —paredes, techo, todo en su sitio— hasta que atraviesas
    /// una pared que se dibuja un poco más allá, o te chocas contra aire. Y como el desplazamiento
    /// depende del giro, puede estar bien en la mitad de las piezas del mundo.
    /// </summary>
    public sealed class Wg3AuthoredVisualTests
    {
        /// <summary>El giro de Unity alrededor de Y, que es lo que el ensamblador le pone al hijo
        /// con <c>Quaternion.Euler</c>. Se usa el propio <c>Quaternion</c> y no una fórmula copiada:
        /// lo que hay que atar es la convención de Unity, no mi idea de ella.</summary>
        private static Vector2 UnityYaw(Vector2 p, float degrees)
        {
            Vector3 v = Quaternion.Euler(0f, degrees, 0f) * new Vector3(p.x, 0f, p.y);
            return new Vector2(v.x, v.z);
        }

        [Test]
        public void TheAuthoredMeshLandsOnItsOwnCollision()
        {
            // Huella y pivote de un pasillo autorado real: el modelo se dibuja centrado, así que su
            // origen cae en el centro de la huella.
            var piece = new Wg3Piece
            {
                id = "cor_authored",
                sizeX = 2.7f,
                sizeZ = 11.3f,
                visualPivot = new Vector2(1.35f, 5.65f)
            };

            // Un punto cualquiera del modelo, descentrado a propósito en los dos ejes: uno centrado
            // pasaría el test incluso con el giro mal puesto.
            var model = new Vector2(0.8f, -3.1f);

            for (int r = 0; r < 4; r++)
            {
                // Ruta de la COLISIÓN: el horno traslada el punto a esquina mínima (sumar el pivote)
                // y `BuildPlaced` le aplica el giro de la colocación.
                Vector2 collision = Wg3Geometry.RotateLocal(
                    model + piece.visualPivot, r, piece.sizeX, piece.sizeZ);

                // Ruta de la MALLA: el hijo se ancla en el pivote ya girado y luego gira sobre sí.
                Vector2 anchor = Wg3Geometry.RotateLocal(
                    piece.visualPivot, r, piece.sizeX, piece.sizeZ);
                Vector2 visual = anchor + UnityYaw(model, r * 90f);

                Assert.AreEqual(collision.x, visual.x, 1e-3f,
                    $"giro {r}: la malla se dibuja en X={visual.x:0.000} y su colisión está en " +
                    $"{collision.x:0.000}");
                Assert.AreEqual(collision.y, visual.y, 1e-3f,
                    $"giro {r}: la malla se dibuja en Z={visual.y:0.000} y su colisión está en " +
                    $"{collision.y:0.000}");
            }
        }

        [Test]
        public void APieceWithoutAMeshStillHasItsBoxes()
        {
            // El catálogo de código no tiene prefab, y tiene que seguir dibujándose. La rebanada 2
            // añade un camino, no sustituye el que había.
            foreach (Wg3Piece piece in Wg3Catalog.Build())
            {
                Assert.IsNull(piece.visualPrefab, $"{piece.id}: el catálogo de código no autora mallas");
                Assert.IsNotEmpty(Wg3Geometry.Build(piece), $"{piece.id}: se quedó sin volúmenes");
            }
        }

        [Test]
        public void WithoutALibraryTheActiveCatalogIsTheCodeOne()
        {
            // Sin biblioteca en Resources —que es el estado de hoy— las dos rutas que preguntan
            // (exportador y streamer) tienen que recibir lo mismo que antes de existir esto.
            List<Wg3Piece> active = Wg3ActiveCatalog.Build(out string source);
            List<Wg3Piece> code = Wg3Catalog.Build();

            Assert.AreEqual(code.Count, active.Count, $"catálogo vigente: {source}");
            for (int i = 0; i < code.Count; i++)
                Assert.AreEqual(code[i].id, active[i].id,
                    $"el índice {i} cambió de pieza, y el índice ES lo que viaja por el wire");
        }
    }
}
