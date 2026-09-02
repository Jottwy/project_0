#if UNITY_EDITOR
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.SaveSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// El destornillador TIRADO EN EL SUELO. Se ejecuta desde "Backrooms ▸ Screwdriver ▸ Crear el
    /// destornillador del suelo", y lo llama al final <see cref="BackroomsScrewdriverModelApplier"/>
    /// para que la mano y el suelo no se separen nunca.
    ///
    /// CALCADO de <see cref="BackroomsSprayPickupCreator"/>. El fallo que cierra es el mismo:
    /// `BR_Screwdriver.asset` traía `_pickup` apuntando al prefab del VENDOR
    /// `STP_Pickup_HuntingAxe` (donado en `BackroomsDismantleAssetsCreator`), y ese prefab es el que
    /// instancian las tres rutas —el `DropAction` al soltar, `StpItemReplicator` en todos los
    /// clientes cuando el host republica `stp_items`, y los spawns de loot— y además el que
    /// `ProxyHeldItemHook` cuelga de la mano del avatar remoto. O sea: un destornillador en el
    /// suelo, o en la mano de otro jugador, era un hacha.
    ///
    /// Se clona el pickup del hacha porque es un pickup DE WIELDABLE (`WieldableItemPickup`), que
    /// es la clase correcta para una herramienta; el del bote es de antorcha. Fuera el `LODGroup` y
    /// sus hijos `HuntingAxe_LOD1/2`, que llevan mallas de hacha. Root a escala 1 (el resaltado del
    /// vendor es en espacio de objeto). Y se refresca la `SaveableDatabase`: el clon trae el
    /// `_prefabGuid` del hacha y una clave duplicada revienta al cargar.
    ///
    /// Reejecutable: si el prefab ya existe se REPARA en sitio, conservando su GUID.
    /// </summary>
    public static class BackroomsScrewdriverPickupCreator
    {
        private const string DefinitionPath = BackroomsScrewdriverModelApplier.DefinitionPath;

        private const string DonorPickupPath =
            "Assets/PolymindGames/STP/Prefabs/Items/STP_Pickup_HuntingAxe.prefab";

        private const string PrefabFolder = "Assets/Prefabs/Items";
        public const string PrefabPath = PrefabFolder + "/BR_Pickup_Screwdriver.prefab";
        private const string RootName = "BR_Pickup_Screwdriver";

        private const string MeshPath = BackroomsScrewdriverModelApplier.BakedMeshPath;
        private const string MaterialPath = BackroomsScrewdriverModelApplier.MaterialPath;

        private const string SaveableDatabasePath =
            "Assets/PolymindGames/FPSCore/Data/Resources/Managers/SaveableDatabase.asset";

        [MenuItem("Backrooms/Screwdriver/Crear el destornillador del suelo", false, 92)]
        public static void Apply()
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            if (definition == null)
            {
                Debug.LogError($"[ScrewdriverPickup] No hay definición en '{DefinitionPath}'.");
                return;
            }

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mesh == null || material == null)
            {
                Debug.LogError($"[ScrewdriverPickup] Falta el arte horneado (malla={mesh != null}, " +
                               $"material={material != null}). Ejecuta antes 'Backrooms/Screwdriver/Aplicar modelo Meshy'.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Prefabs");
            BackroomsEditorFolders.EnsureFolder(PrefabFolder);

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null &&
                !AssetDatabase.CopyAsset(DonorPickupPath, PrefabPath))
            {
                Debug.LogError($"[ScrewdriverPickup] No se pudo clonar '{DonorPickupPath}' a '{PrefabPath}'.");
                return;
            }

            if (!Author(mesh, material, definition)) return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var pickup = prefab != null ? prefab.GetComponent<ItemPickup>() : null;
            if (pickup == null)
            {
                Debug.LogError($"[ScrewdriverPickup] '{PrefabPath}' no tiene ItemPickup tras autorarlo.");
                return;
            }

            AssignPickupToDefinition(definition, pickup);
            RefreshSaveableDatabase();

            ItemDefinition.ReloadDefinitions_EditorOnly();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ScrewdriverPickup] '{PrefabPath}' listo y enganchado a la definición (id={definition.Id}).");
        }

        private static bool Author(Mesh mesh, Material material, ItemDefinition definition)
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                root.name = RootName;
                root.transform.localScale = Vector3.one;

                StripDonorLods(root);

                var filter = root.GetComponent<MeshFilter>();
                var renderer = root.GetComponent<MeshRenderer>();
                if (filter == null || renderer == null)
                {
                    Debug.LogError("[ScrewdriverPickup] El clon no tiene MeshFilter/MeshRenderer en el root.");
                    return false;
                }
                filter.sharedMesh = mesh;
                renderer.sharedMaterial = material;

                // Caja a medida del destornillador (el hacha traía 0,15 × 0,69 × 0,04). La malla
                // canónica está de pie y centrada, así que la caja sale de `bounds` sin corrección.
                var box = root.GetComponent<BoxCollider>();
                if (box != null)
                {
                    box.center = mesh.bounds.center;
                    box.size = mesh.bounds.size;
                }

                var pickup = root.GetComponent<ItemPickup>();
                if (pickup == null)
                {
                    Debug.LogError("[ScrewdriverPickup] El clon no tiene ItemPickup. No se autora nada.");
                    return false;
                }

                // Sin esto se recoge un destornillador y entra un HACHA en la mochila.
                var serialized = new SerializedObject(pickup);
                serialized.FindProperty("_item").FindPropertyRelative("_value").intValue = definition.Id;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var body = root.GetComponent<Rigidbody>();
                if (body != null && definition.Weight > 0f) body.mass = definition.Weight;

                PruneMaterialEffectRenderers(root);

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void StripDonorLods(GameObject root)
        {
            var group = root.GetComponent<LODGroup>();
            if (group != null) Object.DestroyImmediate(group, true);

            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                var child = root.transform.GetChild(i);
                if (child.GetComponent<MeshRenderer>() == null) continue;
                Object.DestroyImmediate(child.gameObject, true);
            }
        }

        private static void PruneMaterialEffectRenderers(GameObject root)
        {
            foreach (var component in root.GetComponents<Component>())
            {
                if (component == null) continue;
                var so = new SerializedObject(component);
                var list = so.FindProperty("_renderers");
                if (list == null || !list.isArray) continue;

                bool changed = false;
                for (int i = list.arraySize - 1; i >= 0; i--)
                {
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue != null) continue;
                    list.DeleteArrayElementAtIndex(i);
                    changed = true;
                }
                if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        /// <summary>`_pickup` es una referencia al COMPONENTE: asignar el GameObject guarda null en
        /// silencio. `_stackPickup` no se toca (saco genérico del vendor, no arte del hacha).</summary>
        private static void AssignPickupToDefinition(ItemDefinition definition, ItemPickup pickup)
        {
            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_pickup").objectReferenceValue = pickup;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
        }

        private static void RefreshSaveableDatabase()
        {
            var database = AssetDatabase.LoadAssetAtPath<SaveableDatabase>(SaveableDatabasePath);
            if (database == null)
            {
                Debug.LogWarning($"[ScrewdriverPickup] No hay SaveableDatabase en '{SaveableDatabasePath}'.");
                return;
            }
            database.SetPrefabs_Editor(SaveableDatabase.FindAllSaveableObjectPrefabs());
            EditorUtility.SetDirty(database);
        }
    }
}
#endif
