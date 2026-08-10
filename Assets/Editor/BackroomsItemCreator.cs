#if UNITY_EDITOR
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Authors the four crafting materials the stabilizer recipes consume: Metal, Circuit, Battery,
    /// Cable. Run via "Backrooms ▸ Create Craft Materials".
    ///
    /// WHY THESE FOUR AND NO OTHERS (ADR-064): `backend/src/crafting/recipes.rs` costs its four
    /// recipes in `Item::{Metal, Circuit, Battery, Cable}` — an ABSTRACT Rust enum that describes
    /// world drops, not the player's bag. The bag is STP (`stp_inventory`, raw `ItemDefinition`
    /// ids, client-reported). Nothing in the 40-item vendor catalogue corresponds to those four:
    /// the closest, `Metal Shard`, is a generic survival material and a different thing. ADR-064
    /// resolves the gap by moving crafting to the STP vocabulary instead of mapping the enum, so
    /// these four have to EXIST as definitions before any of it can be wired.
    ///
    /// OUTSIDE VENDOR TERRITORY ON PURPOSE. These land under "Assets/Resources/Definitions/Item",
    /// not under `Assets/PolymindGames/**`. `DataDefinition.LoadDefinitionsFromResources` scans the
    /// literal path "Definitions/{TypeName-minus-Definition}" and `Resources.LoadAll` MERGES every
    /// Resources folder in the project, so these are picked up alongside the vendor's without
    /// editing a single vendor file — the same trick <see cref="BackroomsCarryableCreator"/> and
    /// <see cref="BackroomsBuildingPieceCreator"/> already use for their definitions.
    ///
    /// NAME RESOLUTION IS THE WHOLE REASON FOR THE "BR_" PREFIX. `DataDefinition.Name` is
    /// `name.RemovePrefix()`, which strips through the FIRST '_'. So the asset `BR_Metal` resolves
    /// to the name "Metal", which is exactly the string the loot pools look up via
    /// `ItemDefinition.GetWithName`. Rename the asset without keeping a prefix and the pools stop
    /// finding it — silently, with a warning and a skipped slot.
    ///
    /// crear-si-falta, per item, for the same reason as the other creators: `DataDefinition` mints
    /// `_id` from `Random.Range(int.MinValue, int.MaxValue)`, and an `ItemDefinition`'s `_id` is the
    /// `def_id` that travels on the wire (`StpItemReplicator` resolves spawns with
    /// `ItemDefinition.GetWithId(it.defId)`) AND is what a save's `stp_inventory` stores.
    /// Regenerating one would orphan every existing save and desync live replication. COMMIT THE
    /// GENERATED ASSETS.
    ///
    /// PLACEHOLDER ART, DECLARED. Icon and pickup prefabs are DONATED BY REFERENCE from the donor
    /// item below, so all four look like scrap in the world and share its inventory icon. That is
    /// safe rather than merely tolerable: `StpItemReplicator` destroys the vendor `ItemPickup`
    /// component off the spawned prefab and drives identity from `def_id` over the wire, so a
    /// shared pickup prefab cannot make a Circuit behave like the donor. What it costs is purely
    /// visual — four materials the player cannot tell apart on the floor. Real art is a later pass.
    /// </summary>
    public static class BackroomsItemCreator
    {
        // Must stay under "<a Resources folder>/Definitions/Item" — that literal path is what
        // DataDefinition.LoadDefinitionsFromResources scans for ItemDefinition.
        private const string DefinitionFolder = "Assets/Resources/Definitions/Item";

        // Plain vendor material: no _actions, no CraftingData, and it carries both a single and a
        // stacked pickup prefab. Donating from an item WITH actions would hand the materials a
        // consume/equip behaviour nobody asked for.
        private const string DonorPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Metal Shard.asset";

        // TODO(balance): first-pass numbers, not tuned. Stack size is the one that bites: a T3
        // stabilizer costs Metal 50 / Circuit 40 / Battery 10 / Cable 30, so at 25 per stack a full
        // T3 occupies ~7 inventory slots. Lower it and the bag becomes the bottleneck instead of
        // the scavenging; raise it and carrying capacity stops being a decision at all.
        private const int StackSize = 25;

        private readonly struct Material_
        {
            public readonly string Name;
            public readonly string Description;
            public readonly float Weight;

            public Material_(string name, string description, float weight)
            {
                Name = name;
                Description = description;
                Weight = weight;
            }
        }

        // The names here are load-bearing: they must match the pool entries in ChunkLootRoll and
        // StpChestSpawner character for character, and they must match `Item::type_name()` on the
        // Rust side (lowercased) so the two vocabularies stay legible to each other.
        private static readonly Material_[] Materials =
        {
            new("Metal", "Salvaged plating. The backbone of anything meant to hold still.", 1.2f),
            new("Circuit", "Scavenged board, mostly intact. Fragile, and there is no second source.", 0.2f),
            new("Battery", "Still holds a charge. Nothing down here recharges it.", 0.6f),
            new("Cable", "Copper run pulled from a wall. Long enough to matter.", 0.5f),
        };

        [MenuItem("Backrooms/Create Craft Materials")]
        public static void CreateIfMissing()
        {
            var donor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DonorPath);
            if (donor == null)
            {
                Debug.LogError($"[BackroomsItemCreator] No donor ItemDefinition at '{DonorPath}'. " +
                               "Nothing created — without it the four materials would have a null pickup " +
                               "and StpItemReplicator would spawn nothing for them.");
                return;
            }

            // Fail loud BEFORE writing anything. A definition whose _pickup is null resolves fine in
            // GetWithName and then produces a silent no-spawn at loot time, which is a much worse
            // failure to diagnose than this message.
            if (donor.Pickup == null)
            {
                Debug.LogError($"[BackroomsItemCreator] Donor '{donor.Name}' has no _pickup. Nothing " +
                               "created: the materials would resolve by name and then never appear.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Resources");
            BackroomsEditorFolders.EnsureFolder("Assets/Resources/Definitions");
            BackroomsEditorFolders.EnsureFolder(DefinitionFolder);

            int created = 0;
            int kept = 0;
            foreach (var material in Materials)
            {
                string path = $"{DefinitionFolder}/BR_{material.Name}.asset";
                var existing = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
                if (existing != null)
                {
                    // Deliberately untouched, not "updated": the id is on the wire and in saves.
                    // But "exists" is not the same as "usable" — CreateAsset writes the file before the
                    // configuration below finishes, so a run that died halfway leaves a survivor that
                    // every later run would keep skipping. Check the one field whose absence is silent
                    // at resolve time and fatal at spawn time.
                    if (existing.Pickup == null)
                    {
                        Debug.LogError($"[BackroomsItemCreator] '{path}' exists but has NO _pickup — a " +
                                       "half-written asset from an interrupted run. It resolves by name and " +
                                       "then spawns nothing. Delete it by hand and re-run; its id was never " +
                                       "on the wire if it never spawned.");
                    }
                    else
                    {
                        Debug.Log($"[BackroomsItemCreator] '{path}' already exists (id={existing.Id}) — left " +
                                  "untouched. Delete it by hand and re-run if you really want a new id, " +
                                  "knowing it orphans saves that reference the old one.");
                    }

                    kept++;
                    continue;
                }

                CreateMaterial(material, donor, path);
                created++;
            }

            if (created > 0)
            {
                // Make the new definitions visible to the already-cached Definitions array, so a loot
                // roll resolves them by name without waiting for a domain reload.
                ItemDefinition.ReloadDefinitions_EditorOnly();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.Log($"[BackroomsItemCreator] {created} created, {kept} already present. The loot pools in " +
                      "ChunkLootRoll/StpChestSpawner reference these by NAME, so until they exist a rolled " +
                      "slot logs 'not in ItemDefinition DB; skipped' and is dropped — the two halves are " +
                      "independently safe to land.");
        }

        private static void CreateMaterial(Material_ material, ItemDefinition donor, string path)
        {
            var definition = ScriptableObject.CreateInstance<ItemDefinition>();
            AssetDatabase.CreateAsset(definition, path);

            // Assigns the unique auto-generated _id. AssignID is private; this is the vendor's own
            // entry point to it, and the only correct way to mint an id — hand-picking one risks a
            // collision the vendor's retry loop is there to avoid.
            definition.Validate_EditorOnly(
                new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));

            var source = new SerializedObject(donor);
            var target = new SerializedObject(definition);

            // Donated by reference: the visual identity only. _rarity is deliberately NOT copied —
            // ItemDefinition.Rarity already falls back to ItemRarityLevel.DefaultRarity when null.
            target.CopyFromSerializedProperty(source.FindProperty("_icon"));
            target.CopyFromSerializedProperty(source.FindProperty("_pickup"));
            target.CopyFromSerializedProperty(source.FindProperty("_stackPickup"));

            target.FindProperty("_description").stringValue = material.Description;
            target.FindProperty("_weight").floatValue = material.Weight;
            target.FindProperty("_stackSize").intValue = StackSize;
            target.ApplyModifiedPropertiesWithoutUndo();

            // The category goes through the vendor's own API and NOT through a CopyFromSerializedProperty
            // of "_parentGroup", which is the difference between a one-way reference and a registered
            // member: SetParentGroup_EditorOnly also calls AddMember_EditorOnly on the group, so the
            // category's own member array knows about the item. Copying the property raw sets only the
            // item→category half and leaves the array that ItemGenerator enumerates without it. Same
            // reasoning, verbatim, as BackroomsBuildingPieceCreator.
            if (donor.ParentGroup != null)
                definition.SetParentGroup_EditorOnly(donor.ParentGroup);

            EditorUtility.SetDirty(definition);

            // The name the loot pools look up is Name, which is name.RemovePrefix() — verify the
            // prefix actually stripped to what the pools expect rather than trusting the convention.
            if (definition.Name != material.Name)
            {
                Debug.LogError($"[BackroomsItemCreator] '{path}' resolves to Name='{definition.Name}', not " +
                               $"'{material.Name}'. The loot pools look it up by that exact string and will " +
                               "skip the slot. Check the BR_ prefix on the asset file name.");
            }

            Debug.Log($"[BackroomsItemCreator] Created '{path}' (id={definition.Id}, Name='{definition.Name}').");
        }
    }
}
#endif
