#if UNITY_EDITOR
using System.IO;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.SaveSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-133 — la linterna TIRADA EN EL SUELO, y con ella el objeto que los demás ven en tu mano.
    /// Se ejecuta desde "Backrooms ▸ Linterna ▸ Crear la linterna del suelo".
    ///
    /// CALCADO de <see cref="BackroomsScrewdriverPickupCreator"/>, y cierra el mismo fallo, que
    /// aquí ya no era "arte prestado" sino un BUG: `BR_Crank Flashlight.asset` traía `_pickup`
    /// apuntando al prefab del vendor `STP_Pickup_WoodenTorch`, y ese prefab es el que instancian
    /// las tres rutas —el `DropAction` al soltar, `StpItemReplicator` en todos los clientes cuando
    /// el host republica `stp_items`, y los spawns de loot— y además el que `ProxyHeldItemHook`
    /// cuelga de la mano del avatar remoto (ADR-023). O sea: una linterna en el suelo era una
    /// antorcha, un vecino con una linterna enseñaba una antorcha, y al recoger la linterna del
    /// suelo entraba UNA ANTORCHA en la mochila, porque el `_item` del pickup era el del donante.
    ///
    /// Se clona el pickup del hacha y no el de la antorcha, igual que el destornillador: los dos
    /// son `WieldableItemPickup`, pero el del hacha trae `BoxCollider` (la antorcha lleva cápsula)
    /// y la caja se ajusta a la malla sin pensar. Fuera el `LODGroup` y sus hijos, que llevan
    /// mallas de hacha. Root a escala 1: el resaltado del vendor es en espacio de objeto.
    ///
    /// LA DIFERENCIA CON EL DESTORNILLADOR: dos mallas. El cuerpo va en el root y la manivela en
    /// un HIJO, en la MISMA posición relativa que en la mano —sale de
    /// <see cref="BackroomsCrankFlashlightModelApplier.CrankLocalPosition"/>, el único sitio que
    /// la calcula— porque una linterna que al caer cambia la manivela de sitio es otro objeto.
    ///
    /// Y se refresca la `SaveableDatabase`: el clon trae el `_prefabGuid` del hacha y una clave
    /// duplicada revienta al cargar. Reejecutable: si el prefab ya existe se REPARA en sitio,
    /// conservando su GUID.
    /// </summary>
    public static class BackroomsCrankFlashlightPickupCreator
    {
        private const string DefinitionPath = BackroomsCrankFlashlightCreator.DefinitionPath;

        private const string DonorPickupPath =
            "Assets/PolymindGames/STP/Prefabs/Items/STP_Pickup_HuntingAxe.prefab";

        private const string PrefabFolder = "Assets/Prefabs/Items";
        public const string PrefabPath = PrefabFolder + "/BR_Pickup_CrankFlashlight.prefab";
        private const string RootName = "BR_Pickup_CrankFlashlight";
        private const string CrankNodeName = BackroomsCrankFlashlightModelApplier.CrankNodeName;

        private const string BodyMeshPath = BackroomsCrankFlashlightModelApplier.BodyMeshPath;
        private const string CrankMeshPath = BackroomsCrankFlashlightModelApplier.CrankMeshPath;
        private const string MaterialPath = BackroomsCrankFlashlightModelApplier.MaterialPath;

        private const string SaveableDatabasePath =
            "Assets/PolymindGames/FPSCore/Data/Resources/Managers/SaveableDatabase.asset";

        /// <summary>
        /// Los dos fotogramas crudos del icono, fondo negro y fondo blanco, en `Temp/` y no en un
        /// scratchpad de sesión: el tratamiento de imagen que deriva el alfa corre fuera de Unity y
        /// tiene que encontrarlos en un sitio que no cambie de sesión en sesión.
        /// </summary>
        private const string IconRawFolder = "Temp/icon";
        private const int IconRawSize = 1024;

        [MenuItem("Backrooms/Linterna/Crear la linterna del suelo", false, 93)]
        public static void Apply()
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            if (definition == null)
            {
                Debug.LogError($"[CrankFlashlightPickup] No hay definición en '{DefinitionPath}'.");
                return;
            }

            var body = AssetDatabase.LoadAssetAtPath<Mesh>(BodyMeshPath);
            var crank = AssetDatabase.LoadAssetAtPath<Mesh>(CrankMeshPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (body == null || crank == null || material == null)
            {
                Debug.LogError($"[CrankFlashlightPickup] Falta el arte horneado (cuerpo={body != null}, " +
                               $"manivela={crank != null}, material={material != null}). Ejecuta antes " +
                               "'Backrooms/Linterna/Aplicar modelo Meshy'.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Prefabs");
            BackroomsEditorFolders.EnsureFolder(PrefabFolder);

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null &&
                !AssetDatabase.CopyAsset(DonorPickupPath, PrefabPath))
            {
                Debug.LogError($"[CrankFlashlightPickup] No se pudo clonar '{DonorPickupPath}' a '{PrefabPath}'.");
                return;
            }

            if (!Author(body, crank, material, definition)) return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var pickup = prefab != null ? prefab.GetComponent<ItemPickup>() : null;
            if (pickup == null)
            {
                Debug.LogError($"[CrankFlashlightPickup] '{PrefabPath}' no tiene ItemPickup tras autorarlo.");
                return;
            }

            AssignPickupToDefinition(definition, pickup);
            RefreshSaveableDatabase();

            ItemDefinition.ReloadDefinitions_EditorOnly();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            RenderIconFrames(body, crank, material);

            Debug.Log($"[CrankFlashlightPickup] '{PrefabPath}' listo y enganchado a la definición (id={definition.Id}).");
        }

        private static bool Author(Mesh body, Mesh crank, Material material, ItemDefinition definition)
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
                    Debug.LogError("[CrankFlashlightPickup] El clon no tiene MeshFilter/MeshRenderer en el root.");
                    return false;
                }
                filter.sharedMesh = body;
                renderer.sharedMaterial = material;

                AttachCrank(root, crank, material, body);

                // Caja a medida del cuerpo (el hacha traía 0,15 × 0,69 × 0,04). La manivela queda
                // fuera de la caja a propósito: es la parte fina, y una caja que la abarcara haría
                // que la linterna reposara en el suelo flotando sobre el pomo.
                var box = root.GetComponent<BoxCollider>();
                if (box != null)
                {
                    box.center = body.bounds.center;
                    box.size = body.bounds.size;
                }

                var pickup = root.GetComponent<ItemPickup>();
                if (pickup == null)
                {
                    Debug.LogError("[CrankFlashlightPickup] El clon no tiene ItemPickup. No se autora nada.");
                    return false;
                }

                // Sin esto se recoge una linterna y entra un HACHA en la mochila.
                var serialized = new SerializedObject(pickup);
                serialized.FindProperty("_item").FindPropertyRelative("_value").intValue = definition.Id;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var rigidbody = root.GetComponent<Rigidbody>();
                if (rigidbody != null && definition.Weight > 0f) rigidbody.mass = definition.Weight;

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
        /// La manivela del suelo, en su sitio y quieta. Sin componente que la gire: un pickup no da
        /// cuerda. Reejecutable: si el hijo ya está, se le reponen malla, material y posición.
        /// </summary>
        private static void AttachCrank(GameObject root, Mesh crank, Material material, Mesh body)
        {
            var node = root.transform.Find(CrankNodeName)?.gameObject;
            if (node == null)
            {
                node = new GameObject(CrankNodeName);
                node.transform.SetParent(root.transform, false);
            }

            node.layer = root.layer;
            node.transform.localPosition = BackroomsCrankFlashlightModelApplier.CrankLocalPosition(body, crank);
            node.transform.localRotation = Quaternion.identity;
            node.transform.localScale = Vector3.one;

            // Con `==` y no con `??`: `GetComponent` devuelve un objeto que Unity considera nulo
            // por su `==` sobrecargado, pero que para `??` es una referencia válida. Con `??` el
            // `AddComponent` no corría nunca y `sharedMesh` reventaba con MissingComponentException.
            var filter = node.GetComponent<MeshFilter>();
            if (filter == null) filter = node.AddComponent<MeshFilter>();
            filter.sharedMesh = crank;

            var renderer = node.GetComponent<MeshRenderer>();
            if (renderer == null) renderer = node.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
        }

        /// <summary>Fuera el LODGroup y los hijos con malla del hacha — pero NO nuestro hijo de la
        /// manivela, que en una segunda pasada ya está ahí.</summary>
        private static void StripDonorLods(GameObject root)
        {
            var group = root.GetComponent<LODGroup>();
            if (group != null) Object.DestroyImmediate(group, true);

            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                var child = root.transform.GetChild(i);
                if (child.name == CrankNodeName) continue;
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
        /// silencio. `_stackPickup` no se toca (saco genérico del vendor, no arte de la antorcha).</summary>
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
                Debug.LogWarning($"[CrankFlashlightPickup] No hay SaveableDatabase en '{SaveableDatabasePath}'.");
                return;
            }
            database.SetPrefabs_Editor(SaveableDatabase.FindAllSaveableObjectPrefabs());
            EditorUtility.SetDirty(database);
        }

        /// <summary>
        /// Dos fotogramas del modelo horneado —cuerpo Y manivela, montados como en el suelo—, fondo
        /// negro y fondo blanco, en perfil con una pizca de elevación. El alfa sale por diferencia
        /// entre los dos; el recorte, el giro de 25° y el 94 % del lienzo los hace el mismo
        /// tratamiento de imagen que los otros tres iconos. Aquí sólo se renderiza el modelo.
        /// </summary>
        private static void RenderIconFrames(Mesh body, Mesh crank, Material material)
        {
            Directory.CreateDirectory(IconRawFolder);

            var stage = new GameObject("IconStage") { hideFlags = HideFlags.HideAndDontSave };
            var pru = new PreviewRenderUtility();
            try
            {
                stage.AddComponent<MeshFilter>().sharedMesh = body;
                stage.AddComponent<MeshRenderer>().sharedMaterial = material;
                AttachCrank(stage, crank, material, body);

                // La caja de las DOS piezas, para que la manivela no se salga del encuadre.
                var bounds = body.bounds;
                var crankOffset = BackroomsCrankFlashlightModelApplier.CrankLocalPosition(body, crank);
                var crankBounds = new Bounds(crank.bounds.center + crankOffset, crank.bounds.size);
                bounds.Encapsulate(crankBounds);

                foreach (var (bg, file) in new[]
                         {
                             (Color.black, "crankflashlight_icon_black.png"),
                             (Color.white, "crankflashlight_icon_white.png"),
                         })
                {
                    var rect = new Rect(0, 0, IconRawSize, IconRawSize);
                    pru.BeginStaticPreview(rect);
                    pru.AddSingleGO(stage);

                    var cam = pru.camera;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = bg;
                    cam.orthographic = true;
                    cam.orthographicSize = bounds.extents.magnitude * 1.05f;
                    cam.nearClipPlane = 0.01f;
                    cam.farClipPlane = 10f;
                    // Desde el lado de la MANIVELA (+X), que es lo que distingue esta linterna de
                    // cualquier otra en un icono de 64 píxeles.
                    var dir = new Vector3(1f, 0.18f, -0.35f).normalized;
                    cam.transform.position = bounds.center + dir * 2f;
                    cam.transform.LookAt(bounds.center, Vector3.up);

                    pru.lights[0].intensity = 1.2f;
                    pru.lights[0].transform.rotation = Quaternion.Euler(35f, 40f, 0f);
                    pru.lights[1].intensity = 0.6f;
                    pru.ambientColor = new Color(0.35f, 0.35f, 0.38f);

                    pru.Render(true, false);
                    var tex = pru.EndStaticPreview();
                    if (tex == null)
                    {
                        Debug.LogError("[CrankFlashlightPickup] El render del icono devolvió nulo.");
                        return;
                    }
                    string path = Path.Combine(IconRawFolder, file);
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);
                    Debug.Log($"[CrankFlashlightPickup] Fotograma del icono en '{path}'.");
                }
            }
            finally
            {
                pru.Cleanup();
                Object.DestroyImmediate(stage);
            }
        }
    }
}
#endif
