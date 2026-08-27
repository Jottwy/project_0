using System.Collections.Generic;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// WorldGen3, tanda 1 — la composición por sockets, sin escena ni geometría.
    ///
    /// Estos tests cubren los invariantes de los que cuelga todo lo demás: determinismo (R3),
    /// cero solapes, toda boca resuelta (L21 contra §13), y que la anchura sea de verdad parte de
    /// la compatibilidad (L6). Si uno de ellos se pone rojo, el problema no es cosmético.
    /// </summary>
    [TestFixture]
    public class Wg3ComposerTests
    {
        private static readonly int[] Seeds = { 42, 7, 1337, -19, 900001 };

        private static List<Wg3Piece> Catalog() => Wg3Catalog.Build();

        private static Wg3World Compose(int seed, int budget = 30, float capChance = 0.17f)
        {
            var settings = new Wg3ComposerSettings { budget = budget, deliberateCapChance = capChance };
            return Wg3Composer.Compose(seed, Catalog(), settings);
        }

        // ── catálogo ────────────────────────────────────────────────────────────────────────

        [Test]
        public void CatalogPassesItsOwnValidator()
        {
            List<string> issues = Wg3Validator.ValidateCatalog(Catalog());
            Assert.IsEmpty(issues, "catálogo inválido:\n" + string.Join("\n", issues));
        }

        [Test]
        public void SeedPieceIsTheFirstCatalogEntry()
        {
            // Si esto cambia, todos los mundos ya generados se mueven. El test existe para que
            // reordenar el catálogo sea una decisión y no un accidente de edición.
            Wg3World w = Compose(42, budget: 1);
            Assert.AreEqual(Catalog()[0].id, w.placements[0].piece.id);
        }

        // ── giro ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// El contrato del que depende TODO el emparejado: girar una pieza deja el offset del
        /// socket intacto y solo suma al lado. Se comprueba contra una rotación escrita de forma
        /// independiente, no contra la propia parametrización.
        /// </summary>
        [Test]
        public void RotatingAPieceKeepsSocketOffsetsAndShiftsTheSide()
        {
            foreach (Wg3Piece piece in Catalog())
            {
                for (int r = 0; r < 4; r++)
                {
                    var placement = new Wg3Placement
                    {
                        piece = piece, rotation = r, originX = 0f, originZ = 0f,
                        socketState = new byte[piece.sockets.Length]
                    };

                    for (int s = 0; s < piece.sockets.Length; s++)
                    {
                        Vector2 flat = Wg3Piece.LocalPoint(piece.sockets[s].side,
                            piece.sockets[s].offset, piece.sizeX, piece.sizeZ);
                        Vector2 expected = RotatePoint(flat, r, piece.sizeX, piece.sizeZ);
                        Vector2 actual = placement.WorldPoint(s);

                        Assert.AreEqual(expected.x, actual.x, 1e-3f,
                            $"{piece.id} boca {s} giro {r}: X");
                        Assert.AreEqual(expected.y, actual.y, 1e-3f,
                            $"{piece.id} boca {s} giro {r}: Z");
                        Assert.AreEqual((piece.sockets[s].side + r) % 4, placement.WorldSide(s),
                            $"{piece.id} boca {s} giro {r}: lado");
                    }
                }
            }
        }

        /// <summary>Giro horario visto desde +Y, escrito a mano. La caja [0,w]×[0,d] se mantiene
        /// en el cuadrante positivo, que es el contrato de origen en esquina mínima.</summary>
        private static Vector2 RotatePoint(Vector2 p, int r, float w, float d)
        {
            switch (r & 3)
            {
                case 0: return p;
                case 1: return new Vector2(p.y, w - p.x);
                case 2: return new Vector2(w - p.x, d - p.y);
                default: return new Vector2(d - p.y, p.x);
            }
        }

        // ── determinismo (R3) ───────────────────────────────────────────────────────────────

        [Test]
        public void SameSeedProducesTheSameWorld()
        {
            foreach (int seed in Seeds)
                Assert.AreEqual(Compose(seed).Signature(), Compose(seed).Signature(),
                    $"la semilla {seed} no reproduce su mundo");
        }

        [Test]
        public void DifferentSeedsProduceDifferentWorlds()
        {
            var seen = new HashSet<string>();
            foreach (int seed in Seeds)
                Assert.IsTrue(seen.Add(Compose(seed).Signature()),
                    $"la semilla {seed} repite un mundo de otra semilla");
        }

        [Test]
        public void TheScaleFieldIsPureAndNotDegenerate()
        {
            Assert.AreEqual(Wg3ScaleField.ValueAt(42, 13.5f, -7.25f),
                            Wg3ScaleField.ValueAt(42, 13.5f, -7.25f));

            // Un campo que devolviera siempre la misma clase apagaría L20 sin dar error: el mundo
            // saldría homogéneo y nadie sabría por qué.
            var classes = new HashSet<Wg3Scale>();
            for (float x = -600f; x <= 600f; x += 23f)
                for (float z = -600f; z <= 600f; z += 23f)
                    classes.Add(Wg3ScaleField.ScaleAt(42, x, z));
            Assert.AreEqual(4, classes.Count, "el campo de escala no produce las cuatro clases");
        }

        [Test]
        public void TheScaleFieldIsSymmetricAroundTheOrigin()
        {
            // Con truncado hacia cero en vez de suelo, −1 y +1 caen en la misma celda y el campo
            // sale espejado. Es el mismo fallo que obligó a `div_euclid` al tallar salas a caballo
            // de dos chunks, y es invisible salvo que se mire a propósito.
            int mirrored = 0;
            for (float d = 1f; d < 200f; d += 7f)
                if (Wg3ScaleField.ValueAt(42, d, 0f) == Wg3ScaleField.ValueAt(42, -d, 0f)) mirrored++;
            Assert.Less(mirrored, 4, "el campo de escala está espejado en el origen");
        }

        // ── invariantes del mundo ───────────────────────────────────────────────────────────

        [Test]
        public void NoPlacementsOverlapAndNoSocketIsLeftOpen()
        {
            foreach (int seed in Seeds)
            {
                Wg3World w = Compose(seed);
                List<string> issues = Wg3Validator.ValidateWorld(w);
                Assert.IsEmpty(issues, $"semilla {seed}:\n" + string.Join("\n", issues));
            }
        }

        [Test]
        public void EveryChildMeetsItsParentAtExactlyOneJoint()
        {
            foreach (int seed in Seeds)
            {
                Wg3World w = Compose(seed);
                for (int i = 1; i < w.placements.Count; i++)
                {
                    Wg3Placement child = w.placements[i];
                    Assert.GreaterOrEqual(child.parentIndex, 0, $"pieza {i} sin padre");
                    Wg3Placement parent = w.placements[child.parentIndex];

                    int joints = 0;
                    for (int a = 0; a < child.piece.sockets.Length; a++)
                        for (int b = 0; b < parent.piece.sockets.Length; b++)
                        {
                            if ((child.WorldPoint(a) - parent.WorldPoint(b)).sqrMagnitude > 1e-4f) continue;
                            Assert.AreEqual(Wg3Piece.OppositeSide(parent.WorldSide(b)), child.WorldSide(a),
                                $"semilla {seed}: la junta {child.piece.id}#{i} no enfrenta lados opuestos");
                            joints++;
                        }
                    Assert.AreEqual(1, joints,
                        $"semilla {seed}: {child.piece.id}#{i} toca a su padre en {joints} puntos, no en 1");
                }
            }
        }

        /// <summary>
        /// El único test que mira la CALIDAD y no la mecánica. Todos los demás los pasaría un
        /// compositor que encadenase treinta pasillos idénticos: no habría solapes, no habría
        /// bocas abiertas y sería perfectamente determinista. Y sería inútil.
        ///
        /// Los umbrales son deliberadamente flojos — no fijan el mundo, solo cazan el colapso: que
        /// el campo de escala deje de modular, que la penalización de repetición deje de aplicarse,
        /// o que el catálogo se parta en dos grafos que no se tocan.
        /// </summary>
        [Test]
        public void TheComposerActuallyVariesTheWorld()
        {
            foreach (int seed in Seeds)
            {
                Wg3World w = Compose(seed, budget: 30);

                Assert.GreaterOrEqual(w.placements.Count, 18,
                    $"semilla {seed}: el mundo se ahoga en {w.placements.Count} piezas de 30");

                var ids = new HashSet<string>();
                foreach (Wg3Placement p in w.placements) ids.Add(p.piece.id);
                Assert.GreaterOrEqual(ids.Count, 5,
                    $"semilla {seed}: solo {ids.Count} piezas distintas — el sorteo colapsó");

                int[] histogram = w.ScaleHistogram();
                int classesPresent = 0;
                for (int i = 0; i < histogram.Length; i++) if (histogram[i] > 0) classesPresent++;
                Assert.GreaterOrEqual(classesPresent, 2,
                    $"semilla {seed}: una sola clase de escala — el campo no está modulando nada");

                int repeatedInARow = 0;
                for (int i = 1; i < w.placements.Count; i++)
                {
                    Wg3Placement child = w.placements[i];
                    if (child.parentIndex >= 0 &&
                        w.placements[child.parentIndex].piece.id == child.piece.id) repeatedInARow++;
                }
                Assert.Less(repeatedInARow, w.placements.Count / 3,
                    $"semilla {seed}: {repeatedInARow} piezas repiten a su padre — R26 no está penalizando");
            }
        }

        [Test]
        public void TheBudgetIsRespected()
        {
            foreach (int budget in new[] { 1, 5, 30, 80 })
            {
                Wg3World w = Compose(42, budget);
                Assert.LessOrEqual(w.placements.Count, budget);
                Assert.GreaterOrEqual(w.placements.Count, 1);
            }
        }

        [Test]
        public void WithoutDeliberateCapsTheWorldIsStillSealed()
        {
            // L21 apagada del todo: el mundo se ramifica hasta agotar presupuesto y TODA boca
            // restante tiene que quedar sellada por la pasada final. Sin ella, un presupuesto
            // agotado deja agujeros al vacío.
            foreach (int seed in Seeds)
            {
                Wg3World w = Compose(seed, 30, capChance: 0f);
                Assert.IsEmpty(Wg3Validator.ValidateWorld(w));
                Assert.Greater(w.caps.Count, 0, $"semilla {seed}: ni un tapón, sospechoso");
            }
        }

        // ── L6: la anchura es compatibilidad ────────────────────────────────────────────────

        [Test]
        public void SocketsOfTheSameTypeButDifferentWidthDoNotConnect()
        {
            var narrow = new Wg3Piece
            {
                id = "a", sizeX = 10f, sizeZ = 3f, heightMeters = 3.2f,
                sockets = new[]
                {
                    new Wg3Socket(3, 1.5f, 2.4f, Wg3SocketType.Corridor),
                    new Wg3Socket(1, 1.5f, 2.4f, Wg3SocketType.Corridor)
                }
            };
            var wider = new Wg3Piece
            {
                id = "b", sizeX = 10f, sizeZ = 4f, heightMeters = 3.2f,
                sockets = new[] { new Wg3Socket(3, 2f, 3.0f, Wg3SocketType.Corridor) }
            };

            Wg3World w = Wg3Composer.Compose(42, new List<Wg3Piece> { narrow, wider },
                new Wg3ComposerSettings { budget = 10, deliberateCapChance = 0f });

            // `narrow` SÍ encadena consigo misma —2,4 con 2,4 es una junta válida— así que el
            // mundo crece. Lo que no puede pasar es que entre `wider`: su única boca mide 3,0 m.
            foreach (Wg3Placement p in w.placements)
                Assert.AreNotEqual("b", p.piece.id,
                    "una boca de 2,4 m aceptó una de 3,0 m: la anchura dejó de ser compatibilidad");
            Assert.Greater(w.rejectedByValidator, 0, "el rechazo por anchura no se está contando");
        }

        [Test]
        public void MismatchedFloorHeightsDoNotConnect()
        {
            // F0 mantiene todo a cota 0 y el validador es estricto. Cuando F5 abra las cotas, este
            // test tendrá que cambiar A PROPÓSITO — que es justo lo que se quiere de él.
            var flat = new Wg3Socket(1, 1.5f, 2.4f, Wg3SocketType.Corridor, 0f, 3.2f);
            var raised = new Wg3Socket(3, 1.5f, 2.4f, Wg3SocketType.Corridor, 1.4f, 4.6f);
            Assert.IsFalse(Wg3Validator.ValidateConnection(flat, raised, out string reason));
            Assert.IsNotNull(reason);
        }

        [Test]
        public void HeadroomBelowTheMinimumIsRejected()
        {
            var low = new Wg3Piece
            {
                id = "low", sizeX = 6f, sizeZ = 4f, heightMeters = 3.2f,
                sockets = new[] { new Wg3Socket(3, 2f, 2.4f, Wg3SocketType.Corridor, 0f, 1.5f) }
            };
            Assert.IsNotEmpty(Wg3Validator.ValidatePiece(low));
        }

        [Test]
        public void ASocketThatDoesNotFitItsSideIsRejected()
        {
            // "Puerta dentro de una pared", el primer punto de L23.
            var bad = new Wg3Piece
            {
                id = "bad", sizeX = 6f, sizeZ = 2f, heightMeters = 3.2f,
                sockets = new[] { new Wg3Socket(3, 1f, 5.0f, Wg3SocketType.Wide) }
            };
            Assert.IsNotEmpty(Wg3Validator.ValidatePiece(bad));
        }
    }
}
