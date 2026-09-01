namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-115 — la aritmética del reloj de las marcas de saqueo, sola y sin Unity.
    ///
    /// Existe separada de <see cref="ChunkLootManager"/> por lo mismo que `SelectExpired` está
    /// extraído allí: es la parte que se puede probar sin levantar un editor, y es también la
    /// parte donde un error no se ve — un sello mal traducido no rompe nada, sólo hace que el
    /// mundo regenere antes o después de lo que dice el ADR.
    ///
    /// EL PROBLEMA QUE RESUELVE: el backend sella en segundos de TIEMPO DE MUNDO (lo que traía el
    /// save más lo que lleva la sesión) y aquí el reloj es `Time.unscaledTime`, que arranca en
    /// cero en cada proceso. Los dos números no son comparables. Lo que sí es comparable es el
    /// TRANSCURRIDO, y eso es lo único que se traduce.
    /// </summary>
    public static class LootMarkClock
    {
        /// <summary>ADR-115 D4 — 2 h. Tiene que ser el MISMO número que `LOOT_MARK_TTL_SECONDS`
        /// del backend (`backend/src/world/loot_marks.rs`). Si se separan, el cliente vuelve a
        /// sembrar un punto cuya marca el backend todavía guarda, o al revés: no revienta nada,
        /// pero la cadencia deja de ser la que dice el ADR.</summary>
        public const float TtlSeconds = 7200f;

        /// <summary>
        /// Sello local equivalente a una marca del mundo: "esto se lo llevaron hace
        /// <c>worldNow - takenAt</c> segundos". Una marca de hace 40 min queda sellada 40 min en
        /// el pasado, con lo que las dos caducidades del cliente (30 min de materiales, 2 h de
        /// items) siguen midiendo exactamente lo que medían antes de que nada se persistiera.
        /// </summary>
        public static float StampFromWorldTime(float localNow, long worldNow, long takenAt)
        {
            return localNow - (worldNow - takenAt);
        }
    }
}
