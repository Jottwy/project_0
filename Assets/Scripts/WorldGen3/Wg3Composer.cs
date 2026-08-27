using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>Perillas de composición. Separadas del algoritmo porque son los números que se
    /// tocan al mirar el mundo, y ninguno debería exigir recompilar la cabeza.</summary>
    [System.Serializable]
    public sealed class Wg3ComposerSettings
    {
        /// <summary>Tope de piezas. En F0 acota la escena; bajo A1 lo sustituirá el chunk.</summary>
        public int budget = 30;

        /// <summary>REGLA L21 — probabilidad de NO usar una boca aunque haya candidata. Es lo que
        /// produce paredes ciegas y espacios residuales. A 0 el mundo se ramifica hasta llenar el
        /// presupuesto y se lee como un árbol; a 0,5 se ahoga enseguida.</summary>
        public float deliberateCapChance = 0.17f;

        /// <summary>Piezas colocadas antes de permitir tapones voluntarios. Sin esto la semilla
        /// puede sellarse a sí misma y el mundo son dos piezas.</summary>
        public int capGraceCount = 3;

        /// <summary>Multiplicador cuando la clase de escala de la pieza es la que pide el campo.</summary>
        public float scaleExactBonus = 4.2f;

        /// <summary>Multiplicador a una clase de distancia (estrecha↔media, media↔grande…).</summary>
        public float scaleNearBonus = 1.0f;

        /// <summary>Multiplicador a dos o más clases. No es cero a propósito: un salto brusco de
        /// escala de vez en cuando es deseable (L9, L10), solo tiene que ser raro.</summary>
        public float scaleFarBonus = 0.22f;

        /// <summary>REGLA R26 — penalización si la candidata repite la pieza a la que se engancha.</summary>
        public float repeatParentPenalty = 0.18f;

        /// <summary>Penalización si repite la de dos pasos atrás. Más suave: A-B-A cansa menos que
        /// A-A, y prohibirlo del todo empobrece el catálogo pequeño.</summary>
        public float repeatGrandparentPenalty = 0.45f;
    }

    /// <summary>
    /// El compositor: convierte semilla + catálogo en una lista de piezas colocadas.
    ///
    /// NO HAY GEOMETRÍA AQUÍ. Trabaja solo con huellas y bocas, que es exactamente lo que sabrá
    /// Rust en F2. Por eso se puede testear sin escena, sin backend y sin abrir Unity.
    ///
    /// LO QUE ESTE FICHERO NO RESUELVE, y conviene tenerlo delante: es un recorrido incremental
    /// desde una semilla, o sea la ruta A3 del brief (mundo finito). Vale para F0–F3, que ocurren
    /// en una región acotada. Lo que hace que la migración a A1 (contrato de frontera) NO sea una
    /// reescritura es que <b>ninguna decisión depende del orden de proceso</b>: cada sorteo abre su
    /// flujo a partir de la POSICIÓN de la boca (<see cref="Wg3Hash.StreamAt"/>), y el campo de
    /// escala es función pura de la posición. Un chunk que solo vea su vecindad llegará a la misma
    /// respuesta. Lo único atado al recorrido es <see cref="Wg3Placement.depth"/>, y el brief ya
    /// anota su sustituto: distancia a un ancla.
    /// </summary>
    public static class Wg3Composer
    {
        private const uint SaltCap = 0xC0DEC0DEu;
        private const uint SaltPick = 0x0F1CE5EDu;

        private struct Candidate
        {
            public Wg3Piece piece;
            public int socketIndex;
            public int rotation;
            public float originX;
            public float originZ;
            public float weight;
        }

        public static Wg3World Compose(int worldSeed, IReadOnlyList<Wg3Piece> catalog,
            Wg3ComposerSettings settings = null)
        {
            settings = settings ?? new Wg3ComposerSettings();
            var world = new Wg3World { worldSeed = worldSeed };
            if (catalog == null || catalog.Count == 0) return world;

            // Semilla: la primera pieza del catálogo, centrada en el origen del mundo. Elegirla
            // por sorteo haría que cambiar el catálogo moviera mundos ya generados.
            Wg3Piece seedPiece = catalog[0];
            Place(world, seedPiece, 0, -seedPiece.sizeX * 0.5f, -seedPiece.sizeZ * 0.5f, 0, -1);

            var frontier = new List<(int placement, int socket)>();
            PushSockets(frontier, world, 0);

            var candidates = new List<Candidate>(64);
            int cursor = 0;

            while (cursor < frontier.Count && world.placements.Count < settings.budget)
            {
                (int pi, int si) = frontier[cursor++];
                Wg3Placement parent = world.placements[pi];
                if (parent.socketState[si] != Wg3World.SocketOpen) continue;

                Wg3Socket parentSocket = parent.piece.sockets[si];
                Vector2 point = parent.WorldPoint(si);
                int parentWorldSide = parent.WorldSide(si);
                int neededSide = Wg3Piece.OppositeSide(parentWorldSide);

                // L21 — a veces la boca se sella aunque hubiera con qué seguir.
                if (world.placements.Count > settings.capGraceCount && settings.deliberateCapChance > 0f)
                {
                    var capStream = Wg3Hash.StreamAt(worldSeed, point.x, point.y, SaltCap);
                    if (capStream.Next01() < settings.deliberateCapChance)
                    {
                        if (!SealMouth(world, catalog, parent, pi, si, point, parentWorldSide, parentSocket))
                            Cap(world, parent, si, point, parentWorldSide, parentSocket, forced: false);
                        continue;
                    }
                }

                CollectCandidates(world, catalog, settings, parent, parentSocket, point, neededSide, candidates);

                if (candidates.Count == 0)
                {
                    if (!SealMouth(world, catalog, parent, pi, si, point, parentWorldSide, parentSocket))
                    {
                        Cap(world, parent, si, point, parentWorldSide, parentSocket, forced: true);
                        world.forcedCaps++;
                    }
                    continue;
                }

                var pickStream = Wg3Hash.StreamAt(worldSeed, point.x, point.y, SaltPick);
                Candidate chosen = WeightedPick(candidates, ref pickStream);

                int childIndex = Place(world, chosen.piece, chosen.rotation,
                    chosen.originX, chosen.originZ, parent.depth + 1, pi);
                parent.socketState[si] = Wg3World.SocketConnected;
                world.placements[childIndex].socketState[chosen.socketIndex] = Wg3World.SocketConnected;
                PushSockets(frontier, world, childIndex);
            }

            // Presupuesto agotado o frontera sin recorrer: todo lo que quede abierto se sella. Sin
            // esta pasada el mundo termina en bocas que dan a la nada.
            for (int i = 0; i < world.placements.Count; i++)
            {
                Wg3Placement p = world.placements[i];
                for (int s = 0; s < p.socketState.Length; s++)
                {
                    if (p.socketState[s] != Wg3World.SocketOpen) continue;
                    if (!SealMouth(world, catalog, p, i, s, p.WorldPoint(s), p.WorldSide(s),
                            p.piece.sockets[s]))
                    {
                        Cap(world, p, s, p.WorldPoint(s), p.WorldSide(s), p.piece.sockets[s],
                            forced: true);
                        world.forcedCaps++;
                    }
                }
            }
            return world;
        }

        /// <summary>
        /// Sella una boca CON GEOMETRÍA, y solo apunta una ficha si no hay con qué.
        ///
        /// EL FALLO QUE ARREGLA: <see cref="Cap"/> marcaba el socket y añadía un registro que no
        /// consumía NADIE — ni el ráster de colisión, ni el wire, ni el cliente. La boca quedaba
        /// abierta con el vacío detrás, y el jugador se caía del mundo. Medido antes de arreglarlo:
        /// una de cada seis bocas del mundo servido no tenía suelo al otro lado.
        ///
        /// LA REGLA DE ELECCIÓN, y tiene que ser idéntica en Rust o el oráculo se pone rojo: entre
        /// las piezas de UNA SOLA boca que casan con ésta y CABEN, la de menor huella; a igualdad,
        /// la de menor índice. Menor huella porque un tapón grande choca contra lo ya colocado y
        /// deja el agujero justo donde hacía falta cerrarlo.
        ///
        /// Devuelve false si no cupo ninguna. Eso sigue dejando un agujero, y por eso la sonda
        /// `probe_open_mouths_in_the_served_world` los cuenta en vez de dar el problema por cerrado.
        /// </summary>
        private static bool SealMouth(Wg3World world, IReadOnlyList<Wg3Piece> catalog,
            Wg3Placement parent, int parentIndex, int socketIndex, Vector2 point,
            int parentWorldSide, in Wg3Socket parentSocket)
        {
            int neededSide = Wg3Piece.OppositeSide(parentWorldSide);

            Wg3Piece best = null;
            int bestSocket = -1, bestRotation = 0;
            float bestOx = 0f, bestOz = 0f, bestArea = float.MaxValue;

            for (int c = 0; c < catalog.Count; c++)
            {
                Wg3Piece piece = catalog[c];
                if (piece?.sockets == null || piece.sockets.Length != 1) continue;

                Wg3Socket socket = piece.sockets[0];
                if (socket.type != parentSocket.type) continue;
                if (!Wg3Validator.ValidateConnection(parentSocket, socket, out _)) continue;

                int rotation = ((neededSide - socket.side) % 4 + 4) % 4;
                float w = (rotation % 2 == 0) ? piece.sizeX : piece.sizeZ;
                float d = (rotation % 2 == 0) ? piece.sizeZ : piece.sizeX;
                Vector2 local = Wg3Piece.LocalPoint(neededSide, socket.offset, w, d);
                float ox = point.x - local.x;
                float oz = point.y - local.y;

                if (OverlapsAny(world, ox, oz, w, d)) continue;

                float area = w * d;
                if (area >= bestArea) continue;
                best = piece; bestSocket = 0; bestRotation = rotation;
                bestOx = ox; bestOz = oz; bestArea = area;
            }

            if (best == null) return false;

            int childIndex = Place(world, best, bestRotation, bestOx, bestOz,
                parent.depth + 1, parentIndex);
            parent.socketState[socketIndex] = Wg3World.SocketConnected;
            world.placements[childIndex].socketState[bestSocket] = Wg3World.SocketConnected;
            return true;
        }

        private static void CollectCandidates(Wg3World world, IReadOnlyList<Wg3Piece> catalog,
            Wg3ComposerSettings settings, Wg3Placement parent, in Wg3Socket parentSocket,
            Vector2 point, int neededSide, List<Candidate> into)
        {
            into.Clear();
            int childDepth = parent.depth + 1;
            string parentId = parent.piece.id;
            string grandparentId = parent.parentIndex >= 0
                ? world.placements[parent.parentIndex].piece.id : null;

            for (int c = 0; c < catalog.Count; c++)
            {
                Wg3Piece piece = catalog[c];
                if (piece?.sockets == null || childDepth < piece.minDepth) continue;

                for (int s = 0; s < piece.sockets.Length; s++)
                {
                    Wg3Socket socket = piece.sockets[s];

                    // El tipo distinto es lo normal y no se cuenta: sería contar todo el catálogo
                    // en cada boca. Lo que interesa medir es la boca que CASI casa —mismo tipo,
                    // otra anchura o cota— porque eso delata que falta una transición (L6/L7).
                    if (socket.type != parentSocket.type) continue;
                    if (!Wg3Validator.ValidateConnection(parentSocket, socket, out _))
                    {
                        world.rejectedByValidator++;
                        continue;
                    }

                    // El giro NO se busca: queda determinado. La boca hija tiene que acabar
                    // mirando al lado `neededSide`, y girar suma al lado sin tocar el offset
                    // (ver el contrato de parametrización en Wg3Socket), así que hay exactamente
                    // una rotación válida por boca candidata. Probar las cuatro sería tirar tres.
                    int rotation = ((neededSide - socket.side) % 4 + 4) % 4;

                    float w = (rotation % 2 == 0) ? piece.sizeX : piece.sizeZ;
                    float d = (rotation % 2 == 0) ? piece.sizeZ : piece.sizeX;
                    Vector2 local = Wg3Piece.LocalPoint(neededSide, socket.offset, w, d);
                    float ox = point.x - local.x;
                    float oz = point.y - local.y;

                    if (OverlapsAny(world, ox, oz, w, d)) { world.rejectedByOverlap++; continue; }

                    into.Add(new Candidate
                    {
                        piece = piece,
                        socketIndex = s,
                        rotation = rotation,
                        originX = ox,
                        originZ = oz,
                        weight = Weigh(world.worldSeed, settings, piece, ox + w * 0.5f, oz + d * 0.5f,
                            parentId, grandparentId)
                    });
                }
            }
        }

        /// <summary>Peso de una candidata: base × campo de escala × penalización de repetición.
        /// El campo se lee en el CENTRO de donde caería la pieza, no en la boca — una nave de 40 m
        /// enganchada al borde de una zona estrecha pertenece a donde va su masa.</summary>
        private static float Weigh(int worldSeed, Wg3ComposerSettings settings, Wg3Piece piece,
            float centreX, float centreZ, string parentId, string grandparentId)
        {
            float w = piece.weight;

            Wg3Scale target = Wg3ScaleField.ScaleAt(worldSeed, centreX, centreZ);
            int distance = Mathf.Abs((int)piece.scale - (int)target);
            if (distance == 0) w *= settings.scaleExactBonus;
            else if (distance == 1) w *= settings.scaleNearBonus;
            else w *= settings.scaleFarBonus;

            if (piece.id == parentId) w *= settings.repeatParentPenalty;
            else if (piece.id == grandparentId) w *= settings.repeatGrandparentPenalty;

            return Mathf.Max(w, 1e-6f);
        }

        private static Candidate WeightedPick(List<Candidate> candidates, ref Wg3Hash.Stream stream)
        {
            float total = 0f;
            for (int i = 0; i < candidates.Count; i++) total += candidates[i].weight;

            float roll = stream.Next01() * total;
            float acc = 0f;
            for (int i = 0; i < candidates.Count; i++)
            {
                acc += candidates[i].weight;
                if (roll <= acc) return candidates[i];
            }
            // Solo alcanzable por acumulación de error en coma flotante.
            return candidates[candidates.Count - 1];
        }

        private static bool OverlapsAny(Wg3World world, float x, float z, float w, float d)
        {
            for (int i = 0; i < world.placements.Count; i++)
                if (world.placements[i].Overlaps(x, z, w, d)) return true;
            return false;
        }

        private static int Place(Wg3World world, Wg3Piece piece, int rotation,
            float originX, float originZ, int depth, int parentIndex)
        {
            world.placements.Add(new Wg3Placement
            {
                piece = piece,
                rotation = rotation,
                originX = originX,
                originZ = originZ,
                depth = depth,
                parentIndex = parentIndex,
                socketState = new byte[piece.sockets.Length]
            });
            return world.placements.Count - 1;
        }

        private static void PushSockets(List<(int, int)> frontier, Wg3World world, int placementIndex)
        {
            Wg3Placement p = world.placements[placementIndex];
            for (int s = 0; s < p.socketState.Length; s++)
                if (p.socketState[s] == Wg3World.SocketOpen)
                    frontier.Add((placementIndex, s));
        }

        private static void Cap(Wg3World world, Wg3Placement placement, int socketIndex,
            Vector2 point, int worldSide, in Wg3Socket socket, bool forced)
        {
            placement.socketState[socketIndex] = Wg3World.SocketCapped;
            world.caps.Add(new Wg3Cap
            {
                point = point,
                side = worldSide,
                width = socket.width,
                type = socket.type,
                forced = forced
            });
        }
    }
}
