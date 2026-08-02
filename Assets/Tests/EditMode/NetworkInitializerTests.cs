using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace BackroomsSurvival.Tests
{
    [TestFixture]
    public class NetworkInitializerTests
    {
        private GameObject _go;
        private NetworkInitializer _init;

        /// <summary>
        /// Los tres tests que fuerzan la rama "exe no resuelto" atraviesan un fail-loud
        /// DELIBERADO: <c>LaunchBackendProcess</c> emite dos <c>Debug.LogError</c> antes de
        /// devolver false (NetworkInitializer.cs:197-201, comentario "Fail loudly: never
        /// silently continue with an unverifiable backend"). El Test Framework tumba cualquier
        /// test con un log de Error no declarado, así que estos tres fallaban DENTRO de la
        /// llamada, no en sus asserts — que de hecho se cumplen todos.
        ///
        /// Declararlos aquí, en vez de silenciarlos con ignoreFailingMessages, convierte el
        /// fail-loud en contrato verificado: si alguien baja esos LogError a LogWarning, estos
        /// tests se ponen rojos. Hoy ningún test del repo usa LogAssert (grep = 0 resultados),
        /// así que este modo de fallo estaba sin cubrir en toda la suite.
        /// </summary>
        private static void ExpectBackendNotFoundErrors()
        {
            LogAssert.Expect(LogType.Error,
                "[NetworkInitializer] MPTRACE step=RUBIK event=unity_backend_exe_path path=UNRESOLVED status=fail_loud");
            LogAssert.Expect(LogType.Error,
                "[NetworkInitializer] Backend executable not found. Build or copy backrooms_server.exe.");
        }

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestNetInit");
            _init = _go.AddComponent<NetworkInitializer>();
        }

        [TearDown]
        public void TearDown()
        {
            _init.Shutdown();
            Object.DestroyImmediate(_go);
        }

        [Test]
        public void InitialRoleIsNone()
        {
            Assert.AreEqual(NetworkInitializer.Role.None, _init.CurrentRole);
            Assert.IsFalse(_init.IsBackendReady);
        }

        [Test]
        public void StartAsHostSetsRole()
        {
            _init.backendPath = "nonexistent/path.exe";
            _init.fallbackBackendPath = "also/nonexistent.exe";
            _init.executableName = "nonexistent_server_xyz.exe";
            ExpectBackendNotFoundErrors();
            _init.StartAsHost("TestPlayer");

            Assert.AreEqual(NetworkInitializer.Role.Host, _init.CurrentRole);
            Assert.IsTrue(_init.StatusMessage.Contains("Error"));
        }

        [Test]
        public void StartAsJoinerSetsRole()
        {
            _init.backendPath = "nonexistent/path.exe";
            _init.fallbackBackendPath = "also/nonexistent.exe";
            _init.executableName = "nonexistent_server_xyz.exe";
            ExpectBackendNotFoundErrors();
            _init.StartAsJoiner("192.168.1.1", 7778, "Joiner");

            Assert.AreEqual(NetworkInitializer.Role.Joiner, _init.CurrentRole);
            Assert.IsTrue(_init.StatusMessage.Contains("Error"));
        }

        [Test]
        public void ShutdownResetsState()
        {
            _init.backendPath = "nonexistent/path.exe";
            _init.fallbackBackendPath = "also/nonexistent.exe";
            _init.executableName = "nonexistent_server_xyz.exe";
            ExpectBackendNotFoundErrors();
            _init.StartAsHost("Test");
            _init.Shutdown();

            Assert.AreEqual(NetworkInitializer.Role.None, _init.CurrentRole);
            Assert.IsFalse(_init.IsBackendReady);
        }

        /// <summary>
        /// Regresión: `StartAsHost(playerName, hostListenPort)` enlazaba con la sobrecarga
        /// `(string, int worldSeed = 42)` — C# prefiere la candidata que no rellena opcionales —
        /// así que el puerto tecleado viajaba como SEED y nunca llegaba a NET_PORT. Doble daño:
        /// el puerto se perdía y el save pasaba a llamarse `world_{puerto}.json`, de modo que
        /// cambiar de puerto hacía "desaparecer" la partida. El punto de entrada dedicado
        /// `StartAsHostOnPort` lo vuelve imposible de reintroducir.
        /// </summary>
        /// <remarks>
        /// El puerto de prueba NO puede ser 7778: ese es el default del inspector
        /// (<c>netPort</c>), así que con el bug vivo el assert habría pasado igual — la rama
        /// mala cae precisamente a ese default. Y se compara con <c>&gt;=</c> porque
        /// <c>SelectLaunchConfig</c> sube al siguiente UDP libre si el pedido está ocupado;
        /// un <c>AreEqual</c> exacto sería un test intermitente.
        /// </remarks>
        [Test]
        public void StartAsHostOnPortUsesPortAsPortAndKeepsDefaultSeed()
        {
            const int RequestedPort = 45123;
            _init.backendPath = "nonexistent/path.exe";
            _init.fallbackBackendPath = "also/nonexistent.exe";
            _init.executableName = "nonexistent_server_xyz.exe";
            ExpectBackendNotFoundErrors();
            _init.StartAsHostOnPort("TestPlayer", RequestedPort);

            Assert.AreEqual(42, _init.LastSelectedWorldSeed,
                "el puerto de escucha NO debe acabar en WORLD_SEED");
            Assert.AreNotEqual(_init.netPort, _init.LastSelectedNetPort,
                "el puerto tecleado se estaba descartando y se usaba el default del inspector");
            Assert.GreaterOrEqual(_init.LastSelectedNetPort, RequestedPort,
                "el puerto tecleado debe llegar a NET_PORT (o al siguiente libre por encima)");
        }

        /// <summary>
        /// Contrapartida del test de arriba: el camino de una sola arg sigue significando
        /// "seed por defecto, puerto de inspector" y no se rompió al separar los dos puntos
        /// de entrada.
        /// </summary>
        [Test]
        public void StartAsHostWithoutPortKeepsDefaultSeed()
        {
            _init.backendPath = "nonexistent/path.exe";
            _init.fallbackBackendPath = "also/nonexistent.exe";
            _init.executableName = "nonexistent_server_xyz.exe";
            ExpectBackendNotFoundErrors();
            _init.StartAsHost("TestPlayer");

            Assert.AreEqual(42, _init.LastSelectedWorldSeed);
        }

        /// <summary>Una seed explícita sí debe llegar tal cual (el parámetro no es decorativo).</summary>
        [Test]
        public void StartAsHostOnPortForwardsAnExplicitSeed()
        {
            _init.backendPath = "nonexistent/path.exe";
            _init.fallbackBackendPath = "also/nonexistent.exe";
            _init.executableName = "nonexistent_server_xyz.exe";
            ExpectBackendNotFoundErrors();
            _init.StartAsHostOnPort("TestPlayer", 45123, 1234);

            Assert.AreEqual(1234, _init.LastSelectedWorldSeed);
            Assert.GreaterOrEqual(_init.LastSelectedNetPort, 45123);
        }

        [Test]
        public void HostLaunchesBackendWithValidPath()
        {
            // Uses the real backend path — verifies process launch works
            // when the exe exists. Skip if not built.
            string relPath = "backend/target/release/backrooms_server.exe";
            string fullPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", relPath));

            if (!System.IO.File.Exists(fullPath))
            {
                relPath = "backend/target/debug/backrooms_server.exe";
                fullPath = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..", relPath));
            }

            if (!System.IO.File.Exists(fullPath))
            {
                Assert.Ignore("Backend exe not built; skipping launch test");
                return;
            }

            _init.startupTimeout = 1f;
            _init.StartAsHost("TestPlayer");

            Assert.AreEqual(NetworkInitializer.Role.Host, _init.CurrentRole);
            Assert.IsFalse(_init.StatusMessage.Contains("Error"),
                $"Expected no error but got: {_init.StatusMessage}");

            _init.Shutdown();
        }
    }
}
