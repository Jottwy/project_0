using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Los parámetros del ritmo de los fluorescentes. Uno por mundo, no por lámpara: lo que cambia
    /// de una lámpara a otra sale del hash, no de aquí.
    /// </summary>
    [System.Serializable]
    public sealed class Wg3LightCadenceSettings
    {
        [Tooltip("Fracción de plafones APAGADOS: difusor puesto, sin Light.")]
        [Range(0f, 1f)] public float offChance = 0.12f;

        [Tooltip("Fracción de plafones que PARPADEAN, con fase propia.")]
        [Range(0f, 1f)] public float flickerChance = 0.08f;

        /// <summary>Temperatura de referencia del tubo. NO cambia el color validado: sólo es el
        /// punto desde el que se mide la desviación, y a desviación cero el tinte es exactamente 1.
        /// </summary>
        public float baseKelvin = 3500f;

        [Tooltip("Desviación máxima de temperatura por fixture, en kelvin (±).")]
        public float kelvinSpread = 200f;

        [Tooltip("Jitter de posición como fracción de la celda del fixture.")]
        [Range(0f, 0.5f)] public float jitterFraction = 0.18f;

        [Tooltip("Tope absoluto del jitter, en metros. Impide que un plafón de una nave se meta " +
                 "dentro de una pared cuando la celda es enorme.")]
        public float jitterMaxMeters = 0.9f;

        public float flickerHzMin = 0.7f;
        public float flickerHzMax = 5.5f;

        /// <summary>Los valores por defecto, compartidos por todo el que no pase los suyos. Es
        /// SÓLO LECTURA de hecho: nadie debe mutar esta instancia — para probar otros valores se
        /// crea una nueva y se pasa.</summary>
        public static readonly Wg3LightCadenceSettings Default = new Wg3LightCadenceSettings();
    }

    /// <summary>Lo que el hash decide para UN plafón concreto.</summary>
    public struct Wg3Fixture
    {
        /// <summary>Falso = tubo muerto: se dibuja el difusor apagado y no se crea la Light.</summary>
        public bool lit;

        public bool flickers;
        public float flickerHz;

        /// <summary>Desfase de la onda, en segundos. Es lo que desincroniza dos lámparas vecinas
        /// que parpadean a la misma frecuencia.</summary>
        public float flickerPhase;

        /// <summary>Multiplicador del color validado del tubo. A desviación cero vale (1,1,1) y el
        /// color no se toca.</summary>
        public Color tint;

        /// <summary>Desplazamiento en el plano, en metros, dentro de la celda del fixture.</summary>
        public Vector2 offset;
    }

    /// <summary>
    /// FRENTE A — el ritmo de los fluorescentes deja de salir del índice.
    ///
    /// # Qué estaba mal
    ///
    /// Los plafones se colocaban en el centro exacto de su celda, todos encendidos, todos del mismo
    /// color y todos fijos. El índice del fixture era lo ÚNICO que decidía dónde iba, así que el
    /// techo de un tramo era una rejilla perfecta repetida idéntica en todo el mundo — y una rejilla
    /// perfecta es la firma de lo generado, igual que la junta visible de la regla R31.
    ///
    /// # De dónde sale ahora
    ///
    /// De un hash de (coordenada de CHUNK, posición del fixture, índice del fixture) más la semilla
    /// del mundo. Los tres hacen falta y ninguno sobra:
    ///
    /// - **Chunk** e **índice** son lo que pide el encargo.
    /// - **La posición** es lo que impide que dos tramos distintos del mismo chunk reciban la misma
    ///   tirada en su fixture 0. Sin ella, un chunk con seis tramos tendría los seis primeros
    ///   plafones idénticos: se habría cambiado un patrón regular por otro, sólo que más grande.
    ///
    /// REGLA R3 — no hay RNG compartido. Se abre un <see cref="Wg3Hash.Stream"/> por fixture,
    /// sembrado por posición, y muere ahí mismo. Nada depende del orden en que se monten los
    /// chunks, así que dos clientes que reciban las piezas en distinto orden ven las mismas
    /// lámparas rotas.
    ///
    /// **EL ORDEN DE LAS TIRADAS ES CONTRATO.** Meter una tirada nueva por en medio corre todas las
    /// de después y cambia el mundo entero de sitio. Se añaden AL FINAL.
    /// </summary>
    public static class Wg3LightCadence
    {
        /// <summary>Sal de propósito, "LCAD". Separa estas decisiones de las demás que ocurren en el
        /// mismo punto del mundo (qué pieza, si taponar, qué variante).</summary>
        private const uint Salt = 0x4C434144u;

        /// <summary>Chunk de una coordenada de mundo. Mismo reparto que
        /// <see cref="Wg3ChunkStreamer.ChunkSize"/>: si allí cambia, aquí también.</summary>
        public static Vector2Int ChunkOf(float x, float z) => new Vector2Int(
            Mathf.FloorToInt(x / Wg3ChunkStreamer.ChunkSize),
            Mathf.FloorToInt(z / Wg3ChunkStreamer.ChunkSize));

        /// <summary>
        /// Lo que le toca a un fixture. <paramref name="worldX"/> y <paramref name="worldZ"/> son la
        /// posición NOMINAL (el centro de su celda, antes del jitter): si se le pasara la ya
        /// desplazada, el jitter dependería de sí mismo y dejaría de ser reproducible.
        /// </summary>
        public static Wg3Fixture Resolve(int worldSeed, float worldX, float worldZ,
            int fixtureIndex, float cellX, float cellZ, Wg3LightCadenceSettings s)
        {
            if (s == null) s = Wg3LightCadenceSettings.Default;

            Vector2Int chunk = ChunkOf(worldX, worldZ);
            // Dos mezclas y no una: la primera ata el fixture a su sitio y a su índice, la segunda
            // lo ata al chunk y a la semilla. Encadenarlas evita tener que meter seis enteros en un
            // mixer que sólo toma cuatro.
            ulong local = Wg3Hash.Mix(Wg3Hash.Quantize(worldX), Wg3Hash.Quantize(worldZ),
                fixtureIndex, unchecked((int)Salt));
            ulong seed = Wg3Hash.Mix(worldSeed, chunk.x, chunk.y, unchecked((int)local));
            var rng = new Wg3Hash.Stream(seed);

            var f = new Wg3Fixture();

            // 1 — estado. Apagado y parpadeante son EXCLUYENTES y salen del mismo tiro, así que
            // subir un porcentaje no altera el otro: con 12 % y 8 %, [0, 0.12) muere, [0.12, 0.20)
            // parpadea y el resto queda fija.
            float state = rng.Next01();
            float off = Mathf.Clamp01(s.offChance);
            float flick = Mathf.Clamp01(s.flickerChance);
            f.lit = state >= off;
            f.flickers = f.lit && state < off + flick;

            // 2 y 3 — frecuencia y fase. Se tiran SIEMPRE, parpadee o no, para que encender el
            // parpadeo de una lámpara no corra las tiradas de color y jitter de esa misma lámpara.
            f.flickerHz = Mathf.Lerp(s.flickerHzMin, s.flickerHzMax, rng.Next01());
            // Fase en segundos sobre un ciclo entero de su propia frecuencia: dos lámparas a la
            // misma frecuencia quedan desincronizadas, que es el punto.
            f.flickerPhase = rng.Next01() * (f.flickerHz > 0.001f ? 1f / f.flickerHz : 1f);

            // 4 — temperatura de color. El tinte es un COCIENTE contra la temperatura de referencia,
            // no un color absoluto: así el color que ya estaba validado sigue siendo exactamente el
            // que sale con desviación cero, y esto sólo lo empuja ±200 K.
            float delta = (rng.Next01() * 2f - 1f) * s.kelvinSpread;
            f.tint = TintFor(s.baseKelvin, s.baseKelvin + delta);

            // 5 y 6 — jitter, un eje por tirada.
            float ampX = Mathf.Min(cellX * s.jitterFraction, s.jitterMaxMeters);
            float ampZ = Mathf.Min(cellZ * s.jitterFraction, s.jitterMaxMeters);
            f.offset = new Vector2(
                (rng.Next01() * 2f - 1f) * ampX,
                (rng.Next01() * 2f - 1f) * ampZ);

            return f;
        }

        /// <summary>
        /// El multiplicador que lleva de <paramref name="baseKelvin"/> a <paramref name="kelvin"/>.
        ///
        /// Por cociente y no evaluando el cuerpo negro directamente, porque el color del tubo es un
        /// valor validado a ojo y no una temperatura medida: sustituirlo por el negro de 3500 K
        /// sería cambiarlo, y eso no es lo que se pide aquí.
        /// </summary>
        public static Color TintFor(float baseKelvin, float kelvin)
        {
            Color a = Mathf.CorrelatedColorTemperatureToRGB(Mathf.Max(1000f, baseKelvin));
            Color b = Mathf.CorrelatedColorTemperatureToRGB(Mathf.Max(1000f, kelvin));
            return new Color(
                b.r / Mathf.Max(a.r, 1e-4f),
                b.g / Mathf.Max(a.g, 1e-4f),
                b.b / Mathf.Max(a.b, 1e-4f));
        }
    }
}
