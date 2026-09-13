using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>Descripción de ficha de las prendas y mochilas del prototipo.</summary>
    public static class WearableDescription
    {
        /// <summary>
        /// Texto de la ficha: la frase y, debajo, las cifras que de verdad aplica el prototipo, sacadas de los mismos datos
        /// (cambiar un número no deja la descripción mintiendo). El reparto de carga no sale: todavía no se aplica.
        /// </summary>
        public static string Describe(string flavor, string slots, float maxKg, float speedPct)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(slots)) parts.Add(slots);
            if (maxKg > 0f) parts.Add($"+{maxKg.ToString("0.#", CultureInfo.InvariantCulture)} kg max load");
            if (speedPct != 0f)
                parts.Add($"Speed {(speedPct > 0f ? "+" : "-")}{Mathf.Abs(speedPct).ToString("0.#", CultureInfo.InvariantCulture)}%");
            return parts.Count == 0 ? flavor : $"{flavor}\n{string.Join(" · ", parts)}";
        }
    }
}
