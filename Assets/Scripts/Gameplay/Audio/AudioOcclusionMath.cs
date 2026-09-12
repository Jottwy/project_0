using UnityEngine;

namespace BackroomsSurvival.Gameplay.Audio
{
    /// <summary>
    /// Oclusión por CUENTA de paredes, no binaria. Compartida por <see cref="FluorescentHumDirector"/>
    /// y <see cref="OfficeAmbienceDirector"/>: los dos medían "¿hay algo en medio? sí/no" con un
    /// único <c>Physics.Linecast</c> y aplicaban SIEMPRE el mismo filtro fijo, así que una fuente
    /// detrás de una pared sonaba igual de tapada que una detrás de tres.
    /// </summary>
    /// <remarks>
    /// **La generalización conserva el caso ya validado.** Con una pared (<c>wallCount = 1</c>) el
    /// resultado de <see cref="VolumeGain"/> y <see cref="Cutoff"/> es EXACTAMENTE el que ya salía
    /// del <c>Mathf.Lerp</c> binario — <c>occluded^1 = occluded</c>, y la interpolación en 1 es el
    /// mismo punto de siempre. Cero pared da 1/CutoffOpen, también sin cambio. Lo nuevo es
    /// únicamente 2+ paredes, donde antes no había nada que distinguiera una sala interior de una
    /// justo al otro lado del pasillo.
    ///
    /// El volumen se compone como potencia (<c>occludedPerWall^n</c>) porque CADA pared adicional
    /// vuelve a filtrar lo que ya salió filtrado de la anterior — es la misma intuición que la
    /// atenuación en dB por espesor, sin necesitar dB de verdad. El corte del low-pass NO compone
    /// así (los Hz no son multiplicativos de forma perceptual útil): se extrapola la MISMA recta
    /// que ya interpolaba entre abierto y una pared, con un suelo para no llegar a un corte tan bajo
    /// que se lea como silencio roto en vez de "amortiguado".
    /// </remarks>
    // Público (no internal) a propósito: es matemática pura sin estado que proteger, y así se
    // testea directamente en vez de por reflexión, igual que Wg3StoreyLayers.
    public static class AudioOcclusionMath
    {
        /// <summary>
        /// Cuenta cuántos colliders de <paramref name="mask"/> cruza el segmento
        /// <paramref name="from"/>→<paramref name="to"/>. Usa el <paramref name="buffer"/> del
        /// llamante para no asignar por sonda — la sonda corre una vez por frame por director.
        /// </summary>
        public static int CountWalls(Vector3 from, Vector3 to, int mask, RaycastHit[] buffer)
        {
            Vector3 delta = to - from;
            float dist = delta.magnitude;
            if (dist < 0.001f) return 0;
            return Physics.RaycastNonAlloc(from, delta / dist, buffer, dist, mask,
                QueryTriggerInteraction.Ignore);
        }

        /// <summary>Ganancia de volumen para <paramref name="wallCount"/> paredes (puede venir
        /// suavizado, así que no tiene por qué ser entero).</summary>
        public static float VolumeGain(float wallCount, float occludedVolumePerWall) =>
            Mathf.Pow(Mathf.Clamp01(occludedVolumePerWall), Mathf.Max(0f, wallCount));

        /// <summary>Corte del low-pass para <paramref name="wallCount"/> paredes, con
        /// <paramref name="minCutoffFloor"/> para no cerrar del todo el filtro.</summary>
        public static float Cutoff(float wallCount, float cutoffOpen, float cutoffOccluded,
            float minCutoffFloor) =>
            Mathf.Max(minCutoffFloor, Mathf.LerpUnclamped(cutoffOpen, cutoffOccluded, wallCount));
    }
}
