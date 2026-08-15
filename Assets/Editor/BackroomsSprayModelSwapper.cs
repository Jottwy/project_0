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
        /// Desde que la malla se hornea canónica (<see cref="MakeCanonical"/>) el eje es +Y por
        /// construcción; el sentido de la boquilla se descubre mirando el render, no se deduce del
        /// FBX, porque Meshy no promete orientación.
        /// </summary>
        private static readonly Vector3 CanLongAxis = Vector3.up;
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

            // Y el mismo arte al objeto del SUELO. Encadenado aquí a propósito: la mano y el
            // suelo comen de la misma malla horneada, y dejarlos en dos botones distintos es
            // pedir que uno se quede atrás sin que nadie lo note.
            BackroomsSprayPickupCreator.Apply();
        }

        /// <summary>
        /// Deja la malla CANÓNICA: tamaño real, de pie (eje largo a +Y, boquilla arriba) y
        /// centrada en su caja. Se hace sobre los VÉRTICES y no con la escala de un Transform, y
        /// eso no es preferencia: el prefab del suelo tiene que quedarse con escala 1 en el root
        /// porque el <c>MaterialEffect</c> del vendor (el resaltado al mirar el objeto) es en
        /// espacio de OBJETO — escalar el root lo infla por el mismo factor, que es como salió una
        /// "marca" enorme sobre la botella de agua de almendras. Con la malla ya en metros, la
        /// mano y el suelo usan la misma sin compensar nada.
        ///
        /// Las NORMALES no se escalan como los vértices: con escala no uniforme eso las tuerce.
        /// Se rotan y se multiplican por la INVERSA de cada factor (traspuesta de la inversa, que
        /// en una diagonal es esto), y se renormalizan. Las tangentes sí van como los vértices en
        /// su `xyz`, conservando la `w` (el signo de la bitangente).
        /// </summary>
        private static void MakeCanonical(Mesh mesh)
        {
            // Las deltas de un blendshape son vectores en espacio local que este método NO
            // transforma: una lata con formas se horneaba deformándose al animarla. Hoy el FBX no
            // trae ninguna; si algún día trae, mejor un error que un fallo callado.
            if (mesh.blendShapeCount > 0)
            {
                Debug.LogError($"[SprayModel] La malla trae {mesh.blendShapeCount} blendshape(s) y este " +
                               "horneado no transforma sus deltas. Nada tocado.");
                return;
            }

            var size = mesh.bounds.size;
            int longAxis = size.x >= size.y && size.x >= size.z ? 0 : (size.y >= size.z ? 1 : 2);

            // Rotación que lleva el eje largo actual a +Y. Si ya es +Y no rota nada.
            Vector3 from = longAxis == 0 ? Vector3.right : longAxis == 1 ? Vector3.up : Vector3.forward;
            var rotation = Quaternion.FromToRotation(from, Vector3.up);

            // Factores POR EJE tras rotar: alto contra alto, y el resto contra el diámetro. El
            // modelo viene achaparrado (2,3:1 contra 2,9:1 de una lata de verdad).
            float longest = size[longAxis];
            float girth = 0f;
            for (int a = 0; a < 3; a++)
                if (a != longAxis) girth = Mathf.Max(girth, size[a]);

            float sy = longest > 1e-6f ? CanHeightMeters / longest : 1f;
            float sxz = girth > 1e-6f ? CanDiameterMeters / girth : 1f;
            var scale = new Vector3(sxz, sy, sxz);

            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = Vector3.Scale(rotation * vertices[i], scale);
            mesh.vertices = vertices;

            var normals = mesh.normals;
            if (normals != null && normals.Length == vertices.Length)
            {
                var inverse = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = Vector3.Scale(rotation * normals[i], inverse).normalized;
                mesh.normals = normals;
            }

            var tangents = mesh.tangents;
            if (tangents != null && tangents.Length == vertices.Length)
            {
                for (int i = 0; i < tangents.Length; i++)
                {
                    var t = tangents[i];
                    var xyz = Vector3.Scale(rotation * new Vector3(t.x, t.y, t.z), scale).normalized;
                    tangents[i] = new Vector4(xyz.x, xyz.y, xyz.z, t.w);
                }
                mesh.tangents = tangents;
            }

            mesh.RecalculateBounds();

            // Centrada en su caja: así el collider del pickup sale de `bounds` sin corrección y el
            // objeto no cuelga desplazado de donde se le ponga.
            var centre = mesh.bounds.center;
            if (centre.sqrMagnitude > 1e-10f)
            {
                vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; i++) vertices[i] -= centre;
                mesh.vertices = vertices;
                mesh.RecalculateBounds();
            }

            // Guarda de cordura, calcada de BackroomsAlmondWaterCreator: si alguien revierte el
            // horneado, lo que se ve NO es un error sino una lata de 2 cm o de tres metros, y eso
            // no lo dice ningún log.
            float widest = Mathf.Max(mesh.bounds.size.x, Mathf.Max(mesh.bounds.size.y, mesh.bounds.size.z));
            if (widest < 0.05f || widest > 0.6f)
            {
                Debug.LogError($"[SprayModel] La malla canónica mide {widest:F3} m de lado mayor, fuera del " +
                               "rango sano de una lata (0,05–0,6). Revisa la transformación.");
            }

            Debug.Log($"[SprayModel] Malla canónica: caja {mesh.bounds.size.x:F3} x " +
                      $"{mesh.bounds.size.y:F3} x {mesh.bounds.size.z:F3} m, centro {mesh.bounds.center}.");
        }

        /// <summary>
        /// Hornea la malla a un `.asset` versionado. Se sobrescribe el asset EXISTENTE con
        /// <c>CopySerialized</c> en vez de borrarlo y crearlo: borrarlo cambia el GUID, y un GUID
        /// nuevo en cada ejecución rompe la referencia del prefab y ensucia el diff sin motivo.
        /// </summary>
        private static Mesh BakeMesh(Mesh source)
        {
            var copy = Object.Instantiate(source);
            // Mismo nombre que el fichero: si no, Unity avisa en cada import ("Main Object Name
            // does not match filename").
            copy.name = "BR_SprayCan_Mesh";
            MakeCanonical(copy);

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

            // REIMPORTAR, y esto costó tres rondas de "pero si el fichero está bien": `SaveAssets`
            // deja el `.asset` correcto en disco, pero la malla que YA estaba cargada sigue
            // dibujándose con sus búferes viejos. Síntoma: la caja mide 19 cm y lo que se ve mide
            // 2, o directamente no se ve. Sin error, sin warning, y el fichero en disco te da la
            // razón mientras la pantalla te la quita.
            AssetDatabase.ImportAsset(BakedMeshPath, ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<Mesh>(BakedMeshPath);

            Debug.Log($"[SprayModel] Malla horneada actualizada en '{BakedMeshPath}' (mismo GUID), " +
                      $"reimportada: caja {reloaded.bounds.size.y:F3} m de alto.");
            return reloaded;
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
            // SIN COMPRIMIR, y esto costó un ciclo entero de "la lata ha desaparecido": la
            // compresión de malla cuantiza las posiciones RELATIVAS A LA CAJA del import. Este
            // horneado reescribe los vértices después (`MakeCanonical`), así que la geometría
            // quedaba cuantizada contra una caja que ya no era la suya: bounds de 19 cm, dibujo de
            // 2 cm, y en la mano directamente nada. Ni un error en consola.
            if (importer.meshCompression != ModelImporterMeshCompression.Off)
            {
                importer.meshCompression = ModelImporterMeshCompression.Off;
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

                // La malla ya viene en metros y de pie desde `MakeCanonical`, así que aquí no hay
                // nada que redimensionar: solo deshacer la escala acumulada del hueso, que en un
                // rig de brazos no tiene por qué ser 1.
                var boneScale = hand.lossyScale;
                float boneFactor = Mathf.Max(1e-5f, Mathf.Max(boneScale.x,
                    Mathf.Max(boneScale.y, boneScale.z)));
                go.transform.localScale = Vector3.one / boneFactor;

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
