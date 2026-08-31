using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Lobbies
{
    public enum LobbySortKey
    {
        Name = 0,
        Players = 1,
        FreeSlots = 2,
        Ping = 3,
        Map = 4,
        Region = 5,
        Version = 6,
    }

    /// <summary>
    /// Orden de la tabla. Dos reglas que no son gusto sino corrección:
    ///
    /// 1. <c>List.Sort</c> NO es estable, así que todo empate se rompe por <see cref="LobbyId"/>.
    ///    Sin eso, dos refrescos con los mismos datos pueden pintar filas en distinto orden y la
    ///    fila bajo el cursor cambiaría sola.
    /// 2. Un lobby SIN ping medido va siempre al final, ascendente o descendente. Tratar
    ///    "desconocido" como 0 lo pondría en cabeza al ordenar por mejor ping, que es justo la
    ///    mentira que el jugador va a cliquear.
    /// </summary>
    public sealed class LobbySort
    {
        public LobbySortKey Key = LobbySortKey.Ping;
        public bool Descending;

        public LobbySort() { }

        public LobbySort(LobbySortKey key, bool descending = false)
        {
            Key = key;
            Descending = descending;
        }

        /// <summary>Ordena la lista EN SITIO (ya es una lista derivada del filtro).</summary>
        public void Apply(List<Lobby> lobbies)
        {
            if (lobbies == null || lobbies.Count < 2) return;

            LobbySortKey key = Key;
            bool descending = Descending;
            lobbies.Sort((a, b) => Compare(a, b, key, descending));
        }

        public List<Lobby> Sorted(IEnumerable<Lobby> lobbies)
        {
            var copy = new List<Lobby>();
            if (lobbies != null)
            {
                foreach (Lobby lobby in lobbies)
                {
                    if (lobby != null) copy.Add(lobby);
                }
            }

            Apply(copy);
            return copy;
        }

        private static int Compare(Lobby a, Lobby b, LobbySortKey key, bool descending)
        {
            int result;
            switch (key)
            {
                case LobbySortKey.Players:
                    result = a.Players.CompareTo(b.Players);
                    break;
                case LobbySortKey.FreeSlots:
                    result = a.FreeSlots.CompareTo(b.FreeSlots);
                    break;
                case LobbySortKey.Ping:
                    return ComparePing(a, b, descending);
                case LobbySortKey.Map:
                    result = string.Compare(a.Map, b.Map, StringComparison.OrdinalIgnoreCase);
                    break;
                case LobbySortKey.Region:
                    result = string.Compare(a.Region, b.Region, StringComparison.OrdinalIgnoreCase);
                    break;
                case LobbySortKey.Version:
                    result = string.Compare(a.Version, b.Version, StringComparison.Ordinal);
                    break;
                default:
                    result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    break;
            }

            if (descending) result = -result;
            return result != 0 ? result : TieBreak(a, b);
        }

        /// El ping desconocido no participa en la dirección: se hunde siempre.
        private static int ComparePing(Lobby a, Lobby b, bool descending)
        {
            if (a.HasPing != b.HasPing) return a.HasPing ? -1 : 1;

            if (!a.HasPing) return TieBreak(a, b);

            int result = a.PingMs.CompareTo(b.PingMs);
            if (descending) result = -result;
            return result != 0 ? result : TieBreak(a, b);
        }

        private static int TieBreak(Lobby a, Lobby b) =>
            string.Compare(a.Id.Value, b.Id.Value, StringComparison.OrdinalIgnoreCase);
    }
}
