using System.Collections.Generic;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// LAS CARAS ENTERRADAS — la poda que quita triángulos y mata el z-fighting.
    ///
    /// El fallo que estos tests existen para impedir NO es «sobra un triángulo»: es podar una cara
    /// que SÍ se ve, y eso es un agujero por el que se mira el interior de una pared. Por eso la
    /// mitad de los casos de aquí abajo comprueban que una cara NO se poda: cobertura parcial, caja
    /// girada, marco, prisma y caja suelta.
    /// </summary>
    [TestFixture]
    public class Wg3HiddenFaceTests
    {
        private static Wg3Volume Box(Vector3 centre, Vector3 size,
            Wg3VolumeKind kind = Wg3VolumeKind.Wall, float yaw = 0f, byte shape = 0) => new Wg3Volume
            {
                center = centre,
                size = size,
                yawDegrees = yaw,
                kind = kind,
                shape = shape,
            };

        /// <summary>Cuántas caras se emitieron: 4 vértices por cara.</summary>
        private static int FaceCount(params Wg3Volume[] volumes)
        {
            Mesh m = Wg3MeshBuilder.Build(new List<Wg3Volume>(volumes), Vector3.zero);
            int faces = m.vertexCount / 4;
            Object.DestroyImmediate(m);
            return faces;
        }

        /// <summary>Vértices crudos. Para comparar por SUMA hace falta esto y no
        /// <see cref="FaceCount"/>: un prisma emite abanicos de tapa, así que su cuenta de vértices
        /// no es múltiplo de cuatro y la división entera se comería el resto.</summary>
        private static int VertexCount(params Wg3Volume[] volumes)
        {
            Mesh m = Wg3MeshBuilder.Build(new List<Wg3Volume>(volumes), Vector3.zero);
            int n = m.vertexCount;
            Object.DestroyImmediate(m);
            return n;
        }

        /// <summary>Cuántas caras miran hacia <paramref name="dir"/>.</summary>
        private static int FacesTowards(Vector3 dir, params Wg3Volume[] volumes)
        {
            Mesh m = Wg3MeshBuilder.Build(new List<Wg3Volume>(volumes), Vector3.zero);
            Vector3[] norms = m.normals;
            int n = 0;
            for (int i = 0; i < norms.Length; i += 4)
                if (Vector3.Dot(norms[i], dir) > 0.99f) n++;
            Object.DestroyImmediate(m);
            return n;
        }

        [Test]
        public void UnaCajaSola_ConservaSusSeisCaras()
        {
            Assert.AreEqual(6, FaceCount(Box(new Vector3(0f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f))));
        }

        [Test]
        public void DosCajasIdenticasQueSeTocan_PierdenLaJuntaYSoloLaJunta()
        {
            // Dos tramos de pared de 2 m pegados: 12 caras sueltas, 10 al podar las dos coplanares
            // de la junta. Ésas son exactamente las que producen el z-fighting.
            var a = Box(new Vector3(0f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f));
            var b = Box(new Vector3(2f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f));

            Assert.AreEqual(6, FaceCount(a), "cada una por separado son seis");
            Assert.AreEqual(10, FaceCount(a, b), "juntas, se van las dos de la junta");

            // Y las que se van son las de la junta, no otras: quedan una a cada lado del muro.
            Assert.AreEqual(1, FacesTowards(Vector3.right, a, b), "sólo el extremo derecho mira a +x");
            Assert.AreEqual(1, FacesTowards(Vector3.left, a, b));
            Assert.AreEqual(2, FacesTowards(Vector3.back, a, b), "las dos caras vistas siguen ahí");
            Assert.AreEqual(2, FacesTowards(Vector3.forward, a, b));
        }

        [Test]
        public void UnaCajaDENTRODeOtra_PierdeLasSeis()
        {
            // El caso extremo: una caja pequeña enterrada del todo. No se ve ninguna de sus caras.
            var grande = Box(new Vector3(0f, 0f, 0f), new Vector3(10f, 10f, 10f));
            var dentro = Box(new Vector3(0f, 0f, 0f), new Vector3(1f, 1f, 1f));

            Assert.AreEqual(6, FaceCount(grande, dentro), "sólo quedan las seis de la grande");
        }

        [Test]
        public void CoberturaPARCIAL_NoPoda_YLaRelacionEsASIMETRICA()
        {
            // LA REGLA QUE IMPIDE EL AGUJERO, y el caso que enseña que «tapar» no es mutuo.
            //
            // La vecina es más baja. Su cara hacia la alta SÍ desaparece: la alta la cubre entera.
            // La de la alta hacia ella NO: la baja sólo le tapa el metro de abajo, y podarla dejaría
            // ver el interior de la pared por los dos metros de arriba. Una sola cara se va, no dos.
            var alta = Box(new Vector3(0f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f));
            var baja = Box(new Vector3(2f, 0.5f, 0f), new Vector3(2f, 1f, 0.2f));

            Assert.AreEqual(11, FaceCount(alta, baja), "se va UNA cara, la de la baja");

            // Hacia +x quedan DOS: la de la alta —que la baja sólo tapa por el metro de abajo, así
            // que se conserva— y el extremo derecho de la baja, que no tiene nada delante.
            Assert.AreEqual(2, FacesTowards(Vector3.right, alta, baja),
                "la alta CONSERVA su cara hacia la baja, y la baja su extremo libre");

            // Y hacia −x queda UNA, la de la alta: la de la baja es la que se podó, porque la alta
            // la cubre entera. Ésta es la aserción que demuestra la asimetría.
            Assert.AreEqual(1, FacesTowards(Vector3.left, alta, baja),
                "la baja perdió la suya contra la alta, que sí la cubre entera");
        }

        [Test]
        public void CajasQueNiSeTocan_NoPodanNada()
        {
            var a = Box(new Vector3(0f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f));
            var b = Box(new Vector3(10f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f));
            Assert.AreEqual(12, FaceCount(a, b));
        }

        [Test]
        public void UnaCajaGIRADA_NiPodaNiSeLePoda()
        {
            // ADR-121 — con giro, las caras dejan de ser perpendiculares a los ejes y la comparación
            // de rectángulos deja de valer. Se apartan las dos direcciones a propósito.
            var recta = Box(new Vector3(0f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f));
            var girada = Box(new Vector3(2f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f), yaw: 45f);

            Assert.AreEqual(12, FaceCount(recta, girada));
        }

        [Test]
        public void UnMARCO_NoTapaAunqueSuCajaLoDiga()
        {
            // ADR-125 enm. 2 — al marco se le talla un perfil de dos escalones y un zócalo, así que
            // NO llena su caja: tratarlo como tapador abriría un agujero en la pared de detrás.
            var pared = Box(new Vector3(0f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f));
            var marco = Box(new Vector3(2f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f),
                kind: Wg3VolumeKind.Casing);

            // Se comparan TOTALES y no caras hacia +x: `AddCasingBox` emite el perfil de dos
            // escalones, así que el marco aporta varias caras propias mirando a +x y contarlas no
            // diría nada de la pared. Si la suma cuadra, no se podó nada por ninguno de los dos
            // lados — que es justo lo que se quiere de un tapador que no tapa.
            Assert.AreEqual(VertexCount(pared) + VertexCount(marco), VertexCount(pared, marco),
                "el marco no llena su caja: ni tapa a la pared ni el arnés le poda a él");
        }

        [Test]
        public void AUnaLosetaPegadaALaPared_SeLePodaElDorso()
        {
            // Al revés sí: el dorso de un rodapié contra la pared no se ve nunca. Es el caso que
            // más piezas quita, porque toda la decoración va pegada a algo.
            // `Decoration` y no `Casing`: al marco se le talla el perfil y no emite seis caras
            // limpias, así que contar caras sobre él no diría nada. Una loseta (ADR-105 enm. 20) sí
            // es una caja lisa, y es igual de decorativa y va igual de pegada.
            var pared = Box(new Vector3(0f, 1.5f, 0f), new Vector3(4f, 3f, 0.4f));
            var loseta = Box(new Vector3(0f, 0.06f, 0.25f), new Vector3(3f, 0.12f, 0.1f),
                kind: Wg3VolumeKind.Decoration);

            Assert.AreEqual(1, FacesTowards(Vector3.back, loseta),
                "arnés: suelta, la loseta tiene su dorso");
            Assert.AreEqual(11, FaceCount(pared, loseta),
                "contra la pared pierde el dorso, y sólo el dorso: 6 + 6 − 1");
            Assert.AreEqual(1, FacesTowards(Vector3.back, pared, loseta),
                "la que queda mirando a −z es la de la pared, no la de la loseta");
        }

        [Test]
        public void UnPrisma_NiPodaNiSeLePoda()
        {
            // ADR-125 — un cilindro no llena su caja. Ni tapa ni se le tapa.
            var caja = Box(new Vector3(0f, 1.5f, 0f), new Vector3(2f, 3f, 2f));
            var cilindro = Box(new Vector3(2f, 1.5f, 0f), new Vector3(2f, 3f, 2f), shape: 1);

            // Por totales, y por lo mismo que el marco: el cilindro aporta caras laterales propias
            // mirando a +x, así que contarlas por dirección no aísla a la caja.
            Assert.AreEqual(VertexCount(caja) + VertexCount(cilindro), VertexCount(caja, cilindro),
                "ni el cilindro tapa a la caja ni la caja al cilindro");
        }

        [Test]
        public void ElSueloDeUnEspacio_TapaElTechoDelDeAbajo()
        {
            // El caso vertical, que es el que más superficie quita en un edificio: la losa de techo
            // de una planta y la de suelo de la de arriba son la misma junta.
            var techoAbajo = Box(new Vector3(0f, 3.0f, 0f), new Vector3(6f, 0.2f, 6f),
                kind: Wg3VolumeKind.Ceiling);
            var sueloArriba = Box(new Vector3(0f, 3.2f, 0f), new Vector3(6f, 0.2f, 6f),
                kind: Wg3VolumeKind.Floor);

            Assert.AreEqual(10, FaceCount(techoAbajo, sueloArriba),
                "la cara de arriba del techo y la de abajo del suelo son la misma junta");
            Assert.AreEqual(1, FacesTowards(Vector3.up, techoAbajo, sueloArriba));
            Assert.AreEqual(1, FacesTowards(Vector3.down, techoAbajo, sueloArriba));
        }

        [Test]
        public void LaPodaNoDependeDelOrdenDeLaLista()
        {
            // Si dependiera, dos clientes que reciban los mismos macizos en distinto orden verían
            // geometría distinta — y el reparto por chunk no garantiza ningún orden.
            var a = Box(new Vector3(0f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f));
            var b = Box(new Vector3(2f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f));
            var c = Box(new Vector3(4f, 1.5f, 0f), new Vector3(2f, 3f, 0.2f));

            Assert.AreEqual(FaceCount(a, b, c), FaceCount(c, b, a));
            Assert.AreEqual(FaceCount(a, b, c), FaceCount(b, a, c));
        }
    }
}
