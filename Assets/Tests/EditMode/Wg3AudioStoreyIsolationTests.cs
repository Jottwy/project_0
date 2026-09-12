using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using BackroomsSurvival.WorldGen3;
using BackroomsSurvival.Gameplay.Audio;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// R6 (2026-09-12) — el zumbido de fluorescente y el ambiente de oficina aislaban por planta
    /// con reglas que no conocían la planta REAL de WG3 (3,32 m + sótanos): el zumbido comparaba un
    /// valor por LOTE calculado con la constante de WG2 (4 m) contra chunks que en WG3 mezclan hasta
    /// ocho plantas en un solo lote, y el ambiente cortaba por una distancia fija (2,6 m) que ni
    /// separa ni respeta el borde real de una planta de 3,32. Estos tests fijan que los dos usan
    /// ahora el mismo índice que ya reparte la luz — <see cref="Wg3StoreyLayers.RawStoreyOf"/> — y
    /// que <c>Wg3SceneAssembler</c> de verdad rellena una planta por lámpara, no un valor a medias.
    /// </summary>
    public sealed class Wg3AudioStoreyIsolationTests
    {
        // --- Wg3StoreyLayers.RawStoreyOf: la base compartida por luz Y sonido ---

        [Test]
        public void RawStoreyOf_LaCalleEsLaPlanta0()
        {
            Assert.AreEqual(0, Wg3StoreyLayers.RawStoreyOf(0f));
            Assert.AreEqual(0, Wg3StoreyLayers.RawStoreyOf(3.0f)); // sala normal, no llega al techo
        }

        [Test]
        public void RawStoreyOf_ElSueloDeLaPlantaDeArribaYaEsOtraPlanta()
        {
            // El caso exacto del bug: un punto a 3,5 m (por encima de una planta de 3,32) ya es
            // planta 1. Con la constante vieja de WG2 (4 m) habría seguido siendo planta 0.
            Assert.AreEqual(1, Wg3StoreyLayers.RawStoreyOf(3.5f));
            Assert.AreNotEqual(Wg3StoreyLayers.RawStoreyOf(3.0f), Wg3StoreyLayers.RawStoreyOf(3.5f),
                "3,0 m y 3,5 m tienen que caer en plantas distintas: el forjado de WG3 está en 3,32");
        }

        [Test]
        public void RawStoreyOf_UnMultiploExactoDeLaAlturaDePlantaEsLaSiguiente()
        {
            Assert.AreEqual(2, Wg3StoreyLayers.RawStoreyOf(6.64f)); // 2 × 3,32
        }

        [Test]
        public void RawStoreyOf_SotanosDanNumerosNegativos()
        {
            Assert.AreEqual(-1, Wg3StoreyLayers.RawStoreyOf(-1f));
            Assert.AreEqual(-2, Wg3StoreyLayers.RawStoreyOf(-6.64f));
        }

        // --- OfficeAmbienceDirector.SamePlanta: reemplaza el corte por distancia ---

        private static bool SamePlanta(float sourceY, float earY)
        {
            MethodInfo m = typeof(OfficeAmbienceDirector).GetMethod("SamePlanta",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(m, "OfficeAmbienceDirector.SamePlanta ha cambiado de firma o desaparecido");
            return (bool)m.Invoke(null, new object[] { sourceY, earY });
        }

        [Test]
        public void SamePlanta_FuenteCercaDelTechoDeTuPropiaPlanta_CuentaComoTuPlanta()
        {
            // El caso que el |dy| ≤ 2,6 m viejo rompía: oyente en el suelo (y=0,2), fuente a 3,1 m
            // (dy = 2,9 m > 2,6, se habría cortado) pero AMBOS en la planta 0 (techo en 3,32).
            Assert.IsTrue(SamePlanta(3.1f, 0.2f),
                "una fuente a 2,9 m de dy pero dentro de la MISMA planta no debe aislarse por distancia");
        }

        [Test]
        public void SamePlanta_FuenteJustoEnElSueloDeArriba_YaNoCuenta()
        {
            // El otro sentido del mismo bug: oyente cerca del techo de su planta (y=3,0), fuente
            // recién entrada en la planta de arriba (y=3,4). dy = 0,4 m, MUY por debajo de 2,6 —
            // el corte viejo la habría dejado sonar a través de la losa.
            Assert.IsFalse(SamePlanta(3.4f, 3.0f),
                "una fuente a solo 0,4 m de dy pero en la planta de ARRIBA no debe sonar como si fuera la tuya");
        }

        [Test]
        public void SamePlanta_MismaPlantaEnCualquierPunto()
        {
            for (float y = 0f; y < 3.32f; y += 0.5f)
                Assert.IsTrue(SamePlanta(y, 1.6f), $"y={y} debería seguir siendo planta 0 igual que el oyente a 1,6");
        }

        // --- Wg3SceneAssembler: que de verdad rellene una planta por lámpara, no a medias ---

        private static Wg3Segment MakeSegment(int xCm, int zCm, int sizeCm, int heightCm) => new Wg3Segment
        {
            xCm = xCm,
            zCm = zCm,
            sizeXCm = sizeCm,
            sizeZCm = sizeCm,
            floorYCm = 0,
            heightCm = heightCm,
            style = 1, // ni oficina (blackout) ni pasillo (emergencia): ver Wg3SegmentLightRangeTests
        };

        [Test]
        public void AssembleSegment_CadaLamparaDelLoteTraeSuPlanta()
        {
            var parent = new GameObject("chunk");
            GameObject go = null;
            try
            {
                var created = new List<Mesh>();
                var hum = new Wg3HumBatch();
                var segment = MakeSegment(0, 0, 2500, 332); // nave grande: varias lámparas seguro
                go = Wg3SceneAssembler.AssembleSegment(segment, parent.transform,
                    null, created, "seg", addLight: true, carves: null, lampMaterial: null,
                    hum: hum, worldSeed: 0, cadence: null);
                Assert.IsNotNull(go);

                // El lote tiene que traer EXACTAMENTE una planta por posición: es la guarda de
                // FluorescentHumDirector.RegisterChunkLamps (storeys.Count == positions.Count) la
                // que decide si el lote usa el camino nuevo por lámpara o cae al de WG2 por lote.
                Assert.AreEqual(hum.positions.Count, hum.storeys.Count,
                    "el lote tiene que traer una planta por CADA lámpara, o RegisterChunkLamps lo descarta entero");

                int expectedStorey = Wg3StoreyLayers.RawStoreyOf(segment.FloorY);
                foreach (int s in hum.storeys)
                    Assert.AreEqual(expectedStorey, s, "la planta de la lámpara no coincide con la del tramo");

                foreach (Mesh m in created) Object.DestroyImmediate(m);
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void AssembleSegment_UnaPlantaAltaDaUnaPlantaDistintaDeLaCalle()
        {
            var parent = new GameObject("chunk");
            GameObject go = null;
            try
            {
                var created = new List<Mesh>();
                var hum = new Wg3HumBatch();
                var segment = new Wg3Segment
                {
                    xCm = 0, zCm = 0, sizeXCm = 2500, sizeZCm = 2500,
                    floorYCm = 664, // dos plantas de 332 cm por encima de la calle
                    heightCm = 332,
                    style = 1,
                };
                go = Wg3SceneAssembler.AssembleSegment(segment, parent.transform,
                    null, created, "seg", addLight: true, carves: null, lampMaterial: null,
                    hum: hum, worldSeed: 0, cadence: null);
                Assert.IsNotNull(go);

                if (hum.storeys.Count > 0)
                    Assert.AreEqual(2, hum.storeys[0], "una planta a 6,64 m tiene que salir como planta 2, no 0");

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
