#if UNITY_EDITOR
using System.IO;
using BackroomsSurvival.UI;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Hornea el reloj táctico de Meshy y lo monta en la muñeca del prefab del reloj.
    /// Ejecutar con "Backrooms/Watch/Aplicar malla Meshy al reloj". Repetible: mismo GUID de
    /// malla/material en cada pasada, así que el prefab no se rompe por re-ejecutar.
    ///
    /// CALCO DELIBERADO de <see cref="BackroomsSprayModelSwapper"/>, que ya pagó las tres trampas
    /// de este camino: MeshyImports está gitignored (referenciar el FBX deja el objeto invisible
    /// en cualquier otra máquina → se hornea copia a Assets/Art); la compresión de malla cuantiza
    /// contra la caja del IMPORT y este horneado reescribe vértices después (→ compresión Off
    /// antes de leer); y `SaveAssets` deja el .asset bien en disco mientras la malla cargada se
    /// dibuja con búferes viejos (→ ForceUpdate tras guardar).
    ///
    /// EL CANVAS SE RE-ANCLA AL NODO DE LA MALLA en la misma pasada. Hasta ahora colgaba de
    /// Hand.L con un offset a ciegas — tres capturas seguidas de playtest salieron sin esfera
    /// visible porque hueso y offset no se ven en el editor. Anclado al reloj físico, colocar la
    /// malla con el gizmo coloca la esfera con ella: un solo objeto que mover, cero
    /// "no se adhiere".
    /// </summary>
    public static class BackroomsWatchMeshApplier
    {
        private const string ImportFolder =
            "Assets/MeshyImports/tactical-watch-optimized-v2_20260815_115032";
        private const string FbxPath =
            ImportFolder + "/Meshy_AI_tactical_watch_optimi_0815095021_texture.fbx";
        private const string BaseColorPath = ImportFolder + "/meshy_basecolor.png";
        private const string MetallicPath = ImportFolder + "/meshy_metallic_smoothness.png";

        private const string BakedFolder = "Assets/Art/Watch";
        private const string BakedMeshPath = BakedFolder + "/BR_WristWatch_Mesh.asset";
        private const string BakedBaseColorPath = BakedFolder + "/BR_WristWatch_BaseColor.png";
        private const string BakedMetallicPath = BakedFolder + "/BR_WristWatch_Metallic.png";
        private const string MaterialPath = BakedFolder + "/BR_WristWatch.mat";

        private const string PrefabPath = "Assets/Resources/Wieldables/BR_Wieldable_Watch.prefab";
        private const string WristBoneName = "Hand.L";
        private const string MeshNodeName = "WatchMesh";

        /// <summary>Lado mayor del reloj horneado, en metros. Un reloj de muñeca grande.</summary>
        private const float WatchWidestMeters = 0.055f;

        private const int MaxTextureSize = 1024;
        private const int TriangleWarnThreshold = 30000;

        /// <summary>Capa ViewModel: la única que dibuja la cámara de viewmodel.</summary>
        private const int ViewModelLayer = 10;

        /// <summary>
        /// Clona los materiales de los brazos con el warp de FOV APAGADO y los asigna en el
        /// prefab. Sin esto, el modo prefab es ineditable: LitFieldOfView_SSS re-proyecta los
        /// vértices según la cámara, así que al orbitar la vista el DIBUJO del brazo se desplaza
        /// de donde el brazo está — y el reloj (sin warp) parece "moverse" respecto a él, cuando
        /// es el único que se dibuja donde de verdad está. Con clones sin warp, el modo prefab
        /// dice la verdad y colocar el reloj con el gizmo vuelve a ser posible.
        ///
        /// Clones PROPIOS en Assets/Art/Watch, jamás el material compartido: FP_Arm lo usan los
        /// brazos de todos los wieldables del vendor, que sí dependen del warp en runtime.
        /// De regalo, el apagado por instancia que hace el overlay en runtime queda redundante
        /// para los brazos (sigue siendo necesario para nada — se conserva por inocuo).
        /// </summary>
        [MenuItem("Backrooms/Watch/Brazos sin warp de FOV (arregla el modo prefab)", false, 99)]
        public static void DisarmArmFovWarp()
        {
            BackroomsEditorFolders.EnsureFolder("Assets/Art");
            BackroomsEditorFolders.EnsureFolder(BakedFolder);

            var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                int swapped = 0;
                foreach (var r in contents.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.name != "LeftArm" && r.name != "RightArm")
                    {
                        Debug.Log($"[WatchMesh] (saltado) renderer '{r.name}'");
                        continue;
                    }

                    var mats = r.sharedMaterials;
                    Debug.Log($"[WatchMesh] '{r.name}' tiene {mats.Length} material(es).");

                    for (int i = 0; i < mats.Length; i++)
                    {
                        var src = mats[i];
                        if (src == null)
                            continue;

                        // SIN filtrar por HasProperty: en batchmode el shadergraph no resuelve su
                        // lista de propiedades y el filtro descartaba los dos materiales buenos
                        // (primera pasada: "0 clonados" con todo correcto en disco). SetFloat de
                        // una propiedad inexistente es un no-op inofensivo, así que el filtro solo
                        // podía hacer daño.
                        Debug.Log($"[WatchMesh]   material '{src.name}' shader '{src.shader?.name}'");

                        string clonePath = $"{BakedFolder}/BR_Watch_{src.name}_NoWarp.mat";
                        var clone = AssetDatabase.LoadAssetAtPath<Material>(clonePath);
                        if (clone == null)
                        {
                            clone = new Material(src);
                            AssetDatabase.CreateAsset(clone, clonePath);
                        }
                        else
                        {
                            clone.CopyPropertiesFromMaterial(src);
                        }

                        clone.SetFloat("_FOV_Enabled", 0f);
                        EditorUtility.SetDirty(clone);
                        mats[i] = clone;
                        swapped++;
                    }
                    r.sharedMaterials = mats;
                }

                AssetDatabase.SaveAssets();
                PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath, out bool ok);
                Debug.Log(ok
                    ? $"[WatchMesh] {swapped} material(es) de brazo clonados sin warp. El modo " +
                      "prefab ya dibuja el brazo donde está."
                    : "[WatchMesh] FALLO guardando el prefab.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [MenuItem("Backrooms/Watch/Aplicar malla Meshy al reloj", false, 98)]
        public static void Apply()
        {
            if (!File.Exists(FbxPath))
            {
                Debug.LogError($"[WatchMesh] No hay FBX en '{FbxPath}'. Nada tocado.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Art");
            BackroomsEditorFolders.EnsureFolder(BakedFolder);

            ConfigureModel();

            var source = LoadFirstMesh();
            if (source == null)
            {
                Debug.LogError($"[WatchMesh] '{FbxPath}' no trae ninguna malla. Nada tocado.");
                return;
            }

            if (source.triangles.Length / 3 > TriangleWarnThreshold)
                Debug.LogWarning($"[WatchMesh] {source.triangles.Length / 3} triángulos para un " +
                                 "reloj de muñeca — Meshy se ha pasado; considera re-exportar más bajo.");

            var mesh = BakeMesh(source);
            if (mesh == null) return;

            BakeTexture(BaseColorPath, BakedBaseColorPath, isNormal: false, sRgb: true);
            BakeTexture(MetallicPath, BakedMetallicPath, isNormal: false, sRgb: false);

            var material = BuildMaterial();
            if (material == null) return;

            AttachToPrefab(mesh, material);
        }

        /// <summary>
        /// Canónica de reloj: escala UNIFORME al tamaño real y centrado en su caja. A diferencia
        /// del bote de spray, aquí NO se adivina orientación por ejes — la lata necesitaba quedar
        /// "de pie" porque también vive en el suelo como pickup; el reloj solo existe atado a la
        /// muñeca y su orientación fina se da con el gizmo en el prefab, donde se VE. Adivinarla
        /// aquí sería repetir la ronda de "el panel flotaba a metro y medio" con otra pieza.
        /// </summary>
        private static void MakeCanonical(Mesh mesh)
        {
            if (mesh.blendShapeCount > 0)
            {
                Debug.LogError($"[WatchMesh] La malla trae {mesh.blendShapeCount} blendshape(s) y este " +
                               "horneado no transforma sus deltas. Nada tocado.");
                return;
            }

            var size = mesh.bounds.size;
            float widest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            float s = widest > 1e-6f ? WatchWidestMeters / widest : 1f;

            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] *= s;
            mesh.vertices = vertices;

            // Escala uniforme: las normales no se tuercen y las tangentes conservan sentido; solo
            // hay que dejar que Unity recalcule la caja y centrar.
            mesh.RecalculateBounds();

            var centre = mesh.bounds.center;
            if (centre.sqrMagnitude > 1e-10f)
            {
                vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; i++) vertices[i] -= centre;
                mesh.vertices = vertices;
                mesh.RecalculateBounds();
            }

            var final = mesh.bounds.size;
            if (Mathf.Max(final.x, Mathf.Max(final.y, final.z)) < 0.02f ||
                Mathf.Max(final.x, Mathf.Max(final.y, final.z)) > 0.15f)
            {
                Debug.LogError($"[WatchMesh] El reloj horneado mide {final.x:F3} x {final.y:F3} x " +
                               $"{final.z:F3} m — fuera del rango sano (0,02–0,15). Revisa la escala.");
            }

            Debug.Log($"[WatchMesh] Malla canónica: {final.x:F3} x {final.y:F3} x {final.z:F3} m.");
        }

        private static Mesh BakeMesh(Mesh source)
        {
            var copy = Object.Instantiate(source);
            copy.name = "BR_WristWatch_Mesh";
            MakeCanonical(copy);

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(BakedMeshPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(copy, BakedMeshPath);
                Debug.Log($"[WatchMesh] Malla horneada nueva en '{BakedMeshPath}'.");
                return copy;
            }

            // CopySerialized y no borrar+crear: borrar cambia el GUID y rompería la referencia del
            // prefab en cada re-ejecución.
            EditorUtility.CopySerialized(copy, existing);
            Object.DestroyImmediate(copy);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();

            // ForceUpdate o la malla YA cargada sigue dibujando búferes viejos: fichero bien,
            // pantalla mal, cero errores (trampa documentada del horneado del spray).
            AssetDatabase.ImportAsset(BakedMeshPath, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<Mesh>(BakedMeshPath);
        }

        private static void BakeTexture(string sourcePath, string bakedPath, bool isNormal, bool sRgb)
        {
            var importer = AssetImporter.GetAtPath(sourcePath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[WatchMesh] Sin textura en '{sourcePath}' — se hornea sin ella.");
                return;
            }

            var previousType = importer.textureType;
            var previousCompression = importer.textureCompression;
            bool previousReadable = importer.isReadable;
            bool previousSrgb = importer.sRGBTexture;
            int previousMax = importer.maxTextureSize;

            try
            {
                importer.textureType = TextureImporterType.Default;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.isReadable = true;
                importer.sRGBTexture = sRgb;
                importer.maxTextureSize = MaxTextureSize;
                importer.SaveAndReimport();

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath);
                if (tex == null)
                {
                    Debug.LogWarning($"[WatchMesh] '{sourcePath}' no cargó como Texture2D.");
                    return;
                }

                File.WriteAllBytes(bakedPath, tex.EncodeToPNG());
                AssetDatabase.ImportAsset(bakedPath, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                importer.textureType = previousType;
                importer.textureCompression = previousCompression;
                importer.isReadable = previousReadable;
                importer.sRGBTexture = previousSrgb;
                importer.maxTextureSize = previousMax;
                importer.SaveAndReimport();
            }

            var baked = AssetImporter.GetAtPath(bakedPath) as TextureImporter;
            if (baked == null) return;

            baked.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            baked.sRGBTexture = sRgb;
            baked.maxTextureSize = MaxTextureSize;
            baked.textureCompression = TextureImporterCompression.Compressed;
            baked.mipmapEnabled = true;
            baked.SaveAndReimport();
        }

        private static void ConfigureModel()
        {
            var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (importer == null) return;

            bool dirty = false;
            if (importer.materialImportMode != ModelImporterMaterialImportMode.None)
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                dirty = true;
            }
            if (importer.importAnimation) { importer.importAnimation = false; dirty = true; }
            if (importer.importCameras) { importer.importCameras = false; dirty = true; }
            if (importer.importLights) { importer.importLights = false; dirty = true; }
            if (!importer.isReadable) { importer.isReadable = true; dirty = true; }
            if (importer.meshCompression != ModelImporterMeshCompression.Off)
            {
                importer.meshCompression = ModelImporterMeshCompression.Off;
                dirty = true;
            }

            if (!dirty) return;
            importer.SaveAndReimport();
        }

        private static Mesh LoadFirstMesh()
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(FbxPath))
                if (asset is Mesh m) return m;
            return null;
        }

        private static Material BuildMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[WatchMesh] Sin shader 'Universal Render Pipeline/Lit'. Nada tocado.");
                return null;
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            mat.shader = shader;

            var baseColor = AssetDatabase.LoadAssetAtPath<Texture2D>(BakedBaseColorPath);
            var metallic = AssetDatabase.LoadAssetAtPath<Texture2D>(BakedMetallicPath);

            if (baseColor != null) mat.SetTexture("_BaseMap", baseColor);
            if (metallic != null)
            {
                mat.SetTexture("_MetallicGlossMap", metallic);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                mat.SetFloat("_Metallic", 1f);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetFloat("_SmoothnessTextureChannel", 0f);
            }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        /// <summary>
        /// Monta el nodo <c>WatchMesh</c> bajo la muñeca y re-ancla el canvas de la esfera a ese
        /// mismo nodo, todo en una pasada sobre el prefab. El material URP Lit estándar se dibuja
        /// SIN el warp de FOV — igual que el brazo con el warp apagado por el overlay — así que
        /// malla, esfera y brazo comparten proyección y no pueden divergir entre sí.
        /// </summary>
        private static void AttachToPrefab(Mesh mesh, Material material)
        {
            var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Transform wrist = null;
                foreach (var t in contents.GetComponentsInChildren<Transform>(true))
                    if (t.name == WristBoneName) { wrist = t; break; }

                if (wrist == null)
                {
                    Debug.LogError($"[WatchMesh] No hay hueso '{WristBoneName}' en el prefab. Nada tocado.");
                    return;
                }

                // Se busca en TODO el prefab, no solo bajo la muñeca, y se REPARENTA si aparece en
                // otro sitio. Motivo medido (2026-08-16): el nodo acabó colgando de `ViewModel` —
                // la raíz del wieldable, que NO se mueve con la animación de los huesos. Síntoma:
                // "el brazo se mueve pero el reloj no sigue", que parecía divergencia de skinning
                // y era simplemente que estaban en ramas distintas del árbol. Con `wrist.Find` a
                // secas, re-ejecutar el menú creaba un SEGUNDO WatchMesh y dejaba el huérfano
                // dibujándose quieto en mitad de la pantalla.
                Transform node = null;
                foreach (var t in contents.GetComponentsInChildren<Transform>(true))
                    if (t.name == MeshNodeName) { node = t; break; }

                if (node == null)
                {
                    var go = new GameObject(MeshNodeName);
                    node = go.transform;
                    node.SetParent(wrist, false);
                    // Arranque en el dorso aproximado de la muñeca; el fino es gizmo en el prefab.
                    node.localPosition = new Vector3(-0.05f, 0.02f, 0f);
                    node.localRotation = Quaternion.identity;
                }
                else if (node.parent != wrist)
                {
                    // worldPositionStays:false — se conserva la colocación LOCAL respecto al nuevo
                    // padre. Con true, Unity recalcularía para mantener la posición en el mundo del
                    // prefab, que es un espacio sin sentido aquí (el rig está en pose de reposo).
                    var localPos = node.localPosition;
                    var localRot = node.localRotation;
                    node.SetParent(wrist, false);
                    node.localPosition = localPos;
                    node.localRotation = localRot;
                    Debug.Log($"[WatchMesh] '{MeshNodeName}' colgaba de '{node.parent.name}' y se " +
                              $"ha reparentado a '{WristBoneName}'.");
                }

                node.gameObject.layer = ViewModelLayer;

                var filter = node.GetComponent<MeshFilter>();
                if (filter == null) filter = node.gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;

                var renderer = node.GetComponent<MeshRenderer>();
                if (renderer == null) renderer = node.gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                // El canvas pasa a colgar del reloj físico: mover la malla mueve la esfera.
                var display = contents.GetComponentInChildren<WristWatchDisplay>(true);
                if (display != null)
                {
                    display.anchorBoneName = MeshNodeName;
                    display.localOffset = new Vector3(0f, 0.012f, 0f);
                    display.localEuler = new Vector3(90f, 180f, 0f);
                    display.faceSizeMeters = new Vector2(0.036f, 0.042f);
                }
                else
                {
                    Debug.LogWarning("[WatchMesh] El prefab no lleva WristWatchDisplay; " +
                                     "la esfera no se re-ancló.");
                }

                PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath, out bool ok);
                Debug.Log(ok
                    ? $"[WatchMesh] Montado '{MeshNodeName}' bajo {WristBoneName} y esfera re-anclada. " +
                      "Abre el prefab y coloca WatchMesh con el gizmo: la esfera viaja con él."
                    : "[WatchMesh] FALLO guardando el prefab.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
    }
}
#endif
