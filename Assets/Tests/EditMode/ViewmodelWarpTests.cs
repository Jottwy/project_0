using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-077 enm. 2 — la puerta del warp del viewmodel. El viewmodel de FPSCore reproyecta los
    /// vértices en el SHADER contra los uniforms globales <c>_FOV</c>/<c>_FOVEnabled</c>, así que
    /// sólo warpea lo que usa un shader de warp. Un objeto colgado de la mano con URP/Lit se dibuja
    /// con la proyección del mundo mientras la mano va con la del viewmodel: sale ~1,5× más grande
    /// que el puño y desplazado. Pasó con el reloj (ADR-077) y volvió a pasar con el destornillador,
    /// el bote y la linterna, sin ningún error en consola. Estos tests lo convierten en rojo.
    ///
    /// Las rutas están DUPLICADAS de los builders a propósito: los tests no ven el ensamblado del
    /// editor, y un cambio a un lado sin el otro sale en rojo aquí, que es lo que se quiere.
    /// </summary>
    [TestFixture]
    public class ViewmodelWarpTests
    {
        private static readonly string[] WieldableFolders =
        {
            "Assets/Prefabs/Wieldables",
            "Assets/Resources/Wieldables",
        };

        /// <summary>Los shaders que warpean: objetos, piel, UI diegética y ropa de primera persona (ADR-149 R4c).</summary>
        private static readonly string[] WarpShaderPaths =
        {
            "Assets/PolymindGames/FPSCore/Code/Shaders/LitFieldOfView.shadergraph",
            "Assets/PolymindGames/FPSCore/Code/Shaders/LitFieldOfView_SSS.shadergraph",
            "Assets/Art/Watch/BR_UIWarp.shader",
            "Assets/Shaders/Garments/BR_GarmentLitFP.shader",
        };

        private const string WorldShaderName = "Universal Render Pipeline/Lit";

        /// <summary>Un objeto de mano del proyecto: material de mundo, material de primera persona, y los dos prefabs.</summary>
        private static readonly (string name, string worldMat, string fpMat, string wieldable, string node, string pickup)[] HeldItems =
        {
            ("Screwdriver",
                "Assets/Art/Items/Screwdriver/BR_Screwdriver_Mat.mat",
                "Assets/Art/Items/Screwdriver/BR_Screwdriver_FP_Mat.mat",
                "Assets/Prefabs/Wieldables/BR_Wieldable_Screwdriver.prefab", "BR_ScrewdriverModel",
                "Assets/Prefabs/Items/BR_Pickup_Screwdriver.prefab"),
            ("SprayCan",
                "Assets/Art/Items/SprayCan/BR_SprayCan_Mat.mat",
                "Assets/Art/Items/SprayCan/BR_SprayCan_FP_Mat.mat",
                "Assets/Prefabs/Wieldables/BR_Wieldable_SprayCan.prefab", "BR_SprayCanModel",
                "Assets/Prefabs/Items/BR_Pickup_SprayCan.prefab"),
            ("CrankFlashlight",
                "Assets/Art/Items/CrankFlashlight/BR_CrankFlashlight_Mat.mat",
                "Assets/Art/Items/CrankFlashlight/BR_CrankFlashlight_FP_Mat.mat",
                "Assets/Prefabs/Wieldables/BR_Wieldable_CrankFlashlight.prefab", "BR_CrankFlashlightModel",
                "Assets/Prefabs/Items/BR_Pickup_CrankFlashlight.prefab"),
        };

        private static HashSet<Shader> WarpShaders()
        {
            var set = new HashSet<Shader>();
            foreach (var path in WarpShaderPaths)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                Assert.IsNotNull(shader, $"falta el shader de warp '{path}'");
                set.Add(shader);
            }
            return set;
        }

        /// <summary>Activo en el prefab: él y todos sus padres con <c>activeSelf</c>. Fuera de
        /// escena <c>activeInHierarchy</c> no vale (siempre false).</summary>
        private static bool ActiveInPrefab(Transform t)
        {
            for (var c = t; c != null; c = c.parent)
                if (!c.gameObject.activeSelf) return false;
            return true;
        }

        private static IEnumerable<string> WieldablePrefabPaths()
        {
            return AssetDatabase.FindAssets("t:Prefab", WieldableFolders)
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, System.StringComparer.Ordinal);
        }

        [Test]
        public void HayWieldablesQueVigilar()
        {
            Assert.That(WieldablePrefabPaths().Count(), Is.GreaterThanOrEqualTo(4),
                "menos wieldables de los que había (3 objetos + el reloj): ¿se movió la carpeta?");
        }

        /// <summary>
        /// LA REGLA. Todo renderer de malla activo y encendido dentro de un wieldable del proyecto usa
        /// un shader de warp. Se listan todos los infractores de golpe, no sólo el primero.
        /// </summary>
        [Test]
        public void TodoRendererActivoDeUnWieldableWarpea()
        {
            var warp = WarpShaders();
            var offenders = new List<string>();

            foreach (var path in WieldablePrefabPaths())
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.IsNotNull(prefab, path);
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                    if (!renderer.enabled || !ActiveInPrefab(renderer.transform)) continue;

                    var mats = renderer.sharedMaterials;
                    if (mats.Length == 0) offenders.Add($"{path} › {renderer.name}: sin material");
                    foreach (var mat in mats)
                    {
                        if (mat == null) offenders.Add($"{path} › {renderer.name}: material nulo");
                        else if (mat.shader == null || !warp.Contains(mat.shader))
                            offenders.Add($"{path} › {renderer.name}: '{mat.name}' usa '{(mat.shader != null ? mat.shader.name : "null")}'");
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "Superficies pegadas a la mano que NO warpean (se dibujarán con la proyección del mundo, " +
                "más grandes que la mano que las sujeta). Objetos → LitFieldOfView, piel → LitFieldOfView_SSS, " +
                "Canvas → BR_UIWarp. Ver ADR-077 enm. 2:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// Lo que hizo invisible al bug del reloj: <c>_FOV</c>/<c>_FOVEnabled</c> son GLOBALES y
        /// ningún material puede pisarlos. Si un reimport del vendor los metiera en el bloque
        /// <c>Properties</c>, el valor serializado de cada material pisaría al global sin avisar.
        /// </summary>
        [Test]
        public void LosUniformsDelWarpSiguenSiendoGlobales()
        {
            foreach (var shader in WarpShaders())
            {
                Assert.AreEqual(-1, shader.FindPropertyIndex("_FOV"), $"'{shader.name}' declara _FOV como propiedad de material");
                Assert.AreEqual(-1, shader.FindPropertyIndex("_FOVEnabled"), $"'{shader.name}' declara _FOVEnabled como propiedad de material");
            }
        }

        /// <summary>
        /// Cada objeto de mano tiene DOS materiales, como el vendor (<c>X.mat</c> / <c>FP_X.mat</c>):
        /// el de mundo con URP/Lit y el de primera persona con el shader de warp y su mask map
        /// horneado bajo <c>Assets/Art/Items/</c>. El nodo del modelo en la mano lleva el segundo.
        /// </summary>
        [Test]
        public void CadaObjetoDeManoTieneSusDosMateriales()
        {
            var fpShader = AssetDatabase.LoadAssetAtPath<Shader>(WarpShaderPaths[0]);
            foreach (var item in HeldItems)
            {
                var world = AssetDatabase.LoadAssetAtPath<Material>(item.worldMat);
                Assert.IsNotNull(world, $"{item.name}: falta el material de mundo '{item.worldMat}'");
                Assert.AreEqual(WorldShaderName, world.shader.name, $"{item.name}: el material de MUNDO no es URP/Lit");

                var fp = AssetDatabase.LoadAssetAtPath<Material>(item.fpMat);
                Assert.IsNotNull(fp, $"{item.name}: falta el material de primera persona '{item.fpMat}' (menú Backrooms/Viewmodel/Rewarp)");
                Assert.AreEqual(fpShader, fp.shader, $"{item.name}: el material de primera persona no usa LitFieldOfView");

                var baseMap = fp.GetTexture("_BaseColorMap");
                Assert.IsNotNull(baseMap, $"{item.name}: material de primera persona sin albedo");
                Assert.AreEqual(world.GetTexture("_BaseMap"), baseMap, $"{item.name}: los dos materiales no comparten albedo");
                var mask = fp.GetTexture("_MaskMap");
                Assert.IsNotNull(mask, $"{item.name}: sin _MaskMap: metallic y smoothness se pierden en el viewmodel");
                StringAssert.StartsWith("Assets/Art/Items/", AssetDatabase.GetAssetPath(mask),
                    $"{item.name}: el mask map apunta fuera del arte versionado");

                var wieldable = AssetDatabase.LoadAssetAtPath<GameObject>(item.wieldable);
                Assert.IsNotNull(wieldable, item.wieldable);
                var node = wieldable.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == item.node);
                Assert.IsNotNull(node, $"{item.name}: sin nodo '{item.node}' en el wieldable");
                var renderers = node.GetComponentsInChildren<MeshRenderer>(true);
                Assert.IsNotEmpty(renderers, $"{item.name}: '{item.node}' sin MeshRenderer");
                foreach (var r in renderers)
                    Assert.AreEqual(item.fpMat, AssetDatabase.GetAssetPath(r.sharedMaterial),
                        $"{item.name}: '{r.name}' en la mano no lleva el material de primera persona");
            }
        }

        /// <summary>El objeto del SUELO no warpea: en el mundo, el warp lo deformaría contra el FOV del viewmodel.</summary>
        [Test]
        public void ElPickupNoWarpea()
        {
            var warp = WarpShaders();
            foreach (var item in HeldItems)
            {
                var pickup = AssetDatabase.LoadAssetAtPath<GameObject>(item.pickup);
                Assert.IsNotNull(pickup, item.pickup);
                foreach (var r in pickup.GetComponentsInChildren<Renderer>(true))
                    foreach (var mat in r.sharedMaterials)
                        Assert.IsFalse(mat != null && warp.Contains(mat.shader),
                            $"{item.name}: el pickup '{r.name}' usa el shader de warp '{(mat != null ? mat.shader.name : "")}'");
            }
        }
    }
}
