using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;

namespace BackroomsSurvival.Connectivity
{
    /// <summary>
    /// UNA conexión de la red de Valve, sin un solo tipo de Steamworks dentro.
    ///
    /// Existe por lo mismo que <c>ISteamLobbyQuery</c> y <c>ISteamLobbyHost</c>: todo lo que de
    /// verdad se puede equivocar —autorizar, mapear conexión a puerto, cerrar sin dejar nada
    /// abierto— vive de este lado y se prueba sin cliente de Steam. La implementación real es
    /// <c>FacepunchSteamTunnelChannel</c>.
    /// </summary>
    public interface ISteamTunnelChannel
    {
        /// El `SteamId` del otro extremo. Es la identidad que Valve autentica (ADR-135 D4'), y lo
        /// que se registra al aceptar o rechazar.
        ulong RemoteSteamId { get; }

        bool IsOpen { get; }

        /// <summary>
        /// Manda un mensaje **no fiable y sin Nagle**: un datagrama del juego es un mensaje, y la
        /// fiabilidad la pone el backend (`reliability.rs`). Doblarla aquí es exactamente lo que
        /// hay que evitar.
        /// </summary>
        bool Send(byte[] payload, int length);

        void Close();
    }

    /// <summary>
    /// El socket UDP de loopback que habla con el backend de esta máquina. Costura por la misma
    /// razón que <see cref="ISteamTunnelChannel"/>: para poder probar el túnel entero sin abrir un
    /// puerto. La implementación real es <see cref="UdpTunnelSocket"/>.
    /// </summary>
    public interface ISteamTunnelSocket : IDisposable
    {
        /// El puerto efímero que el sistema asignó. Es lo que viaja en `CONNECT_STEAM`.
        int Port { get; }

        void SendTo(byte[] payload, int length, IPEndPoint target);

        /// <summary>
        /// Espera hasta `timeoutMs` por un datagrama. `false` = no llegó nada, que es el caso
        /// normal y no es un error.
        /// </summary>
        bool TryReceive(int timeoutMs, out byte[] payload, out int length, out IPEndPoint from);
    }

    /// <summary>
    /// El sobre de autorización de ADR-135 D4'. **Viaja una sola vez**, como primer mensaje de la
    /// conexión, y no vuelve a aparecer: en cuanto el host lo acepta, todo lo demás son datagramas
    /// de juego opacos, uno por mensaje.
    ///
    /// No es una capa de protocolo del juego y no entra en el wire: el backend no lo ve, no lo
    /// cuenta y no sabe que existió — igual que el sobre del relay de ADR-117 D4 tampoco entra.
    /// </summary>
    public static class SteamTunnelAuth
    {
        /// Marca del sobre. Sirve para que un primer mensaje que NO sea una autorización se
        /// distinga de uno mal formado, y las dos cosas acaben igual: conexión cerrada.
        public static readonly byte[] Magic = { (byte)'B', (byte)'S', (byte)'T', (byte)'A', 1 };

        /// 16 bytes en 32 hexadecimales — la misma forma que el token del relay (ADR-117 D9). No es
        /// un formato nuevo: es el que el proyecto ya usa.
        public const int SecretLength = 32;

        public const int SecretBytes = SecretLength / 2;

        public static int FrameLength => Magic.Length + SecretBytes;

        /// <summary>
        /// Un secreto de sesión nuevo. **Criptográfico y no `System.Random`**, por lo mismo que
        /// documenta <see cref="RelaySessionCredentials"/>: `System.Random` se siembra con el
        /// reloj, así que dos partidas arrancadas en el mismo milisegundo sacarían el mismo valor y
        /// cualquiera que sepa cuándo empezó una podría reproducirlo.
        /// </summary>
        public static string NewSecret()
        {
            var bytes = new byte[SecretBytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);

            var hex = new System.Text.StringBuilder(SecretLength);
            foreach (byte b in bytes) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }

        /// <summary>El sobre que manda el joiner. `null` si el secreto no es utilizable.</summary>
        public static byte[] Build(string secretHex)
        {
            if (!TryParseSecret(secretHex, out byte[] secret)) return null;

            var frame = new byte[FrameLength];
            Buffer.BlockCopy(Magic, 0, frame, 0, Magic.Length);
            Buffer.BlockCopy(secret, 0, frame, Magic.Length, SecretBytes);
            return frame;
        }

