#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Pone el modelo de verdad en la mano del bote de spray: la lata generada en Meshy, en vez
    /// del <c>WoodenTorch</c> que heredó por ser un clon de la antorcha.
    ///
    /// HOY EL BOTE SE EMPUÑA VACÍO, y eso no se había dicho en ningún sitio: "Apagar el fuego del
    /// bote" desactiva todo nodo cuyo nombre contenga "torch", y el nodo de la MALLA se llama
    /// justo <c>WoodenTorch</c> — así que quedó apagada con las brasas. El jugador saca las manos
    /// y no lleva nada. Esto lo arregla de paso.
    ///
    /// TRES COSAS QUE ESTE SCRIPT HACE Y NO SON OBVIAS:
    ///
    /// 1. La lata cuelga del HUESO <c>Hand.R</c>, no del raíz del arma. La malla de la antorcha
    ///    era <c>SkinnedMeshRenderer</c> pesada al esqueleto de los brazos; una malla estática de
    ///    Meshy no tiene pesos, así que si se cuelga donde estaba se queda CLAVADA en el aire
    ///    mientras los brazos se mueven. Colgando del hueso, la animación la arrastra gratis.
    ///
    /// 2. La escala se CALCULA, no se adivina: se mide la caja de la malla y se lleva su lado
    ///    mayor a <see cref="CanHeightMeters"/>. Meshy exporta en la escala que le sale, y una
    ///    lata de tres metros en la mano no es un bug que se vea en un log.
    ///
    /// 3. Las texturas vienen a 8192² (82 MB el basecolor). Para un objeto que ocupa un palmo de
    ///    pantalla eso son ~350 MB de VRAM por gusto, así que se hornean a
    ///    <see cref="MaxTextureSize"/>. El fichero fuente no se toca.
    ///
    /// 4. Y lo horneado va a <see cref="BakedFolder"/>, DENTRO del control de versiones, porque
    ///    `Assets/MeshyImports/` está en .gitignore: apuntar el prefab al import dejaba el bote
    ///    invisible en cualquier otra máquina.
    ///
    /// Reejecutable a propósito (el prefab vive en territorio del vendor y un reimport se lo
    /// lleva): borra su propio nodo antes de volver a crearlo, así que correrlo dos veces no
    /// apila dos latas.
    /// </summary>
    public static class BackroomsSprayModelSwapper
    {
        private const string PrefabPath = "Assets/Prefabs/Wieldables/BR_Wieldable_SprayCan.prefab";

        private const string ModelFolder = "Assets/MeshyImports/Meshy_Model_20260815_113116";
        private const string FbxPath = ModelFolder + "/Meshy_AI__0815093048_texture.fbx";
        private const string BaseColorPath = ModelFolder + "/meshy_basecolor.png";
        private const string NormalPath = ModelFolder + "/meshy_normal.png";
        private const string MetallicPath = ModelFolder + "/meshy_metallic_smoothness.png";

        /// <summary>
        /// Copia HORNEADA y versionada del modelo. `Assets/MeshyImports/` está en .gitignore
        /// ("cientos de MB, se regeneran desde la herramienta"), así que apuntar el prefab
        /// directamente al import deja el bote INVISIBLE en cualquier máquina que no sea ésta —
        /// exactamente el fallo que este script viene a arreglar, pero para todos los demás.
        ///
        /// Lo horneado pesa poco: la malla como `.asset` (los 75 MB del FBX son las texturas 8K
        /// que Meshy incrusta dentro, no la geometría) y las tres texturas ya reducidas a 1024.
        /// </summary>
        private const string BakedFolder = "Assets/Art/Items/SprayCan";
        private const string BakedMeshPath = BakedFolder + "/BR_SprayCan_Mesh.asset";
        private const string BakedBaseColorPath = BakedFolder + "/BR_SprayCan_BaseColor.png";
        private const string BakedNormalPath = BakedFolder + "/BR_SprayCan_Normal.png";
        private const string BakedMetallicPath = BakedFolder + "/BR_SprayCan_Metallic.png";

        private const string MaterialPath = BakedFolder + "/BR_SprayCan_Mat.mat";

        /// <summary>Nombre del nodo que crea este script. Es su ancla para poder rehacerse.</summary>
        private const string NodeName = "BR_SprayCanModel";

        /// <summary>Hueso del que cuelga la lata.</summary>
        private const string HandBoneName = "Hand.R";

        /// <summary>Alto real de una lata de spray. La escala del import sale de aquí.</summary>
        private const float CanHeightMeters = 0.19f;

        /// <summary>Y su diámetro, que no se deduce del alto: el modelo viene achaparrado.</summary>
        private const float CanDiameterMeters = 0.066f;

        /// <summary>
        /// Retoque fino SOBRE el agarre calculado, en espacio del hueso. Es lo único que se ajusta
        /// a ojo; el grueso lo pone <see cref="TryGripFromKnuckles"/>. No se ajusta moviendo el nodo
        /// en el prefab: la siguiente reejecución lo borraría.
        /// </summary>
        private static readonly Vector3 GripNudge = Vector3.zero;
        private static readonly Vector3 EulerNudge = Vector3.zero;

        /// <summary>
        /// Agarre de reserva, por si el nodo de la antorcha ya no estuviera para copiarle la pose.
        /// </summary>
        private static readonly Vector3 FallbackPosition = new Vector3(0.02f, 0.03f, 0.01f);
        private static readonly Vector3 FallbackEuler = new Vector3(0f, 0f, 90f);

        /// <summary>
        /// El eje largo de la lata en su espacio local, y hacia dónde cae la BOQUILLA en ese eje.
        /// Se descubre mirando el render, no se deduce del FBX: Meshy no promete orientación.
        /// </summary>
        private static readonly Vector3 CanLongAxis = Vector3.forward;
        private const bool NozzleTowardsAxis = true;

        /// <summary>Un palmo de pantalla no necesita 8K.</summary>
        private const int MaxTextureSize = 1024;

        /// <summary>Por encima de esto, la malla es cara de más para un objeto de mano.</summary>
        private const int TriangleWarnThreshold = 30000;

        [MenuItem("Backrooms/Spray/Aplicar modelo Meshy al bote", false, 98)]
        public static void Apply()
        {
            if (!File.Exists(FbxPath))
            {
                Debug.LogError($"[SprayModel] No hay FBX en '{FbxPath}'. Nada tocado.");
                return;
            }

            BackroomsEditorFolders.EnsureFolder("Assets/Art");
            BackroomsEditorFolders.EnsureFolder("Assets/Art/Items");
            BackroomsEditorFolders.EnsureFolder(BakedFolder);

            ConfigureModel();

            var source = LoadFirstMesh();
            if (source == null)
            {
                Debug.LogError($"[SprayModel] '{FbxPath}' no trae ninguna malla. Nada tocado.");
                return;
            }

            var mesh = BakeMesh(source);
            if (mesh == null) return;

            BakeTexture(BaseColorPath, BakedBaseColorPath, isNormal: false, sRgb: true);
            BakeTexture(NormalPath, BakedNormalPath, isNormal: true, sRgb: false);
            BakeTexture(MetallicPath, BakedMetallicPath, isNormal: false, sRgb: false);

            var material = BuildMaterial();
            if (material == null) return;

            AttachToPrefab(mesh, material);
        }

        /// <summary>
        /// Hornea la malla a un `.asset` versionado. Se sobrescribe el asset EXISTENTE con
        /// <c>CopySerialized</c> en vez de borrarlo y crearlo: borrarlo cambia el GUID, y un GUID
        /// nuevo en cada ejecución rompe la referencia del prefab y ensucia el diff sin motivo.
        /// </summary>
        private static Mesh BakeMesh(Mesh source)
        {
            var copy = Object.Instantiate(source);
            copy.name = "BR_SprayCan";

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(BakedMeshPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(copy, BakedMeshPath);
                Debug.Log($"[SprayModel] Malla horneada nueva en '{BakedMeshPath}'.");
                return copy;
            }

            EditorUtility.CopySerialized(copy, existing);
            Object.DestroyImmediate(copy);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            Debug.Log($"[SprayModel] Malla horneada actualizada en '{BakedMeshPath}' (mismo GUID).");
            return existing;
        }

        /// <summary>
        /// Hornea una textura del import a una copia versionada de 1024 px.
        ///
        /// El origen se lee en CRUDO (sin comprimir, sin marcar como normal y en lineal) porque
        /// leer píxeles de una textura ya comprimida o ya convertida a normal devuelve los canales
        /// barajados: el mapa de relieve saldría con la X y la Y cambiadas, que se ve como una luz
        /// que viene del lado equivocado. La copia SÍ se marca como normal, ya en destino.
        /// </summary>
        private static void BakeTexture(string sourcePath, string bakedPath, bool isNormal, bool sRgb)
        {
            var importer = AssetImporter.GetAtPath(sourcePath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[SprayModel] Sin textura en '{sourcePath}' — se hornea sin ella.");
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
                    Debug.LogWarning($"[SprayModel] '{sourcePath}' no cargó como Texture2D.");
                    return;
                }

                File.WriteAllBytes(bakedPath, tex.EncodeToPNG());
                AssetDatabase.ImportAsset(bakedPath, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                // El import se deja como estaba: es un fichero de cientos de MB que no queremos
                // residente y sin comprimir en el editor solo por haberlo horneado una vez.
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
            Debug.Log($"[SprayModel] '{Path.GetFileName(bakedPath)}' horneada a {MaxTextureSize}px " +
                      $"({kb} KB, normal={isNormal}, sRGB={sRgb}).");
        }

        /// <summary>
        /// Ajustes del FBX. Sin materiales (el nuestro se construye aparte y en URP: importar los
        /// del FBX los trae en shader Built-in y salen MAGENTA desde ADR-065), sin animación,
        /// sin cámaras ni luces, y sin lectura desde CPU — una malla `isReadable` duplica su
        /// memoria para nada cuando solo se dibuja.
        /// </summary>
        private static void ConfigureModel()
        {
            var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[SprayModel] '{FbxPath}' no tiene ModelImporter.");
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
            // Legible SÍ, al revés de lo que pediría un modelo que se dibuja y ya: de este FBX no
            // se dibuja nada, se HORNEA — y copiar la malla a un asset propio necesita leerla.
            if (!importer.isReadable) { importer.isReadable = true; dirty = true; }
            if (importer.meshCompression != ModelImporterMeshCompression.Medium)
            {
                importer.meshCompression = ModelImporterMeshCompression.Medium;
                dirty = true;
            }
            if (!importer.optimizeMeshPolygons) { importer.optimizeMeshPolygons = true; dirty = true; }
            if (!importer.optimizeMeshVertices) { importer.optimizeMeshVertices = true; dirty = true; }

            if (!dirty) return;
            importer.SaveAndReimport();
            Debug.Log("[SprayModel] FBX reimportado sin materiales, sin animación y sin lectura CPU.");
        }

        private static Mesh LoadFirstMesh()
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(FbxPath))
                if (asset is Mesh m) return m;
            return null;
        }

        /// <summary>
        /// Material URP Lit propio, fuera de la carpeta del import (que se pisa entera al volver a
        /// bajar el modelo de Meshy). El canal de smoothness se lee del ALFA del mapa metálico,
        /// que es como lo exporta Meshy y como URP lo espera con
        /// <c>_SmoothnessTextureChannel = 0</c>.
        /// </summary>
        private static Material BuildMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[SprayModel] Sin shader 'Universal Render Pipeline/Lit'. Nada tocado.");
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
        /// Cuánto sube el CENTRO de la lata por encima del punto donde la mano agarra, en
        /// fracción de su alto. Una lata se coge por el tercio bajo, así que su centro queda algo
        /// por encima del puño.
        /// </summary>
        private const float GripRiseFraction = 0.15f;

        /// <summary>
        /// Deduce el agarre de los NUDILLOS, que es geometría de la mano y no una postura copiada.
        ///
        /// Un puño cerrado sobre un cilindro lo cruza por la palma: el eje entra por el lado del
        /// índice y sale por el del meñique. Así que el eje de la lata es la línea
        /// meñique→índice, y la boquilla sale por el índice, que es donde va el dedo que aprieta.
        /// El centro se pone en el puño (muñeca y nudillo medio a medias) y sube un pellizco,
        /// porque una lata se agarra por el tercio bajo.
        ///
        /// SE PROBÓ ANTES A COPIÁRSELO A LA ANTORCHA, que está pesada a este mismo esqueleto, y no
        /// sirve por dos motivos medidos: la caja de una malla skinned vive en el espacio de la
        /// MALLA y no en el del hueso, así que su centro puso la lata a 32 cm de la mano; y el
        /// truco de "el extremo lejos de la muñeca es el de arriba" devolvió la llama apuntando al
        /// suelo, porque en esa pose los dos extremos quedan por debajo. Los nudillos no mienten.
        /// </summary>
        private static bool TryGripFromKnuckles(Transform hand, Transform index, Transform pinky,
            Transform middle, out Vector3 localPos, out Quaternion localRot)
        {
            localPos = Vector3.zero;
            localRot = Quaternion.identity;
            if (hand == null || index == null || pinky == null || middle == null) return false;

            Vector3 nozzleDir = (index.position - pinky.position).normalized;
            if (nozzleDir.sqrMagnitude < 1e-8f) return false;


            Vector3 fist = (hand.position + middle.position) * 0.5f;
            Vector3 canCentre = fist + nozzleDir * (CanHeightMeters * GripRiseFraction);

            Vector3 nozzleAxis = NozzleTowardsAxis ? CanLongAxis : -CanLongAxis;
            localPos = hand.InverseTransformPoint(canCentre);
            localRot = Quaternion.FromToRotation(nozzleAxis, hand.InverseTransformDirection(nozzleDir));
            return true;
        }

        private static void AttachToPrefab(Mesh mesh, Material material)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
            {
                Debug.LogError($"[SprayModel] No hay prefab en '{PrefabPath}'. " +
                               "Ejecuta antes 'Backrooms/Spray/Crear bote de spray'.");
                return;
            }

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Transform hand = null, torch = null, index = null, pinky = null, middle = null;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (hand == null && t.name == HandBoneName) hand = t;
                    if (torch == null && t.name == "WoodenTorch") torch = t;
                    if (index == null && t.name == "Index.1.R") index = t;
                    if (pinky == null && t.name == "Pinky.1.R") pinky = t;
                    if (middle == null && t.name == "Middle.1.R") middle = t;
                }

                if (hand == null)
                {
                    Debug.LogError($"[SprayModel] No aparece el hueso '{HandBoneName}' en el prefab. " +
                                   "Nada tocado: colgar la lata del raíz la dejaría flotando quieta.");
                    return;
                }

                // La malla prestada, fuera de la vista para siempre. Ya venía desactivada (la
                // pilló el filtro "torch" de StripFire), pero conviene que quede explícito.
                if (torch != null && torch.gameObject.activeSelf) torch.gameObject.SetActive(false);

                var previous = hand.Find(NodeName);
                if (previous != null) Object.DestroyImmediate(previous.gameObject);

                var go = new GameObject(NodeName);
                go.transform.SetParent(hand, false);

                // El agarre sale de la GEOMETRÍA de la mano, no de probar números a ojo: son diez
                // ciclos de render-y-mirar contra uno.
                if (TryGripFromKnuckles(hand, index, pinky, middle, out var gripPos, out var gripRot))
                {
                    go.transform.localPosition = gripPos + GripNudge;
                    go.transform.localRotation = gripRot * Quaternion.Euler(EulerNudge);
                }
                else
                {
                    Debug.LogWarning("[SprayModel] Faltan huesos de dedos para calcular el agarre: " +
                                     "se usa la pose de reserva, que habrá que ajustar a ojo.");
                    go.transform.localPosition = FallbackPosition + GripNudge;
                    go.transform.localEulerAngles = FallbackEuler + EulerNudge;
                }

                // La escala se deriva de la caja de la malla, y por EJES: el largo pasa a medir lo
                // que mide una lata y el ancho lo que mide su diámetro. Uniforme salía una lata de
                // 8,3 cm de gruesa — la proporción que trae el modelo de Meshy es 2,3:1 y la de
                // una lata real 2,9:1, o sea un bote achaparrado. Y se divide por la escala
                // acumulada del hueso, que en un rig de brazos no tiene por qué ser 1.
                Vector3 size = mesh.bounds.size;
                int longAxis = size.x >= size.y && size.x >= size.z ? 0 : (size.y >= size.z ? 1 : 2);
                var boneScale = hand.lossyScale;
                float boneFactor = Mathf.Max(1e-5f, Mathf.Max(boneScale.x,
                    Mathf.Max(boneScale.y, boneScale.z)));

                var scale = Vector3.one;
                for (int a = 0; a < 3; a++)
                {
                    float want = a == longAxis ? CanHeightMeters : CanDiameterMeters;
                    scale[a] = size[a] > 1e-5f ? want / size[a] / boneFactor : 1f;
                }
                go.transform.localScale = scale;

                // La capa manda MÁS que el renderer: el arma se dibuja con la cámara de primera
                // persona, que filtra por capa. En la capa del mundo la lata sale detrás de las
                // paredes o directamente no sale.
                int layer = torch != null ? torch.gameObject.layer : hand.gameObject.layer;
                go.layer = layer;

                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();

                // Por índices y no por `mesh.triangles`: ese exige la malla legible desde CPU, y
                // acabamos de importarla con `isReadable = false` a propósito.
                long indices = 0;
                for (int s = 0; s < mesh.subMeshCount; s++) indices += (long)mesh.GetIndexCount(s);
                long tris = indices / 3;
                Debug.Log($"[SprayModel] Lata colgada de '{HandBoneName}': tris={tris}, " +
                          $"caja={mesh.bounds.size.x:F4}/{mesh.bounds.size.y:F4}/{mesh.bounds.size.z:F4}, " +
                          $"escala={go.transform.localScale}, capa={layer}, " +
                          $"pos={go.transform.localPosition}, euler={go.transform.localEulerAngles}.");
                if (tris > TriangleWarnThreshold)
                {
                    Debug.LogWarning($"[SprayModel] {tris} triángulos para un objeto de mano es MUCHO " +
                                     $"(referencia sana: <{TriangleWarnThreshold}). El import no puede " +
                                     "reducirlos; hay que reexportar la lata más ligera desde Meshy.");
                }
                Debug.LogWarning("[SprayModel] VERIFICAR EN JUEGO: que la lata se ve en la mano, que no " +
                                 "atraviesa el brazo y que gira con la animación. El encaje se ajusta en " +
                                 "LocalPosition/LocalEuler de este script y se vuelve a ejecutar el menú.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
#endif
