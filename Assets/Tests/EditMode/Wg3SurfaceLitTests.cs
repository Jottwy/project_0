using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// A4 (13-09) — el shader propio del suelo de WG3: copia de URP/Lit 17.0.4 con teselado
    /// estocástico y humedad en coordenadas de MUNDO. Lo que se prueba es lo que falla sin avisar:
    /// un shader que no compila pinta en magenta sólo en Play, y un material que pierde el shader
    /// o sus palabras clave vuelve a la moqueta repetida sin ningún error.
    /// </summary>
    [TestFixture]
    public class Wg3SurfaceLitTests
    {
        private const string ShaderName = "Backrooms/WG3/Surface Lit";
        private const string FloorPath = "Assets/Materials/WorldGen3/Wg3_Floor.mat";

        [Test]
        public void TheShaderExistsAndCompiles()
        {
            Shader shader = Shader.Find(ShaderName);
            Assert.IsNotNull(shader, $"no se encuentra «{ShaderName}»");
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader), "el shader tiene errores de compilación");
            Assert.IsTrue(shader.isSupported, "el shader no está soportado en esta plataforma");
        }

        [Test]
        public void TheFloorUsesItWithBothFeatures()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(FloorPath);
            Assert.IsNotNull(mat, FloorPath);
            Assert.AreEqual(ShaderName, mat.shader.name, "Wg3_Floor tiene que usar el shader de WG3");
            Assert.IsTrue(mat.IsKeywordEnabled("_WG3_STOCHASTIC"), "sin teselado estocástico");
            Assert.IsTrue(mat.IsKeywordEnabled("_WG3_WETNESS"), "sin humedad de mundo");
            Assert.IsTrue(mat.IsKeywordEnabled("_NORMALMAP"), "sin normal");
            Assert.IsTrue(mat.IsKeywordEnabled("_METALLICSPECGLOSSMAP"), "sin máscara");
            Assert.Greater(mat.GetFloat("_Wg3StochasticCell"), 0.5f, "celda estocástica degenerada");
            Assert.Greater(mat.GetFloat("_Wg3WetScale"), 1f, "escala de humedad degenerada");
        }

        /// <summary>El SRP Batcher exige el MISMO bloque UnityPerMaterial en todas las pasadas: si
        /// una pasada incluye el LitInput de URP y otra el de WG3, las propiedades no casan. Se
        /// comprueba que ninguna pasada del shader siga apuntando al de URP.</summary>
        [Test]
        public void EveryPassIncludesTheWg3Input()
        {
            string path = AssetDatabase.GetAssetPath(Shader.Find(ShaderName));
            Assert.IsNotEmpty(path);
            string src = System.IO.File.ReadAllText(path);
            StringAssert.DoesNotContain("Shaders/LitInput.hlsl", src);
            StringAssert.DoesNotContain("Shaders/LitForwardPass.hlsl", src);
            StringAssert.DoesNotContain("Shaders/LitGBufferPass.hlsl", src);
        }
    }
}
