#if UNITY_EDITOR
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.SaveSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// El bote de spray TIRADO EN EL SUELO. Se ejecuta desde
    /// "Backrooms ▸ Spray ▸ Crear el bote del suelo", y lo llama al final
    /// <see cref="BackroomsSprayModelSwapper"/> para que la mano y el suelo no se separen nunca.
    ///
    /// EL FALLO QUE CIERRA: `BR_Spray Can.asset` traía `_pickup` apuntando al prefab del VENDOR
    /// `STP_Pickup_WoodenTorch` (copiado del donante en `BackroomsSprayCanCreator`), y ese mismo
    /// prefab es el que instancian las tres rutas — el `DropAction` del vendor al soltarlo,
    /// `StpItemReplicator` en TODOS los clientes cuando el host republica el roster, y los spawns
    /// de loot. O sea que una lata en el suelo era una antorcha para todo el mundo, y una lata
    /// salida de un cofre nacía siendo antorcha.
    ///
    /// NO se puede arreglar tocando el prefab del vendor: lo comparten las antorchas de verdad.
    /// Hace falta uno propio, y el patrón exacto ya existía en
    /// <see cref="BackroomsAlmondWaterCreator"/> (único pickup propio del repo hasta hoy).
    ///
    /// TRES COSAS QUE PARECEN DETALLE Y NO LO SON:
    ///
    /// 1. El root se queda a escala 1. El `MaterialEffect` del vendor (resaltado al mirar) es en
    ///    espacio de OBJETO: escalar el root lo infla igual, y eso ya salió en juego como una
    ///    "marca" enorme sobre la botella de agua. Por eso la malla se hornea en metros.
    /// 2. Fuera el `LODGroup` y sus dos hijos `WoodenTorch_LOD1/LOD2`: llevan mallas de ANTORCHA.
    ///    Dejarlos es el mismo bug, pero solo a partir de cierta distancia y por tanto peor de
    ///    encontrar.
    /// 3. El `_prefabGuid` del `SaveableObject` llega clonado del de la antorcha, y
    ///    `SaveableDatabase.CreatePrefabsLookup` es un `Dictionary.Add` sin `try/catch`: clave
    ///    duplicada = excepción al cargar. Se refresca la base al terminar.
    ///
    /// Reejecutable: si el prefab ya existe se REPARA en sitio, para que su GUID sobreviva y con
    /// él la referencia `_pickup` de la definición.
    /// </summary>
    public static class BackroomsSprayPickupCreator
    {
        private const string DefinitionPath = "Assets/Resources/Definitions/Item/BR_Spray Can.asset";

        private const string DonorPickupPath =
            "Assets/PolymindGames/STP/Prefabs/Items/STP_Pickup_WoodenTorch.prefab";

        private const string PrefabFolder = "Assets/Prefabs/Items";
        private const string PrefabPath = PrefabFolder + "/BR_Pickup_SprayCan.prefab";
        private const string RootName = "BR_Pickup_SprayCan";

        private const string MeshPath = "Assets/Art/Items/SprayCan/BR_SprayCan_Mesh.asset";
        private const string MaterialPath = "Assets/Art/Items/SprayCan/BR_SprayCan_Mat.mat";

        private const string SaveableDatabasePath =
            "Assets/PolymindGames/FPSCore/Data/Resources/Managers/SaveableDatabase.asset";

        [MenuItem("Backrooms/Spray/Crear el bote del suelo", false, 99)]
        public static void Apply()
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            if (definition == null)
            {
                Debug.LogError($"[SprayPickup] No hay definición en '{DefinitionPath}'. " +
                               "Ejecuta antes 'Backrooms/Spray/Crear bote de spray'.");
                return;
            }

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mesh == null || material == null)
            {
                Debug.LogError($"[SprayPickup] Falta el arte horneado (malla={mesh != null}, " +
                               $"material={material != null}). Ejecuta antes " +
                               "'Backrooms/Spray/Aplicar modelo Meshy al bote'.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Prefabs");
            BackroomsEditorFolders.EnsureFolder(PrefabFolder);

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null &&
                !AssetDatabase.CopyAsset(DonorPickupPath, PrefabPath))
            {
                Debug.LogError($"[SprayPickup] No se pudo clonar '{DonorPickupPath}' a '{PrefabPath}'.");
                return;
            }

            if (!Author(mesh, material, definition)) return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var pickup = prefab != null ? prefab.GetComponent<ItemPickup>() : null;
            if (pickup == null)
            {
                Debug.LogError($"[SprayPickup] '{PrefabPath}' no tiene ItemPickup tras autorarlo.");
                return;
            }

            AssignPickupToDefinition(definition, pickup);
            RefreshSaveableDatabase();

            ItemDefinition.ReloadDefinitions_EditorOnly();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[SprayPickup] '{PrefabPath}' listo y enganchado a la definición " +
                      $"(id={definition.Id}). COMMITEAR el prefab y el .asset de la definición.");
        }

        /// <summary>
        /// Deja el prefab clonado con la lata dentro: malla, material, collider a medida y el item
        /// correcto. Devuelve false y no guarda si falta algo que haría un objeto roto en silencio.
        /// </summary>
        private static bool Author(Mesh mesh, Material material, ItemDefinition definition)
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                root.name = RootName;
                root.transform.localScale = Vector3.one;

                StripTorchLods(root);

                var filter = root.GetComponent<MeshFilter>();
                var renderer = root.GetComponent<MeshRenderer>();
                if (filter == null || renderer == null)
                {
                    Debug.LogError("[SprayPickup] El prefab clonado no tiene MeshFilter/MeshRenderer en " +
                                   "el root. No se autora nada.");
                    return false;
                }
                filter.sharedMesh = mesh;
                renderer.sharedMaterial = material;

                // Cápsula a medida de la lata, no la de la antorcha (r 0,03, alto 0,457). Sin
                // esto el objeto se apoya y rueda como si fuera un palo de medio metro.
                var capsule = root.GetComponent<CapsuleCollider>();
                if (capsule != null)
                {
                    var size = mesh.bounds.size;
                    capsule.direction = 1; // Y: la malla canónica está de pie
                    capsule.height = size.y;
                    capsule.radius = Mathf.Max(size.x, size.z) * 0.5f;
                    capsule.center = mesh.bounds.center;
                }

                var pickup = root.GetComponent<ItemPickup>();
                if (pickup == null)
                {
                    Debug.LogError("[SprayPickup] El prefab clonado no tiene ItemPickup. No se autora nada.");
                    return false;
                }

                // Sin esto se recoge una lata y entra una ANTORCHA en la mochila: el clon trae el
                // id del donante.
                var serialized = new SerializedObject(pickup);
                serialized.FindProperty("_item").FindPropertyRelative("_value").intValue = definition.Id;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                // Una lata pesa 400 g, no el kilo y medio de un palo de antorcha: el clon traía la
                // masa del donante y la lata rebotaba como un tronco.
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

        /// <summary>
        /// Quita la cadena de LOD heredada. Los dos hijos son mallas de antorcha y el
        /// <see cref="LODGroup"/> las elige por distancia, así que sin esto la lata se convierte
        /// en antorcha unos metros más allá — el mismo fallo, más difícil de ver.
        /// </summary>
        private static void StripTorchLods(GameObject root)
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

        /// <summary>
        /// El resaltado del vendor guarda una lista de renderers; los de los LOD que acabamos de
        /// borrar quedan en null dentro de ella. Un hueco no revienta hoy, pero es el tipo de
        /// residuo que revienta cuando alguien recorra la lista sin comprobar.
        /// </summary>
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

        /// <summary>
        /// `_pickup` es una referencia al COMPONENTE, no al GameObject: asignar el GameObject a un
        /// campo de tipo `ItemPickup` guarda null EN SILENCIO y el item no aparece jamás.
        ///
        /// `_stackPickup` NO se toca, y es una decisión, no un olvido: apunta al saco de tela del
        /// vendor, que es su visual genérico de "un montón de cosas" y NO arte prestado de la
        /// antorcha — la propia antorcha lo lleva igual. Hoy es inalcanzable (`_stackSize` 1 y el
        /// loot siempre tira count 1), y si algún día llegara un montón, un saco que da botes es
        /// más honesto que UNA lata representando cinco.
        /// </summary>
        private static void AssignPickupToDefinition(ItemDefinition definition, ItemPickup pickup)
        {
            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_pickup").objectReferenceValue = pickup;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
        }

        /// <summary>
        /// Rehace la lista de prefabs guardables. NO es opcional: el clon llega con el
        /// `_prefabGuid` de la antorcha y `SaveableDatabase.CreatePrefabsLookup` es un
        /// `Dictionary.Add` sin `try/catch`. Misma llamada que
        /// <c>BackroomsAlmondWaterCreator.RefreshSaveableDatabase</c>.
        /// </summary>
        private static void RefreshSaveableDatabase()
        {
            var database = AssetDatabase.LoadAssetAtPath<SaveableDatabase>(SaveableDatabasePath);
            if (database == null)
            {
                Debug.LogWarning($"[SprayPickup] No hay SaveableDatabase en '{SaveableDatabasePath}'. El " +
                                 "clon conserva el _prefabGuid de la antorcha; arréglalo antes de construir " +
                                 "un build o el lookup de guardables lanzará por clave duplicada.");
                return;
            }

            database.SetPrefabs_Editor(SaveableDatabase.FindAllSaveableObjectPrefabs());
            EditorUtility.SetDirty(database);
        }
    }
}
#endif
