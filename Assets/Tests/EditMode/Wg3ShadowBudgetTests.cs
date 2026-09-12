using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// R4 (2026-09-12) — el tope global de luces con sombra. <see cref="Wg3ShadowBudget.Rank"/> es
    /// la única parte pura (no toca un <c>AudioListener</c> ni depende de MonoBehaviour vivo), y por
    /// eso la única que este test ejercita directamente: fija que ordena por distancia y que las
    /// primeras <c>MaxShadowCasters</c> tras ordenar son de verdad las más cercanas al oyente.
    /// </summary>
    public sealed class Wg3ShadowBudgetTests
    {
        private static GameObject NewLight(string name)
        {
            var go = new GameObject(name);
            go.AddComponent<Light>();
            return go;
        }

        [Test]
        public void RankOrdenaPorDistanciaAscendente()
        {
            var lejos = NewLight("lejos");
            var cerca = NewLight("cerca");
            var media = NewLight("media");
            try
            {
                var ranked = new List<(float distSqr, Light light)>
                {
                    (100f, lejos.GetComponent<Light>()),
                    (1f, cerca.GetComponent<Light>()),
                    (25f, media.GetComponent<Light>()),
                };

                Wg3ShadowBudget.Rank(ranked);

                Assert.AreEqual("cerca", ranked[0].light.name);
                Assert.AreEqual("media", ranked[1].light.name);
                Assert.AreEqual("lejos", ranked[2].light.name);
            }
            finally
            {
                Object.DestroyImmediate(lejos);
                Object.DestroyImmediate(cerca);
                Object.DestroyImmediate(media);
            }
        }

        [Test]
        public void SoloLasMasCercanasQuedanDentroDelPresupuesto()
        {
            // El corte de a quién le toca sombra vive fuera de Rank (en Rerank, dentro de la
            // MonoBehaviour), pero el contrato que hace falta para que ese corte sea correcto es
            // este: tras ordenar, las MaxShadowCasters primeras posiciones son las más cercanas.
            var lights = new List<GameObject>();
            try
            {
                var ranked = new List<(float distSqr, Light light)>();
                for (int i = 0; i < 8; i++)
                {
                    var go = NewLight($"l{i}");
                    lights.Add(go);
                    // Distancias en orden inverso al índice de creación, a propósito: si Rank
                    // hiciera trampa con el orden de inserción en vez de mirar distSqr, este test
                    // lo cazaría.
                    ranked.Add(((float)(8 - i), go.GetComponent<Light>()));
                }

                Wg3ShadowBudget.Rank(ranked);

                Assert.AreEqual("l7", ranked[0].light.name, "la más cercana (distSqr=1, índice 7) debe ir primero");

                // Ninguna de las MaxShadowCasters primeras puede tener una distancia mayor que
                // ninguna de las que quedan fuera del presupuesto.
                float peorDentro = ranked[Wg3ShadowBudget.MaxShadowCasters - 1].distSqr;
                for (int i = Wg3ShadowBudget.MaxShadowCasters; i < ranked.Count; i++)
                    Assert.GreaterOrEqual(ranked[i].distSqr, peorDentro,
                        "una luz fuera del presupuesto no puede estar más cerca que una dentro");
            }
            finally
            {
                foreach (GameObject go in lights) Object.DestroyImmediate(go);
            }
        }
    }
}
