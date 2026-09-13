using System.IO;
using BackroomsSurvival.Gameplay.Body;
using BackroomsSurvival.Wearables;
using NUnit.Framework;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>ADR-149 enm. 1, R1: ropa por zonas, bolsillos que se tachan y se cosen, y que el prototipo solo viva en la escena de pruebas.</summary>
    public class GarmentZonesTests
    {
        private const string TestScenePath = "Assets/Scenes/BR_InventoryTest.unity";
        private const string ShowcasePath = "Assets/PolymindGames/STP/Demo/Scenes/Showcase/STP_Showcase.unity";

        private static GarmentZonesData Jacket() => new GarmentZonesData(
            new GarmentZone(BodyZone.Chest, 0.2f, 2), new GarmentZone(BodyZone.Abdomen, 0.2f, 2), new GarmentZone(BodyZone.UpperArmL, 0.2f));

        [Test]
        public void LaTelaSeRompeSegunElGolpe()
        {
            Assert.AreEqual(GarmentDamage.Intact, GarmentState.DamageFor(DamageType.Blunt, 50f), "un golpe no rompe tela");
            Assert.AreEqual(GarmentDamage.Intact, GarmentState.DamageFor(DamageType.Fall, 50f));
            Assert.AreEqual(GarmentDamage.Cut, GarmentState.DamageFor(DamageType.Ballistic, 5f), "una bala hace agujero");
            Assert.AreEqual(GarmentDamage.Cut, GarmentState.DamageFor(DamageType.Pierce, 30f));
            Assert.AreEqual(GarmentDamage.Cut, GarmentState.DamageFor(DamageType.Slash, 10f));
            Assert.AreEqual(GarmentDamage.Torn, GarmentState.DamageFor(DamageType.Slash, 25f), "un zarpazo fuerte desgarra");
            Assert.AreEqual("desgarro · bolsillo roto", GarmentState.Describe(GarmentDamage.Torn, true));
            Assert.AreEqual(string.Empty, GarmentState.Describe(GarmentDamage.Intact, false));
        }

        [Test]
        public void UnaBalaEnUnaZonaConBolsilloLoRompeSiempre()
        {
            var data = Jacket();
            var state = new GarmentState();
            Assert.AreEqual(2, data.PocketStart(1));
            Assert.AreEqual(1, data.ZoneOfPocketSlot(3));
            Assert.AreEqual(-1, data.ZoneOfPocketSlot(4));

            Assert.AreEqual(0.2f, state.Protection(0, data.Zones[0]), 1e-4f, "sana protege entera");
            Assert.IsTrue(state.ApplyHit(0, data.Zones[0], DamageType.Ballistic, 12f, out bool broke));
            Assert.IsTrue(broke, "sin sorteo: el bolsillo se rompe siempre");
            Assert.AreEqual(0.2f * GarmentState.CutFactor, state.Protection(0, data.Zones[0]), 1e-4f);
            state.ApplyHit(0, data.Zones[0], DamageType.Ballistic, 12f, out broke);
            Assert.IsFalse(broke, "ya estaba roto");
            Assert.IsFalse(state.ApplyHit(2, data.Zones[2], DamageType.Blunt, 40f, out _), "un golpe no rompe la manga");
            Assert.IsTrue(state.ApplyHit(2, data.Zones[2], DamageType.Slash, 30f, out broke));
            Assert.IsFalse(broke, "la manga no tiene bolsillo");

            Assert.AreEqual(2, state.BrokenPocketSlots(data));
            Assert.IsTrue(state.IsPocketSlotBroken(data, 0));
            Assert.IsTrue(state.IsPocketSlotBroken(data, 1));
            Assert.IsFalse(state.IsPocketSlotBroken(data, 2), "el bolsillo del abdomen sigue sano");
            Assert.IsTrue(BackroomsGarmentPrototype.IsBlocked(data, state, 4), "más allá de los bolsillos no se guarda nada");
        }

        [Test]
        public void CoserDevuelveElBolsilloYLaCintaNo()
        {
            var data = Jacket();
            var state = new GarmentState();
            state.ApplyHit(1, data.Zones[1], DamageType.Slash, 25f, out _);
            Assert.IsTrue(state.NeedsCloth(1), "un desgarro pide tela para coserse");
            int version = GarmentState.Version;

            Assert.IsTrue(state.Repair(1, GarmentRepair.Tape));
            Assert.AreEqual(0.2f * GarmentState.PatchedFactor, state.Protection(1, data.Zones[1]), 1e-4f, "cinta al 60 %");
            Assert.IsTrue(state.IsPocketBroken(1), "la cinta no devuelve el bolsillo");
            Assert.IsFalse(state.CanRepair(1, GarmentRepair.Tape), "no se pone cinta sobre cinta");
            Assert.IsTrue(state.NeedsRepair(1), "con cinta todavía se puede coser");

            Assert.IsTrue(state.Repair(1, GarmentRepair.Sew));
            Assert.AreEqual(0.2f * GarmentState.SewnFactor, state.Protection(1, data.Zones[1]), 1e-4f, "cosido al 90 %");
            Assert.IsFalse(state.IsPocketBroken(1), "coser devuelve los huecos");
            Assert.IsFalse(state.NeedsRepair(1));
            Assert.Greater(GarmentState.Version, version, "la UI se entera");
        }

        [Test]
        public void LaRestriccionDejaSoloLosHuecosSanos()
        {
            Assert.AreEqual(1, GarmentPocketRestriction.Evaluate(4, 2, 1, false, false, 1, "Work Jacket").allowed);
            var full = GarmentPocketRestriction.Evaluate(4, 2, 2, false, false, 1, "Work Jacket");
            Assert.AreEqual(0, full.allowed);
            StringAssert.Contains("2 huecos sanos", full.reason);
            Assert.AreEqual(1, GarmentPocketRestriction.Evaluate(4, 2, 2, true, false, 1, "Work Jacket").allowed, "apilar en lo que ya hay sí");
            Assert.AreEqual(0, GarmentPocketRestriction.Evaluate(4, 4, 0, false, false, 1, "Work Jacket").allowed, "todo roto");
            Assert.AreEqual(0, GarmentPocketRestriction.Evaluate(0, 0, 0, false, false, 1, string.Empty).allowed, "sin bolsillos");
            Assert.AreEqual(0, GarmentPocketRestriction.Evaluate(4, 0, 0, false, true, 1, "Work Jacket").allowed, "una prenda no va en un bolsillo");
            StringAssert.Contains("llenos", GarmentPocketRestriction.Evaluate(4, 0, 4, false, false, 1, "Work Jacket").reason);
        }

        [Test]
        public void LaEscenaMontaBolsillosCosturaYElPrototipo()
        {
            var jacket = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/Resources/Definitions/Item/BR_Work Jacket.asset");
            Assert.IsTrue(jacket.TryGetDataOfType(out GarmentZonesData jacketZones), "la chaqueta sin zonas");
            Assert.AreEqual(4, jacketZones.PocketSlots);
            var trousers = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/Resources/Definitions/Item/BR_Work Trousers.asset");
            Assert.IsNotNull(trousers, "falta el pantalón de trabajo");
            Assert.IsTrue(trousers.TryGetDataOfType(out GarmentZonesData legs));
            Assert.AreEqual(2, legs.PocketSlots);
            var legsTag = AssetDatabase.LoadAssetAtPath<ItemTagDefinition>("Assets/PolymindGames/STP/Data/Resources/Definitions/ItemTag/STP_Legs Equipment.asset");
            Assert.AreEqual(legsTag.Id, (int)trousers.Tag, "el pantalón va en el hueco Legs del vendor");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/Resources/Definitions/Item/BR_Sewing Needle.asset"), "falta la aguja");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/Resources/Definitions/Item/BR_Thread.asset"), "falta el hilo");

            string scene = File.ReadAllText(TestScenePath);
            StringAssert.Contains("value: OuterPockets", scene);
            StringAssert.Contains("value: LegsPockets", scene);
            StringAssert.Contains("Pad_Bala", scene, "falta la placa de bala para romper un bolsillo");
            var guids = AssetDatabase.FindAssets("BackroomsGarmentPrototype t:MonoScript");
            Assert.IsNotEmpty(guids);
            StringAssert.Contains(guids[0], scene, "la escena de pruebas no monta la ropa por zonas");
            StringAssert.DoesNotContain(guids[0], File.ReadAllText(ShowcasePath), "STP_Showcase lleva la ropa por zonas");

            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/BR_UI_Player.prefab");
            bool outer = false, legsPockets = false;
            foreach (var ui in variant.GetComponentsInChildren<BackroomsWornSlotsUI>(true))
            {
                string owner = new SerializedObject(ui).FindProperty("_ownerContainer").stringValue;
                outer |= owner == "Outer";
                legsPockets |= owner == "Legs";
            }
            Assert.IsTrue(outer && legsPockets, "faltan las secciones de bolsillos");
        }
    }
}
