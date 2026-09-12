#if UNITY_EDITOR
using System.IO;
using PolymindGames.InventorySystem;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Pone el modelo de verdad en la mano del destornillador: el generado en Meshy, en vez del
    /// hacha que heredó por ser un clon de <c>STP_Wieldable_HuntingAxe</c> (ADR-114, arte prestado
    /// declarado). Se ejecuta desde "Backrooms ▸ Screwdriver ▸ Aplicar modelo Meshy".
    ///
    /// CALCADO de <see cref="BackroomsSprayModelSwapper"/>, que es el precedente del proyecto para
    /// un objeto de mano venido de Meshy, con las mismas cuatro reglas que allí costaron sesiones:
    ///
    /// 1. Cuelga del HUESO <c>Hand.R</c>, no del raíz: la malla del hacha es skinned al esqueleto
    ///    de los brazos; una malla estática colgada del raíz se queda clavada en el aire.
    /// 2. La escala se CALCULA sobre los vértices (malla canónica en metros, punta a +Y, centrada)
    ///    y no en un Transform: el prefab del suelo tiene que quedarse a escala 1 en el root
    ///    porque el resaltado del vendor (<c>MaterialEffect</c>) es en espacio de objeto.
    /// 3. Las texturas se hornean a 1024 y el FBX no se toca.
    /// 4. Lo horneado va a <see cref="BakedFolder"/>, versionado: `Assets/MeshyImports/` está en
    ///    .gitignore y apuntar el prefab al import deja el objeto invisible en otra máquina.
    ///
    /// Y una diferencia con la lata, que es la única: la escala es UNIFORME (un destornillador no
    /// viene achaparrado, y deformarlo por ejes lo haría parecer otro objeto), y la PUNTA se
    /// detecta por geometría —el extremo más fino es el vástago— en vez de leerse de un render.
    ///
    /// Encadena el pickup del suelo y deja renderizados los dos fotogramas del icono (fondo negro
    /// y fondo blanco) para que el script de imagen derive el alfa por diferencia: el icono se
    /// genera del MODELO, con el mismo tratamiento que los dos anteriores (recorte al contenido,
    /// 25° de giro, 94 % del lienzo).
    ///
    /// Reejecutable: borra su propio nodo antes de rehacerlo.
    /// </summary>
    public static class BackroomsScrewdriverModelApplier
    {
        public const string WieldablePrefabPath = "Assets/Prefabs/Wieldables/BR_Wieldable_Screwdriver.prefab";
        public const string DefinitionPath = "Assets/Resources/Definitions/Item/BR_Screwdriver.asset";

        private const string ModelFolder = "Assets/MeshyImports/Screwdriver";
        private const string FbxPath = ModelFolder + "/Screwdriver.fbx";
        private const string BaseColorPath = ModelFolder + "/meshy_basecolor.png";
        private const string NormalPath = ModelFolder + "/meshy_normal.png";
        private const string MetallicPath = ModelFolder + "/meshy_metallic_smoothness.png";

        public const string BakedFolder = "Assets/Art/Items/Screwdriver";
        public const string BakedMeshPath = BakedFolder + "/BR_Screwdriver_Mesh.asset";
        public const string BakedBaseColorPath = BakedFolder + "/BR_Screwdriver_BaseColor.png";
        public const string BakedNormalPath = BakedFolder + "/BR_Screwdriver_Normal.png";
        public const string BakedMetallicPath = BakedFolder + "/BR_Screwdriver_Metallic.png";
        public const string MaterialPath = BakedFolder + "/BR_Screwdriver_Mat.mat";
        /// <summary>El material de la MANO (ADR-077 enm. 2): mismo arte, shader de warp del viewmodel.
        /// <see cref="MaterialPath"/> es el del mundo (pickup, icono, proxy).</summary>
        public const string FirstPersonMaterialPath = BakedFolder + "/BR_Screwdriver_FP_Mat.mat";
        public const string MaskMapPath = BakedFolder + "/BR_Screwdriver_MaskMap.png";
        public const string MeshName = "BR_Screwdriver_Mesh";

        /// <summary>Nombre del nodo que crea este script, bajo <see cref="HandBoneName"/>.</summary>
        public const string NodeName = "BR_ScrewdriverModel";
        public const string HandBoneName = "Hand.R";
        /// <summary>El nodo de la malla prestada del hacha, que se apaga.</summary>
        public const string DonorMeshNode = "Axe";

        /// <summary>Largo real de un destornillador de electricista. La escala del import sale de aquí.</summary>
        private const float LengthMeters = 0.24f;

        /// <summary>Icono: dos renders del modelo horneado, fondo negro y blanco, para derivar el alfa.</summary>
        public const string IconPath = "Assets/Art/Items/BR_Screwdriver_Icon.png";
        private const string IconRawFolder =
            @"C:\Users\JOELV\AppData\Local\Temp\claude\J--Unity-BackroomsSurvivalMMO\bbdd555e-216a-4514-96e0-97106baf4326\scratchpad";
        private const int IconRawSize = 1024;

        /// <summary>Retoque fino SOBRE el agarre calculado, en espacio del hueso.</summary>
        private static readonly Vector3 GripNudge = Vector3.zero;
        private static readonly Vector3 EulerNudge = Vector3.zero;
        private static readonly Vector3 FallbackPosition = new Vector3(0.02f, 0.03f, 0.01f);
        private static readonly Vector3 FallbackEuler = new Vector3(0f, 0f, 90f);

        /// <summary>
        /// Cuánto sube el CENTRO del destornillador por encima del puño, en fracción del largo. Se
        /// agarra por el mango, que es el quinto inferior: el centro (mitad del largo) queda unos
        /// 0,3 L hacia la punta.
        /// </summary>
        private const float GripRiseFraction = 0.30f;

        private const int MaxTextureSize = 1024;
        private const int TriangleWarnThreshold = 30000;

        [MenuItem("Backrooms/Screwdriver/Aplicar modelo Meshy", false, 90)]
        public static void Apply()
        {
            if (!File.Exists(FbxPath))
            {
                Debug.LogError($"[ScrewdriverModel] No hay FBX en '{FbxPath}'. Nada tocado.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Art");
            BackroomsEditorFolders.EnsureFolder("Assets/Art/Items");
            BackroomsEditorFolders.EnsureFolder(BakedFolder);

            ConfigureModel();

            var source = LoadFirstMesh();
            if (source == null)
            {
                Debug.LogError($"[ScrewdriverModel] '{FbxPath}' no trae ninguna malla. Nada tocado.");
                return;
            }

            var mesh = BakeMesh(source);
            if (mesh == null) return;

            BakeTexture(BaseColorPath, BakedBaseColorPath, isNormal: false, sRgb: true);
            BakeTexture(NormalPath, BakedNormalPath, isNormal: true, sRgb: false);
            BakeTexture(MetallicPath, BakedMetallicPath, isNormal: false, sRgb: false);

            var material = BuildMaterial();
            if (material == null) return;

            // En la mano va el material de PRIMERA PERSONA: el de mundo con URP/Lit se dibujaría con
            // la proyección de la cámara y no con la del viewmodel (ADR-077 enm. 2).
            var firstPerson = BackroomsViewmodelMaterials.BuildFirstPerson(
                MaterialPath, FirstPersonMaterialPath, MaskMapPath, "[ScrewdriverModel]");
            if (firstPerson == null) return;

            AttachToPrefab(mesh, firstPerson);

            // El mismo arte al objeto del SUELO, encadenado a propósito (ver el bote).
            BackroomsScrewdriverPickupCreator.Apply();

            RenderIconFrames(mesh, material);
        }

        /// <summary>
        /// Malla CANÓNICA: escala uniforme a <see cref="LengthMeters"/>, eje largo a +Y, punta a
        /// +Y, centrada en su caja. Sobre los vértices, no sobre un Transform (ver la clase).
        /// </summary>
        private static void MakeCanonical(Mesh mesh)
        {
            if (mesh.blendShapeCount > 0)
            {
                Debug.LogError($"[ScrewdriverModel] La malla trae {mesh.blendShapeCount} blendshape(s) y este " +
                               "horneado no transforma sus deltas. Nada tocado.");
                return;
            }

            var size = mesh.bounds.size;
            int longAxis = size.x >= size.y && size.x >= size.z ? 0 : (size.y >= size.z ? 1 : 2);
            Vector3 from = longAxis == 0 ? Vector3.right : longAxis == 1 ? Vector3.up : Vector3.forward;
            var rotation = Quaternion.FromToRotation(from, Vector3.up);

            float longest = size[longAxis];
            float s = longest > 1e-6f ? LengthMeters / longest : 1f;

            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = rotation * vertices[i] * s;
            mesh.vertices = vertices;
            mesh.RecalculateBounds();

            // Centrada en su caja.
            var centre = mesh.bounds.center;
            if (centre.sqrMagnitude > 1e-10f)
            {
                for (int i = 0; i < vertices.Length; i++) vertices[i] -= centre;
                mesh.vertices = vertices;
                mesh.RecalculateBounds();
            }

            // LA PUNTA POR GEOMETRÍA: el extremo más fino es el vástago. Se compara el grosor
            // medio (distancia al eje Y) de los vértices del 15 % superior contra el 15 % inferior.
            float half = mesh.bounds.extents.y;
            float band = half * 0.30f;
            float topGirth = 0f, botGirth = 0f;
            int topN = 0, botN = 0;
            foreach (var v in vertices)
            {
                float r = new Vector2(v.x, v.z).magnitude;
                if (v.y > half - band) { topGirth += r; topN++; }
                else if (v.y < -half + band) { botGirth += r; botN++; }
            }
            topGirth = topN > 0 ? topGirth / topN : 0f;
            botGirth = botN > 0 ? botGirth / botN : 0f;
            bool tipIsUp = topGirth <= botGirth;
            if (!tipIsUp)
            {
                // Media vuelta sobre Z: la punta pasa a +Y. Rotación pura, las normales van igual.
                var flip = Quaternion.Euler(0f, 0f, 180f);
                for (int i = 0; i < vertices.Length; i++) vertices[i] = flip * vertices[i];
                mesh.vertices = vertices;
                rotation = flip * rotation;
                mesh.RecalculateBounds();
            }

            // Normales y tangentes: rotación pura + escala uniforme, así que sólo se rotan.
            var normals = mesh.normals;
            if (normals != null && normals.Length == vertices.Length)
            {
                for (int i = 0; i < normals.Length; i++) normals[i] = (rotation * normals[i]).normalized;
                mesh.normals = normals;
            }
            var tangents = mesh.tangents;
            if (tangents != null && tangents.Length == vertices.Length)
            {
                for (int i = 0; i < tangents.Length; i++)
                {
                    var t = tangents[i];
                    var xyz = (rotation * new Vector3(t.x, t.y, t.z)).normalized;
                    tangents[i] = new Vector4(xyz.x, xyz.y, xyz.z, t.w);
                }
                mesh.tangents = tangents;
            }

            float widest = Mathf.Max(mesh.bounds.size.x, Mathf.Max(mesh.bounds.size.y, mesh.bounds.size.z));
            if (widest < 0.10f || widest > 0.50f)
            {
                Debug.LogError($"[ScrewdriverModel] La malla canónica mide {widest:F3} m de lado mayor, fuera " +
                               "del rango sano de un destornillador (0,10–0,50). Revisa la transformación.");
            }

            Debug.Log($"[ScrewdriverModel] Malla canónica: caja {mesh.bounds.size.x:F3} x " +
                      $"{mesh.bounds.size.y:F3} x {mesh.bounds.size.z:F3} m; grosor arriba {topGirth:F4} / " +
                      $"abajo {botGirth:F4} → punta {(tipIsUp ? "ya estaba" : "volteada")} a +Y.");
        }

        private static Mesh BakeMesh(Mesh source)
        {
            var copy = Object.Instantiate(source);
            copy.name = MeshName;
            MakeCanonical(copy);

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(BakedMeshPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(copy, BakedMeshPath);
                Debug.Log($"[ScrewdriverModel] Malla horneada nueva en '{BakedMeshPath}'.");
                return copy;
            }

            // Sobrescribir, NUNCA borrar y recrear: cambia el GUID y rompe la referencia del prefab.
            EditorUtility.CopySerialized(copy, existing);
            Object.DestroyImmediate(copy);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            // Reimportar a la fuerza: la malla ya cargada sigue dibujándose con búferes viejos.
            AssetDatabase.ImportAsset(BakedMeshPath, ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<Mesh>(BakedMeshPath);
            Debug.Log($"[ScrewdriverModel] Malla horneada actualizada en '{BakedMeshPath}' (mismo GUID).");
            return reloaded;
        }

        private static void BakeTexture(string sourcePath, string bakedPath, bool isNormal, bool sRgb)
        {
            var importer = AssetImporter.GetAtPath(sourcePath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[ScrewdriverModel] Sin textura en '{sourcePath}' — se hornea sin ella.");
                return;
            }

            var previousType = importer.textureType;
            var previousCompression = importer.textureCompression;
            bool previousReadable = importer.isReadable;
            bool previousSrgb = importer.sRGBTexture;
            int previousMax = importer.maxTextureSize;

            try
            {
                // En CRUDO: leer píxeles de una textura comprimida o ya marcada como normal
                // devuelve los canales barajados.
                importer.textureType = TextureImporterType.Default;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.isReadable = true;
                importer.sRGBTexture = sRgb;
                importer.maxTextureSize = MaxTextureSize;
                importer.SaveAndReimport();

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath);
                if (tex == null)
                {
                    Debug.LogWarning($"[ScrewdriverModel] '{sourcePath}' no cargó como Texture2D.");
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

            long kb = new FileInfo(bakedPath).Length / 1024;
            Debug.Log($"[ScrewdriverModel] '{Path.GetFileName(bakedPath)}' horneada a {MaxTextureSize}px " +
                      $"({kb} KB, normal={isNormal}, sRGB={sRgb}).");
        }

        /// <summary>Sin materiales del FBX (Built-in = magenta en URP), sin animación, sin cámaras
        /// ni luces; legible y SIN comprimir porque la malla se reescribe después.</summary>
        private static void ConfigureModel()
        {
            var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[ScrewdriverModel] '{FbxPath}' no tiene ModelImporter.");
                return;
            }

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
            if (!importer.optimizeMeshPolygons) { importer.optimizeMeshPolygons = true; dirty = true; }
            if (!importer.optimizeMeshVertices) { importer.optimizeMeshVertices = true; dirty = true; }

            if (!dirty) return;
            importer.SaveAndReimport();
            Debug.Log("[ScrewdriverModel] FBX reimportado sin materiales, sin animación y legible.");
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
                Debug.LogError("[ScrewdriverModel] Sin shader 'Universal Render Pipeline/Lit'. Nada tocado.");
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
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(BakedNormalPath);
            var metallic = AssetDatabase.LoadAssetAtPath<Texture2D>(BakedMetallicPath);

            if (baseColor != null) mat.SetTexture("_BaseMap", baseColor);
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
            }
            if (metallic != null)
            {
                mat.SetTexture("_MetallicGlossMap", metallic);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                mat.SetFloat("_Metallic", 1f);
                mat.SetFloat("_Smoothness", 1f);
                mat.SetFloat("_SmoothnessTextureChannel", 0f); // alfa del mapa metálico
            }

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        /// <summary>
        /// El agarre desde los NUDILLOS, calcado del bote: un puño cerrado sobre un mango lo cruza
        /// por la palma, el eje entra por el meñique y sale por el índice, y la punta sale por el
        /// lado del índice. El centro del objeto se pone en el puño y sube <see cref="GripRiseFraction"/>.
        /// </summary>
        private static bool TryGripFromKnuckles(Transform hand, Transform index, Transform pinky,
            Transform middle, out Vector3 localPos, out Quaternion localRot)
        {
            localPos = Vector3.zero;
            localRot = Quaternion.identity;
            if (hand == null || index == null || pinky == null || middle == null) return false;

            Vector3 tipDir = (index.position - pinky.position).normalized;
            if (tipDir.sqrMagnitude < 1e-8f) return false;

            Vector3 fist = (hand.position + middle.position) * 0.5f;
            Vector3 centre = fist + tipDir * (LengthMeters * GripRiseFraction);

            localPos = hand.InverseTransformPoint(centre);
            // La punta está a +Y en la malla canónica, por construcción.
            localRot = Quaternion.FromToRotation(Vector3.up, hand.InverseTransformDirection(tipDir));
            return true;
        }

        private static void AttachToPrefab(Mesh mesh, Material material)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(WieldablePrefabPath) == null)
            {
                Debug.LogError($"[ScrewdriverModel] No hay prefab en '{WieldablePrefabPath}'. " +
                               "Ejecuta antes 'Backrooms/Create Dismantle Assets'.");
                return;
            }

            var root = PrefabUtility.LoadPrefabContents(WieldablePrefabPath);
            try
            {
                Transform hand = null, donor = null, index = null, pinky = null, middle = null;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (hand == null && t.name == HandBoneName) hand = t;
                    if (donor == null && t.name == DonorMeshNode) donor = t;
                    if (index == null && t.name == "Index.1.R") index = t;
                    if (pinky == null && t.name == "Pinky.1.R") pinky = t;
                    if (middle == null && t.name == "Middle.1.R") middle = t;
                }

                if (hand == null)
                {
                    Debug.LogError($"[ScrewdriverModel] No aparece el hueso '{HandBoneName}'. Nada tocado.");
                    return;
                }

                // El hacha, fuera de la vista. Se DESACTIVA, no se borra: reversible.
                if (donor != null && donor.gameObject.activeSelf) donor.gameObject.SetActive(false);

                // Por todo el prefab y no sólo bajo la mano: destruye el nodo dondequiera que haya
                // quedado colgado (una pasada vieja pudo parentarlo al hueso del hacha en vez de a
                // Hand.R).
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || t.name != NodeName) continue;
                    Object.DestroyImmediate(t.gameObject);
                    break;
                }

                // EL HORNEADOR DE POSE MANDA. Si ya hay clips horneados para este objeto, la
                // posición del nodo la fija BackroomsToolPoseBaker y aquí NO se toca: había tres
                // sitios escribiendo la misma localPosition y ganaba el último que corriese, con
                // 60 mm de desvío medido entre lo que el solver resolvía y lo que quedaba guardado.
                bool posedByBaker = AssetDatabase.LoadAssetAtPath<AnimationClip>(BackroomsToolPoseSpecs.ScrewdriverIdleClip) != null;

                var go = new GameObject(NodeName);
                Transform parentBone = hand;

                // EL AGARRE SE LEE DEL HACHA, no se calcula: su malla es skinned y el bindpose dice
                // exactamente cómo la mano del vendor cierra el puño sobre un mango — el mismo
                // agarre que la animación de equipar reproduce fotograma a fotograma. El cálculo por
                // nudillos (abajo) es sólo la reserva: acertaba en pose de bind y salía desplazado en
                // cuanto entraba la animación real (ADR-077 enm. 3, mismo síntoma que ya documentó
                // la linterna).
                if (BackroomsDonorGrip.TryReadHandle(root, DonorMeshNode, null,
                        out var dominantBone, out var axis, out var lateral, "[ScrewdriverModel]"))
                {
                    parentBone = dominantBone;
                    // Defensivo: un hueso de donante apagado deja a su hijo invisible sin error
                    // (mordió con el bote de spray, cuyo hueso 'Torch' venía apagado por un filtro
                    // viejo — ver ADR-077 enm. 3).
                    if (!parentBone.gameObject.activeSelf) parentBone.gameObject.SetActive(true);
                    go.transform.SetParent(parentBone, false);
                    go.transform.localPosition = lateral + axis * (LengthMeters * GripRiseFraction) + GripNudge;
                    go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis) * Quaternion.Euler(EulerNudge);
                }
                else
                {
                    go.transform.SetParent(parentBone, false);
                    if (TryGripFromKnuckles(hand, index, pinky, middle, out var gripPos, out var gripRot))
                    {
                        go.transform.localPosition = gripPos + GripNudge;
                        go.transform.localRotation = gripRot * Quaternion.Euler(EulerNudge);
                    }
                    else
                    {
                        Debug.LogWarning("[ScrewdriverModel] Faltan huesos de dedos para el agarre: pose de reserva.");
                        go.transform.localPosition = FallbackPosition + GripNudge;
                        go.transform.localEulerAngles = FallbackEuler + EulerNudge;
                    }
                }

                // Si el horneador de pose ya mandó, se deshace lo que este método acaba de
                // escribir y se deja SU pose. Este script sigue creando el nodo, la malla y el
                // material; la colocación es suya sólo mientras no haya agarre horneado.
                if (posedByBaker)
                    Debug.Log("[ScrewdriverModel] Hay pose horneada: la colocación del nodo la manda " +
                              "BackroomsToolPoseBaker y aquí no se toca (escritor único, ADR-077 enm. 5).");

                var boneScale = parentBone.lossyScale;
                float boneFactor = Mathf.Max(1e-5f, Mathf.Max(boneScale.x, Mathf.Max(boneScale.y, boneScale.z)));
                go.transform.localScale = Vector3.one / boneFactor;

                // La capa del viewmodel manda: la cámara de primera persona filtra por capa.
                int layer = donor != null ? donor.gameObject.layer : hand.gameObject.layer;
                go.layer = layer;

                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                PrefabUtility.SaveAsPrefabAsset(root, WieldablePrefabPath);
                AssetDatabase.SaveAssets();

                long indices = 0;
                for (int s = 0; s < mesh.subMeshCount; s++) indices += (long)mesh.GetIndexCount(s);
                long tris = indices / 3;
                Debug.Log($"[ScrewdriverModel] Destornillador colgado de '{parentBone.name}': tris={tris}, " +
                          $"caja={mesh.bounds.size.x:F4}/{mesh.bounds.size.y:F4}/{mesh.bounds.size.z:F4}, " +
                          $"escala={go.transform.localScale}, capa={layer}, " +
                          $"pos={go.transform.localPosition}, euler={go.transform.localEulerAngles}.");
                if (tris > TriangleWarnThreshold)
                {
                    Debug.LogWarning($"[ScrewdriverModel] {tris} triángulos para un objeto de mano es MUCHO " +
                                     $"(referencia sana: <{TriangleWarnThreshold}).");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Dos fotogramas del modelo horneado, fondo negro y fondo blanco, en perfil (punta arriba)
        /// con una pizca de elevación. El alfa sale por diferencia entre los dos; el recorte, el giro
        /// de 25° y el 94 % del lienzo los hace el mismo tratamiento de imagen que los otros dos
        /// iconos. Aquí sólo se renderiza el modelo tal cual es.
        /// </summary>
        private static void RenderIconFrames(Mesh mesh, Material material)
        {
            var go = new GameObject("IconStage");
            go.hideFlags = HideFlags.HideAndDontSave;
            var pru = new PreviewRenderUtility();
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;

                foreach (var (bg, file) in new[]
                         {
                             (Color.black, "screwdriver_icon_black.png"),
                             (Color.white, "screwdriver_icon_white.png"),
                         })
                {
                    var rect = new Rect(0, 0, IconRawSize, IconRawSize);
                    pru.BeginStaticPreview(rect);
                    pru.AddSingleGO(go);

                    var b = mesh.bounds;
                    var cam = pru.camera;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = bg;
                    cam.orthographic = true;
                    cam.orthographicSize = b.extents.magnitude * 1.05f;
                    cam.nearClipPlane = 0.01f;
                    cam.farClipPlane = 10f;
                    var dir = new Vector3(0.25f, 0.18f, -1f).normalized;
                    cam.transform.position = b.center + dir * 2f;
                    cam.transform.LookAt(b.center, Vector3.up);

                    pru.lights[0].intensity = 1.2f;
                    pru.lights[0].transform.rotation = Quaternion.Euler(35f, 40f, 0f);
                    pru.lights[1].intensity = 0.6f;
                    pru.ambientColor = new Color(0.35f, 0.35f, 0.38f);

                    pru.Render(true, false);
                    var tex = pru.EndStaticPreview();
                    if (tex == null)
                    {
                        Debug.LogError("[ScrewdriverModel] El render del icono devolvió nulo.");
                        return;
                    }
                    string path = Path.Combine(IconRawFolder, file);
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);
                    Debug.Log($"[ScrewdriverModel] Fotograma del icono en '{path}'.");
                }
            }
            finally
            {
                pru.Cleanup();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Asigna el icono propio, importándolo como Sprite FullRect — el hueco del
        /// inventario es cuadrado y sin FullRect Unity recorta el margen transparente y lo estira.
        /// Misma rutina que el bote y el agua de almendras.</summary>
        /// <summary>
        /// Re-cuelga SOLO el agarre (destino del nodo bajo su hueso), reusando la malla y el
        /// material de primera persona YA horneados: no toca el FBX de Meshy (que ni siquiera hace
        /// falta que exista). Sirve para iterar el agarre (BackroomsDonorGrip) sin repetir el
        /// horneado de malla/texturas cada vez.
        /// </summary>
        [MenuItem("Backrooms/Screwdriver/Re-colgar agarre (sin rehornear)", false, 92)]
        public static void ReattachGrip()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(BakedMeshPath);
            if (mesh == null) { Debug.LogError($"[ScrewdriverModel] Sin malla horneada en '{BakedMeshPath}'."); return; }
            var firstPerson = BackroomsViewmodelMaterials.BuildFirstPerson(
                MaterialPath, FirstPersonMaterialPath, MaskMapPath, "[ScrewdriverModel]");
            if (firstPerson == null) return;
            AttachToPrefab(mesh, firstPerson);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [MenuItem("Backrooms/Screwdriver/Asignar icono", false, 91)]
        public static void AssignIconMenu()
        {
            var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(DefinitionPath);
            if (definition == null)
            {
                Debug.LogError($"[ScrewdriverModel] No hay definición en '{DefinitionPath}'.");
                return;
            }
            if (AssignIcon(definition))
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        public static bool AssignIcon(ItemDefinition definition)
        {
            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), IconPath)))
            {
                Debug.LogWarning($"[ScrewdriverModel] Sin icono en '{IconPath}' — se queda el prestado del hacha.");
                return false;
            }

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
                    importer.ReadTextureSettings(settings);
                    settings.spriteMeshType = SpriteMeshType.FullRect;
                    importer.SetTextureSettings(settings);
                    importer.SaveAndReimport();
                }
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(IconPath);
            if (sprite == null)
            {
                Debug.LogError($"[ScrewdriverModel] '{IconPath}' existe pero no da un Sprite.");
                return false;
            }

            var target = new SerializedObject(definition);
            var icon = target.FindProperty("_icon");
            if (icon == null) return false;
            if (icon.objectReferenceValue == sprite) return false;
            icon.objectReferenceValue = sprite;
            target.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            Debug.Log($"[ScrewdriverModel] Icono asignado desde '{IconPath}'.");
            return true;
        }
    }
}
#endif
