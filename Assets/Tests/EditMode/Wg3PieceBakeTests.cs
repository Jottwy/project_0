using BackroomsSurvival.Gameplay.GridWorld;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El horno de piezas autoradas: que el dibujo del editor de salas llegue a WG3 sin traerse la
    /// rejilla de 5 m con él.
    ///
    /// Este fichero existe por una objeción concreta que estaba escrita en el código: reutilizar
    /// <c>RoomDefinition</c> "devolvería WG3 a la retícula de la que viene huyendo", porque el
    /// modelo se mide en tiles de 5 m. La respuesta es que los tiles son el LIENZO y no la pieza —
    /// y una respuesta así solo vale si hay un test que la sostenga cuando alguien toque el horno
    /// dentro de seis meses.
    /// </summary>
    public sealed class Wg3PieceBakeTests
    {
        private const float Thickness = 0.15f;

        /// <summary>
        /// Un pasillo de 2,4 × 11 m dibujado a mano sobre un lienzo de 4 × 4 tiles, o sea de
        /// 20 × 20 m. La desproporción es el punto: si el horno mirara los tiles, la huella saldría
        /// ocho veces más ancha de lo que mide el pasillo.
        /// </summary>
        private static RoomDefinition Corridor(float holeWidth = 2.4f, float holeBaseY = 0f,
            int holeSide = 0)
        {
            var def = new RoomDefinition
            {
                tilesX = 4,
                tilesZ = 4,
                heightMeters = 3.2f,
                wallThickness = Thickness,
                planMode = RoomDefinition.PlanMode.Manual,
                manualContour = new[]
                {
                    new Vector2(-1.2f, -5.5f),
                    new Vector2(1.2f, -5.5f),
                    new Vector2(1.2f, 5.5f),
                    new Vector2(-1.2f, 5.5f)
                }
            };
            def.holes = new[]
            {
                new RoomDefinition.WallHole
                {
                    side = holeSide,
                    along = 0.5f,
                    baseY = holeBaseY,
                    width = holeWidth,
                    height = 2.2f
                }
            };
            return def;
        }

        [Test]
        public void TheFootprintComesFromTheContourAndNotFromTheTiles()
        {
            Wg3PieceBake.Result baked = Wg3PieceBake.From(Corridor(), "cor_test");

            Assert.IsTrue(baked.Ok, string.Join("\n", baked.issues));

            // 2,4 m de paso + el grosor de pared a cada lado. NO 20 m, que es lo que medirían
            // `tilesX * 5` — ese número es el lienzo del editor y se queda en el editor.
            Assert.AreEqual(2.4f + 2f * Thickness, baked.sizeX, 0.001f,
                "la huella en X salió de los tiles y no del contorno");
            Assert.AreEqual(11f + 2f * Thickness, baked.sizeZ, 0.001f,
                "la huella en Z salió de los tiles y no del contorno");
            Assert.AreEqual(3.2f, baked.heightMeters, 0.001f);
        }

        [Test]
        public void TheOriginMovesToTheMinimumCorner()
        {
            Wg3PieceBake.Result baked = Wg3PieceBake.From(Corridor(), "cor_test");
            Assert.IsTrue(baked.Ok, string.Join("\n", baked.issues));

            // El modelo dibuja centrado en el origen; WG3 pone el origen en la esquina mínima. Si la
            // traslación faltara, media pieza saldría en coordenadas negativas y el mundo aparecería
            // desplazado media huella por pieza — un fallo que se ve como "las piezas no encajan".
            foreach (Wg3Volume v in baked.volumes)
            {
                Assert.GreaterOrEqual(v.center.x - v.size.x * 0.5f, -0.001f,
                    "hay una caja a la izquierda del origen: falta trasladar a esquina mínima");
                Assert.GreaterOrEqual(v.center.z - v.size.z * 0.5f, -0.001f,
                    "hay una caja detrás del origen: falta trasladar a esquina mínima");
                Assert.LessOrEqual(v.center.x + v.size.x * 0.5f, baked.sizeX + 0.001f);
                Assert.LessOrEqual(v.center.z + v.size.z * 0.5f, baked.sizeZ + 0.001f);
            }
        }

        [Test]
        public void AFloorLevelHoleBecomesATypedSocketOnTheRightSide()
        {
            Wg3PieceBake.Result baked = Wg3PieceBake.From(Corridor(), "cor_test");
            Assert.IsTrue(baked.Ok, string.Join("\n", baked.issues));
            Assert.AreEqual(1, baked.sockets.Length);

            Wg3Socket s = baked.sockets[0];

            // El lado 0 del contorno va de (−1,2,−5,5) a (1,2,−5,5): mira a −Z, que en WG3 es el
            // lado 2 (S). Es la conversión que se rompería sola si alguien invirtiera el sentido de
            // giro del contorno, y entonces todas las bocas mirarían hacia dentro.
            Assert.AreEqual(2, s.side, "la boca no cayó en el lado sur");

            // Centrada en un lado de 2,7 m.
            Assert.AreEqual((2.4f + 2f * Thickness) * 0.5f, s.offset, 0.001f);
            Assert.AreEqual(2.4f, s.width, 0.001f);
            Assert.AreEqual(Wg3SocketType.Corridor, s.type, "el ancho ES el tipo de la boca");
            Assert.AreEqual(0f, s.floorY, 0.001f);
            Assert.AreEqual(2.2f, s.ceilingY, 0.001f);
        }

        [Test]
        public void AHoleAboveTheFloorIsAWindowAndNotASocket()
        {
            // Una ventana convertida en boca haría que el compositor enchufara un pasillo a metro y
            // medio del suelo, y el jugador se encontraría el vano en la pared sin forma de pasar.
            Wg3PieceBake.Result baked = Wg3PieceBake.From(Corridor(holeBaseY: 1.5f), "cor_test");

            Assert.AreEqual(1, baked.windows, "el agujero alto no se contó como ventana");
            Assert.IsFalse(baked.Ok, "una pieza sin ninguna boca no puede hornearse");
            Assert.IsTrue(baked.issues.Exists(i => i.Contains("boca")),
                "el motivo no dice que el problema son las bocas: " + string.Join("\n", baked.issues));
        }

        [Test]
        public void AWidthTheComposerCannotMatchRefusesToBake()
        {
            // 1,60 m es el ancho por defecto de una puerta en el editor de salas, así que este es el
            // fallo que va a cometer todo el mundo la primera vez. Tiene que salir por el horno con
            // los anchos válidos escritos, no aparecer como una pieza que nunca se coloca.
            Wg3PieceBake.Result baked = Wg3PieceBake.From(Corridor(holeWidth: 1.6f), "cor_test");

            Assert.IsFalse(baked.Ok);
            Assert.IsTrue(baked.issues.Exists(i => i.Contains("2,40") || i.Contains("2.40")),
                "el motivo no dice cuáles son los anchos válidos: " + string.Join("\n", baked.issues));
        }

        [Test]
        public void EveryBakedPieceKeepsItsDoorwayOpen()
        {
            Wg3PieceBake.Result baked = Wg3PieceBake.From(Corridor(), "cor_test");
            Assert.IsTrue(baked.Ok, string.Join("\n", baked.issues));

            // La comprobación que de verdad importa: que en el punto de la boca no haya una caja
            // sólida a la altura de la cabeza. Una boca correcta en los números y tapiada en la
            // chuleta es exactamente el fallo que el jugador vive como "veo la puerta y me choco".
            Wg3Socket s = baked.sockets[0];
            Vector2 mouth = Wg3Piece.LocalPoint(s.side, s.offset, baked.sizeX, baked.sizeZ);

            // Se sonda DENTRO del grosor de la pared, no en el plano de la huella. Justo en el borde
            // la sonda queda fuera de la caja de pared por medio grosor y el test pasaría aunque el
            // vano estuviera tapiado: mediría el aire de delante de la puerta.
            Vector2 inward = -Wg3Piece.OutwardNormal(s.side) * (Thickness * 0.5f);
            var probe = new Vector3(mouth.x + inward.x, 1.6f, mouth.y + inward.y);

            foreach (Wg3Volume v in baked.volumes)
            {
                if (v.kind == Wg3VolumeKind.Decoration) continue;
                bool inside = Mathf.Abs(probe.x - v.center.x) < v.size.x * 0.5f - 0.01f
                           && Mathf.Abs(probe.y - v.center.y) < v.size.y * 0.5f - 0.01f
                           && Mathf.Abs(probe.z - v.center.z) < v.size.z * 0.5f - 0.01f;
                Assert.IsFalse(inside,
                    $"la boca está tapiada por una caja {v.kind} en {v.center} de {v.size}");
            }
        }
    }
}
