namespace BackroomsSurvival.Net
{
    /// <summary>
    /// La mezcla determinista (worldSeed, chunkX, chunkZ) → 64 bits que usa TODO lo que tiene que
    /// dar el mismo resultado para el mismo chunk en el host y en el joiner.
    ///
    /// **Por qué existe este fichero.** La mezcla vivía en dos sitios: aquí, dentro de
    /// <see cref="ChunkLootRoll.Hash"/>, y en <c>ChunkRenderer.Level0Profile.FromSeedAndPos</c>,
    /// que se declaraba «el patrón del proyecto» y al que este fichero decía espejar. Ese renderer
    /// llevaba inerte desde que <c>GameBootstrap</c> dejó de añadirlo y se borró el 2026-09-05: la
    /// mezcla se extrae aquí para que la copia que sobrevive tenga nombre propio en vez de ser el
    /// espejo de algo que ya no está.
    ///
    /// **Y las dos copias NO eran iguales.** El renderer escribía <c>((ulong)cx)</c>, que
    /// EXTIENDE EL SIGNO: para <c>cx = -1</c> daba <c>0xFFFF_FFFF_FFFF_FFFF</c>. Aquí siempre se
    /// escribió <c>((ulong)(uint)cx)</c>, que da <c>0x0000_0000_FFFF_FFFF</c>. Divergían en TODO
    /// chunk de coordenada negativa, o sea en tres cuartas partes del mundo. Se conserva la
    /// variante de este fichero, byte a byte, porque es la que ha decidido el loot de todas las
    /// partidas jugadas: cambiarla movería el botín de cada chunk ya visitado.
    /// </summary>
    public static class ChunkCoordHash
    {
        /// <summary>
        /// Mezcla (semilla, coordenadas de chunk, sal) → 64 bits. La sal separa canales: dos usos
        /// distintos sobre el MISMO chunk deben ser independientes entre sí, no la misma tirada.
        /// </summary>
        public static ulong Mix(long worldSeed, int cx, int cz, ulong salt)
        {
            ulong h = (ulong)worldSeed ^ 0x9E3779B97F4A7C15UL ^ salt;
            h += ((ulong)(uint)cx) * 0xFF51AFD7ED558CCDUL;
            h ^= h >> 33;
            h += ((ulong)(uint)cz) * 0xC4CEB9FE1A85EC53UL;
            h ^= h >> 29;
            h *= 0x9E3779B185EBCA87UL;
            h ^= h >> 32;
            return h;
        }
    }
}
