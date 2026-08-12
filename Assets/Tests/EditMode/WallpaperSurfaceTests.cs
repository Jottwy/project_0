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

        /// <summary>
        /// El papel de Level 0 ES amarillo verdoso y saturado. La luz solo lo intensifica.
        ///
        /// Esta guarda existe por las DOS formas de equivocarse, y las dos ya pasaron. (1)
        /// Se leyó el canon como "el color real es beige amarronado y el amarillo lo pone
        /// la iluminación", el albedo bajó a saturación 0.14 —gris topo— y para compensar
        /// la lámpara subió a un amarillo casi neón: reparto invertido, techo verde oliva
        /// y paredes marrones. (2) Al corregirlo se puso el albedo en 0.28 razonando que
        /// "la foto de referencia está bajo luz cálida, así que el albedo va más apagado",
        /// y eso siguió siendo demasiado pálido. En el frame de referencia los paneles
        /// fluorescentes son BLANCOS, casi fríos: el amarillo verdoso no lo puede estar
        /// poniendo la luz, vive aquí. Por eso el rango tiene SUELO además de techo — por
        /// debajo, alguien devuelve el color por la vía de la luz, que es la que no toca.
        /// </summary>
        [Test]
        public void TheWallpaperIsSaturatedGreenYellow()
        {
            var m = MeanRgb(Load(Albedo, out int w, out int h));
            Assert.AreEqual(1024, w, "el tile de pared es de 1024 — a 512 el chevron no tiene píxeles");
            Assert.AreEqual(1024, h);

            float sat = (Mathf.Max(m.x, m.y, m.z) - Mathf.Min(m.x, m.y, m.z)) / Mathf.Max(m.x, m.y, m.z);
            // BANDA REAJUSTADA (2026-08-12) de 0.22–0.36 a 0.38–0.52 contra el frame de
            // referencia de la película. El razonamiento de la banda anterior —"la foto está
            // tomada bajo luz cálida, así que el albedo va más apagado"— resultó ser una
            // corrección de más: en el frame los paneles fluorescentes son BLANCOS, casi
            // fríos, así que el amarillo verdoso no lo puede estar poniendo la luz. Vive en
            // el albedo, y la pared se lee saturada, no crema.
            Assert.GreaterOrEqual(sat, 0.38f,
                $"saturación {sat:F3}: por debajo la pared vuelve a leerse crema pálido y no " +
                "como el material de la referencia — y el error se acaba compensando por la vía " +
                "de la luz, que es el lazo que ya costó tres commits");
            Assert.LessOrEqual(sat, 0.52f,
                $"saturación {sat:F3}: por encima el papel deja de ser papel y se vuelve rótulo");

            // Amarillo VERDOSO: 60° es amarillo puro, por debajo de 50 tira a naranja/beige.
            float hue = Hue(m);
            Assert.That(hue, Is.InRange(53f, 62f),
                $"tono {hue:F1}°: la referencia es amarillo-verde inequívoco, no beige cálido");

            // Amarillo: R y G altos y juntos, azul muy por debajo. Es la firma que separa
            // amarillo de beige amarronado (donde R−G se acerca a G−B) y de verde (G > R).
            Assert.Greater(m.x, m.y, "media R ≤ G — el papel viraría a verde");
            Assert.Greater(m.y - m.z, 3f * (m.x - m.y),
                $"R−G {m.x - m.y:F1} contra G−B {m.y - m.z:F1}: con el rojo despegado del verde " +
                "esto es beige amarronado, no amarillo");

            // La luminancia la sube o baja la SATURACIÓN si no se compensa, así que se ancla
            // aquí y la relación con suelo y techo la comprueba
            // TheThreeSurfacesStayInANarrowLuminanceBand.
            float wall = Luma(m.x, m.y, m.z);
            Assert.That(wall, Is.InRange(190f, 202f),
                $"luminancia {wall:F1}: girar el tono no puede mover el nivel de la pared, que " +
                "es lo único que quedó validado en captura de todo el trabajo de color");
        }

        /// <summary>
        /// Las tres superficies caben en una banda de luminancia estrecha. Es lo que impide
        /// volver al estado del que venimos, donde el suelo estaba 13 puntos por debajo de
        /// la pared y se leía marrón.
        ///
        /// LO QUE ESTE TEST YA NO EXIGE (2026-08-12): que las tres compartan tono y que la
        /// moqueta sea la más saturada. La pared pasa a ser el ANCLA —amarillo verdoso
        /// saturado, fijado contra el frame de referencia— y suelo y techo tienen que
        /// DIVERGIR de ella en un commit posterior, porque ahora mismo las tres se funden.
        /// Hasta que ese commit exista, aquí solo quedan las cotas anchas que ninguna
        /// autoría futura debería cruzar: nadie se sale del amarillo y nadie se vuelve
        /// rótulo. Cuando el suelo y el techo estén autorados, esta guarda vuelve a
        /// apretarse con las relaciones que se decidan allí.
        /// </summary>
        [Test]
        public void TheThreeSurfacesStayInANarrowLuminanceBand()
        {
            var wall = MeanRgb(Load(Albedo, out _, out _));
            var floor = MeanRgb(Load(Carpet, out _, out _));
            var ceiling = MeanRgb(Load(Ceiling, out _, out _));

            foreach (var (name, c) in new[] { ("suelo", floor), ("techo", ceiling) })
            {
                float h = Hue(c);
                Assert.That(h, Is.InRange(35f, 70f),
                    $"tono de {name} {h:F1}° — fuera de esa horquilla ya no es un amarillo de Backrooms");
                Assert.LessOrEqual(Sat(c), 0.52f,
                    $"saturación de {name} {Sat(c):F3}: por encima deja de ser una superficie de oficina");
            }

            float lw = Luma(wall.x, wall.y, wall.z);
            float lf = Luma(floor.x, floor.y, floor.z);
            float lc = Luma(ceiling.x, ceiling.y, ceiling.z);
            Assert.That(lw, Is.InRange(lf - 2f, lc + 2f),
                $"luminancia de pared {lw:F1} fuera de la banda suelo {lf:F1} … techo {lc:F1}");
            Assert.LessOrEqual(lc - lf, 20f,
                $"suelo {lf:F1} y techo {lc:F1} a {lc - lf:F1} puntos — el sitio uniformemente " +
                "iluminado del canon no admite que una superficie destaque sobre las otras");
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

        /// <summary>
        /// La prueba de la media distancia. Un promedio de 8×8 es lo que el mip entrega a
        /// unos diez metros (el tile cubre 2.5 m × 2.0 m).
        ///
        /// ESTE TEST CAMBIÓ DE SIGNO (2026-08-12) y conviene saber por qué antes de
        /// volverlo a mover. Nació exigiendo que el motivo DESAPARECIERA con la distancia
        /// (recorrido ≤ 12), leyendo el canon como "el chevron es casi imperceptible". A
        /// 6 de 255 de contraste el resultado fue que la pared se leía como color plano en
        /// TODO el rango, incluso a dos metros, y el frame de referencia sí muestra textura
        /// de superficie. El contraste sube a 12–15 y la guarda pasa a ser una BANDA: el
        /// motivo tiene que percibirse a media distancia y aun así no puede convertirse en
        /// un estampado que grite.
        /// </summary>
        [Test]
        public void TheChevronStaysPerceptibleAtDistanceWithoutShouting()
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

            Assert.LessOrEqual(max - min, 30f,
                $"recorrido de luminancia {max - min:F1}/255 tras promediar 8×8 — por encima el " +
                "motivo deja de ser papel pintado y se lee como estampado de tela");
            Assert.Greater(max - min, 12f,
                $"recorrido {max - min:F1}/255: por debajo la pared vuelve a leerse como color " +
                "plano a media distancia, que es de donde venimos");
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
