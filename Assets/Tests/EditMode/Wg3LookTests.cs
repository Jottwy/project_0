using BackroomsSurvival.WorldGen3;
using NUnit.Framework;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// ADR-105 enm. 19 — el material por función se INFIERE, así que lo que hay que vigilar no es que
    /// la mampara salga gris: es que la regla que la reconoce siga apuntando a la forma que el
    /// servidor emite.
    ///
    /// Estos tests fijan los números del espejo (<c>fill::CUBICLE_T_CM</c> 12 y <c>CUBICLE_H_CM</c>
    /// 140) y las fronteras del falso techo. Si alguien cambia una constante en Rust, el rojo sale
    /// aquí y no en una captura tres sesiones después.
    ///
    /// Nada de <c>Material</c> ni de <c>Resources</c> a propósito: son llamadas nativas y el arnés
    /// .NET que se usa cuando el runner del editor no contesta muere en ellas. La clasificación es
    /// aritmética pura y se comprueba como tal.
    /// </summary>
    public class Wg3LookTests
    {
        [Test]
        public void CubicleWallIsPartition()
        {
            // Una mampara de 2,60 de largo, 12 de grueso, de 0 a 140.
            Assert.AreEqual(Wg3Look.Partition, Wg3Looks.ForSolid(260, 12, 0, 140));
            // Y girada 90°: el grueso es el lado MENOR, no el eje x.
            Assert.AreEqual(Wg3Look.Partition, Wg3Looks.ForSolid(12, 260, 0, 140));
        }

        [Test]
        public void CubicleWallOffTheFloorIsStillPartition()
        {
            // Lo que fija la forma es la ALTURA, no la cota: una mampara de la tercera planta sigue
            // midiendo 140 de alto y sigue siendo tela.
            Assert.AreEqual(Wg3Look.Partition, Wg3Looks.ForSolid(260, 12, 996, 1136));
        }

        [Test]
        public void OtherSolidsAreNotPartitions()
        {
            // Los grosores que el backend reparte entre sus emisores: 8 barrote de rejilla, 15
            // dintel, 20 pretil, 30 división, 35 parteluz, 40 viga, 45 oclusor. Ninguno es tela.
            Assert.AreEqual(Wg3Look.Base, Wg3Looks.ForSolid(260, 8, 0, 140), "barrote de rejilla");
            Assert.AreEqual(Wg3Look.Base, Wg3Looks.ForSolid(260, 15, 0, 140), "dintel");
            Assert.AreEqual(Wg3Look.Base, Wg3Looks.ForSolid(260, 30, 0, 140), "división");
            Assert.AreEqual(Wg3Look.Base, Wg3Looks.ForSolid(260, 45, 0, 140), "oclusor");
            // El grueso correcto con otra altura tampoco: 12 × 230 es la mampara de sala (enm. 8).
            Assert.AreEqual(Wg3Look.Base, Wg3Looks.ForSolid(260, 12, 0, 230));
            // Un pilar cuadrado no tiene lado menor de 12.
            Assert.AreEqual(Wg3Look.Base, Wg3Looks.ForSolid(50, 50, 0, 300));
        }

        [Test]
        public void MirrorConstantsMatchTheBackend()
        {
            Assert.AreEqual(12, Wg3Looks.CubicleThicknessCm, "fill::CUBICLE_T_CM");
            Assert.AreEqual(140, Wg3Looks.CubicleHeightCm, "fill::CUBICLE_H_CM");
        }

        [Test]
        public void OfficeWithDroppedCeilingGetsPlates()
        {
            // El rango que sortea `fill::ceiling_cap_cm` para el carácter oficina: 270..300 de 10 en 10.
            Assert.AreEqual(Wg3Look.OfficeDropped, Wg3Looks.ForSegment(0, 270));
            Assert.AreEqual(Wg3Look.OfficeDropped, Wg3Looks.ForSegment(0, 290));
            // 300 es el empate y cae del lado de la placa, que es la decisión declarada.
            Assert.AreEqual(Wg3Look.OfficeDropped, Wg3Looks.ForSegment(0, 300));
        }

        [Test]
        public void OfficeWithoutDroppedCeilingKeepsTheSlab()
        {
            // Un despacho sin falso techo mide 300..380 (ADR-104 enm. 4): moqueta sí, placa no.
            Assert.AreEqual(Wg3Look.Office, Wg3Looks.ForSegment(0, 310));
            Assert.AreEqual(Wg3Look.Office, Wg3Looks.ForSegment(0, 380));
            // Una megasala de atrio tampoco, obviamente.
            Assert.AreEqual(Wg3Look.Office, Wg3Looks.ForSegment(0, 1636));
        }

        [Test]
        public void CirculationAndTheRestKeepTheBackroomsSurfaces()
        {
            // 1 espina, 2 pasillo, 3 nave, 4 servicio/almacén, 5 callejón, 6 escalera, 7/8 pozo.
            // Un techo bajo en un pasillo NO es un falso techo: es un pasillo bajo.
            for (byte style = 1; style <= 8; style++)
            {
                Assert.AreEqual(Wg3Look.Base, Wg3Looks.ForSegment(style, 280), $"estilo {style} bajo");
                Assert.AreEqual(Wg3Look.Base, Wg3Looks.ForSegment(style, 340), $"estilo {style} alto");
            }
        }

        [Test]
        public void ZeroHeightIsNotADroppedCeiling()
        {
            // Defensa contra un tramo sin altura: la placa se pone porque el techo BAJÓ, no porque
            // falte el dato.
            Assert.AreEqual(Wg3Look.Office, Wg3Looks.ForSegment(0, 0));
        }
    }
}
