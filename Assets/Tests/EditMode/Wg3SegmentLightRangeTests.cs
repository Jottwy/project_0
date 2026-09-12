using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// R3 (2026-09-12, Joel autorizó tocar el alcance de las luces WG3): una Light sin sombra propia
    /// no sabe que hay una pared — Forward+ no la ocluye — así que el alcance fijo de 11/18 m,
    /// pensado para naves de 25 m, regaba dos salas vecinas cuando el tramo medía 4. Estos tests
    /// fijan la fórmula de <c>Wg3SceneAssembler.BoundedRange</c> (privada: se llega por reflexión,
    /// mismo patrón que el resto de la suite) y comprueban que el tramo pequeño sale capado y la nave
    /// sale exactamente como antes.
    /// </summary>
    public sealed class Wg3SegmentLightRangeTests
    {
        private static float BoundedRange(float localX, float localZ, float sizeX, float sizeZ,
            float heightAboveFloor, float maxRange)
        {
            MethodInfo m = typeof(Wg3SceneAssembler).GetMethod("BoundedRange",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(m, "Wg3SceneAssembler.BoundedRange ha cambiado de firma o desaparecido");
            return (float)m.Invoke(null, new object[] { localX, localZ, sizeX, sizeZ, heightAboveFloor, maxRange });
        }

        private static bool NearAnyLitFixture(float px, float pz, List<Vector2> litPositions, float radiusSq)
        {
            MethodInfo m = typeof(Wg3SceneAssembler).GetMethod("NearAnyLitFixture",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(m, "Wg3SceneAssembler.NearAnyLitFixture ha cambiado de firma o desaparecido");
            return (bool)m.Invoke(null, new object[] { px, pz, litPositions, radiusSq });
        }

        [Test]
        public void UnaSalaPequenaCapaPorDebajoDelTecho()
        {
            // Lámpara en el centro de una sala de 4×4, colgada a 3 m — el caso real de
            // AddSegmentLights (nx=nz=1, y = HangHeight). Valor de mano: la esquina más lejana está
            // a raíz(2²+2²+3²) = raíz(17) m; ×1,25 + 0,15 m de pared.
            float actual = BoundedRange(2f, 2f, 4f, 4f, 3f, 11f);
            float expected = Mathf.Sqrt(17f) * 1.25f + 0.15f;
            Assert.AreEqual(expected, actual, 0.001f);
            Assert.Less(actual, 11f, "una sala de 4 m no puede seguir alcanzando lo mismo que una nave de 25");
        }

        [Test]
        public void UnaNaveGrandeConservaElAlcanceDeSiempre()
        {
            // Lámpara en el centro de una nave de 25×25 (MAX_SEGMENT_M): la esquina más lejana está
            // muy por encima de lo que 11 m alcanzan, así que Mathf.Min debe devolver el techo tal cual.
            float actual = BoundedRange(12.5f, 12.5f, 25f, 25f, 3f, 11f);
            Assert.AreEqual(11f, actual, 0.0001f,
                "una nave de 25 m no debe perder alcance: R3 solo recorta hacia abajo");
        }

        [Test]
        public void UnaEsquinaEstrechaSigueAcotada()
        {
            // La lámpara no siempre cae en el centro (jitter + Clamp a marginX/marginZ): probar desde
            // una esquina de la celda confirma que se usa la distancia a la esquina MÁS LEJANA, no la
            // más cercana ni el centro del tramo.
            float actual = BoundedRange(0.5f, 0.5f, 4f, 4f, 3f, 11f);
            float expected = Mathf.Sqrt(3.5f * 3.5f + 3.5f * 3.5f + 3f * 3f) * 1.25f + 0.15f;
            Assert.AreEqual(expected, actual, 0.001f);
        }

        [Test]
        public void NingunaSalaSuperaElAlcanceValidado()
        {
            // Barrido de tamaños de tramo: el resultado nunca debe superar el máximo pase lo que pase
            // con la posición local dentro de la celda.
            for (int sizeCm = 200; sizeCm <= 2500; sizeCm += 137)
            {
                float size = sizeCm * 0.01f;
                float actual = BoundedRange(size * 0.5f, size * 0.5f, size, size, 3f, 11f);
                Assert.LessOrEqual(actual, 11f, $"tramo de {size} m superó el techo validado");
            }
        }

        [Test]
        public void NearAnyLitFixture_ListaNula_NoPenaliza()
        {
            // Sin datos (un llamador que no calculó posiciones), no se apaga nada a ciegas.
            Assert.IsTrue(NearAnyLitFixture(5f, 5f, null, 4.5f * 4.5f));
        }

        [Test]
        public void NearAnyLitFixture_ListaVacia_ApagaElPanel()
        {
            // Lista VACÍA (blackout, o un tramo sin ninguna Light real) sí apaga: es la diferencia
            // deliberada entre "no lo sé" (null) y "lo sé, y no hay ninguna" (vacía).
            Assert.IsFalse(NearAnyLitFixture(5f, 5f, new List<Vector2>(), 4.5f * 4.5f));
        }

        [Test]
        public void NearAnyLitFixture_CercaDeUnaLuz_Prende()
        {
            var lit = new List<Vector2> { new Vector2(10f, 10f) };
            Assert.IsTrue(NearAnyLitFixture(11f, 11f, lit, 4.5f * 4.5f), "a 1,4 m de la luz debería contar como cerca");
        }

        [Test]
        public void NearAnyLitFixture_LejosDeTodas_SeApaga()
        {
            var lit = new List<Vector2> { new Vector2(10f, 10f), new Vector2(-10f, -10f) };
            Assert.IsFalse(NearAnyLitFixture(30f, 30f, lit, 4.5f * 4.5f), "a 28 m de la más cercana no debería contar como cerca");
        }

        // --- Integración: AssembleSegment realmente aplica BoundedRange a las Light que crea ---

        private static Wg3Segment MakeSegment(int sizeCm, int heightCm) => new Wg3Segment
        {
            xCm = 0,
            zCm = 0,
            sizeXCm = sizeCm,
            sizeZCm = sizeCm,
            floorYCm = 0,
            heightCm = heightCm,
            // Ni StyleOffice (0, puede quedar a oscuras por IsDarkOffice) ni StyleCorridor (2, añade
            // emergencia): un estilo neutro para que el único motivo de una Light ausente sea el
            // 12 % de tubos muertos de la cadencia, no una regla de estilo.
            style = 1,
        };

        [Test]
        public void SegmentoPequeno_TodaLuzSaleCapadaPorDebajoDelMaximo()
        {
            var parent = new GameObject("chunk");
            GameObject go = null;
            try
            {
                var created = new List<Mesh>();
                go = Wg3SceneAssembler.AssembleSegment(MakeSegment(400, 332), parent.transform,
                    null, created, "seg", addLight: true, carves: null, lampMaterial: null,
                    hum: null, worldSeed: 0, cadence: null);
                Assert.IsNotNull(go);

                Light[] lights = go.GetComponentsInChildren<Light>(true);
                bool anyChecked = false;
                foreach (Light l in lights)
                {
                    bool isFill = l.gameObject.name == "light_fill";
                    float ceiling = isFill ? 18f : 11f;
                    Assert.Less(l.range, ceiling - 0.01f,
                        $"{(isFill ? "el relleno" : "el plafón")} de una sala de 4 m no debería conservar el alcance de una nave");
                    anyChecked = true;
                }
                // No es un requisito duro que haya alguna Light viva (la cadencia puede apagar el
                // único tubo del tramo), pero con un solo fixture y 12 % de mortalidad lo normal es
                // que sí la haya; si esto empieza a fallar con `anyChecked == false` de forma
                // reproducible, hay que sembrar el test con otra semilla en vez de borrar la comprobación.
                Assert.IsTrue(anyChecked, "el tramo de prueba no generó ninguna Light que comprobar");

                foreach (Mesh m in created) Object.DestroyImmediate(m);
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void SegmentoDeNave_LaLuzQueSobreviveConservaElAlcanceDeSiempre()
        {
            var parent = new GameObject("chunk");
            GameObject go = null;
            try
            {
                var created = new List<Mesh>();
                go = Wg3SceneAssembler.AssembleSegment(MakeSegment(2500, 332), parent.transform,
                    null, created, "seg", addLight: true, carves: null, lampMaterial: null,
                    hum: null, worldSeed: 0, cadence: null);
                Assert.IsNotNull(go);

                Light[] lights = go.GetComponentsInChildren<Light>(true);
                int mainCount = 0;
                foreach (Light l in lights)
                {
                    bool isFill = l.gameObject.name == "light_fill";
                    float expected = isFill ? 18f : 11f;
                    Assert.AreEqual(expected, l.range, 0.01f,
                        $"una nave de 25 m debería conservar el alcance de siempre, no {l.range}");
                    if (!isFill) mainCount++;
                }
                // Con 3×3 fixtures y 12 % de mortalidad, que los NUEVE salgan muertos es
                // astronómicamente improbable (0,12^9); si esto falla de verdad, no es mala suerte.
                Assert.Greater(mainCount, 0, "la nave de prueba no dejó ninguna Light con la que comprobar el alcance");

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
