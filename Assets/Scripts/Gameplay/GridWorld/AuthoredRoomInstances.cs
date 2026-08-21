using System;
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// ADR-084 punto 5 — el prefab de una sala autorada vive FUERA del ciclo de vida del chunk.
    ///
    /// Hasta wire 40 la sala se instanciaba bajo el root de su chunk, y valía porque cabía entera
    /// dentro de él. Una sala repartida entre hasta cuatro chunks ya no: <c>ProceduralWorldGenerator</c>
    /// hace <c>Destroy(go)</c> sobre el root al descargar, así que descargar el chunk ancla con el
    /// jugador dentro de la sala **borraría media sala bajo sus pies**. Y <c>RebuildChunk</c>, que
    /// corre cuando por fin llega la zona, la destruiría y la volvería a crear por cada chunk.
    ///
    /// Aquí la sala pasa a un root de mundo con **refcount por chunk cargado que la referencia**: se
    /// instancia con el primero y se destruye con el último. Los cuatro chunks la piden por igual;
    /// ninguno la posee.
    ///
    /// **La identidad es <c>(ancla, tile de ancla)</c>**, que es exactamente lo que el wire 41 manda
    /// y lo mismo en los cuatro chunks. Dos salas del mismo ancla no pueden compartir tile de origen
    /// —sus reservas se pisarían—, así que la pareja identifica sin ambigüedad y no hace falta un id
    /// aparte.
    ///
    /// Rechazado dejar el prefab bajo el root del ancla y no descargar nunca ese chunk (alternativa
    /// (B) del ADR): ata la retención de chunks al contenido y filtra memoria en un mundo
    /// persistente.
    /// </summary>
    public static class AuthoredRoomInstances
    {
        /// <summary>Qué sala es, con el mismo criterio en todos los chunks que la ven.</summary>
        public readonly struct Key : IEquatable<Key>
        {
            public readonly int AnchorCx;
            public readonly int AnchorCz;
            public readonly int TileX;
            public readonly int TileZ;

            public Key(int anchorCx, int anchorCz, int tileX, int tileZ)
            {
                AnchorCx = anchorCx;
                AnchorCz = anchorCz;
                TileX = tileX;
                TileZ = tileZ;
            }

            public bool Equals(Key o) =>
                AnchorCx == o.AnchorCx && AnchorCz == o.AnchorCz && TileX == o.TileX && TileZ == o.TileZ;

            public override bool Equals(object o) => o is Key k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = AnchorCx;
                    h = h * 397 ^ AnchorCz;
                    h = h * 397 ^ TileX;
                    return h * 397 ^ TileZ;
                }
            }

            public override string ToString() => $"({AnchorCx},{AnchorCz})+({TileX},{TileZ})";
        }

        private sealed class Entry
        {
            public GameObject go;
            public int refs;
            /// <summary>
            /// Si el tinte que lleva puesto es el del chunk ANCLA. ADR-084 punto 7: la sala se talla
            /// entera con las reglas del ancla, así que su tinte también sale de ahí. El primero que
            /// llega la instancia con el suyo —puede ser un chunk invadido—, y cuando el ancla se
            /// construye se re-tinta. Sin esto, el color de la sala dependería de por dónde entrase
            /// el jugador.
            /// </summary>
            public bool tintedByAnchor;
        }

        private static readonly Dictionary<Key, Entry> _live = new Dictionary<Key, Entry>();

        /// <summary>Qué salas pidió cada chunk cargado, para poder soltarlas al descargarlo.</summary>
        private static readonly Dictionary<(int cx, int cz, int layer), List<Key>> _byChunk =
            new Dictionary<(int, int, int), List<Key>>();

        private static Transform _root;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _live.Clear();
            _byChunk.Clear();
            _root = null;
        }

        /// <summary>Cuántas salas hay instanciadas ahora mismo. Diagnóstico y tests.</summary>
        public static int LiveRoomCount => _live.Count;

        /// <summary>Cuántos chunks referencian la sala <paramref name="key"/>, 0 si no está viva.</summary>
        public static int RefCount(Key key) => _live.TryGetValue(key, out var e) ? e.refs : 0;

        /// <summary>
        /// El chunk <paramref name="chunk"/> necesita la sala <paramref name="key"/>. La instancia si
        /// es el primero en pedirla; si no, solo suma una referencia.
        ///
        /// <paramref name="worldCenter"/> va en coordenadas de MUNDO y no del chunk, que es lo único
        /// que tiene sentido para algo que no cuelga de ningún chunk: los cuatro que pueden pedirla
        /// tienen orígenes distintos y la sala está en un solo sitio.
        /// </summary>
        public static void Acquire((int cx, int cz, int layer) chunk, Key key, GameObject prefab,
            Vector3 worldCenter, float yaw, Color zoneTint, bool isAnchorChunk)
        {
            if (prefab == null)
                return;

            if (!_byChunk.TryGetValue(chunk, out var mine))
            {
                mine = new List<Key>(1);
                _byChunk[chunk] = mine;
            }
            // Un chunk no puede referenciar dos veces la misma sala. Pasa de verdad: RebuildChunk
            // construye el root nuevo ANTES de destruir el viejo (a propósito — ver su comentario),
            // así que entre las dos llamadas este chunk existe dos veces. Sin esta guarda el
            // refcount subiría a 2 y el Release del root viejo lo dejaría en 1 para siempre: la
            // sala nunca se destruiría.
            if (mine.Contains(key))
                return;
            mine.Add(key);

            if (_live.TryGetValue(key, out var entry) && entry.go != null)
            {
                entry.refs++;
                if (isAnchorChunk && !entry.tintedByAnchor)
                {
                    GridChunkBuilder.TintAuthoredRoom(entry.go, zoneTint);
                    entry.tintedByAnchor = true;
                }
                return;
            }

            var go = UnityEngine.Object.Instantiate(prefab, Root());
            go.transform.position = worldCenter;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.name = $"AuthoredRoom_{key}";
            GridChunkBuilder.TintAuthoredRoom(go, zoneTint);
            _live[key] = new Entry { go = go, refs = 1, tintedByAnchor = isAnchorChunk };
        }

        /// <summary>
        /// El chunk se descarga: suelta todas sus salas y destruye las que se queden sin nadie.
        ///
        /// Lo llama <see cref="ProceduralWorldGenerator"/> justo donde destruye el root del chunk. Un
        /// chunk que nunca pidió ninguna sala sale por el primer <c>TryGetValue</c>, que es el caso
        /// del 99 % de los chunks.
        /// </summary>
        public static void ReleaseChunk((int cx, int cz, int layer) chunk)
        {
            if (!_byChunk.TryGetValue(chunk, out var mine))
                return;
            _byChunk.Remove(chunk);

            for (int i = 0; i < mine.Count; i++)
            {
                var key = mine[i];
                if (!_live.TryGetValue(key, out var entry))
                    continue;
                entry.refs--;
                if (entry.refs > 0)
                    continue;
                DestroyRoom(entry.go);
                _live.Remove(key);
            }
        }

        /// <summary>
        /// Se lleva todo por delante: cambio de escena, teardown del generador o conexión a otro
        /// mundo. Sin esto, reconectar a otra seed dejaría las salas del mundo anterior flotando —
        /// el mismo fallo que <see cref="AuthoredRoomRegistry.ResetForNewConnection"/> arregla para
        /// los planes.
        /// </summary>
        public static void ClearAll()
        {
            foreach (var entry in _live.Values)
            {
                DestroyRoom(entry.go);
            }
            _live.Clear();
            _byChunk.Clear();
            if (_root != null)
                DestroyRoom(_root.gameObject);
            _root = null;
        }

        /// <summary>
        /// Destruye de verdad, también fuera de play mode.
        ///
        /// <c>Object.Destroy</c> en modo edición no destruye: registra un error
        /// (<i>Destroy may not be called from edit mode</i>) y deja el objeto vivo. Sin este desvío
        /// el refcount NO se podría probar en EditMode, que es la única forma de verificarlo sin
        /// arrancar el juego entero — y este es código cuyo fallo es "media sala borrada bajo los
        /// pies del jugador" o "salas filtradas para siempre".
        /// </summary>
        private static void DestroyRoom(GameObject go)
        {
            if (go == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(go);
            else
                UnityEngine.Object.DestroyImmediate(go);
        }

        /// <summary>
        /// El root de mundo, creado al colgar la primera sala.
        ///
        /// Sin padre a propósito: el sentido de este registro es sobrevivir a la destrucción de los
        /// roots de chunk, y colgarlo de uno los volvería a atar. Y por eso las salas se colocan en
        /// coordenadas de mundo — este transform no se mueve nunca.
        /// </summary>
        private static Transform Root()
        {
            if (_root == null)
                _root = new GameObject("AuthoredRooms").transform;
            return _root;
        }
    }
}
