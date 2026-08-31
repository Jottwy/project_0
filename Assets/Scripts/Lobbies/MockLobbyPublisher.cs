using System;

namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Anuncia una partida DENTRO DEL MISMO PROCESO, metiendo su ficha en un
    /// <see cref="MockLobbyDirectory"/>.
    ///
    /// Para qué sirve: cerrar el circuito `Host → publicador → directorio → navegador` y poder
    /// probarlo entero sin red. Para qué NO sirve: para que otro PC vea la partida. No hay
    /// transporte debajo — es memoria compartida entre dos objetos del mismo proceso, y está aquí
    /// para que el día que exista un `HttpLobbyPublisher` no haya que inventarse la forma del
    /// interfaz con prisa.
    ///
    /// El id se deriva del endpoint (`local-host-puerto`) en vez de sortearse: dos publicaciones
    /// del mismo servidor tienen que ser la MISMA ficha, no dos filas.
    /// </summary>
    public sealed class MockLobbyPublisher : ILobbyPublisher
    {
        private readonly MockLobbyDirectory _directory;
        private MockLobbyDirectory.MockEntry _entry;
        private LobbyPublication _publication;

        public MockLobbyPublisher(MockLobbyDirectory directory)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        public bool IsPublishing => _entry != null;
        public LobbyId PublishedId { get; private set; } = LobbyId.None;

        public bool Publish(LobbyPublication publication, int players, LobbyStatus status, double nowUnix)
        {
            Withdraw();

            string id = MakeId(publication.Endpoint);
            if (!Lobby.TryCreate(id, publication.Name, publication.Version, players,
                    publication.MaxPlayers, publication.Map, publication.Region, Lobby.UnknownPing,
                    publication.Privacy, publication.RequiresPassword,
                    publication.Endpoint.Host, publication.Endpoint.Port, nowUnix,
                    publication.TtlSeconds, status, out Lobby lobby))
            {
                return false;
            }

            _publication = publication;
            PublishedId = lobby.Id;

            // `keepTemplateTimestamp` encendido: el anuncio conserva SU hora y envejece si nadie
            // llama a Touch. Sin eso el directorio lo re-sellaría en cada foto y un host caído
            // seguiría anunciado para siempre — que es justo el fallo que el TTL existe para
            // evitar.
            _entry = new MockLobbyDirectory.MockEntry(lobby, keepTemplateTimestamp: true);
            _directory.Entries.Add(_entry);
            return true;
        }

        public void Touch(int players, LobbyStatus status, double nowUnix)
        {
            if (_entry == null) return;

            Lobby current = _entry.Template;
            if (!Lobby.TryCreate(current.Id.Value, _publication.Name, _publication.Version, players,
                    _publication.MaxPlayers, _publication.Map, _publication.Region,
                    Lobby.UnknownPing, _publication.Privacy, _publication.RequiresPassword,
                    _publication.Endpoint.Host, _publication.Endpoint.Port, nowUnix,
                    _publication.TtlSeconds, status, out Lobby updated))
            {
                return;
            }

            _entry.Template = updated;
        }

        public void Withdraw()
        {
            if (_entry == null) return;
            _directory.Entries.Remove(_entry);
            _entry = null;
            PublishedId = LobbyId.None;
        }

        /// <summary>El id que tendría el anuncio de este endpoint. Determinista y público para tests.</summary>
        public static string MakeId(LobbyEndpoint endpoint) =>
            "local-" + (endpoint.Host ?? "unknown") + "-" + endpoint.Port;
    }
}
