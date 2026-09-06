using System.Collections.Generic;
using BackroomsSurvival.Net;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// DÍA 3 DEL CONTRATO — el fundido de macizos por chunk.
    ///
    /// Lo que se prueba no es que «vaya más rápido», que eso lo dice una medición en Play, sino las
    /// cuatro cosas que el fundido NO puede romper mientras lo consigue: la capa de render por
    /// planta (ADR-104 enm. 2), el macizo invisible de ADR-129 D2, el collider por forma de ADR-125
    /// y que la colisión salga de los volúmenes y no de la malla.
    ///
    /// El cociente que persigue el día 3 lo fija <see cref="TresMilMacizos_NoSonTresMilRenderers"/>.
    /// </summary>
    public class Wg3SolidFusionTests
    {
        private static Wg3SolidMsg Box(int xCm, int zCm, int bottomYCm, byte style = 0,
            int sizeXCm = 100, int sizeZCm = 100, int heightCm = 110) => new Wg3SolidMsg
            {
                xCm = xCm,
                zCm = zCm,
                sizeXCm = sizeXCm,
                sizeZCm = sizeZCm,
                bottomYCm = bottomYCm,
                topYCm = bottomYCm + heightCm,
                style = style,
                shape = Wg3Shape.Box,
            };

        /// <summary>Una planta mide 3,32 m: el espejo de `plan::STOREY_HEIGHT_CM`.</summary>
        private static int Storey(int n) => Mathf.RoundToInt(n * Wg3StoreyLayers.StoreyM * 100f);

        private static List<MeshRenderer> RenderersOf(GameObject parent)
        {
            var found = new List<MeshRenderer>();
            // Por JERARQUÍA y no con `FindObjectsByType`: todo lo que monta WG3 lleva
            // `HideFlags.DontSave`, y la búsqueda por tipo no lo ve. Un arnés que barra la escena
            // por tipo sale ciego y da cero sin decir por qué.
            parent.GetComponentsInChildren(true, found);
            return found;
        }

        private static int BoxCollidersOf(GameObject parent)
        {
            var found = new List<BoxCollider>();
            parent.GetComponentsInChildren(true, found);
            return found.Count;
        }

        private static GameObject Chunk() => new GameObject("chunk");

        [Test]
        public void TresMilMacizos_NoSonTresMilRenderers()
        {
            // 300 macizos en UNA planta y UN estilo: la clave de fundido es la misma para todos, así
            // que tienen que salir en una sola malla. Es el caso que motiva el día 3.
            var parent = Chunk();
            try
            {
                var solids = new List<Wg3SolidMsg>();
                for (int i = 0; i < 300; i++)
                    solids.Add(Box(i * 150, 0, Storey(0)));

                int renderers = Wg3SceneAssembler.AssembleSolids(
                    solids, parent.transform, null, new List<Mesh>(), "solid");

                Assert.AreEqual(1, renderers, "300 macizos de la misma planta y estilo van en UNA malla");
                Assert.AreEqual(1, RenderersOf(parent).Count, "y en la escena tiene que haber uno");
                Assert.AreEqual(300, BoxCollidersOf(parent),
                    "el fundido NO puede perder colliders: son 300 cajas y siguen frenando las 300");
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void DosPlantas_NoComparten_MallaNiCapaDeRender()
        {
            // LA RESTRICCIÓN QUE DA FORMA AL FUNDIDO. El chunk mide 50 m en XZ y no se parte en Y,
            // así que las plantas de una columna caen todas dentro. Un Renderer tiene UNA
            // `renderingLayerMask`: si el fundido las colapsara, volvería la fuga de luz entre pisos
            // que cerró ADR-104 enm. 2 — o, con una sola planta elegida, las demás saldrían negras.
            var parent = Chunk();
            try
            {
                var solids = new List<Wg3SolidMsg>
                {
                    Box(0, 0, Storey(0)),
                    Box(200, 0, Storey(0)),
                    Box(0, 0, Storey(1)),
                    Box(200, 0, Storey(1)),
                };

                int renderers = Wg3SceneAssembler.AssembleSolids(
                    solids, parent.transform, null, new List<Mesh>(), "solid");

                Assert.AreEqual(2, renderers, "dos plantas son dos mallas, no una");

                var masks = new HashSet<uint>();
                foreach (MeshRenderer r in RenderersOf(parent)) masks.Add(r.renderingLayerMask);
                Assert.AreEqual(2, masks.Count, "y cada una con SU capa");

                uint baja = Wg3StoreyLayers.ForSurface(0f, 1.1f);
                uint alta = Wg3StoreyLayers.ForSurface(Wg3StoreyLayers.StoreyM, 1.1f);
                Assert.AreNotEqual(baja, alta, "el arnés no vale si las dos plantas dan la misma capa");
                Assert.IsTrue(masks.Contains(baja) && masks.Contains(alta),
                    "las dos capas que salen tienen que ser las de las dos plantas");
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void DosEstilos_NoComparten_Malla()
        {
            // El estilo elige el juego de materiales, y `sharedMaterials` es por renderer: dos
            // estilos en la misma malla pintarían uno de los dos con el material del otro.
            var parent = Chunk();
            try
            {
                var solids = new List<Wg3SolidMsg>
                {
                    Box(0, 0, Storey(0), style: 0),
                    Box(200, 0, Storey(0), style: 0),
                    Box(400, 0, Storey(0), style: 5),
                };

                int renderers = Wg3SceneAssembler.AssembleSolids(
                    solids, parent.transform, null, new List<Mesh>(), "solid");

                Assert.AreEqual(2, renderers, "dos estilos son dos mallas");
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void ElMacizoInvisible_SigueSinRenderer_YConSuCollider()
        {
            // ADR-129 D2: frena y no se dibuja — la malla la pone el prefab del mueble que va
            // encima. Si el fundido lo arrastrara a la malla común, saldría una caja gris debajo de
            // cada mesa, y es el fallo que menos se parece a un fallo de rendimiento.
            var parent = Chunk();
            try
            {
                var invisible = Box(0, 0, Storey(0));
                invisible.style = Wg3SolidMsg.HiddenBit;

                var solids = new List<Wg3SolidMsg> { invisible, Box(300, 0, Storey(0)) };

                int renderers = Wg3SceneAssembler.AssembleSolids(
                    solids, parent.transform, null, new List<Mesh>(), "solid");

                Assert.AreEqual(1, renderers, "el invisible no suma renderer");
                Assert.AreEqual(1, RenderersOf(parent).Count);
                Assert.AreEqual(2, BoxCollidersOf(parent), "pero SÍ frena: dos colliders, no uno");
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void UnPrisma_ConservaSuPropioMeshCollider()
        {
            // ADR-125: un prisma frena con SU malla, convexa, y el arco con la suya cóncava. Un
            // MeshCollider no puede apuntar a media malla fundida, así que estos salen del fundido
            // por diseño y no por olvido.
            var parent = Chunk();
            try
            {
                var prisma = Box(0, 0, Storey(0));
                prisma.shape = 1; // cilindro

                var solids = new List<Wg3SolidMsg> { prisma, Box(300, 0, Storey(0)) };

                int renderers = Wg3SceneAssembler.AssembleSolids(
                    solids, parent.transform, null, new List<Mesh>(), "solid");

                Assert.AreEqual(2, renderers, "el prisma va por su cuenta: dos renderers");

                var mcs = new List<MeshCollider>();
                parent.GetComponentsInChildren(true, mcs);
                Assert.AreEqual(1, mcs.Count, "y con su MeshCollider propio");
                Assert.IsTrue(mcs[0].convex, "un cilindro es convexo (ADR-125)");
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void LaDecoracionFundida_SeDibujaYNoFrena()
        {
            // ADR-125 enm. 2: un marco sobresale 2 cm de la pared y no puede cerrar media celda del
            // ráster ni frenar al jugador contra la jamba. La colisión sale de los VOLÚMENES
            // filtrados por `IsSolid`, no de la malla — derivarla de la malla metería marcos y
            // rodapiés en la colisión y rompería el contrato de fuente única.
            var parent = Chunk();
            try
            {
                var marco = Box(0, 0, Storey(0), heightCm: 200);
                marco.style = Wg3SolidMsg.DecorBit;

                int renderers = Wg3SceneAssembler.AssembleSolids(
                    new List<Wg3SolidMsg> { marco }, parent.transform, null, new List<Mesh>(), "solid");

                Assert.AreEqual(1, renderers, "la decoración SÍ se dibuja");
                Assert.AreEqual(0, BoxCollidersOf(parent), "y NO frena");
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void ElOrdenDeLasMallas_EsEstableEntreCargas()
        {
            // El recorrido de un Dictionary no está definido. Si el orden cambiara de una carga a
            // otra, dos clientes —o el mismo al volver a un chunk— montarían las mallas en distinto
            // orden: las capturas dejarían de ser comparables y el diagnóstico por jerarquía,
            // imposible. Es la misma regla que prohíbe emitir iterando un HashSet sin ordenar.
            var solids = new List<Wg3SolidMsg>
            {
                Box(0, 0, Storey(2), style: 7),
                Box(200, 0, Storey(0), style: 3),
                Box(400, 0, Storey(1), style: 0),
                Box(600, 0, Storey(0), style: 0),
            };

            string primera = null;
            for (int pass = 0; pass < 3; pass++)
            {
                var parent = Chunk();
                try
                {
                    Wg3SceneAssembler.AssembleSolids(
                        solids, parent.transform, null, new List<Mesh>(), "solid");

                    var nombres = new List<string>();
                    foreach (MeshRenderer r in RenderersOf(parent)) nombres.Add(r.gameObject.name);
                    string firma = string.Join("|", nombres);

                    if (primera == null) primera = firma;
                    else Assert.AreEqual(primera, firma, $"la pasada {pass} montó en otro orden");
                }
                finally { Object.DestroyImmediate(parent); }
            }
        }
    }
}
