using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Relación entre la PLANTA de una sala (su contorno interior, en metros) y su FOOTPRINT (los
    /// tiles de 5 m que reserva el backend y que el cliente vacía de laberinto, ADR-083/084).
    ///
    /// Existe por un boquete: una planta dibujada a mano de 20 × 24 m horneada con un footprint
    /// tecleado de 10 × 10 tiles (50 × 50 m). El backend reservó los 50 × 50, el cliente suprimió
    /// suelo y techo en todo eso, y el prefab solo llenó el centro. Ni el footprint seguía a la
    /// planta ni nadie avisaba de la diferencia. Aquí viven las dos cuentas que lo cierran: ajustar
    /// los tiles a la planta, y medir cuánto footprint llena de verdad.
    ///
    /// El footprint es SIMÉTRICO respecto al origen de la sala a propósito: el pivote del prefab ES
    /// el centro del footprint y el placer gira la sala sobre él, así que una planta descentrada
    /// necesita un footprint que la cubra por el lado largo en ambos sentidos. Lo que sobra del
    /// otro lado es mucho menos que el boquete de antes, y es lo que Validate mide como cobertura.
    ///
    /// Mismo criterio que <see cref="RoomDefinition.PolygonContour"/>: el contorno interior llega
    /// hasta el borde del footprint y la pared asoma <c>wallThickness</c> por fuera, así que un
    /// rectángulo de 12 tiles ajusta a 12, no a 13.
    /// </summary>
    public static class RoomFootprint
    {
        /// <summary>Holgura para que 30,0 m exactos den 12 tiles y no 13 por redondeo flotante.</summary>
        private const float Epsilon = 0.01f;

        /// <summary>
        /// Cuántos tiles hacen falta para cubrir el contorno en cada eje, simétricos respecto al
        /// origen. Con un contorno vacío o degenerado devuelve 1 × 1.
        /// </summary>
        public static void FitTilesToPlan(IList<Vector2> innerContour, out int tilesX, out int tilesZ)
        {
            float halfX = 0f, halfZ = 0f;
            if (innerContour != null)
            {
                for (int i = 0; i < innerContour.Count; i++)
                {
                    halfX = Mathf.Max(halfX, Mathf.Abs(innerContour[i].x));
                    halfZ = Mathf.Max(halfZ, Mathf.Abs(innerContour[i].y));
                }
            }
            tilesX = TilesToCover(halfX * 2f);
            tilesZ = TilesToCover(halfZ * 2f);
        }

        /// <summary>True si la planta asoma fuera del footprint dado (por encima de la holgura).</summary>
        public static bool PlanExceedsFootprint(IList<Vector2> innerContour, int tilesX, int tilesZ)
        {
            if (innerContour == null) return false;
            float halfW = tilesX * GridVisualConstants.TileSize * 0.5f + Epsilon;
            float halfD = tilesZ * GridVisualConstants.TileSize * 0.5f + Epsilon;
            for (int i = 0; i < innerContour.Count; i++)
            {
                if (Mathf.Abs(innerContour[i].x) > halfW || Mathf.Abs(innerContour[i].y) > halfD)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Fracción del footprint que la planta llena de verdad (área del contorno / área del
        /// footprint), en 0..1. Es la medida del boquete: 1 = la sala llena sus tiles, 0,2 = el
        /// 80 % del footprint es hueco alrededor de la sala.
        /// </summary>
        public static float PlanCoverage(IList<Vector2> innerContour, int tilesX, int tilesZ)
        {
            float footprint = tilesX * GridVisualConstants.TileSize * tilesZ * GridVisualConstants.TileSize;
            if (footprint <= 0f || innerContour == null || innerContour.Count < 3) return 0f;
            float area = 0f;
            for (int i = 0; i < innerContour.Count; i++)
            {
                Vector2 c = innerContour[i], n = innerContour[(i + 1) % innerContour.Count];
                area += c.x * n.y - n.x * c.y;
            }
            return Mathf.Clamp01(Mathf.Abs(area) * 0.5f / footprint);
        }

        private static int TilesToCover(float meters) =>
            Mathf.Max(1, Mathf.CeilToInt((meters - Epsilon) / GridVisualConstants.TileSize));
    }
}
