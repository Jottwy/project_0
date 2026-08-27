using System;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Una pieza AUTORADA de WG3, ya horneada.
    ///
    /// EL REPARTO DE PAPELES, que es lo que hay que entender de este fichero: el editor de salas
    /// (<c>Backrooms/Room Authoring Tool</c>) es el TABLERO DE DIBUJO, y este asset es el resultado
    /// del dibujo traducido al idioma de WG3 — metros, origen en esquina mínima, bocas tipadas y
    /// cajas. La unidad de 5 m del tablero se queda en el tablero: aquí no entra un tile.
    ///
    /// POR QUÉ NO SE AUTORA DIRECTAMENTE EN WG3: <see cref="RoomColliderBuilder"/> ya deriva del
    /// MODELO —no de la malla— suelo, techo, paredes con sus vanos, columnas, pozos, plataformas
    /// con altura por punto y escaleras, y lleva tiempo en producción. Rehacerlo en el namespace de
    /// WG3 por pureza costaría semanas y no daría una sola caja mejor.
    ///
    /// DÓNDE SE DEBILITA R2, dicho aquí y no escondido: en el catálogo de código, colisión y malla
    /// salen de LA MISMA LISTA de volúmenes, así que no pueden divergir. Una pieza autorada no
    /// puede sostener eso: su colisión son cajas exactas y su malla lleva detalle que NO debe costar
    /// colisión (molduras, salientes, rodapié). Vuelven a ser dos recorridos sobre el MISMO MODELO,
    /// que es R2 tal y como está escrita en el brief, pero es una garantía más floja que la del
    /// catálogo de código y conviene saberlo antes de perseguir un "veo puerta y me choco".
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/WorldGen3/Pieza autorada", fileName = "wg3_piece")]
    public sealed class Wg3PieceAsset : ScriptableObject
    {
        // ── lo que decide el autor ──────────────────────────────────────────────────────────

        /// <summary>Identificador estable. Entra en el hash de decisión del compositor, así que
        /// renombrarlo cambia todos los mundos ya generados.</summary>
        public string pieceId;

        public Wg3Scale scale = Wg3Scale.Medium;

        /// <summary>Peso base del sorteo. Ojo con el reparto: con el catálogo de arranque una sola
        /// pieza se llevaba el 27 % del mundo, y eso reduce un catálogo de 14 a 7,4 efectivas.
        /// La sonda que lo mide es <c>how_soon_the_same_piece_comes_round_again</c>.</summary>
        public float weight = 1f;

        public int minDepth;

        /// <summary>Callejón sin salida a propósito (L12). Además de ambientar, es lo que tapona su
        /// tipo de boca: sin un tapón por tipo el validador de catálogo protesta con razón.</summary>
        public bool isDeadEnd;

        // ── lo que sale del horno: NO se toca a mano ────────────────────────────────────────

        /// <summary>Huella en METROS, tomada de los límites reales del contorno exterior — no de
        /// <c>tilesX * 5</c>. Es la línea exacta por la que este camino no devuelve WG3 a la
        /// rejilla: el lienzo del editor mide en tiles, la pieza no.</summary>
        public float sizeX;
        public float sizeZ;
        public float heightMeters;

        /// <summary>Bocas, derivadas de los <c>WallHole</c> del modelo. El TIPO sale del ancho
        /// (2,4 m = pasillo, 5 m = vano), no de un campo aparte: un array paralelo a los agujeros se
        /// desincroniza en silencio en cuanto se reordena uno.</summary>
        public Wg3Socket[] sockets = Array.Empty<Wg3Socket>();

        /// <summary>La chuleta: cajas en coordenadas locales con la esquina mínima en (0,0).</summary>
        public Wg3Volume[] volumes = Array.Empty<Wg3Volume>();

        /// <summary>Malla visual horneada. Nula = el cliente dibuja las cajas de
        /// <see cref="volumes"/>. Que se pueda quedar vacía es deliberado: la pieza ya es jugable
        /// —y colisiona— antes de tener una sola moldura.</summary>
        public GameObject visualPrefab;

        /// <summary>Dónde cae el pivote del prefab en coordenadas de la pieza. Lo escribe el horno;
        /// tocarlo a mano descoloca la malla respecto a su colisión.</summary>
        public Vector2 visualPivot;

        /// <summary>
        /// EL MODELO del que salió esta pieza, para poder volver a abrirla y seguir tocándola.
        ///
        /// Viaja en el asset y por tanto en el build, aunque en runtime NADIE lo lee: lo mira el
        /// horneador y nada más. La alternativa —dejarlo fuera— convierte cada pieza horneada en un
        /// callejón sin salida donde mover una boca obliga a redibujarla entera, que es el mismo
        /// fallo que ya costó los <c>m_IsKinematic</c> del avatar remoto al re-hornear un prefab.
        /// </summary>
        public RoomDefinition sourceDefinition;

        public bool IsBaked => volumes != null && volumes.Length > 0;

        /// <summary>
        /// La pieza tal y como la ve el compositor. Sale SIN <c>blocks</c>, <c>pillars</c> ni
        /// <c>stairs</c>: su geometría ya está resuelta en <see cref="volumes"/>, y dejar los dos
        /// caminos vivos a la vez sería invitar a que se contradigan.
        /// </summary>
        public Wg3Piece ToPiece() => new Wg3Piece
        {
            id = pieceId,
            geometryId = pieceId,
            sizeX = sizeX,
            sizeZ = sizeZ,
            heightMeters = heightMeters,
            scale = scale,
            weight = weight,
            minDepth = minDepth,
            isDeadEnd = isDeadEnd,
            sockets = sockets ?? Array.Empty<Wg3Socket>(),
            bakedVolumes = volumes ?? Array.Empty<Wg3Volume>(),
            visualPrefab = visualPrefab,
            visualPivot = visualPivot
        };
    }
}
