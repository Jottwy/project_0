using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Herramientas de la migración BIRP→URP (commit 4/8 del plan 2026-08-10).
    ///
    /// Convierte SOLO los materiales propios (fuera de Assets/PolymindGames) que
    /// siguen en el shader Built-in "Standard" al URP Lit, usando el
    /// StandardUpgrader oficial del paquete URP — el mismo remapeo de propiedades
    /// (_MainTex→_BaseMap, _Color→_BaseColor, _Glossiness→_Smoothness, keywords)
    /// que el Render Pipeline Converter, pero acotado por ruta para que la
    /// conversión del vendor viva en su propio commit (5/8).
    ///
    /// La herramienta de switch del propio vendor NO se usa: pasa fromPipeline dos
    /// veces y su conversión compila vacía (ver auditoría en el ADR de migración).
    ///
    /// Headless:
    ///   Unity.exe -batchmode -quit -nographics -projectPath &lt;proyecto&gt; ^
    ///     -executeMethod BackroomsSurvival.EditorTools.UrpMigrationTools.UpgradeOwnStandardMaterials ^
    ///     -logFile &lt;log&gt;
    /// </summary>
    public static class UrpMigrationTools
    {
        // Raíces con materiales propios; PolymindGames y TextMesh Pro quedan fuera.
        private static readonly string[] OwnMaterialRoots =
        {
            "Assets/Resources",
            "Assets/MeshyImports",
            "Assets/TripoModels",
            "Assets/_Migration",
        };

        [MenuItem("Backrooms/URP Migration/Upgrade Own Standard Materials")]
        public static void UpgradeOwnStandardMaterials() =>
            UpgradeStandardMaterials(OwnMaterialRoots);

        // Commit 5/8: los materiales Standard que el .unitypackage URP del vendor no
        // reemplazó. Mismo upgrader oficial; el ConvertMaterialsToUrp del vendor no se
        // usa (su define nunca se activa solo — bug documentado en el ADR).
        [MenuItem("Backrooms/URP Migration/Upgrade Vendor Standard Materials")]
        public static void UpgradeVendorStandardMaterials() =>
            UpgradeStandardMaterials(new[] { "Assets/PolymindGames" });

        // Los .unitypackage URP del vendor tampoco tocaron los materiales de
        // partículas: la hoguera del menú principal (MenuFire) y las llamas de
        // FPS_VFX_SmallFire siguen en "Particles/Standard Unlit" de Built-in.
        // ParticleUpgrader es el mismo upgrader que el Render Pipeline Converter
        // registra para esos shaders (UniversalRenderPipelineMaterialUpgrader.cs:202).
        [MenuItem("Backrooms/URP Migration/Upgrade Particle Materials")]
        public static void UpgradeParticleMaterials()
        {
            var upgraders = new (string from, MaterialUpgrader upgrader)[]
            {
                ("Particles/Standard Unlit", new ParticleUpgrader("Particles/Standard Unlit")),
                ("Particles/Standard Surface", new ParticleUpgrader("Particles/Standard Surface")),
                ("Particles/VertexLit Blended", new ParticleUpgrader("Particles/VertexLit Blended")),
            };

            int upgraded = 0;

            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null)
                    continue;

                foreach (var (from, upgrader) in upgraders)
                {
                    if (mat.shader.name != from)
                        continue;

                    MaterialUpgrader.Upgrade(mat, upgrader, MaterialUpgrader.UpgradeFlags.None);
                    upgraded++;
                    Debug.Log($"[UrpMigration] particle upgraded ({from}): {path}");
                    break;
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[UrpMigration] Particles -> URP: {upgraded} materiales convertidos.");
        }

        private static void UpgradeStandardMaterials(string[] roots)
        {
            var upgrader = new StandardUpgrader("Standard");
            int upgraded = 0;

            foreach (var guid in AssetDatabase.FindAssets("t:Material", roots))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null || mat.shader.name != "Standard")
                    continue;

                MaterialUpgrader.Upgrade(mat, upgrader, MaterialUpgrader.UpgradeFlags.None);
                upgraded++;
                Debug.Log($"[UrpMigration] upgraded: {path}");
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[UrpMigration] Standard -> URP Lit: {upgraded} materiales convertidos.");
        }
    }
}
