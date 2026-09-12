#if UNITY_EDITOR
using System.IO;
using BackroomsSurvival.WorldGen3;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Hornea el papel de <see cref="Wg3WallpaperPattern"/> a PNG y viste con él
    /// <c>Assets/Materials/WorldGen3/Wg3_Structure.mat</c>, el material de ESTRUCTURA que el
    /// streamer tiene asignado en la escena de juego. Es el mismo asset, así que ni la escena ni
    /// el prefab cambian: sólo lo que hay dentro del material.
    ///
    /// Lo que se toca del material: los cuatro mapas y sus escalas, las tres palabras clave y la
    /// fuerza de normal/oclusión. Lo que NO se toca: <c>_BaseColor</c> (el crema que tiñe cada
    /// papel) y <c>_Smoothness</c> —con máscara puesta URP lee la suavidad del alfa de la máscara.
    ///
    /// Menú: Backrooms/WG3/Generate Wallpaper Surface. Para verlo sin abrir el editor,
    /// <see cref="GenerateWithSwatches"/> fotografía la pared antes y después a
    /// <c>Temp/captures/wall_before.png</c> y <c>wall_after.png</c> con la misma lámpara que WG3.
    /// </summary>
    public static class Wg3WallpaperSurface
    {
        private const string TextureFolder = "Assets/Art/Wg3/Textures";
        private const string MaterialPath = "Assets/Materials/WorldGen3/Wg3_Structure.mat";

        private const string AlbedoName = "Wg3_Wallpaper.png";
        private const string NormalName = "Wg3_Wallpaper_Normal.png";
        private const string MaskName = "Wg3_Wallpaper_Mask.png";

        [MenuItem("Backrooms/WG3/Generate Wallpaper Surface")]
        public static void Generate()
        {
            if (!AssetDatabase.IsValidFolder(TextureFolder))
            {
                Debug.LogError($"[wg3] no existe {TextureFolder}: ejecuta antes Backrooms/WG3/Generate Office Surfaces");
                return;
            }
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                Debug.LogError($"[wg3] no está {MaterialPath}");
                return;
            }

            Wg3WallpaperPattern.Build(out byte[] albedo, out byte[] normal, out byte[] mask);
            Texture2D albedoTex = Bake(albedo, 3, AlbedoName, TextureImporterType.Default, true);
            Texture2D normalTex = Bake(normal, 3, NormalName, TextureImporterType.NormalMap, false);
            Texture2D maskTex = Bake(mask, 4, MaskName, TextureImporterType.Default, false);

            var s = new Vector2(Wg3WallpaperPattern.MaterialScale, Wg3WallpaperPattern.MaterialScale);
            mat.SetTexture("_BaseMap", albedoTex);
            mat.SetTextureScale("_BaseMap", s);
            mat.SetTexture("_BumpMap", normalTex);
            mat.SetTextureScale("_BumpMap", s);
            mat.SetFloat("_BumpScale", 1f);
            // Una sola textura para las dos ranuras: URP/Lit toma metal (R) y suavidad (A) de
            // `_MetallicGlossMap` y la oclusión (G) de `_OcclusionMap`.
            mat.SetTexture("_MetallicGlossMap", maskTex);
            mat.SetTextureScale("_MetallicGlossMap", s);
            mat.SetTexture("_OcclusionMap", maskTex);
            mat.SetTextureScale("_OcclusionMap", s);
            mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f); // 0 = alfa del mapa metálico
            // Las palabras clave las pone el inspector al asignar a mano; por script hay que
            // ponerlas, o el shader compila la variante sin mapas y no se ve ninguno.
            mat.EnableKeyword("_NORMALMAP");
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.EnableKeyword("_OCCLUSIONMAP");
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[wg3] papel generado en {TextureFolder} y aplicado a {MaterialPath} " +
                      $"(repite cada {Wg3WallpaperPattern.RepeatM} m).");
        }

        /// <summary>
        /// Para `-executeMethod` sin abrir el editor: genera y deja la muestra en
        /// <c>Builds/Captures/wallpaper/wall_swatch.png</c>. Un «antes» clonando el material con
        /// los mapas del yeso salía MAGENTA en batch (el clon pierde la variante), así que no hay
        /// antes: la referencia son las capturas de juego previas al cambio.
        /// </summary>
        public static void GenerateWithSwatches()
        {
            Generate();
            SwatchFromMenu();
        }

        /// <summary>Fuera de <c>Temp/</c> a propósito: Unity en batch la vacía al salir.</summary>
        private const string CaptureFolder = "Builds/Captures/wallpaper";

        /// <summary>
        /// Una pared de 4 × 3 m con el material dado, suelo, el plafón de WG3 (puntual 3,1 a
        /// 3 500 K, alcance 11 m) a 2,7 m y una cámara a 1,6 m mirando en diagonal, que es como se
        /// ve una pared en juego. Sin ambiente, como en WG3. A <c>Builds/Captures/wallpaper/&lt;name&gt;.png</c>.
        /// </summary>
        [MenuItem("Backrooms/WG3/Wallpaper Swatch")]
        public static void SwatchFromMenu() =>
            Swatch("wall_swatch", AssetDatabase.LoadAssetAtPath<Material>(MaterialPath));

        public static void Swatch(string name, Material mat)
        {
            if (mat == null) { Debug.LogError("[wg3] muestra sin material"); return; }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("[WallSwatch]");
            try
            {
                Color savedAmbient = RenderSettings.ambientLight;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = Color.black;
                RenderSettings.fog = false;

                GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Quad);
                wall.name = "Wall";
                wall.transform.SetParent(root.transform, false);
                wall.transform.localScale = new Vector3(4f, 3f, 1f);
                // El Quad de Unity mira hacia −Z de fábrica, que es donde está la cámara: sin girar.
                // Girado 180° la cámara ve la cara trasera, se descarta y la muestra sale sin pared.
                wall.transform.position = new Vector3(0f, 1.5f, 0f);
                var wallR = wall.GetComponent<Renderer>();
                wallR.sharedMaterial = mat;
                wallR.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

                GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Quad);
                floor.name = "Floor";
                floor.transform.SetParent(root.transform, false);
                floor.transform.localScale = new Vector3(4f, 4f, 1f);
                floor.transform.position = new Vector3(0f, 0f, -2f);
                floor.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                var floorR = floor.GetComponent<Renderer>();
                floorR.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/WorldGen3/Wg3_Floor.mat");
                floorR.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

                var lampGo = new GameObject("Lamp");
                lampGo.transform.SetParent(root.transform, false);
                lampGo.transform.position = new Vector3(0.6f, 2.7f, -1.2f);
                var lamp = lampGo.AddComponent<Light>();
                lamp.type = LightType.Point;
                lamp.intensity = 3.1f;
                lamp.range = 11f;
                lamp.color = new Color(1f, 0.96f, 0.78f);
                lamp.shadows = LightShadows.Soft;
                lamp.shadowStrength = 0.72f;

                var camGo = new GameObject("Cam");
                camGo.transform.SetParent(root.transform, false);
                camGo.transform.position = new Vector3(-1.4f, 1.6f, -2.2f);
                camGo.transform.LookAt(new Vector3(0.6f, 1.4f, 0f));
                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = 70f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 30f;

                Directory.CreateDirectory(CaptureFolder);
                RenderTo(cam, Path.Combine(CaptureFolder, name + ".png"));
                RenderSettings.ambientLight = savedAmbient;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            Debug.Log($"[wg3] muestra de pared en {CaptureFolder}/{name}.png");
        }

        private static void RenderTo(Camera cam, string path)
        {
            const int width = 1280, height = 720;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            RenderTexture previous = RenderTexture.active;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        private static Texture2D Bake(byte[] data, int channels, string fileName,
                                      TextureImporterType type, bool srgb)
        {
            int size = Wg3WallpaperPattern.Size;
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++)
            {
                byte r = data[i * channels], g = data[i * channels + 1], b = data[i * channels + 2];
                byte a = channels == 4 ? data[i * channels + 3] : (byte)255;
                px[i] = new Color32(r, g, b, a);
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();

            string path = $"{TextureFolder}/{fileName}";
            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = type;
                importer.sRGBTexture = srgb;
                importer.alphaIsTransparency = false;
                importer.alphaSource = channels == 4 ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 8; // la pared se ve casi siempre a rasante
                importer.mipmapEnabled = true;
                importer.maxTextureSize = size;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
#endif
