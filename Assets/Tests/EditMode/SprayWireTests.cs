using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-068 — el espejo C# de `world::spray::Spray`. Round-trip msgpack REAL (encode con
    /// MsgPackWriter → decode con el mismo MsgPackReader que usa IPCClient → Parse), mismo
    /// criterio que <see cref="RoomZoneWireTests"/>: un test que llamara a Parse con un
    /// diccionario montado a mano no probaría el decoder, que es donde vive el riesgo.
    ///
    /// Dos cosas concretas que estos tests existen para no equivocar:
    /// el blob de puntos viaja como `bin` (no como lista de enteros), y las coordenadas
    /// guardadas son LOCALES al chunk — quien las lea como globales pinta la pared equivocada
    /// en cualquier chunk que no sea el (0,0).
    /// </summary>
    [TestFixture]
    public class SprayWireTests
    {
        /// <summary>Los 10 pares de una pintada, SIN cabecera de mapa: la escribe el llamante,
        /// que es lo único que cambia entre la forma anidada y la plana.</summary>
        private const int SprayPairs = 10;

        private static void WriteSprayPairs(MsgPackWriter w, uint id, int cx, int cz, byte layer,
            float lx, float ly, float lz, ulong tick, byte[] points)
        {
            w.WriteString("id"); w.WriteInt((int)id);
            w.WriteString("cx"); w.WriteInt(cx);
            w.WriteString("cz"); w.WriteInt(cz);
            w.WriteString("layer"); w.WriteInt(layer);
            w.WriteString("local_pos");
            w.WriteArrayHeader(3);
            w.WriteFloat(lx); w.WriteFloat(ly); w.WriteFloat(lz);
            w.WriteString("yaw"); w.WriteFloat(90f);
            w.WriteString("size");
            w.WriteArrayHeader(2);
            w.WriteFloat(1.5f); w.WriteFloat(1f);
            w.WriteString("author"); w.WriteInt(7);
            w.WriteString("tick"); w.WriteInt((int)tick);
            w.WriteString("strokes");
            w.WriteArrayHeader(1);
            w.WriteMapHeader(3);
            w.WriteString("color"); w.WriteInt(3);
            w.WriteString("width"); w.WriteInt(6);
            w.WriteString("points"); w.WriteBin(points);
        }

        /// <summary>Envelope root-tagged real: Dispatch consume el header y el par "type".</summary>
        private static MsgPackReader OpenFrame(byte[] bytes, string expectedType, out int remaining)
        {
            var reader = new MsgPackReader(bytes);
            int n = reader.ReadMapHeader();
            Assert.IsTrue(n > 0, "el round-trip msgpack debe producir un mapa");
            Assert.IsTrue(MsgPackReader.Is(reader.ReadKey(), "type"));
            Assert.AreEqual(expectedType, reader.ReadString());
            remaining = n - 1;
            return reader;
        }

        private static GridChunkDataMsg EncodeChunk(int sprayCount, int cx, int cz)
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(sprayCount < 0 ? 5 : 6);
            w.WriteString("type"); w.WriteString("chunk_data");
            w.WriteString("cx"); w.WriteInt(cx);
            w.WriteString("cz"); w.WriteInt(cz);
            w.WriteString("layer"); w.WriteInt(0);
            w.WriteString("walls");
            w.WriteArrayHeader(GridChunkDataMsg.Tiles);
            for (int x = 0; x < GridChunkDataMsg.Tiles; x++)
            {
                w.WriteArrayHeader(GridChunkDataMsg.Tiles);
                for (int z = 0; z < GridChunkDataMsg.Tiles; z++)
                    w.WriteInt(0x0F);
            }

            if (sprayCount >= 0)
            {
                w.WriteString("sprays");
                w.WriteArrayHeader(sprayCount);
                for (int i = 0; i < sprayCount; i++)
                {
                    w.WriteMapHeader(SprayPairs); // anidada: cada pintada trae su propio header
                    WriteSprayPairs(w, (uint)(i + 1), cx, cz, 0, 12.5f, 1.6f, 33f,
                        (ulong)((i + 1) * 10), new byte[] { 0, 0, 128, 200, 255, 255 });
                }
            }

            var reader = OpenFrame(w.ToArray(), "chunk_data", out int remaining);
            return GridChunkDataMsg.Parse(reader, remaining);
        }

        /// <summary>
        /// COMPATIBILIDAD HACIA ATRÁS, el caso que de verdad importa: la inmensa mayoría de los
        /// chunks no tienen ni una pintada y el backend omite la clave entera. Debe decodificar
        /// con el resto del mensaje intacto y `sprays` como array VACÍO, nunca null.
        /// </summary>
        [Test]
        public void ChunkWithoutSpraysKeyStillParses()
        {
            var msg = EncodeChunk(-1, 3, -7);

            Assert.AreEqual(3, msg.cx);
            Assert.AreEqual(-7, msg.cz);
            Assert.IsNotNull(msg.sprays, "sprays nunca puede ser null");
            Assert.AreEqual(0, msg.sprays.Length);
            Assert.AreEqual(0x0F, msg.walls[0, 0], "el resto del mensaje intacto");
        }

        [Test]
        public void SpraysRideTheChunkInRenderOrder()
        {
            var msg = EncodeChunk(3, 3, -7);

            Assert.AreEqual(3, msg.sprays.Length);
            // El orden es el del backend (por tick ascendente) y el cliente NO lo reordena: la
            // última se dibuja encima, que es cómo se tapa la pintada de otro.
            Assert.AreEqual(10ul, msg.sprays[0].tick);
            Assert.AreEqual(30ul, msg.sprays[2].tick);
        }

        /// <summary>
        /// El anclaje de ADR-068 decisión 3 visto desde el cliente. Se usa un chunk que NO es el
        /// (0,0) a propósito: dentro del primero, local y global coinciden y este test pasaría
        /// aunque la traducción estuviera rota.
        /// </summary>
        [Test]
        public void LocalCoordinatesBecomeWorldCoordinatesOnRead()
        {
            var msg = EncodeChunk(1, 3, -7);
            var spray = msg.sprays[0];

            Assert.AreEqual(12.5f, spray.lx, 1e-4f, "lo guardado es LOCAL");
            Assert.AreEqual(33f, spray.lz, 1e-4f);

            var world = spray.WorldPos;
            Assert.AreEqual(3 * 50f + 12.5f, world.x, 1e-3f);
            Assert.AreEqual(1.6f, world.y, 1e-4f, "la Y no es chunk-local");
            Assert.AreEqual(-7 * 50f + 33f, world.z, 1e-3f);
        }

        /// <summary>
        /// El blob de puntos viaja como `bin`, igual que la voz de ADR-046, y debe volver byte a
        /// byte: es lo único del mensaje que no es un escalar y donde un decoder equivocado
        /// produciría un dibujo plausible pero distinto.
        /// </summary>
        [Test]
        public void StrokePointsSurviveAsRawBytes()
        {
            var msg = EncodeChunk(1, 0, 0);
            var stroke = msg.sprays[0].strokes[0];

            Assert.AreEqual(3, stroke.color);
            Assert.AreEqual(6, stroke.width);
            Assert.AreEqual(new byte[] { 0, 0, 128, 200, 255, 255 }, stroke.points);
            Assert.AreEqual(3, stroke.PointCount, "X e Y intercalados: la mitad de bytes");
        }

        /// <summary>
        /// El frame suelto `spray_placed`. serde APLANA la variante de tupla, así que los campos
        /// de la pintada vienen junto al tag y no anidados bajo una clave — misma forma que
        /// world_state, chunk_data y el hello que el backend fija byte a byte. Leerlo como
        /// anidado devolvería una pintada vacía sin dar ningún error.
        /// </summary>
        [Test]
        public void SprayPlacedFrameIsFlatBesideTheTypeTag()
        {
            var w = new MsgPackWriter();
            // UN solo mapa: el tag y los campos de la pintada al mismo nivel. Ahí está la
            // diferencia con la forma anidada, y es justo lo que este test fija.
            w.WriteMapHeader(1 + SprayPairs);
            w.WriteString("type"); w.WriteString("spray_placed");
            WriteSprayPairs(w, 42, 2, -2, 3, 10f, 1.8f, 20f, 99, new byte[] { 1, 2, 3, 4 });

            var reader = OpenFrame(w.ToArray(), "spray_placed", out int remaining);
            var spray = SprayPlacedMsg.Parse(reader, remaining);

            Assert.IsNotNull(spray, "el frame plano debe producir una pintada");
            Assert.AreEqual(42u, spray.id);
            Assert.AreEqual(2, spray.cx);
            Assert.AreEqual(-2, spray.cz);
            Assert.AreEqual(3, spray.layer, "la capa es parte de qué pared es");
            Assert.AreEqual(99ul, spray.tick);
            Assert.AreEqual(new byte[] { 1, 2, 3, 4 }, spray.strokes[0].points);
            Assert.AreEqual(new Vector3(2 * 50f + 10f, 1.8f, -2 * 50f + 20f), spray.WorldPos);
        }
    }
}
