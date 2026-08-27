using System.Runtime.CompilerServices;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Hash determinista de WG3. DUPLICADO ADAPTADO de <c>GridChunkBuilder.Tinting.CeilingHash</c>
    /// y de las derivaciones de <c>GridChunkBuilder.Props</c> (regla R4: WG3 no referencia a WG2,
    /// lo copia, para que borrar WG2 sea borrar ficheros y no desenredar dependencias).
    ///
    /// Se conserva la constante de la casa (0x9E3779B97F4A7C15 y los multiplicadores de murmur3)
    /// a propósito: cuando F2 lo espeje en Rust, el espejo tiene que ser bit a bit, y partir de un
    /// mixer ya usado en producción evita inventar uno cuyo sesgo nadie ha mirado.
    ///
    /// REGLA R3 — NO HAY RNG COMPARTIDO. Cada decisión abre su propio flujo a partir de datos
    /// que son función de la POSICIÓN en el mundo, nunca del orden de proceso. Es lo que permitirá
    /// que dos chunks vecinos lleguen a la misma respuesta sin hablarse (ruta A1, decisión abierta
    /// 1 del brief). Un `System.Random` compartido rompería eso aunque hoy, en una región finita,
    /// diese resultados idénticos.
    /// </summary>
    public static class Wg3Hash
    {
        /// <summary>Cuantización de coordenadas de mundo a enteros para sembrar por posición.
        /// 4 pasos por metro: por debajo de 25 cm dos sockets distintos no pueden coexistir, así
        /// que no hay colisión posible, y el entero cabe holgado en 32 bits para un mundo de
        /// ±500 km.</summary>
        public const float PositionQuantum = 4f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Quantize(float v) => (int)System.Math.Round(v * PositionQuantum);

        /// <summary>Mezcla de cuatro enteros a 64 bits. Mismo esqueleto que <c>CeilingHash</c>.</summary>
        public static ulong Mix(int a, int b, int c, int d)
        {
            unchecked
            {
                ulong h = 0x9E3779B97F4A7C15UL;
                h ^= (ulong)(uint)a * 0xFF51AFD7ED558CCDUL; h ^= h >> 33;
                h ^= (ulong)(uint)b * 0xC4CEB9FE1A85EC53UL; h ^= h >> 29;
                h ^= (ulong)(uint)c * 0x165667B19E3779F9UL; h ^= h >> 32;
                h ^= (ulong)(uint)d * 0x27D4EB2F165667C5UL; h ^= h >> 30;
                h *= 0x9E3779B185EBCA87UL; h ^= h >> 32;
                return h;
            }
        }

        /// <summary>Hash de una posición de mundo más una sal de propósito. La sal separa
        /// decisiones que ocurren en el MISMO punto (qué pieza, si taponar, qué variante) para que
        /// no queden correlacionadas — el mismo motivo por el que
        /// <c>GridChunkBuilder.Props</c> da una sal distinta a cada superficie.</summary>
        public static ulong AtPosition(int worldSeed, float x, float z, uint salt) =>
            Mix(worldSeed, Quantize(x), Quantize(z), unchecked((int)salt));

        /// <summary>Flotante en [0,1) a partir de un hash ya mezclado.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ToUnit(ulong h) => (h >> 11) * (1.0f / 9007199254740992.0f);

        /// <summary>Flujo determinista de flotantes. NO es un RNG global: se abre uno por decisión,
        /// sembrado por posición, y muere ahí mismo.</summary>
        public struct Stream
        {
            private ulong _state;

            public Stream(ulong seed)
            {
                // Un estado a cero deja splitmix64 produciendo la misma secuencia degenerada.
                _state = seed == 0UL ? 0x9E3779B97F4A7C15UL : seed;
            }

            /// <summary>splitmix64, el mismo avance que usa el mixer de arriba al cerrar.</summary>
            public ulong NextRaw()
            {
                unchecked
                {
                    _state += 0x9E3779B97F4A7C15UL;
                    ulong z = _state;
                    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                    return z ^ (z >> 31);
                }
            }

            public float Next01() => ToUnit(NextRaw());
        }

        public static Stream StreamAt(int worldSeed, float x, float z, uint salt) =>
            new Stream(AtPosition(worldSeed, x, z, salt));
    }
}
