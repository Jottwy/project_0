#if UNITY_EDITOR
// Partición de RoomAuthoringWindow: el FOOTPRINT frente a la PLANTA. El resto vive en
// RoomAuthoringWindow.cs. Nació de un boquete: una planta manual de 20 × 24 m horneada con un
// footprint tecleado de 10 × 10 tiles (50 × 50 m) — el backend reservó los 50 × 50, el cliente
// vació suelo y techo en todo eso y el prefab solo llenó el centro. Aquí vive lo que lo cierra:
// el ajuste del footprint a la planta (modo Manual), el aviso de Validate cuando la planta no
// llena el footprint (cualquier modo), y el menú que rehornea las salas del pool cuyo footprint
// guardado ya no coincide con su planta.
using UnityEditor;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    public sealed partial class RoomAuthoringWindow
    {
        /// <summary>
        /// Por debajo de esta fracción del footprint llena, Validate avisa: el resto es hueco
        /// alrededor de la sala en el mundo. Un rectángulo llena 1,0; una planta de 4 lados con
        /// squareness 0 (rombo) llena 0,5, y es legítima; por debajo de 0,6 ya se ve como boquete.
        /// Es aviso, no bloqueo: la sala se hornea igual.
        /// </summary>
        private const float MinPlanCoverage = 0.6f;

        /// <summary>
        /// Footprint = lo que cubre la planta, solo en modo Manual (Polygon y Blocks ya parten del
        /// footprint y lo llenan por construcción). Devuelve true si cambió algo.
        /// </summary>
        internal static bool FitFootprintToPlan(RoomDefinition def)
        {
            if (def == null || def.planMode != RoomDefinition.PlanMode.Manual) return false;
            RoomFootprint.FitTilesToPlan(def.InnerContour(), out int tx, out int tz);
            if (tx == def.tilesX && tz == def.tilesZ) return false;
            def.tilesX = tx;
            def.tilesZ = tz;
            return true;
        }

        /// <summary>
        /// Mensaje de Validate sobre planta frente a footprint, o null si está bien. Dos casos: la
        /// planta asoma fuera del footprint (el backend no reserva esa parte: la sala pisa
        /// laberinto) o llena menos de <see cref="MinPlanCoverage"/> de él (boquete alrededor).
        /// </summary>
        internal static string FootprintIssue(RoomDefinition def)
        {
            if (def == null) return null;
            var contour = def.InnerContour();
            if (contour == null || contour.Length < 3) return null;

            if (RoomFootprint.PlanExceedsFootprint(contour, def.tilesX, def.tilesZ))
            {
                RoomFootprint.FitTilesToPlan(contour, out int tx, out int tz);
                return $"La planta asoma fuera del footprint de {def.tilesX} × {def.tilesZ} tiles: " +
                       $"esa parte no se reserva y la sala pisaría laberinto. Hacen falta {tx} × {tz}.";
            }

            float coverage = RoomFootprint.PlanCoverage(contour, def.tilesX, def.tilesZ);
            if (coverage < MinPlanCoverage)
                return $"La planta solo llena el {coverage * 100f:0} % del footprint de " +
                       $"{def.tilesX} × {def.tilesZ} tiles ({def.WidthMeters:0.#} × {def.DepthMeters:0.#} m): " +
                       "el resto será un hueco alrededor de la sala en el mundo (el backend reserva " +
                       "el footprint entero y el cliente lo vacía). Acerca el footprint a la planta " +
                       "o agranda la planta.";
            return null;
        }

        /// <summary>
        /// Rehornea EN EL SITIO (mismo id, mismo prefab y malla) las salas del pool que guardaron
        /// su modelo y cuyo footprint guardado no es el que su planta manual pide hoy. Es el
        /// arreglo para las salas horneadas antes de que el footprint siguiera a la planta:
        /// el pool guarda <c>definition</c>, así que se pueden regenerar sin abrir la ventana.
        /// Reexporta el manifiesto al final.
        /// </summary>
        [MenuItem("Backrooms/Refit Room Footprints")]
        private static void RefitRoomFootprints()
        {
            var pool = AssetDatabase.LoadAssetAtPath<RoomPool>(PoolPath);
            if (pool == null || pool.rooms == null)
            {
                Debug.LogError($"[RoomAuthoringWindow] No hay pool en {PoolPath}.");
                return;
            }

            var before = (RoomPool.RoomEntry[])pool.rooms.Clone();
            int refit = 0, failed = 0;
            foreach (var entry in before)
            {
                if (entry == null || entry.definition == null) continue;
                // CLON: el modelo del pool es el registro de cómo se horneó la entrada; se decide
                // sobre la copia y, si hay que rehornear, el horneado guarda su propio clon.
                var clone = JsonUtility.FromJson<RoomDefinition>(JsonUtility.ToJson(entry.definition));
                if (!FitFootprintToPlan(clone)) continue;

                int oldX = entry.tilesX, oldZ = entry.tilesZ;
                if (SaveGeneratedRoom(clone, entry.id, out string message, out _))
                {
                    refit++;
                    Debug.Log($"[RoomAuthoringWindow] {entry.id}: footprint {oldX}×{oldZ} → " +
                              $"{clone.tilesX}×{clone.tilesZ} tiles, rehorneada en el sitio. {message}");
                }
                else
                {
                    failed++;
                    Debug.LogError($"[RoomAuthoringWindow] {entry.id} no se pudo rehornear: {message}");
                }
            }

            if (refit == 0 && failed == 0)
            {
                Debug.Log("[RoomAuthoringWindow] Ningún footprint que ajustar: todas las salas " +
                          "del pool cubren su planta.");
                return;
            }
            if (RoomManifestExporter.Export(out string exportMessage))
                Debug.Log($"[RoomAuthoringWindow] {refit} reajustada(s), {failed} fallida(s). {exportMessage}");
            else
                Debug.LogError($"[RoomAuthoringWindow] Manifiesto no exportado: {exportMessage}");
        }
    }
}
#endif
