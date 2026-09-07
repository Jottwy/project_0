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

        /// <summary>ADR-130 D4 (r2b) — cuánto de lo que sigue vivo se lleva la profundidad. Suavizado
        /// el 07-09 (Joel: más Level 0, el fondo no debe leerse casi apagado): con 0,60 y el 12 % de
        /// base, el fondo servido queda con el 65 % de los plafones muertos, no el 82 % de antes.</summary>
        public const float DecayOffShare = 0.60f;

        /// <summary>ADR-130 D4 (r2b) — cuánto de los SUPERVIVIENTES parpadea en el fondo. Suavizado
        /// junto a <see cref="DecayOffShare"/> el 07-09.</summary>
        public const float DecayFlickerShare = 0.30f;

        /// <summary>ADR-130 D4 (r2b) — cuánto brillo pierde el color en el fondo servido. Suavizado
        /// junto a <see cref="DecayOffShare"/> el 07-09.</summary>
        public const float DecayDimShare = 0.35f;

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
        /// <param name="decay">ADR-130 D4 — el decaimiento de la planta
        /// (<see cref="Wg3StoreyLayers.DecayOfFloor"/>), 0 en la calle y 1 en el sótano más hondo.
        /// **Por defecto 0, y con 0 esta función devuelve exactamente lo de antes**: es lo que deja
        /// que el arnés, el rig y la escena de prueba sigan llamándola sin saber de sótanos.</param>
        public static Wg3Fixture Resolve(int worldSeed, float worldX, float worldZ,
            int fixtureIndex, float cellX, float cellZ, Wg3LightCadenceSettings s,
            float decay = 0f)
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

            // ADR-130 D4 (r2b) — LA PROFUNDIDAD MUEVE LOS UMBRALES, NO AÑADE UNA TIRADA.
            //
            // Ésta es la única forma de meter el decaimiento aquí sin romper el mundo. El encabezado
            // de esta clase dice que el orden de las tiradas es contrato: una tirada nueva, aunque
            // fuera la última, correría el flujo de TODOS los fixtures de los sótanos y cambiaría de
            // sitio cada lámpara rota que ya se ha visto en una captura. Modulando el umbral, `state`
            // sigue siendo el mismo número para el mismo plafón — sólo se mueve la frontera que
            // decide qué le pasa, y una lámpara que se apaga al bajar es una que ya estaba cerca del
            // borde.
            //
            // El apagado come de lo que queda vivo (`1 − off`), así que nunca pasa de 1 y en la calle
            // vale exactamente `off`. Con 0,80 el fondo servido deja el 82 % de los plafones muertos.
            float d = Mathf.Clamp01(decay);
            off = off + (1f - off) * d * DecayOffShare;
            // El parpadeo se lleva parte de los supervivientes, no de todo el tramo: en el fondo, de
            // los pocos que siguen encendidos casi todos parpadean, que es la lectura que se busca —
            // un sótano no está «medio iluminado», está «a punto de quedarse a oscuras».
            flick = flick + (1f - off) * d * DecayFlickerShare;

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

        /// <summary>Sal del DESPACHO A OSCURAS, "DOFF". Cada decisión abre su propio flujo (R3):
        /// ésta no puede salir del mismo <see cref="Wg3Hash.Stream"/> que <see cref="Resolve"/>
        /// porque no se decide por fixture, sino por planta y chunk.</summary>
        private const uint DarkOfficeSalt = 0x444F4646u;

        /// <summary>Sal del MONITOR encendido, "MONI".</summary>
        private const uint MonitorSalt = 0x4D4F4E49u;

        /// <summary>Papel de un espacio, espejo de <c>fill::style_of</c>. Los dos que necesita este
        /// fichero: el resto vive en <see cref="Wg3StyleMaterials"/>, que es quien los viste.</summary>
        public const byte StyleOffice = 0;
        public const byte StyleCorridor = 2;

        /// <summary>Uno de cada cinco monitores tiene la pantalla encendida.</summary>
        private const float MonitorLitChance = 0.2f;

        /// <summary>Sal de la LUMINARIA QUE FALTA, "PMIS". Flujo propio (R3): no se decide por
        /// fixture de luz sino por hueco de la retícula del falso techo, y los dos repartos son
        /// distintos.</summary>
        private const uint PanelMissingSalt = 0x504D4953u;

        /// <summary>ADR-130 D4 (r2b) — fracción de luminarias arrancadas del falso techo en el
        /// fondo servido. Menos que <see cref="DecayOffShare"/> a propósito: un techo sin NINGUNA
        /// luminaria deja de leerse como oficina y pasa a leerse como túnel. Suavizado junto a los
        /// otros tres el 07-09 (Joel: más Level 0).</summary>
        public const float DecayPanelMissingShare = 0.30f;

        /// <summary>
        /// Si a esta posición del falso techo le FALTA la luminaria: el marco arrancado que se ve en
        /// cualquier foto de oficina abandonada.
        ///
        /// **Se siembra con la posición en centímetros enteros y la sal propia, no con el índice de
        /// la retícula.** Un tramo de 25 m y otro de 5 m tienen retículas distintas y su luminaria
        /// (0,0) cae en sitios diferentes: con el índice, los dos primeros huecos de todos los tramos
        /// de una planta se arrancarían a la vez. Y en centímetros porque es como viaja la geometría
        /// por el cable — cuantizar el float sería meter una diferencia entre dos clientes por un
        /// redondeo, que es el mismo motivo por el que <see cref="MonitorLit"/> lo hace así.
        ///
        /// **La cota entra**, por lo mismo que en <see cref="MonitorLit"/>: la retícula de una planta
        /// se reparte igual que la de la de abajo, y sin la Y se arrancaría la misma columna de
        /// luminarias en las tres o cuatro plantas del edificio.
        /// </summary>
        public static bool PanelMissing(int xCm, int yCm, int zCm, float decay)
        {
            float d = Mathf.Clamp01(decay);
            if (d <= 0f) return false;
            ulong h = Wg3Hash.Mix(xCm, yCm, zCm, unchecked((int)PanelMissingSalt));
            return Wg3Hash.ToUnit(h) < d * DecayPanelMissingShare;
        }

        /// <summary>
        /// EL DESPACHO A OSCURAS: un espacio de oficina por planta y chunk con TODAS las lámparas
        /// muertas, no el 12 % que le tocaría por la cadencia.
        ///
        /// # Por qué por chunk y no por región
        ///
        /// «Uno por planta» hace falta resolverlo SIN ver la planta entera: el cliente monta un chunk
        /// cada vez y nunca tiene delante la lista de despachos de su cota. Así que el sorteo va al
        /// revés — en vez de elegir un despacho entre los que hay, se elige un PUNTO del chunk y se
        /// apaga el despacho que lo contenga. Como los espacios de una misma planta no se solapan en
        /// planta, **a lo sumo uno lo contiene**: sale exactamente un despacho a oscuras por chunk y
        /// planta, o ninguno si el punto cae en un pasillo, en una nave o en el vacío.
        ///
        /// Ninguno es un resultado correcto, no un fallo: un edificio en el que TODAS las plantas
        /// tienen su despacho apagado sería otro patrón regular, que es justo lo que la cadencia vino
        /// a romper.
        ///
        /// El chunk se toma del CENTRO del espacio —el mismo criterio de propiedad que usa el
        /// servidor para repartir tramos— para que un despacho a caballo de la frontera no reciba dos
        /// tiradas ni se lo dispute nadie.
        /// </summary>
        /// <param name="storey">Planta CRUDA (<see cref="Wg3StoreyLayers.RawStoreyOf"/>), no la capa
        /// de render: la capa se acota a ocho y desplaza por los sótanos, así que dos plantas
        /// distintas pueden compartirla y compartirían el sorteo.</param>
        public static bool IsDarkOffice(int worldSeed, byte style, int storey,
            float minX, float minZ, float sizeX, float sizeZ)
        {
            if (style != StyleOffice) return false;

            float centreX = minX + sizeX * 0.5f;
            float centreZ = minZ + sizeZ * 0.5f;
            Vector2Int chunk = ChunkOf(centreX, centreZ);

            ulong local = Wg3Hash.Mix(chunk.x, chunk.y, storey, unchecked((int)DarkOfficeSalt));
            ulong seed = Wg3Hash.Mix(worldSeed, chunk.x, chunk.y, unchecked((int)local));
            var rng = new Wg3Hash.Stream(seed);

            float targetX = (chunk.x + rng.Next01()) * Wg3ChunkStreamer.ChunkSize;
            float targetZ = (chunk.y + rng.Next01()) * Wg3ChunkStreamer.ChunkSize;

            return targetX >= minX && targetX < minX + sizeX
                && targetZ >= minZ && targetZ < minZ + sizeZ;
        }

        /// <summary>
        /// Si la pantalla de ESTE monitor está encendida. Uno de cada cinco.
        ///
        /// Se siembra con la posición del ancla y NADA MÁS —ni semilla de mundo ni índice—, que es la
        /// misma regla con la que <see cref="Wg3PropCatalog.Prefab"/> elige la variante del mueble:
        /// el ancla ya la decidió el servidor a partir de la semilla, así que volver a meterla aquí
        /// no añade una sola tirada independiente. Y en centímetros ENTEROS, que es como viaja por el
        /// cable: cuantizar el float sería introducir una diferencia entre dos clientes por un
        /// redondeo.
        ///
        /// **LA COTA ENTRA EN EL HASH, y sin ella esto estaba mal.** Los cubículos de una planta se
        /// reparten igual que los de la de abajo, así que el mismo puesto de trabajo tiene su monitor
        /// en el mismo (x, z) en las tres o cuatro plantas del edificio: con un hash de dos ejes, una
        /// columna entera de monitores se enciende o se apaga a la vez. Medido en la primera pasada
        /// del arnés — 34 encendidos de 92 donde tocaban 18, porque los encendidos venían en pilas de
        /// tres y de cuatro.
        /// </summary>
        public static bool MonitorLit(int xCm, int yCm, int zCm)
        {
            ulong h = Wg3Hash.Mix(xCm, yCm, zCm, unchecked((int)MonitorSalt));
            return Wg3Hash.ToUnit(h) < MonitorLitChance;
        }

        /// <summary>
        /// ADR-130 D4 (r2b) — el color de una lámpara VISTO DESDE LA PROFUNDIDAD: más gris y más
        /// apagado cuanto más abajo.
        /// </summary>
        /// <remarks>
        /// **Va sobre el color final y no sobre el tinte del fixture, y con el tinte no funcionaba.**
        /// <see cref="Wg3Fixture.tint"/> es un COCIENTE alrededor de (1,1,1) —±200 K de desviación—,
        /// así que desaturarlo no desatura nada: lo que hay que llevar a gris es el cálido validado
        /// (1 · 0,96 · 0,78) con el que se multiplica. Por eso esto se aplica en el ensamblador,
        /// después del producto, y no dentro de <see cref="Resolve"/>.
        ///
        /// El gris es la LUMINANCIA del propio color y no el 0,5 neutro: así la calle sale idéntica
        /// bit a bit (decaimiento 0 ⇒ interpolación 0) y el fondo pierde el tono sin ganar ni perder
        /// brillo por el camino — el brillo lo quita el segundo factor, que es el que se mide.
        ///
        /// **Sin tocar `intensity` ni `range`, a propósito.** Los dos números están validados por
        /// Joel (2,7 / 11 m) y el alcance además es lo que paga el clustering de Forward+: oscurecer
        /// por color no cambia ni una cosa ni la otra.
        /// </remarks>
        public static Color Decayed(Color lit, float decay)
        {
            float d = Mathf.Clamp01(decay);
            if (d <= 0f) return lit;
            float grey = lit.r * 0.2126f + lit.g * 0.7152f + lit.b * 0.0722f;
            var flat = Color.Lerp(lit, new Color(grey, grey, grey, lit.a), d);
            float dim = 1f - DecayDimShare * d;
            return new Color(flat.r * dim, flat.g * dim, flat.b * dim, lit.a);
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
