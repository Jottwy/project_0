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
    }
}
