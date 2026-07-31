using System.Collections.Generic;
using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-034 — `room_zones` en `GridChunkData`. Campo ADITIVO: el backend lo
    /// omite del wire cuando está vacío (`skip_serializing_if`), y un backend
    /// anterior al ADR no lo manda nunca. Los dos casos tienen que decodificar
    /// sin error, así que el eje del fixture es "con campo" vs "sin campo".
    ///
    /// Round-trip msgpack REAL (encode con MsgPackWriter → decode con el mismo
    /// MsgPackReader que usa IPCClient → Parse), mismo patrón que
    /// <see cref="PillarNibbleTests"/>: un test que llamara a Parse con un
    /// diccionario montado a mano no probaría el decoder, que es donde vive el
    /// riesgo real de un campo nuevo.
    /// </summary>
    [TestFixture]
    public class RoomZoneWireTests
    {
        /// <summary>Escribe cx/cz/layer/walls; `zones` null ⇒ la clave room_zones NO se emite.</summary>
        private static Dictionary<string, object> EncodeAndDecode(RoomZoneMsg[] zones)
        {
            var writer = new MsgPackWriter();
            writer.WriteMapHeader(zones == null ? 4 : 5);
            writer.WriteString("cx"); writer.WriteInt(3);
            writer.WriteString("cz"); writer.WriteInt(-7);
            writer.WriteString("layer"); writer.WriteInt(0);
            writer.WriteString("walls");
            writer.WriteArrayHeader(GridChunkDataMsg.Tiles);
            for (int x = 0; x < GridChunkDataMsg.Tiles; x++)
            {
                writer.WriteArrayHeader(GridChunkDataMsg.Tiles);
                for (int z = 0; z < GridChunkDataMsg.Tiles; z++)
                    writer.WriteInt(0x0F);
            }

            if (zones != null)
            {
                writer.WriteString("room_zones");
                writer.WriteArrayHeader(zones.Length);
                foreach (var z in zones)
                {
                    writer.WriteMapHeader(5);
                    writer.WriteString("x0"); writer.WriteInt(z.x0);
                    writer.WriteString("z0"); writer.WriteInt(z.z0);
                    writer.WriteString("x1"); writer.WriteInt(z.x1);
                    writer.WriteString("z1"); writer.WriteInt(z.z1);
                    writer.WriteString("kind"); writer.WriteInt(z.kindByte);
                }
            }

            var decoded = new MsgPackReader(writer.ToArray()).ReadValue() as Dictionary<string, object>;
            Assert.IsNotNull(decoded, "el round-trip msgpack debe producir un mapa");
            return decoded;
        }

        private static RoomZoneMsg Zone(byte x0, byte z0, byte x1, byte z1, byte kind) =>
            new RoomZoneMsg { x0 = x0, z0 = z0, x1 = x1, z1 = z1, kindByte = kind };

        /// <summary>
        /// COMPATIBILIDAD HACIA ATRÁS — el caso que de verdad importa: un chunk
        /// sin la clave `room_zones` (backend viejo, o `num_open_zones == 0`)
        /// sigue decodificando, con el resto del mensaje intacto y `roomZones`
        /// como array VACÍO, nunca null.
        /// </summary>
        [Test]
        public void ChunkWithoutRoomZonesKeyStillParses()
        {
            var msg = GridChunkDataMsg.Parse(EncodeAndDecode(null));

            Assert.AreEqual(3, msg.cx);
            Assert.AreEqual(-7, msg.cz);
            Assert.AreEqual(0, msg.layer);
            Assert.AreEqual(0x0F, msg.walls[0, 0], "el bitmask no debe verse afectado");
            Assert.IsNotNull(msg.roomZones, "roomZones nunca debe ser null");
            Assert.AreEqual(0, msg.roomZones.Length, "sin la clave ⇒ array vacío");
        }

        /// <summary>Los 3 tipos sobreviven al round-trip con sus coordenadas exactas.</summary>
        [Test]
        public void RoomZonesSurviveTheRoundTripWithExactCoordinates()
        {
            var sent = new[]
            {
                Zone(2, 4, 10, 12, (byte)RoomZoneKind.Open),
                Zone(0, 0, 8, 8, (byte)RoomZoneKind.SealedRoom),
                Zone(6, 14, 18, 18, (byte)RoomZoneKind.CorridorSpine),
            };

            var msg = GridChunkDataMsg.Parse(EncodeAndDecode(sent));

            Assert.AreEqual(0x0F, msg.walls[0, 0], "el bitmask no debe verse afectado");
            Assert.AreEqual(sent.Length, msg.roomZones.Length);
            for (int i = 0; i < sent.Length; i++)
            {
                Assert.AreEqual(sent[i].x0, msg.roomZones[i].x0, $"zona {i}: x0");
                Assert.AreEqual(sent[i].z0, msg.roomZones[i].z0, $"zona {i}: z0");
                Assert.AreEqual(sent[i].x1, msg.roomZones[i].x1, $"zona {i}: x1");
                Assert.AreEqual(sent[i].z1, msg.roomZones[i].z1, $"zona {i}: z1");
                Assert.AreEqual(sent[i].kindByte, msg.roomZones[i].kindByte, $"zona {i}: kind");
            }

            Assert.AreEqual(RoomZoneKind.Open, msg.roomZones[0].Kind);
            Assert.AreEqual(RoomZoneKind.SealedRoom, msg.roomZones[1].Kind);
            Assert.AreEqual(RoomZoneKind.CorridorSpine, msg.roomZones[2].Kind);
        }

        /// <summary>
        /// COMPATIBILIDAD HACIA DELANTE — un `kind` que este cliente no conoce
        /// (backend más nuevo con un cuarto RoomType) colapsa a Open, el tipo sin
        /// perímetro sellado, en vez de castear a un valor de enum inválido. El
        /// byte crudo se conserva por si algún consumidor futuro lo quiere.
        /// </summary>
        [Test]
        public void UnknownKindCollapsesToOpen()
        {
            var msg = GridChunkDataMsg.Parse(EncodeAndDecode(new[] { Zone(2, 2, 6, 6, 99) }));

            Assert.AreEqual(1, msg.roomZones.Length);
            Assert.AreEqual(99, msg.roomZones[0].kindByte, "el byte crudo debe conservarse");
            Assert.AreEqual(RoomZoneKind.Open, msg.roomZones[0].Kind);
        }

        /// <summary>
        /// `ContainsCell` respeta el contrato del backend: x1/z1 EXCLUSIVOS. Un
        /// off-by-one aquí pintaría la variante de sala una fila de celdas más
        /// allá del rect real.
        /// </summary>
        [Test]
        public void ContainsCellTreatsUpperBoundAsExclusive()
        {
            var zone = Zone(2, 4, 6, 8, (byte)RoomZoneKind.SealedRoom);

            Assert.IsTrue(zone.ContainsCell(2, 4), "esquina inferior incluida");
            Assert.IsTrue(zone.ContainsCell(5, 7), "última celda interior incluida");
            Assert.IsFalse(zone.ContainsCell(6, 7), "x1 excluido");
            Assert.IsFalse(zone.ContainsCell(5, 8), "z1 excluido");
            Assert.IsFalse(zone.ContainsCell(1, 4), "por debajo de x0");
            Assert.IsFalse(zone.ContainsCell(2, 3), "por debajo de z0");
        }
    }
}
