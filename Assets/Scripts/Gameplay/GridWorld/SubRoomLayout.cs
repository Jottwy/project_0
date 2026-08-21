using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// De una partición en estancias a los tabiques que la separan. Solo geometría sobre la rejilla
    /// de tiles: ni metros, ni bloques, ni sala.
    ///
    /// Vive en la assembly de runtime por lo mismo que <see cref="MazeCarver"/>: los tests EditMode
    /// no pueden referenciar <c>Assets/Editor</c>, y el reparto de vanos es justo la parte donde un
    /// error deja una estancia sin salida sin que lo delate nada hasta jugarla.
    /// </summary>
    public static class SubRoomLayout
    {
        public struct Partition
        {
            public int tileX, tileZ, tilesX, tilesZ;
        }

        /// <summary>Un tabique entre tiles, en coordenadas de TILE desde la esquina (0,0).</summary>
        public struct Wall
        {
            /// <summary>Centro, en tiles.</summary>
            public Vector2 center;

            /// <summary>Largo, en tiles.</summary>
            public float length;

            /// <summary>true = paralelo a Z (separa dos tiles vecinos en X).</summary>
            public bool alongZ;

            /// <summary>Las dos estancias que separa se tocan aquí: es donde va el vano. False = da
            /// al vacío de la sala, y ahí un vano no comunica nada.</summary>
            public bool shared;

            /// <summary>Índices en la lista de estancias de los dos lados, o -1 para el vacío. Un
            /// tramo separa siempre al MISMO par de punta a punta. Van aquí porque el ancho del
            /// vano lo decide el par concreto: sin saber quiénes son, quien convierta a bloques
            /// solo puede tomar un ancho global, y entonces UNA estancia sellada tapia los vanos
            /// de todo el piso, incluidos los que no toca.</summary>
            public int roomA, roomB;
        }

        /// <summary>
        /// Los tabiques del contorno de cada estancia, FUNDIDOS por tramos. Un borde donde se tocan
        /// dos estancias sale UNA sola vez y marcado como compartido — dos tabiques espalda contra
        /// espalda dejarían un resquicio impasable entre ellos, y es exactamente el fallo que en el
        /// emplazamiento de salas obliga a dejar un tile entre reservas.
        ///
        /// El borde exterior de la sala NO sale: ahí ya está su propia pared.
        /// </summary>
        /// <param name="tilesX">Tamaño de la sala, para saber qué bordes son el exterior.</param>
        public static void Walls(IReadOnlyList<Partition> parts, int tilesX, int tilesZ,
            List<Wall> into)
        {
            into.Clear();
            if (parts == null || parts.Count == 0 || tilesX <= 0 || tilesZ <= 0) return;

            // owner[x, z] = índice de la estancia que ocupa ese tile, o -1. Se pinta por orden, así
            // que la última estancia gana los tiles que solape: solaparlas es un error de autorado,
            // pero resolverlo en silencio es mejor que emitir tabiques dentro de una estancia.
            var owner = new int[tilesX, tilesZ];
            for (int z = 0; z < tilesZ; z++)
                for (int x = 0; x < tilesX; x++) owner[x, z] = -1;

            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                int x1 = Mathf.Min(tilesX, p.tileX + Mathf.Max(1, p.tilesX));
                int z1 = Mathf.Min(tilesZ, p.tileZ + Mathf.Max(1, p.tilesZ));
                for (int z = Mathf.Max(0, p.tileZ); z < z1; z++)
                    for (int x = Mathf.Max(0, p.tileX); x < x1; x++) owner[x, z] = i;
            }

            Emit(owner, tilesX, tilesZ, into, alongZ: true);
            Emit(owner, tilesX, tilesZ, into, alongZ: false);
        }

        private static void Emit(int[,] owner, int tilesX, int tilesZ, List<Wall> into, bool alongZ)
        {
            int boundaries = alongZ ? tilesX - 1 : tilesZ - 1;
            int span = alongZ ? tilesZ : tilesX;

            for (int a = 0; a < boundaries; a++)
            {
                int runStart = -1, runLo = -1, runHi = -1;

                for (int b = 0; b <= span; b++)
                {
                    int lo = -1, hi = -1;
                    if (b < span)
                    {
                        lo = alongZ ? owner[a, b] : owner[b, a];
                        hi = alongZ ? owner[a + 1, b] : owner[b, a + 1];
                    }

                    // Tabique solo entre estancias DISTINTAS, y con al menos una a cada lado del
                    // borde: dos tiles de la misma estancia no se separan, y el vacío contra el
                    // vacío no es nada.
                    bool wall = b < span && lo != hi && (lo >= 0 || hi >= 0);

                    // Un tramo se corta en cuanto cambia el PAR de estancias que separa, no solo
                    // al acabarse el tabique. Dos costuras distintas alineadas (A|B encima de C|D)
                    // fundidas en un tabique único pondrían su único vano justo en el cruce con el
                    // tabique perpendicular — un paso tapiado por la otra pared.
                    if (wall && runStart >= 0 && (lo != runLo || hi != runHi))
                    {
                        Add(into, a, runStart, b, runLo, runHi, alongZ);
                        runStart = -1;
                    }
                    if (wall && runStart < 0) { runStart = b; runLo = lo; runHi = hi; }
                    if (wall || runStart < 0) continue;

                    Add(into, a, runStart, b, runLo, runHi, alongZ);
                    runStart = -1;
                }
            }
        }

        private static void Add(List<Wall> into, int a, int from, int to, int lo, int hi, bool alongZ)
        {
            float len = to - from;
            float mid = from + len * 0.5f;
            float edge = a + 1;
            into.Add(new Wall
            {
                center = alongZ ? new Vector2(edge, mid) : new Vector2(mid, edge),
                length = len,
                alongZ = alongZ,
                shared = lo >= 0 && hi >= 0,
                roomA = lo,
                roomB = hi,
            });
        }
    }
}
