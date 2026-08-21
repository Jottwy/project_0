#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Estancias dentro de una sala: declarar la partición en tiles y que salgan los tabiques, con
    /// vano donde dos estancias se tocan. Es la forma de autorar un COMPLEJO — varios espacios
    /// comunicados — sin salirse de una sola sala.
    ///
    /// Por qué dentro de una sala y no juntando varias: el emplazamiento exige un tile macizo entre
    /// dos reservas (dos salas pegadas nacen selladas la una de la otra), así que dos salas nunca
    /// comparten pared. Una sola sala, en cambio, llega a 16 × 16 tiles y sus pisos ya dan la
    /// verticalidad — el complejo cabe entero dentro de ella.
    ///
    /// Lo que sale son <see cref="RoomDefinition.Block"/> normales: después se mueven, se borran y
    /// se les cambia el vano uno a uno, igual que si se hubieran puesto a mano.
    /// </summary>
    public sealed partial class RoomAuthoringWindow
    {
        private FeatureGroup<RoomDefinition.SubRoom> SubRoomsGroup() =>
            new FeatureGroup<RoomDefinition.SubRoom>
            {
                key = "subrooms",
                title = "Sub-rooms",
                accentColor = new Color(0.45f, 0.7f, 0.8f),
                get = () => _def.subRooms,
                set = a => _def.subRooms = a,
                level = s => s.level,
                setLevel = (s, v) => s.level = v,
                make = () => new RoomDefinition.SubRoom
                {
                    tilesX = Mathf.Max(1, _def.tilesX / 2),
                    tilesZ = Mathf.Max(1, _def.tilesZ / 2),
                },
                row = (s, i) => $"#{i}   {s.label}   ·   {s.tilesX}×{s.tilesZ} tiles at ({s.tileX}, {s.tileZ})",
                fields = new[]
                {
                    new GroupField<RoomDefinition.SubRoom>("Tile X", s => s.tileX,
                        (s, v) => s.tileX = Mathf.Max(0, Mathf.RoundToInt(v))),
                    new GroupField<RoomDefinition.SubRoom>("Tile Z", s => s.tileZ,
                        (s, v) => s.tileZ = Mathf.Max(0, Mathf.RoundToInt(v))),
                    new GroupField<RoomDefinition.SubRoom>("Width (tiles)", s => s.tilesX,
                        (s, v) => s.tilesX = Mathf.Max(1, Mathf.RoundToInt(v))),
                    new GroupField<RoomDefinition.SubRoom>("Depth (tiles)", s => s.tilesZ,
                        (s, v) => s.tilesZ = Mathf.Max(1, Mathf.RoundToInt(v))),
                    new GroupField<RoomDefinition.SubRoom>("Door width (m)", s => s.doorWidth,
                        (s, v) => s.doorWidth = Mathf.Max(0f, v)),
                },
                rows = new[]
                {
                    RowText<RoomDefinition.SubRoom>("Label", s => s.label, (s, v) => s.label = v,
                        "Solo para reconocerla en la lista."),
                    RowIntSlider<RoomDefinition.SubRoom>("Tile X", s => s.tileX,
                        (s, v) => s.tileX = v, 0, Mathf.Max(0, _def.tilesX - 1)),
                    RowIntSlider<RoomDefinition.SubRoom>("Tile Z", s => s.tileZ,
                        (s, v) => s.tileZ = v, 0, Mathf.Max(0, _def.tilesZ - 1)),
                    RowIntSlider<RoomDefinition.SubRoom>("Width (tiles)", s => s.tilesX,
                        (s, v) => s.tilesX = v, 1, Mathf.Max(1, _def.tilesX)),
                    RowIntSlider<RoomDefinition.SubRoom>("Depth (tiles)", s => s.tilesZ,
                        (s, v) => s.tilesZ = v, 1, Mathf.Max(1, _def.tilesZ)),
                    RowFloat<RoomDefinition.SubRoom>("Wall height (m)", s => s.wallHeight,
                        (s, v) => s.wallHeight = Mathf.Max(0f, v), "0 = hasta el techo de su piso."),
                    RowFloat<RoomDefinition.SubRoom>("Door width (m)", s => s.doorWidth,
                        (s, v) => s.doorWidth = Mathf.Max(0f, v),
                        "Vano donde toca a otra estancia. 0 = tabique macizo, sin paso."),
                },
            };

        /// <summary>Los tabiques del piso, a partir de las estancias declaradas en él.</summary>
        private void DrawStoreySubRoomsRow(int storey)
        {
            int declared = 0;
            foreach (var s in _def.subRooms)
                if (s != null && StoreyOf(s.level) == storey) declared++;
            if (declared == 0) return;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField($"{declared} sub-room(s) declared", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Build walls",
                        "Levanta los tabiques entre estancias de este piso, con vano donde dos se "
                        + "tocan. Rehace los bloques de ESTE piso; los demás no se tocan."),
                        EditorStyles.miniButton, GUILayout.Width(90)))
                    Defer(() => BuildSubRoomWalls(storey));
            }
        }

        private void BuildSubRoomWalls(int storey)
        {
            var parts = new List<SubRoomLayout.Partition>();
            var sources = new List<RoomDefinition.SubRoom>();
            foreach (var s in _def.subRooms)
            {
                if (s == null || StoreyOf(s.level) != storey) continue;
                parts.Add(new SubRoomLayout.Partition
                {
                    tileX = s.tileX, tileZ = s.tileZ, tilesX = s.tilesX, tilesZ = s.tilesZ,
                });
                sources.Add(s);
            }
            if (parts.Count == 0) return;

            var walls = new List<SubRoomLayout.Wall>();
            SubRoomLayout.Walls(parts, _def.tilesX, _def.tilesZ, walls);
            if (walls.Count == 0) return;

            var inner = _def.InnerContour();
            float minCeil = _def.MinCeilingOver(inner);
            float floorY = _def.StoreyBaseY(storey, minCeil);
            float ceilY = storey == _def.StoreyCount
                ? minCeil
                : _def.StoreyCeilingY(storey, minCeil);
            float fullHeight = Mathf.Max(0.5f, ceilY - floorY);

            float wallH = 0f;
            foreach (var s in sources) wallH = Mathf.Max(wallH, s.wallHeight);
            float height = wallH > 0.01f ? wallH : fullHeight;

            // El vano de un tabique lo deciden LAS DOS estancias que separa, y se queda con el más
            // estrecho de los dos: el estrecho es el que alguien pidió a propósito, y ensancharlo
            // sin avisar deshace su decisión. Por pareja y no un ancho para todo el piso — con un
            // ancho global, UNA estancia sellada (doorWidth 0) tapiaba también los vanos que no
            // tocaba.
            float DoorFor(SubRoomLayout.Wall w) => Mathf.Min(
                Mathf.Max(0f, sources[w.roomA].doorWidth),
                Mathf.Max(0f, sources[w.roomB].doorWidth));

            var blocks = new List<RoomDefinition.Block>();
            foreach (var b in _def.blocks)
                if (b != null && StoreyOf(b.level) != storey) blocks.Add(b);

            float tile = GridVisualConstants.TileSize;
            float t = Mathf.Max(0.05f, _def.wallThickness);
            // Las estancias se declaran en tiles desde la esquina (0,0) y los bloques van en metros
            // desde el CENTRO de la sala: sin este desplazamiento el complejo entero sale corrido a
            // un cuadrante.
            float halfX = _def.tilesX * tile * 0.5f;
            float halfZ = _def.tilesZ * tile * 0.5f;

            int doors = 0;
            foreach (var w in walls)
            {
                float len = w.length * tile;
                var block = new RoomDefinition.Block
                {
                    position = new Vector2(w.center.x * tile - halfX, w.center.y * tile - halfZ),
                    sizeX = w.alongZ ? t : len,
                    sizeZ = w.alongZ ? len : t,
                    baseY = 0f,
                    height = height,
                    level = storey,
                };

                // El vano no cabe si es más ancho que el propio tabique: dejarlo entrar lo recorta
                // hasta desaparecer y el paso que se pidió no está — el mismo modo de fallo que un
                // boquete más ancho que su pared (trampa 4 de ROOMS-ROADMAP).
                float door = w.shared ? DoorFor(w) : 0f;
                if (door > 0.01f && door < len - 0.2f)
                {
                    block.holes = new[]
                    {
                        new RoomDefinition.BlockHole { side = w.alongZ ? 1 : 0, along = 0.5f, width = door },
                    };
                    doors++;
                }
                blocks.Add(block);
            }

            if (blocks.Count > MaxPerGroup)
            {
                EditorUtility.DisplayDialog("Too many walls",
                    $"Saldrían {blocks.Count} bloques y el tope por grupo es {MaxPerGroup}. "
                    + "Junta estancias o quita alguna.", "OK");
                return;
            }

            _def.blocks = blocks.ToArray();
            _strips.Clear();
            _foldouts.Clear();
            RebuildIfLive();
            Debug.Log($"[RoomAuthoringWindow] Sub-rooms on storey {storey}: {parts.Count} room(s), "
                      + $"{walls.Count} wall(s), {doors} doorway(s).");
        }
    }
}
#endif
