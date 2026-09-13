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
    /// Assets del PROTOTIPO de mochilas (ADR-147 PROPUESTA + enm. 1): el tag de espalda, las dos restricciones de
    /// los contenedores precreados y tres mochilas de D2 con malla e icono de camiseta de placeholder. Menú
    /// Backrooms/Inventory/Create Backpack Prototype; también lo llama la escena de pruebas.
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

        public const string TagPath = "Assets/Resources/Definitions/ItemTag/BR_Back Equipment.asset";
        public const string BackRestrictionPath = "Assets/Data/Inventory/BR_Restriction_BackEquipment.asset";
        public const string StorageRestrictionPath = "Assets/Data/Inventory/BR_Restriction_BackStorage.asset";
        private const string DonorPath = "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_White T-Shirt.asset";
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

        [MenuItem("Backrooms/Inventory/Create Backpack Prototype")]
        public static void CreateMenu()
        {
            EnsureAssets();
            AssetDatabase.SaveAssets();
        }

        /// <summary>Crea lo que falte y devuelve las tres definiciones en el orden de <see cref="Backpacks"/>.</summary>
        public static ItemDefinition[] EnsureAssets()
        {
            var tag = EnsureTag();
            EnsureBackRestriction(tag);
            EnsureStorageRestriction();

            var donor = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DonorPath);
            if (donor == null) { Debug.LogError($"[Mochilas] falta el donante '{DonorPath}'"); return new ItemDefinition[0]; }

            var result = new ItemDefinition[Backpacks.Length];
            for (int i = 0; i < Backpacks.Length; i++)
                result[i] = EnsureBackpack(Backpacks[i], donor, tag);
            Debug.Log($"[Mochilas] prototipo listo: tag {tag.Id}, {result.Length} mochilas.");
            return result;
        }

        private static ItemTagDefinition EnsureTag()
        {
            var tag = AssetDatabase.LoadAssetAtPath<ItemTagDefinition>(TagPath);
            if (tag != null) return tag;
            EnsureFolder(Path.GetDirectoryName(TagPath));
            tag = ScriptableObject.CreateInstance<ItemTagDefinition>();
            AssetDatabase.CreateAsset(tag, TagPath);
            tag.Validate_EditorOnly(new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));
            EditorUtility.SetDirty(tag);
            return tag;
        }

        private static void EnsureBackRestriction(ItemTagDefinition tag)
        {
            if (AssetDatabase.LoadAssetAtPath<TagContainerRestriction>(BackRestrictionPath) != null) return;
            EnsureFolder(Path.GetDirectoryName(BackRestrictionPath));
            var restriction = TagContainerRestriction.Create(TagContainerRestriction.AllowType.WithTags, tag.Id);
            AssetDatabase.CreateAsset(restriction, BackRestrictionPath);
            var so = new SerializedObject(restriction);
            so.FindProperty("_rejectionReason").stringValue = "Solo mochilas";
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureStorageRestriction()
        {
            if (AssetDatabase.LoadAssetAtPath<WornCapacityRestriction>(StorageRestrictionPath) != null) return;
            EnsureFolder(Path.GetDirectoryName(StorageRestrictionPath));
            AssetDatabase.CreateAsset(WornCapacityRestriction.Create(BackContainer), StorageRestrictionPath);
        }

        private static ItemDefinition EnsureBackpack(Spec spec, ItemDefinition donor, ItemTagDefinition tag)
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(spec.Path);
            bool created = definition == null;
            if (created)
            {
                definition = ScriptableObject.CreateInstance<ItemDefinition>();
                AssetDatabase.CreateAsset(definition, spec.Path);
                definition.Validate_EditorOnly(new DataDefinition.ValidationContext(false, DataDefinition.ValidationTrigger.Created));
            }

            var from = new SerializedObject(donor);
            var to = new SerializedObject(definition);
            // Placeholder declarado: icono, pickup y acciones (Equip) de la camiseta blanca.
            to.CopyFromSerializedProperty(from.FindProperty("_icon"));
            to.CopyFromSerializedProperty(from.FindProperty("_pickup"));
            to.CopyFromSerializedProperty(from.FindProperty("_actions"));
            to.FindProperty("_description").stringValue = spec.Description;
            to.FindProperty("_weight").floatValue = spec.Weight;
            // Invariante de la enm. 1: el vendor funde pilas comparando solo el id; una mochila nunca se apila.
            to.FindProperty("_stackSize").intValue = 1;
            to.FindProperty("_tag._value").intValue = tag.Id;
            var data = to.FindProperty("_data");
            data.arraySize = 1;
            data.GetArrayElementAtIndex(0).managedReferenceValue = new WearableCapacityData(spec.Slots, spec.MaxKg, spec.CarryBonusPct);
            to.ApplyModifiedPropertiesWithoutUndo();

            if (created && donor.ParentGroup != null)
                definition.SetParentGroup_EditorOnly(donor.ParentGroup);
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