        /// <summary>
        /// ¿Este primer mensaje autoriza? Falso para todo lo demás: longitud rara, marca que no
        /// cuadra, secreto que no coincide. **Quien no acierta no recibe un motivo** (D4'.6).
        /// </summary>
        public static bool Accepts(byte[] frame, int length, string expectedSecretHex)
        {
            if (frame == null || length != FrameLength) return false;
            if (!TryParseSecret(expectedSecretHex, out byte[] expected)) return false;

            for (int i = 0; i < Magic.Length; i++)
            {
                if (frame[i] != Magic[i]) return false;
            }

            // Comparación de tiempo constante: el bucle no sale antes por un byte que no cuadra.
            // Un secreto se compara así aunque el atacante tenga que estar en la red de Valve para
            // intentarlo.
            int diff = 0;
            for (int i = 0; i < SecretBytes; i++) diff |= frame[Magic.Length + i] ^ expected[i];
            return diff == 0;
        }

        private static bool TryParseSecret(string secretHex, out byte[] secret)
        {
            secret = null;
            if (secretHex == null) return false;

            string trimmed = secretHex.Trim();
            if (trimmed.Length != SecretLength) return false;

            var bytes = new byte[SecretBytes];
            for (int i = 0; i < SecretBytes; i++)
            {
                if (!byte.TryParse(trimmed.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                {
                    return false;
                }
            }

            secret = bytes;
            return true;
        }
    }

    /// <summary>
    /// Un par conexión-de-Steam ↔ socket de loopback: lo que convierte un mensaje de Valve en un
    /// datagrama para el backend y al revés. **Uno por peer**, y ése es el punto (ADR-135 D3): cada
    /// joiner llega al backend del host desde su PROPIO puerto de origen, así que la deduplicación
    /// por endpoint de `handlers.rs` sigue significando lo que decía y dos joiners no se toman por
    /// una reconexión del mismo.
    ///
    /// No mira el payload. Ni un byte: para el túnel, lo que pasa por él son bytes opacos.
    /// </summary>
    public sealed class SteamTunnelLink : IDisposable
    {
        private readonly ISteamTunnelChannel _channel;
        private readonly ISteamTunnelSocket _socket;

        /// A dónde se le entrega al backend. En el host es fijo —el `NET_PORT` del backend local—;
        /// en el joiner es null hasta que su backend manda el primer datagrama, porque el puerto de
        /// origen lo elige el sistema y no se puede saber antes.
        private IPEndPoint _backend;

        public SteamTunnelLink(ISteamTunnelChannel channel, ISteamTunnelSocket socket, IPEndPoint backend)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _backend = backend;
        }

        public int LocalPort => _socket.Port;

        public ulong RemoteSteamId => _channel.RemoteSteamId;

        /// Cuántos datagramas han cruzado en cada sentido. Observable para la suite y para el log:
        /// un túnel que no mueve nada es indistinguible de uno que no existe.
        public int ToBackend { get; private set; }

        public int ToSteam { get; private set; }

        /// <summary>
        /// Llegó un mensaje por Steam: va tal cual al backend. Se descarta —y se dice— sólo si
        /// todavía no se sabe a qué puerto del backend entregarlo.
        /// </summary>
        public bool DeliverFromSteam(byte[] payload, int length)
        {
            if (_backend == null) return false;

            _socket.SendTo(payload, length, _backend);
            ToBackend++;
            return true;
        }

        /// <summary>
        /// Un latido del sentido contrario: lo que el backend local haya escrito sale por Steam.
        /// Devuelve `true` si movió un datagrama, y es lo que el hilo del túnel repite en bucle.
        ///
        /// **Aquí se aprende el endpoint del backend** cuando no venía dado: el primer datagrama
        /// del joiner es su handshake, y su dirección de origen es a donde hay que devolverle las
        /// respuestas.
        /// </summary>
        public bool PumpToSteam(int timeoutMs)
        {
            if (!_socket.TryReceive(timeoutMs, out byte[] payload, out int length, out IPEndPoint from))
                return false;

            _backend ??= from;

            if (_channel.IsOpen && _channel.Send(payload, length)) ToSteam++;
            return true;
        }

        public void Dispose()
        {
            _channel.Close();
            _socket.Dispose();
        }
    }

    /// <summary>
    /// El lado HOST del túnel: acepta conexiones de Steam, las autoriza con el secreto de sesión
    /// (ADR-135 D4') y le da a cada una su propio socket de loopback contra el backend local.
    ///
    /// **La estrella la impone esto** (ADR-015, ADR-117 D6): un joiner sólo puede hablar con el
    /// host, porque no hay más túnel que éste y no reenvía a nadie más que al backend.
    ///
    /// No sabe de hilos: quien lo bombea es <see cref="SteamTunnelRunner"/>. Así se prueba entero,
    /// sin Steam, sin sockets y sin esperar a un reloj.
    /// </summary>
    public sealed class SteamTunnelHost : IDisposable
    {
        private readonly string _secret;
        private readonly IPEndPoint _backend;
        private readonly Func<ISteamTunnelSocket> _socketFactory;
        private readonly Dictionary<ISteamTunnelChannel, SteamTunnelLink> _authorized =
            new Dictionary<ISteamTunnelChannel, SteamTunnelLink>();

