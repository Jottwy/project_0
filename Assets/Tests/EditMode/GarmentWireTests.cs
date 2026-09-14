using BackroomsSurvival.Net;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-149 enm. 7 + ADR-022 enm. (wire 69): la prenda de encima y la rotura de lo puesto viajan en `garments`
    /// ({ outer, damage: [u32; 5], cuts: [u16; 5] }). El receptor la lee con los 32 bits enteros y tolera claves
    /// desconocidas y listas de otro largo.
    /// </summary>
    public class GarmentWireTests
    {
        [Test]
        public void ElJugadorRemotoTraeSuRopaDeEncimaYSuRotura()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(3);
            w.WriteString("id"); w.WriteInt(7);
            w.WriteString("garments"); w.WriteMapHeader(4);
            w.WriteString("outer"); w.WriteInt(-1208217892);
            w.WriteString("damage"); w.WriteArrayHeader(5);
            foreach (uint v in new uint[] { 0u, 0x9u, 0xFFFFFFFFu, 0x10000001u, 42u }) w.WriteInt(v);
            w.WriteString("cuts"); w.WriteArrayHeader(6);
            foreach (ushort v in new ushort[] { 0, 1, 0xFFFF, 3, 4, 99 }) w.WriteInt(v);
            w.WriteString("desconocida"); w.WriteBool(true);
            w.WriteString("species"); w.WriteInt(0);

            var msg = RemotePlayerMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(7, msg.id);
            Assert.AreEqual(-1208217892, msg.outer);
            CollectionAssert.AreEqual(new uint[] { 0u, 0x9u, 0xFFFFFFFFu, 0x10000001u, 42u }, msg.garmentDamage,
                "la rotura de 32 bits llega entera");
            CollectionAssert.AreEqual(new ushort[] { 0, 1, 0xFFFF, 3, 4 }, msg.garmentCuts, "una lista más larga se recorta sin romper");
        }

        [Test]
        public void SinGarmentsTodoSanoYNadaEncima()
        {
            var w = new MsgPackWriter();
            w.WriteMapHeader(1);
            w.WriteString("id"); w.WriteInt(3);
            var msg = RemotePlayerMsg.Parse(new MsgPackReader(w.ToArray()));
            Assert.AreEqual(0, msg.outer);
            CollectionAssert.AreEqual(new uint[5], msg.garmentDamage);
            CollectionAssert.AreEqual(new ushort[5], msg.garmentCuts);
        }

        [Test]
        public void ElEsquemaEsEl69()
        {
            Assert.AreEqual(69u, WireSchema.Expected, "ADR-149 enm. 7: garments en la pose");
        }
    }
}
