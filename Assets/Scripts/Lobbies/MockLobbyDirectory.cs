using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Directorio de mentira, y el ÚNICO que existe hoy. Sirve para dos cosas distintas y las dos
    /// importan: darle al navegador datos bastante variados como para que sus estados raros se
    /// puedan ver a mano, y darle a la suite un directorio determinista que no necesita servidor.
    ///
    /// No hace red. No abre sockets. No lee ficheros.
    /// </summary>
    public sealed class MockLobbyDirectory : ILobbyDirectory
    {
        /// <summary>
        /// Una plantilla del catálogo. El sello de tiempo del lobby NO se guarda aquí: se calcula
        /// en cada foto como `ahora - StaleSeconds`, que es lo que hace que "este lleva 45 s sin
        /// anunciarse" siga siendo cierto ejecutes el test hoy o dentro de un año.
        /// </summary>
        public sealed class MockEntry
        {
            /// Mutable: un host que publica y va actualizando su aforo sustituye SU plantilla en
            /// sitio (ver <see cref="MockLobbyPublisher"/>) en vez de quitar y volver a añadir la
            /// entrada, que haría parpadear la fila en la tabla.
            public Lobby Template;

            /// Refresco (1-based) a partir del cual esta entrada se anuncia. 1 = desde el primero.
            public int AppearsFromRefresh;

            /// Segundos transcurridos desde su último anuncio en el momento de la foto. Mayor que
            /// el TTL del lobby = entrada caducada.
            public float StaleSeconds;

            /// <summary>
            /// Con esto encendido la ficha conserva SU sello de tiempo en vez de recibir el de la
            /// foto. Es la diferencia entre una entrada del catálogo (siempre fresca, para poder
            /// probar la tabla) y un ANUNCIO de verdad, que envejece si su host no lo renueva —
            /// que es como caduca un servidor que se cayó.
            /// </summary>
            public bool KeepTemplateTimestamp;

            public MockEntry(Lobby template, int appearsFromRefresh = 1, float staleSeconds = 0f,
                bool keepTemplateTimestamp = false)
            {
                Template = template;
                AppearsFromRefresh = appearsFromRefresh < 1 ? 1 : appearsFromRefresh;
                StaleSeconds = staleSeconds;
                KeepTemplateTimestamp = keepTemplateTimestamp;
            }
        }

        public const string DefaultFailureMessage = "Lobby directory unreachable (mock)";

        /// Latencia simulada. En cero, el resultado llega en el primer <see cref="Tick"/>.
        public float LatencySeconds = 0.35f;

        /// Hace fallar LA PRÓXIMA consulta y se consume. Es como se prueba el estado de error sin
        /// desenchufar nada.
        public bool FailNextRefresh;

        public string FailureMessage = DefaultFailureMessage;

        public readonly List<MockEntry> Entries = new List<MockEntry>();

        /// Cuántas consultas se han pedido. Es lo que mueve `AppearsFromRefresh`.
        public int RefreshCount { get; private set; }

        private Action<LobbyDirectoryResult> _pending;
        private double _deadlineUnix;

        public MockLobbyDirectory() { }

        public MockLobbyDirectory(IEnumerable<MockEntry> entries)
        {
            if (entries == null) return;
            foreach (MockEntry entry in entries)
            {
                if (entry != null && entry.Template != null) Entries.Add(entry);
            }
        }

        public string Description => "mock";

        public bool IsRefreshing => _pending != null;

        public void Refresh(double nowUnix, Action<LobbyDirectoryResult> onCompleted)
        {
            // La consulta anterior se cancela ANTES de contar ésta: si la lenta contestara
            // después de la nueva, repintaría la tabla con datos más viejos.
            CancelRefresh();

            RefreshCount++;
            _pending = onCompleted;
            _deadlineUnix = nowUnix + (LatencySeconds > 0f ? LatencySeconds : 0f);
        }

        public void CancelRefresh()
        {
            Action<LobbyDirectoryResult> pending = _pending;
            _pending = null;
            pending?.Invoke(LobbyDirectoryResult.Cancelled());
        }

        public void Tick(double nowUnix)
        {
            if (_pending == null) return;
            if (nowUnix < _deadlineUnix) return;

            Action<LobbyDirectoryResult> pending = _pending;
            _pending = null;

            if (FailNextRefresh)
            {
                FailNextRefresh = false;
                pending(LobbyDirectoryResult.Failed(FailureMessage));
                return;
            }

            pending(LobbyDirectoryResult.Ok(Snapshot(nowUnix)));
        }

        /// <summary>
        /// La foto que devolvería el directorio AHORA. Pública porque es útil en tests que sólo
        /// quieren el catálogo, sin pasar por el ciclo consulta/espera.
        /// </summary>
        public LobbyList Snapshot(double nowUnix)
        {
            // Una foto pedida sin haber consultado nunca cuenta como la PRIMERA consulta; si no,
            // `Snapshot` a pelo devolvería la lista vacía y parecería un catálogo roto.
            int query = RefreshCount < 1 ? 1 : RefreshCount;

            var lobbies = new List<Lobby>(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                MockEntry entry = Entries[i];
                if (entry == null || entry.Template == null) continue;
                if (query < entry.AppearsFromRefresh) continue;

                lobbies.Add(entry.KeepTemplateTimestamp
                    ? entry.Template
                    : entry.Template.WithUpdatedAt(nowUnix - entry.StaleSeconds));
            }

            return LobbyList.Create(lobbies, nowUnix);
        }

        /// <summary>
        /// El catálogo por defecto. Cada entrada existe para poder VER un caso concreto del
        /// navegador; si borras una, dejas de poder probar ese caso a mano.
        ///
        /// `clientVersion` es la versión que se considera compatible: todo lo que no la lleve
        /// queda incompatible a propósito.
        /// </summary>
        public static MockLobbyDirectory CreateDefault(string clientVersion)
        {
            string version = string.IsNullOrWhiteSpace(clientVersion) ? "0.0.0" : clientVersion.Trim();
            var mock = new MockLobbyDirectory();

            // Vacío, ping bajo, mismo continente.
            mock.Entries.Add(new MockEntry(Make("eu-empty-01", "Almond Water Lounge", version,
                0, 8, "Level 0", "EU-West", 24, LobbyPrivacy.Public, false, "10.0.0.11", 7778,
                LobbyStatus.Waiting)));

            // A medio llenar, ping bajo. Es el caso normal.
            mock.Entries.Add(new MockEntry(Make("eu-partial-01", "Yellow Halls", version,
                3, 8, "Level 0", "EU-West", 38, LobbyPrivacy.Public, false, "10.0.0.12", 7778,
                LobbyStatus.InProgress)));

            // Lleno: se lista, se ve, y no se entra.
            mock.Entries.Add(new MockEntry(Make("eu-full-01", "Pool Rooms 24/7", version,
                8, 8, "Level 37", "EU-West", 41, LobbyPrivacy.Public, false, "10.0.0.13", 7778,
                LobbyStatus.InProgress)));

            // Con contraseña, ping medio.
            mock.Entries.Add(new MockEntry(Make("eu-locked-01", "Private Run [PW]", version,
                2, 6, "Level 1", "EU-Central", 87, LobbyPrivacy.Public, true, "10.0.0.14", 7778,
                LobbyStatus.Waiting)));

            // Versión incompatible: el jugador tiene que entender POR QUÉ no puede entrar.
            mock.Entries.Add(new MockEntry(Make("na-oldbuild-01", "Legacy Build Server", "0.0.0.9z",
                4, 12, "Level 0", "NA-East", 132, LobbyPrivacy.Public, false, "10.0.0.15", 7778,
                LobbyStatus.InProgress)));

            // Ping alto de verdad, otro mapa, otra región.
            mock.Entries.Add(new MockEntry(Make("ap-far-01", "Backrooms AP", version,
                5, 16, "Level 37", "AP-South", 268, LobbyPrivacy.Public, false, "10.0.0.16", 7778,
                LobbyStatus.InProgress)));

            // Ping sin medir: prueba que no se cuela en cabeza al ordenar por ping.
            mock.Entries.Add(new MockEntry(Make("na-unknown-ping", "Hub 9 (unmeasured)", version,
                1, 10, "Level 9", "NA-West", Lobby.UnknownPing, LobbyPrivacy.Public, false,
                "10.0.0.17", 7778, LobbyStatus.Waiting)));

            // Sólo amigos: no debería anunciarse, pero el cliente no puede darlo por hecho.
            mock.Entries.Add(new MockEntry(Make("eu-friends-01", "Joel & co", version,
                2, 4, "Level 1", "EU-West", 30, LobbyPrivacy.FriendsOnly, false, "10.0.0.18", 7778,
                LobbyStatus.Waiting)));

            // Cerrado por el host: sigue en la lista hasta que caduque.
            mock.Entries.Add(new MockEntry(Make("eu-closed-01", "Ended Session", version,
                0, 8, "Level 0", "EU-Central", 55, LobbyPrivacy.Public, false, "10.0.0.19", 7778,
                LobbyStatus.Closed)));

            // Endpoint roto (puerto 0): se lista y no se entra. Simula un anuncio mal formado que
            // pasó el saneado.
            mock.Entries.Add(new MockEntry(Make("eu-broken-endpoint", "Misconfigured Host", version,
                1, 8, "Level 0", "EU-West", 62, LobbyPrivacy.Public, false, "10.0.0.20", 0,
                LobbyStatus.Waiting)));

            // Caducado por TTL: anunciado hace 45 s con TTL de 30. Desaparece solo.
            mock.Entries.Add(new MockEntry(Make("eu-ghost-01", "Ghost Server", version,
                2, 8, "Level 0", "EU-West", 44, LobbyPrivacy.Public, false, "10.0.0.21", 7778,
                LobbyStatus.Waiting), appearsFromRefresh: 1, staleSeconds: 45f));

            // Aparece a partir del SEGUNDO refresco: es el caso "le doy a refrescar y sale uno
            // nuevo", que sin esto no se puede probar.
            mock.Entries.Add(new MockEntry(Make("eu-late-01", "Just Opened", version,
                1, 8, "Level 1", "EU-West", 29, LobbyPrivacy.Public, false, "10.0.0.22", 7778,
                LobbyStatus.Waiting), appearsFromRefresh: 2));

            return mock;
        }

        private static Lobby Make(
            string id, string name, string version, int players, int maxPlayers,
            string map, string region, int pingMs, LobbyPrivacy privacy, bool requiresPassword,
            string host, int port, LobbyStatus status)
        {
            // Pasa por el saneado a propósito: el mock entra por la misma puerta que entrará el
            // JSON del directorio real, así que una plantilla mal escrita revienta aquí y no en
            // producción.
            if (!Lobby.TryCreate(id, name, version, players, maxPlayers, map, region, pingMs,
                    privacy, requiresPassword, host, port, 0d, Lobby.DefaultTtlSeconds, status,
                    out Lobby lobby))
            {
                throw new ArgumentException("Mock lobby template rejected by Lobby.TryCreate: " + id);
            }

            return lobby;
        }
    }
}
