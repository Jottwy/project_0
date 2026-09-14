using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.Net;
using NUnit.Framework;
using PolymindGames;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-072 + ADR-149: las propiedades de item viajan en float64. La rotura de una prenda (<c>Garment Zones</c>) empaqueta
    /// 32 bits y un float32 solo es exacto hasta 2^24: con float32 la rotura de las zonas 6 y 7 (los antebrazos de la chaqueta)
    /// se corrompía en el reporte de inventario, el guardado y los cadáveres.
    /// </summary>
    public class ItemPropsDoubleTests
    {
        [Test]
        public void UnDoubleDe32BitsSobreviveAlMsgPack()
        {
            double[] values = { 0d, 1d, 16777217d, 4294967295d, 0xF0F0F0F0u, -3.25d };
            var w = new MsgPackWriter();
            w.WriteArrayHeader(values.Length);
            foreach (var v in values) w.WriteDouble(v);

            var r = new MsgPackReader(w.ToArray());
            Assert.AreEqual(values.Length, r.ReadArrayHeader());
            foreach (var v in values)
                Assert.AreEqual(v, r.ReadDouble(), $"{v} no sobrevivió el round-trip");

            Assert.AreEqual(16777217d, IPCParse.ToDouble(16777217d), "el camino de diccionario tampoco trunca");
            Assert.AreNotEqual(16777217d, (double)(float)16777217d, "premisa: float32 NO guarda 2^24 + 1");
        }

        [Test]
        public void LaRoturaDeTodasLasZonasViajaIntacta()
        {
            var zones = new List<GarmentZone>();
            for (int i = 0; i < GarmentZonesData.MaxZones; i++) zones.Add(new GarmentZone((BodyZone)(i + 1), 0.1f, 1));
            var state = new GarmentState();
            for (int i = 0; i < GarmentZonesData.MaxZones; i++) state.ApplyHit(i, zones[i], DamageType.Slash, 30f, out _);
            uint packed = state.Pack();
            Assert.Greater(packed, 1u << 24, "premisa: con las 8 zonas rotas el empaquetado pasa de 24 bits");

            var w = new MsgPackWriter();
            w.WriteDouble(packed);
            double back = new MsgPackReader(w.ToArray()).ReadDouble();

            var copy = new GarmentState();
            copy.Unpack((uint)back);
            for (int i = 0; i < GarmentZonesData.MaxZones; i++)
            {
                Assert.AreEqual(GarmentDamage.Torn, copy.DamageOf(i), $"zona {i}");
                Assert.IsTrue(copy.IsPocketBroken(i), $"bolsillo {i}");
            }
        }
    }
}
