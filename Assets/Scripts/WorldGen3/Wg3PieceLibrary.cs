using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// El catálogo autorado de WG3: la lista ordenada de piezas que sustituye a
    /// <see cref="Wg3Catalog"/> cuando el autorado deja de venir de código.
    ///
    /// EL ORDEN ES EL ÍNDICE, y el índice es lo que viaja por el wire y lo que entra en el hash de
    /// colocación. Reordenar la lista NO reordena una interfaz: reescribe todos los mundos ya
    /// generados y descoloca cualquier partida guardada. Añadir al final es seguro; insertar en
    /// medio, borrar o intercambiar no lo es. Es la misma trampa que ya lleva anotada la primera
    /// pieza de <see cref="Wg3Catalog"/>, aquí con la diferencia de que ahora se arrastra con el
    /// ratón, que es mucho más fácil de hacer sin querer.
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/WorldGen3/Biblioteca de piezas",
        fileName = "Wg3PieceLibrary")]
    public sealed class Wg3PieceLibrary : ScriptableObject
    {
        /// <summary>Ruta bajo <c>Resources/</c>. Se carga sin cablear nada en la escena, mismo
        /// patrón que <c>RoomPool</c> y <c>GridPrefabSet</c>.</summary>
        public const string ResourcePath = "WorldGen3/Wg3PieceLibrary";

        public Wg3PieceAsset[] pieces = System.Array.Empty<Wg3PieceAsset>();

        public static Wg3PieceLibrary Load() => Resources.Load<Wg3PieceLibrary>(ResourcePath);

        /// <summary>
        /// El catálogo de colocación. Devuelve vacío si la biblioteca no está, está vacía o alguna
        /// pieza no ha pasado por el horno — y de eso se entera quien llama, no se sortea aquí:
        /// componer un mundo con media biblioteca daría un mundo que se ve bien y que NO coincide
        /// con el del servidor.
        /// </summary>
        public List<Wg3Piece> BuildCatalog()
        {
            var catalog = new List<Wg3Piece>(pieces.Length);
            foreach (Wg3PieceAsset asset in pieces)
            {
                if (asset == null || !asset.IsBaked) return new List<Wg3Piece>();
                catalog.Add(asset.ToPiece());
            }
            return catalog;
        }

        /// <summary>
        /// Motivos por los que esta biblioteca no puede exportarse. Vacío = se puede.
        ///
        /// REGLA R6 — una pieza sin chuleta firmada no existe. Un hueco, un id repetido o una pieza
        /// sin hornear no se saltan con un aviso: paran la exportación entera, porque un catálogo a
        /// medias desplaza todos los índices posteriores y el resultado no es "falta una pieza",
        /// es "el mundo entero es otro".
        /// </summary>
        public List<string> Validate()
        {
            var issues = new List<string>();
            if (pieces.Length == 0)
            {
                issues.Add("la biblioteca está vacía");
                return issues;
            }

            var seen = new HashSet<string>();
            for (int i = 0; i < pieces.Length; i++)
            {
                Wg3PieceAsset asset = pieces[i];
                if (asset == null)
                {
                    issues.Add($"hueco en el índice {i}: un slot vacío corre todos los índices " +
                               "siguientes y cambia el mundo");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(asset.pieceId))
                    issues.Add($"[{i}] {asset.name}: sin id");
                else if (!seen.Add(asset.pieceId))
                    issues.Add($"[{i}] id repetido «{asset.pieceId}»: el id entra en el hash de " +
                               "decisión, así que dos piezas con el mismo se pisan");
                if (!asset.IsBaked)
                    issues.Add($"[{i}] {asset.pieceId}: sin hornear, no tiene ni una caja");
                if (asset.sockets == null || asset.sockets.Length == 0)
                    issues.Add($"[{i}] {asset.pieceId}: sin bocas, no se puede conectar a nada");
                if (asset.sizeX <= 0f || asset.sizeZ <= 0f)
                    issues.Add($"[{i}] {asset.pieceId}: huella {asset.sizeX:0.00} × " +
                               $"{asset.sizeZ:0.00} m");
            }
            return issues;
        }
    }
}
