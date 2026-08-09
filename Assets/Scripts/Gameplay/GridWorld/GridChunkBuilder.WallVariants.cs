// Partición de GridChunkBuilder: resolución de prefab de pared (ADR-035) y
// RoomType por tile/panel. El resto vive en GridChunkBuilder.cs (raíz),
// .Placement.cs, .Tinting.cs y .Props.cs.
// TODOS los campos estáticos (incl. los salts WallSalt*) viven en el fichero
// raíz: el orden de inicialización de estáticos entre partials es indefinido.
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    public static partial class GridChunkBuilder
    {
        private static uint KneeSaltFor(byte flag) =>
            (flag == EdgeNorth || flag == EdgeSouth) ? WallSaltKneeN : WallSaltKneeE;

        private static uint VariantSaltFor(byte flag) =>
            (flag == EdgeNorth || flag == EdgeSouth) ? WallSaltVariantN : WallSaltVariantE;

        /// <summary>
        /// ADR-035 — prefab a instanciar para UN panel concreto. Devuelve
        /// <c>prefabs.wall</c> siempre que no haya un set autorizado que case, así que con
        /// <c>hasVariants == false</c> (sin cfg o con <c>wallVariantSets</c> vacío) el
        /// render es byte-idéntico al previo a este ADR.
        ///
        /// El hash va en coords GLOBALES de tile (gx, gz) con salt por carril, misma
        /// disciplina que knee walls y dinteles: nunca toca el <c>System.Random</c> del
        /// jitter, así que los tintes de las Piezas A-F no se mueven.
        /// </summary>
        /// <paramref name="hasVariants"/> y <paramref name="roomType"/> los trae ya
        /// resueltos el llamador. El RoomType del panel lo comparte con el gate de knee
        /// walls (deuda de la enmienda 2026-08-08, activada al autorar
        /// <c>wallVariantSets</c>): `roomZones` se recorre como máximo UNA vez por panel,
        /// nunca dos, y el `!= null` nativo de <c>cfg</c> sigue resuelto una sola vez
        /// fuera del bucle de paneles.
        private static GameObject ResolveWallPrefab(GridPrefabSet prefabs, LayerVisualConfig cfg,
            bool hasVariants, int zoneKind, RoomZoneKind roomType, int gx, int gz, byte flag)
        {
            if (!hasVariants)
                return prefabs.wall;
            var variant = cfg.WallPrefabFor(zoneKind, roomType,
                Hash01(gx, gz, VariantSaltFor(flag)));
            return variant != null ? variant : prefabs.wall;
        }

        /// <summary>
        /// ADR-035 — RoomType del tile (tx, tz), o <see cref="RoomZoneKind.Open"/> cuando
        /// ninguna zona lo cubre, el chunk no trae el campo, o el backend es anterior a
        /// ADR-034. `Open` es el fallback correcto: es el único tipo sin perímetro sellado,
        /// o sea el comportamiento previo a que RoomType existiera.
        ///
        /// ESCALA: <see cref="RoomZoneMsg"/> habla en CELDAS de 2.5 m (20 por lado) y este
        /// builder en TILES de 5 m (10 por lado). Se consulta la sub-celda NOROESTE del
        /// tile — <c>(2·tx, 2·tz)</c>, la misma que <c>tile_walls_from_grid</c> toma como
        /// <c>x0/z0</c> del tile. Para SealedRoom/CorridorSpine eso es exacto y no
        /// aproximado en la práctica: sus rects se alinean a la retícula de 5 m (origen Y
        /// tamaño pares, fix de cuantización del ADR de RoomType), así que un tile está
        /// entero dentro o entero fuera. La alineación tiene UN caso degenerado declarado
        /// en el backend (`align_origin_to_tile` puede devolver un origen impar cuando la
        /// zona es casi tan ancha como el chunk entero, imposible con los perfiles de
        /// producción de hoy); si llegara a darse, el peor efecto es media fila de tiles
        /// clasificada como Open, nunca una excepción.
        ///
        /// Primera zona que cubre el tile gana (orden de estampado). Con más de una zona
        /// solapada — posible en perfiles con `num_open_zones > 1` y pesos no default,
        /// hoy inexistentes en producción — el orden de `room_zones` es el desempate.
        /// OJO: el backend estampa en ese mismo orden, así que sobre un solape la ÚLTIMA
        /// zona es la que se ve en el grid mientras que aquí gana la primera. No se
        /// resuelve porque el solape en sí es una limitación declarada del ADR de RoomType.
        /// </summary>
        // `public`, no `internal` como Hash01: EditModeTests es un ensamblado aparte sin
        // InternalsVisibleTo, y esta conversión celda↔tile es exactamente donde un
        // off-by-one silencioso (consultar tx en vez de 2·tx) pasaría desapercibido.
        public static RoomZoneKind RoomTypeForTile(RoomZoneMsg[] zones, int tx, int tz)
        {
            if (zones == null || zones.Length == 0) return RoomZoneKind.Open;
            int cellX = tx * 2, cellZ = tz * 2;
            for (int i = 0; i < zones.Length; i++)
                if (zones[i].ContainsCell(cellX, cellZ))
                    return zones[i].Kind;
            return RoomZoneKind.Open;
        }

        /// <summary>
        /// RoomType del PANEL, que no es el del tile que lo instancia.
        ///
        /// Un panel no pertenece a un tile: vive en la FRONTERA entre dos. Por la regla de
        /// no-duplicación, cada tile emite solo sus paneles +Z y +X, así que el muro OESTE
        /// de una sala lo instancia el tile de fuera (<c>txMin−1</c>, como su panel +X) y
        /// el muro NORTE el tile <c>tzMin−1</c>. Preguntar solo por el tile propietario
        /// dejaría toda sala con dos lados con el modelo nuevo (E y S, cuyo emisor sí está
        /// dentro) y dos con el de siempre — asimetría visible en cuanto exista un modelo.
        ///
        /// Regla: gana el primer RoomType NO-Open entre el tile propietario y el vecino al
        /// otro lado del panel. `Open` no tiene perímetro propio, así que "el vecino es una
        /// sala sellada y yo no" significa que ese muro es de la sala. Con salas selladas a
        /// ambos lados (imposible hoy: layer 0 estampa una sola zona) gana el propietario.
        ///
        /// Un vecino fuera del chunk (tx+1 == Tiles) no tiene dato aquí — `room_zones` es
        /// por chunk — y cae al tipo del propietario, que es la respuesta correcta en ese
        /// caso: si el tile del borde está dentro de la sala, el panel es suyo; si está
        /// fuera, no hay sala vecina que reclamarlo desde este chunk.
        /// </summary>
        public static RoomZoneKind RoomTypeForPanel(RoomZoneMsg[] zones, int tx, int tz, byte flag)
        {
            var own = RoomTypeForTile(zones, tx, tz);
            if (own != RoomZoneKind.Open) return own;

            // EdgeNorth es el panel +Z de este tile → vecino (tx, tz+1);
            // EdgeEast es el panel +X → vecino (tx+1, tz). S y W no los emite el runtime.
            int nx = tx, nz = tz;
            if (flag == EdgeNorth) nz++;
            else if (flag == EdgeEast) nx++;
            else return own;

            if (nx >= Tiles || nz >= Tiles) return own; // vecino en el chunk contiguo
            return RoomTypeForTile(zones, nx, nz);
        }
    }
}
