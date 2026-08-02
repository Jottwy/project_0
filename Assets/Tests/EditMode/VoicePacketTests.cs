using System;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-046 — el formato del datagrama de voz: N tramas Opus, cada una con su longitud en 2
    /// bytes big-endian por delante.
    ///
    /// Lo escribe <c>VoiceCapture</c> y lo deshace <c>RemoteVoicePlayer</c>, que viven en
    /// ENSAMBLADOS DISTINTOS, así que este contrato es justo el que puede divergir sin que nada
    /// se queje. Todo lo de aquí se verificó además ejecutándolo contra el Concentus real bajo el
    /// Mono de Unity, y con mutación: retirar cualquiera de las tres guardas, o invertir el orden
    /// de bytes de la cabecera, rompe uno de estos tests.
    /// </summary>
    [TestFixture]
    public class VoicePacketTests
    {
        // Una trama Opus de voz real ronda los 30-100 B; 60 es representativo.
        private static byte[] Payload(int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)((i * 17 + 3) & 0xff);
            return b;
        }

        [Test]
        public void TwoFramesRoundTripAndTheReaderConsumesTheDatagramWhole()
        {
            var packet = new byte[512];
            byte[] a = Payload(60), b = Payload(45);
            int end = 0;

            foreach (var frame in new[] { a, b })
            {
                int payloadStart = VoicePacket.PayloadOffset(packet, end);
                Assert.GreaterOrEqual(payloadStart, 0, "debe haber sitio para la cabecera");
                Array.Copy(frame, 0, packet, payloadStart, frame.Length);
                end = VoicePacket.SealFrame(packet, end, frame.Length);
                Assert.Greater(end, 0, "el sellado debe avanzar el paquete");
            }

            int offset = 0, recovered = 0;
            foreach (var expected in new[] { a, b })
            {
                int len = VoicePacket.ReadFrameLength(packet, offset, end);
                Assert.AreEqual(expected.Length, len, $"longitud de la trama {recovered}");
                var got = new byte[len];
                Array.Copy(packet, offset + VoicePacket.FrameHeaderBytes, got, 0, len);
                Assert.AreEqual(expected, got, $"contenido de la trama {recovered}");
                offset += VoicePacket.FrameHeaderBytes + len;
                recovered++;
            }

            Assert.AreEqual(2, recovered);
            Assert.AreEqual(end, offset, "el lector debe consumir el datagrama ENTERO, sin sobras");
            Assert.Less(VoicePacket.ReadFrameLength(packet, offset, end), 0,
                "y en el final exacto debe cortar el bucle");
        }

        /// <summary>
        /// La cabecera es BIG-ENDIAN. Fijado con bytes literales y no con un round-trip: un
        /// round-trip pasa igual si los dos lados se equivocan en el mismo sentido, y aquí los dos
        /// lados están en ensamblados distintos.
        /// </summary>
        [Test]
        public void TheLengthHeaderIsBigEndian()
        {
            var packet = new byte[600];
            VoicePacket.SealFrame(packet, 0, 300); // 300 = 0x012C

            Assert.AreEqual(0x01, packet[0], "byte alto primero");
            Assert.AreEqual(0x2C, packet[1], "byte bajo despues");
            Assert.AreEqual(300, VoicePacket.ReadFrameLength(packet, 0, 302));
        }

        /// <summary>
        /// Una longitud de 0 tiene que rechazarse: el lector avanza <c>2 + len</c> por vuelta, así
        /// que un 0 deja el offset quieto y el bucle no termina NUNCA. Un peer malicioso o un
        /// datagrama corrupto bastarían para colgar el hilo principal.
        /// </summary>
        [Test]
        public void AZeroLengthFrameIsRejectedBecauseItWouldHangTheReadLoop()
        {
            var packet = new byte[] { 0, 0, 9, 9 };
            Assert.Less(VoicePacket.ReadFrameLength(packet, 0, packet.Length), 0);
        }

        /// <summary>
        /// Todo datagrama mal formado es un DESCARTE SILENCIOSO, nunca una excepción: el emisor
        /// está al otro lado de una red no fiable y de un host que este cliente no controla, así
        /// que una excepción por trama sería una forma de tumbar el audio desde fuera.
        /// </summary>
        [Test]
        public void EveryMalformedDatagramDegradesToMinusOneInsteadOfThrowing()
        {
            var packet = new byte[64];
            VoicePacket.SealFrame(packet, 0, 20);

            Assert.Less(VoicePacket.ReadFrameLength(null, 0, 10), 0, "buffer nulo");
            Assert.Less(VoicePacket.ReadFrameLength(packet, -1, 64), 0, "offset negativo");
            Assert.Less(VoicePacket.ReadFrameLength(packet, 0, 3), 0,
                "el datagrama dice ser mas corto que su propia trama");

            // El caso que exige la guarda de cabecera: la longitud del datagrama es EXACTAMENTE
            // la del array, así que el segundo byte de la cabecera cae fuera del buffer. Con un
            // array holgado esto pasa desapercibido y revienta luego con un paquete real.
            Assert.Less(VoicePacket.ReadFrameLength(packet, packet.Length - 1, packet.Length), 0,
                "cabecera a caballo del final del array");

            Assert.Less(VoicePacket.SealFrame(packet, packet.Length - 1, 10), 0, "sellar pasado el final");
            Assert.Less(VoicePacket.SealFrame(packet, 0, 0), 0, "sellar una trama vacia");
            Assert.Less(VoicePacket.SealFrame(packet, 0, ushort.MaxValue + 1), 0,
                "una longitud que no cabe en la cabecera");
            Assert.Less(VoicePacket.SealFrame(null, 0, 10), 0, "buffer nulo");
            Assert.Less(VoicePacket.PayloadOffset(packet, packet.Length), 0, "sin sitio para la cabecera");
            Assert.Less(VoicePacket.PayloadOffset(null, 0), 0, "buffer nulo");
        }

        /// <summary>
        /// El contador de secuencia es <c>ushort</c> y ENVUELVE. La resta sin signo es lo que hace
        /// que 65535 → 0 se lea como un paso y no como 65535 tramas perdidas — que dispararía una
        /// ráfaga de ocultación justo cuando no se ha perdido nada.
        /// </summary>
        [Test]
        public void TheSequenceCounterWrapsWithoutInventingLostFrames()
        {
            // Variables y no literales: con literales el compilador pliega la resta y se niega a
            // convertirla, que es justo lo que NO ocurre en runtime — y runtime es lo que importa.
            ushort last = 65535, next = 0, twoLater = 2;

            Assert.AreEqual(1, (ushort)(next - last), "65535 -> 0 es UN paso");
            Assert.AreEqual(0, (ushort)(next - last) - 1, "y por tanto cero tramas perdidas");
            // 65535 → 0 → 1 → 2: la diferencia es 3 y las tramas PERDIDAS son 2 (la 0 y la 1).
            // Confundir una cosa con la otra mete un frame de ocultación de más en cada hueco.
            Assert.AreEqual(3, (ushort)(twoLater - last), "la diferencia de 65535 a 2 es 3");
            Assert.AreEqual(2, (ushort)(twoLater - last) - 1, "y por tanto se perdieron 2 tramas");

            // Una trama ATRASADA se reconoce porque la diferencia sin signo cae en la mitad alta
            // del rango. Sin esa regla, un paquete que llega tarde se leería como un salto de
            // 65000 tramas y dispararía una ráfaga de ocultación.
            ushort seen = 3, late = 65530;
            Assert.Greater((ushort)(late - seen), 32768, "atrasada: mitad alta");
            Assert.Less((ushort)(seen - late), 32768, "y su inversa, que es un envolvimiento normal");
        }
    }
}
