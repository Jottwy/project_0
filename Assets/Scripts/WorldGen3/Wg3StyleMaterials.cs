using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// EL ÚNICO SITIO QUE DECIDE CON QUÉ SE VISTE UN ESPACIO.
    ///
    /// El servidor clasifica cada espacio por su papel y manda el número en <c>style</c>
    /// (<c>fill::style_of</c>: 1 espina, 2 pasillo/cruce, 3 nave, 4 servicio/almacén, 5 callejón,
    /// 6 escalera, 0 oficina). Ese byte viajaba por el cable desde el wire 48 y **no lo leía nadie**:
    /// un pasillo, un almacén y una nave se dibujaban idénticos, y la partida con dos plantas se
    /// resumió en «no sé dónde ir a subir».
    ///
    /// Aquí se resuelve, y se resuelve en UNA función a propósito. El aspecto va a dejar de depender
    /// solo del papel —la identidad de nivel es un segundo eje que está en discusión— y un punto de
    /// decisión se amplía con un argumento; seis casos incrustados en el ensamblador se reescriben.
    ///
    /// # Por qué tinte y no una paleta autorada
    ///
    /// Vestir seis papeles con materiales propios son 24 assets que nadie ha dibujado todavía. El
    /// tinte multiplica sobre los cuatro materiales base, así que sale del mismo sitio que hoy —los
    /// valores que Joel ya miró— y se diferencia sin inventar arte. Cuando existan materiales
    /// autorados, entran por esta misma función y el tinte pasa a ser el caso por defecto.
    ///
    /// # POR QUÉ LOS PAPELES SE SEPARAN POR TONO Y NO POR BRILLO
    ///
    /// La primera versión los separaba por claridad —la espina más clara que el pasillo, y así— y
    /// **estaba por debajo del suelo de ruido**. Medido sobre una captura real de la sesión en vivo:
    /// dos paredes del mismo fotograma se separan un **29,5 %** de luminancia sólo por dónde cae el
    /// plafón, mientras que espina contra pasillo se separaban un **9,5 %** por diseño, y pasillo
    /// contra oficina un **5,0 %**. Una diferencia tres veces menor que la que ya produce la luz
    /// sobre el mismo material no se lee como «otro sitio»: se lee como sombra.
    ///
    /// El tono sobrevive a eso y el brillo no, y la razón es la que hace que la regla se pueda
    /// aplicar sin volver a medir: **una lámpara sube o baja los tres canales a la vez, así que no
    /// invierte su orden**. Un verde por encima del rojo y del azul —el servicio— sigue siendo verde
    /// a plena luz y en penumbra. Por eso la única pareja que se separa por brillo es nave contra
    /// pasillo, y sólo porque además no se confunden nunca: uno mide diez metros de ancho y el otro
    /// dos.
    ///
    /// Consecuencia para quien añada papeles o ejes: **un delta de albedo del 10 % no existe**. Si
    /// hay que distinguir dos cosas, se mueven en tono o en material, nunca en claridad.
    ///
    /// FUGAS: las variantes se crean en tiempo de ejecución. Se cachean por estilo y son 4 × 7 como
    /// mucho para toda la sesión — NO se destruyen al podar un chunk, porque las comparten todos los
    /// que sigan montados. <see cref="ClearCache"/> existe para el editor, que regenera el mundo sin
    /// reiniciar el proceso.
    /// </summary>
    public static class Wg3StyleMaterials
    {
        /// <summary>Multiplicadores sobre el color base, en el orden de submalla de
        /// <see cref="Wg3MeshBuilder.SubMesh"/>: suelo, estructura, techo, decoración.</summary>
        private struct Tint
        {
            public Color floor, structure, ceiling, decoration;
        }

        private static Tint TintFor(byte style)
        {
            switch (style)
            {
                // La espina es el eje de la región: ámbar franco, el más cálido de los seis. Se lee
                // como «por aquí se va a alguna parte» y es el opuesto exacto del pasillo.
                case 1: return Make(1.12f, 1.02f, 0.78f, 1.14f, 1.03f, 0.79f);
                // Pasillo y cruce: FRÍO, y ahí está el trabajo. Separarlo de la espina por brillo no
                // funcionaba —ver la nota de arriba—, así que se separa por el lado contrario del
                // tono: la espina tira a ámbar y el pasillo a azul, y entre los dos hay un 30 % de
                // azul-contra-rojo que ninguna lámpara puede fabricar.
                case 2: return Make(0.90f, 0.97f, 1.06f, 0.92f, 0.98f, 1.07f);
                // Nave: hormigón frío y OSCURO. Comparte el frío del pasillo —los dos son azules— y
                // se separa de él por brillo, que es la única vez que eso vale: una nave y un pasillo
                // no se confunden nunca porque uno mide diez metros de ancho y el otro dos.
                case 3: return Make(0.76f, 0.84f, 1.00f, 0.74f, 0.82f, 0.99f);
                // Servicio y almacén: verde enfermo. El verde por encima del rojo Y del azul es un
                // orden de canales que no produce ninguna luz de la escena, todas cálidas.
                case 4: return Make(0.80f, 0.94f, 0.72f, 0.78f, 0.92f, 0.70f);
                // Callejón: pardo rojizo y apagado. Cálido como la espina pero con el verde hundido,
                // que es lo que lo separa de ella: un ámbar sin verde es marrón.
                case 5: return Make(0.96f, 0.70f, 0.52f, 0.94f, 0.68f, 0.50f);
                // ESCALERA. El único sitio del que se sale por arriba, y hasta hoy se vestía de
                // oficina. Estructura neutra fría y decoración FRANCAMENTE anaranjada: el rodapié
                // hace de señal, y una señal se lee por lo que contrasta contra lo que la rodea.
                case 6: return MakeStair();
                // Oficina y cualquier número que el servidor añada mañana: el juego base, sin tocar.
                default: return Make(1f, 1f, 1f, 1f, 1f, 1f);
            }
        }

        /// <summary>Un tinte de suelo y otro para estructura/techo/decoración. Son los dos ejes que
        /// se distinguen andando: lo que pisas y lo que te rodea.</summary>
        private static Tint Make(float fr, float fg, float fb, float sr, float sg, float sb)
        {
            var floor = new Color(fr, fg, fb, 1f);
            var rest = new Color(sr, sg, sb, 1f);
            return new Tint { floor = floor, structure = rest, ceiling = rest, decoration = rest };
        }

        private static Tint MakeStair()
        {
            return new Tint
            {
                floor = new Color(0.90f, 0.94f, 0.98f, 1f),
                structure = new Color(0.98f, 1.00f, 1.04f, 1f),
                ceiling = new Color(0.94f, 0.96f, 1.00f, 1f),
                decoration = new Color(1.20f, 0.58f, 0.26f, 1f),
            };
        }

        private static readonly Dictionary<byte, Material[]> Cache = new Dictionary<byte, Material[]>();
        /// <summary>Con qué juego base se llenó la caché. Si el ensamblador llega con otro —la escena
        /// de pruebas y la de juego no comparten materiales— la caché entera está mintiendo.</summary>
        private static Wg3Materials _cachedFor;

        /// <summary>Los cuatro materiales con los que se dibuja un espacio de este papel. Devuelve
        /// <c>null</c> si no hay juego base, que es lo mismo que hacía el ensamblador antes.</summary>
        public static Material[] Resolve(Wg3Materials baseSet, byte style)
        {
            if (baseSet == null) return null;
            if (!ReferenceEquals(_cachedFor, baseSet)) ClearCache(baseSet);

            if (Cache.TryGetValue(style, out Material[] cached) && Valid(cached)) return cached;

            Material[] mats = Build(baseSet, style);
            Cache[style] = mats;
            return mats;
        }

        /// <summary>Una variante puede morir por debajo —recarga de dominio, cambio de escena— y
        /// asignar un material destruido pinta en rosa sin decir por qué.</summary>
        private static bool Valid(Material[] mats)
        {
            if (mats == null) return false;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] == null) return false;
            return true;
        }

        private static Material[] Build(Wg3Materials baseSet, byte style)
        {
            Material[] source = baseSet.AsArray();
            if (style == 0) return source;

            Tint tint = TintFor(style);
            var factors = new[] { tint.floor, tint.structure, tint.ceiling, tint.decoration };
            var mats = new Material[source.Length];

            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == null) { mats[i] = null; continue; }

                var variant = new Material(source[i])
                {
                    name = $"{source[i].name}_s{style}",
                    hideFlags = HideFlags.DontSave,
                };

                // URP Lit escribe en `_BaseColor`; `_Color` sobrevive de los materiales convertidos
                // desde Built-in y algunos shaders lo siguen leyendo. Escribir sólo uno deja el
                // tinte a medias según el shader que traiga el material base.
                if (variant.HasProperty("_BaseColor"))
                    variant.SetColor("_BaseColor", source[i].GetColor("_BaseColor") * factors[i]);
                if (variant.HasProperty("_Color"))
                    variant.SetColor("_Color", source[i].GetColor("_Color") * factors[i]);

                mats[i] = variant;
            }

            return mats;
        }

        /// <summary>Tira las variantes creadas. La escena de pruebas regenera el mundo sin reiniciar
        /// el proceso, y sin esto cada regeneración deja atrás su juego de materiales.</summary>
        public static void ClearCache(Wg3Materials newBaseSet = null)
        {
            foreach (KeyValuePair<byte, Material[]> entry in Cache)
            {
                if (entry.Key == 0 || entry.Value == null) continue; // el 0 es el juego base: no es nuestro
                for (int i = 0; i < entry.Value.Length; i++)
                {
                    if (entry.Value[i] == null) continue;
                    if (Application.isPlaying) Object.Destroy(entry.Value[i]);
                    else Object.DestroyImmediate(entry.Value[i]);
                }
            }

            Cache.Clear();
            _cachedFor = newBaseSet;
        }
    }
}
