using System.Collections.Generic;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La COSTURA DE TEXTURA entre dos macizos, y las tres propiedades que la cierran.
    ///
    /// Antes del 2026-09-06 cada cara arrancaba su UV en (0,0). El tamaño era correcto —las UV se
    /// emiten en metros— pero la fase se reiniciaba en cada caja, así que dos tramos de pared
    /// alineados cortaban el dibujo del gotelé justo en la junta. Lo que se prueba aquí no es que
    /// «se vea bien», que eso lo dice una captura, sino la aritmética de la que depende: si la UV
    /// de un punto del mundo depende de en qué caja o en qué malla vino, la costura vuelve.
    ///
    /// Y la tercera es la que importa para el fundido por chunk: la UV NO puede depender del
    /// `origin` de la malla, o al agrupar macizos en una malla común la textura se movería.
    /// </summary>
    [TestFixture]
    public class Wg3WorldUvTests
    {
        private const float Eps = 1e-3f;

        private static Wg3Volume Wall(Vector3 centre, Vector3 size) => new Wg3Volume
        {
            center = centre,
            size = size,
            yawDegrees = 0f,
            kind = Wg3VolumeKind.Wall,
            shape = Wg3Shape.Box,
        };

        private static Mesh Build(Wg3Volume v, Vector3 origin) =>
            Wg3MeshBuilder.Build(new List<Wg3Volume> { v }, origin);

        /// <summary>La UV del vértice que está en <paramref name="worldPoint"/>. Se busca por
        /// posición y no por índice: el orden de las caras es un detalle del emisor.</summary>
        private static Vector2 UvAt(Mesh mesh, Vector3 origin, Vector3 worldPoint)
        {
            Vector3[] verts = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < verts.Length; i++)
            {
                float d = (verts[i] + origin - worldPoint).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            Assert.Less(Mathf.Sqrt(bestD), Eps,
                $"ningún vértice en {worldPoint}: el más cercano estaba a {Mathf.Sqrt(bestD):0.###} m");
            return uvs[best];
        }

        /// <summary>La textura no distingue una repetición de la siguiente, así que dos UV que
        /// difieren en un entero son la MISMA. Comparar en crudo daría falsos rojos.</summary>
        private static void AssertSameTexel(Vector2 a, Vector2 b, string what)
        {
            float du = Mathf.Abs(Mathf.Repeat(a.x - b.x + 0.5f, 1f) - 0.5f);
            float dv = Mathf.Abs(Mathf.Repeat(a.y - b.y + 0.5f, 1f) - 0.5f);
            Assert.Less(du, Eps, $"{what}: la U se desvía {du:0.####} de repetición");
            Assert.Less(dv, Eps, $"{what}: la V se desvía {dv:0.####} de repetición");
        }

        [Test]
        public void DosMacizosContiguos_CosenLaTexturaEnLaJunta()
        {
            // Dos tramos de pared de 4 m, uno detrás del otro sobre el mismo plano z = 0. Comparten
            // la arista x = 12: el mismo punto del mundo visto desde las dos cajas.
            var a = Wall(new Vector3(10f, 1.5f, 0f), new Vector3(4f, 3f, 0.2f));
            var b = Wall(new Vector3(14f, 1.5f, 0f), new Vector3(4f, 3f, 0.2f));

            Mesh ma = Build(a, Vector3.zero);
            Mesh mb = Build(b, Vector3.zero);

            // La esquina compartida, en la cara que mira a −z, al ras del suelo.
            var junta = new Vector3(12f, 0f, -0.1f);
            AssertSameTexel(UvAt(ma, Vector3.zero, junta), UvAt(mb, Vector3.zero, junta),
                "la junta entre dos tramos alineados");

            Object.DestroyImmediate(ma);
            Object.DestroyImmediate(mb);
        }

        [Test]
        public void ElOrigenDeLaMalla_NoMueveLaTextura()
        {
            // LA PROPIEDAD QUE HABILITA EL FUNDIDO POR CHUNK. Los vértices se emiten relativos al
            // origen para no perder precisión lejos del (0,0), pero la UV se ancla al mundo: la
            // misma caja metida en dos mallas con orígenes distintos tiene que texturarse igual. Si
            // esto se rompe, agrupar macizos en una malla común desplaza el dibujo de todos ellos.
            var v = Wall(new Vector3(37f, 1.5f, 11f), new Vector3(4f, 3f, 0.2f));

            var o1 = new Vector3(35f, 0f, 10f);
            var o2 = new Vector3(0f, 0f, 0f);
            Mesh m1 = Build(v, o1);
            Mesh m2 = Build(v, o2);

            var punto = new Vector3(35f, 0f, 10.9f);
            AssertSameTexel(UvAt(m1, o1, punto), UvAt(m2, o2, punto), "dos orígenes de malla");

            Object.DestroyImmediate(m1);
            Object.DestroyImmediate(m2);
        }

        [Test]
        public void UnPeriodoDeDesplazamiento_DevuelveLaMismaUV_YMedioNo()
        {
            // El periodo es el inverso de UvPerMetre por construcción. Mover una pared justo un
            // periodo la deja texturada igual; moverla medio periodo la desplaza medio tile. Es lo
            // que separa «anclado al mundo» de «anclado a nada»: sin ancla, las tres darían igual.
            float p = Wg3MeshBuilder.RepeatPeriodM;
            Assert.AreEqual(1f / Wg3MeshBuilder.UvPerMetre, p, 1e-6f);

            var baseCentre = new Vector3(10f, 1.5f, 0f);
            var size = new Vector3(4f, 3f, 0.2f);

            Mesh m0 = Build(Wall(baseCentre, size), Vector3.zero);
            Mesh mP = Build(Wall(baseCentre + new Vector3(p, 0f, 0f), size), Vector3.zero);
            Mesh mH = Build(Wall(baseCentre + new Vector3(p * 0.5f, 0f, 0f), size), Vector3.zero);

            var esquina = new Vector3(8f, 0f, -0.1f);
            Vector2 u0 = UvAt(m0, Vector3.zero, esquina);
            Vector2 uP = UvAt(mP, Vector3.zero, esquina + new Vector3(p, 0f, 0f));
            Vector2 uH = UvAt(mH, Vector3.zero, esquina + new Vector3(p * 0.5f, 0f, 0f));

            AssertSameTexel(u0, uP, "un periodo entero de desplazamiento");

            float medio = Mathf.Abs(Mathf.Repeat(u0.x - uH.x + 0.5f, 1f) - 0.5f);
            Assert.That(medio, Is.EqualTo(0.5f).Within(Eps),
                "medio periodo tiene que mover la textura medio tile, y movió " + medio);

            Object.DestroyImmediate(m0);
            Object.DestroyImmediate(mP);
            Object.DestroyImmediate(mH);
        }

        [Test]
        public void ElGranoSigueSiendoElValidado_MedioMetroPorRepeticion()
        {
            // `UvPerMetre` = 0,5 lo dio por bueno Joel el 06-09 y este cambio NO lo toca: mueve la
            // fase, no la escala. Una pared de 4 m tiene que seguir cubriendo 2 repeticiones.
            var v = Wall(new Vector3(10f, 1.5f, 0f), new Vector3(4f, 3f, 0.2f));
            Mesh m = Build(v, Vector3.zero);

            Vector2 izq = UvAt(m, Vector3.zero, new Vector3(8f, 0f, -0.1f));
            Vector2 der = UvAt(m, Vector3.zero, new Vector3(12f, 0f, -0.1f));

            Assert.That(Mathf.Abs(der.x - izq.x), Is.EqualTo(4f * Wg3MeshBuilder.UvPerMetre).Within(Eps),
                "4 m de pared son 2 repeticiones con UvPerMetre 0,5");

            Object.DestroyImmediate(m);
        }

        [Test]
        public void ElCuboUnitario_SigueEnUvLocal()
        {
            // La luminaria comparte UNA malla escalada por el transform para todas las lámparas de
            // la sesión: no tiene sitio propio en el mundo del que colgar la fase, y anclarla haría
            // que todas compartieran la UV de la caja unitaria en el origen.
            Mesh cubo = Wg3MeshBuilder.BuildUnitCube();
            Vector2[] uvs = cubo.uv;

            bool hayCero = false;
            for (int i = 0; i < uvs.Length; i++)
                if (uvs[i].sqrMagnitude < Eps * Eps) { hayCero = true; break; }

            Assert.IsTrue(hayCero, "el cubo unitario debe conservar una UV que arranque en (0,0)");
            Object.DestroyImmediate(cubo);
        }
    }
}
