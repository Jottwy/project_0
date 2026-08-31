namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Lo que un host anuncia de su partida. Es el espejo exacto de lo que el navegador necesita
    /// para pintar una fila y para entrar: ni un campo más.
    /// </summary>
    public readonly struct LobbyPublication
    {
        public readonly string Name;
        public readonly string Version;
        public readonly int MaxPlayers;
        public readonly string Map;
        public readonly string Region;
        public readonly LobbyPrivacy Privacy;
        public readonly bool RequiresPassword;

        /// El destino al que llamará el cliente. Es el dato que hace útil todo lo demás — y el
        /// que hoy sólo sirve dentro de la misma red (ver `docs/SERVER_BROWSER.md`, NAT).
        public readonly LobbyEndpoint Endpoint;

        public readonly float TtlSeconds;

        public LobbyPublication(string name, string version, int maxPlayers, string map,
            string region, LobbyPrivacy privacy, bool requiresPassword, LobbyEndpoint endpoint,
            float ttlSeconds = Lobby.DefaultTtlSeconds)
        {
            Name = name;
            Version = version;
            MaxPlayers = maxPlayers;
            Map = map;
            Region = region;
            Privacy = privacy;
            RequiresPassword = requiresPassword;
            Endpoint = endpoint;
            TtlSeconds = ttlSeconds;
        }
    }

    /// <summary>
    /// El lado del HOST: quién le cuenta al mundo que esta partida existe.
    ///
    /// **HOY NO HAY NINGUNA IMPLEMENTACIÓN REAL, y no se ha inventado una.** El proyecto no tiene
    /// servicio de lobbies, ni descubrimiento LAN, ni enumeración de lobbies de Steam
    /// (`SteamLobbyManager` sabe CREAR un lobby e invitar por overlay, que es otra cosa: no se
    /// puede listar). La única implementación es <see cref="MockLobbyPublisher"/>, en memoria y
    /// dentro del mismo proceso, y sirve para cerrar el circuito en pruebas — no para que otro PC
    /// vea nada.
    ///
    /// El anuncio es de VIDA CORTA a propósito: se renueva con <see cref="Touch"/> y caduca solo.
    /// Un directorio no se entera de que un host MUERE, sólo de que deja de anunciarse; por eso
    /// el modelo lleva TTL desde el primer día.
    /// </summary>
    public interface ILobbyPublisher
    {
        bool IsPublishing { get; }

        /// Id del anuncio vivo, o <see cref="LobbyId.None"/>.
        LobbyId PublishedId { get; }

        /// <summary>Empieza a anunciar. Devuelve false si los datos no dan para una ficha válida.</summary>
        bool Publish(LobbyPublication publication, int players, LobbyStatus status, double nowUnix);

        /// <summary>
        /// Renueva el anuncio y actualiza lo que cambia en caliente: jugadores dentro y estado.
        /// Sin llamadas, el anuncio caduca por TTL, que es justo lo que tiene que pasar cuando el
        /// host se cae.
        /// </summary>
        void Touch(int players, LobbyStatus status, double nowUnix);

        /// <summary>Retira el anuncio. Idempotente.</summary>
        void Withdraw();
    }
}
