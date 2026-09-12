using System.IO;
using System.Linq;
using System.Text;
using BackroomsSurvival.Net;
using NUnit.Framework;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-064 enm. 1 — los tres artefactos de Unity comprobados como los usa el juego: el gestor
    /// que <c>Character</c> va a mapear, la receta que <c>CraftingUI</c> va a listar y el oráculo
    /// que <c>cargo test</c> va a leer. Piden el editor (AssetDatabase). Las rutas y los números
    /// son el CONTRATO con <c>BackroomsCraftAssetsCreator</c> (que esta suite no puede
    /// referenciar): por eso están escritos aquí. Si el oráculo falla, la cura es el menú
    /// <c>Backrooms/Create Craft Assets</c>, que lo regenera; y después <c>cargo test crafting</c>.
    /// </summary>
    public class CraftAssetsTests
    {
        private const string PlayerPrefabPath = "Assets/PolymindGames/STP/Prefabs/Core/STP_Player.prefab";
        private const string BandageDefinitionPath = "Assets/Resources/Definitions/Item/BR_Bandage.asset";
        private const int BandageId = -1114026992;
        private const int ClothId = 8505358;

        private static string ProjectRoot() => Path.GetDirectoryName(Application.dataPath);

        [Test]
        public void ElJugadorLlevaUnSoloGestorDeCrafteoYEsElNuestro()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            Assert.IsNotNull(prefab, $"falta '{PlayerPrefabPath}'");

            var managers = prefab.GetComponentsInChildren<ICraftingManagerCC>(true);
            Assert.AreEqual(1, managers.Length,
                "Character mapea por interfaz: dos ICraftingManagerCC lanzan en release, cero deja el menú de crafteo muerto");
            Assert.IsInstanceOf<NetworkedCraftingManager>(managers[0],
                "el gestor del vendor no informa al backend; reejecuta Backrooms/Create Craft Assets");
            Assert.IsNull(prefab.GetComponentInChildren<CraftingManager>(true),
                "el CraftingManager del vendor ha vuelto (¿reimport?): vendor-patches.md fila 8");
        }

        [Test]
        public void ElSustitutoConservaElSonidoDeCrafteoDelVendor()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            var ours = prefab.GetComponentInChildren<NetworkedCraftingManager>(true);
            Assert.IsNotNull(ours);
            var clip = new SerializedObject(ours).FindProperty("_craftAudio").FindPropertyRelative("Clip");
            Assert.IsNotNull(clip.objectReferenceValue, "el _craftAudio del vendor no se copió al sustituto");
        }

        [Test]
        public void LaVendaSeCrafteaConDosTelasANivelCero()
        {
            var bandage = AssetDatabase.LoadAssetAtPath<ItemDefinition>(BandageDefinitionPath);
            Assert.IsNotNull(bandage, $"falta '{BandageDefinitionPath}'");
            Assert.AreEqual(BandageId, bandage.Id, "el id de la venda es el del wire y de crafting_spec; no se regenera");

            Assert.IsTrue(bandage.TryGetDataOfType<CraftingData>(out var data),
                "la venda no tiene CraftingData: reejecuta Backrooms/Create Craft Assets");
            Assert.IsTrue(data.IsCraftable);
            Assert.AreEqual(0, data.CraftLevel, "nivel 0 = a mano, sin estación (E1.4)");
            Assert.AreEqual(1, data.CraftAmount);
            Assert.IsFalse(data.AllowDismantle, "la venda no se desmonta en tela (E1.7)");
            Assert.AreEqual(1, data.Blueprint.Length);
            Assert.AreEqual(ClothId, data.Blueprint[0].Item.Id, "la tela es STP_Cloth");
            Assert.AreEqual(2, data.Blueprint[0].Amount, "paridad con Rope: 2 Cloth");
            Assert.IsNotNull(ItemDefinition.GetWithId(ClothId), "STP_Cloth no resuelve por id");
        }

        [Test]
        public void ElOraculoJsonCommiteadoEsElQueGeneranLosAssets()
        {
            string path = Path.Combine(ProjectRoot(), CraftingRecipesOracle.RelativePath);
            Assert.IsTrue(File.Exists(path), $"falta '{CraftingRecipesOracle.RelativePath}'");
            string onDisk = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
            string generated = CraftingRecipesOracle.BuildJson();
            Assert.AreEqual(generated, onDisk,
                "docs/data/crafting-recipes.json no refleja los CraftingData reales: reejecuta " +
                "Backrooms/Create Craft Assets y luego `cargo test crafting` (la tabla Rust lo lee)");
        }

        [Test]
        public void ElOraculoContieneLaVendaYSoloRecetasCraftables()
        {
            var rows = CraftingRecipesOracle.CollectRows();
            Assert.IsTrue(rows.Any(r => r.ItemId == BandageId), "la venda no está en el oráculo");
            Assert.IsTrue(rows.All(r => r.Amount > 0 && r.Blueprint.Length > 0),
                "sólo craftables: las prendas y el garrote del vendor (amount 0) son sólo desmontables");
            for (int i = 1; i < rows.Count; i++)
                Assert.Less(rows[i - 1].ItemId, rows[i].ItemId, "ordenado por item_id como la tabla Rust");
            int craftables = ItemDefinition.Definitions.Count(d =>
                d != null && d.TryGetDataOfType<CraftingData>(out var c) && c.IsCraftable);
            Assert.AreEqual(craftables, rows.Count);
        }
    }
}
