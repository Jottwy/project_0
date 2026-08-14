#if UNITY_EDITOR
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.SaveSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Authors "Almond Water" — the canon Backrooms find-object: a sealed bottle that never
    /// expires, always drinkable, and (per lore) settles the mind a little on top of hydrating.
    /// Run via "Backrooms/Create Almond Water".
    ///
    /// TWO ASSETS, cloned from the two donors that already do 90% of the job:
    /// - <see cref="ItemDefinition"/> at <see cref="DefinitionPath"/>, donor
    ///   <see cref="DonorDefinitionPath"/> (STP_Water Bottle — same consumable shape: single
    ///   stack pickup, one Drink action, ConsumeData).
    /// - The world pickup prefab at <see cref="PrefabPath"/>, cloned from
    ///   <see cref="DonorPickupPath"/> with its mesh/material swapped for the Meshy import —
    ///   same technique as <see cref="BackroomsCarryableCreator"/>.
    ///
    /// crear-si-falta, same reasoning as every other creator in this folder: <c>DataDefinition</c>
    /// mints <c>_id</c> from <c>Random.Range(int.MinValue, int.MaxValue)</c>, and that id is the
    /// <c>def_id</c> that travels the wire and lands in `stp_inventory`/save files. Regenerating it
    /// orphans anything that already referenced it. COMMIT THE GENERATED ASSETS.
    ///
    /// SANITY RESTORE LIVES ENTIRELY SERVER-SIDE. STP's <see cref="ConsumeData"/> has no sanity
    /// field (vendor never modeled the stat) and the authoritative amount always comes from the
    /// backend's `consumable_spec` table (ADR-030) regardless of what's authored here — this
    /// asset's ConsumeData ranges are the OFFLINE/disconnected fallback only (same caveat ADR-030
    /// documents for every other consumable). The backend wiring (new item id in
    /// `consumable_spec`, `restore_sanity`) is a separate change; this script only authors the
    /// Unity-side asset pair. See the ADR-030 amendment for the fixed numbers.
    ///
    /// ICON: donated by reference from the water bottle at creation time (declared debt), then
    /// swapped for the real one by AssignIcon/AssignIconMenu once art lands at IconPath — same
    /// two-step pattern as the spray can. The model/material were never borrowed: they're the
    /// real Meshy import from the start.
    /// </summary>
    public static class BackroomsAlmondWaterCreator
    {
        private const string DefinitionFolder = "Assets/Resources/Definitions/Item";
        private const string DefinitionPath = DefinitionFolder + "/BR_Almond Water.asset";

        private const string PrefabFolder = "Assets/Prefabs/Items";
        private const string PrefabPath = PrefabFolder + "/BR_Pickup_AlmondWater.prefab";
        private const string RootName = "BR_Pickup_AlmondWater";

        // Same consumable shape (single stack pickup, ConsumeData, one Drink action) as the item
        // being authored — the reason it is the donor and not, say, the spray can.
        private const string DonorDefinitionPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Water Bottle.asset";
        private const string DonorPickupPath =
            "Assets/PolymindGames/STP/Prefabs/Items/STP_Pickup_WaterBottle.prefab";

        // ADR-030: the SAME shared action asset the water bottle itself already uses (STP_Water
        // Bottle's own `_actions[0]` resolves to this exact asset — the vendor's original
        // ConsumeAction reference was swapped for it that session). Reusing it by reference, not
        // cloning: it is item-agnostic (reads whatever ConsumeData is on the stack's own
        // definition) and NetworkedConsumeAction.cs is sealed with no per-item state.
        private const string DrinkActionPath =
            "Assets/_Migration/STPIntegration/Resources/ItemAction_NetworkedConsume_Drink.asset";

        private const string MeshFolder =
            "Assets/MeshyImports/almond-water-unity-optimized_20260814_215655";
        private const string MeshPath = MeshFolder + "/Meshy_AI_almond_water_unity_op_0814195629_texture.fbx";
        private const string MaterialPath = MeshFolder + "/Material.001.mat";

        private const string SaveableDatabasePath =
            "Assets/PolymindGames/FPSCore/Data/Resources/Managers/SaveableDatabase.asset";

        // Real icon, no longer borrowed from the water bottle: Joel's reference photo, background
        // removed + cropped to content + rotated 25° (same treatment as BR_SprayCan_Icon.png).
        // Assigned by AssignIcon/AssignIconMenu below, separate from CreateIfMissing on purpose --
        // same two-step pattern as the spray can (art can land after the item already exists).
        private const string IconPath = "Assets/Art/Items/BR_AlmondWater_Icon.png";

        private const string ItemName = "Almond Water";

        private const string Description =
            "Tastes like nothing. Faintly of almonds, if you think about it too hard.";

        private const string LongDescription =
            "Bottled somewhere, by someone, for reasons the label doesn't explain. It shouldn't " +
            "still be drinkable after all this time, and yet it always is -- cool, clear, and " +
            "exactly as thirst-quenching as the day it was sealed.\n\n" +
            "Finding one feels less like luck and more like being allowed to.";

        private const float Weight = 0.5f; // matches the water bottle's own weight
        private const int StackSize = 5;

        // Local/offline-only fallback (see class doc) -- the backend's fixed table is what
        // actually applies while connected. Midpoints match the fixed values in the ADR-030
        // amendment: health 8, hunger 20, thirst 60.
        private static readonly Vector2 HealthRange = new(6f, 10f);
        private static readonly Vector2 HungerRange = new(15f, 25f);
        private static readonly Vector2 ThirstRange = new(50f, 70f);

        // THE SIZE LIVES ON THE FBX IMPORTER, AND THAT IS NOT A STYLE CHOICE -- it is a bug fix.
        // The raw import measures (0.0063, 0.0063, 0.02) m, bottle-cap sized. The first version of
        // this script scaled the PREFAB ROOT by 11.5 instead, which renders identically but breaks
        // anything defined in OBJECT space: the vendor's hover highlight (MaterialEffect, the
        // "mark" that appears when you look at a pickup) came out 11.5× too thick, and only on
        // this item. Every other pickup prefab in the project sits at root scale 1 for exactly
        // this reason, with the factor on the importer -- same as the flight case (globalScale
        // 100, BackroomsCarryableCreator).
        //
        // Read as (diameter, diameter, height) the raw ratio (~1:1:3.2) already looks like a
        // slender bottle; 11.5× brings the height axis to ~0.23 m, a real bottled-water height,
        // with ~0.072 m diameter.
        private const float ImporterScale = 11.5f;

        // Guard against a reverted/mis-set importer scale producing an invisible speck or a
        // room-sized bottle -- same defensive pattern as BackroomsCarryableCreator. Checked on
        // mesh.bounds directly, which is what the importer scale bakes into (verified against the
        // flight case, whose own guard reads the same way).
        private const float MinPlausibleSize = 0.05f;
        private const float MaxPlausibleSize = 0.6f;

        [MenuItem("Backrooms/Create Almond Water")]
        public static void CreateIfMissing()
        {
            var existingDefinition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            var existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existingDefinition != null && existingPrefab != null)
            {
                Debug.Log($"[AlmondWaterCreator] '{DefinitionPath}' and '{PrefabPath}' already exist " +
                          $"(id={existingDefinition.Id}) -- left untouched. The id is/will-be on the wire " +
                          "and in saves once the backend spec is wired; delete both by hand and re-run for " +
                          "a new id, knowing it orphans anything that already referenced it.");
                // Repairs on an existing pair -- never a regeneration (the definition's _id is in
                // the backend's consumable_spec and on the wire).
                EnsureImporterScale();
                RepairPickupScale(existingPrefab,
                    ResolveMesh(AssetDatabase.LoadAssetAtPath<GameObject>(MeshPath)));

                if (AssignIcon(existingDefinition))
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }
                Selection.activeObject = existingDefinition;
                return;
            }

            if (existingDefinition != null || existingPrefab != null)
            {
                Debug.LogError("[AlmondWaterCreator] Half the pair exists " +
                               $"(definition={existingDefinition != null}, prefab={existingPrefab != null}). " +
                               "Refusing to regenerate: recreating the definition mints a new def_id, " +
                               "recreating the prefab orphans the existing one. Delete the survivor by hand " +
                               "and re-run, or restore the missing file from source control.");
                return;
            }

            var donorDefinition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DonorDefinitionPath);
            var donorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DonorPickupPath);
            var drinkAction = AssetDatabase.LoadAssetAtPath<ItemAction>(DrinkActionPath);
            var meshAsset = AssetDatabase.LoadAssetAtPath<GameObject>(MeshPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (donorDefinition == null || donorPrefab == null || drinkAction == null ||
                meshAsset == null || material == null)
            {
                Debug.LogError("[AlmondWaterCreator] Missing source asset " +
                               $"(donor definition={donorDefinition != null}, donor prefab={donorPrefab != null}, " +
                               $"drink action={drinkAction != null} at '{DrinkActionPath}', " +
                               $"mesh={meshAsset != null} at '{MeshPath}', material={material != null}). " +
                               "Nothing created.");
                return;
            }
            if (donorDefinition.Pickup == null)
            {
                Debug.LogError($"[AlmondWaterCreator] Donor '{donorDefinition.Name}' has no _pickup. " +
                               "Nothing created: the stack pickup would resolve to null.");
                return;
            }

            var mesh = ResolveMesh(meshAsset);
            if (mesh == null)
            {
                Debug.LogError($"[AlmondWaterCreator] No MeshFilter with a mesh under '{MeshPath}'. " +
                               "Nothing created.");
                return;
            }

            // Importer scale FIRST -- it changes mesh.bounds, which both the guard below and the
            // BoxCollider are derived from.
            if (EnsureImporterScale())
                mesh = ResolveMesh(AssetDatabase.LoadAssetAtPath<GameObject>(MeshPath));
            if (mesh == null)
            {
                Debug.LogError("[AlmondWaterCreator] Mesh went null after the importer reimport. Nothing created.");
                return;
            }

            // Fail loud rather than author an invisible speck or a room-sized bottle.
            var meshSize = mesh.bounds.size;
            float widest = Mathf.Max(meshSize.x, Mathf.Max(meshSize.y, meshSize.z));
            if (widest < MinPlausibleSize || widest > MaxPlausibleSize)
            {
                Debug.LogError($"[AlmondWaterCreator] '{MeshPath}' has bounds " +
                               $"({meshSize.x:F4}, {meshSize.y:F4}, {meshSize.z:F4}) -- widest side " +
                               $"{widest:F4} m, outside the plausible {MinPlausibleSize}-{MaxPlausibleSize} m " +
                               $"for a hand-sized bottle. Re-check ImporterScale ({ImporterScale}) against the " +
                               "FBX's raw bounds and re-run. Nothing created.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder("Assets/Resources/Definitions");
            BackroomsEditorFolders.EnsureFolder(DefinitionFolder);
            BackroomsEditorFolders.EnsureFolder("Assets/Prefabs");
            BackroomsEditorFolders.EnsureFolder(PrefabFolder);

            // Definition first (empty pickup slot), prefab second, then wired together -- same
            // order as BackroomsCarryableCreator, for the same reason (they reference each other).
            var definition = CreateDefinition(donorDefinition, drinkAction);
            var prefab = CreatePrefab(definition, mesh, material);
            if (prefab == null)
            {
                Debug.LogError($"[AlmondWaterCreator] Prefab creation failed; '{DefinitionPath}' is now an " +
                               "orphan half-pair. Delete it by hand before re-running.");
                return;
            }

            AssignPickupToDefinition(definition, prefab);

            ItemDefinition.ReloadDefinitions_EditorOnly();
            RefreshSaveableDatabase();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = definition;

            Debug.Log($"[AlmondWaterCreator] Created '{DefinitionPath}' (id={definition.Id}, " +
                      $"Name='{definition.Name}') and '{PrefabPath}'. COMMIT BOTH plus SaveableDatabase.asset. " +
                      $"NEXT STEP (not done by this script): wire id={definition.Id} into " +
                      "backend/src/game_loop.rs consumable_spec (hunger/thirst/health/sanity fixed amounts) " +
                      "per the ADR-030 amendment, and into the chest loot pool (StpChestSpawner) by the name " +
                      "'Almond Water'.");
        }

        /// <summary>
        /// Puts <see cref="ImporterScale"/> on the FBX importer if it isn't there already, and
        /// reimports. Returns true if it changed anything (callers must re-resolve the mesh: the
        /// old Mesh reference is stale after a reimport).
        ///
        /// Verified approach, not a guess: the flight case FBX in this same project carries its
        /// factor the same way (globalScale 100) and BackroomsCarryableCreator's guard reads the
        /// scaled value straight off mesh.bounds.
        /// </summary>
        private static bool EnsureImporterScale()
        {
            var importer = AssetImporter.GetAtPath(MeshPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[AlmondWaterCreator] No ModelImporter at '{MeshPath}' -- cannot set the " +
                                 "import scale. The bottle will be authored at whatever size the FBX carries.");
                return false;
            }

            if (Mathf.Approximately(importer.globalScale, ImporterScale))
                return false;

            importer.globalScale = ImporterScale;
            importer.SaveAndReimport();
            Debug.Log($"[AlmondWaterCreator] FBX import scale set to {ImporterScale} on '{MeshPath}'.");
            return true;
        }

        /// <summary>
        /// Repairs an ALREADY EXISTING pickup prefab that was authored by the first version of this
        /// script, which put the size on the prefab root (scale 11.5) instead of the importer. That
        /// inflated the vendor's object-space hover highlight by 11.5× -- visible in game as a huge
        /// "mark" when looking at the bottle, and only on this item.
        ///
        /// Repairs in place rather than regenerating: the prefab is referenced by the definition's
        /// `_pickup`, and the definition's `_id` is already in the backend's consumable_spec and on
        /// the wire. Only scale and collider are touched.
        /// </summary>
        private static void RepairPickupScale(GameObject prefabAsset, Mesh mesh)
        {
            if (prefabAsset == null || mesh == null) return;

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                bool scaleWrong = !Approximately(root.transform.localScale, Vector3.one);
                var box = root.GetComponent<BoxCollider>();
                bool colliderWrong = box != null &&
                                     (!Approximately(box.size, mesh.bounds.size) ||
                                      !Approximately(box.center, mesh.bounds.center));

                if (!scaleWrong && !colliderWrong)
                    return;

                root.transform.localScale = Vector3.one;
                if (box != null)
                {
                    box.center = mesh.bounds.center;
                    box.size = mesh.bounds.size;
                }

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.LogWarning($"[AlmondWaterCreator] Repaired '{PrefabPath}': root scale back to 1 and " +
                                 "collider re-derived from the mesh. The old root scale inflated the vendor's " +
                                 "hover highlight by the same factor.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static bool Approximately(Vector3 a, Vector3 b) =>
            Mathf.Approximately(a.x, b.x) && Mathf.Approximately(a.y, b.y) && Mathf.Approximately(a.z, b.z);

        private static Mesh ResolveMesh(GameObject meshAsset)
        {
            foreach (var filter in meshAsset.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null)
                    return filter.sharedMesh;
            }

            return null;
        }

        private static ItemDefinition CreateDefinition(ItemDefinition donor, ItemAction drinkAction)
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            AssetDatabase.CreateAsset(definition, DefinitionPath);

            // Vendor's own entry point to `_id` minting (private AssignID, reached only this way) --
            // hand-picking one risks a collision the vendor's retry loop exists to avoid.
            definition.Validate_EditorOnly(
                new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));

            var source = new SerializedObject(donor);
            var target = new SerializedObject(definition);

            // Icon donated by reference (declared debt, see class doc). Stack pickup donated too --
            // it is the generic "cloth sack" visual shared by ~40 other items in the catalogue, not
            // something specific to the water bottle.
            target.CopyFromSerializedProperty(source.FindProperty("_icon"));
            target.CopyFromSerializedProperty(source.FindProperty("_stackPickup"));

            target.FindProperty("_description").stringValue = Description;
            target.FindProperty("_longDescription").stringValue = LongDescription;
            target.FindProperty("_weight").floatValue = Weight;
            target.FindProperty("_stackSize").intValue = StackSize;

            // One Drink action, same asset the water bottle itself uses.
            var actions = target.FindProperty("_actions");
            actions.arraySize = 1;
            actions.GetArrayElementAtIndex(0).objectReferenceValue = drinkAction;

            // Fresh ConsumeData via managedReferenceValue -- NOT copied from the donor's `_data`,
            // which would leave both items pointing at the same RefId entry and make editing one's
            // range silently edit the other's. See class doc: these ranges are the offline-only
            // fallback, the backend's fixed table is authoritative while connected.
            var data = target.FindProperty("_data");
            data.arraySize = 1;
            var consumeData = data.GetArrayElementAtIndex(0);
            consumeData.managedReferenceValue = new ConsumeData();
            target.ApplyModifiedPropertiesWithoutUndo();

            // managedReferenceValue only takes effect after the assignment above is applied --
            // re-fetch the element before writing its nested fields.
            data = target.FindProperty("_data");
            consumeData = data.GetArrayElementAtIndex(0);
            consumeData.FindPropertyRelative("_healthChange").vector2Value = HealthRange;
            consumeData.FindPropertyRelative("_hungerChange").vector2Value = HungerRange;
            consumeData.FindPropertyRelative("_thirstChange").vector2Value = ThirstRange;
            target.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);

            // Vendor API and not a raw "_parentGroup" copy: SetParentGroup_EditorOnly also calls
            // AddMember_EditorOnly, registering the item IN the category's own member array (which
            // is what ItemGenerator enumerates). Same reasoning as every other creator here.
            if (donor.ParentGroup != null)
                definition.SetParentGroup_EditorOnly(donor.ParentGroup);

            if (definition.Name != ItemName)
            {
                Debug.LogError($"[AlmondWaterCreator] '{DefinitionPath}' resolves to Name='{definition.Name}', " +
                               $"not '{ItemName}'. The chest loot pool will look it up by that exact string and " +
                               "skip the slot with a warning. Check the BR_ prefix on the asset file name.");
            }

            return definition;
        }

        /// <summary>
        /// Clones the water bottle's pickup prefab and swaps in the real Meshy mesh/material.
        /// Everything else -- pickup audio, the Interactable, the surface impact handler, the
        /// rigidbody -- is inherited unchanged, same reasoning as CreatePrefab in
        /// <see cref="BackroomsCarryableCreator"/>.
        /// </summary>
        private static GameObject CreatePrefab(ItemDefinition definition, Mesh mesh, Material material)
        {
            if (!AssetDatabase.CopyAsset(DonorPickupPath, PrefabPath))
            {
                Debug.LogError($"[AlmondWaterCreator] Could not copy '{DonorPickupPath}' to '{PrefabPath}'.");
                return null;
            }

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                root.name = RootName;
                // Root stays at scale 1 -- see ImporterScale's doc comment. Scaling the root
                // inflates the vendor's object-space hover highlight by the same factor.
                root.transform.localScale = Vector3.one;

                var filter = root.GetComponent<MeshFilter>();
                var renderer = root.GetComponent<MeshRenderer>();
                if (filter == null || renderer == null)
                {
                    Debug.LogError("[AlmondWaterCreator] The cloned prefab has no MeshFilter/MeshRenderer on " +
                                   "its root. Refusing to author.");
                    return null;
                }

                filter.sharedMesh = mesh;
                renderer.sharedMaterial = material;

                // Collider straight off the new mesh, not off the donor's remembered bottle
                // dimensions -- same reasoning as BackroomsCarryableCreator.
                var box = root.GetComponent<BoxCollider>();
                if (box != null)
                {
                    box.center = mesh.bounds.center;
                    box.size = mesh.bounds.size;
                }

                var pickup = root.GetComponent<ItemPickup>();
                if (pickup == null)
                {
                    Debug.LogError("[AlmondWaterCreator] The cloned prefab has no ItemPickup component. " +
                                   "Refusing to author.");
                    return null;
                }

                var serialized = new SerializedObject(pickup);
                serialized.FindProperty("_item").FindPropertyRelative("_value").intValue = definition.Id;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        private static void AssignPickupToDefinition(ItemDefinition definition, GameObject prefab)
        {
            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_pickup").objectReferenceValue = prefab.GetComponent<ItemPickup>();
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
        }

        /// <summary>
        /// Re-mints the cloned prefab's saveable guid from its asset guid. Not optional -- the
        /// clone arrives carrying the water bottle prefab's _prefabGuid verbatim, and
        /// SaveableDatabase.CreatePrefabsLookup is a Dictionary.Add with no try/catch: a duplicate
        /// key throws. Same call as BackroomsCarryableCreator.RefreshSaveableDatabase.
        /// </summary>
        private static void RefreshSaveableDatabase()
        {
            var database = AssetDatabase.LoadAssetAtPath<SaveableDatabase>(SaveableDatabasePath);
            if (database == null)
            {
                Debug.LogWarning($"[AlmondWaterCreator] No SaveableDatabase at '{SaveableDatabasePath}'. The " +
                                 "cloned prefab still carries the water bottle's _prefabGuid; fix it before " +
                                 "building a player or the saveable lookup will throw on a duplicate key.");
                return;
            }

            database.SetPrefabs_Editor(SaveableDatabase.FindAllSaveableObjectPrefabs());
            EditorUtility.SetDirty(database);
        }

        /// <summary>
        /// Assigns the real icon if <see cref="IconPath"/> exists; leaves the borrowed water
        /// bottle icon otherwise. Separate menu item from "Create Almond Water" on purpose -- art
        /// can land after the item already exists, same two-step pattern as the spray can's
        /// "Backrooms/Spray/Asignar icono del bote".
        /// </summary>
        [MenuItem("Backrooms/Assign Almond Water Icon", false, 96)]
        public static void AssignIconMenu()
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            if (definition == null)
            {
                Debug.LogError($"[AlmondWaterCreator] No definition at '{DefinitionPath}'. Run " +
                               "\"Backrooms/Create Almond Water\" first.");
                return;
            }
            if (AssignIcon(definition))
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        private static bool AssignIcon(ItemDefinition definition)
        {
            if (!System.IO.File.Exists(System.IO.Path.Combine(
                    System.IO.Directory.GetCurrentDirectory(), IconPath)))
            {
                Debug.LogWarning($"[AlmondWaterCreator] No icon at '{IconPath}' -- staying on the " +
                                 "borrowed water bottle icon. Drop the PNG there and re-run " +
                                 "\"Backrooms/Assign Almond Water Icon\".");
                return false;
            }

            // Importer first -- without a Sprite there is nothing to assign.
            var importer = AssetImporter.GetAtPath(IconPath) as TextureImporter;
            if (importer != null)
            {
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);

                bool dirty = importer.textureType != TextureImporterType.Sprite
                             || importer.spriteImportMode != SpriteImportMode.Single
                             || !importer.alphaIsTransparency
                             || settings.spriteMeshType != SpriteMeshType.FullRect;

                if (dirty)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.alphaIsTransparency = true;
                    importer.mipmapEnabled = false;

                    // FullRect, not Tight -- with Tight, Unity trims the transparent border and the
                    // square inventory slot stretches the icon back out. Same reasoning as the
                    // spray can icon.
                    importer.ReadTextureSettings(settings);
                    settings.spriteMeshType = SpriteMeshType.FullRect;
                    importer.SetTextureSettings(settings);

                    importer.SaveAndReimport();
                }
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(IconPath);
            if (sprite == null)
            {
                Debug.LogError($"[AlmondWaterCreator] '{IconPath}' exists but did not import as a Sprite.");
                return false;
            }

            var target = new SerializedObject(definition);
            var icon = target.FindProperty("_icon");
            if (icon == null) return false;
            if (icon.objectReferenceValue == sprite) return false;

            icon.objectReferenceValue = sprite;
            target.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            Debug.Log($"[AlmondWaterCreator] Icon assigned from '{IconPath}'.");
            return true;
        }
    }
}
#endif
