#if UNITY_EDITOR
using System.IO;
using BackroomsSurvival.Gameplay;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-133 bloque A — pone el modelo de verdad en la mano de la linterna: los dos FBX de Meshy,
    /// horneados y montados como UN objeto con la manivela como HIJO. Se ejecuta desde
    /// "Backrooms ▸ Linterna ▸ Aplicar modelo Meshy".
    ///
    /// "UN SOLO MODELO" NO ES UNA SOLA MALLA, y es la corrección que sostiene todo lo demás. Si el
    /// cuerpo y la manivela se funden en una malla, la manivela no puede girar: una malla estática
    /// se dibuja entera con la transformación de su nodo. Lo que hace falta es un nodo raíz con dos
    /// hijos —cuerpo y manivela— y el PIVOTE de la manivela en su eje de giro. Por eso los dos FBX
    /// se hornean por separado y se montan aquí.
    ///
    /// CALCADO de <see cref="BackroomsScrewdriverModelApplier"/>, que es el precedente del proyecto
    /// para un objeto de mano venido de Meshy, con sus cuatro reglas ya pagadas:
    ///
    /// 1. Cuelga del HUESO <c>Hand.R</c>, no del raíz: la malla de la antorcha es skinned al
    ///    esqueleto de los brazos y una malla estática colgada del raíz se queda clavada en el aire.
    /// 2. La escala se CALCULA sobre los vértices y no en un Transform: el resaltado del vendor
    ///    (<c>MaterialEffect</c>) es en espacio de objeto y quiere el root a escala 1.
    /// 3. Las texturas se hornean a 1024 y el FBX no se toca.
    /// 4. Lo horneado va a <see cref="BakedFolder"/>, versionado: <c>Assets/MeshyImports/</c> está
    ///    en .gitignore, y apuntar el prefab al import deja el objeto invisible en otra máquina.
    ///
    /// Y una quinta, propia: LA POSE SALE DE LA ANTORCHA. El nodo del tronco de antorcha bajo
    /// <c>Hand.R</c> ya está colocado en el puño y apuntando hacia delante-arriba, que es
    /// exactamente cómo se sostiene una linterna. Se copia su transformación local en vez de
    /// recalcular un agarre desde los nudillos: menos números que afinar a ciegas, y la lente cae
    /// donde estaba la llama.
    ///
    /// LO QUE NO SE PUEDE ADIVINAR y queda declarado: DÓNDE va la manivela sobre el cuerpo. Sin ver
    /// las dos piezas juntas no hay forma de saberlo, así que nace en el centro del cuerpo y
    /// desplazada a un lado, y se afina con <see cref="CrankOffset"/> tras una captura.
    /// </summary>
    public static class BackroomsCrankFlashlightModelApplier
    {
        public const string WieldablePrefabPath = BackroomsCrankFlashlightCreator.PrefabPath;

        /// <summary>Carpeta de imports. Los FBX se buscan por PISTA de nombre y no por ruta fija:
        /// Meshy escribe carpetas con marca de tiempo y renombrarlas a mano se olvida.</summary>
        private const string ImportRoot = "Assets/MeshyImports";

        /// <summary>
        /// Pistas del CUERPO. La lista es larga porque Meshy bautiza el modelo con lo que le sugiere
        /// su propio prompt y no con lo que la pieza es: el cuerpo de esta linterna llegó como
        /// "Rugged Beacon", que no contiene ni "flashlight" ni "linterna". Cuando no encaja nada se
        /// listan los candidatos en consola en vez de decir sólo que no hay: el fallo real es
        /// siempre "está, pero se llama de otra manera".
        /// </summary>
        private static readonly string[] BodyHints =
        {
            "flashlight", "linterna", "torchlight", "beacon", "lantern", "lamp", "dynamo",
        };

        private static readonly string[] CrankHints = { "crank", "manivela", "handle" };

        public const string BakedFolder = "Assets/Art/Items/CrankFlashlight";
        public const string BodyMeshPath = BakedFolder + "/BR_CrankFlashlight_Body_Mesh.asset";
        public const string CrankMeshPath = BakedFolder + "/BR_CrankFlashlight_Crank_Mesh.asset";
        public const string MaterialPath = BakedFolder + "/BR_CrankFlashlight_Mat.mat";
        private const string BakedBaseColorPath = BakedFolder + "/BR_CrankFlashlight_BaseColor.png";
        private const string BakedNormalPath = BakedFolder + "/BR_CrankFlashlight_Normal.png";
        private const string BakedMetallicPath = BakedFolder + "/BR_CrankFlashlight_Metallic.png";

        public const string NodeName = "BR_CrankFlashlightModel";
        public const string BodyNodeName = "Body";
        public const string CrankNodeName = "Crank";
        public const string HandBoneName = "Hand.R";

        /// <summary>Los nodos de la antorcha bajo la mano: uno da la pose, los dos se apagan.</summary>
        private static readonly string[] DonorMeshNodes = { "Torch", "WoodenTorch" };

        /// <summary>Largo real de una linterna de mano. La escala del import sale de aquí.</summary>
        private const float BodyLengthMeters = 0.18f;

        /// <summary>Largo del brazo de la manivela, del eje al pomo.</summary>
        private const float CrankLengthMeters = 0.06f;

        /// <summary>
        /// A lo LARGO del cuerpo, en fracción de su tamaño: dónde nace el eje de la manivela. Va
        /// por DELANTE del puño, no detrás — en `linterna_mano_lado` se ve que la mano ocupa la
        /// mitad de atrás, y una manivela bajo los dedos no se puede girar. Con el brazo de 6 cm y
        /// medio cuerpo de 9, desde aquí el barrido no asoma por la lente.
        /// </summary>
        private const float CrankAlongBody = 0.18f;

        /// <summary>
        /// Aire entre el disco de la manivela y la carcasa. Lo único que se elige a mano de la
        /// separación: el resto sale de las dos mallas, ver <see cref="CrankPivotX"/>.
        /// </summary>
        private const float CrankClearance = 0.002f;

        /// <summary>
        /// A qué distancia del eje del cuerpo se monta la manivela, DERIVADO de las dos mallas en
        /// vez de elegido: el brazo barre el plano YZ a X constante, así que para que ninguna parte
        /// de la manivela entre en la carcasa en ningún ángulo hace falta que su semiancho quepa
        /// entero fuera del semiancho del cuerpo. Con una fracción a ojo esto era una lotería que
        /// se volvía a perder cada vez que se rehornease cualquiera de las dos piezas — y va a
        /// pasar, porque las dos piden un remesh.
        /// </summary>
        private static float CrankPivotX(Mesh body, Mesh crank)
            => body.bounds.extents.x + crank.bounds.extents.x + CrankClearance;

        private const int MaxTextureSize = 1024;
        private const int TriangleWarnThreshold = 30000;

        [MenuItem("Backrooms/Linterna/Aplicar modelo Meshy", false, 91)]
        public static void Apply()
        {
            string bodyFbx = FindFbx(BodyHints, CrankHints);
            string crankFbx = FindFbx(CrankHints, null);

            if (crankFbx == null)
            {
                Debug.LogError($"[CrankFlashlightModel] No encuentro el FBX de la manivela bajo '{ImportRoot}' " +
                               "(pistas: crank/manivela/handle). Nada tocado.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Art");
            BackroomsEditorFolders.EnsureFolder("Assets/Art/Items");
            BackroomsEditorFolders.EnsureFolder(BakedFolder);

            // La manivela SIEMPRE se puede hornear: es la pieza que ya llegó. El cuerpo puede no
            // estar, y entonces esto se para antes de tocar el prefab en vez de montar media
            // linterna que habría que deshacer.
            ConfigureModel(crankFbx);
            var crankSource = LoadFirstMesh(crankFbx);
            if (crankSource == null)
            {
                Debug.LogError($"[CrankFlashlightModel] '{crankFbx}' no trae ninguna malla. Nada tocado.");
                return;
            }

            var crankMesh = BakeMesh(crankSource, CrankMeshPath, "BR_CrankFlashlight_Crank_Mesh",
                CrankLengthMeters, pivotAtBase: true);

            BakeTexturesFrom(Path.GetDirectoryName(crankFbx)?.Replace('\\', '/'));
            var material = BuildMaterial();

            if (bodyFbx == null)
            {
                Debug.LogWarning("[CrankFlashlightModel] La manivela está horneada, pero NO encuentro el FBX " +
                                 $"del CUERPO bajo '{ImportRoot}'. Pistas probadas: {string.Join(", ", BodyHints)}. " +
                                 "El prefab no se toca: media linterna en la mano es peor que ninguna. Lo que SÍ " +
                                 $"hay ahí:\n{ListCandidates()}\nSi el cuerpo está entre esos con otro nombre, " +
                                 "añade su palabra a BodyHints y repite.");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                return;
            }

            ConfigureModel(bodyFbx);
            var bodySource = LoadFirstMesh(bodyFbx);
            if (bodySource == null)
            {
                Debug.LogError($"[CrankFlashlightModel] '{bodyFbx}' no trae ninguna malla. Nada tocado.");
                return;
            }

            var bodyMesh = BakeMesh(bodySource, BodyMeshPath, "BR_CrankFlashlight_Body_Mesh",
                BodyLengthMeters, pivotAtBase: false);

            // Las texturas del CUERPO ganan: es la pieza que se ve entera. Se hornean las segundas
            // a propósito, sobreescribiendo a las de la manivela — un único material para las dos
            // piezas es una draw call en lugar de dos, y el arte de Meshy sale del mismo prompt.
            BakeTexturesFrom(Path.GetDirectoryName(bodyFbx)?.Replace('\\', '/'));
            material = BuildMaterial();

            AttachToPrefab(bodyMesh, crankMesh, material);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Busca un FBX bajo la carpeta de imports cuya RUTA contenga alguna pista y ninguna de las
        /// excluidas. Devuelve el más reciente: si Joel reimporta una versión mejor, gana la nueva
        /// sin renombrar nada.
        /// </summary>
        private static string FindFbx(string[] hints, string[] exclude)
        {
            if (!AssetDatabase.IsValidFolder(ImportRoot))
                return null;

            string best = null;
            System.DateTime bestTime = System.DateTime.MinValue;

            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { ImportRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) continue;

                string lower = path.ToLowerInvariant();
                if (!Matches(lower, hints)) continue;
                if (exclude != null && Matches(lower, exclude)) continue;

                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp <= bestTime) continue;
                bestTime = stamp;
                best = path;
            }

            return best;
        }

        /// <summary>Los FBX que hay bajo la carpeta de imports, para que un fallo de pista se lea
        /// en la misma línea de consola en vez de acabar en un `find` a mano.</summary>
        private static string ListCandidates()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { ImportRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    sb.Append("  ").Append(path).Append('\n');
            }
            return sb.Length > 0 ? sb.ToString() : "  (ninguno)";
        }

        private static bool Matches(string lowerPath, string[] hints)
        {
            foreach (var h in hints)
                if (lowerPath.Contains(h)) return true;
            return false;
        }

        /// <summary>
        /// Malla CANÓNICA: escala uniforme al largo pedido, eje largo a +Y, y el origen o bien en el
        /// centro de la caja (el cuerpo) o bien en su extremo inferior (la manivela).
        ///
        /// EL PIVOTE DE LA MANIVELA ES EL PUNTO ENTERO DE ESTE FICHERO. Meshy centra el origen en la
        /// caja de la pieza; una manivela con el origen en su centro no gira, ORBITA — el brazo
        /// describe un círculo alrededor de un eje que le pasa por la mitad. Poniendo el origen en
        /// el extremo de la raíz del brazo, girar sobre el eje perpendicular (Z local) barre el
        /// brazo como una manivela de verdad.
        /// </summary>
        private static void MakeCanonical(Mesh mesh, float lengthMeters, bool pivotAtBase)
        {
            if (mesh.blendShapeCount > 0)
            {
                Debug.LogError($"[CrankFlashlightModel] '{mesh.name}' trae {mesh.blendShapeCount} blendshape(s) " +
                               "y este horneado no transforma sus deltas. Nada tocado.");
                return;
            }

            var size = mesh.bounds.size;
            int longAxis = size.x >= size.y && size.x >= size.z ? 0 : (size.y >= size.z ? 1 : 2);
            Vector3 from = longAxis == 0 ? Vector3.right : longAxis == 1 ? Vector3.up : Vector3.forward;
            var rotation = Quaternion.FromToRotation(from, Vector3.up);

            float longest = size[longAxis];
            float s = longest > 1e-6f ? lengthMeters / longest : 1f;

            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = rotation * vertices[i] * s;
            mesh.vertices = vertices;
            mesh.RecalculateBounds();

            // QUÉ EXTREMO ES EL EJE, por geometría y no por fe: el disco de la manivela es la
            // pieza GRUESA y el pomo la fina, así que se compara el grosor medio (distancia al eje
            // Y) del 15 % de arriba contra el de abajo, igual que el destornillador busca su punta.
            // Sin esto, el pivote cae en el extremo que la caja deje abajo — y si es el pomo, el
            // brazo gira alrededor del pomo en vez de alrededor del eje.
            if (pivotAtBase)
            {
                float halfY = mesh.bounds.extents.y;
                float band = halfY * 0.30f;
                float topGirth = 0f, botGirth = 0f;
                int topN = 0, botN = 0;
                foreach (var v in vertices)
                {
                    float r = new Vector2(v.x, v.z).magnitude;
                    if (v.y > mesh.bounds.center.y + halfY - band) { topGirth += r; topN++; }
                    else if (v.y < mesh.bounds.center.y - halfY + band) { botGirth += r; botN++; }
                }
                topGirth = topN > 0 ? topGirth / topN : 0f;
                botGirth = botN > 0 ? botGirth / botN : 0f;

                bool axleIsDown = botGirth >= topGirth;
                if (!axleIsDown)
                {
                    // Media vuelta sobre Z: el eje pasa a −Y y el brazo sale hacia +Y. Rotación
                    // pura, así que normales y tangentes se arreglan con el mismo acumulado.
                    var flip = Quaternion.Euler(0f, 0f, 180f);
                    for (int i = 0; i < vertices.Length; i++) vertices[i] = flip * vertices[i];
                    mesh.vertices = vertices;
                    rotation = flip * rotation;
                    mesh.RecalculateBounds();
                }

                Debug.Log($"[CrankFlashlightModel] '{mesh.name}': grosor arriba {topGirth:F4} / abajo " +
                          $"{botGirth:F4} → eje {(axleIsDown ? "ya estaba" : "volteado")} a −Y.");
            }

            var origin = pivotAtBase
                ? new Vector3(mesh.bounds.center.x, mesh.bounds.min.y, mesh.bounds.center.z)
                : mesh.bounds.center;

            if (origin.sqrMagnitude > 1e-10f)
            {
                for (int i = 0; i < vertices.Length; i++) vertices[i] -= origin;
                mesh.vertices = vertices;
                mesh.RecalculateBounds();
            }

            // Rotación pura + escala uniforme: normales y tangentes sólo se rotan.
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

            if (mesh.triangles.Length / 3 > TriangleWarnThreshold)
            {
                Debug.LogWarning($"[CrankFlashlightModel] '{mesh.name}' trae {mesh.triangles.Length / 3} " +
                                 "triángulos: mucho para un objeto de mano. Remesh en Meshy si pesa.");
            }

            var b = mesh.bounds;
            Debug.Log($"[CrankFlashlightModel] '{mesh.name}': caja {b.size.x:F3} x {b.size.y:F3} x " +
                      $"{b.size.z:F3} m, origen {(pivotAtBase ? "en la base (eje de giro)" : "en el centro")}.");
        }

        private static Mesh BakeMesh(Mesh source, string path, string name, float lengthMeters, bool pivotAtBase)
        {
            var copy = Object.Instantiate(source);
            copy.name = name;
            MakeCanonical(copy, lengthMeters, pivotAtBase);

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(copy, path);
                Debug.Log($"[CrankFlashlightModel] Malla horneada nueva en '{path}'.");
                return copy;
            }

            // Sobrescribir, NUNCA borrar y recrear: cambia el GUID y rompe la referencia del prefab.
            EditorUtility.CopySerialized(copy, existing);
            Object.DestroyImmediate(copy);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            // A la fuerza: sin esto la malla ya cargada se sigue dibujando con búferes viejos.
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        private static void BakeTexturesFrom(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            BakeTexture(folder + "/meshy_basecolor.png", BakedBaseColorPath, isNormal: false, sRgb: true);
            BakeTexture(folder + "/meshy_normal.png", BakedNormalPath, isNormal: true, sRgb: false);
            BakeTexture(folder + "/meshy_metallic_smoothness.png", BakedMetallicPath, isNormal: false, sRgb: false);
        }

        private static void BakeTexture(string sourcePath, string bakedPath, bool isNormal, bool sRgb)
        {
            var importer = AssetImporter.GetAtPath(sourcePath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[CrankFlashlightModel] Sin textura en '{sourcePath}' — se hornea sin ella.");
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
                    Debug.LogWarning($"[CrankFlashlightModel] '{sourcePath}' no cargó como Texture2D.");
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
            Debug.Log($"[CrankFlashlightModel] '{Path.GetFileName(bakedPath)}' horneada a {MaxTextureSize}px " +
                      $"({kb} KB, normal={isNormal}, sRGB={sRgb}).");
        }

        /// <summary>Sin materiales del FBX (Built-in = magenta en URP), sin animación, sin cámaras ni
        /// luces; legible y SIN comprimir porque la malla se reescribe después — con compresión los
        /// datos se van a <c>m_CompressedMesh</c> y lo que se reescribe no es lo que se dibuja.</summary>
        private static void ConfigureModel(string fbxPath)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[CrankFlashlightModel] '{fbxPath}' no tiene ModelImporter.");
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
            Debug.Log($"[CrankFlashlightModel] '{Path.GetFileName(fbxPath)}' reimportado sin materiales, sin " +
                      "animación y legible.");
        }

        private static Mesh LoadFirstMesh(string fbxPath)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
                if (asset is Mesh m) return m;
            return null;
        }

        private static Material BuildMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[CrankFlashlightModel] Sin shader 'Universal Render Pipeline/Lit'. Nada tocado.");
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

        private static void AttachToPrefab(Mesh bodyMesh, Mesh crankMesh, Material material)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(WieldablePrefabPath) == null)
            {
                Debug.LogError($"[CrankFlashlightModel] No hay prefab en '{WieldablePrefabPath}'. Ejecuta antes " +
                               "'Backrooms/Linterna/Crear linterna de manivela'.");
                return;
            }

            var root = PrefabUtility.LoadPrefabContents(WieldablePrefabPath);
            try
            {
                Transform hand = null, donor = null, index = null, pinky = null, middle = null;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (hand == null && t.name == HandBoneName) hand = t;
                    if (index == null && t.name == "Index.1.R") index = t;
                    if (pinky == null && t.name == "Pinky.1.R") pinky = t;
                    if (middle == null && t.name == "Middle.1.R") middle = t;
                    if (donor != null) continue;
                    foreach (var n in DonorMeshNodes)
                    {
                        if (t.name != n) continue;
                        donor = t;
                        break;
                    }
                }

                if (hand == null)
                {
                    Debug.LogError($"[CrankFlashlightModel] No aparece el hueso '{HandBoneName}'. Nada tocado.");
                    return;
                }

                // El tronco de antorcha, fuera de la vista. Se DESACTIVA, no se borra: reversible.
                if (donor != null && donor.gameObject.activeSelf) donor.gameObject.SetActive(false);

                var previous = hand.Find(NodeName);
                if (previous != null) Object.DestroyImmediate(previous.gameObject);

                var node = new GameObject(NodeName);
                node.transform.SetParent(hand, false);

                // EL AGARRE SE CALCULA DESDE LOS NUDILLOS, y no se copia del nodo de la antorcha
                // aunque fuera lo obvio: ese nodo NO cuelga de `Hand.R`, cuelga de `Root`, porque
                // la malla de la antorcha es SKINNED al esqueleto de los brazos. Copiar su
                // `localPosition` a un hijo de la mano mete la linterna 1,59 m por encima del puño
                // —medido—, y colgarla de `Root` la dejaría en la pose de bind sin seguir a la mano.
                // El cálculo es el mismo del destornillador: un puño cerrado sobre un mango lo cruza
                // por la palma, el eje entra por el meñique y sale por el índice, y la lente (el +Y
                // de la malla canónica) sale por el lado del índice.
                if (TryGripFromKnuckles(hand, index, pinky, middle, out var gripPos, out var gripRot))
                {
                    node.transform.localPosition = gripPos;
                    node.transform.localRotation = gripRot;
                }
                else
                {
                    Debug.LogWarning("[CrankFlashlightModel] Faltan huesos de dedos para el agarre: pose de " +
                                     "reserva, VERIFICAR con captura.");
                    node.transform.localPosition = new Vector3(0.02f, 0.03f, 0.01f);
                    node.transform.localEulerAngles = new Vector3(0f, 0f, 90f);
                }

                var boneScale = hand.lossyScale;
                float boneFactor = Mathf.Max(1e-5f, Mathf.Max(boneScale.x, Mathf.Max(boneScale.y, boneScale.z)));
                node.transform.localScale = Vector3.one / boneFactor;

                // La capa del viewmodel manda: la cámara de primera persona filtra por capa.
                int layer = donor != null ? donor.gameObject.layer : hand.gameObject.layer;
                node.layer = layer;

                var body = NewMeshChild(node.transform, BodyNodeName, bodyMesh, material, layer);
                var crank = NewMeshChild(node.transform, CrankNodeName, crankMesh, material, layer);

                float pivotX = CrankPivotX(bodyMesh, crankMesh);
                float pivotY = bodyMesh.bounds.size.y * CrankAlongBody;
                crank.transform.localPosition = new Vector3(pivotX, pivotY, 0f);
                Debug.Log($"[CrankFlashlightModel] Eje de la manivela en ({pivotX:F4}, {pivotY:F4}, 0): " +
                          $"semiancho del cuerpo {bodyMesh.bounds.extents.x:F4} + semiancho de la manivela " +
                          $"{crankMesh.bounds.extents.x:F4} + {CrankClearance:F3} de aire.");
                // El brazo sale del eje hacia +Y (malla canónica) y queda tumbado a lo largo del
                // cuerpo, que es como se guarda una manivela plegable.
                crank.transform.localRotation = Quaternion.identity;

                MoveBeamUnderBody(root, body.transform, bodyMesh);
                PointComponentAtModel(root, crank.transform);

                PrefabUtility.SaveAsPrefabAsset(root, WieldablePrefabPath);
                Debug.LogWarning("[CrankFlashlightModel] Modelo montado. VERIFICAR CON CAPTURA antes de darlo " +
                                 "por bueno: dónde cae la manivela sobre el cuerpo (CrankOffset) y hacia dónde " +
                                 "sale el haz. Los dos son números que no se pueden adivinar sin verlo.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Cuánto sube el CENTRO de la linterna por encima del puño, en fracción de su largo. Se
        /// agarra por el mango, que es el tercio de atrás: el centro queda hacia la lente.
        /// </summary>
        private const float GripRiseFraction = 0.20f;

        private static bool TryGripFromKnuckles(Transform hand, Transform index, Transform pinky,
            Transform middle, out Vector3 localPos, out Quaternion localRot)
        {
            localPos = Vector3.zero;
            localRot = Quaternion.identity;
            if (hand == null || index == null || pinky == null || middle == null) return false;

            Vector3 tipDir = (index.position - pinky.position).normalized;
            if (tipDir.sqrMagnitude < 1e-8f) return false;

            Vector3 fist = (hand.position + middle.position) * 0.5f;
            Vector3 centre = fist + tipDir * (BodyLengthMeters * GripRiseFraction);

            localPos = hand.InverseTransformPoint(centre);
            // La lente está a +Y en la malla canónica, por construcción.
            localRot = Quaternion.FromToRotation(Vector3.up, hand.InverseTransformDirection(tipDir));
            return true;
        }

        private static GameObject NewMeshChild(Transform parent, string name, Mesh mesh, Material material, int layer)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = layer;

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return go;
        }

        /// <summary>
        /// El haz pasa de colgar del hueso a colgar del CUERPO, en la punta de la linterna y
        /// mirando hacia donde ésta apunta. Hasta que el modelo llega, la luz sale del puño: se ve,
        /// pero no está donde debe.
        /// </summary>
        private static void MoveBeamUnderBody(GameObject root, Transform body, Mesh bodyMesh)
        {
            // CREAR-SI-FALTA en vez de avisar y seguir: el haz nace en el creador, pero vive dentro
            // del nodo del modelo desde la primera pasada de este aplicador, y este método corre
            // DESPUÉS de que ese nodo se haya destruido para rehacerlo. Sin esta llamada, rehacer
            // el modelo dejaba la linterna a oscuras sin un solo error — pasó, y se ve sólo al
            // equiparla.
            var light = BackroomsCrankFlashlightCreator.EnsureBeam(root);
            if (light == null)
            {
                Debug.LogError("[CrankFlashlightModel] No hay haz y no se ha podido crear. La linterna no " +
                               "alumbraría, no la verían los peers (ADR-042) y no existiría para ADR-080.");
                return;
            }

            var beam = light.transform;
            beam.SetParent(body, false);
            // La lente donde estaba la llama: el extremo +Y de la malla canónica del cuerpo.
            beam.localPosition = new Vector3(0f, bodyMesh.bounds.max.y, 0f);
            beam.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);

            // Y el componente reapuntado al haz NUEVO: la referencia anterior murió con el nodo que
            // se destruyó. Dejarla rota no da error —el componente busca una `Light` en los hijos
            // como reserva— pero esa búsqueda incluye los inactivos y podría quedarse con la luz de
            // fuego de la antorcha.
            var flashlight = root.GetComponent<CrankFlashlightWieldable>();
            if (flashlight == null) return;

            var so = new SerializedObject(flashlight);
            var field = so.FindProperty("beam");
            if (field == null) return;
            field.objectReferenceValue = light;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void PointComponentAtModel(GameObject root, Transform crank)
        {
            var flashlight = root.GetComponent<CrankFlashlightWieldable>();
            if (flashlight == null)
            {
                Debug.LogWarning("[CrankFlashlightModel] El prefab no tiene CrankFlashlightWieldable: la " +
                                 "manivela no girará.");
                return;
            }

            var so = new SerializedObject(flashlight);
            var field = so.FindProperty("crank");
            if (field != null) field.objectReferenceValue = crank;

            // EL EJE ES X, no Z, y la diferencia es que media vuelta pase por dentro del cuerpo o
            // no. La manivela va montada en el COSTADO: su eje sale perpendicular a la carcasa, que
            // es el +X local, y el brazo —que apunta a +Y— barre entonces el plano YZ, siempre por
            // fuera. Con el eje en Z el brazo giraría en el plano que contiene la carcasa y la
            // atravesaría en medio giro.
            var axis = so.FindProperty("crankAxis");
            if (axis != null) axis.vector3Value = Vector3.right;

            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif
