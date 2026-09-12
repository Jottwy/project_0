using System.Collections.Generic;
using BackroomsSurvival.Net;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Un renderer que se mueve entre plantas (proxy, brazos, objeto en mano) tiene que recibir la
    /// lámpara de CUALQUIER planta. El contrato se mide contra <see cref="Wg3StoreyLayers"/>, no
    /// contra un número: con la máscara puesta, la capa que el mundo da a una lámpara en cada
    /// cota servida tiene que intersecar la del renderer.
    /// </summary>
    [TestFixture]
    public class Wg3DynamicLitLayersTests
    {
        private static readonly float[] ServedHeights = { -9.5f, -3.2f, 1.4f, 4.6f, 14.1f };

        private readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            foreach (GameObject go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        private GameObject Spawn(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        /// <summary>El fallo real: la máscara por defecto (bit 0) es B3 y ninguna otra planta.</summary>
        [Test]
        public void DefaultMaskIsLitByNoStreetLamp()
        {
            uint street = Wg3StoreyLayers.ForLight(1.4f);
            Assert.AreEqual(0u, 1u & street,
                "con tres sótanos el bit 0 es B3: una lámpara de la calle no toca un renderer por defecto");
        }

        [Test]
        public void ApplyOpensEveryServedStoreyAndKillsProbes()
        {
            GameObject root = Spawn("Proxy");
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.transform.SetParent(root.transform, false);
            var held = GameObject.CreatePrimitive(PrimitiveType.Cube);
            held.transform.SetParent(body.transform, false);
            held.SetActive(false); // un wieldable sin equipar: inactivo y también cuenta
            var renderers = new[] { body.GetComponent<Renderer>(), held.GetComponent<Renderer>() };
            foreach (Renderer r in renderers)
            {
                r.renderingLayerMask = 1;
                r.lightProbeUsage = LightProbeUsage.BlendProbes;
            }

            int written = Wg3DynamicLitLayers.Apply(root);

            Assert.AreEqual(2, written);
            foreach (Renderer r in renderers)
            {
                Assert.AreEqual(LightProbeUsage.Off, r.lightProbeUsage, r.name);
                foreach (float y in ServedHeights)
                    Assert.AreNotEqual(0u, r.renderingLayerMask & Wg3StoreyLayers.ForLight(y),
                        r.name + ": la lámpara de la cota " + y + " tiene que iluminarlo");
            }
        }

        [Test]
        public void ApplyIsIdempotentAndAttachAddsOneComponent()
        {
            GameObject root = Spawn("Proxy");
            var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            body.transform.SetParent(root.transform, false);

            Wg3DynamicLitLayers first = Wg3DynamicLitLayers.Attach(root);
            Wg3DynamicLitLayers second = Wg3DynamicLitLayers.Attach(root);

            Assert.AreSame(first, second);
            Assert.AreEqual(1, root.GetComponents<Wg3DynamicLitLayers>().Length);
            Assert.AreEqual(0, Wg3DynamicLitLayers.Apply(root), "la segunda pasada no toca nada");
        }

        /// <summary>El proxy que nace de un roster lleva la máscara desde el primer frame.</summary>
        [Test]
        public void RemoteProxyIsLitOnEveryStorey()
        {
            // El log nativo de TMP en modo editor, ver RemotePlayerManagerTests.
            LogAssert.ignoreFailingMessages = true;
            GameObject managerGo = Spawn("Manager");
            var manager = managerGo.AddComponent<RemotePlayerManager>();
            manager.missingRemoteGraceSeconds = 0f;

            manager.UpdateFromWorldState(new List<RemotePlayerMsg>
            {
                new RemotePlayerMsg { id = 7, name = "Bob", position = new Vector3(3f, 0f, 14f), animation = "idle" }
            });

            Transform root = manager.ActivePlayers[7].root;
            Assert.IsNotNull(root.GetComponent<Wg3DynamicLitLayers>(),
                "el proxy tiene que seguir cubierto cuando se le cuelguen cosas");
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            Assert.Greater(renderers.Length, 0);
            foreach (Renderer r in renderers)
                foreach (float y in ServedHeights)
                    Assert.AreNotEqual(0u, r.renderingLayerMask & Wg3StoreyLayers.ForLight(y),
                        r.name + " a la cota " + y);
        }
    }
}