        public SteamTunnelHost(string secret, IPEndPoint backend, Func<ISteamTunnelSocket> socketFactory)
        {
            _secret = secret;
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        }

        public int AuthorizedCount => _authorized.Count;

        /// Conexiones cerradas por no autorizarse. Se cuenta porque es la señal de que alguien está
        /// llamando sin haber visto el lobby.
        public int RejectedCount { get; private set; }

        /// <summary>El último `SteamId` rechazado. Es el dato con el que se diagnostica (D4'.6).</summary>
        public ulong LastRejectedSteamId { get; private set; }

        /// <summary>
        /// Un mensaje de una conexión. Mientras no esté autorizada **no se le reenvía ni un byte al
        /// backend**: o el primer mensaje es la autorización, o la conexión se cierra.
        /// </summary>
        public void OnMessage(ISteamTunnelChannel channel, byte[] payload, int length)
        {
            if (channel == null) return;

            if (_authorized.TryGetValue(channel, out SteamTunnelLink link))
            {
                link.DeliverFromSteam(payload, length);
                return;
            }

            if (!SteamTunnelAuth.Accepts(payload, length, _secret))
            {
                RejectedCount++;
                LastRejectedSteamId = channel.RemoteSteamId;
                channel.Close();
                return;
            }

            _authorized[channel] = new SteamTunnelLink(channel, _socketFactory(), _backend);
        }

        /// <summary>La conexión murió (o la cerramos). Idempotente.</summary>
        public void OnClosed(ISteamTunnelChannel channel)
        {
            if (channel == null) return;
            if (!_authorized.TryGetValue(channel, out SteamTunnelLink link)) return;

            _authorized.Remove(channel);
            link.Dispose();
        }

        /// <summary>
        /// Un latido de todos los enlaces vivos: lo que el backend haya contestado sale hacia su
        /// peer. Devuelve cuántos datagramas movió.
        /// </summary>
        public int PumpToSteam(int timeoutMsPerLink)
        {
            int moved = 0;
            // Copia: un enlace puede cerrarse mientras se bombea, y modificar el diccionario dentro
            // de su propio recorrido lanza.
            var links = new List<SteamTunnelLink>(_authorized.Values);
            for (int i = 0; i < links.Count; i++)
            {
                if (links[i].PumpToSteam(timeoutMsPerLink)) moved++;
            }

            return moved;
        }

        public void Dispose()
        {
            foreach (SteamTunnelLink link in _authorized.Values) link.Dispose();
            _authorized.Clear();
        }
    }

    /// <summary>
    /// El lado JOINER: una sola conexión contra el host, y el puerto de loopback donde su propio
    /// backend le va a hablar. Ese puerto se elige ANTES de lanzar el backend y es lo que viaja en
    /// `CONNECT_STEAM` (ADR-135 D3).
    /// </summary>
    public sealed class SteamTunnelJoiner : IDisposable
    {
        private readonly ISteamTunnelChannel _channel;
        private readonly SteamTunnelLink _link;
        private readonly string _secret;

        public SteamTunnelJoiner(ISteamTunnelChannel channel, ISteamTunnelSocket socket, string secret)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _secret = secret;
            // `null`: el endpoint del backend se aprende de su primer datagrama. No se puede saber
            // antes, porque el puerto de origen lo elige el sistema al mandarlo.
            _link = new SteamTunnelLink(channel, socket, null);
        }

        public int LocalPort => _link.LocalPort;

        public bool AuthSent { get; private set; }

        public int ToBackend => _link.ToBackend;

        public int ToSteam => _link.ToSteam;

        /// <summary>
        /// Manda la autorización. Va **antes que ningún datagrama de juego**: hasta que el host la
        /// acepta, lo que se mande no llega a su backend. Idempotente: se manda una vez.
        /// </summary>
        public bool SendAuth()
        {
            if (AuthSent) return true;

            byte[] frame = SteamTunnelAuth.Build(_secret);
            if (frame == null || !_channel.IsOpen) return false;

            if (!_channel.Send(frame, frame.Length)) return false;

            AuthSent = true;
            return true;
        }

        public void OnMessage(byte[] payload, int length) => _link.DeliverFromSteam(payload, length);

        public bool PumpToSteam(int timeoutMs) => _link.PumpToSteam(timeoutMs);

        public void Dispose() => _link.Dispose();
    }
}
