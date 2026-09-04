using BackroomsSurvival.Net;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// ADR-105 D4 — el macizo que llega por el cable se monta DONDE dice el cable.
    ///
    /// Existe porque no existía: desde wire 50 el centro del volumen se daba en coordenadas locales
    /// y <c>Wg3MeshBuilder</c> le restaba el origen otra vez, así que todos los macizos del mundo se
    /// dibujaban y colisionaban apilados en (0, 0). Ningún test lo miraba: los de tramos y piezas
    /// prueban álgebra de volúmenes, no dónde acaba la malla. Este mide la malla y el collider en
    /// coordenadas de MUNDO, que es lo único que el jugador ve y contra lo que choca.
    /// </summary>
    public class Wg3SolidAssemblyTests
    {
        private static Wg3SolidMsg Solid() => new Wg3SolidMsg
        {
            xCm = 900,
            zCm = -60,
            sizeXCm = 150,
            sizeZCm = 20,
            bottomYCm = 332,
            topYCm = 442,
            style = 3,
        };

        [Test]
        public void TheMeshAndTheColliderLandWhereTheWireSays()
        {
            var parent = new GameObject("chunk");
            GameObject go = null;
            try
            {
                var created = new System.Collections.Generic.List<Mesh>();
                go = Wg3SceneAssembler.AssembleSolid(Solid(), parent.transform, null, created, "solid");
                Assert.IsNotNull(go, "el macizo válido tiene que montarse");

                // Centro esperado en metros: esquina mínima + medio tamaño.
                var expected = new Vector3(9f + 0.75f, 3.32f + 0.55f, -0.6f + 0.1f);

                Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
                Vector3 meshCentre = go.transform.TransformPoint(mesh.bounds.center);
                Assert.Less((meshCentre - expected).magnitude, 0.001f,
                    $"la malla está centrada en {meshCentre}, no en {expected}: el macizo se dibuja en otro sitio");
                Assert.Less((mesh.bounds.size - new Vector3(1.5f, 1.1f, 0.2f)).magnitude, 0.001f,
                    "la malla no mide lo que el cable dice");

                var box = go.GetComponent<BoxCollider>();
                Assert.IsNotNull(box, "un macizo frena: lleva collider");
                Vector3 boxCentre = go.transform.TransformPoint(box.center);
                Assert.Less((boxCentre - expected).magnitude, 0.001f,
                    $"el collider está centrado en {boxCentre}, no en {expected}: colisión y dibujo separados");

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
