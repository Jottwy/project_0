using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// R5 (2026-09-12) — el suelo de WG3 no llevaba <c>PhysicsMaterial</c>, así que el sistema de
    /// pasos de PolymindGames caía siempre al surface por defecto: oficina, nave y servicio sonaban
    /// igual al pisar. <see cref="Wg3StyleSurfaces"/> resuelve un <c>PhysicsMaterial</c> distinto
    /// para la oficina, y <c>Wg3SceneAssembler.AddColliders</c> lo aplica SOLO al suelo (y a los
    /// escalones), nunca a paredes ni techo.
    /// </summary>
    public sealed class Wg3StyleSurfacesTests
    {
        [Test]
        public void OficinaYElRestoResuelvenMaterialesDistintos()
        {
            PhysicsMaterial office = Wg3StyleSurfaces.FloorFor(Wg3LightCadence.StyleOffice);
            PhysicsMaterial corridor = Wg3StyleSurfaces.FloorFor(Wg3LightCadence.StyleCorridor);

            Assert.IsNotNull(office,
                "sin FPS_Dirt en el catálogo de SurfaceDefinition, el paso de oficina cae al default: revisa que el arnés vea Resources/Definitions/Surface");
            Assert.IsNotNull(corridor,
                "sin FPS_Concrete en el catálogo de SurfaceDefinition, el paso de pasillo cae al default");
            Assert.AreNotEqual(office, corridor, "oficina y pasillo tienen que sonar distinto al pisar");
        }

        [Test]
        public void CualquierEstiloNoOficinaResuelveElMismoSueloDuro()
        {
            // 1 espina, 2 pasillo, 3 nave, 4 servicio, 5 callejón, 6 escalera: hoy sólo hay dos
            // superficies disponibles en el catálogo del vendor, así que TODOS caen al mismo suelo
            // duro — es un hueco declarado (ver el remarks de Wg3StyleSurfaces), no un bug.
            PhysicsMaterial reference = Wg3StyleSurfaces.FloorFor(1);
            for (byte style = 2; style <= 6; style++)
                Assert.AreEqual(reference, Wg3StyleSurfaces.FloorFor(style),
                    $"estilo {style} debería compartir el suelo duro con el estilo 1 hasta que haya más superficies");
        }

        // --- Integración: AddColliders (vía AssembleSegment) sólo viste el SUELO ---

        private static Wg3Segment MakeSegment(byte style) => new Wg3Segment
        {
            xCm = 0,
            zCm = 0,
            sizeXCm = 400,
            sizeZCm = 400,
            floorYCm = 0,
            heightCm = 332,
            style = style,
        };

        [Test]
        public void SoloElSueloRecibeElPhysicsMaterial()
        {
            var parent = new GameObject("chunk");
            GameObject go = null;
            try
            {
                var created = new List<Mesh>();
                go = Wg3SceneAssembler.AssembleSegment(MakeSegment(Wg3LightCadence.StyleOffice),
                    parent.transform, null, created, "seg", addLight: false);
                Assert.IsNotNull(go);

                PhysicsMaterial expectedFloor = Wg3StyleSurfaces.FloorFor(Wg3LightCadence.StyleOffice);
                BoxCollider[] boxes = go.GetComponents<BoxCollider>();
                Assert.Greater(boxes.Length, 1, "un tramo tiene que traer más de un collider (suelo, techo, paredes)");

                int withMaterial = 0;
                foreach (BoxCollider b in boxes)
                    if (b.sharedMaterial != null)
                    {
                        Assert.AreEqual(expectedFloor, b.sharedMaterial,
                            "el único material que puede aparecer en un tramo es el del suelo");
                        withMaterial++;
                    }

                Assert.AreEqual(1, withMaterial,
                    "exactamente un collider (el suelo) debe llevar PhysicsMaterial; el resto se queda sin él");

                foreach (Mesh m in created) Object.DestroyImmediate(m);
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                Object.DestroyImmediate(parent);
            }
        }
    }
}
