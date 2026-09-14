using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.UI;
using NUnit.Framework;
using PolymindGames;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149, pestaña SASTRERÍA: qué arreglo cabe con lo que se lleva y qué se le dice al jugador.</summary>
    public class TailoringPanelTests
    {
        [Test]
        public void CoserYCintaDicenLoQueFalta()
        {
            var zone = new GarmentZone(BodyZone.Chest, 0.2f, 2);
            var state = new GarmentState();
            Assert.IsFalse(GarmentRepairPlan.CanSew(state, 0, 1, 1, 1, out string missing), "sana: nada que coser");
            Assert.AreEqual(string.Empty, missing);
            Assert.IsFalse(GarmentRepairPlan.CanTape(state, 0, 1, out _));

            state.ApplyHit(0, zone, DamageType.Slash, 30f, out _);
            Assert.IsFalse(GarmentRepairPlan.CanSew(state, 0, 0, 0, 0, out missing));
            Assert.AreEqual("aguja + hilo + tela", missing);
            Assert.IsFalse(GarmentRepairPlan.CanSew(state, 0, 1, 2, 0, out missing));
            Assert.AreEqual("tela", missing, "un desgarro pide tela");
            Assert.IsTrue(GarmentRepairPlan.CanSew(state, 0, 1, 2, 1, out _));
            Assert.IsFalse(GarmentRepairPlan.CanTape(state, 0, 0, out missing));
            Assert.AreEqual("cinta", missing);
            Assert.IsTrue(GarmentRepairPlan.CanTape(state, 0, 1, out _));

            state.Repair(0, GarmentRepair.Tape);
            Assert.IsFalse(GarmentRepairPlan.CanTape(state, 0, 5, out missing), "no se pone cinta sobre cinta");
            Assert.AreEqual(string.Empty, missing);
            Assert.IsTrue(GarmentRepairPlan.CanSew(state, 0, 1, 1, 0, out _), "sobre la cinta se cose sin tela");
        }

        [Test]
        public void LaLineaDeEstadoDiceDanoProteccionYFalta()
        {
            Assert.AreEqual("desgarro · bolsillo roto · protege 0 % · coser: falta tela",
                BackroomsTailoringPanel.StateLine(GarmentDamage.Torn, true, 0, "tela"));
            Assert.AreEqual("con cinta · protege 12 %", BackroomsTailoringPanel.StateLine(GarmentDamage.Patched, false, 12, string.Empty));
            Assert.AreEqual("bolsillo roto · protege 20 %", BackroomsTailoringPanel.StateLine(GarmentDamage.Intact, true, 20, null));
        }
    }
}
