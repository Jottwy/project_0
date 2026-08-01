using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// C8 — retirada del busy-wait del hilo de red. <see cref="IpcStreamReader.ReadExactly"/>
    /// sustituyó el bucle `DataAvailable` + <c>Thread.Sleep(1)</c> (que quemaba ~100 despertares
    /// por hueco entre snapshots a 10 Hz) por lecturas bloqueantes con timeout de socket.
    ///
    /// El modo de fallo de este cambio NO es un número de profiler: es un CUELGUE al salir. El
    /// spin de 1 ms re-chequeaba la bandera de parada constantemente; una lectura bloqueante mal
    /// hecha cambia CPU quemada por un hilo que no muere y un Thread.Join que expira. Por eso
    /// estos tests usan sockets de loopback REALES y un hilo real, y miden tiempo de salida.
    ///
    /// Cubre los tres escenarios de cierre exigidos + dos no-regresiones:
    ///   (a) salir con un snapshot A MEDIAS (frame parcial en el buffer),
    ///   (b) salir con el backend CAÍDO,
    ///   (c) salida normal (conexión ociosa),
    ///   (+) un frame partido entre slices sigue ensamblándose,
    ///   (+) una conexión ociosa sigue expirando al timeout COMPLETO de frame, no al slice.
    /// </summary>
    [TestFixture]
    public class IpcStreamReaderShutdownTests
    {
        private TcpListener _listener;
        private TcpClient _client;
        private TcpClient _server;

        /// <summary>Par conectado sobre loopback en un puerto efímero (0 = lo elige el SO, así
        /// dos corridas simultáneas no chocan).</summary>
        private void Connect()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _client = new TcpClient();
            _client.Connect(IPAddress.Loopback, port);
            _server = _listener.AcceptTcpClient();
        }

        [TearDown]
        public void TearDown()
        {
            try { _client?.Close(); } catch { }
            try { _server?.Close(); } catch { }
            try { _listener?.Stop(); } catch { }
            _client = null; _server = null; _listener = null;
        }

        /// <summary>(c) Salida normal: conexión ociosa, la bandera de parada pasa a false. El
        /// lector debe enterarse en ~1 slice, NO quedarse los 5 s del timeout de frame.</summary>
        [Test]
        public void NormalQuitTerminatesTheReaderWithinOneSlice()
        {
            Connect();
            bool running = true;
            bool returned = false;

            var t = new Thread(() =>
            {
                var buf = new byte[4];
                IpcStreamReader.ReadExactly(_client.GetStream(), buf, 4, () => Volatile.Read(ref running));
                returned = true;
            }) { IsBackground = true };
            t.Start();

            Thread.Sleep(400); // dejar que entre en lectura bloqueante
            var sw = Stopwatch.StartNew();
            Volatile.Write(ref running, false);
            bool joined = t.Join(1500);
            sw.Stop();

            Assert.IsTrue(joined, "el hilo lector debe terminar, no quedar bloqueado");
            Assert.IsTrue(returned, "ReadExactly debe retornar, no colgarse");
            Assert.Less(sw.ElapsedMilliseconds, 1000,
                $"debe salir en ~1 slice ({IpcStreamReader.ReadSliceMs} ms), no en el timeout de frame " +
                $"({IpcStreamReader.ReadTimeoutMs} ms). Real: {sw.ElapsedMilliseconds} ms");
        }

        /// <summary>(b) Backend caído: el peer cierra el socket mientras el lector espera. Debe
        /// reportar pérdida de conexión enseguida, sin agotar el timeout de frame.</summary>
        [Test]
        public void BackendDownIsDetectedPromptlyAndReportsConnectionLoss()
        {
            Connect();
            bool running = true;
            bool result = true;

            var t = new Thread(() =>
            {
                var buf = new byte[4];
                result = IpcStreamReader.ReadExactly(_client.GetStream(), buf, 4, () => Volatile.Read(ref running));
            }) { IsBackground = true };
            t.Start();

            Thread.Sleep(300);
            var sw = Stopwatch.StartNew();
            try { _server.Close(); } catch { } // el backend muere
            bool joined = t.Join(2000);
            sw.Stop();

            Assert.IsTrue(joined, "el hilo lector debe terminar tras caerse el peer");
            Assert.IsFalse(result, "una conexión caída debe reportarse como fallo (false)");
            Assert.Less(sw.ElapsedMilliseconds, 2000,
                $"debe detectarse enseguida, no tras los {IpcStreamReader.ReadTimeoutMs} ms de timeout. " +
                $"Real: {sw.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// (a) Salida con snapshot a medias: el peer mandó PARTE del frame y el lector está
        /// bloqueado esperando el resto cuando empieza el teardown. Es el caso donde un
        /// `while(true) Read()` ingenuo se quedaría colgado para siempre con medio frame.
        /// </summary>
        [Test]
        public void QuitWithAPartialFrameInFlightDoesNotHang()
        {
            Connect();
            bool running = true;
            bool returned = false;
            bool result = true;

            // El peer manda 2 de los 8 bytes pedidos y se calla.
            _server.GetStream().Write(new byte[] { 0xAA, 0xBB }, 0, 2);
            _server.GetStream().Flush();

            var t = new Thread(() =>
            {
                var buf = new byte[8];
                result = IpcStreamReader.ReadExactly(_client.GetStream(), buf, 8, () => Volatile.Read(ref running));
                returned = true;
            }) { IsBackground = true };
            t.Start();

            Thread.Sleep(500); // el lector tiene 2 bytes y espera los otros 6
            var sw = Stopwatch.StartNew();
            Volatile.Write(ref running, false);
            bool joined = t.Join(1500);
            sw.Stop();

            Assert.IsTrue(joined, "el hilo debe terminar aunque tenga un frame a medias");
            Assert.IsTrue(returned, "ReadExactly debe retornar, no colgarse con el frame parcial");
            Assert.IsFalse(result, "un frame incompleto debe reportarse como fallo");
            Assert.Less(sw.ElapsedMilliseconds, 1000, $"Real: {sw.ElapsedMilliseconds} ms");
        }

        /// <summary>No-regresión: un frame completo sigue leyéndose, incluso llegando a trozos
        /// repartidos entre varios slices de lectura.</summary>
        [Test]
        public void FrameSplitAcrossReadSlicesIsStillAssembled()
        {
            Connect();
            bool running = true;
            bool result = false;
            var buf = new byte[8];

            var t = new Thread(() =>
            {
                result = IpcStreamReader.ReadExactly(_client.GetStream(), buf, 8, () => Volatile.Read(ref running));
            }) { IsBackground = true };
            t.Start();

            // Partir el payload cruzando una frontera de slice: 3 bytes, esperar, luego 5.
            _server.GetStream().Write(new byte[] { 1, 2, 3 }, 0, 3);
            _server.GetStream().Flush();
            Thread.Sleep(IpcStreamReader.ReadSliceMs + 150);
            _server.GetStream().Write(new byte[] { 4, 5, 6, 7, 8 }, 0, 5);
            _server.GetStream().Flush();

            Assert.IsTrue(t.Join(3000), "el hilo debe terminar tras completarse el frame");
            Assert.IsTrue(result, "un frame partido entre slices debe ensamblarse igual");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, buf,
                "los bytes deben llegar intactos y en orden pese al corte");
        }

        /// <summary>
        /// No-regresión del contrato de timeout: una conexión ociosa sigue expirando al timeout
        /// COMPLETO de frame, no al slice. Quitar el spin no debe acortar la detección de
        /// desconexión — el slice es un detalle de implementación, no una política nueva.
        /// </summary>
        [Test]
        public void IdleConnectionStillHonoursTheFullFrameTimeoutNotTheSlice()
        {
            Connect();
            bool result = true;

            var t = new Thread(() =>
            {
                var buf = new byte[4];
                result = IpcStreamReader.ReadExactly(_client.GetStream(), buf, 4, () => true);
            }) { IsBackground = true };
            var sw = Stopwatch.StartNew();
            t.Start();

            bool joined = t.Join(IpcStreamReader.ReadTimeoutMs + 3000);
            sw.Stop();

            Assert.IsTrue(joined, "la lectura ociosa debe rendirse sola");
            Assert.IsFalse(result, "expirar debe reportarse como fallo");
            Assert.GreaterOrEqual(sw.ElapsedMilliseconds, IpcStreamReader.ReadTimeoutMs - IpcStreamReader.ReadSliceMs,
                $"debe esperar el timeout completo ({IpcStreamReader.ReadTimeoutMs} ms), no un slice " +
                $"({IpcStreamReader.ReadSliceMs} ms). Real: {sw.ElapsedMilliseconds} ms");
        }
    }
}
