namespace BackroomsSurvival.WorldGen3
{
    /// <summary>Cuánto contenido pide un sitio: vacío → disperso → estructurado → denso → anómalo.
    /// Espejo de las constantes `DENSITY_*` de `wg3/density.rs`.</summary>
    public enum Wg3Density
    {
        Empty = 0,
        Sparse = 1,
        Structured = 2,
        Dense = 3,
        Anomalous = 4
    }

    /// <summary>
    /// Auditoría 2026-09-02, Fase 6 — EL CAMPO DE DENSIDAD AMBIENTAL. Espejo EXACTO de
    /// <c>wg3::density</c> en Rust, con la misma disciplina que <see cref="Wg3ScaleField"/>:
    /// función pura de la posición, escalonada por celda, y cero wire — los dos lados la calculan.
    ///
    /// <para>
    /// Contesta cuánto contenido quiere cada sitio. <b>El vacío también es contenido</b>: es la
    /// clase más probable, y lo denso y lo anómalo son la excepción que hace que lo demás se lea
    /// como abandono y no como decorado. Ningún consumidor la lee todavía: el vestido por densidad
    /// (mobiliario, cables, cajas, manchas, señales) entra por aquí cuando toque, y leerá lo mismo
    /// que el servidor cuando éste decida loot o cordura por densidad.
    /// </para>
    /// </summary>
    public static class Wg3DensityField
    {
        /// <summary>Celda gruesa, en metros: del orden de una sala grande, para que una sala sea
        /// de una sola clase.</summary>
        public const float CoarseCell = 38f;

        /// <summary>Celda fina, desplazada y con otro grano, para que la gruesa no se lea como
        /// cuadrícula.</summary>
        public const float FineCell = 13f;

        private const uint SaltCoarse = 0xDE450000u;
        private const uint SaltFine = 0xDE450001u;

        /// <summary>Valor crudo del campo en [0,1). Mismas operaciones y en el mismo orden que
        /// <c>density::value_at</c>: el producto por 0,70 y 0,30 se hace en float.</summary>
        public static float ValueAt(int worldSeed, float x, float z)
        {
            float coarse = Cell(worldSeed, x, z, CoarseCell, 0f, 0f, SaltCoarse);
            float fine = Cell(worldSeed, x, z, FineCell, 11f, 7f, SaltFine);
            return coarse * 0.70f + fine * 0.30f;
        }

        /// <summary>Clase de densidad por posición. Los umbrales SON el reparto.</summary>
        public static Wg3Density ClassAt(int worldSeed, float x, float z)
        {
            float v = ValueAt(worldSeed, x, z);
            if (v < 0.32f) return Wg3Density.Empty;
            if (v < 0.62f) return Wg3Density.Sparse;
            if (v < 0.86f) return Wg3Density.Structured;
            if (v < 0.95f) return Wg3Density.Dense;
            return Wg3Density.Anomalous;
        }

        /// <summary>La clase de un ESPACIO, cruzada con su escala: en zona <see cref="Wg3Scale.Weird"/>
        /// todo lo que no es vacío sube un escalón. Un vacío en zona rara es liminal y se queda.</summary>
        public static Wg3Density ForSpace(int worldSeed, float x, float z, Wg3Scale scale)
        {
            Wg3Density c = ClassAt(worldSeed, x, z);
            if (scale == Wg3Scale.Weird && c > Wg3Density.Empty && c < Wg3Density.Anomalous)
                return c + 1;
            return c;
        }

        /// <summary>Ruido de celda, vecino más próximo. Copia de <c>Wg3ScaleField.Cell</c> a
        /// propósito: dos campos que compartieran función se moverían juntos el día que uno
        /// cambiara, y el de escala tiene un oráculo.</summary>
        private static float Cell(int worldSeed, float x, float z, float size,
            float offX, float offZ, uint salt)
        {
            int cx = FloorDiv(x + offX, size);
            int cz = FloorDiv(z + offZ, size);
            return Wg3Hash.ToUnit(Wg3Hash.Mix(worldSeed, cx, cz, unchecked((int)salt)));
        }

        private static int FloorDiv(float v, float size)
        {
            float q = v / size;
            int i = (int)q;
            return (q < 0f && q != i) ? i - 1 : i;
        }
    }
}
