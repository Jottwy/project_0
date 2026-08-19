using System;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Pool of hand-authored rooms (Room Authoring Tool, <c>Backrooms/Bake Room</c>) that can
    /// later be anchored into a chunk's existing Open zone in place of the procedural tile
    /// geometry. Phase 1 only produces and catalogues entries here — nothing reads this pool at
    /// runtime yet (that is the deferred anchoring/fit step, a separate change to
    /// GridChunkBuilder/ChunkStreamer).
    ///
    /// Lives at Resources/Rooms/RoomPool.asset so the future runtime consumer can
    /// Resources.Load it without scene wiring, same pattern as GridPrefabSet.
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/RoomPool", fileName = "RoomPool")]
    public sealed class RoomPool : ScriptableObject
    {
        /// <summary>
        /// Una caja de colisión de la sala, en el espacio LOCAL del prefab y en metros.
        ///
        /// Es el proxy de colisión: la malla visual puede ser todo lo detallada que quiera
        /// (molduras, tuberías, salientes) porque lo que bloquea el paso son estas cajas y no
        /// la malla. Una caja es exactamente el mismo volumen en Unity y en Rust, así que —a
        /// diferencia de una malla resuelta por dos motores de física distintos— los dos lados
        /// no pueden discrepar y no hay rubber-banding posible.
        ///
        /// Lleva <see cref="yawDegrees"/> y no es solo un AABB: una sala de planta redonda tiene
        /// paredes en diagonal, y meter una pared diagonal en una caja alineada a los ejes da un
        /// pedrusco que bloquea media sala. Sigue siendo una forma EXACTA (no el resultado de un
        /// solver), así que la garantía de "los dos motores coinciden" se mantiene; al backend
        /// solo le cuesta un test de caja orientada en vez de alineada.
        ///
        /// Las deriva <c>RoomColliderBuilder</c> del MODELO, no de la malla: si sabemos que esa
        /// pared es "un trozo recto de 3 m", sale una caja exacta, mientras que deducirla de
        /// triángulos sería adivinar. El backend NO las calcula: solo registra dónde están y
        /// prueba contra ellas. Ese registro es la pieza 2 (ADR-083, wire 37 → 38) y aún no existe.
        /// </summary>
        [Serializable]
        public struct CollisionBox
        {
            public Vector3 center;
            public Vector3 size;

            /// <summary>Giro alrededor de Y, en grados. 0 = alineada a los ejes.</summary>
            public float yawDegrees;
        }

        [Serializable]
        public sealed class RoomEntry
        {
            /// <summary>Matches the prefab file name, e.g. "room_0".</summary>
            public string id;

            /// <summary>
            /// CONTRATO DE PIVOTE: la raíz del prefab es el CENTRO de su footprint, no una
            /// esquina. El colocador (GridChunkBuilder.PlanAuthoredRooms) gira la pieza 0/90/
            /// 180/270° para hacer coincidir su puerta con la apertura real del rect sellado, y
            /// con pivote en esquina cada giro la desplazaría. La herramienta de autoría
            /// (Backrooms/Room Authoring Tool) lo comprueba antes de hornear.
            /// </summary>
            public GameObject prefab;

            /// <summary>Footprint in 5 m tiles (GridVisualConstants.TileSize), not raw 2.5 m cells.</summary>
            public int tilesX;
            public int tilesZ;

            public float heightMeters;

            /// <summary>Door anchor, local to the prefab root — where the room expects to meet a corridor.</summary>
            public Vector3 doorLocalPosition;
            public Vector3 doorLocalForward;

            /// <summary>Proxy de colisión de la sala. Vacío = la sala no bloquea nada.</summary>
            public CollisionBox[] collisionBoxes = Array.Empty<CollisionBox>();

            /// <summary>
            /// El MODELO del que salió esta sala, guardado para poder volver a abrirla y seguir
            /// tocándola. Sin esto una sala horneada es un callejón sin salida: tendrías la malla
            /// pero no los parámetros, y mover una puerta obligaría a rehacerla entera. Null en
            /// las salas montadas a mano, que no salen de un modelo.
            /// </summary>
            public RoomDefinition definition;
        }

        public RoomEntry[] rooms = Array.Empty<RoomEntry>();
    }
}
