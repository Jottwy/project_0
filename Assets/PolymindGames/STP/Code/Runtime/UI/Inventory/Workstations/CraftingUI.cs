using PolymindGames.InventorySystem;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault2)]
    public sealed class CraftingUI : CharacterUIBehaviour
    {
        [SerializeField, Title("Category")]
        private SelectableGroupBase _categoriesGroup;

        [SerializeField, PrefabObjectOnly]
        private ItemCategoryDefinitionUI _categoryTemplate;

        [SerializeField, SceneObjectOnly]
        private TextMeshProUGUI _categoryNameText;

        [SerializeField, SceneObjectOnly]
        private SelectableButton _allCategory;

        [SerializeField, SceneObjectOnly]
        private SelectableButton _favoriteCategory;

        [SerializeField, NotNull, Title("Blueprint")]
        private Transform _blueprintsRoot;

        [SerializeField, PrefabObjectOnly]
        private ItemBlueprintUI _blueprintTemplate;

        [SerializeField, Range(5, 24)]
        private int _templateBuffer = 12;

        /// <summary>
        /// <para> Key: Crafting level. </para>
        /// Value: List of blueprints that correspond to the crafting level.
        /// </summary>
        private readonly Dictionary<int, BlueprintCollectionEntry> _levelsBlueprintMap = new(2);
        private readonly List<ItemDefinition> _blueprintsCache = new(16);

        private IInventoryInspectionManagerCC _inventoryInspection;
        private ICraftingManagerCC _craftingManager;
        private ItemBlueprintUI[] _cacheddisplays;
        private int _craftingItemsCount;
        private int _craftingLevel = -1;

        private const string FavoriteCategory = "Favorites";
        private const string AllCategory = "All";

        /// <summary>
        /// Updates the current crafting level and refreshes the craftable items based on the selected category.
        /// If the new level is invalid, an error is logged and no changes are made.
        /// If the selected category is not "All", it switches to the "All" category and updates the displays accordingly.
        /// </summary>
        /// <param name="level">The new crafting level to be set.</param>
        public void UpdateCraftingLevel(int level)
        {
            if (level == _craftingLevel)
                return;

            if (!_levelsBlueprintMap.ContainsKey(level))
            {
                Debug.LogError($"Crafting level {level} is not available; no craftable item has this level.");
                return;
            }

            _craftingLevel = level;

            // If the currently selected category is not "All", switch to "All" and update the displays.
            if (_categoriesGroup.Selected != _allCategory)
            {
                _categoriesGroup.SelectSelectable(_allCategory);
            }
            else
            {
                // If "All" is already selected, just update the crafting displays for that category.
                UpdateCraftingdisplays(GetBlueprintsForSelectedCategory(_allCategory));
            }
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            InitializeCategories();
            InitializeCraftableItems();
            InitializeCraftingDisplays();

            _craftingManager = character.GetCC<ICraftingManagerCC>();
            _inventoryInspection = character.GetCC<IInventoryInspectionManagerCC>();
            _inventoryInspection.InspectionStarted += OnInspectionStarted;
            _inventoryInspection.InspectionEnded += OnInspectionEnded;
            
            if (_inventoryInspection.IsInspecting)
                OnInspectionStarted();
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            _craftingManager = null;
            _inventoryInspection.InspectionStarted -= OnInspectionStarted;
            _inventoryInspection.InspectionEnded -= OnInspectionEnded;
            _inventoryInspection = null;
        }

        private void OnInspectionStarted()
        {
            if (_inventoryInspection.Workstation == null)
                UpdateCraftingLevel(0);
            
            Character.Inventory.Changed += RefreshCraftingdisplays;
            RefreshCraftingdisplays();
        }

        private void OnInspectionEnded() => Character.Inventory.Changed -= RefreshCraftingdisplays;

        private void OnCategorydisplaySelected(SelectableButton selectedCategory)
        {
            UpdateCraftingdisplays(GetBlueprintsForSelectedCategory(selectedCategory));
        }

        private void RefreshCraftingdisplays()
        {
            foreach (var display in _cacheddisplays)
                display.Refresh();
        }

        /// <summary>
        /// Updates the crafting displays with the provided list of craftable items. 
        /// Activates and populates the displays with the appropriate item definitions.
        /// Deactivates any remaining displays that are not needed.
        /// </summary>
        /// <param name="items">The list of item definitions to display in the crafting displays.</param>
        private void UpdateCraftingdisplays(List<ItemDefinition> items)
        {
            // Determine how many displays should be enabled based on the number of items and available displays.
            int enableddisplayCount = Mathf.Min(items.Count, _cacheddisplays.Length);

            // Populate and activate the necessary displays with the corresponding item definitions.
            for (int i = 0; i < enableddisplayCount; i++)
            {
                var cachedDisplay = _cacheddisplays[i];
                cachedDisplay.SetData(items[i]);
                cachedDisplay.gameObject.SetActive(true); 
                cachedDisplay.SetFavoriteStatus(IsFavorite(items[i]));
            }

            // Deactivate any remaining displays that are not used.
            for (int i = enableddisplayCount; i < _cacheddisplays.Length; i++)
            {
                var cacheddisplay = _cacheddisplays[i];
                cacheddisplay.ClearData();          // Clear the blueprint from the display.
                cacheddisplay.gameObject.SetActive(false); // Deactivate the display in the UI.
            }

            // Determines if a given item is marked as a favorite.
            bool IsFavorite(ItemDefinition itemDef)
            {
                // If the crafting manager is not available, return false.
                if (_craftingManager == null)
                    return false;

                // Check if the item is in the list of favorite blueprints.
                return _craftingManager.FavoriteBlueprints.IndexOf(itemDef) != -1;
            }
        }

        /// <summary>
        /// Retrieves the list of item blueprints associated with the selected category.
        /// If no category is selected, retrieves the blueprints for the default (All) category.
        /// </summary>
        /// <param name="selectedCategory">The selected category display (optional).</param>
        /// <returns>A list of item definitions representing the blueprints for the selected category.</returns>
        private List<ItemDefinition> GetBlueprintsForSelectedCategory(SelectableButton selectedCategory = null)
        {
            if (selectedCategory == null || selectedCategory == _allCategory)
            {
                _categoryNameText.text = AllCategory;
                return _levelsBlueprintMap[_craftingLevel].BlueprintsList;
            }

            if (selectedCategory == _favoriteCategory)
            {
                _categoryNameText.text = FavoriteCategory;
                return GetFavoriteBlueprints();
            }

            var categoryDefinition = selectedCategory.GetComponent<ItemCategoryDefinitionUI>().Data;
            _categoryNameText.text = categoryDefinition.Name;
            return GetBlueprintsForCategory(categoryDefinition);
        }

        /// <summary>
        /// Retrieves the list of favorite blueprints that are allowed to be crafted at the current crafting level.
        /// </summary>
        /// <returns>A list of item definitions representing the favorite blueprints.</returns>
        private List<ItemDefinition> GetFavoriteBlueprints()
        {
            _blueprintsCache.Clear();

            var favoriteBlueprints = _craftingManager.FavoriteBlueprints;
            var allowedBlueprints = _levelsBlueprintMap[_craftingLevel].BlueprintsSet;

            foreach (var favoriteItem in favoriteBlueprints)
            {
                var blueprint = favoriteItem.Def;

                if (allowedBlueprints.Contains(blueprint))
                {
                    _blueprintsCache.Add(blueprint);
                }
            }

            return _blueprintsCache;
        }

        /// <summary>
        /// Retrieves the list of item blueprints associated with a specific category and allowed at the current crafting level.
        /// </summary>
        /// <param name="category">The item category definition.</param>
        /// <returns>A list of item definitions representing the blueprints for the specified category.</returns>
        private List<ItemDefinition> GetBlueprintsForCategory(ItemCategoryDefinition category)
        {
            _blueprintsCache.Clear();

            var categoryBlueprints = category.Members;
            var allowedBlueprints = _levelsBlueprintMap[_craftingLevel].BlueprintsSet;

            foreach (var blueprint in categoryBlueprints)
            {
                if (allowedBlueprints.Contains(blueprint))
                {
                    _blueprintsCache.Add(blueprint);
                }
            }

            return _blueprintsCache;
        }

        /// <summary>
        /// Initializes the craftable items by categorizing them based on their crafting level.
        /// Populates the levels blueprint map and counts the total number of craftable items.
        /// </summary>
        private void InitializeCraftableItems()
        {
            _levelsBlueprintMap.Clear();
            _craftingItemsCount = 0;

            foreach (var item in ItemDefinition.Definitions)
            {
                if (item.TryGetDataOfType<CraftingData>(out var data) && data.IsCraftable)
                {
                    if (!_levelsBlueprintMap.TryGetValue(data.CraftLevel, out var entry))
                    {
                        entry = new BlueprintCollectionEntry(new List<ItemDefinition>(), new HashSet<ItemDefinition>());
                        _levelsBlueprintMap[data.CraftLevel] = entry;
                    }

                    entry.BlueprintsList.Add(item);
                    entry.BlueprintsSet.Add(item);
                    _craftingItemsCount++;
                }
            }
        }

        /// <summary>
        /// Initializes the UI for the crafting categories. Instantiates category displays and assigns definitions to them.
        /// Sets up the event listener for category selection changes.
        /// </summary>
        private void InitializeCategories()
        {
            var spawnRoot = _categoriesGroup.transform;
            var categoryDefinitions = ItemCategoryDefinition.Definitions;

            foreach (var def in categoryDefinitions)
            {
                var categoryDisplay = Instantiate(_categoryTemplate, spawnRoot);
                categoryDisplay.SetData(def);
            }

            _categoriesGroup.SelectedChanged += OnCategorydisplaySelected;
            if (_categoriesGroup.Selected != null)
            {
                OnCategorydisplaySelected(_categoriesGroup.Selected);
            }
        }

        /// <summary>
        /// Initializes the crafting displays UI by instantiating a set number of displays and setting up their initial state.
        /// Sets up event listeners for crafting and toggling favorite status for each display.
        /// </summary>
        private void InitializeCraftingDisplays()
        {
            int displayCount = Mathf.Min(_templateBuffer, _craftingItemsCount);
            _cacheddisplays = new ItemBlueprintUI[displayCount];

            for (int i = 0; i < displayCount; i++)
            {
                var display = Instantiate(_blueprintTemplate, _blueprintsRoot);
                display.ClearData();
                display.Selectable.Clicked += _ => StartCrafting(display);
                display.FavoriteButton.Clicked += _ => ToggleFavoriteStatus(display);
                _cacheddisplays[i] = display;
            }

            // Starts the crafting process for the selected blueprint display.
            void StartCrafting(ItemBlueprintUI display)
            {
                _craftingManager.Craft(display.Data);
            }

            // Toggles the favorite status of the selected blueprint display.
            // Adds or removes the blueprint from the favorites list and updates the UI accordingly.
            void ToggleFavoriteStatus(ItemBlueprintUI display)
            {
                var blueprint = new DataIdReference<ItemDefinition>(display.Data);

                if (_craftingManager.FavoriteBlueprints.IndexOf(blueprint) != -1)
                {
                    display.SetFavoriteStatus(false);
                    _craftingManager.RemoveFavoriteBlueprint(blueprint);

                    if (_categoriesGroup.Selected == _favoriteCategory)
                    {
                        UpdateCraftingdisplays(GetBlueprintsForSelectedCategory(_favoriteCategory));
                    }
                }
                else
                {
                    display.SetFavoriteStatus(true);
                    _craftingManager.AddFavoriteBlueprint(blueprint);
                }
            }
        }
        
        #region Internal Types
        private sealed class BlueprintCollectionEntry
        {
            public readonly HashSet<ItemDefinition> BlueprintsSet;
            public readonly List<ItemDefinition> BlueprintsList;

            public BlueprintCollectionEntry(List<ItemDefinition> blueprintsList, HashSet<ItemDefinition> blueprintsSet)
            {
                BlueprintsList = blueprintsList;
                BlueprintsSet = blueprintsSet;
            }
        }
        #endregion
    }
}