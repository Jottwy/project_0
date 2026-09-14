using BackroomsSurvival.Gameplay.Body;
using NUnit.Framework;
using PolymindGames;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149 enm. 7: lo que llega en la pose se desempaqueta igual que la propiedad del objeto.</summary>
    public class GarmentWireDecodeTests
    {
        [Test]
        public void CadaPrendaEncuentraSuHueco()
        {
            var equipment = new[] { 11, 22, 33, 44 };
            Assert.AreEqual(0, GarmentVisualState.WireSlot(11, equipment, 99));
            Assert.AreEqual(3, GarmentVisualState.WireSlot(44, equipment, 99));
            Assert.AreEqual(4, GarmentVisualState.WireSlot(99, equipment, 99), "la de encima va en el hueco 4");
            Assert.AreEqual(-1, GarmentVisualState.WireSlot(55, equipment, 99), "lo que no se lleva puesto no tiene hueco");
            Assert.AreEqual(-1, GarmentVisualState.WireSlot(0, equipment, 0), "el id 0 es «nada»");
            Assert.AreEqual(-1, GarmentVisualState.WireSlot(11, null, 0));
        }

        [Test]
        public void LaRoturaQueViajaEsLaDelObjeto()
        {
            var data = new GarmentZonesData(new GarmentZone(BodyZone.Chest, 0.2f, 2), new GarmentZone(BodyZone.ForearmL, 0.2f));
            var local = new GarmentState();
            local.ApplyHit(0, data.Zones[0], DamageType.Ballistic, 8f, out _);
            local.ApplyHit(1, data.Zones[1], DamageType.Slash, 30f, out _);

            var remote = new GarmentState();
            remote.ApplyHit(0, data.Zones[0], DamageType.Pierce, 8f, out _); // basura previa: el desempaquetado la pisa
            GarmentVisualState.StateFromWire(remote, local.Pack(), (ushort)local.PackCuts());

            var localCodes = new float[GarmentVisualState.Slots];
            var remoteCodes = new float[GarmentVisualState.Slots];
            GarmentVisualState.ZoneCodes(local, data, localCodes);
            GarmentVisualState.ZoneCodes(remote, data, remoteCodes);
            CollectionAssert.AreEqual(localCodes, remoteCodes);
            Assert.IsTrue(remote.IsPocketBroken(0));
        }
    }
}
