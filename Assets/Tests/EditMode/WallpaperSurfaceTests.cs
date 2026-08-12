using System.IO;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El papel pintado de PARED — los dos PNG que produce
    /// <c>Backrooms/Generate Textures</c> y cómo llegan al material de mundo.
    ///
    /// Por qué existe: el motivo del papel de Level 0 se distingue SOLO por diferencia
    /// de valor, y el fallo típico al regenerarlo es subir ese contraste hasta que el
    /// patrón se lee a diez metros — momento en el que el pasillo deja de parecer un
    /// sitio real y parece una textura. Ese fallo no rompe nada y no se ve en ningún
    /// log: solo se ve en una captura, y solo si alguien la mira. De ahí las guardas.
    ///
    /// Se leen los PNG del disco y no el <c>Texture2D</c> importado porque el asset se
    /// importa con <c>isReadable = 0</c>; los bytes del archivo son además la fuente
    /// exacta, sin la compresión de la plataforma por medio.
    ///
    /// NO comprueban luz, exposición ni post-proceso — igual que
    /// <see cref="SurfacePaletteTests"/>, eso se valida con captura.
    /// </summary>
    [TestFixture]
    public class WallpaperSurfaceTests
    {
        private const string Albedo = "Resources/Textures/WallpaperYellow.png";
        private const string Normal = "Resources/Textures/WallpaperYellow_Normal.png";
        private const string Carpet = "Resources/Textures/CarpetBeige.png";
        private const string Ceiling = "Resources/Textures/CeilingTiles.png";

        private static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

        private static Color32[] Load(string relative, out int width, out int height)
        {
            string path = Path.Combine(Application.dataPath, relative);
            Assert.IsTrue(File.Exists(path), $"falta {relative} — lo genera Backrooms/Generate Textures");

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Assert.IsTrue(tex.LoadImage(File.ReadAllBytes(path)), $"{relative} no decodifica como PNG");
            width = tex.width; height = tex.height;
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return px;
        }

        private static Vector3 MeanRgb(Color32[] px)
        {
            double r = 0, g = 0, b = 0;
            foreach (var c in px) { r += c.r; g += c.g; b += c.b; }
            return new Vector3((float)(r / px.Length), (float)(g / px.Length), (float)(b / px.Length));
        }

        private static float MeanLuma(string relative)
        {
            var m = MeanRgb(Load(relative, out _, out _));
            return Luma(m.x, m.y, m.z);
        }

        /// <summary>
        /// El papel de Level 0 ES amarillo pálido. La luz solo lo intensifica.
        ///
        /// Esta guarda existe por las DOS formas de equivocarse, y la segunda ya pasó: la
        /// iteración del 2026-08-12 leyó el canon como "el color real es beige amarronado
        /// y el amarillo lo pone la iluminación", bajó la saturación a 0.14 —gris topo— y
        /// para compensar tuvo que subir la lámpara a un amarillo casi neón. El reparto
        /// quedó invertido respecto al canon (emisores saturados sobre superficies
        /// neutras) y el mundo salió con techo verde oliva y paredes marrones. Por eso el
        /// rango de saturación tiene SUELO además de techo: por debajo se pierde el
        /// amarillo del sitio y alguien lo devuelve por la vía de la luz, que es la que
        /// no toca.
        /// </summary>
        [Test]
        public void TheWallpaperIsPaleYellowAtTheHeightOfFloorAndCeiling()
        {
            var m = MeanRgb(Load(Albedo, out int w, out int h));
            Assert.AreEqual(1024, w, "el tile de pared es de 1024 — a 512 el chevron no tiene píxeles");
            Assert.AreEqual(1024, h);

            float sat = (Mathf.Max(m.x, m.y, m.z) - Mathf.Min(m.x, m.y, m.z)) / Mathf.Max(m.x, m.y, m.z);
            Assert.GreaterOrEqual(sat, 0.22f,
                $"saturación {sat:F3}: por debajo el papel aislado ya no se lee amarillo y el " +
                "sitio pierde su color, que es lo que hay que arreglar en el ALBEDO");
            Assert.LessOrEqual(sat, 0.36f,
                $"saturación {sat:F3}: la referencia está fotografiada bajo luz cálida — el " +
                "albedo va algo más apagado que la foto, no igual ni por encima");

            // Amarillo: R y G altos y juntos, azul muy por debajo. Es la firma que separa
            // amarillo de beige amarronado (donde R−G se acerca a G−B) y de verde (G > R).
            Assert.Greater(m.x, m.y, "media R ≤ G — el papel viraría a verde");
            Assert.Greater(m.y - m.z, 3f * (m.x - m.y),
                $"R−G {m.x - m.y:F1} contra G−B {m.y - m.z:F1}: con el rojo despegado del verde " +
                "esto es beige amarronado, no amarillo");

            // La paleta base es una sola: la pared no puede escaparse por encima del
            // techo ni por debajo de la moqueta. Banda estrecha = el sitio uniformemente
            // iluminado del canon, sin una superficie que llame la atención sobre otra.
            float wall = Luma(m.x, m.y, m.z), floor = MeanLuma(Carpet), ceiling = MeanLuma(Ceiling);
            Assert.That(wall, Is.InRange(floor - 2f, ceiling + 2f),
                $"luminancia de pared {wall:F1} fuera de la banda suelo {floor:F1} … techo {ceiling:F1}");
            Assert.LessOrEqual(ceiling - floor, 20f,
                $"suelo {floor:F1} y techo {ceiling:F1} a {ceiling - floor:F1} puntos — en la " +
                "referencia las tres superficies caben en un rango estrecho");
        }

        /// <summary>
        /// Las tres superficies son la misma familia de color. Es lo que impide volver al
        /// estado del que venimos, donde el suelo estaba 13 puntos por debajo de la pared
        /// y el techo era casi neutro: al lado de un papel amarillo, eso lee marrón y gris.
        /// </summary>
        [Test]
        public void FloorAndCeilingShareTheWallHue()
        {
            var wall = MeanRgb(Load(Albedo, out _, out _));
            var floor = MeanRgb(Load(Carpet, out _, out _));
            var ceiling = MeanRgb(Load(Ceiling, out _, out _));

            float hw = Hue(wall), hf = Hue(floor), hc = Hue(ceiling);
            Assert.That(hw, Is.InRange(45f, 58f), $"tono de pared {hw:F1}° — fuera de esto no es amarillo");
            Assert.LessOrEqual(Mathf.Abs(hf - hw), 8f,
                $"tono de suelo {hf:F1}° contra pared {hw:F1}°: la moqueta es más sucia, no de otro color");
            Assert.LessOrEqual(Mathf.Abs(hc - hw), 8f,
                $"tono de techo {hc:F1}° contra pared {hw:F1}°: crema, no blanco sucio");

            Assert.Greater(Sat(floor), Sat(wall),
                "la moqueta lleva un punto más de saturación que el papel — es lo que las " +
                "distingue cuando las dos son amarillas");
            Assert.Less(Sat(ceiling), Sat(wall),
                "una placa de fibra mineral no está impresa: menos saturada que el papel");
        }

        /// <summary>Tono en grados, con el rojo como canal máximo (que es el caso de las
        /// tres superficies). 60° es amarillo puro; por debajo tira a naranja.</summary>
        private static float Hue(Vector3 c)
        {
            float mx = Mathf.Max(c.x, c.y, c.z), mn = Mathf.Min(c.x, c.y, c.z);
            return mx > mn ? 60f * (c.y - c.z) / (mx - mn) : 0f;
        }

        private static float Sat(Vector3 c)
        {
            float mx = Mathf.Max(c.x, c.y, c.z);
            return (mx - Mathf.Min(c.x, c.y, c.z)) / mx;
        }

        /// <summary>La prueba de los diez metros. Un promedio de 8×8 es lo que el mip
        /// entrega a esa distancia (el tile cubre 2.5 m × 2.0 m); si el motivo sigue
        /// teniendo recorrido ahí, se lee de lejos y está mal.</summary>
        [Test]
        public void TheChevronIsAValueDifferenceOnlyAndDiesWithDistance()
        {
            var px = Load(Albedo, out int w, out int h);

            const int box = 8;
            float min = float.MaxValue, max = float.MinValue;
            for (int by = 0; by < h; by += box)
            {
                for (int bx = 0; bx < w; bx += box)
                {
                    float r = 0, g = 0, b = 0;
                    for (int y = by; y < by + box; y++)
                        for (int x = bx; x < bx + box; x++)
                        {
                            var c = px[y * w + x];
                            r += c.r; g += c.g; b += c.b;
                        }
                    float n = box * box;
                    float l = Luma(r / n, g / n, b / n);
                    min = Mathf.Min(min, l); max = Mathf.Max(max, l);
                }
            }

            Assert.LessOrEqual(max - min, 12f,
                $"recorrido de luminancia {max - min:F1}/255 tras promediar 8×8 — a diez metros " +
                "el motivo debe haber desaparecido, no dibujarse");
            Assert.Greater(max - min, 2f,
                "el papel quedó liso del todo: sin motivo ni mancha no es papel viejo, es un plano");
        }

        /// <summary>El relieve es el canal que hace el trabajo visual, así que tiene que
        /// existir — y tiene que ser de PAPEL: inclinaciones de pocos grados, no un
        /// muro de piedra.</summary>
        [Test]
        public void TheNormalMapCarriesTheEmbossAndIsImportedAsANormalMap()
        {
            var px = Load(Normal, out int w, out int h);
            Assert.AreEqual(1024, w, "el relieve tilea con el albedo, mismo tamaño");

            double z = 0; int tilted = 0; float minZ = 1f;
            foreach (var c in px)
            {
                float nx = c.r / 255f * 2f - 1f, ny = c.g / 255f * 2f - 1f, nz = c.b / 255f * 2f - 1f;
                z += nz;
                minZ = Mathf.Min(minZ, nz);
                if (Mathf.Sqrt(nx * nx + ny * ny) > 0.03f) tilted++;
            }
            float meanZ = (float)(z / px.Length), frac = tilted / (float)px.Length;

            Assert.Greater(frac, 0.20f,
                $"solo el {frac:P0} de los téxeles tiene inclinación — sin grabado el papel " +
                "no aparece en luz rasante y el albedo se queda solo");
            Assert.GreaterOrEqual(meanZ, 0.97f,
                $"normal media z={meanZ:F3}: demasiado relieve para papel pintado");
            Assert.GreaterOrEqual(minZ, 0.85f,
                $"z mínima {minZ:F3} — una pendiente así es piedra, no un grabado de papel");

            // Un mapa de normales importado como textura normal (sRGB, sin swizzle) se
            // ve como una pared sucia de azul y no lanza ningún error.
            string meta = File.ReadAllText(Path.Combine(Application.dataPath, Normal + ".meta"));
            StringAssert.Contains("textureType: 1", meta, "el PNG de normales no está importado como Normal map");
            StringAssert.Contains("sRGBTexture: 0", meta, "un mapa de normales en sRGB deforma la iluminación");
        }

        /// <summary>Lo que de verdad se ve en el mundo es el material canon, no el PNG:
        /// el relieve tiene que estar asignado y tilear EXACTAMENTE como el albedo, o el
        /// grabado se despega del motivo.</summary>
        [Test]
        public void TheCanonWallMaterialWiresTheEmbossToTheSameTiling()
        {
            var mat = Resources.Load<Material>(LayerVisualMaterials.WallMaterialResource);
            Assert.IsNotNull(mat, "M_Backrooms_Wall no aparece por Resources");

            Assert.IsTrue(mat.HasProperty("_BumpMap"), $"{mat.shader.name} no expone _BumpMap");
            Assert.IsNotNull(mat.GetTexture("_BumpMap"),
                "sin mapa de normales el papel solo tiene el albedo, que a propósito casi no se ve");

            Assert.AreEqual(mat.GetTextureScale("_BaseMap"), mat.GetTextureScale("_BumpMap"),
                "albedo y relieve con tiling distinto: el grabado deja de coincidir con el motivo");

            // 2 repeticiones sobre el panel de 5 m × 4 m = 2.5 m × 2.0 m por tile, que es
            // la escala en la que la celda de 64 px mide 15.6 cm. Cambiar esto reescala
            // el papel pintado entero.
            Assert.AreEqual(new Vector2(2f, 2f), mat.GetTextureScale("_BaseMap"),
                "el tiling de la pared fija el tamaño físico del motivo");
        }
    }
}
