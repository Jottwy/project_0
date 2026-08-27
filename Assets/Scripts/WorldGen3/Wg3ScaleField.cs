namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// El campo de escala: qué TAMAÑO de espacio quiere el mundo en cada punto.
    ///
    /// Sustituye a L19/L20 tal y como estaban escritas. El documento pedía ponderar por "historial
    /// reciente de piezas", y eso es memoria del recorrido: exige un socket-walk global y hace
    /// imposible generar el chunk (500,300) sin haber generado la cadena que llega hasta él —
    /// justo la propiedad que hace infinito y transmitible a WG2.
    ///
    /// Aquí el ritmo deja de ser historia y pasa a ser un CAMPO horneado en el mapa. Ventajas
    /// sobre el historial, y no son pocas:
    ///  · es función pura de la posición, así que dos chunks vecinos coinciden sin hablarse (A1);
    ///  · el contraste está en el mapa, no en el camino: no depende de por dónde entres;
    ///  · el mismo sitio se siente igual al volver, que es lo que hace que un sitio se RECUERDE;
    ///  · lo ajusta un artista con dos números — el tamaño de celda es literalmente "cada cuántos
    ///    metros cambia el mundo de tamaño".
    ///
    /// R28 (control de densidad) es esta misma regla. Estaban duplicadas en el documento.
    /// </summary>
    public static class Wg3ScaleField
    {
        /// <summary>Celda gruesa, en metros: el grano al que el mundo cambia de tamaño.</summary>
        public const float CoarseCell = 46f;

        /// <summary>Celda fina. Desplazada y con otro grano para que la trama de la gruesa no se
        /// lea como una cuadrícula — que sería reintroducir por la puerta de atrás justo lo que
        /// WG3 viene a quitar.</summary>
        public const float FineCell = 29f;

        private const uint SaltCoarse = 0x5CA1E000u;
        private const uint SaltFine = 0x5CA1E001u;

        /// <summary>Valor crudo del campo en [0,1).</summary>
        public static float ValueAt(int worldSeed, float x, float z)
        {
            float coarse = Cell(worldSeed, x, z, CoarseCell, 0f, 0f, SaltCoarse);
            float fine = Cell(worldSeed, x, z, FineCell, 23f, 17f, SaltFine);
            return coarse * 0.66f + fine * 0.34f;
        }

        /// <summary>Clase de escala que el mundo pide en ese punto.</summary>
        public static Wg3Scale ScaleAt(int worldSeed, float x, float z)
        {
            float v = ValueAt(worldSeed, x, z);
            if (v < 0.34f) return Wg3Scale.Narrow;
            if (v < 0.70f) return Wg3Scale.Medium;
            if (v < 0.92f) return Wg3Scale.Large;
            return Wg3Scale.Weird;
        }

        /// <summary>Ruido de celda, vecino más próximo. Escalonado a propósito: el mundo cambia de
        /// escala al cruzar una frontera, no derivando poco a poco. Un gradiente suave se lee como
        /// terreno, y el terreno es lo contrario de lo liminal (L22).</summary>
        private static float Cell(int worldSeed, float x, float z, float size,
            float offX, float offZ, uint salt)
        {
            int cx = FloorDiv(x + offX, size);
            int cz = FloorDiv(z + offZ, size);
            return Wg3Hash.ToUnit(Wg3Hash.Mix(worldSeed, cx, cz, unchecked((int)salt)));
        }

        /// <summary>División con suelo. `(int)(v / size)` trunca hacia cero, así que −1 y +1
        /// caerían en la misma celda y el campo saldría espejado en el origen — el mismo fallo que
        /// obligó a usar `div_euclid` al tallar salas ancladas en el chunk vecino.</summary>
        private static int FloorDiv(float v, float size)
        {
            float q = v / size;
            int i = (int)q;
            return (q < 0f && q != i) ? i - 1 : i;
        }
    }
}
