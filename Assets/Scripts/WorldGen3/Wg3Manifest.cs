using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Una boca vista desde el manifiesto. Claves en snake_case a propósito: son claves de JSON que
    /// leerá <c>serde</c> al otro lado, no identificadores de C# — mismo criterio que
    /// <c>RoomManifestExporter</c> y los espejos de <c>IPCMessages</c>.
    /// </summary>
    [Serializable]
    public sealed class Wg3ManifestSocket
    {
        public int side;
        public float offset;
        public float width;

        /// <summary>Discriminante de <see cref="Wg3SocketType"/>. Va como entero porque es un
        /// CONTRATO con Rust: cambiar el orden del enum cambia el mundo, así que tiene que doler
        /// al tocarlo y no pasar por un renombrado inocente de cadena.</summary>
        public int type;

        public float floor_y;
        public float ceiling_y;
    }

    /// <summary>
    /// Una caja de la chuleta de colisión, en coordenadas LOCALES de la pieza sin girar. El giro de
    /// colocación lo aplica quien la coloca — mandarla ya girada obligaría a exportar cuatro
    /// versiones de cada pieza.
    /// </summary>
    [Serializable]
    public sealed class Wg3ManifestVolume
    {
        public float cx, cy, cz;
        public float sx, sy, sz;
        public float yaw;

        /// <summary>Discriminante de <see cref="Wg3VolumeKind"/>. Rust no lo necesita para
        /// colisionar —todo lo que llega aquí bloquea— pero sí para distinguir suelo de pared al
        /// depurar, y para que un volcado del backend se pueda leer.</summary>
        public int kind;
    }

    /// <summary>Una pieza vista por el backend: huella, bocas, chuleta y lo que hace falta para
    /// SORTEARLA. Sin escala, peso y profundidad mínima el manifiesto describiría la pieza pero no
    /// permitiría colocarla, que es justo lo que tiene que permitir.</summary>
    [Serializable]
    public sealed class Wg3ManifestPiece
    {
        /// <summary>Índice en el catálogo. ES lo que viajará por el wire en F2: mandar la cadena
        /// costaría bytes por chunk para identificar algo que las dos partes ya tienen.</summary>
        public int index;

        /// <summary>Solo para leer el fichero y los logs. El backend no lo interpreta… salvo en el
        /// hash de decisión, donde entra el índice, no esto.</summary>
        public string id;

        /// <summary>La huella ES el bounds: el origen de una pieza es su esquina mínima, así que
        /// tamaño y caja envolvente son el mismo dato y exportar los dos sería invitar a que se
        /// contradigan.</summary>
        public float size_x;
        public float size_z;
        public float height_meters;

        public int scale;
        public float weight;
        public int min_depth;
        public bool dead_end;

        public Wg3ManifestSocket[] sockets;

        /// <summary>La chuleta: SOLO volúmenes sólidos.</summary>
        public Wg3ManifestVolume[] collision;
    }

    /// <summary>
    /// El manifiesto de WG3: lo único que el servidor sabrá nunca de la geometría.
    ///
    /// REGLA R1 — aquí no hay mallas, ni triángulos, ni materiales, ni normales. Unity hornea y
    /// Rust coloca. Si algún día hiciera falta que el backend "entendiera" una pieza más allá de
    /// esto, la arquitectura estaría mal.
    ///
    /// REGLA R25 — la DECORACIÓN NO SE EXPORTA. El rodapié existe en el cliente y el servidor no
    /// llega a saber que existe. Es la línea entre estructura y decoración cruzando la frontera de
    /// autoridad: lo que no bloquea, no viaja.
    /// </summary>
    [Serializable]
    public sealed class Wg3Manifest
    {
        /// <summary>Versión del FORMATO. Sube cuando cambie la forma del fichero, no cuando cambie
        /// el catálogo — para eso está el digest.</summary>
        public const int FormatVersion = 1;

        public int version = FormatVersion;

        /// <summary>
        /// SHA-256 en hex minúscula del JSON de <see cref="pieces"/>, tal cual lo escribió esta
        /// exportación.
        ///
        /// El backend lo trata como CADENA OPACA: lo compara y nada más. No lo recalcula, y eso es
        /// deliberado — recalcularlo obligaría a que C# y Rust coincidieran byte a byte en una
        /// forma canónica, que es la clase de duplicación que este proyecto ya paga cara.
        /// </summary>
        public string digest;

        public Wg3ManifestPiece[] pieces;

        /// <summary>Envoltorio solo para digerir el array: <c>JsonUtility</c> no serializa un array
        /// desnudo, y el digest tiene que cubrir las piezas y nada más.</summary>
        [Serializable]
        private sealed class PiecesOnly
        {
            public Wg3ManifestPiece[] pieces;
        }

        // ── horneado ────────────────────────────────────────────────────────────────────────

        public static Wg3Manifest FromCatalog(IReadOnlyList<Wg3Piece> catalog)
        {
            var pieces = new Wg3ManifestPiece[catalog.Count];
            for (int i = 0; i < catalog.Count; i++)
            {
                Wg3Piece p = catalog[i];

                var sockets = new Wg3ManifestSocket[p.sockets.Length];
                for (int s = 0; s < p.sockets.Length; s++)
                    sockets[s] = new Wg3ManifestSocket
                    {
                        side = p.sockets[s].side,
                        offset = p.sockets[s].offset,
                        width = p.sockets[s].width,
                        type = (int)p.sockets[s].type,
                        floor_y = p.sockets[s].floorY,
                        ceiling_y = p.sockets[s].ceilingY
                    };

                var collision = new List<Wg3ManifestVolume>();
                foreach (Wg3Volume v in Wg3Geometry.Build(p))
                {
                    if (!v.IsSolid) continue;
                    collision.Add(new Wg3ManifestVolume
                    {
                        cx = v.center.x, cy = v.center.y, cz = v.center.z,
                        sx = v.size.x, sy = v.size.y, sz = v.size.z,
                        yaw = v.yawDegrees,
                        kind = (int)v.kind
                    });
                }

                pieces[i] = new Wg3ManifestPiece
                {
                    index = i,
                    id = p.id,
                    size_x = p.sizeX,
                    size_z = p.sizeZ,
                    height_meters = p.heightMeters,
                    scale = (int)p.scale,
                    weight = p.weight,
                    min_depth = p.minDepth,
                    dead_end = p.isDeadEnd,
                    sockets = sockets,
                    collision = collision.ToArray()
                };
            }

            var manifest = new Wg3Manifest { version = FormatVersion, pieces = pieces };
            manifest.digest = Sha256Hex(JsonUtility.ToJson(new PiecesOnly { pieces = pieces }));
            return manifest;
        }

        public string ToJson(bool prettyPrint = true) => JsonUtility.ToJson(this, prettyPrint);

        public static Wg3Manifest FromJson(string json) => JsonUtility.FromJson<Wg3Manifest>(json);

        /// <summary>Recalcula el digest de estas piezas. NO se usa al leer —el digest es opaco—
        /// sino para poder afirmar en un test que exportar dos veces da lo mismo.</summary>
        public string RecomputeDigest() =>
            Sha256Hex(JsonUtility.ToJson(new PiecesOnly { pieces = pieces }));

        // ── vuelta ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reconstruye el catálogo de COLOCACIÓN a partir del manifiesto.
        ///
        /// Las piezas que salen no llevan columnas, bloques ni escaleras: su geometría ya está
        /// horneada en <see cref="Wg3ManifestPiece.collision"/> y no se vuelve a derivar. Eso es
        /// exactamente lo que podrá hacer Rust en F2, y por eso existe este método aquí y no en el
        /// exportador: es lo que permite AFIRMAR EN UN TEST que el manifiesto basta para colocar,
        /// en vez de suponerlo y descubrirlo al portarlo.
        /// </summary>
        public List<Wg3Piece> ToPlacementCatalog()
        {
            var catalog = new List<Wg3Piece>(pieces.Length);
            foreach (Wg3ManifestPiece mp in pieces)
            {
                var sockets = new Wg3Socket[mp.sockets.Length];
                for (int s = 0; s < mp.sockets.Length; s++)
                    sockets[s] = new Wg3Socket(mp.sockets[s].side, mp.sockets[s].offset,
                        mp.sockets[s].width, (Wg3SocketType)mp.sockets[s].type,
                        mp.sockets[s].floor_y, mp.sockets[s].ceiling_y);

                catalog.Add(new Wg3Piece
                {
                    id = mp.id,
                    geometryId = mp.id,
                    sizeX = mp.size_x,
                    sizeZ = mp.size_z,
                    heightMeters = mp.height_meters,
                    scale = (Wg3Scale)mp.scale,
                    weight = mp.weight,
                    minDepth = mp.min_depth,
                    isDeadEnd = mp.dead_end,
                    sockets = sockets
                });
            }
            return catalog;
        }

        /// <summary>Los volúmenes horneados de una pieza, ya en coordenadas de mundo para una
        /// colocación. Es el camino que en F2 seguirá el backend: leer cajas, no derivarlas.</summary>
        public static List<Wg3Volume> PlacedCollision(Wg3ManifestPiece piece, Wg3Placement placement)
        {
            var result = new List<Wg3Volume>(piece.collision.Length);
            float w = placement.piece.sizeX, d = placement.piece.sizeZ;
            int r = placement.rotation & 3;

            foreach (Wg3ManifestVolume v in piece.collision)
            {
                Vector2 p = RotateLocal(new Vector2(v.cx, v.cz), r, w, d);
                result.Add(new Wg3Volume
                {
                    center = new Vector3(placement.originX + p.x, v.cy, placement.originZ + p.y),
                    size = new Vector3(v.sx, v.sy, v.sz),
                    yawDegrees = v.yaw + r * 90f,
                    kind = (Wg3VolumeKind)v.kind
                });
            }
            return result;
        }

        private static Vector2 RotateLocal(Vector2 p, int r, float w, float d)
        {
            switch (r & 3)
            {
                case 0: return p;
                case 1: return new Vector2(p.y, w - p.x);
                case 2: return new Vector2(w - p.x, d - p.y);
                default: return new Vector2(d - p.y, p.x);
            }
        }

        private static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
