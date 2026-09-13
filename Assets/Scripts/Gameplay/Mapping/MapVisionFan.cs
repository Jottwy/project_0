using System;
using System.Collections.Generic;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.3b de MAPPING-PROTOTYPE — lo que se ve LEJOS, en el ángulo de la mirada: un rayo horizontal convertido en
    /// celdas. C# puro; el muestreador lanza el <c>Physics.Raycast</c> y le pasa la distancia del impacto.
    /// </summary>
    /// <remarks>
    /// Recorre la rejilla celda a celda (Amanatides–Woo): toda celda que el rayo cruza antes del impacto es suelo y
    /// la del impacto, pared. Sin impacto, suelo hasta la distancia máxima. La línea de visión la da el propio rayo.
    /// Una celda ya escrita en la misma muestra (por el disco cercano o por otro rayo) no se repite.
    /// </remarks>
    public static class MapVisionFan
    {
        /// <summary>Celdas que puede escribir un rayo de <paramref name="distanceM"/>, contando la de la pared.</summary>
        public static int MaxCellsPerRay(double cellSizeM, double distanceM) =>
            2 * (int)Math.Ceiling(distanceM / cellSizeM) + 2;

        /// <summary>
        /// Escribe las celdas del rayo desde (<paramref name="originX"/>, <paramref name="originZ"/>) en metros, con
        /// dirección (<paramref name="dirX"/>, <paramref name="dirZ"/>), a partir de <paramref name="written"/>.
        /// <paramref name="hitDistanceM"/> &lt; 0 = no chocó. Devuelve el nuevo total escrito.
        /// </summary>
        public static int Trace(double cellSizeM, double originX, double originZ, double dirX, double dirZ,
            double maxDistanceM, double hitDistanceM, HashSet<long> seen, int[] outX, int[] outZ,
            MapCellKind[] outKind, int written)
        {
            double length = Math.Sqrt(dirX * dirX + dirZ * dirZ);
            if (length < 1e-9) return written;
            dirX /= length;
            dirZ /= length;

            bool hit = hitDistanceM >= 0.0 && hitDistanceM <= maxDistanceM;
            // Las paredes de WG3 caen en fronteras de celda: el impacto está justo EN la frontera. Un pelo más allá
            // asegura que la pared sea la celda de detrás y no el suelo de delante.
            double end = hit ? hitDistanceM + cellSizeM * 0.05 : maxDistanceM;

            int cellX = (int)Math.Floor(originX / cellSizeM);
            int cellZ = (int)Math.Floor(originZ / cellSizeM);
            int stepX = dirX > 0 ? 1 : -1;
            int stepZ = dirZ > 0 ? 1 : -1;
            double deltaX = Math.Abs(dirX) < 1e-9 ? double.MaxValue : cellSizeM / Math.Abs(dirX);
            double deltaZ = Math.Abs(dirZ) < 1e-9 ? double.MaxValue : cellSizeM / Math.Abs(dirZ);
            double nextX = Math.Abs(dirX) < 1e-9
                ? double.MaxValue
                : ((dirX > 0 ? (cellX + 1) * cellSizeM : cellX * cellSizeM) - originX) / dirX;
            double nextZ = Math.Abs(dirZ) < 1e-9
                ? double.MaxValue
                : ((dirZ > 0 ? (cellZ + 1) * cellSizeM : cellZ * cellSizeM) - originZ) / dirZ;

            int limit = outX.Length;
            while (written < limit)
            {
                double leave = Math.Min(nextX, nextZ);
                // La celda del impacto es la que el rayo ocupa cuando llega a la distancia del impacto.
                bool wall = hit && leave >= end;
                long key = ((long)cellX << 32) ^ (uint)cellZ;
                if (seen.Add(key))
                {
                    outX[written] = cellX;
                    outZ[written] = cellZ;
                    outKind[written] = wall ? MapCellKind.Wall : MapCellKind.Floor;
                    written++;
                }

                if (wall || leave >= end) break;

                if (nextX < nextZ)
                {
                    cellX += stepX;
                    nextX += deltaX;
                }
                else
                {
                    cellZ += stepZ;
                    nextZ += deltaZ;
                }
            }

            return written;
        }
    }
}
