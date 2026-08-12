#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Generates the Backrooms surface textures procedurally (no external deps —
    /// pure Texture2D pixel writes), saves them as PNG assets under
    /// Resources/Textures, and wires them into the existing grid materials.
    /// Deterministic, así que volver a ejecutarlo es idempotente: suelo y techo con
    /// una semilla fija por textura, la pared con el hash entero de
    /// <see cref="WallpaperPattern"/>. La pared son DOS mapas —albedo y normal— y es
    /// el mapa de normales el que hace el trabajo visual.
    /// Menu: Backrooms/Generate Textures.
    /// </summary>
    public static class TextureGenerator
    {
        private const string ResourcesRoot = "Assets/Resources";
        private const string TextureFolder = "Assets/Resources/Textures";
        private const string MaterialFolder = "Assets/Resources/GridMaterials";
        private const int Size = 512;

        [MenuItem("Backrooms/Generate Textures")]
        public static void GenerateTextures()
        {
            EnsureFolders();

            // La pared sale de WallpaperPattern (motivo + relieve) y a 1024, no a 512:
            // el chevron de Level 0 mide 6 cm y a 512 sobre 2.5 m de tile no le quedan
            // píxeles para leerse como grabado. Suelo y techo siguen a 512.
            WallpaperPattern.Build(out byte[] wallAlbedo, out byte[] wallNormal);
            var wallpaper   = Bake(ToPixels(wallAlbedo), WallpaperPattern.Size, "WallpaperYellow.png", false);
            var wallpaperNM = Bake(ToPixels(wallNormal), WallpaperPattern.Size, "WallpaperYellow_Normal.png", true);
            var carpet      = Bake(BuildCarpetBeige(),  Size, "CarpetBeige.png",  false);
            var ceiling     = Bake(BuildCeilingTiles(), Size, "CeilingTiles.png", false);

            ApplyTexture($"{MaterialFolder}/GridWall.mat",    wallpaper, 2f, 2f, wallpaperNM);
            ApplyTexture($"{MaterialFolder}/GridFloor.mat",   carpet,    4f, 4f);
            ApplyTexture($"{MaterialFolder}/GridCeiling.mat", ceiling,   2f, 2f);

            // Los tres materiales CANON — los únicos que el mundo generado usa de verdad
            // (LayerVisualMaterials.Build copia estos; los Grid* de arriba solo sobreviven
            // como material de los prefabs y como fallback de textura). Si se dejaran fuera
            // de aquí, regenerar las texturas movería el aspecto de los prefabs y no el del
            // mundo, que es justo al revés de lo que se espera al ejecutar este menú.
            ApplyTexture($"{MaterialFolder}/M_Backrooms_Wall.mat",    wallpaper, 2f, 2f, wallpaperNM);
            ApplyTexture($"{MaterialFolder}/M_Backrooms_Floor.mat",   carpet,    4f, 4f);
            ApplyTexture($"{MaterialFolder}/M_Backrooms_Ceiling.mat", ceiling,   2f, 2f);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[TextureGenerator] Generated WallpaperYellow (+ _Normal) / CarpetBeige / " +
                      "CeilingTiles and applied them to GridWall/GridFloor/GridCeiling " +
                      "and to M_Backrooms_Wall/Floor/Ceiling.");
        }

        // ─── Texture 1: el papel pintado de pared ────────────────────────────
        //
        // El motivo, la paleta y el relieve viven en WallpaperPattern — un archivo sin
        // una sola referencia a Unity, para poder compilarlo suelto y comparar su salida
        // byte a byte antes de meter los PNG en el proyecto. Aquí solo se empaqueta.
        //
        // Historia: el rombo de argyle que había antes NO era el papel de Level 0 (ese
        // motivo es del Manila Room) y además se leía de lejos. Lo sustituye el galón
        // vertical, y el trabajo visual pasa del albedo al mapa de normales.
        private static Color32[] ToPixels(byte[] rgb)
        {
            var px = new Color32[rgb.Length / 3];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], 255);
            return px;
        }

        // ─── Texture 2: light beige office carpet, woven diagonal fibres ─────
        //
        // El nombre del fichero decía "beige" y el color era marrón oscuro (120/95/65 → con
        // el tinte de capa, albedo 0.13 en azul: gris carbón en pantalla). La moqueta de
        // oficina del canon es clara.
        //
        // Segundo ajuste, con la paleta base unificada: 150/255 dejaba el suelo un 24 % por
        // debajo de la pared (197), y el canon de Level 0 tiene la moqueta APENAS más oscura
        // que el papel, no en otra liga. Se sube la luminancia de fondo a 180/255 (−9 %
        // respecto a la pared) conservando la saturación, que sí debe ser mayor que la del
        // papel: 0.30 frente a 0.24.
        //
        // TERCER ajuste (2026-08-12): 184 de media seguía leyéndose MARRÓN al lado del papel
        // y en la referencia el suelo está casi tan claro como la pared. Sube a 194 de media
        // (3 puntos por debajo del papel, no 13) y la saturación a 0.32 — un punto por encima
        // de la del papel, que es lo que distingue moqueta de pared cuando las dos son
        // amarillas y no cuando una es marrón.
        private static Color32[] BuildCarpetBeige()
        {
            var rng = new System.Random(2002);
            var bg    = new Color32(205, 192, 138, 255);
            var fibre = new Color32(217, 203, 147, 255);

            // Thin diagonal fibres every 3 px; the diagonal flips direction every
            // 6 px band, giving an interlocked weave.
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                bool flip = (y / 6) % 2 == 1;
                for (int x = 0; x < Size; x++)
                {
                    int diag = flip ? (x - y) : (x + y);
                    int m = ((diag % 3) + 3) % 3;
                    Color32 c = m == 0 ? fibre : bg;
                    px[y * Size + x] = AddNoise(c, rng, 8);
                }
            }
            return px;
        }

        // ─── Texture 3: dirty-white drop-ceiling tiles ───────────────────────
        //
        // Dos correcciones, ambas hacia el canon:
        //
        // (1) FONDO de 220 a 203 de luminancia. "Igual o un punto más claro que la pared" es
        //     lo que pide Level 0; contra los 197 del fondo del papel, 220 son 23 puntos de
        //     escalón y el techo terminaba llamando la atención por encima de la pared. A 203
        //     la placa queda 6 puntos por encima: se lee como más clara sin separarse.
        //
        // (2) CONTRASTE de la rejilla. El punto de esquina caía a 120/255, un 47 % por debajo
        //     del fondo, y a 0,625 m de placa eso se lee como una mancha oscura por baldosa en
        //     vez de como una junta. Rejilla y punto pasan a 16 y 33 puntos: la placa se
        //     distingue de cerca y el techo es prácticamente liso a distancia.
        //
        // (3) CREMA, no blanco sucio (2026-08-12). La placa estaba a saturación 0.09, o sea
        //     casi neutra, y al lado de un papel amarillo eso lee gris. En la referencia el
        //     techo es la superficie más clara Y comparte el tono de la pared. Sube a 0.21 de
        //     saturación conservando la luminancia (204): crema claro, todavía menos saturado
        //     que el papel porque una placa de fibra mineral no es papel pintado.
        private static Color32[] BuildCeilingTiles()
        {
            var rng = new System.Random(3003);
            var bg     = new Color32(212, 205, 168, 255);
            var border = new Color32(196, 189, 153, 255);
            var dot    = new Color32(179, 173, 136, 255);

            // 128 px plates (4 across). 3 px grid line on the top/left of each
            // plate; a 4×4 dark dot at every plate corner. Interior carries faint
            // mineral-fibre noise.
            const int plate = 128;
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                int ly = y % plate;
                for (int x = 0; x < Size; x++)
                {
                    int lx = x % plate;
                    Color32 c;
                    if (lx < 4 && ly < 4)            c = dot;                   // corner dot
                    else if (lx < 3 || ly < 3)       c = border;                // plate edge
                    else                             c = AddNoise(bg, rng, 5);  // mineral fibre
                    px[y * Size + x] = c;
                }
            }
            return px;
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        private static Color32 AddNoise(Color32 c, System.Random rng, int amp)
        {
            int n = rng.Next(-amp, amp + 1);
            return new Color32(
                (byte)Mathf.Clamp(c.r + n, 0, 255),
                (byte)Mathf.Clamp(c.g + n, 0, 255),
                (byte)Mathf.Clamp(c.b + n, 0, 255),
                255);
        }

        private static Texture2D Bake(Color32[] pixels, int size, string fileName, bool normalMap)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply();

            string path = $"{TextureFolder}/{fileName}";
            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ConfigureImporter(path, size, normalMap);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void ConfigureImporter(string path, int size, bool normalMap)
        {
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer) return;
            importer.textureType   = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture   = !normalMap;
            importer.wrapMode      = TextureWrapMode.Repeat;
            importer.filterMode    = FilterMode.Bilinear;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = size;
            // BC7 y no BC1: el motivo del papel son 6 de 255 de luminancia, y BC1
            // cuantiza los extremos a 5-6-5 bits — un escalón así se pierde entero o
            // sale en bandas dentro del bloque. Lo mismo vale para el mapa de normales,
            // donde el bandeo se ve como facetas en luz rasante.
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
        }

        private static void ApplyTexture(string matPath, Texture2D tex, float tileX, float tileY,
                                         Texture2D normalMap = null)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                Debug.LogError($"[TextureGenerator] Material not found: {matPath}");
                return;
            }

            var scale = new Vector2(tileX, tileY);
            if (mat.HasProperty("_BaseMap")) { mat.SetTexture("_BaseMap", tex); mat.SetTextureScale("_BaseMap", scale); }
            if (mat.HasProperty("_MainTex")) { mat.SetTexture("_MainTex", tex); mat.SetTextureScale("_MainTex", scale); }

            // El relieve tilea con el albedo o el grabado se despega del motivo.
            if (normalMap != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normalMap);
                mat.SetTextureScale("_BumpMap", scale);
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
            }

            // Neutralise the existing colour tint so the texture reads true.
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color"))     mat.SetColor("_Color", Color.white);

            EditorUtility.SetDirty(mat);
        }

        private static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder(ResourcesRoot))
                AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(TextureFolder))
                AssetDatabase.CreateFolder(ResourcesRoot, "Textures");
        }
    }
}
#endif
