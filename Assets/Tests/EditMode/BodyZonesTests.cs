using System.Collections.Generic;
using System.IO;
using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.UI;
using NUnit.Framework;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149 R0: cuerpo por zonas local. Resolver, lesiones y tratamientos puros; y que el prototipo solo viva en la escena de pruebas.</summary>
    public class BodyZonesTests
    {
        private const string TestScenePath = "Assets/Scenes/BR_InventoryTest.unity";
        private const string ShowcasePath = "Assets/PolymindGames/STP/Demo/Scenes/Showcase/STP_Showcase.unity";

        [Test]
        public void LasZonasSonQuinceConIndiceEstable()
        {
            Assert.AreEqual(15, System.Enum.GetValues(typeof(BodyZone)).Length);
            Assert.AreEqual(0, (int)BodyZone.Head);
            Assert.AreEqual(5, (int)BodyZone.ForearmL, "APPEND-ONLY: los índices no se mueven");
            Assert.AreEqual(14, (int)BodyZone.FootR);
            var labels = new HashSet<string>();
            foreach (BodyZone zone in System.Enum.GetValues(typeof(BodyZone)))
                Assert.IsTrue(labels.Add(BodyZones.Label(zone)), $"{zone} sin rótulo propio");
        }

        [Test]
        public void ElHuesoDaSuZona()
        {
            var goldens = new Dictionary<string, BodyZone>
            {
                { "Head", BodyZone.Head }, { "MiddleSpine", BodyZone.Chest }, { "Pelvis", BodyZone.Abdomen },
                { "UpperArm.L", BodyZone.UpperArmL }, { "LowerArm.R", BodyZone.ForearmR }, { "UpperLeg.L", BodyZone.ThighL },
                { "LowerLeg.R", BodyZone.ShinR }, { "Foot.L", BodyZone.FootL }, { "Hand.R", BodyZone.HandR },
            };
            foreach (var pair in goldens)
            {
                Assert.IsTrue(BodyZoneResolver.TryFromBone(pair.Key, out var zone), pair.Key);
                Assert.AreEqual(pair.Value, zone, pair.Key);
            }
            Assert.IsFalse(BodyZoneResolver.TryFromBone("Spine.Tail", out _), "un hueso desconocido no inventa zona");
        }

        [Test]
        public void LaAlturaYElLadoDanLaZona()
        {
            Assert.AreEqual(BodyZone.Head, BodyZoneResolver.FromLocalPoint(new Vector3(0f, 1.6f, 0f)));
            Assert.AreEqual(BodyZone.Chest, BodyZoneResolver.FromLocalPoint(new Vector3(0.05f, 1.3f, 0.2f)));
            Assert.AreEqual(BodyZone.UpperArmL, BodyZoneResolver.FromLocalPoint(new Vector3(-0.25f, 1.3f, 0f)));
            Assert.AreEqual(BodyZone.ForearmR, BodyZoneResolver.FromLocalPoint(new Vector3(0.3f, 0.95f, 0.2f)));
            Assert.AreEqual(BodyZone.Abdomen, BodyZoneResolver.FromLocalPoint(new Vector3(0f, 0.9f, 0f)));
            Assert.AreEqual(BodyZone.ThighL, BodyZoneResolver.FromLocalPoint(new Vector3(-0.1f, 0.6f, 0f)));
            Assert.AreEqual(BodyZone.ShinR, BodyZoneResolver.FromLocalPoint(new Vector3(0.1f, 0.3f, 0f)));
            Assert.AreEqual(BodyZone.FootR, BodyZoneResolver.FromLocalPoint(new Vector3(0.12f, 0.05f, 0.2f)), "la placa de cristales");
            Assert.AreEqual(BodyZone.Head, BodyZoneResolver.FromLocalPoint(new Vector3(0f, 0.8f, 0f), 0.85f), "las bandas escalan con la altura");
        }

        [Test]
        public void ElSorteoEsDeterministaYRespetaLaCausa()
        {
            foreach (var cause in new[] { DamageType.Fall, DamageType.Undefined })
            {
                int sum = 0;
                foreach (var w in BodyZoneResolver.WeightsFor(cause)) sum += w;
                Assert.AreEqual(100, sum, $"los pesos de {cause} suman 100");
                Assert.AreEqual(BodyZones.Count, BodyZoneResolver.WeightsFor(cause).Length);
            }
            for (uint seed = 0; seed < 500; seed++)
            {
                Assert.AreEqual(BodyZoneResolver.Draw(DamageType.Fall, seed), BodyZoneResolver.Draw(DamageType.Fall, seed), "misma semilla, misma zona");
                var zone = BodyZoneResolver.Draw(DamageType.Fall, seed);
                Assert.IsTrue(BodyZones.IsLeg(zone) || zone == BodyZone.HandL || zone == BodyZone.HandR, $"una caída no da en {zone}");
            }
            var seen = new HashSet<BodyZone>();
            for (uint seed = 0; seed < 2000; seed++) seen.Add(BodyZoneResolver.Draw(DamageType.Undefined, seed));
            Assert.AreEqual(BodyZones.Count, seen.Count, "sin causa, todas las zonas pueden salir");
        }

        [Test]
        public void ElDanoDecideLaLesion()
        {
            Assert.AreEqual(BodyInjury.None, BodyState.InjuryFor(5f, DamageType.Slash, BodyZone.FootL), "un roce no marca");
            Assert.AreEqual(BodyInjury.Scratch, BodyState.InjuryFor(10f, DamageType.Slash, BodyZone.FootL));
            Assert.AreEqual(BodyInjury.Cut, BodyState.InjuryFor(16f, DamageType.Slash, BodyZone.FootL));
            Assert.AreEqual(BodyInjury.Fracture, BodyState.InjuryFor(30f, DamageType.Fall, BodyZone.ShinR), "caída fuerte en la pierna");
            Assert.AreEqual(BodyInjury.Cut, BodyState.InjuryFor(30f, DamageType.Fall, BodyZone.HandL), "la mano que para la caída se corta");
            Assert.AreEqual(BodyInjury.Fracture, BodyState.InjuryFor(35f, DamageType.Blunt, BodyZone.ForearmL));
            Assert.AreEqual(BodyInjury.Cut, BodyState.InjuryFor(35f, DamageType.Blunt, BodyZone.Chest), "el tronco no se fractura de un golpe");
            Assert.AreEqual(BodyInjury.None, BodyState.InjuryFor(50f, DamageType.Poison, BodyZone.Chest), "el veneno no abre herida");
        }

        [Test]
        public void VendarCuraElCorteYLaFerulaAliviaLaFractura()
        {
            var body = new BodyState();
            int changes = 0;
            body.Changed += _ => changes++;

            Assert.AreEqual(BodyInjury.Cut, body.ApplyDamage(BodyZone.ForearmL, 16f, DamageType.Slash));
            Assert.IsTrue(body.IsBleeding(BodyZone.ForearmL));
            Assert.AreEqual(BodyState.BleedPerSecond * 2f, body.Tick(2f), 1e-4f, "un corte abierto sangra");
            Assert.AreEqual(BodyInjury.None, body.ApplyDamage(BodyZone.ForearmL, 10f, DamageType.Slash), "un rasguño no tapa un corte");

            Assert.IsFalse(body.Treat(BodyZone.ForearmL, BodyTreatment.Splint), "la férula no va en un corte");
            Assert.IsTrue(body.Treat(BodyZone.ForearmL, BodyTreatment.Bandage));
            Assert.IsFalse(body.Treat(BodyZone.ForearmL, BodyTreatment.Bandage), "no se venda dos veces");
            Assert.AreEqual(0f, body.Tick(1f), "vendado no sangra");
            body.Tick(BodyState.BandagedHealSeconds);
            Assert.AreEqual(BodyInjury.None, body.InjuryOf(BodyZone.ForearmL), "vendado, se cura");

            body.ApplyDamage(BodyZone.ShinR, 30f, DamageType.Fall);
            Assert.AreEqual(BodyState.FractureSpeed, body.LegSpeedMultiplier(), 1e-4f, "la fractura frena");
            Assert.AreEqual(BodyTreatment.Splint, BodyState.TreatmentFor(body.InjuryOf(BodyZone.ShinR)));
            Assert.IsTrue(body.Treat(BodyZone.ShinR, BodyTreatment.Splint));
            Assert.AreEqual(BodyState.SplintedFractureSpeed, body.LegSpeedMultiplier(), 1e-4f, "con férula frena menos");
            body.ApplyDamage(BodyZone.ShinR, 30f, DamageType.Fall);
            Assert.IsFalse(body.IsSplinted(BodyZone.ShinR), "otro golpe igual quita la férula");

            body.Clear();
            Assert.AreEqual(1f, body.LegSpeedMultiplier());
            Assert.Greater(changes, 0, "cada cambio avisa");
        }

        [Test]
        public void ElPrototipoSoloViveEnLaEscenaDePruebasYLaVistaTieneQuinceZonas()
        {
            foreach (var script in new[] { "BackroomsBodyPrototype", "BackroomsHurtPad" })
            {
                var guids = AssetDatabase.FindAssets($"{script} t:MonoScript");
                Assert.IsNotEmpty(guids, $"falta el script {script}");
                StringAssert.Contains(guids[0], File.ReadAllText(TestScenePath), $"la escena de pruebas no monta {script}");
                StringAssert.DoesNotContain(guids[0], File.ReadAllText(ShowcasePath), $"STP_Showcase lleva {script}");
            }
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/Resources/Definitions/Item/BR_Splint.asset"), "falta la férula");

            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/BR_UI_Player.prefab");
            var zones = new HashSet<BodyZone>();
            foreach (var ui in variant.GetComponentsInChildren<BackroomsWoundZoneUI>(true)) zones.Add(ui.Zone);
            Assert.AreEqual(BodyZones.Count, zones.Count, "la vista Heridas no tiene una casilla por zona");
            Assert.AreEqual("corte · sangra", BackroomsWoundZoneUI.Describe(BodyInjury.Cut, false, false));
        }
    }
}
