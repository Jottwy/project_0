using System.Collections;
using System.Collections.Generic;

namespace BackroomsSurvival.Lobbies
{
    /// <summary>
    /// Una FOTO de lo que el directorio anunciaba en un instante. Inmutable y con el sello de
    /// cuándo se tomó, porque el TTL de cada entrada se juzga contra un reloj, y mezclar "cuándo
    /// se anunció" con "cuándo lo miro" es como se consigue una lista que caduca sola en pausa.
    ///
    /// La lista es la unidad que la UI pinta: filtrar y ordenar producen OTRA lista, nunca
    /// mutan ésta. Así un refresh a medias no puede dejar la tabla con media lista vieja.
    /// </summary>
    public sealed class LobbyList : IEnumerable<Lobby>
    {
        public static readonly LobbyList Empty = new LobbyList(new List<Lobby>(0), 0d);

        private readonly List<Lobby> _items;

        /// Segundos Unix en que se tomó la foto.
        public readonly double CapturedAtUnix;

        private LobbyList(List<Lobby> items, double capturedAtUnix)
        {
            _items = items;
            CapturedAtUnix = capturedAtUnix;
        }

        public int Count => _items.Count;
        public bool IsEmpty => _items.Count == 0;
        public Lobby this[int index] => _items[index];
        public IReadOnlyList<Lobby> Items => _items;

        /// <summary>
        /// Construye la foto. Deduplica por id QUEDÁNDOSE CON EL ANUNCIO MÁS RECIENTE: dos
        /// entradas con el mismo id no son un servidor duplicado, son el mismo servidor
        /// anunciado dos veces, y quedarse con la primera dejaría contadores rancios en la
        /// tabla. Las entradas nulas se descartan en silencio: vienen de un parseo fallido y no
        /// hay nada que enseñar de ellas.
        /// </summary>
        public static LobbyList Create(IEnumerable<Lobby> lobbies, double capturedAtUnix)
        {
            var items = new List<Lobby>();
            if (lobbies == null) return new LobbyList(items, capturedAtUnix);

            var indexById = new Dictionary<LobbyId, int>();
            foreach (Lobby lobby in lobbies)
            {
                if (lobby == null || !lobby.Id.IsValid) continue;

                if (indexById.TryGetValue(lobby.Id, out int existing))
                {
                    if (lobby.UpdatedAtUnix >= items[existing].UpdatedAtUnix) items[existing] = lobby;
                    continue;
                }

                indexById[lobby.Id] = items.Count;
                items.Add(lobby);
            }

            return new LobbyList(items, capturedAtUnix);
        }

        public bool TryGet(LobbyId id, out Lobby lobby)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == id)
                {
                    lobby = _items[i];
                    return true;
                }
            }

            lobby = null;
            return false;
        }

        public bool Contains(LobbyId id) => TryGet(id, out _);

        /// <summary>
        /// La foto sin lo que ya caducó. NO es lo mismo que filtrar: el TTL no es una preferencia
        /// del jugador, es la diferencia entre un servidor y un recuerdo suyo. Por eso se aplica
        /// antes que <see cref="LobbyFilter"/> y no forma parte de él.
        /// </summary>
        public LobbyList WithoutExpired(double nowUnix)
        {
            var kept = new List<Lobby>(_items.Count);
            for (int i = 0; i < _items.Count; i++)
            {
                if (!_items[i].IsExpired(nowUnix)) kept.Add(_items[i]);
            }

            return kept.Count == _items.Count ? this : new LobbyList(kept, CapturedAtUnix);
        }

        public IEnumerator<Lobby> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }
}
