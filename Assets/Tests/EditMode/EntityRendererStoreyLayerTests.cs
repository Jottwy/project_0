using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// R7 (2026-09-12) — el Lurker/Crawler/Shadow se veía NEGRO en cuanto subía a cualquier planta
    /// que no fuera el sótano más profundo: <c>EntityRenderer.SpawnVisual</c> monta una cápsula
    /// primitiva que nunca pasaba por <see cref="Wg3DynamicLitLayers"/>, así que se quedaba con la
    /// máscara de render por defecto (bit 0 = B3 con ADR-130) y el ambiente negro desde el 07-09
    /// hacía el resto. Este test fija que toda la jerarquía que <c>SpawnVisual</c> monta sale con
    /// la máscara de TODAS las plantas, igual que ya reciben el proxy de un jugador remoto o un
    /// vigilante (<c>RemotePlayerManager.cs</c>).
    /// </summary>
    public sealed class EntityRendererStoreyLayerTests
    {
        private static GameObject SpawnVisualGo(string entityType)
        {
            var hostGo = new GameObject("host");
            try
            {
                var renderer = hostGo.AddComponent<EntityRenderer>();

                MethodInfo m = typeof(EntityRenderer).GetMethod("SpawnVisual",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(m, "EntityRenderer.SpawnVisual ha cambiado de firma o desaparecido");

                var ev = new EntityViewMsg { id = 1, entityType = entityType, position = Vector3.zero, rotation = 0f };
                object visual = m.Invoke(renderer, new object[] { ev });
                Assert.IsNotNull(visual);

                var goField = visual.GetType().GetField("go", BindingFlags.Public | BindingFlags.Instance);
                var go = (GameObject)goField.GetValue(visual);
                Assert.IsNotNull(go, "SpawnVisual no montó ningún GameObject");
                return go;
            }
            finally
            {
                // El host sólo existía para poder llamar al método de instancia por reflexión: la
                // jerarquía que de verdad importa (la que monta SpawnVisual) es un GameObject aparte.
                Object.DestroyImmediate(hostGo);
            }
        }

        [TestCase("lurker")]
        [TestCase("crawler")]
        [TestCase("shadow")]
        public void TodaLaJerarquiaSaleConLaMascaraDeTodasLasPlantas(string entityType)
        {
            GameObject go = SpawnVisualGo(entityType);
            try
            {
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
                Assert.Greater(renderers.Length, 0, "la entidad de prueba no montó ningún Renderer");
                foreach (Renderer r in renderers)
                    Assert.AreEqual(Wg3DynamicLitLayers.AllStoreys, r.renderingLayerMask,
                        $"{r.name} se quedó con la máscara por defecto: se vería negro fuera de B3");
            }
            finally
            {
                Object.DestroyImmediate(go.transform.root.gameObject);
            }
        }

        [Test]
        public void SigueRescaneandoSiCuelgaAlgoNuevoDespues()
        {
            GameObject go = SpawnVisualGo("lurker");
            try
            {
                // Simula un hijo añadido DESPUÉS del montaje inicial (p. ej. un efecto visual que
                // se activa más tarde): sin el rescan periódico de Wg3DynamicLitLayers, éste se
                // quedaría con la máscara por defecto para siempre.
                var lateChild = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                lateChild.name = "late";
                lateChild.transform.SetParent(go.transform, false);
                Assert.AreNotEqual(Wg3DynamicLitLayers.AllStoreys, lateChild.GetComponent<Renderer>().renderingLayerMask,
                    "sanity check: el hijo recién añadido todavía no debería tener la máscara aplicada");

                Wg3DynamicLitLayers.Apply(go); // lo que el Update periódico haría por su cuenta

                Assert.AreEqual(Wg3DynamicLitLayers.AllStoreys, lateChild.GetComponent<Renderer>().renderingLayerMask);
            }
            finally
            {
                Object.DestroyImmediate(go.transform.root.gameObject);
            }
        }
    }
}
