using System;
using System.Collections.Generic;
using System.Net;
using BackroomsSurvival.Connectivity;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El túnel de Steam de ADR-135, probado ENTERO sin cliente de Steam y sin abrir un socket:
    /// las dos costuras (<see cref="ISteamTunnelChannel"/> y <see cref="ISteamTunnelSocket"/>)
    /// existen exactamente para esto.
    ///
    /// Lo que se prueba aquí es lo que de verdad se puede equivocar: a quién se autoriza, que un
    /// datagrama sea un mensaje, que el payload no se toque, que cada peer tenga su propio puerto
    /// —de eso depende la deduplicación de `handlers.rs`— y que al cerrar no quede nada abierto.
    /// </summary>
    public sealed class SteamTunnelTests
    {
        private const string Secret = "00112233445566778899aabbccddeeff";
        private const string OtherSecret = "ffeeddccbbaa99887766554433221100";

        private static readonly IPEndPoint Backend = new IPEndPoint(IPAddress.Loopback, 7778);

        // ─── Dobles ───

        private sealed class FakeChannel : ISteamTunnelChannel
        {
            public readonly List<byte[]> Sent = new List<byte[]>();

            public FakeChannel(ulong steamId = 76561190000000001UL) => RemoteSteamId = steamId;

            public ulong RemoteSteamId { get; }

            public bool IsOpen { get; private set; } = true;

            public bool Closed { get; private set; }

            public bool Send(byte[] payload, int length)
            {
                if (!IsOpen) return false;

                var copy = new byte[length];
                Buffer.BlockCopy(payload, 0, copy, 0, length);
                Sent.Add(copy);
                return true;
            }

            public void Close()
            {
                Closed = true;
                IsOpen = false;
            }
        }

        private sealed class FakeSocket : ISteamTunnelSocket
        {
            private readonly Queue<(byte[] payload, IPEndPoint from)> _inbox =
                new Queue<(byte[], IPEndPoint)>();

            public FakeSocket(int port) => Port = port;

            public readonly List<(byte[] payload, IPEndPoint target)> Sent =
                new List<(byte[], IPEndPoint)>();

            public int Port { get; }

            public bool Disposed { get; private set; }

            /// Simula un datagrama que el backend local acaba de mandar al túnel.
            public void QueueFromBackend(byte[] payload, IPEndPoint from) => _inbox.Enqueue((payload, from));

            public void SendTo(byte[] payload, int length, IPEndPoint target)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(payload, 0, copy, 0, length);
                Sent.Add((copy, target));
            }

            public bool TryReceive(int timeoutMs, out byte[] payload, out int length, out IPEndPoint from)
            {
                payload = null;
                length = 0;
                from = null;
                if (_inbox.Count == 0) return false;

                (byte[] p, IPEndPoint f) = _inbox.Dequeue();
                payload = p;
                length = p.Length;
                from = f;
                return true;
            }

            public void Dispose() => Disposed = true;
        }

        private static SteamTunnelHost NewHost(List<FakeSocket> created, string secret = Secret)
        {
            int nextPort = 40000;
            return new SteamTunnelHost(secret, Backend, () =>
            {
                var socket = new FakeSocket(nextPort++);
                created.Add(socket);
                return socket;
            });
        }

        // ─── El sobre de autorización (D4') ───

        [Test]
        public void Un_secreto_nuevo_tiene_la_forma_que_el_proyecto_ya_usa()
        {
            // 32 hexadecimales, como el token del relay de ADR-117 D9. No es un formato nuevo.
            string secret = SteamTunnelAuth.NewSecret();

            Assert.AreEqual(SteamTunnelAuth.SecretLength, secret.Length);
            foreach (char c in secret)
            {
                Assert.IsTrue((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                    "hexadecimal en minúsculas, como el token del relay: " + secret);
            }

            Assert.AreNotEqual(secret, SteamTunnelAuth.NewSecret(), "dos sesiones no comparten secreto");
        }

        [Test]
        public void El_sobre_correcto_autoriza_y_el_de_otra_sesion_no()
        {
            byte[] frame = SteamTunnelAuth.Build(Secret);

            Assert.IsTrue(SteamTunnelAuth.Accepts(frame, frame.Length, Secret));
            Assert.IsFalse(SteamTunnelAuth.Accepts(frame, frame.Length, OtherSecret),
                "el secreto de otra partida no puede abrir ésta");
        }

        [Test]
        public void Un_primer_mensaje_que_no_es_el_sobre_no_autoriza()
        {
            // Un datagrama de juego, basura, o un sobre recortado: los tres acaban igual.
            Assert.IsFalse(SteamTunnelAuth.Accepts(new byte[] { 1, 2, 3 }, 3, Secret));
            Assert.IsFalse(SteamTunnelAuth.Accepts(null, 0, Secret));

            byte[] frame = SteamTunnelAuth.Build(Secret);
            Assert.IsFalse(SteamTunnelAuth.Accepts(frame, frame.Length - 1, Secret));

            byte[] mangled = SteamTunnelAuth.Build(Secret);
            mangled[0] ^= 0xFF;
            Assert.IsFalse(SteamTunnelAuth.Accepts(mangled, mangled.Length, Secret), "la marca no cuadra");
        }

        [Test]
        public void Un_secreto_mal_formado_no_construye_sobre_ni_autoriza_nada()
        {
            Assert.IsNull(SteamTunnelAuth.Build(null));
            Assert.IsNull(SteamTunnelAuth.Build("corto"));
            Assert.IsNull(SteamTunnelAuth.Build("zzzz2233445566778899aabbccddeeff"), "no es hexadecimal");

            byte[] frame = SteamTunnelAuth.Build(Secret);
            Assert.IsFalse(SteamTunnelAuth.Accepts(frame, frame.Length, null),
                "un host sin secreto no autoriza a nadie");
        }

        // ─── El host ───

        [Test]
        public void Hasta_que_no_se_autoriza_no_llega_ni_un_byte_al_backend()
        {
            var sockets = new List<FakeSocket>();
            SteamTunnelHost host = NewHost(sockets);
            var channel = new FakeChannel();

            // Un datagrama de juego como PRIMER mensaje: no hay autorización, así que se cierra.
            host.OnMessage(channel, new byte[] { 9, 9, 9 }, 3);

            Assert.AreEqual(0, host.AuthorizedCount);
            Assert.AreEqual(0, sockets.Count, "ni siquiera se abre un socket de loopback");
            Assert.IsTrue(channel.Closed);
            Assert.AreEqual(1, host.RejectedCount);
            Assert.AreEqual(channel.RemoteSteamId, host.LastRejectedSteamId,
                "el SteamId rechazado es el dato con el que se diagnostica");
        }

        [Test]
        public void Con_el_secreto_correcto_se_autoriza_y_a_partir_de_ahi_todo_pasa_tal_cual()
        {
            var sockets = new List<FakeSocket>();
            SteamTunnelHost host = NewHost(sockets);
            var channel = new FakeChannel();

            byte[] auth = SteamTunnelAuth.Build(Secret);
            host.OnMessage(channel, auth, auth.Length);

            Assert.AreEqual(1, host.AuthorizedCount);
            Assert.AreEqual(0, host.RejectedCount);
            Assert.IsFalse(channel.Closed);
            Assert.AreEqual(1, sockets.Count);
            Assert.AreEqual(0, sockets[0].Sent.Count, "el sobre NO se le reenvía al backend");

            // Y ahora un datagrama de juego: va entero y sin tocar.
            var datagram = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            host.OnMessage(channel, datagram, datagram.Length);

            Assert.AreEqual(1, sockets[0].Sent.Count, "un datagrama recibido = un datagrama entregado");
            CollectionAssert.AreEqual(datagram, sockets[0].Sent[0].payload);
            Assert.AreEqual(Backend, sockets[0].Sent[0].target);
        }

        [Test]
        public void Cada_joiner_llega_al_backend_desde_su_propio_puerto()
        {
            // Es de esto de lo que depende la deduplicación por endpoint de `handlers.rs`: con un
            // solo puerto compartido, el segundo joiner se tomaría por una reconexión del primero.
            var sockets = new List<FakeSocket>();
            SteamTunnelHost host = NewHost(sockets);

            var uno = new FakeChannel(76561190000000001UL);
            var dos = new FakeChannel(76561190000000002UL);
            byte[] auth = SteamTunnelAuth.Build(Secret);

            host.OnMessage(uno, auth, auth.Length);
            host.OnMessage(dos, auth, auth.Length);

            Assert.AreEqual(2, host.AuthorizedCount);
            Assert.AreEqual(2, sockets.Count);
            Assert.AreNotEqual(sockets[0].Port, sockets[1].Port);
        }

        [Test]
        public void Lo_que_contesta_el_backend_vuelve_por_la_conexion_de_ese_peer()
        {
            var sockets = new List<FakeSocket>();
            SteamTunnelHost host = NewHost(sockets);
            var channel = new FakeChannel();
            byte[] auth = SteamTunnelAuth.Build(Secret);
            host.OnMessage(channel, auth, auth.Length);

            var respuesta = new byte[] { 1, 2, 3, 4, 5 };
            sockets[0].QueueFromBackend(respuesta, Backend);

            Assert.AreEqual(1, host.PumpToSteam(0));
            Assert.AreEqual(1, channel.Sent.Count);
            CollectionAssert.AreEqual(respuesta, channel.Sent[0]);
        }

        [Test]
        public void Al_cerrarse_una_conexion_su_socket_se_suelta()
        {
            var sockets = new List<FakeSocket>();
            SteamTunnelHost host = NewHost(sockets);
            var channel = new FakeChannel();
            byte[] auth = SteamTunnelAuth.Build(Secret);
            host.OnMessage(channel, auth, auth.Length);

            host.OnClosed(channel);

            Assert.AreEqual(0, host.AuthorizedCount);
            Assert.IsTrue(sockets[0].Disposed, "un socket que sobrevive al peer es un puerto perdido");
            // Idempotente: el teardown puede llegar después de que Steam ya avisara. Si lanzara,
            // este test fallaría por la excepción.
            host.OnClosed(channel);
            Assert.AreEqual(0, host.AuthorizedCount);
        }

        [Test]
        public void Cerrar_el_host_suelta_todos_los_sockets()
        {
            var sockets = new List<FakeSocket>();
            SteamTunnelHost host = NewHost(sockets);
            byte[] auth = SteamTunnelAuth.Build(Secret);
            host.OnMessage(new FakeChannel(1UL), auth, auth.Length);
            host.OnMessage(new FakeChannel(2UL), auth, auth.Length);

            host.Dispose();

            Assert.AreEqual(0, host.AuthorizedCount);
            foreach (FakeSocket socket in sockets) Assert.IsTrue(socket.Disposed);
        }

        [Test]
        public void El_teardown_del_host_cierra_tambien_las_conexiones_de_Steam()
        {
            // ADR-135 D10: al cerrar el túnel no puede quedar ni un socket ni una conexión viva. Un
            // peer que siguiera conectado a un túnel muerto se queda esperando a un backend que ya
            // no está, y sólo saldría por el latido de 5 s.
            var sockets = new List<FakeSocket>();
            SteamTunnelHost host = NewHost(sockets);
            var uno = new FakeChannel(1UL);
            var dos = new FakeChannel(2UL);
            byte[] auth = SteamTunnelAuth.Build(Secret);
            host.OnMessage(uno, auth, auth.Length);
            host.OnMessage(dos, auth, auth.Length);

            host.Dispose();

            Assert.IsTrue(uno.Closed);
            Assert.IsTrue(dos.Closed);
            // Y después de cerrar no se le entrega nada más a nadie.
            Assert.AreEqual(0, host.PumpToSteam(0));
        }

        [Test]
        public void El_teardown_del_joiner_cierra_su_conexion_y_su_socket()
        {
            var channel = new FakeChannel();
            var socket = new FakeSocket(51004);
            var joiner = new SteamTunnelJoiner(channel, socket, Secret);
            joiner.SendAuth();

            joiner.Dispose();

            Assert.IsTrue(channel.Closed);
            Assert.IsTrue(socket.Disposed);
        }

        // ─── El joiner ───

        [Test]
        public void El_joiner_manda_el_sobre_una_sola_vez_y_antes_que_nada()
        {
            var channel = new FakeChannel();
            var socket = new FakeSocket(51000);
            var joiner = new SteamTunnelJoiner(channel, socket, Secret);

            Assert.IsTrue(joiner.SendAuth());
            Assert.IsTrue(joiner.SendAuth(), "idempotente: el bombeo lo reintenta cada vuelta");

            Assert.AreEqual(1, channel.Sent.Count);
            Assert.IsTrue(SteamTunnelAuth.Accepts(channel.Sent[0], channel.Sent[0].Length, Secret));
            Assert.AreEqual(51000, joiner.LocalPort, "es el puerto que viaja en CONNECT_STEAM");
        }

        [Test]
        public void El_joiner_aprende_el_puerto_de_su_backend_del_primer_datagrama()
        {
            // No se puede saber antes: el puerto de origen lo elige el sistema al mandar.
            var channel = new FakeChannel();
            var socket = new FakeSocket(51001);
            var joiner = new SteamTunnelJoiner(channel, socket, Secret);
            joiner.SendAuth();

            var haciaSteam = new byte[] { 7, 7 };
            var backendLocal = new IPEndPoint(IPAddress.Loopback, 60123);
            socket.QueueFromBackend(haciaSteam, backendLocal);
            Assert.IsTrue(joiner.PumpToSteam(0));

            var respuesta = new byte[] { 8, 8, 8 };
            joiner.OnMessage(respuesta, respuesta.Length);

            Assert.AreEqual(1, socket.Sent.Count);
            CollectionAssert.AreEqual(respuesta, socket.Sent[0].payload);
            Assert.AreEqual(backendLocal, socket.Sent[0].target, "se contesta a donde escribió el backend");
        }

        [Test]
        public void Lo_que_llega_de_Steam_antes_de_saber_el_puerto_se_descarta_sin_reventar()
        {
            var channel = new FakeChannel();
            var socket = new FakeSocket(51002);
            var joiner = new SteamTunnelJoiner(channel, socket, Secret);

            // Si lanzara, este test fallaría por la excepción: el túnel no puede reventar porque el
            // host conteste antes de que el backend local haya dicho nada.
            joiner.OnMessage(new byte[] { 1 }, 1);
            Assert.AreEqual(0, socket.Sent.Count);
        }

        [Test]
        public void Un_joiner_sin_secreto_no_manda_nada()
        {
            var channel = new FakeChannel();
            var joiner = new SteamTunnelJoiner(channel, new FakeSocket(51003), null);

            Assert.IsFalse(joiner.SendAuth());
            Assert.AreEqual(0, channel.Sent.Count);
        }

        // ─── El payload no se toca (ADR-135 D3) ───

        [Test]
        public void Un_datagrama_de_1200_bytes_cruza_entero_y_sin_agrupar()
        {
            // El techo de ADR-113 es 1200 B de payload. El túnel no fragmenta ni agrupa: si lo
            // hiciera, reintroduciría justo el modo de fallo que ADR-113 cerró.
            var sockets = new List<FakeSocket>();
            SteamTunnelHost host = NewHost(sockets);
            var channel = new FakeChannel();
            byte[] auth = SteamTunnelAuth.Build(Secret);
            host.OnMessage(channel, auth, auth.Length);

            var grande = new byte[1200];
            for (int i = 0; i < grande.Length; i++) grande[i] = (byte)(i % 251);

            host.OnMessage(channel, grande, grande.Length);

            Assert.AreEqual(1, sockets[0].Sent.Count);
            CollectionAssert.AreEqual(grande, sockets[0].Sent[0].payload);
        }

        [Test]
        public void Dos_datagramas_son_dos_mensajes_y_conservan_el_orden()
        {
            var sockets = new List<FakeSocket>();
            SteamTunnelHost host = NewHost(sockets);
            var channel = new FakeChannel();
            byte[] auth = SteamTunnelAuth.Build(Secret);
            host.OnMessage(channel, auth, auth.Length);

            sockets[0].QueueFromBackend(new byte[] { 1 }, Backend);
            sockets[0].QueueFromBackend(new byte[] { 2 }, Backend);

            host.PumpToSteam(0);
            host.PumpToSteam(0);

            Assert.AreEqual(2, channel.Sent.Count);
            // `(byte)` explícito: comparar un byte con un literal int los boxea a tipos distintos.
            Assert.AreEqual((byte)1, channel.Sent[0][0]);
            Assert.AreEqual((byte)2, channel.Sent[1][0]);
        }
    }
}
