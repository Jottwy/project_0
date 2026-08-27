using System.Reflection;
using BackroomsSurvival.Gameplay.Building;
using NUnit.Framework;
using PolymindGames;
using PolymindGames.BuildingSystem;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// FARMING-ROADMAP.md E4: automated version of the manual
    /// "Backrooms/Diagnostics/Verify Storage Rack Display" probe
    /// (BackroomsStorageRackProbe.VerifyDisplay) — same reflection recipe (Workstation.Start(),
    /// force BuildingPiece to Constructed, StorageRackDisplay.Update() once) so this runs headless
    /// under the Test Runner instead of needing a human to fire the menu item and read a report.
    ///
    /// Instantiates directly into the active scene rather than an additive empty scene like the
    /// probe does — the probe needs isolation for its screenshot camera, this doesn't, and
    /// EditorSceneManager.NewScene(..., Additive) throws "Cannot create a new scene additively
    /// with an untitled scene unsaved" whenever the editor's currently open scene happens to be
    /// unsaved, which the Test Runner has no control over. TearDown destroys the instance instead.
    /// </summary>
    [TestFixture]
    public class StorageRackDisplayTests
    {
        private const string PrefabPath = "Assets/Prefabs/Building/BR_BuildingPiece_StorageRack.prefab";
        private const string AlmondWaterPath = "Assets/Resources/Definitions/Item/BR_Almond Water.asset";

        private GameObject _instance;
        private StorageRackDisplay _display;
        private IItemContainer _container;
        private ItemDefinition _almondWater;

        [SetUp]
        public void Setup()
        {
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.NotNull(prefabAsset, $"missing '{PrefabPath}'");

            _almondWater = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AlmondWaterPath);
            Assert.NotNull(_almondWater, $"missing '{AlmondWaterPath}'");

            _instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset);

            var station = _instance.GetComponent<StorageStation>();
            _display = _instance.GetComponent<StorageRackDisplay>();
            Assert.NotNull(station, "prefab has no StorageStation");
            Assert.NotNull(_display, "prefab has no StorageRackDisplay");

            typeof(Workstation).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(station, null);

            // A freshly-instantiated prefab is not BuildingPieceState.Constructed by default —
            // StorageRackDisplay.Update() gates on IsConstructed (FARMING-ROADMAP.md E3b), so a
            // real completed build has to be simulated the same way the probe does.
            var buildingPiece = _instance.GetComponent<BuildingPiece>();
            var stateEnumType = typeof(BuildingPiece).GetNestedType("BuildingPieceState", BindingFlags.NonPublic);
            var constructedValue = System.Enum.Parse(stateEnumType, "Constructed");
            typeof(BuildingPiece).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(buildingPiece, constructedValue);

            typeof(StorageRackDisplay).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(_display, null);

            _container = station.GetContainers()[0];
        }

        [TearDown]
        public void Teardown()
        {
            if (_instance != null)
                Object.DestroyImmediate(_instance);
        }

        // "Model" (mesh), the shelf anchors, and Joel's WoodenTable dressing are permanent
        // children — only count what RefreshSlot itself instantiates (item pickup clones).
        private int VisualChildCount()
        {
            int count = 0;
            foreach (Transform child in _instance.transform)
                if (!IsRackDressing(child.name))
                    count++;
            return count;
        }

        private static bool IsRackDressing(string name) =>
            name.Contains("Model") || name.StartsWith("ShelfAnchor_") || name.StartsWith("WoodenTable");

        [Test]
        public void N_items_produce_exactly_N_visuals()
        {
            Assert.AreEqual(0, VisualChildCount(), "rack should start empty");

            for (int i = 0; i < 3; i++)
                _container.AddItemsById(_almondWater.Id, 1);

            Assert.AreEqual(3, VisualChildCount());
        }

        [Test]
        public void Emptying_every_slot_leaves_zero_tracked_visuals()
        {
            for (int i = 0; i < 3; i++)
                _container.AddItemsById(_almondWater.Id, 1);
            Assert.AreEqual(3, VisualChildCount());

            _container.RemoveItemsById(_almondWater.Id, 3);

            // Destroy() is deferred to end-of-frame and no frame boundary passes inside this
            // synchronous test, so the GameObjects are still physically in the hierarchy here even
            // though RefreshSlot already nulled its own bookkeeping — the private _visuals array is
            // what actually reflects "does the component think this slot is now empty", which is
            // the real thing under test (see BackroomsStorageRackProbe.VerifyDisplay for the same
            // reasoning against the manual probe).
            var visualsField = typeof(StorageRackDisplay).GetField("_visuals",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var visuals = (GameObject[])visualsField.GetValue(_display);

            int stillTracked = 0;
            foreach (var v in visuals)
                if (v != null) stillTracked++;

            Assert.AreEqual(0, stillTracked);
        }
    }
}
