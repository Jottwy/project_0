using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.Gameplay.Medical;
using NUnit.Framework;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149 R2c: la venda por brazo se deriva del cuerpo por zonas sin cambiar lo que Joel validó.</summary>
    public class BodyMedicalBridgeTests
    {
        [Test]
        public void ElBrazoSaleDeSusZonas()
        {
            var body = new BodyState();
            Assert.AreEqual(BodyPartCondition.Healthy, BodyMedicalBridge.ArmCondition(body, BodyPartSide.Left, false));

            body.ApplyDamage(BodyZone.ForearmL, 16f, DamageType.Slash);
            Assert.AreEqual(BodyPartCondition.Wounded, BodyMedicalBridge.ArmCondition(body, BodyPartSide.Left, false));
            Assert.AreEqual(BodyPartCondition.Healthy, BodyMedicalBridge.ArmCondition(body, BodyPartSide.Right, false));

            body.Treat(BodyZone.ForearmL, BodyTreatment.Bandage);
            Assert.AreEqual(BodyPartCondition.Bandaged, BodyMedicalBridge.ArmCondition(body, BodyPartSide.Left, false));

            body.ApplyDamage(BodyZone.HandL, 10f, DamageType.Slash);
            Assert.AreEqual(BodyPartCondition.Wounded, BodyMedicalBridge.ArmCondition(body, BodyPartSide.Left, false), "una herida abierta manda");

            var fractured = new BodyState();
            fractured.ApplyDamage(BodyZone.ForearmR, 35f, DamageType.Blunt);
            Assert.AreEqual(BodyPartCondition.Healthy, BodyMedicalBridge.ArmCondition(fractured, BodyPartSide.Right, false),
                "una fractura no se venda");
        }

        [Test]
        public void ConElBrazoIzquierdoForzadoTodoVaALaIzquierda()
        {
            var body = new BodyState();
            body.ApplyDamage(BodyZone.UpperArmR, 10f, DamageType.Slash);
            body.ApplyDamage(BodyZone.ForearmR, 16f, DamageType.Slash);
            Assert.AreEqual(BodyPartCondition.Wounded, BodyMedicalBridge.ArmCondition(body, BodyPartSide.Left, true));
            Assert.AreEqual(BodyPartCondition.Healthy, BodyMedicalBridge.ArmCondition(body, BodyPartSide.Right, true));

            Assert.IsTrue(BodyMedicalBridge.TryPickArmZone(body, BodyPartSide.Left, true, out var zone));
            Assert.AreEqual(BodyZone.ForearmR, zone, "el corte antes que el rasguño");
            Assert.IsFalse(BodyMedicalBridge.TryPickArmZone(body, BodyPartSide.Right, true, out _));
            Assert.IsFalse(BodyMedicalBridge.TryPickArmZone(new BodyState(), BodyPartSide.Left, true, out _), "sin herida no se gasta venda");
        }

        [Test]
        public void ConElCuerpoActivoLaVendaPasaPorElCuerpo()
        {
            var medical = new PlayerMedicalState { RestrictToLeftArm = true, BodyDriven = true };
            Assert.IsNull(medical.ReportDamage(50f, Vector3.zero, Vector3.zero, null), "con cuerpo, las heridas no las abre el daño local");

            var body = new BodyState();
            body.ApplyDamage(BodyZone.ForearmL, 16f, DamageType.Slash);
            int changes = 0;
            medical.Changed += (_, _) => changes++;
            BodyMedicalBridge.Sync(body, medical);
            Assert.IsTrue(medical.HasTreatableWound);
            Assert.IsTrue(medical.TryGetWoundedSide(out var side));
            Assert.AreEqual(BodyPartSide.Left, side);
            Assert.AreEqual(1, changes);

            medical.BandageOverride = s =>
                BodyMedicalBridge.TryPickArmZone(body, s, true, out var z) && body.Treat(z, BodyTreatment.Bandage);
            Assert.IsTrue(medical.ApplyBandage(BodyPartSide.Left), "la venda trata la zona del cuerpo");
            BodyMedicalBridge.Sync(body, medical);
            Assert.IsTrue(medical.IsBandaged(BodyPartSide.Left));
            Assert.IsFalse(medical.ApplyBandage(BodyPartSide.Left), "no se gasta otra venda en lo vendado");
        }
    }
}
