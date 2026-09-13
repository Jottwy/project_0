#if UNITY_EDITOR
using System.IO;
using BackroomsSurvival.Wearables;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Assets del PROTOTIPO de equipo (ADR-147 PROPUESTA + enm. 1; D14 y su enmienda): los tags de espalda y cintura,
    /// las restricciones de los contenedores precreados, tres mochilas de D2 y tres cinturones por tier, con icono y
    /// pickup de placeholder. Menú Backrooms/Inventory/Create Backpack Prototype; también lo llama la escena de pruebas.
    ///
    /// CREAR-SI-FALTA por asset, como los demás creadores: el <c>_id</c> de una definición se acuña una vez y es el
    /// que viaja por el wire y se guarda. Los NÚMEROS (huecos, kg, reparto) sí se reescriben en cada pasada: son de
    /// maqueta, sin balancear.
    /// </summary>
    public static class BackroomsBackpackPrototypeCreator
    {
        public const string BackContainer = "Back";
        public const string BackStorageContainer = "BackStorage";
        public const int BackStorageSlots = 27;

        // D14 enm. 1: la barra son 2 manos que el cinturón amplía hasta 8 (las teclas 1-8 del vendor) y hay una base de 9.
        public const string WaistContainer = "Waist";
        public const string HolsterContainer = "Holster";
        public const string BaseContainer = "Backpack";
        public const int HandSlots = 2;
        public const int HolsterSlots = 8;
        public const int BaseSlots = 9;

        public const string TagPath = "Assets/Resources/Definitions/ItemTag/BR_Back Equipment.asset";
        public const string WaistTagPath = "Assets/Resources/Definitions/ItemTag/BR_Waist Equipment.asset";
        public const string BackRestrictionPath = "Assets/Data/Inventory/BR_Restriction_BackEquipment.asset";
        public const string StorageRestrictionPath = "Assets/Data/Inventory/BR_Restriction_BackStorage.asset";
        public const string WaistRestrictionPath = "Assets/Data/Inventory/BR_Restriction_WaistEquipment.asset";
        public const string HolsterRestrictionPath = "Assets/Data/Inventory/BR_Restriction_HolsterByBelt.asset";
        private const string DonorPath = "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_White T-Shirt.asset";
        private const string BeltArtDonorPath = "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Rope.asset";
        private const string ItemFolder = "Assets/Resources/Definitions/Item";

        public readonly struct Spec
        {
            public readonly string Asset, Description;
            public readonly int Slots;
            public readonly float Weight, MaxKg, CarryBonusPct;

            public Spec(string asset, string description, int slots, float weight, float maxKg, float carryBonusPct)
            {
                Asset = asset; Description = description; Slots = slots; Weight = weight; MaxKg = maxKg; CarryBonusPct = carryBonusPct;
            }

            public string Path => $"{ItemFolder}/{Asset}.asset";
        }

        // D2, cifras de maqueta. maxKg sin balancear.
        public static readonly Spec[] Backpacks =
        {
            new Spec("BR_Cloth Bag", "A cloth bag. Six slots, carries badly.", 6, 0.2f, 6f, -10f),
            new Spec("BR_Office Backpack", "An office backpack. Eighteen slots.", 18, 1.1f, 15f, 0f),
            new Spec("BR_Hiking Backpack", "A hiking backpack. Twenty-seven slots, spreads the load.", 27, 2.4f, 25f, 20f),
        };

        // D14: huecos que el cinturón suma a las 2 manos (T1 → 4, T2 → 6, T3 → 8). maxKg = lo que sumará al máximo (D6 enm. 2).
        public static readonly Spec[] Belts =
        {
            new Spec("BR_Cord Belt", "A cord tied as a belt. Two more slots.", 2, 0.1f, 2f, 0f),
            new Spec("BR_Work Belt", "A work belt. Four more slots.", 4, 0.4f, 4f, 0f),
            new Spec("BR_Tool Belt", "A tool belt. Six more slots, spreads the load a little.", 6, 0.9f, 6f, 5f),
        };

        [MenuItem("Backrooms/Inventory/Create Backpack Prototype")]
        public static void CreateMenu()
        {
            EnsureAssets();
            EnsureBelts();
            AssetDatabase.SaveAssets();
        }

        /// <summary>Espalda: crea lo que falte y devuelve las tres mochilas en el orden de <see cref="Backpacks"/>.</summary>
        public static ItemDefinition[] EnsureAssets()
        {
            var tag = EnsureTag(TagPath);
            EnsureTagRestriction(BackRestrictionPath, tag, "Solo mochilas");
            EnsureCapacityRestriction(StorageRestrictionPath, BackContainer, 0, true);

            var donor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DonorPath);
            if (donor == null) { Debug.LogError($"[Mochilas] falta el donante '{DonorPath}'"); return new ItemDefinition[0]; }

            var result = new ItemDefinition[Backpacks.Length];
            for (int i = 0; i < Backpacks.Length; i++)
                result[i] = EnsureWearable(Backpacks[i], donor, donor, tag);
            Debug.Log($"[Mochilas] prototipo listo: tag {tag.Id}, {result.Length} mochilas.");
            return result;
        }

        /// <summary>Cintura: tag, restricciones de la cintura y de la barra, y los tres cinturones.</summary>
        public static ItemDefinition[] EnsureBelts()
        {
            var tag = EnsureTag(WaistTagPath);
            EnsureTagRestriction(WaistRestrictionPath, tag, "Solo cinturones");
            // La barra: 2 manos siempre, más lo que dé el cinturón; sin tope de kg propio (lo pone el total).
            EnsureCapacityRestriction(HolsterRestrictionPath, WaistContainer, HandSlots, false);

            var actionDonor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DonorPath);
            var artDonor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(BeltArtDonorPath);
            if (actionDonor == null || artDonor == null) { Debug.LogError("[Cinturones] faltan los donantes"); return new ItemDefinition[0]; }

            var result = new ItemDefinition[Belts.Length];
            for (int i = 0; i < Belts.Length; i++)
                result[i] = EnsureWearable(Belts[i], artDonor, actionDonor, tag);
            Debug.Log($"[Cinturones] prototipo listo: tag {tag.Id}, {result.Length} cinturones.");
            return result;
        }

        private static ItemTagDefinition EnsureTag(string path)
        {
            var tag = AssetDatabase.LoadAssetAtPath<ItemTagDefinition>(path);
            if (tag != null) return tag;
            EnsureFolder(Path.GetDirectoryName(path));
            tag = ScriptableObject.CreateInstance<ItemTagDefinition>();
            AssetDatabase.CreateAsset(tag, path);
            tag.Validate_EditorOnly(new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));
            EditorUtility.SetDirty(tag);
            return tag;
        }

        private static void EnsureTagRestriction(string path, ItemTagDefinition tag, string reason)
        {
            if (AssetDatabase.LoadAssetAtPath<TagContainerRestriction>(path) != null) return;
            EnsureFolder(Path.GetDirectoryName(path));
            var restriction = TagContainerRestriction.Create(TagContainerRestriction.AllowType.WithTags, tag.Id);
            AssetDatabase.CreateAsset(restriction, path);
            var so = new SerializedObject(restriction);
            so.FindProperty("_rejectionReason").stringValue = reason;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureCapacityRestriction(string path, string owner, int baseSlots, bool limitWeight)
        {
            var restriction = AssetDatabase.LoadAssetAtPath<WornCapacityRestriction>(path);
            if (restriction == null)
            {
                EnsureFolder(Path.GetDirectoryName(path));
                restriction = WornCapacityRestriction.Create(owner);
                AssetDatabase.CreateAsset(restriction, path);
            }
            var so = new SerializedObject(restriction);
            so.FindProperty("_ownerContainer").stringValue = owner;
            so.FindProperty("_baseSlots").intValue = baseSlots;
            so.FindProperty("_limitWeight").boolValue = limitWeight;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static ItemDefinition EnsureWearable(Spec spec, ItemDefinition artDonor, ItemDefinition actionDonor, ItemTagDefinition tag)
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(spec.Path);
            bool created = definition == null;
            if (created)
            {
                definition = ScriptableObject.CreateInstance<ItemDefinition>();
                AssetDatabase.CreateAsset(definition, spec.Path);
                definition.Validate_EditorOnly(new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));
            }

            var art = new SerializedObject(artDonor);
            var actions = new SerializedObject(actionDonor);
            var to = new SerializedObject(definition);
            // Placeholder declarado: icono y pickup de un donante; acciones (Equip) de la camiseta blanca.
            to.CopyFromSerializedProperty(art.FindProperty("_icon"));
            to.CopyFromSerializedProperty(art.FindProperty("_pickup"));
            to.CopyFromSerializedProperty(actions.FindProperty("_actions"));
            to.FindProperty("_description").stringValue = spec.Description;
            to.FindProperty("_weight").floatValue = spec.Weight;
            // Invariante de la enm. 1: el vendor funde pilas comparando solo el id; una prenda nunca se apila.
            to.FindProperty("_stackSize").intValue = 1;
            to.FindProperty("_tag._value").intValue = tag.Id;
            var data = to.FindProperty("_data");
            data.arraySize = 1;
            data.GetArrayElementAtIndex(0).managedReferenceValue = new WearableCapacityData(spec.Slots, spec.MaxKg, spec.CarryBonusPct);
            to.ApplyModifiedPropertiesWithoutUndo();

            if (created && actionDonor.ParentGroup != null)
                definition.SetParentGroup_EditorOnly(actionDonor.ParentGroup);
            EditorUtility.SetDirty(definition);
            return definition;
        }

        private static void EnsureFolder(string path)
        {
            path = path.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
#endif
