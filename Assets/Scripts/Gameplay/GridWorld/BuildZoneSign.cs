using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Enmienda a ADR-081 — el cartel que dice que en esta sala SÍ se puede construir.
    ///
    /// EL PROBLEMA QUE RESUELVE: la zona construible no se distinguía de ninguna otra sala. El
    /// jugador solo podía enterarse probando a construir y viendo si le dejaban — descubrir una regla por prueba y error, que es la peor forma de enseñarla. Se eligió
    /// señalización dentro del mundo antes que un aviso en el HUD: un cartel de oficina laminado ES
    /// el vocabulario de los Backrooms, y además se ve desde el pasillo, cosa que un mensaje no.
    ///
    /// Desde la enmienda 5 cuelga DENTRO de la habitación construible, cuyo emplazamiento manda el
    /// backend con el chunk (`build_room`) — no se re-deriva aquí, y por eso todos los peers cuelgan
    /// el mismo cartel en el mismo sitio. Es DECORATIVO — sin collider, sin lógica: quien decide de verdad dónde se construye es el host
    /// (`position_is_buildable`), y este cartel solo cuenta lo que ese host ya va a hacer.
    ///
    /// TEXTO CON `TextMesh` LEGACY y la fuente incorporada de Unity, no TextMeshPro: TMP necesita un
    /// `TMP_Settings.defaultFontAsset` asignado en el proyecto y aquí no hay ninguno, así que el
    /// primer cartel escribiría un error por consola en vez de una letra. La fuente incorporada
    /// existe siempre y no se puede podar en un build.
    /// </summary>
    public static class BuildZoneSign
    {
        /// <summary>Dos por habitación, no uno: la sala tiene cuatro paredes y con un solo cartel es
        /// perfectamente posible entrar de espaldas a él. Cuatro ya sería decorado repetido.</summary>
        private const int SignsPerChunk = 2;

        private const float PlateWidth = 1.30f;
        private const float PlateHeight = 0.55f;
        private const float PlateThickness = 0.04f;

        /// <summary>Altura del centro del cartel. A la de los ojos, que es donde se cuelga un aviso
        /// de verdad y donde no lo tapa un mueble.</summary>
        private const float MountHeight = 1.75f;

        /// <summary>Separación de la cara de la pared. Media pared (0,1) más un poco: sin esto el
        /// cartel comparte plano con el panel y aparece el z-fighting a distancia.</summary>
        private const float WallClearance = GridVisualConstants.WallThickness * 0.5f + 0.03f;

        private const uint SignSaltTile = 0x5347544CU; // "SGTL" — qué tiles llevan cartel
        private const uint SignSaltEdge = 0x53474447U; // "SGDG" — en qué pared de ese tile

        private static readonly Color PlateColour = new Color(0.86f, 0.72f, 0.16f); // amarillo señal
        private static readonly Color TextColour = new Color(0.08f, 0.07f, 0.05f);

        /// <summary>Lo que pone. Dos líneas: la de arriba nombra el sitio, la de abajo da el permiso —
        /// que es como está redactado cualquier cartel de obra de verdad.</summary>
        private const string SignText = "ZONA DE OBRAS\nCONSTRUCCIÓN PERMITIDA";

        private static Font _font;

        /// <summary>
        /// Cuelga los carteles de la habitación de este chunk bajo <paramref name="parent"/>. No hace
        /// nada si el chunk no tiene habitación (`roomTileX < 0`), que es la inmensa mayoría.
        ///
        /// Se le pasa el MISMO <paramref name="walls"/> que usan props y escalera, y se aplican los
        /// mismos dos rechazos (tile macizo, tile con columna) para no colgar un cartel dentro de
        /// una pared.
        /// </summary>
        public static void Place(Transform parent, byte[,] walls, int roomTileX, int roomTileZ,
            int chunkX, int chunkZ)
        {
            if (roomTileX < 0 || walls == null)
                return;

            int tiles = GridChunkBuilder.TilesPerChunk;
            int placed = 0;

            // Barrido determinista, acotado a los 3 x 3 tiles de la habitación: el cartel cuelga
            // DENTRO, que es donde el jugador necesita leerlo.
            int lastX = Mathf.Min(roomTileX + GridChunkDataMsg.BuildRoomTiles, tiles);
            int lastZ = Mathf.Min(roomTileZ + GridChunkDataMsg.BuildRoomTiles, tiles);
            for (int tz = roomTileZ; tz < lastZ && placed < SignsPerChunk; tz++)
            {
                for (int tx = roomTileX; tx < lastX && placed < SignsPerChunk; tx++)
                {
                    byte b = walls[tx, tz];
                    byte edges = (byte)(b & 0x0F);

                    if (edges == 0x0F) continue;                                  // tile cerrado
                    if (edges == 0) continue;                                     // sin pared donde colgar
                    if ((b & GridChunkBuilder.PillarMask) != 0) continue;         // columna en el tile

                    // Sin sorteo: la habitación son 9 tiles, no 100, y el cartel es la señal de
                    // una REGLA. Un 6 % sobre 9 tiles dejaría a la mayoría de las salas sin cartel,
                    // que es justo el fallo que este cartel existe para arreglar. El orden de barrido
                    // es fijo, así que los dos primeros tiles con pared se lo llevan siempre.
                    int gx = chunkX * tiles + tx, gz = chunkZ * tiles + tz;

                    if (!TryPickEdge(edges, gx, gz, out float ox, out float oz, out float yaw))
                        continue;

                    Build(parent, new Vector3(
                            (tx + 0.5f) * GridVisualConstants.TileSize + ox * (GridVisualConstants.TileSize * 0.5f - WallClearance),
                            MountHeight,
                            (tz + 0.5f) * GridVisualConstants.TileSize + oz * (GridVisualConstants.TileSize * 0.5f - WallClearance)),
                        yaw);
                    placed++;
                }
            }
        }

        /// <summary>
        /// Elige una de las paredes que este tile tiene de verdad y devuelve hacia dónde mira el
        /// cartel. El offset es en unidades de medio tile con signo, y el yaw deja la CARA del
        /// cartel mirando hacia dentro del tile — que es donde está el jugador.
        /// </summary>
        private static bool TryPickEdge(byte edges, int gx, int gz,
            out float ox, out float oz, out float yaw)
        {
            ox = oz = yaw = 0f;

            // Mismo orden y mismo convenio que `WallEdgeTable` de GridChunkBuilder: South = −z,
            // North = +z, West = −x, East = +x. El cartel mira al contrario que su pared.
            System.Span<int> present = stackalloc int[4];
            int count = 0;
            if ((edges & GridChunkBuilder.EdgeSouth) != 0) present[count++] = 0;
            if ((edges & GridChunkBuilder.EdgeNorth) != 0) present[count++] = 1;
            if ((edges & GridChunkBuilder.EdgeWest) != 0) present[count++] = 2;
            if ((edges & GridChunkBuilder.EdgeEast) != 0) present[count++] = 3;
            if (count == 0)
                return false;

            int pick = present[Mathf.Clamp((int)(GridChunkBuilder.Hash01(gx, gz, SignSaltEdge) * count), 0, count - 1)];
            switch (pick)
            {
                case 0: ox = 0f; oz = -1f; yaw = 0f; break;    // pared sur, mira a +z
                case 1: ox = 0f; oz = 1f; yaw = 180f; break;   // pared norte, mira a −z
                case 2: ox = -1f; oz = 0f; yaw = 90f; break;   // pared oeste, mira a +x
                default: ox = 1f; oz = 0f; yaw = 270f; break;  // pared este, mira a −x
            }

            return true;
        }

        /// <summary>
        /// Construye un cartel suelto. Público para poder verificar la geometría en un test sin
        /// levantar un chunk entero, igual que <c>OfficeStairs.Build</c>.
        /// </summary>
        public static GameObject Build(Transform parent, Vector3 localPos, float yaw)
        {
            var root = new GameObject("BuildZoneSign");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = localPos;
            root.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            // La chapa. SIN COLLIDER a propósito: es decorado, y un collider a la altura de los ojos
            // en la única sala donde el jugador va a estar construyendo sería un estorbo constante.
            var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = "Plate";
            plate.transform.SetParent(root.transform, false);
            plate.transform.localScale = new Vector3(PlateWidth, PlateHeight, PlateThickness);
            // `Destroy` NO destruye en modo edición: escribe "Destroy may not be called from edit
            // mode!" y sigue, así que un test EditMode que construyera un cartel se pondría rojo por
            // el error y encima seguiría viendo el collider. Ya se comió tres tests de SprayStacking.
            var plateCollider = plate.GetComponent<Collider>();
            if (Application.isPlaying)
                Object.Destroy(plateCollider);
            else
                Object.DestroyImmediate(plateCollider);
            plate.GetComponent<MeshRenderer>().sharedMaterial = MaterialHelper.MakeLit(PlateColour);

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(root.transform, false);
            // Delante de la chapa por su media profundidad más un pelo, y girado para que el texto
            // se lea desde el mismo lado al que mira la cara del cartel.
            textGo.transform.localPosition = new Vector3(0f, 0f, -(PlateThickness * 0.5f + 0.005f));
            textGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            var text = textGo.AddComponent<TextMesh>();
            text.text = SignText;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = TextColour;
            // Tamaño grande + escala pequeña, no fuente pequeña: TextMesh rasteriza al `fontSize`, así
            // que subirlo y encoger el transform es lo que da un borde limpio de cerca en vez de un
            // texto pastoso.
            text.fontSize = 64;
            text.characterSize = 0.055f;
            textGo.transform.localScale = Vector3.one;

            var font = ResolveFont();
            if (font != null)
            {
                text.font = font;
                textGo.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }

            return root;
        }

        /// <summary>
        /// La fuente incorporada de Unity. Cambió de nombre en 2022.2 (`Arial.ttf` →
        /// `LegacyRuntime.ttf`) y este proyecto va en Unity 6, pero se prueban las dos: pedir un
        /// recurso incorporado que no existe devuelve null en silencio, y un cartel mudo no diría
        /// por qué.
        /// </summary>
        private static Font ResolveFont()
        {
            if (_font != null)
                return _font;

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                    ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

            if (_font == null)
                Debug.LogWarning("[BuildZoneSign] Sin fuente incorporada: los carteles saldrán en blanco.");

            return _font;
        }
    }
}
