// Partición de GridChunkBuilder: salas AUTORADAS a mano (RoomPool). El resto vive en
// GridChunkBuilder.cs (raíz), .Placement.cs, .WallVariants.cs, .Tinting.cs y .Props.cs.
// TODOS los campos estáticos (incl. _roomPool y los scratch) viven en el raíz: el orden de
// inicialización de estáticos entre partials es indefinido.
using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    public static partial class GridChunkBuilder
    {
        /// <summary>
        /// Una sala del pool ya resuelta contra un sitio del chunk: qué prefab, dónde y con qué
        /// giro. Rect en TILES de 5 m, con `tx1`/`tz1` EXCLUSIVOS.
        /// </summary>
        private struct RoomPlan
        {
            public GameObject prefab;
            public Vector3 localCenter;
            public float yaw;
            public int tx0, tz0, tx1, tz1;

            public bool ContainsTile(int tx, int tz) =>
                tx >= tx0 && tx < tx1 && tz >= tz0 && tz < tz1;
        }

        /// <summary>El pool autorado, o null si nadie ha horneado una sala todavía.</summary>
        private static RoomPool AuthoredRoomPool()
        {
            if (_roomPoolLoaded)
                return _roomPool;
            _roomPoolLoaded = true;
            _roomPool = Resources.Load<RoomPool>("Rooms/RoomPool");
            return _roomPool;
        }

        /// <summary>
        /// Coloca la sala autorada que el BACKEND ya decidió para este chunk.
        ///
        /// Desde ADR-083 enmienda 1 el cliente NO elige nada: el servidor reserva el sitio, vacía el
        /// interior, lo cierra con un anillo y excava el pasillo, y manda por el wire qué entrada del
        /// pool va y con qué giro. Aquí solo se instancia.
        ///
        /// Antes de eso el cliente sorteaba por su cuenta, emparejando el footprint del pool contra
        /// las zonas selladas del generador. Ese camino se retiró entero, y con motivo: exigía
        /// igualdad EXACTA de footprint contra unas zonas que miden 3 × 3 tiles, así que no colocaba
        /// ni una sala en todo el mundo. Reintroducirlo reabriría además la divergencia que ADR-081
        /// ya dejó anotada como deuda — dos implementaciones de la misma regla en dos lenguajes.
        ///
        /// El plan sale de <see cref="AuthoredRoomRegistry"/> y no del mensaje: una reconstrucción
        /// (chunk revisitado, o el reintento tras el gate de zona) tiene que ver la misma sala.
        /// </summary>
        /// <summary>
        /// La capa en la que una sala autorada se ANCLA y se instancia, sea cual sea su altura
        /// (ADR-085 punto 2). Espejo de <c>AUTHORED_LAYER</c> en el backend; las capas que la sala
        /// invade la reciben igual, pero solo para NO pintarle encima.
        /// </summary>
        internal const int AuthoredRoomAnchorLayer = 0;

        private static void PlanAuthoredRooms(int chunkX, int chunkZ, int layerIndex,
            List<RoomPlan> into)
        {
            into.Clear();
            var placed = AuthoredRoomRegistry.GetRooms(chunkX, chunkZ, layerIndex);
            if (placed == null)
                return;

            var pool = AuthoredRoomPool();
            if (pool == null || pool.rooms == null)
                return;

            // ADR-083 enmienda 3: varias salas por chunk, y el ORDEN del backend es contrato.
            for (int i = 0; i < placed.Length; i++)
                AddRoomPlan(pool, placed[i], into);
        }

        /// <summary>Resuelve UNA sala del wire contra el pool y la añade a la lista de planes.</summary>
        private static void AddRoomPlan(RoomPool pool, GridChunkDataMsg.AuthoredRoom placed,
            List<RoomPlan> into)
        {
            int tx0 = placed.tileX, tz0 = placed.tileZ;
            int entry = placed.entry, quarter = placed.quarter;

            // Un índice fuera de rango significa que el pool del cliente y el manifiesto que leyó el
            // backend NO son el mismo. Es exactamente el fallo que el digest del handshake existe
            // para cazar; aquí, al menos, no se instancia una sala equivocada en silencio.
            if (entry < 0 || entry >= pool.rooms.Length)
            {
                if (!_loggedAuthoredRoomMismatch)
                {
                    _loggedAuthoredRoomMismatch = true;
                    Debug.LogError($"[GridChunkBuilder] El backend pidió la sala {entry} y el pool " +
                                   $"tiene {pool.rooms.Length}. Pool y manifiesto desparejados: " +
                                   "ejecuta Backrooms ▸ Export Room Manifest y reinicia la sesión.");
                }
                return;
            }

            var room = pool.rooms[entry];
            if (room == null || room.prefab == null || room.tilesX < 1 || room.tilesZ < 1)
                return;

            // Footprint YA GIRADO, misma cuenta que `ManifestRoom::footprint_cells` en Rust: un
            // cuarto impar intercambia los ejes.
            bool swapped = quarter == 1 || quarter == 3;
            int tw = swapped ? room.tilesZ : room.tilesX;
            int th = swapped ? room.tilesX : room.tilesZ;

            into.Add(new RoomPlan
            {
                prefab = room.prefab,
                // El pivote de la sala es el CENTRO de su footprint (contrato de la herramienta de
                // autoría): con el centro, girar 90° no descoloca la pieza.
                //
                // Y sube `PropFloorY`. La sala se autora con su cara pisable en y = 0
                // (`RoomMeshBuilder`: `yFloor = 0f`) y la losa del pasillo tiene la suya a 0,04 — la
                // mitad de sus 8 cm de canto. Colocada a 0 la sala queda 4 cm HUNDIDA y cada puerta
                // es un escalón hacia abajo con el labio de la losa a la vista.
                //
                // ADR-083 enmienda 1 anotó este riesgo como "suelo doble", pero doble ya no hay: el
                // punto 7 de esa misma enmienda hace que el bucle de tiles se salte los de la sala,
                // así que ahí el chunk no suela nada. Lo que quedaba era solo el desnivel.
                //
                // Se corrige AQUÍ y no en el horneado —que es lo que la enmienda proponía— porque
                // subir la sala entera deja igual de alineados suelo, paredes y proxy de colisión,
                // sin rehornear ni un prefab (y el horneado tiene DOS rutas, `SaveGeneratedRoom` y
                // `BakeRoom`, que habría que tocar las dos). El wire no se toca, como pedía.
                localCenter = new Vector3((tx0 + tw * 0.5f) * Ts, PropFloorY, (tz0 + th * 0.5f) * Ts),
                yaw = quarter * 90f,
                tx0 = tx0,
                tz0 = tz0,
                tx1 = tx0 + tw,
                tz1 = tz0 + th,
            });
        }

        /// <summary>Instancia las salas ya planificadas bajo el root del chunk, con el tinte de su
        /// zona ya aplicado.</summary>
        private static void PlaceAuthoredRooms(Transform parent, List<RoomPlan> plans, Color zoneTint)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                var go = Instantiate(plans[i].prefab, parent, plans[i].localCenter, plans[i].yaw);
                TintAuthoredRoom(go, zoneTint);
            }
        }

        /// <summary>
        /// Aplica el tinte de zona a una sala autorada recién instanciada.
        ///
        /// Sin esto la sala es la ÚNICA superficie del chunk que no recibe `zoneTint`: el laberinto
        /// que la rodea sí (suelo, techo, paneles y pilares lo multiplican en el bucle de tiles), así
        /// que en una zona con tinte fuerte —BLACKOUT, PIT, RED— la sala cantaba como una pieza
        /// pegada de otro sitio.
        ///
        /// Se MULTIPLICA el `_BaseColor` que el material autorado ya traía, no se sustituye:
        /// sustituirlo aplanaría a un solo color toda la paleta de la sala, que es justo lo que se
        /// autoró a mano. Es la misma cuenta que hace el laberinto (`wallBase * zoneTint`).
        ///
        /// Y NO consume ninguna tirada del `rng` de la clase: ese `rng` es por tile y su secuencia
        /// decide el jitter HSV del chunk entero. Misma disciplina que `PlaceLintels` y que la
        /// escalera de OFFICE.
        /// </summary>
        private static void TintAuthoredRoom(GameObject go, Color zoneTint)
        {
            // ZONE_NORMAL, zona desconocida y capa sin estilo dan blanco. Multiplicar por blanco no
            // cambia nada, así que ni se tocan los renderers del prefab.
            if (zoneTint == Color.white)
                return;

            go.GetComponentsInChildren(_rendererScratch);
            for (int i = 0; i < _rendererScratch.Count; i++)
            {
                var r = _rendererScratch[i];

                // Por SUBMALLA y no por renderer: una pieza autorada con dos materiales tiene dos
                // `_BaseColor` distintos, y un único bloque de renderer le pondría a la segunda
                // submalla el color base de la primera.
                r.GetSharedMaterials(_materialScratch);
                for (int m = 0; m < _materialScratch.Count; m++)
                {
                    var mat = _materialScratch[m];
                    // Un material sin `_BaseColor` (Built-in sin convertir, unlit propio) se deja en
                    // paz: escribirle la propiedad no haría nada y `GetColor` devolvería negro, que
                    // multiplicado apagaría la pieza entera.
                    if (mat == null || !mat.HasProperty(LayerVisualMaterials.BaseColorId))
                        continue;

                    _mpb.Clear();
                    _mpb.SetColor(LayerVisualMaterials.BaseColorId,
                        mat.GetColor(LayerVisualMaterials.BaseColorId) * zoneTint);
                    r.SetPropertyBlock(_mpb, m);
                }
            }
        }

        /// <summary>True si (tx, tz) cae dentro de alguna sala autorada de este chunk.</summary>
        private static bool IsAuthoredRoomTile(List<RoomPlan> plans, int tx, int tz)
        {
            for (int i = 0; i < plans.Count; i++)
                if (plans[i].ContainsTile(tx, tz)) return true;
            return false;
        }
    }
}
