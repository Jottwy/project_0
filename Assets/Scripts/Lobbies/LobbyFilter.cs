using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Lo que el JUGADOR decide no ver. Deliberadamente NO incluye el TTL: una entrada caducada
    /// no es una preferencia, es una ficha que ya no describe nada, y se poda antes en
    /// <see cref="LobbyList.WithoutExpired"/>.
    ///
    /// Mutable porque es el estado de unos controles de UI; aplicarlo, en cambio, produce una
    /// lista nueva y nunca toca la foto original.
    /// </summary>
    public sealed class LobbyFilter
    {
        /// Sin ping medido no se puede juzgar contra un tope, así que un lobby sin ping PASA el
        /// filtro de ping. Esconderlo castigaría al servidor por no haber contestado todavía.
        public const int AnyPing = 0;

        /// Versión de ESTE cliente. Vacía = no se compara nada (útil en tests y en el arranque,
        /// antes de que el bootstrap la inyecte).
        public string ClientVersion = "";

        public bool HideFull;
        public bool HideEmpty;
        public bool HidePasswordProtected;
        public bool HideIncompatibleVersion;

        /// Esconde FriendsOnly y Private. Lo que un directorio real ya no debería ni anunciar,
        /// pero el cliente no puede dar por supuesto.
        public bool HideNonPublic;

        /// Tope en milisegundos, o <see cref="AnyPing"/>.
        public int MaxPingMs = AnyPing;

        /// Nulo o vacío = cualquiera. Comparación exacta ignorando mayúsculas.
        public string Region;
        public string Map;

        /// Subcadena buscada en nombre y mapa, ignorando mayúsculas.
        public string SearchText;

        public bool Matches(Lobby lobby)
        {
            if (lobby == null) return false;

            if (HideFull && lobby.IsFull) return false;
            if (HideEmpty && lobby.IsEmpty) return false;
            if (HidePasswordProtected && lobby.RequiresPassword) return false;
            if (HideNonPublic && lobby.Privacy != LobbyPrivacy.Public) return false;

            if (HideIncompatibleVersion &&
                !string.IsNullOrEmpty(ClientVersion) &&
                !lobby.IsCompatibleWith(ClientVersion)) return false;

            if (MaxPingMs > AnyPing && lobby.HasPing && lobby.PingMs > MaxPingMs) return false;

            if (!MatchesExact(Region, lobby.Region)) return false;
            if (!MatchesExact(Map, lobby.Map)) return false;

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                string needle = SearchText.Trim();
                bool inName = lobby.Name != null &&
                              lobby.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                bool inMap = lobby.Map != null &&
                             lobby.Map.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!inName && !inMap) return false;
            }

            return true;
        }

        /// <summary>Aplica el filtro a una foto y devuelve una lista NUEVA.</summary>
        public List<Lobby> Apply(LobbyList source)
        {
            var kept = new List<Lobby>(source == null ? 0 : source.Count);
            if (source == null) return kept;

            for (int i = 0; i < source.Count; i++)
            {
                if (Matches(source[i])) kept.Add(source[i]);
            }

            return kept;
        }

        public void Reset()
        {
            HideFull = false;
            HideEmpty = false;
            HidePasswordProtected = false;
            HideIncompatibleVersion = false;
            HideNonPublic = false;
            MaxPingMs = AnyPing;
            Region = null;
            Map = null;
            SearchText = null;
        }

        public LobbyFilter Clone() => new LobbyFilter
        {
            ClientVersion = ClientVersion,
            HideFull = HideFull,
            HideEmpty = HideEmpty,
            HidePasswordProtected = HidePasswordProtected,
            HideIncompatibleVersion = HideIncompatibleVersion,
            HideNonPublic = HideNonPublic,
            MaxPingMs = MaxPingMs,
            Region = Region,
            Map = Map,
            SearchText = SearchText,
        };

        private static bool MatchesExact(string wanted, string actual)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return true;
            return string.Equals(wanted.Trim(), actual, StringComparison.OrdinalIgnoreCase);
        }
    }
}
