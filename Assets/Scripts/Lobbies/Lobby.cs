using System;

namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Identidad de un lobby en el directorio. Envuelve un string porque el identificador lo
    /// emite QUIEN publica (hoy el mock, mañana el Lobby Directory), y compararlo como texto
    /// suelto por todo el código es como se cuela un `==` sensible a mayúsculas contra un
    /// backend que no lo es. La comparación es ordinal-ignore-case y el valor viaja recortado.
    /// </summary>
    public readonly struct LobbyId : IEquatable<LobbyId>
    {
        public static readonly LobbyId None = new LobbyId(null);

        public readonly string Value;

        public LobbyId(string value)
        {
            Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public bool IsValid => Value != null;

        public bool Equals(LobbyId other)
        {
            if (Value == null || other.Value == null) return Value == null && other.Value == null;
            return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj) => obj is LobbyId other && Equals(other);

        public override int GetHashCode() =>
            Value == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

        public override string ToString() => Value ?? "<none>";

        public static bool operator ==(LobbyId a, LobbyId b) => a.Equals(b);
        public static bool operator !=(LobbyId a, LobbyId b) => !a.Equals(b);
    }

    /// <summary>Dónde se conecta el cliente. Es el ÚNICO dato del lobby que toca la red.</summary>
    public readonly struct LobbyEndpoint : IEquatable<LobbyEndpoint>
    {
        public static readonly LobbyEndpoint None = default;

        public readonly string Host;
        public readonly int Port;

        /// <summary>
        /// Otra dirección del MISMO host, para reintentar si la principal no contesta. Hoy la
        /// llena `bs_lan_ip`: cuando un host con mapeo UPnP confirmado anuncia su IP pública, un
        /// joiner de la misma red sólo llega ahí si el router hace hairpin (NAT loopback), y
        /// muchos routers domésticos no lo hacen. El puerto es el mismo.
        ///
        /// **No forma parte de la identidad del endpoint** — <see cref="Equals"/> y
        /// <see cref="GetHashCode"/> siguen mirando sólo host y puerto. Dos fichas del mismo
        /// servidor, una leída antes de que el host publicara su LAN y otra después, tienen que
        /// seguir siendo el mismo destino; si la alternativa contara, la selección del navegador
        /// se perdería sola en el refresco siguiente.
        /// </summary>
        public readonly string Alternate;

        public LobbyEndpoint(string host, int port) : this(host, port, null)
        {
        }

        public LobbyEndpoint(string host, int port, string alternate)
        {
            Host = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
            Port = port;
            Alternate = string.IsNullOrWhiteSpace(alternate) ? null : alternate.Trim();
        }

        /// <summary>Hay una segunda dirección a la que probar, y no es la misma que la primera.</summary>
        public bool HasAlternate =>
            Alternate != null && !string.Equals(Alternate, Host, StringComparison.OrdinalIgnoreCase);

        /// El rango es el de un puerto utilizable; el 0 es "que elija el sistema" y nunca es un
        /// destino válido al que llamar.
        public bool IsValid => Host != null && Port > 0 && Port <= 65535;

        public bool Equals(LobbyEndpoint other) =>
            string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase) && Port == other.Port;

        public override bool Equals(object obj) => obj is LobbyEndpoint other && Equals(other);

        public override int GetHashCode() =>
            ((Host == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Host)) * 397) ^ Port;

        public override string ToString() => IsValid ? Host + ":" + Port : "<invalid>";
    }

    /// <summary>
    /// La sesión de relay que anuncia un lobby (ADR-117), o nada.
    ///
    /// **Las tres claves valen como un bloque: o están las tres o no hay relay.** Media sesión no
    /// sirve para entrar —sin token el relay deniega, sin id de sesión no hay a qué entrar— y
    /// tratarla como válida convertiría un lobby mal publicado en uno que promete algo que no puede
    /// cumplir, que es exactamente lo que ADR-112 evita al no publicar endpoints malos.
    /// </summary>
    public readonly struct LobbyRelay : IEquatable<LobbyRelay>
    {
        public static readonly LobbyRelay None = default;

        /// 16 bytes en hexadecimal. Espejo de `TOKEN_BYTES` del relay.
        public const int TokenLength = 32;

        /// `ip:puerto` del relay, tal cual lo publicó el host.
        public readonly string Address;

        /// El id de sesión, como cadena: viaja así por Steam y así se le pasa al backend, que es
        /// quien lo parsea. Convertirlo aquí sólo añadiría un sitio donde equivocarse de base.
        public readonly string Session;

        /// 32 hexadecimales. **No se registra en ningún log** (ADR-117 D9).
        public readonly string Token;

        public LobbyRelay(string address, string session, string token)
        {
            Address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
            Session = string.IsNullOrWhiteSpace(session) ? null : session.Trim();
            Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }

        /// Hay relay utilizable: las tres, y el token con la longitud que el backend exige.
        public bool IsValid =>
            Address != null && Session != null && Token != null && Token.Length == TokenLength;

        public bool Equals(LobbyRelay other) =>
            string.Equals(Address, other.Address, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Session, other.Session, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is LobbyRelay other && Equals(other);

        public override int GetHashCode() =>
            ((Address == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Address)) * 397) ^
            (Session == null ? 0 : Session.GetHashCode());

        /// **Sin el token**: esto se pinta y se registra.
        public override string ToString() => IsValid ? Address + "#" + Session : "<sin relay>";
    }

    /// <summary>
    /// La vía Steam que anuncia un lobby (ADR-135), o nada: a qué `SteamId` llamar y el secreto de
    /// sesión con el que ese host autoriza (D4', enmienda 1).
    ///
    /// **Las dos valen como un bloque**, por lo mismo que <see cref="LobbyRelay"/>: sin `SteamId`
    /// no hay a quién llamar, y sin secreto el host cierra la conexión en el primer mensaje. Media
    /// vía anunciada como entera es un lobby que promete lo que no puede cumplir.
    ///
    /// **El `SteamId` sale de una clave explícita del lobby, NUNCA de `Lobby.Owner`**: la propiedad
    /// de un lobby de Steam MIGRA cuando el dueño se va, y esto tiene que apuntar al proceso que
    /// sirve el mundo, no al miembro más antiguo.
    /// </summary>
    public readonly struct LobbySteamHost : IEquatable<LobbySteamHost>
    {
        public static readonly LobbySteamHost None = default;

        /// 16 bytes en hexadecimal, la misma forma que <see cref="LobbyRelay.TokenLength"/>.
        public const int SecretLength = 32;

        /// El `SteamId` del host, tal cual lo publicó. 0 = no hay vía Steam.
        public readonly ulong SteamId;

        /// El secreto de sesión. **No se registra en ningún log** (ADR-135 D4'.6).
        public readonly string Secret;

        public LobbySteamHost(ulong steamId, string secret)
        {
            SteamId = steamId;
            Secret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
        }

        public bool IsValid => SteamId != 0UL && Secret != null && Secret.Length == SecretLength;

        public bool Equals(LobbySteamHost other) => SteamId == other.SteamId;

        public override bool Equals(object obj) => obj is LobbySteamHost other && Equals(other);

        public override int GetHashCode() => SteamId.GetHashCode();

        /// **Sin el secreto**: esto se pinta y se registra.
        public override string ToString() => IsValid ? "steam:" + SteamId : "<sin steam>";
    }

    /// <summary>Visibilidad declarada por quien publica. El navegador NO la deduce.</summary>
    public enum LobbyPrivacy
    {
        Public = 0,
        FriendsOnly = 1,
        Private = 2,
    }

    /// <summary>En qué punto de su vida está la partida.</summary>
    public enum LobbyStatus
    {
        Unknown = 0,

        /// Aceptando gente.
        Waiting = 1,

        /// Partida en curso.
        InProgress = 2,

        /// El host la cerró; sigue en la lista hasta que caduque por TTL.
        Closed = 3,
    }

    /// <summary>
    /// Resultado de preguntar "¿puedo entrar aquí?". Un bool no vale: la UI tiene que decir POR
    /// QUÉ no, y el adaptador de Join tiene que negarse por el mismo motivo que la lista pinta.
    /// </summary>
    public enum LobbyJoinability
    {
        Joinable = 0,
        Expired = 1,
        Closed = 2,
        Private = 3,
        InvalidEndpoint = 4,
        VersionMismatch = 5,
        Full = 6,
        PasswordRequired = 7,
    }

    /// <summary>
    /// Una entrada del navegador de servidores. Inmutable a propósito: un refresh SUSTITUYE la
    /// lista entera en vez de mutar fichas vivas, así que la UI nunca pinta un objeto a medio
    /// actualizar y la selección se resuelve por <see cref="LobbyId"/>, no por referencia.
    ///
    /// Este modelo NO habla con la red. No conoce el protocolo, ni el handshake, ni
    /// NetworkManager: lo único que exporta hacia la conexión es <see cref="Endpoint"/>.
    /// </summary>
    public sealed class Lobby
    {
        public const int UnknownPing = -1;
        public const float DefaultTtlSeconds = 30f;
        public const int MaxNameLength = 48;

        public readonly LobbyId Id;
        public readonly string Name;

        /// Versión del build que sirve la partida. Se compara como texto, ordinal: dos builds
        /// sólo son compatibles si publican exactamente la misma cadena.
        public readonly string Version;

        public readonly int Players;
        public readonly int MaxPlayers;
        public readonly string Map;
        public readonly string Region;

        /// Milisegundos, o <see cref="UnknownPing"/> mientras no se haya medido.
        public readonly int PingMs;

        public readonly LobbyPrivacy Privacy;
        public readonly bool RequiresPassword;
        public readonly LobbyEndpoint Endpoint;

        /// Segundos Unix del último anuncio recibido. Con <see cref="TtlSeconds"/> decide cuándo
        /// la entrada deja de ser creíble: un directorio no avisa de los servidores que MUEREN,
        /// sólo deja de anunciarlos.
        public readonly double UpdatedAtUnix;

        public readonly float TtlSeconds;
        public readonly LobbyStatus Status;

        /// <summary>
        /// ADR-117: la sesión de relay que anuncia el host, o <see cref="LobbyRelay.None"/>.
        ///
        /// Es la SEGUNDA vía de entrada, no un adorno del endpoint: un lobby con relay se puede
        /// entrar aunque <see cref="Endpoint"/> no valga, que es justo el caso del host sin UPnP
        /// del playtest del 2026-09-02.
        /// </summary>
        public readonly LobbyRelay Relay;

        /// <summary>
        /// ADR-135: la vía Steam que anuncia el host, o <see cref="LobbySteamHost.None"/>. Es la
        /// TERCERA vía de entrada, y como el relay no es un adorno del endpoint: un lobby con sólo
        /// esto se puede entrar.
        /// </summary>
        public readonly LobbySteamHost SteamHost;

        public Lobby(
            LobbyId id,
            string name,
            string version,
            int players,
            int maxPlayers,
            string map,
            string region,
            int pingMs,
            LobbyPrivacy privacy,
            bool requiresPassword,
            LobbyEndpoint endpoint,
            double updatedAtUnix,
            float ttlSeconds,
            LobbyStatus status)
            : this(id, name, version, players, maxPlayers, map, region, pingMs, privacy,
                requiresPassword, endpoint, updatedAtUnix, ttlSeconds, status, LobbyRelay.None)
        {
        }

        public Lobby(
            LobbyId id,
            string name,
            string version,
            int players,
            int maxPlayers,
            string map,
            string region,
            int pingMs,
            LobbyPrivacy privacy,
            bool requiresPassword,
            LobbyEndpoint endpoint,
            double updatedAtUnix,
            float ttlSeconds,
            LobbyStatus status,
            LobbyRelay relay)
            : this(id, name, version, players, maxPlayers, map, region, pingMs, privacy,
                requiresPassword, endpoint, updatedAtUnix, ttlSeconds, status, relay,
                LobbySteamHost.None)
        {
        }

        public Lobby(
            LobbyId id,
            string name,
            string version,
            int players,
            int maxPlayers,
            string map,
            string region,
            int pingMs,
            LobbyPrivacy privacy,
            bool requiresPassword,
            LobbyEndpoint endpoint,
            double updatedAtUnix,
            float ttlSeconds,
            LobbyStatus status,
            LobbyRelay relay,
            LobbySteamHost steamHost)
        {
            Relay = relay;
            SteamHost = steamHost;
            Id = id;
            Name = name;
            Version = version;
            Players = players;
            MaxPlayers = maxPlayers;
            Map = map;
            Region = region;
            PingMs = pingMs;
            Privacy = privacy;
            RequiresPassword = requiresPassword;
            Endpoint = endpoint;
            UpdatedAtUnix = updatedAtUnix;
            TtlSeconds = ttlSeconds;
            Status = status;
        }

        public bool HasPing => PingMs >= 0;

        /// <summary>
        /// Hay por dónde entrar: un endpoint directo, la vía Steam, una sesión de relay, o varias.
        /// Es lo que sustituye al viejo «¿el endpoint vale?» desde ADR-117 D7, con la vía de
        /// ADR-135 sumada.
        /// </summary>
        public bool HasSomeWayIn => Endpoint.IsValid || Relay.IsValid || SteamHost.IsValid;

        /// <summary>Sólo se puede entrar por relay: el host no anunció ningún endpoint directo.</summary>
        public bool IsRelayOnly => !Endpoint.IsValid && Relay.IsValid;

        public bool IsFull => Players >= MaxPlayers;
        public bool IsEmpty => Players <= 0;
        public int FreeSlots => MaxPlayers - Players < 0 ? 0 : MaxPlayers - Players;

        /// <summary>Caducidad por TTL. Un `ttl &lt;= 0` significa "no caduca".</summary>
        public bool IsExpired(double nowUnix) =>
            TtlSeconds > 0f && nowUnix - UpdatedAtUnix > TtlSeconds;

        public bool IsCompatibleWith(string clientVersion) =>
            !string.IsNullOrEmpty(Version) &&
            !string.IsNullOrEmpty(clientVersion) &&
            string.Equals(Version, clientVersion.Trim(), StringComparison.Ordinal);

        /// <summary>
        /// El orden de las comprobaciones ES la regla: primero lo que invalida la ficha entera
        /// (caducada, cerrada, privada, sin destino), luego lo que impide entrar a ESTE cliente
        /// (versión), y sólo al final lo que el jugador puede resolver esperando un hueco o
        /// tecleando una contraseña. Al revés, un servidor lleno de otra versión pediría hueco.
        /// </summary>
        public LobbyJoinability EvaluateJoinability(string clientVersion, double nowUnix, bool passwordSupplied = false)
        {
            if (IsExpired(nowUnix)) return LobbyJoinability.Expired;
            if (Status == LobbyStatus.Closed) return LobbyJoinability.Closed;
            if (Privacy == LobbyPrivacy.Private) return LobbyJoinability.Private;
            // ADR-117 D7: hay DOS vías, y basta con una. Un lobby sin `connect_ip` pero con relay
            // es entrable, y ése es el caso del host sin UPnP ni reenvío de puertos — o sea, la
            // mayoría. Antes se marcaba `InvalidEndpoint` y el botón salía apagado.
            if (!HasSomeWayIn) return LobbyJoinability.InvalidEndpoint;
            if (!IsCompatibleWith(clientVersion)) return LobbyJoinability.VersionMismatch;
            if (IsFull) return LobbyJoinability.Full;
            if (RequiresPassword && !passwordSupplied) return LobbyJoinability.PasswordRequired;
            return LobbyJoinability.Joinable;
        }

        /// <summary>Copia con otro ping. Es lo único que se remide sin volver a anunciar.</summary>
        public Lobby WithPing(int pingMs) => new Lobby(
            Id, Name, Version, Players, MaxPlayers, Map, Region, pingMs,
            Privacy, RequiresPassword, Endpoint, UpdatedAtUnix, TtlSeconds, Status, Relay, SteamHost);

        /// <summary>Copia con otro sello de tiempo. Es lo que hace un anuncio repetido.</summary>
        public Lobby WithUpdatedAt(double updatedAtUnix) => new Lobby(
            Id, Name, Version, Players, MaxPlayers, Map, Region, PingMs,
            Privacy, RequiresPassword, Endpoint, updatedAtUnix, TtlSeconds, Status, Relay, SteamHost);

        /// <summary>
        /// La única puerta por la que deben entrar datos de OTRA máquina. Lo que hoy sanea al
        /// mock es exactamente lo que mañana saneará al JSON del Lobby Directory; por eso vive
        /// aquí y no en el mock.
        ///
        /// Rechaza (devuelve false) lo que no se puede reparar sin inventarse un servidor: sin
        /// id, sin aforo. Repara lo que sí: nombres nulos, contadores fuera de rango, ping
        /// negativo, TTL ausente. Un lobby sin endpoint válido SE ADMITE —se lista y se ve— pero
        /// <see cref="EvaluateJoinability"/> lo marca InvalidEndpoint; esconderlo dejaría al
        /// jugador buscando un servidor que sí está anunciado.
        /// </summary>
        public static bool TryCreate(
            string id,
            string name,
            string version,
            int players,
            int maxPlayers,
            string map,
            string region,
            int pingMs,
            LobbyPrivacy privacy,
            bool requiresPassword,
            string host,
            int port,
            double updatedAtUnix,
            float ttlSeconds,
            LobbyStatus status,
            out Lobby lobby) =>
            TryCreate(id, name, version, players, maxPlayers, map, region, pingMs, privacy,
                requiresPassword, host, port, updatedAtUnix, ttlSeconds, status, null,
                LobbyRelay.None, out lobby);

        /// <summary>
        /// Igual, con una dirección alternativa del mismo host (ver
        /// <see cref="LobbyEndpoint.Alternate"/>).
        ///
        /// Es una SOBRECARGA y no un parámetro opcional porque `out lobby` va al final, y C# no
        /// admite un opcional delante de un obligatorio. Y no es un `Attach` posterior porque
        /// <see cref="Lobby"/> es inmutable a propósito: un refresh sustituye la lista entera, así
        /// que la UI nunca pinta una ficha a medio actualizar.
        /// </summary>
        public static bool TryCreate(
            string id,
            string name,
            string version,
            int players,
            int maxPlayers,
            string map,
            string region,
            int pingMs,
            LobbyPrivacy privacy,
            bool requiresPassword,
            string host,
            int port,
            double updatedAtUnix,
            float ttlSeconds,
            LobbyStatus status,
            string alternateHost,
            out Lobby lobby) =>
            TryCreate(id, name, version, players, maxPlayers, map, region, pingMs, privacy,
                requiresPassword, host, port, updatedAtUnix, ttlSeconds, status, alternateHost,
                LobbyRelay.None, out lobby);

        /// <summary>
        /// Igual, con la sesión de relay del host (ADR-117). Tercera sobrecarga por el mismo
        /// motivo que la segunda: `out lobby` va al final y C# no admite un opcional delante de un
        /// obligatorio.
        /// </summary>
        public static bool TryCreate(
            string id,
            string name,
            string version,
            int players,
            int maxPlayers,
            string map,
            string region,
            int pingMs,
            LobbyPrivacy privacy,
            bool requiresPassword,
            string host,
            int port,
            double updatedAtUnix,
            float ttlSeconds,
            LobbyStatus status,
            string alternateHost,
            LobbyRelay relay,
            out Lobby lobby) =>
            TryCreate(id, name, version, players, maxPlayers, map, region, pingMs, privacy,
                requiresPassword, host, port, updatedAtUnix, ttlSeconds, status, alternateHost,
                relay, LobbySteamHost.None, out lobby);

        /// <summary>
        /// Igual, con la vía Steam del host (ADR-135). Cuarta sobrecarga por el mismo motivo que
        /// las tres anteriores: `out lobby` va al final y C# no admite un opcional delante de un
        /// obligatorio.
        /// </summary>
        public static bool TryCreate(
            string id,
            string name,
            string version,
            int players,
            int maxPlayers,
            string map,
            string region,
            int pingMs,
            LobbyPrivacy privacy,
            bool requiresPassword,
            string host,
            int port,
            double updatedAtUnix,
            float ttlSeconds,
            LobbyStatus status,
            string alternateHost,
            LobbyRelay relay,
            LobbySteamHost steamHost,
            out Lobby lobby)
        {
            lobby = null;

            var lobbyId = new LobbyId(id);
            if (!lobbyId.IsValid) return false;
            if (maxPlayers <= 0) return false;

            int safeMax = maxPlayers;
            int safePlayers = players < 0 ? 0 : players;
            if (safePlayers > safeMax) safePlayers = safeMax;

            string safeName = Sanitize(name, lobbyId.Value, MaxNameLength);
            string safeVersion = string.IsNullOrWhiteSpace(version) ? "" : version.Trim();
            string safeMap = Sanitize(map, "Unknown", 32);
            string safeRegion = Sanitize(region, "Unknown", 16);
            int safePing = pingMs < 0 ? UnknownPing : pingMs;
            float safeTtl = ttlSeconds > 0f ? ttlSeconds : DefaultTtlSeconds;
            double safeUpdated = updatedAtUnix < 0d ? 0d : updatedAtUnix;

            lobby = new Lobby(
                lobbyId, safeName, safeVersion, safePlayers, safeMax, safeMap, safeRegion,
                safePing, privacy, requiresPassword, new LobbyEndpoint(host, port, alternateHost),
                safeUpdated, safeTtl, status, relay, steamHost);
            return true;
        }

        private static string Sanitize(string value, string fallback, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            string trimmed = value.Trim();
            return trimmed.Length > maxLength ? trimmed.Substring(0, maxLength) : trimmed;
        }

        public override string ToString() =>
            Name + " [" + Id + "] " + Players + "/" + MaxPlayers + " " + Map + "@" + Region +
            " v" + Version + " " + Endpoint;
    }
}
