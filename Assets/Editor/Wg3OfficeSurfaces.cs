#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-105 enm. 19 — las TRES superficies que separan una oficina de un pasillo Backrooms:
    /// moqueta grafito fría, placa de falso techo de 60 con su perfil en T, y tela gris de mampara.
    ///
    /// Procedurales y deterministas como <see cref="TextureGenerator"/> (misma escuela: escritura
    /// directa de píxeles, semilla fija por textura, idempotente). Los PNG van a
    /// <c>Assets/Art/Wg3/Textures</c> —horneados en el repo, nunca apuntando a un import ignorado— y
    /// los materiales a <c>Assets/Resources/Wg3Materials</c>, que es de donde los carga
    /// <c>Wg3StyleMaterials</c> sin tocar ninguna escena ni ningún inspector.
    ///
    /// # Por qué cada material lleva mapa de normales aunque el relieve casi no se vea
    ///
    /// **Por el batcher.** Los cuatro materiales base de WG3 tienen <c>_NORMALMAP</c> activo, y el
    /// SRP Batcher agrupa por VARIANTE de shader: un material sin normal es otra variante y parte el
    /// lote. El relieve además existe —trama, junta y perfil— y sale gratis del mismo campo de
    /// alturas con el que se dibuja el albedo.
    ///
    /// # La escala no es decorativa
    ///
    /// Las UV se emiten en metros y <c>Wg3MeshBuilder.UvPerMetre</c> (0,5) las multiplica una sola
    /// vez, así que una textura con escala S se repite cada <c>2/S</c> metros. De ahí salen los
    /// números de abajo y NO se pueden redondear: 0,8333 son los 2,40 m que dan cuatro placas de 60.
    ///
    /// Menú: Backrooms/WG3/Generate Office Surfaces.
    /// </summary>
    public static class Wg3OfficeSurfaces
    {
        private const string ArtRoot = "Assets/Art";
        private const string ArtWg3 = "Assets/Art/Wg3";
        private const string TextureFolder = "Assets/Art/Wg3/Textures";
        private const string ResourcesRoot = "Assets/Resources";
        private const string MaterialFolder = "Assets/Resources/Wg3Materials";

        /// <summary>1024 y no 512: la placa mide 60 cm y su perfil en T 2, o sea un 3 % del lado.
        /// A 512 el perfil son cuatro píxeles y el mip lo borra a dos metros.</summary>
        private const int Size = 1024;

        /// <summary>Cuatro placas de 60 cm = 2,40 m de repetición. Ver la nota de escala.</summary>
        private const float CeilingScale = 0.8333f;
        /// <summary>Cuatro losetas de moqueta de 50 cm = 2,00 m.</summary>
        private const float CarpetScale = 1f;
        /// <summary>2,00 m de tela. Una mampara mide 1,40 de alto: no llega a repetir en vertical.</summary>
        private const float FabricScale = 1f;

        [MenuItem("Backrooms/WG3/Generate Office Surfaces")]
        public static void Generate()
        {
            EnsureFolders();

            BuildOfficeCarpet(out Color32[] carpet, out double[] carpetH);
            BuildCeilingTile(out Color32[] ceiling, out double[] ceilingH);
            BuildPartitionFabric(out Color32[] fabric, out double[] fabricH);

            Texture2D carpetTex = Bake(carpet, "Wg3_OfficeCarpet.png", false);
            Texture2D carpetNm = Bake(NormalFrom(carpetH, 2.2), "Wg3_OfficeCarpet_Normal.png", true);
            Texture2D ceilingTex = Bake(ceiling, "Wg3_OfficeCeiling.png", false);
            Texture2D ceilingNm = Bake(NormalFrom(ceilingH, 3.0), "Wg3_OfficeCeiling_Normal.png", true);
            Texture2D fabricTex = Bake(fabric, "Wg3_PartitionFabric.png", false);
            Texture2D fabricNm = Bake(NormalFrom(fabricH, 2.6), "Wg3_PartitionFabric_Normal.png", true);

            // El tinte de cada material replica el de su base (`Wg3_Floor` 0,21/0,25/0,19,
            // `Wg3_Ceiling` 0,8/0,8/0,77, `Wg3_Structure` 0,86/0,86/0,82) en LUMINANCIA efectiva,
            // que es lo que la iluminación validada de la 23.ª tanda da por bueno. Lo que cambia es
            // el tono y el motivo, no cuánta luz devuelve la superficie.
            Write($"{MaterialFolder}/Wg3_FloorOffice.mat", carpetTex, carpetNm, CarpetScale,
                  new Color(0.42f, 0.44f, 0.50f, 1f), 0.03f);
            Write($"{MaterialFolder}/Wg3_CeilingOffice.mat", ceilingTex, ceilingNm, CeilingScale,
                  new Color(0.80f, 0.80f, 0.77f, 1f), 0.04f);
            // La mampara NO copia el tinte cálido de `Wg3_Structure` (0,86/0,86/0,82), y tampoco vale
            // uno neutro: **la luz de la escena es cálida, así que un albedo gris sale caqui**. Un
            // gris de verdad hay que fabricarlo CONTRA la luz, hundiendo el rojo y subiendo el azul
            // hasta que el producto de los dos vuelva a ser plano. El primer intento (0,82/0,83/0,85)
            // era neutro sobre el papel y en la captura seguía siendo del color de la pared.
            //
            // La luminancia se conserva —la media sigue en 0,81—, así que esto no toca el balance de
            // luz que Joel dio por bueno: sólo gira el tono.
            Write($"{MaterialFolder}/Wg3_Partition.mat", fabricTex, fabricNm, FabricScale,
                  new Color(0.72f, 0.79f, 0.93f, 1f), 0.05f);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[wg3] superficies de oficina generadas: moqueta grafito, placa de 60 y tela de mampara " +
                      $"en {TextureFolder}, materiales en {MaterialFolder}.");
        }

        // ─── 1. Moqueta de oficina: grafito FRÍO ──────────────────────────────
        //
        // Se separa de la moqueta Backrooms por TONO y no por claridad, que es la regla medida de
        // `Wg3StyleMaterials`: el suelo de circulación tira a oliva (0,21/0,25/0,19 sobre terrazo) y
        // éste a azul-gris. Un delta de albedo no habría existido.
        //
        // Loseta de 50 cm con la trama girada un cuarto en tablero de ajedrez, que es cómo se pone
        // una moqueta modular de verdad y lo que hace que 2 m de repetición no se lean como
        // repetición: la junta manda sobre el motivo.
        private static void BuildOfficeCarpet(out Color32[] px, out double[] height)
        {
            var rng = new System.Random(4004);
            var bg = new Color32(150, 153, 160, 255);
            var fibre = new Color32(162, 165, 173, 255);
            var seam = new Color32(132, 135, 142, 255);

            const int tile = 256;   // 50 cm
            px = new Color32[Size * Size];
            height = new double[Size * Size];

            for (int y = 0; y < Size; y++)
            {
                int ty = y / tile, ly = y % tile;
                for (int x = 0; x < Size; x++)
                {
                    int tx = x / tile, lx = x % tile;
                    int i = y * Size + x;

                    // La junta entre losetas: dos píxeles hundidos. Es lo único que sobrevive a la
                    // distancia, así que es lo que dice «oficina» desde el otro lado de la sala.
                    if (lx < 2 || ly < 2)
                    {
                        px[i] = AddNoise(seam, rng, 4);
                        height[i] = -0.55;
                        continue;
                    }

                    // Bucle de pelo corto: rayas de tres píxeles, giradas un cuarto en las losetas
                    // impares del tablero.
                    bool quarter = ((tx + ty) & 1) == 1;
                    int run = quarter ? ly : lx;
                    int m = ((run % 3) + 3) % 3;
                    Color32 c = m == 0 ? fibre : bg;
                    px[i] = AddNoise(c, rng, 7);
                    height[i] = (m == 0 ? 0.35 : 0.0) + (Hash01(x, y, 4101) - 0.5) * 0.30;
                }
            }
        }

        // ─── 2. Placa de falso techo, 60 × 60 con perfil en T ─────────────────
        //
        // Lo que faltaba: el falso techo de ADR-105 enm. 18 bajaba la cota y se quedaba con la placa
        // crema de Backrooms, que no tiene rejilla metálica. Aquí la rejilla es lo primero que se ve
        // —un perfil CLARO sobre placa media, 2 cm de ancho— y la placa lleva su perforación de
        // fibra mineral, que es lo que la distingue de un plano liso al mirarla de cerca.
        //
        // Dos de las dieciséis placas van manchadas de gotera. Sin eso el techo es una rejilla
        // perfecta y ninguna oficina abandonada la tiene.
        private static void BuildCeilingTile(out Color32[] px, out double[] height)
        {
            var rng = new System.Random(5005);
            var plate = new Color32(212, 208, 196, 255);
            // **El perfil va MÁS OSCURO que la placa, y es una corrección medida.** El primer intento
            // lo puso claro (236) con una ranura oscura a cada lado, y a dos metros el mip promedia
            // los tres y la rejilla desaparece: la captura del falso techo salía con una cuadrícula
            // que había que buscar. Una raya oscura limpia con un labio claro al lado sobrevive al
            // mip porque el promedio ya no es el color de la placa.
            var rail = new Color32(186, 183, 174, 255);
            var lip = new Color32(232, 230, 222, 255);
            var hole = new Color32(196, 193, 182, 255);

            const int cell = 256;   // 60 cm
            const int railW = 9;    // ≈ 2,1 cm
            px = new Color32[Size * Size];
            height = new double[Size * Size];

            for (int y = 0; y < Size; y++)
            {
                int py = y / cell, ly = y % cell;
                for (int x = 0; x < Size; x++)
                {
                    int pxi = x / cell, lx = x % cell;
                    int i = y * Size + x;

                    if (lx < railW || ly < railW)
                    {
                        px[i] = AddNoise(rail, rng, 3);
                        height[i] = 1.0;
                        continue;
                    }
                    // El labio de la placa contra el perfil: el canto biselado que coge la luz. Es la
                    // mitad clara del par que hace legible la rejilla.
                    if (lx < railW + 3 || ly < railW + 3)
                    {
                        px[i] = AddNoise(lip, rng, 3);
                        height[i] = 0.70;
                        continue;
                    }

                    // Perforación de fibra mineral: un punto cada 13 px con salto por hash, para que
                    // no forme rejilla propia.
                    int hx = (lx + (int)(Hash01(lx / 13, ly / 13, 5101) * 5)) % 13;
                    int hy = (ly + (int)(Hash01(lx / 13, ly / 13, 5102) * 5)) % 13;
                    bool pin = hx < 2 && hy < 2;

                    Color32 c = pin ? hole : plate;
                    double h = pin ? 0.25 : 0.55;

                    // Gotera: dos placas de las dieciséis, con caída radial desde un punto de la
                    // placa. Amarillea y oscurece, que es lo que hace una filtración.
                    double stain = StainAt(pxi, py, lx, ly, cell);
                    if (stain > 0.0)
                    {
                        c = new Color32(
                            (byte)Mathf.Clamp(c.r - (int)(38 * stain), 0, 255),
                            (byte)Mathf.Clamp(c.g - (int)(46 * stain), 0, 255),
                            (byte)Mathf.Clamp(c.b - (int)(66 * stain), 0, 255),
                            255);
                    }

                    px[i] = AddNoise(c, rng, 4);
                    height[i] = h + (Hash01(x, y, 5103) - 0.5) * 0.20;
                }
            }
        }

        /// <summary>Cuánto mancha la gotera en este píxel, 0 fuera de las dos placas manchadas.</summary>
        private static double StainAt(int plateX, int plateY, int lx, int ly, int cell)
        {
            // Dos placas fijas de la rejilla de 4 × 4. Fijas y no sorteadas: la textura tiene que ser
            // idéntica en cada ejecución, y con dos manchas ya no hay rejilla perfecta.
            double cx, cy, radius;
            if (plateX == 1 && plateY == 2) { cx = 0.35; cy = 0.55; radius = 0.42; }
            else if (plateX == 3 && plateY == 0) { cx = 0.70; cy = 0.30; radius = 0.30; }
            else return 0.0;

            double dx = (double)lx / cell - cx;
            double dy = (double)ly / cell - cy;
            double d = Math.Sqrt(dx * dx + dy * dy) / radius;
            if (d >= 1.0) return 0.0;
            return (1.0 - d) * (1.0 - d);
        }

        // ─── 3. Tela de mampara: gris ─────────────────────────────────────────
        //
        // Trama de cuatro píxeles alternando urdimbre y trama, con el hilo variando de tono. No lleva
        // junta ni motivo: lo que separa una mampara de la pared es que la pared es GOTELÉ —grano
        // grueso e irregular— y esto es tejido, regular y fino.
        private static void BuildPartitionFabric(out Color32[] px, out double[] height)
        {
            var rng = new System.Random(6006);
            var warp = new Color32(150, 150, 148, 255);
            var weft = new Color32(138, 138, 137, 255);

            const int thread = 4;
            px = new Color32[Size * Size];
            height = new double[Size * Size];

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    int tx = x / thread, ty = y / thread;
                    bool over = ((tx + ty) & 1) == 0;

                    // Cada hilo tiene su tono: sin esto la trama es un damero y se lee como cuadros.
                    double shade = (Hash01(over ? tx : ty, 0, 6101) - 0.5) * 12.0;
                    Color32 c = over ? warp : weft;
                    c = new Color32(
                        (byte)Mathf.Clamp(c.r + (int)shade, 0, 255),
                        (byte)Mathf.Clamp(c.g + (int)shade, 0, 255),
                        (byte)Mathf.Clamp(c.b + (int)shade, 0, 255),
                        255);

                    // La panza del hilo: alto en el centro del hilo, bajo en el cruce.
                    int inThread = (over ? y : x) % thread;
                    double bulge = 1.0 - Math.Abs(inThread - 1.5) / 1.5;
                    px[i] = AddNoise(c, rng, 5);
                    height[i] = (over ? 0.55 : 0.15) + bulge * 0.35;
                }
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        /// <summary>Normal en espacio tangente desde el campo de alturas, con la MISMA fórmula que
        /// <c>WallpaperPattern</c>: <c>normalize(−dh/du, −dh/dv, 1)</c>, verde hacia arriba.</summary>
        private static Color32[] NormalFrom(double[] h, double strength)
        {
            var px = new Color32[h.Length];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    double dhx = (h[At(x + 1, y)] - h[At(x - 1, y)]) * 0.5;
                    double dhy = (h[At(x, y + 1)] - h[At(x, y - 1)]) * 0.5;
                    double nx = -dhx * strength, ny = -dhy * strength, nz = 1.0;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    px[i] = new Color32(
                        ToByte((nx / len * 0.5 + 0.5) * 255.0),
                        ToByte((ny / len * 0.5 + 0.5) * 255.0),
                        ToByte((nz / len * 0.5 + 0.5) * 255.0),
                        255);
                }
            }
            return px;
        }

        private static int At(int x, int y)
        {
            x = ((x % Size) + Size) % Size;
            y = ((y % Size) + Size) % Size;
            return y * Size + x;
        }

        private static byte ToByte(double v) => (byte)Mathf.Clamp((int)Math.Round(v), 0, 255);

        private static double Hash01(int a, int b, int salt)
        {
            unchecked
            {
                uint h = (uint)a * 2654435761u ^ (uint)b * 2246822519u ^ (uint)salt * 3266489917u;
                h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
                return h / 4294967296.0;
            }
        }

        private static Color32 AddNoise(Color32 c, System.Random rng, int amp)
        {
            int n = rng.Next(-amp, amp + 1);
            return new Color32(
                (byte)Mathf.Clamp(c.r + n, 0, 255),
                (byte)Mathf.Clamp(c.g + n, 0, 255),
                (byte)Mathf.Clamp(c.b + n, 0, 255),
                255);
        }

        private static Texture2D Bake(Color32[] pixels, string fileName, bool normalMap)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply();

            string path = $"{TextureFolder}/{fileName}";
            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), path), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = !normalMap;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Bilinear;
                importer.mipmapEnabled = true;
                importer.maxTextureSize = Size;
                // BC7 por lo mismo que en `TextureGenerator`: el perfil de la placa son 24 puntos de
                // luminancia sobre la placa y BC1 los cuantiza a bandas dentro del bloque.
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void Write(string path, Texture2D baseMap, Texture2D normal, float scale,
                                 Color tint, float smoothness)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null)
            {
                Debug.LogError("[wg3] no está el shader URP/Lit: ¿pipeline sin instalar?");
                return;
            }
            if (mat == null)
            {
                mat = new Material(lit);
                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                mat.shader = lit;
            }

            var s = new Vector2(scale, scale);
            mat.SetTexture("_BaseMap", baseMap);
            mat.SetTextureScale("_BaseMap", s);
            mat.SetTexture("_BumpMap", normal);
            mat.SetTextureScale("_BumpMap", s);
            mat.SetFloat("_BumpScale", 1f);
            // `_NORMALMAP` lo activa el inspector de URP al asignar el mapa a mano; creando el
            // material por script hay que ponerlo, o el shader compila la variante SIN normal y el
            // material entra en otro lote del batcher que los cuatro base.
            mat.EnableKeyword("_NORMALMAP");
            mat.SetColor("_BaseColor", tint);
            mat.SetColor("_Color", tint);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(mat);
        }

        private static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder(ArtRoot)) AssetDatabase.CreateFolder("Assets", "Art");
            if (!AssetDatabase.IsValidFolder(ArtWg3)) AssetDatabase.CreateFolder(ArtRoot, "Wg3");
            if (!AssetDatabase.IsValidFolder(TextureFolder)) AssetDatabase.CreateFolder(ArtWg3, "Textures");
            if (!AssetDatabase.IsValidFolder(ResourcesRoot)) AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(MaterialFolder)) AssetDatabase.CreateFolder(ResourcesRoot, "Wg3Materials");
        }
    }
}
#endif
