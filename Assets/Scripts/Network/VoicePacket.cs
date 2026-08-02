namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-046 — el formato de un datagrama de voz: N tramas Opus, cada una precedida por su
    /// longitud en 2 bytes big-endian.
    ///
    /// La cabecera de longitud es OBLIGATORIA y no un adorno: Opus no es auto-delimitado, así que
    /// un datagrama con dos tramas pegadas es indescifrable — el decodificador no puede saber
    /// dónde acaba la primera. Se agrupan dos tramas de 20 ms por datagrama porque a una sola la
    /// cabecera IP/UDP (28 B) pesaría casi tanto como la carga útil (~30-60 B medidos).
    ///
    /// Vive aquí, y no dentro del emisor, porque lo escriben y lo leen DOS archivos distintos
    /// (<see cref="VoiceCapture"/> y <see cref="RemoteVoicePlayer"/>) y una copia en cada lado es
    /// una copia que puede divergir. Estático y puro: se puede ejercitar sin Unity, que es la
    /// única forma de PROBAR este formato mientras el editor esté abierto.
    ///
    /// PÚBLICO por necesidad de ensamblados, no por diseño: el reproductor vive en
    /// Assembly-CSharp (necesita <c>ProxyAudioCurves</c>) y este archivo en el asmdef
    /// BackroomsSurvival, así que `internal` lo dejaría fuera de su alcance.
    /// </summary>
    public static class VoicePacket
    {
        /// <summary>Bytes de cabecera por trama.</summary>
        public const int FrameHeaderBytes = 2;

        /// <summary>
        /// Escribe la cabecera de longitud de una trama en <paramref name="offset"/> y devuelve
        /// dónde empieza el hueco donde el códec debe escribir. Devuelve -1 si no cabe.
        /// </summary>
        public static int PayloadOffset(byte[] packet, int offset)
        {
            if (packet == null) return -1;
            int start = offset + FrameHeaderBytes;
            return start < packet.Length ? start : -1;
        }

        /// <summary>
        /// Sella una trama ya codificada de <paramref name="written"/> bytes cuya carga empieza en
        /// <c>offset + FrameHeaderBytes</c>. Devuelve el nuevo final del paquete, o -1 si la
        /// longitud no cabe en la cabecera o se sale del buffer.
        /// </summary>
        public static int SealFrame(byte[] packet, int offset, int written)
        {
            if (packet == null || written <= 0 || written > ushort.MaxValue) return -1;
            int end = offset + FrameHeaderBytes + written;
            if (end > packet.Length) return -1;
            packet[offset] = (byte)(written >> 8);
            packet[offset + 1] = (byte)written;
            return end;
        }

        /// <summary>
        /// Lee la longitud de la trama que empieza en <paramref name="offset"/>.
        /// Devuelve -1 cuando la cabecera no cabe, la longitud es 0, o la trama se sale del
        /// buffer — es decir, ante CUALQUIER datagrama truncado o corrupto. El emisor está al
        /// otro lado de una red no fiable y de un host que no controlamos, así que un paquete mal
        /// formado tiene que ser un descarte silencioso y no una excepción por trama.
        /// </summary>
        public static int ReadFrameLength(byte[] packet, int offset, int packetLength)
        {
            if (packet == null || offset < 0) return -1;
            if (offset + FrameHeaderBytes > packetLength) return -1;
            int len = (packet[offset] << 8) | packet[offset + 1];
            if (len <= 0) return -1;
            if (offset + FrameHeaderBytes + len > packetLength) return -1;
            return len;
        }
    }
}
