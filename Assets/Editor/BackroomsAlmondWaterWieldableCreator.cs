#if UNITY_EDITOR
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.WieldableSystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Autora el wieldable de manos del Almond Water: hoy el item sólo existe como pickup de mundo
    /// (<see cref="BackroomsAlmondWaterCreator"/>) y no se puede equipar. Se ejecuta desde
    /// "Backrooms ▸ Almond Water ▸ Crear wieldable".
    ///
    /// CALCADO de <see cref="BackroomsBandageCreator"/>, que resolvió el mismo caso: un item sin
    /// ataque, sin luz y sin fuego. Donante de brazos y de la etiqueta «esto se empuña»: la
    /// brújula (<c>STP_Wieldable_Compass</c> / <c>STP_Compass</c>), el tool más limpio del vendor.
    ///
    /// A DIFERENCIA DE LA VENDA, aquí no hace falta crear el <c>ItemDefinition</c>: el Almond Water
    /// YA EXISTE (<see cref="BackroomsAlmondWaterCreator.DefinitionPath"/>), con icono, pickup y
    /// Drink action de <c>BackroomsAlmondWaterCreator</c>. Lo único que falta en ese asset es la
    /// etiqueta <c>_tag</c> — sin ella el item entra en la mochila pero no se deja equipar, sin
    /// error. Este script la copia del compass y NO TOCA nada más del asset (ni <c>_id</c>, que
    /// está en el wire, ni el Drink action ya asignado).
    ///
    /// MODELO: la malla real de Meshy (mismo import que ya usa el pickup de mundo), colgada de
    /// <c>Hand.R</c> como nodo propio — igual que el destornillador y la manivela — y NO por el
    /// truco de la venda (<c>BandageVisual</c> procedural), porque aquí ya hay una malla real que
    /// clonar. `Tools ▸ Interaction Authoring` sube desde cualquier malla activa hasta el hijo
    /// directo de un hueso para decidir el nodo del modelo, así que basta con colgarla ahí — no
    /// hace falta acertar la pose: el agarre se hornea después con esa herramienta.
    /// </summary>
    public static class BackroomsAlmondWaterWieldableCreator
    {
        public const string PrefabFolder = "Assets/Prefabs/Wieldables";
        public const string PrefabPath = PrefabFolder + "/BR_Wieldable_AlmondWater.prefab";
        public const string RootName = "BR_Wieldable_AlmondWater";

        /// <summary>Donante de la etiqueta «esto se empuña» y del prefab del wieldable.</summary>
        private const string WieldableDonorDefinitionPath =
            "Assets/PolymindGames/STP/Data/Resources/Definitions/Item/STP_Compass.asset";

        private const string WieldableDonorPrefabPath =
            "Assets/PolymindGames/STP/Prefabs/Wieldables/STP_Wieldable_Compass.prefab";

        /// <summary>Hueso del que cuelga la botella en la mano — la portadora, como todo lo demás en el proyecto.</summary>
        private const string HandBoneName = "Hand.R";

        /// <summary>
        /// Nombre del nodo del modelo. Termina en "Model" a propósito: es el desempate que usa
        /// `HandInteractionAnalyzer.Inspect` para preferirlo sobre cualquier resto del donante que
        /// se le haya escapado al ocultado de abajo.
        /// </summary>
        public const string ModelNodeName = "BR_AlmondWaterModel";

        /// <summary>Mallas del donante que SÍ se quedan encendidas: los brazos.</summary>
        private static readonly string[] KeptRenderers = { "LeftArm", "RightArm" };

        [MenuItem("Backrooms/Almond Water/Crear wieldable", false, 90)]
        public static void CreateIfMissing()
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(BackroomsAlmondWaterCreator.DefinitionPath);
            if (definition == null)
            {
                Debug.LogError($"[AlmondWaterWieldable] No hay '{BackroomsAlmondWaterCreator.DefinitionPath}'. " +
                               "Ejecuta antes 'Backrooms/Create Almond Water'.");
                return;
            }

            var wieldableDonorDefinition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(WieldableDonorDefinitionPath);
            var donorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(WieldableDonorPrefabPath);
            if (wieldableDonorDefinition == null || donorPrefab == null)
            {
                Debug.LogError($"[AlmondWaterWieldable] Falta un donante ('{WieldableDonorDefinitionPath}' o " +
                               $"'{WieldableDonorPrefabPath}'). Nada creado.");
                return;
            }

            var meshAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BackroomsAlmondWaterCreator.MeshPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(BackroomsAlmondWaterCreator.MaterialPath);
            if (meshAsset == null || material == null)
            {
                Debug.LogError("[AlmondWaterWieldable] Falta el arte de Meshy " +
                               $"(mesh={meshAsset != null} en '{BackroomsAlmondWaterCreator.MeshPath}', " +
                               $"material={material != null}). MeshyImports/ no viaja por git — ejecuta esto " +
                               "donde SÍ esté esa carpeta (el clon principal de Joel), no en un worktree " +
                               "recién clonado. Nada creado.");
                return;
            }

            var mesh = ResolveMesh(meshAsset);
            if (mesh == null)
            {
                Debug.LogError($"[AlmondWaterWieldable] No hay ninguna malla bajo '{BackroomsAlmondWaterCreator.MeshPath}'. " +
                               "Nada creado.");
                return;
            }

            if (EnsureWieldableTag(definition, wieldableDonorDefinition))
            {
                AssetDatabase.SaveAssets();
                ItemDefinition.ReloadDefinitions_EditorOnly();
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null)
            {
                Debug.Log($"[AlmondWaterWieldable] '{PrefabPath}' ya existe — intacto.");
            }
            else
            {
                CreateWieldablePrefab(donorPrefab, definition, mesh, material);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.LogWarning("[AlmondWaterWieldable] SIGUIENTE PASO OBLIGATORIO: 'Backrooms ▸ Almond Water ▸ " +
                             "Registrar en el jugador'. Sin esa alta, equipar la botella no saca nada en la " +
                             "mano y no hay ningún error que lo diga. Después, hornear el agarre con " +
                             "'Tools ▸ Interaction Authoring' (inspect → prepare OneHand → bake).");
        }

        private static Mesh ResolveMesh(GameObject meshAsset)
        {
            foreach (var filter in meshAsset.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null)
                    return filter.sharedMesh;
            }

            return null;
        }

        /// <summary>
        /// Copia SOLO <c>_tag</c> del compass al Almond Water ya existente. No toca <c>_id</c>, ni
        /// el icono, ni el Drink action: ese asset ya está bien montado por
        /// <see cref="BackroomsAlmondWaterCreator"/>, aquí sólo falta la etiqueta que decide si se
        /// puede empuñar.
        /// </summary>
        private static bool EnsureWieldableTag(ItemDefinition definition, ItemDefinition wieldableDonor)
        {
            var target = new SerializedObject(definition);
            var tagValue = target.FindProperty("_tag").FindPropertyRelative("_value");
            var donorValue = new SerializedObject(wieldableDonor).FindProperty("_tag").FindPropertyRelative("_value");

            if (tagValue.intValue == donorValue.intValue)
                return false;

            if (tagValue.intValue != 0)
            {
                Debug.LogWarning($"[AlmondWaterWieldable] '{BackroomsAlmondWaterCreator.DefinitionPath}' ya " +
                                 $"tenía _tag={tagValue.intValue}, distinto del compass ({donorValue.intValue}). " +
                                 "No se pisa: revísalo a mano si el item no se deja equipar.");
                return false;
            }

            tagValue.intValue = donorValue.intValue;
            target.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            Debug.Log($"[AlmondWaterWieldable] '{BackroomsAlmondWaterCreator.DefinitionPath}' _tag puesto a " +
                      $"{donorValue.intValue} (del compass) — ahora se puede empuñar.");
            return true;
        }

        private static void CreateWieldablePrefab(GameObject donorPrefab, ItemDefinition definition, Mesh mesh, Material material)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(donorPrefab);
            if (instance == null)
            {
                Debug.LogError("[AlmondWaterWieldable] No se pudo instanciar el prefab donante.");
                return;
            }

            try
            {
                // Romper el vínculo con el prefab del vendor: si no, esto sería una VARIANTE y un
                // reimport del .unitypackage arrastraría sus cambios hasta aquí dentro.
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);

                instance.name = RootName;

                SwapWieldableComponent(instance);
                HideDonorModel(instance);

                if (!AttachBottleMesh(instance, mesh, material))
                {
                    Debug.LogError("[AlmondWaterWieldable] No se pudo colgar la malla de la botella. " +
                                   "Nada guardado.");
                    return;
                }

                int repointed = RepointReferencedItem(instance, definition.Id);
                if (repointed == 0)
                {
                    Debug.LogError("[AlmondWaterWieldable] No se encontró ningún '_referencedItem' en el " +
                                   "prefab clonado. El wieldable seguiría ligado a la BRÚJULA: equipar el " +
                                   "agua sacaría una brújula. Revísalo antes de usarlo.");
                }

                PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
                Debug.Log($"[AlmondWaterWieldable] Creado '{PrefabPath}' (referencedItem reapuntado en " +
                          $"{repointed} componente(s) al id {definition.Id}).");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Fuera el <c>WieldableTool</c> (el compass lo usa para el pulsado que enseña la aguja; el
        /// agua no ataca ni usa nada así todavía), fuera <c>WieldableDurabilityDepleter</c> (quema
        /// carga que el agua no tiene) y fuera <c>WieldableRotatingElement</c> (la aguja). Nuestro
        /// componente se añade ANTES de quitarlos, igual que la venda: dejar el objeto un instante
        /// sin ningún <c>IWieldable</c> es pedirle a Unity que se queje.
        /// </summary>
        private static void SwapWieldableComponent(GameObject root)
        {
            if (root.GetComponent<AlmondWaterWieldable>() == null)
                root.AddComponent<AlmondWaterWieldable>();

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string type = component.GetType().Name;
                if (type != "WieldableTool" && type != "WieldableDurabilityDepleter"
                    && type != "WieldableRotatingElement") continue;

                Object.DestroyImmediate(component, true);
            }
        }

        /// <summary>
        /// Apaga el modelo de la brújula y deja encendidos los brazos. Se apagan y no se BORRAN
        /// porque el esqueleto de los brazos y el del item comparten raíz en este prefab: quitar
        /// una malla es barato de deshacer, quitar huesos no.
        /// </summary>
        private static void HideDonorModel(GameObject root)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                bool keep = System.Array.IndexOf(KeptRenderers, renderer.gameObject.name) >= 0;
                renderer.enabled = keep;
            }
        }

        /// <summary>
        /// Cuelga la malla real de la botella como HIJO DIRECTO de <see cref="HandBoneName"/>, sin
        /// intentar acertar su pose: `HandInteractionAnalyzer.Inspect` sube desde cualquier malla
        /// activa hasta el hijo directo de un hueso para decidir el nodo del modelo, y `bake`
        /// reescribe esta posición en cuanto se hornee el agarre. Aquí sólo hace falta que la
        /// botella EXISTA en la mano y que su renderer esté activo (para ganarle a los restos
        /// apagados del compass en el desempate por puntuación).
        /// </summary>
        private static bool AttachBottleMesh(GameObject root, Mesh mesh, Material material)
        {
            Transform hand = null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == HandBoneName) { hand = t; break; }
            }

            if (hand == null)
            {
                Debug.LogError($"[AlmondWaterWieldable] No aparece el hueso '{HandBoneName}' en el prefab " +
                               "donante. Nada colgado.");
                return false;
            }

            // Por si una pasada anterior dejó el nodo colgado de otro sitio.
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == ModelNodeName)
                {
                    Object.DestroyImmediate(t.gameObject);
                    break;
                }
            }

            var go = new GameObject(ModelNodeName);
            go.transform.SetParent(hand, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            // Compensa la escala del hueso: la malla ya sale a tamaño real del importer (ADR-030,
            // ver BackroomsAlmondWaterCreator), y un hueso de rig con escala distinta de 1 la
            // multiplicaría otra vez.
            var boneScale = hand.lossyScale;
            float boneFactor = Mathf.Max(1e-5f, Mathf.Max(boneScale.x, Mathf.Max(boneScale.y, boneScale.z)));
            go.transform.localScale = Vector3.one / boneFactor;

            go.layer = hand.gameObject.layer;

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = BakeMeshWithLongestAxisOnY(mesh);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return true;
        }

        /// <summary>
        /// `inspect` mide `axisAlignment` sobre <c>mesh.vertices</c> — coordenadas LOCALES DE LA
        /// MALLA — así que girar el nodo padre no cambia nada: medido, `axisAlignment` seguía en
        /// 0,00 después de rotar sólo el transform. La malla de Meshy sale tumbada (el eje largo
        /// cae en el plano XZ, no en +Y), así que aquí se hornea una COPIA con los vértices y
        /// normales rotados para que el eje más largo de sus bounds quede en +Y. No se toca la
        /// malla ORIGINAL (la usa también el pickup de mundo, de pie a su manera): esta copia sólo
        /// vive embebida en este prefab, igual que la gasa procedural de la venda
        /// (<c>BandageVisual</c>) — un <c>Mesh</c> nuevo asignado a un <c>MeshFilter</c> y guardado
        /// con <c>SaveAsPrefabAsset</c> queda como sub-asset del prefab sin pedir <c>CreateAsset</c>.
        /// Si el import se corrige algún día y +Y ya es el eje largo, esto devuelve la malla tal
        /// cual (rotación identidad).
        /// </summary>
        private static Mesh BakeMeshWithLongestAxisOnY(Mesh source)
        {
            var size = source.bounds.size;
            Vector3 longestLocalAxis = (size.x >= size.y && size.x >= size.z) ? Vector3.right
                : (size.z >= size.y) ? Vector3.forward
                : Vector3.up;
            var align = Quaternion.FromToRotation(longestLocalAxis, Vector3.up);
            if (align == Quaternion.identity)
                return source;

            var baked = Object.Instantiate(source);
            baked.name = source.name + "_YUp";

            var vertices = baked.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = align * vertices[i];
            baked.vertices = vertices;

            var normals = baked.normals;
            for (int i = 0; i < normals.Length; i++)
                normals[i] = align * normals[i];
            baked.normals = normals;

            var tangents = baked.tangents;
            for (int i = 0; i < tangents.Length; i++)
            {
                Vector3 t = align * (Vector3)tangents[i];
                tangents[i] = new Vector4(t.x, t.y, t.z, tangents[i].w);
            }
            baked.tangents = tangents;

            baked.RecalculateBounds();
            return baked;
        }

        /// <summary>Escribe un DataIdReference&lt;T&gt; cuya forma serializada es un entero _value bajo _referencedItem.</summary>
        private static int RepointReferencedItem(GameObject root, int newItemId)
        {
            int count = 0;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                var so = new SerializedObject(component);
                var prop = so.FindProperty("_referencedItem");
                if (prop == null) continue;

                var value = prop.FindPropertyRelative("_value") ?? prop;
                if (value.propertyType != SerializedPropertyType.Integer) continue;

                value.intValue = newItemId;
                so.ApplyModifiedPropertiesWithoutUndo();
                count++;
            }
            return count;
        }

        [MenuItem("Backrooms/Almond Water/Registrar en el jugador", false, 91)]
        public static void Register()
            => SprayCanWieldableRegistrar.RegisterWieldablePrefab(PrefabPath, RootName);

        /// <summary>
        /// Reintenta SÓLO <see cref="AttachBottleMesh"/> sobre el prefab YA CREADO — no toca
        /// _tag ni el registro en el jugador. Para cuando `inspect` da `GRIP_AXIS_NOT_Y` u otro
        /// fallo de colocación de la malla sin tener que borrar y recrear todo el wieldable.
        /// </summary>
        [MenuItem("Backrooms/Almond Water/Reparar modelo en la mano", false, 93)]
        public static void RepairModel()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[AlmondWaterWieldable] No hay '{PrefabPath}'. Ejecuta antes 'Crear wieldable'.");
                return;
            }

            var meshAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BackroomsAlmondWaterCreator.MeshPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(BackroomsAlmondWaterCreator.MaterialPath);
            var mesh = meshAsset != null ? ResolveMesh(meshAsset) : null;
            if (mesh == null || material == null)
            {
                Debug.LogError("[AlmondWaterWieldable] Falta el arte de Meshy — MeshyImports/ no viaja por git. " +
                               "Nada reparado.");
                return;
            }

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                if (AttachBottleMesh(root, mesh, material))
                {
                    PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                    Debug.Log($"[AlmondWaterWieldable] '{PrefabPath}' — modelo recolgado con el eje largo en +Y.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>Dar agua a mano, calcado del giver de la venda: sin loot table todavía, es la única forma de tener una.</summary>
        [MenuItem("Backrooms/Almond Water/Dar tres al jugador", false, 92)]
        private static void Give()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[AlmondWaterWieldable] Solo en Play: el inventario no existe fuera de una partida.");
                return;
            }

            var definition = ItemDefinition.GetWithName(BackroomsAlmondWaterCreator.ItemName);
            if (definition == null)
            {
                Debug.LogError($"[AlmondWaterWieldable] No hay ItemDefinition llamada '{BackroomsAlmondWaterCreator.ItemName}'. " +
                               "Ejecuta antes 'Backrooms/Create Almond Water'.");
                return;
            }

            var motor = LocalPlayerLocator.Find<PolymindGames.MovementSystem.CharacterControllerMotor>();
            var character = motor != null ? motor.GetComponentInParent<ICharacter>() : null;
            if (character?.Inventory == null)
            {
                Debug.LogError("[AlmondWaterWieldable] No se encuentra al jugador LOCAL con inventario. " +
                               "¿Estás dentro de una partida, no solo en el menú?");
                return;
            }

            // De una en una: `AddItemsById` en bloque trunca a 1 cuando el StackSize del item es 1.
            int added = 0;
            for (int i = 0; i < 3; i++)
            {
                var (ok, _) = character.Inventory.AddItemsById(definition.Id, 1);
                added += ok;
            }

            Debug.Log($"[AlmondWaterWieldable] {added} Almond Water(s) al inventario.");
        }
    }
}
#endif
