using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Paleta base de superficie — los TRES materiales de mundo (M_Backrooms_Wall /
    /// _Floor / _Ceiling) y la garantía de que las cuatro capas comparten exactamente
    /// esos tres.
    ///
    /// Por qué existe: antes de la unificación, el material de suelo/pared/techo se
    /// construía por capa desde <c>LayerVisualConfig.{floor,wall,ceiling}Texture</c> y se
    /// bajaba con <c>*Tint</c> hasta albedo 0.20 en la capa 3. Una superficie oscura en
    /// una captura podía ser el material o podía ser la luz, y no había forma de saberlo
    /// — que es justo lo que este paso tenía que despejar. Estas guardas fallan si
    /// alguien vuelve a meter divergencia por capa, que es la regresión silenciosa que
    /// más caro sale: no rompe nada, solo devuelve el diagnóstico visual a cero.
    ///
    /// NO comprueban luz, exposición ni post-proceso: el criterio de "negro puro" en
    /// pantalla depende de la iluminación y se valida con captura, no aquí.
    /// </summary>
    [TestFixture]
    public class SurfacePaletteTests
    {
        private static readonly string[] LayerConfigs =
        {
            "LayerVisuals/Layer0_Vestibulo",
            "LayerVisuals/Layer1_Industrial",
            "LayerVisuals/Layer2_Concreto",
            "LayerVisuals/Layer3_Vacio",
        };

        /// <summary>Luminancia Rec.709 — el brillo percibido, no la media de canales.</summary>
        private static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        [Test]
        public void TheThreeCanonMaterialsExistAndAreMatteNonMetallic()
        {
            foreach (string res in new[]
            {
                LayerVisualMaterials.WallMaterialResource,
                LayerVisualMaterials.FloorMaterialResource,
                LayerVisualMaterials.CeilingMaterialResource,
            })
            {
                var mat = Resources.Load<Material>(res);
                Assert.IsNotNull(mat,
                    $"'{res}' no aparece por Resources — LayerVisualMaterials caería al " +
                    "material por capa y el mundo dejaría de compartir la paleta base");

                Assert.IsNotNull(mat.GetTexture(LayerVisualMaterials.BaseMapId),
                    $"{mat.name}: sin _BaseMap el albedo sale del color plano y se pierde el grano");

                // El tinte por tile llega por MaterialPropertyBlock y MULTIPLICA a este
                // color; cualquier valor por debajo de blanco oscurecería el canon entero
                // por una vía que no se ve en el Inspector del material.
                var baseColor = mat.GetColor(LayerVisualMaterials.BaseColorId);
                Assert.AreEqual(1f, Luma(baseColor), 0.001f,
                    $"{mat.name}: _BaseColor debe ser blanco — el albedo lo pone la textura");

                if (mat.HasProperty("_Metallic"))
                    Assert.AreEqual(0f, mat.GetFloat("_Metallic"), 0.0001f,
                        $"{mat.name}: papel, moqueta y placa de fibra mineral no son metal");

                // El shader de pared (Backrooms/GridWallOffset) es BlinnPhong sin
                // smoothness: matte por construcción, así que no declara la propiedad.
                if (mat.HasProperty("_Smoothness"))
                    Assert.LessOrEqual(mat.GetFloat("_Smoothness"), 0.05f,
                        $"{mat.name}: por encima de ~0.05 la superficie devuelve un brillo " +
                        "especular que lee como plástico, no como oficina abandonada");
            }
        }

        [Test]
        public void EveryLayerSharesTheSameThreeSurfaceMaterials()
        {
            string floor0 = null, wall0 = null, ceiling0 = null;

            foreach (string res in LayerConfigs)
            {
                var cfg = Resources.Load<LayerVisualConfig>(res);
                Assert.IsNotNull(cfg, $"{res} no aparece por Resources");

                var mats = LayerVisualMaterials.Build(cfg);
                try
                {
                    // La huella se compara como TEXTO y no por referencia porque cada capa
                    // recibe su propia copia del material canon (Build instancia; destruir
                    // el asset de disco lo borraría del proyecto). Lo que debe coincidir es
                    // el contenido, no la instancia.
                    string f = Fingerprint(mats.floor), w = Fingerprint(mats.wall),
                           c = Fingerprint(mats.ceiling);

                    if (floor0 == null) { floor0 = f; wall0 = w; ceiling0 = c; continue; }

                    Assert.AreEqual(floor0,   f, $"{res}: el suelo no comparte la paleta base");
                    Assert.AreEqual(wall0,    w, $"{res}: la pared no comparte la paleta base");
                    Assert.AreEqual(ceiling0, c, $"{res}: el techo no comparte la paleta base");
                }
                finally { mats.Destroy(); }
            }
        }

        /// <summary>Todo lo que decide el albedo de una superficie ANTES de que el
        /// MaterialPropertyBlock por tile entre en juego: shader, textura base, escala de
        /// UV y color base.</summary>
        private static string Fingerprint(Material m)
        {
            Assert.IsNotNull(m, "material de superficie nulo — en URP eso se dibuja magenta");
            var tex = m.GetTexture(LayerVisualMaterials.BaseMapId);
            return $"{m.shader.name}|{(tex != null ? tex.name : "<none>")}|" +
                   $"{m.GetTextureScale(LayerVisualMaterials.BaseMapId)}|" +
                   $"{m.GetColor(LayerVisualMaterials.BaseColorId)}";
        }

        [Test]
        public void NoLayerDragsTheBaseAlbedoDownWithItsTint()
        {
            foreach (string res in LayerConfigs)
            {
                var cfg = Resources.Load<LayerVisualConfig>(res);
                Assert.IsNotNull(cfg, $"{res} no aparece por Resources");

                // El tinte de capa multiplica al albedo del material canon. En blanco, el
                // canon se ve tal cual; por debajo, la capa reintroduce por la puerta de
                // atrás la divergencia que los tres materiales acaban de quitar. El tinte
                // por ZONA (zoneTints, ADR-059/066) sigue aplicándose encima y no se toca.
                AssertWhite(cfg.floorTint,   $"{res}.floorTint");
                AssertWhite(cfg.wallTint,    $"{res}.wallTint");
                AssertWhite(cfg.ceilingTint, $"{res}.ceilingTint");

                AssertNoVariants(cfg.floorTintVariants,   $"{res}.floorTintVariants");
                AssertNoVariants(cfg.wallTintVariants,    $"{res}.wallTintVariants");
                AssertNoVariants(cfg.ceilingTintVariants, $"{res}.ceilingTintVariants");
            }
        }

        private static void AssertWhite(Color c, string what) =>
            Assert.AreEqual(Color.white, c,
                $"{what} = {c} — con la paleta base unificada el tinte de capa es blanco");

        /// <summary>La paleta por tile queda vacía a propósito: era una segunda fuente de
        /// albedo por capa que no se ve en el material, y la variedad la daba mejor el
        /// jitter HSV de <c>GridChunkBuilder.JitterValue</c>.
        ///
        /// Nota (2026-08-12): ese argumento ya solo vale para SUELO y TECHO. En pared el
        /// jitter está a 0 (<c>WallValueJitter</c>) porque sobre una superficie vertical el
        /// salto entre paneles contiguos cae en el borde geométrico y se lee como costura
        /// de tiling, no como desgaste. La variedad de pared la aporta <c>WallStain</c>.
        /// Reactivar estas variantes NO es la forma de recuperarla: volverían a mover el
        /// albedo por capa, que es justo lo que la paleta base unificada quitó.</summary>
        private static void AssertNoVariants(Color[] variants, string what) =>
            Assert.IsTrue(variants == null || variants.Length == 0,
                $"{what} tiene {variants?.Length} entradas — la paleta base es un solo tinte");
    }
}
