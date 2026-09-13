using System.IO;
using BackroomsSurvival.Wearables;
using NUnit.Framework;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Prototipo de mochilas (ADR-147 PROPUESTA + enm. 1). Las condiciones de la auditoría son la mitad de esta
    /// suite: el prototipo vive SOLO en la escena de pruebas y el jugador real sigue con sus 6 contenedores, porque
    /// un contenedor 6+ en partida real se reportaría y el contenido empaquetado en memoria se perdería en el save.
    /// Las escenas y prefabs se leen como TEXTO: abrirlos en un test cambia la escena del que lo corre.
    /// </summary>
    public class BackroomsBackpackPrototypeTests
    {
        private const string PlayerPrefab = "Assets/PolymindGames/STP/Prefabs/Core/STP_Player.prefab";
        private const string ShowcasePath = "Assets/PolymindGames/STP/Demo/Scenes/Showcase/STP_Showcase.unity";
        private const string TestScenePath = "Assets/Scenes/BR_InventoryTest.unity";
        private const string TagPath = "Assets/Resources/Definitions/ItemTag/BR_Back Equipment.asset";

        private static readonly string[] BackpackPaths =
        {
            "Assets/Resources/Definitions/Item/BR_Cloth Bag.asset",
            "Assets/Resources/Definitions/Item/BR_Office Backpack.asset",
            "Assets/Resources/Definitions/Item/BR_Hiking Backpack.asset",
        };

        private static string WornStorageScriptGuid()
        {
            var guids = AssetDatabase.FindAssets("BackroomsWornStorage t:MonoScript");
            Assert.IsNotEmpty(guids, "falta el script BackroomsWornStorage");
            return guids[0];
        }

        [Test]
        public void ElJugadorRealSigueConSeisContenedores()
        {
            string prefab = File.ReadAllText(PlayerPrefab);
            StringAssert.Contains("propertyPath: _defaultContainers.Array.size\n      value: 6", prefab.Replace("\r\n", "\n"),
                "STP_Player cambió su número de contenedores: el prototipo no puede tocar el jugador real (ADR-147 enm. 1)");
            StringAssert.DoesNotContain("BackStorage", prefab);
        }

        [Test]
        public void ShowcaseNoLlevaElPrototipoYLaEscenaDePruebasSi()
        {
            string guid = WornStorageScriptGuid();
            string showcase = File.ReadAllText(ShowcasePath);
            StringAssert.DoesNotContain(guid, showcase, "STP_Showcase lleva el prototipo de mochilas");
            StringAssert.DoesNotContain("BackStorage", showcase);

            string test = File.ReadAllText(TestScenePath);
            StringAssert.Contains(guid, test, "la escena de pruebas no monta el prototipo: Backrooms/UI/Build Inventory Test Scene");
            StringAssert.Contains("BackStorage", test);
        }

        [Test]
        public void LasTresMochilasSonDeEspaldaNoSeApilanYTienenCapacidad()
        {
            var tag = AssetDatabase.LoadAssetAtPath<ItemTagDefinition>(TagPath);
            Assert.IsNotNull(tag, $"falta '{TagPath}': Backrooms/Inventory/Create Backpack Prototype");
            int previousSlots = 0;
            foreach (var path in BackpackPaths)
            {
                var def = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
                Assert.IsNotNull(def, $"falta '{path}'");
                Assert.AreEqual(tag.Id, (int)def.Tag, $"{def.name} no lleva el tag de espalda");
                Assert.AreEqual(1, def.StackSize, $"{def.name} se apila: el vendor funde pilas por id y mezclaría contenidos");
                Assert.IsTrue(def.TryGetDataOfType(out WearableCapacityData capacity), $"{def.name} sin WearableCapacityData");
                Assert.Greater(capacity.Slots, previousSlots, "las mochilas van de menos a más huecos (D2)");
                previousSlots = capacity.Slots;
            }
        }

        [Test]
        public void LaRestriccionCapaPorPrendaHuecosPesoYAnidado()
        {
            var bag = new WearableCapacityData(6, 6f, -10f);

            Assert.AreEqual(0, WornCapacityRestriction.Evaluate(null, "", 0, 0f, 1f, false, false, 1).allowed, "sin mochila, 0 huecos");
            Assert.AreEqual(0, WornCapacityRestriction.Evaluate(bag, "Bolsa", 0, 0f, 1f, false, true, 1).allowed, "mochila dentro de mochila");
            Assert.AreEqual(0, WornCapacityRestriction.Evaluate(bag, "Bolsa", 6, 1f, 0.1f, false, false, 1).allowed, "llena de huecos");
            Assert.AreEqual(3, WornCapacityRestriction.Evaluate(bag, "Bolsa", 6, 1f, 0.1f, true, false, 3).allowed,
                "apilar en una pila que ya está no gasta hueco");

            var (allowed, reason) = WornCapacityRestriction.Evaluate(bag, "Bolsa", 1, 5.5f, 1f, false, false, 1);
            Assert.AreEqual(0, allowed, "el máximo de la prenda IMPIDE guardar (D6 enmendado)");
            StringAssert.Contains("6 kg", reason);
            Assert.AreEqual(2, WornCapacityRestriction.Evaluate(bag, "Bolsa", 1, 4f, 1f, false, false, 5).allowed, "cabe hasta el máximo exacto");
        }

        [Test]
        public void LaVarianteAtaElHuecoDeEspaldaYElAlmacenDeLaMochila()
        {
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/BR_UI_Player.prefab");
            Assert.IsNotNull(variant, "falta BR_UI_Player: Backrooms/UI/Build Inventory Variant");
            var list = new SerializedObject(variant.GetComponentInChildren<InventoryUI>(true)).FindProperty("_nonPersistentContainers");
            bool back = false, storage = false;
            for (int i = 0; i < list.arraySize; i++)
            {
                if (!(list.GetArrayElementAtIndex(i).objectReferenceValue is ItemContainerUI ui)) continue;
                back |= ui.ContainerName == "Back" && DirectSlots(ui.transform) == 1;
                storage |= ui.ContainerName == "BackStorage" && ui.GetComponent<BackroomsWornSlotsUI>() != null
                           && DirectSlots(ui.transform) == 27;
            }
            Assert.IsTrue(back, "ningún panel atado a Back con su hueco real: la mochila puesta no se vería");
            Assert.IsTrue(storage, "ningún panel atado a BackStorage con BackroomsWornSlotsUI y 27 huecos creados en el builder");
        }

        /// <summary>Huecos generados en Play salen con la piel del vendor: tienen que existir ya en la variante.</summary>
        private static int DirectSlots(Transform t)
        {
            int n = 0;
            foreach (Transform child in t)
                if (child.GetComponent<ItemSlotUIBase>() != null) n++;
            return n;
        }

        [Test]
        public void LaBarraSonDosManosMasLoQueDaElCinturon()
        {
            var cord = new WearableCapacityData(2, 2f, 0f);
            Assert.AreEqual(1, WornCapacityRestriction.Evaluate(null, "", 1, 0f, 1f, false, false, 1, 2, false).allowed, "desnudo: 2 manos");
            Assert.AreEqual(0, WornCapacityRestriction.Evaluate(null, "", 2, 0f, 1f, false, false, 1, 2, false).allowed, "manos llenas");
            Assert.AreEqual(1, WornCapacityRestriction.Evaluate(cord, "Cordel", 3, 0f, 1f, false, false, 1, 2, false).allowed, "cinturón T1: 4");
            Assert.AreEqual(0, WornCapacityRestriction.Evaluate(cord, "Cordel", 4, 0f, 1f, false, false, 1, 2, false).allowed);
            Assert.AreEqual(1, WornCapacityRestriction.Evaluate(cord, "Cordel", 0, 99f, 1f, false, false, 1, 2, false).allowed,
                "la barra no tiene tope de kg propio: lo pone el total");
        }

        [Test]
        public void LosCinturonesSonDeCinturaYNoSeApilan()
        {
            var tag = AssetDatabase.LoadAssetAtPath<ItemTagDefinition>("Assets/Resources/Definitions/ItemTag/BR_Waist Equipment.asset");
            Assert.IsNotNull(tag, "falta el tag de cintura: Backrooms/Inventory/Create Backpack Prototype");
            int previous = 0;
            foreach (var name in new[] { "BR_Cord Belt", "BR_Work Belt", "BR_Tool Belt" })
            {
                var def = AssetDatabase.LoadAssetAtPath<ItemDefinition>($"Assets/Resources/Definitions/Item/{name}.asset");
                Assert.IsNotNull(def, $"falta {name}");
                Assert.AreEqual(tag.Id, (int)def.Tag);
                Assert.AreEqual(1, def.StackSize);
                Assert.IsTrue(def.TryGetDataOfType(out WearableCapacityData capacity));
                Assert.Greater(capacity.Slots, previous, "los tiers van de menos a más huecos");
                Assert.LessOrEqual(2 + capacity.Slots, 8, "la barra no pasa de las 8 teclas del vendor");
                previous = capacity.Slots;
            }
        }

        [Test]
        public void LaEscenaDePruebasDejaBaseDeNueveYBarraDeOcho()
        {
            string scene = File.ReadAllText(TestScenePath).Replace("\r\n", "\n");
            StringAssert.Contains("value: Waist", scene, "la escena no añade el contenedor de cintura");
            StringAssert.Contains("propertyPath: _defaultContainers.Array.data[0].MaxSlotCount\n      value: 9", scene, "base de 9");
            StringAssert.Contains("propertyPath: _defaultContainers.Array.data[1].MaxSlotCount\n      value: 8", scene, "barra de 8");

            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/BR_UI_Player.prefab");
            bool waist = false, holster = false;
            foreach (var ui in variant.GetComponentsInChildren<ItemContainerUI>(true))
            {
                waist |= ui.ContainerName == "Waist" && DirectSlots(ui.transform) == 1;
                holster |= ui.ContainerName == "Holster" && DirectSlots(ui.transform) == 8 && ui.GetComponent<BackroomsWornSlotsUI>() != null;
            }
            Assert.IsTrue(waist, "sin hueco de cintura en la columna del personaje");
            Assert.IsTrue(holster, "la barra no tiene 8 huecos creados ni su BackroomsWornSlotsUI");
        }

        [Test]
        public void QuitarseElCinturonDejaSoloLosHuecosDeLasManos()
        {
            Assert.AreEqual(2, BackroomsWornStorage.VisibleHandSlots(2, null, 8), "sin cinturón, las manos");
            Assert.AreEqual(4, BackroomsWornStorage.VisibleHandSlots(2, new WearableCapacityData(2, 2f, 0f), 8));
            Assert.AreEqual(8, BackroomsWornStorage.VisibleHandSlots(2, new WearableCapacityData(9, 2f, 0f), 8),
                "nunca más huecos de los creados");
        }

        [Test]
        public void ElPesoMaximoEsLaBaseMasLoPuesto()
        {
            Assert.AreEqual(10f, BackroomsCarryWeight.MaxWeight(10f, new WearableCapacityData[0]), "desnudo, la base");
            var worn = new[] { new WearableCapacityData(27, 25f, 20f), new WearableCapacityData(6, 6f, 5f) };
            Assert.AreEqual(41f, BackroomsCarryWeight.MaxWeight(10f, worn), 0.001f, "montaña + cinturón de herramientas");

            var guids = AssetDatabase.FindAssets("BackroomsCarryWeight t:MonoScript");
            Assert.IsNotEmpty(guids, "falta el script BackroomsCarryWeight");
            StringAssert.Contains(guids[0], File.ReadAllText(TestScenePath), "la escena de pruebas no monta BackroomsCarryWeight");
            StringAssert.DoesNotContain(guids[0], File.ReadAllText(ShowcasePath), "STP_Showcase lleva el máximo por equipo");
        }

        [Test]
        public void LosHuecosDelCinturonSeDespliegan()
        {
            Assert.AreEqual(0f, BackroomsWornSlotsUI.EaseOutBack(0f), 1e-4f);
            Assert.AreEqual(1f, BackroomsWornSlotsUI.EaseOutBack(1f), 1e-4f, "el hueco acaba en su tamaño");
            Assert.Greater(BackroomsWornSlotsUI.EaseOutBack(0.7f), 1f, "se pasa un poco y vuelve: se nota que se despliega");
            Assert.AreEqual(0f, BackroomsWornSlotsUI.StaggerDelay(0, 6), "el primero sale ya");
            Assert.Greater(BackroomsWornSlotsUI.StaggerDelay(3, 6), BackroomsWornSlotsUI.StaggerDelay(2, 6), "uno detrás de otro");
            Assert.LessOrEqual(BackroomsWornSlotsUI.StaggerDelay(26, 27), 0.25f, "una mochila grande no tarda un segundo en abrirse");
        }

        [Test]
        public void AlEncogerLaBarraSeCompactaYLoQueSobraSaleDesdeLaDerecha()
        {
            var kept = new System.Collections.Generic.List<int>();
            var overflow = new System.Collections.Generic.List<int>();

            Assert.IsFalse(BackroomsWornStorage.Compact(new[] { true, true, false, false, false, false }, 2, kept, overflow),
                "si todo cabe donde está, no se reordena nada");

            Assert.IsTrue(BackroomsWornStorage.Compact(new[] { true, false, true, false, true, false }, 4, kept, overflow));
            CollectionAssert.AreEqual(new[] { 0, 2, 4 }, kept, "sí, no, sí, no, sí: se juntan en orden");
            CollectionAssert.IsEmpty(overflow);

            Assert.IsTrue(BackroomsWornStorage.Compact(new[] { true, false, true, false, true, true, false, true }, 2, kept, overflow));
            CollectionAssert.AreEqual(new[] { 0, 2 }, kept, "los dos primeros se quedan en las manos");
            CollectionAssert.AreEqual(new[] { 7, 5, 4 }, overflow, "lo que sobra va a la base empezando por la derecha");
        }

        [Test]
        public void IntercambiarEnUnaMochilaLlenaNoOcupaHuecoNuevo()
        {
            Assert.AreEqual(5, WornCapacityRestriction.EffectiveUsedSlots(6, true), "el que sale deja su hueco");
            Assert.AreEqual(6, WornCapacityRestriction.EffectiveUsedSlots(6, false), "fuera de un intercambio cuenta todo");
            Assert.AreEqual(0, WornCapacityRestriction.EffectiveUsedSlots(0, true));

            var bag = new WearableCapacityData(6, 6f, -10f);
            Assert.AreEqual(0, WornCapacityRestriction.Evaluate(bag, "Bolsa", 6, 1f, 0.1f, false, false, 1).allowed, "llena: no entra nada nuevo");
            Assert.AreEqual(1, WornCapacityRestriction.Evaluate(bag, "Bolsa", WornCapacityRestriction.EffectiveUsedSlots(6, true),
                1f, 0.1f, false, false, 1).allowed, "llena pero intercambiando: sí");

            WornCapacityRestriction.BeginSwap();
            WornCapacityRestriction.EndSwap();
            WornCapacityRestriction.EndSwap();
            Assert.AreEqual(6, WornCapacityRestriction.EffectiveUsedSlots(6, false), "cerrar de más no deja el modo intercambio puesto");
        }

        [Test]
        public void LaPiezaSeisTraeEncimaManosYCara()
        {
            const string tags = "Assets/Resources/Definitions/ItemTag/";
            var pieces = new[]
            {
                ("Outer", "BR_Work Jacket", tags + "BR_Outer Equipment.asset"),
                ("Gloves", "BR_Work Gloves", tags + "BR_Hands Equipment.asset"),
                ("Face", "BR_Dust Mask", tags + "BR_Face Equipment.asset"),
            };
            string scene = File.ReadAllText(TestScenePath);
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/BR_UI_Player.prefab");
            foreach (var (container, item, tagPath) in pieces)
            {
                var tag = AssetDatabase.LoadAssetAtPath<ItemTagDefinition>(tagPath);
                Assert.IsNotNull(tag, $"falta {tagPath}: Backrooms/Inventory/Create Backpack Prototype");
                var def = AssetDatabase.LoadAssetAtPath<ItemDefinition>($"Assets/Resources/Definitions/Item/{item}.asset");
                Assert.IsNotNull(def, $"falta {item}");
                Assert.AreEqual(tag.Id, (int)def.Tag, $"{item} no lleva el tag de {container}");
                Assert.AreEqual(1, def.StackSize, "una prenda nunca se apila");
                StringAssert.Contains($"value: {container}", scene, $"la escena no añade el contenedor {container}");

                bool slot = false;
                foreach (var ui in variant.GetComponentsInChildren<ItemContainerUI>(true))
                    slot |= ui.ContainerName == container && DirectSlots(ui.transform) == 1;
                Assert.IsTrue(slot, $"sin hueco {container} en la columna del personaje");
            }
        }

        [Test]
        public void LaCargaFrenaConCurvaHastaLaMitadEnElMaximo()
        {
            Assert.AreEqual(1f, BackroomsCarrySpeed.Multiplier(0f, 20f, 0.5f, 2f), "sin carga, velocidad entera");
            Assert.Less(BackroomsCarrySpeed.Multiplier(1f, 20f, 0.5f, 2f), 1f, "frena desde el primer kg");
            Assert.Greater(BackroomsCarrySpeed.Multiplier(4f, 20f, 0.5f, 2f), 0.97f, "al principio apenas se nota");
            Assert.AreEqual(0.875f, BackroomsCarrySpeed.Multiplier(10f, 20f, 0.5f, 2f), 1e-4f, "a la mitad");
            Assert.AreEqual(0.5f, BackroomsCarrySpeed.Multiplier(20f, 20f, 0.5f, 2f), 1e-4f, "en el máximo, la mitad");
            Assert.AreEqual(0.5f, BackroomsCarrySpeed.Multiplier(30f, 20f, 0.5f, 2f), 1e-4f, "por encima del máximo no frena más");
            float firstHalf = 1f - BackroomsCarrySpeed.Multiplier(10f, 20f, 0.5f, 2f);
            float secondHalf = BackroomsCarrySpeed.Multiplier(10f, 20f, 0.5f, 2f) - BackroomsCarrySpeed.Multiplier(20f, 20f, 0.5f, 2f);
            Assert.Greater(secondHalf, 2f * firstHalf, "pasada la mitad aprieta mucho más");

            var guids = AssetDatabase.FindAssets("BackroomsCarrySpeed t:MonoScript");
            Assert.IsNotEmpty(guids, "falta el script BackroomsCarrySpeed");
            StringAssert.Contains(guids[0], File.ReadAllText(TestScenePath), "la escena de pruebas no monta BackroomsCarrySpeed");
            StringAssert.DoesNotContain(guids[0], File.ReadAllText(ShowcasePath), "STP_Showcase lleva el frenado por carga");
        }

        [Test]
        public void LasPrendasSumanVelocidadConTope()
        {
            Assert.AreEqual(1f, BackroomsWornStats.SpeedMultiplier(new WearableStatData[0], -30f, 20f), "desnudo, nada");
            Assert.AreEqual(1.05f, BackroomsWornStats.SpeedMultiplier(new[] { new WearableStatData(8f), new WearableStatData(-3f) }, -30f, 20f),
                1e-4f, "se suman");
            Assert.AreEqual(1.2f, BackroomsWornStats.SpeedMultiplier(new[] { new WearableStatData(15f), new WearableStatData(15f) }, -30f, 20f),
                1e-4f, "apilar prendas no pasa del tope");
            Assert.AreEqual(0.7f, BackroomsWornStats.SpeedMultiplier(new[] { new WearableStatData(-45f) }, -30f, 20f), 1e-4f, "ni del suelo");

            var feet = AssetDatabase.LoadAssetAtPath<ItemTagDefinition>("Assets/PolymindGames/STP/Data/Resources/Definitions/ItemTag/STP_Feet Equipment.asset");
            var shoes = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/Resources/Definitions/Item/BR_Running Shoes.asset");
            Assert.IsNotNull(shoes, "faltan las zapatillas de correr");
            Assert.AreEqual(feet.Id, (int)shoes.Tag, "las zapatillas van en el hueco de pies del vendor");
            Assert.IsTrue(shoes.TryGetDataOfType(out WearableStatData stats));
            Assert.Greater(stats.SpeedPct, 0f, "las zapatillas dan velocidad");

            var guids = AssetDatabase.FindAssets("BackroomsWornStats t:MonoScript");
            Assert.IsNotEmpty(guids, "falta el script BackroomsWornStats");
            StringAssert.Contains(guids[0], File.ReadAllText(TestScenePath), "la escena de pruebas no monta BackroomsWornStats");
            StringAssert.DoesNotContain(guids[0], File.ReadAllText(ShowcasePath), "STP_Showcase lleva los modificadores por prenda");
        }

        [Test]
        public void LaFichaDeCadaPrendaEnsenaSusCifras()
        {
            Assert.AreEqual("A work belt.\n+4 belt slots · +4 kg max load",
                WearableDescription.Describe("A work belt.", "+4 belt slots", 4f, 0f));
            Assert.AreEqual("Heavy work boots.\nSpeed -3%",
                WearableDescription.Describe("Heavy work boots.", null, 0f, -3f));
            Assert.AreEqual("A paper dust mask.",
                WearableDescription.Describe("A paper dust mask.", null, 0f, 0f), "sin cifras, sin línea");

            var shoes = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/Resources/Definitions/Item/BR_Running Shoes.asset");
            StringAssert.Contains("Speed +8%", shoes.Description, "la ficha de las zapatillas no dice su bonus");
            var office = AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/Resources/Definitions/Item/BR_Office Backpack.asset");
            StringAssert.Contains("18 slots · +15 kg max load", office.Description);
        }
    }
}
