#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-077 enm. 2 — el material de PRIMERA PERSONA de un objeto de mano.
    ///
    /// El viewmodel de FPSCore no se dibuja con una segunda cámara: los brazos llevan un shader
    /// (<c>LitFieldOfView*.shadergraph</c>) que reproyecta cada vértice contra los uniforms
    /// GLOBALES <c>_FOV</c>/<c>_FOVEnabled</c>. Sólo warpea lo que usa ese shader. Un objeto
    /// colgado de la mano con URP/Lit se dibuja con la proyección de la cámara del jugador
    /// mientras la mano que lo sujeta se dibuja con la del viewmodel: dos proyecciones en el mismo
    /// puño, y el objeto sale ~1,5× más grande que la mano y desplazado. Mordió con el reloj
    /// (ADR-077) y volvió a morder con el destornillador, el bote y la linterna.
    ///
    /// La cura es la del vendor: DOS materiales por objeto. <c>BR_X_Mat</c> (URP/Lit) para lo que
    /// vive en el mundo —pickup, icono, proxy de terceros— y <c>BR_X_FP_Mat</c> (este) para el
    /// renderer que cuelga de la mano. Igual que <c>HuntingKnife.mat</c> / <c>FP_HuntingKnife.mat</c>.
    ///
    /// El shader del viewmodel lee metallic, oclusión y smoothness de UN <c>_MaskMap</c> empaquetado
    /// (convención HDRP: R = metallic, G = oclusión, A = smoothness × <c>_SmoothnessIntensity</c>),
    /// así que el mapa metálico de Meshy (metallic en RGB, smoothness en el alfa) se reempaqueta
    /// aquí. Cuando el PNG no trae alfa el alfa vale 1, que es exactamente lo que URP/Lit ya estaba
    /// leyendo: el aspecto no cambia, sólo la proyección.
    ///
    /// La puerta es <c>ViewmodelWarpTests</c>: todo renderer activo de un wieldable del proyecto
    /// tiene que usar un shader de warp.
    /// </summary>
    public static class BackroomsViewmodelMaterials
    {
        /// <summary>El shader de los OBJETOS del viewmodel (el del cuchillo, el hacha, la antorcha).</summary>
        public const string FirstPersonShaderPath =
            "Assets/PolymindGames/FPSCore/Code/Shaders/LitFieldOfView.shadergraph";

        /// <summary>El de la PIEL (brazos, y el reloj por ADR-077). Misma cadena de warp, más SSS.</summary>
        public const string SkinShaderPath =
            "Assets/PolymindGames/FPSCore/Code/Shaders/LitFieldOfView_SSS.shadergraph";

        /// <summary>El de la UI diegética (esfera del reloj): <c>UI/Default</c> con el warp a mano.</summary>
        public const string UiWarpShaderPath = "Assets/Art/Watch/BR_UIWarp.shader";

        private const int MaxTextureSize = 1024;

        public static Shader FirstPersonShader() => AssetDatabase.LoadAssetAtPath<Shader>(FirstPersonShaderPath);

        /// <summary>
        /// Construye (o refresca) el material de primera persona a partir del material de MUNDO ya
        /// horneado: mismas texturas, shader de warp, y el mask map reempaquetado desde el mapa
        /// metálico. Devuelve null, con error en consola, si falta el material de mundo o el shader.
        /// </summary>
        public static Material BuildFirstPerson(string worldMaterialPath, string firstPersonMaterialPath,
            string maskMapPath, string tag)
        {
            var world = AssetDatabase.LoadAssetAtPath<Material>(worldMaterialPath);
            if (world == null)
            {
                Debug.LogError($"{tag} Sin material de mundo en '{worldMaterialPath}': no hay de qué derivar el de primera persona.");
                return null;
            }

            var shader = FirstPersonShader();
            if (shader == null)
            {
                Debug.LogError($"{tag} Sin shader de viewmodel en '{FirstPersonShaderPath}' (¿vendor reimportado?). Nada tocado.");
                return null;
            }

            var baseMap = world.GetTexture("_BaseMap") as Texture2D;
            var normal = world.GetTexture("_BumpMap") as Texture2D;
            var metallic = world.GetTexture("_MetallicGlossMap") as Texture2D;

            Texture2D mask = null;
            if (metallic != null) mask = BakeMaskMap(metallic, maskMapPath, tag);
            else Debug.LogWarning($"{tag} El material de mundo no lleva mapa metálico: el de primera persona sale sin _MaskMap.");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(firstPersonMaterialPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, firstPersonMaterialPath);
            }
            mat.shader = shader;
            mat.SetColor("_BaseColor", Color.white);
            mat.SetTexture("_BaseColorMap", baseMap);
            mat.SetTexture("_NormalMap", normal);
            mat.SetTexture("_MaskMap", mask);
            // El alfa del mask map YA es la smoothness que URP/Lit leía (1 cuando el PNG no trae
            // alfa); el multiplicador se deja en 1 para que el aspecto sea el mismo.
            mat.SetFloat("_SmoothnessIntensity", 1f);

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            Debug.Log($"{tag} Material de primera persona en '{firstPersonMaterialPath}' con '{shader.name}'" +
                      (mask != null ? $" y mask map '{Path.GetFileName(maskMapPath)}'." : ", sin mask map."));
            return mat;
        }

        /// <summary>
        /// R = metallic (R del mapa metálico), G = oclusión (1: Meshy no la exporta), B = 0,
        /// A = smoothness (alfa del mapa metálico; 1 si el PNG es RGB). Se lee el PNG del disco, no
        /// la textura importada: así no hay que tocar el importador (que marca el mapa como no
        /// legible y comprimido) y los canales llegan sin barajar.
        /// </summary>
        private static Texture2D BakeMaskMap(Texture2D metallic, string maskMapPath, string tag)
        {
            string sourcePath = AssetDatabase.GetAssetPath(metallic);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                Debug.LogWarning($"{tag} El mapa metálico no está en disco ('{sourcePath}'): sin mask map.");
                return null;
            }

            var raw = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!ImageConversion.LoadImage(raw, File.ReadAllBytes(sourcePath)))
                {
                    Debug.LogWarning($"{tag} '{sourcePath}' no se pudo decodificar: sin mask map.");
                    return null;
                }

                var src = raw.GetPixels32();
                var dst = new Color32[src.Length];
                for (int i = 0; i < src.Length; i++)
                    dst[i] = new Color32(src[i].r, 255, 0, src[i].a);

                var packed = new Texture2D(raw.width, raw.height, TextureFormat.RGBA32, false, true);
                try
                {
                    packed.SetPixels32(dst);
                    packed.Apply(false, false);
                    File.WriteAllBytes(maskMapPath, packed.EncodeToPNG());
                }
                finally
                {
                    Object.DestroyImmediate(packed);
                }
            }
            finally
            {
                Object.DestroyImmediate(raw);
            }

            AssetDatabase.ImportAsset(maskMapPath, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(maskMapPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;
                importer.maxTextureSize = MaxTextureSize;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(maskMapPath);
        }

        /// <summary>
        /// Cuelga el material de primera persona de TODOS los MeshRenderer bajo el nodo del modelo
        /// (el nodo mismo incluido) dentro del prefab del wieldable. Devuelve cuántos cambió.
        /// </summary>
        public static int ApplyToWieldable(string prefabPath, string modelNodeName, Material firstPerson, string tag)
        {
            if (firstPerson == null) return 0;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                Debug.LogError($"{tag} No hay prefab en '{prefabPath}'.");
                return 0;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Transform node = null;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != modelNodeName) continue;
                    node = t;
                    break;
                }
                if (node == null)
                {
                    Debug.LogError($"{tag} El prefab no tiene ningún nodo '{modelNodeName}'. Nada tocado.");
                    return 0;
                }

                int swapped = 0;
                foreach (var renderer in node.GetComponentsInChildren<MeshRenderer>(true))
                {
                    renderer.sharedMaterial = firstPerson;
                    swapped++;
                }
                if (swapped == 0)
                {
                    Debug.LogError($"{tag} '{modelNodeName}' no tiene MeshRenderer debajo. Nada tocado.");
                    return 0;
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                Debug.Log($"{tag} {swapped} renderer(s) bajo '{modelNodeName}' con '{firstPerson.name}' en '{prefabPath}'.");
                return swapped;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Los tres objetos de mano que nacieron con URP/Lit en el puño, en una sola pasada y sin
        /// rehornear mallas ni texturas. Idempotente. También es lo que corre el builder de cada
        /// uno al rehornear, así que después de esto y después de un re-bake el prefab queda igual.
        /// </summary>
        [MenuItem("Backrooms/Viewmodel/Rewarp held items (ADR-077)", false, 200)]
        public static void RewarpAll()
        {
            int done = 0;

            done += Rewarp(BackroomsScrewdriverModelApplier.WieldablePrefabPath, BackroomsScrewdriverModelApplier.NodeName,
                BackroomsScrewdriverModelApplier.MaterialPath, BackroomsScrewdriverModelApplier.FirstPersonMaterialPath,
                BackroomsScrewdriverModelApplier.MaskMapPath, "[ScrewdriverModel]");

            done += Rewarp(BackroomsSprayModelSwapper.PrefabPath, BackroomsSprayModelSwapper.NodeName,
                BackroomsSprayModelSwapper.MaterialPath, BackroomsSprayModelSwapper.FirstPersonMaterialPath,
                BackroomsSprayModelSwapper.MaskMapPath, "[SprayModel]");

            done += Rewarp(BackroomsCrankFlashlightModelApplier.WieldablePrefabPath, BackroomsCrankFlashlightModelApplier.NodeName,
                BackroomsCrankFlashlightModelApplier.MaterialPath, BackroomsCrankFlashlightModelApplier.FirstPersonMaterialPath,
                BackroomsCrankFlashlightModelApplier.MaskMapPath, "[CrankFlashlightModel]");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[ViewmodelMaterials] Rewarp: {done}/3 objetos de mano con material de primera persona.");
        }

        private static int Rewarp(string prefabPath, string nodeName, string worldMat, string fpMat, string maskMap, string tag)
        {
            var material = BuildFirstPerson(worldMat, fpMat, maskMap, tag);
            return ApplyToWieldable(prefabPath, nodeName, material, tag) > 0 ? 1 : 0;
        }
    }
}
#endif
