#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Player de desarrollo que arranca DIRECTAMENTE en la escena de pruebas del inventario
    /// (<see cref="BackroomsInventoryTestSceneBuilder.ScenePath"/>), sin menú ni backend: lo que
    /// hace falta para un play-test autónomo (computer-use sobre el exe + capturas por
    /// CopyFromScreen, memoria <c>unity-remote-playtest</c>). No entra en Build Settings ni
    /// sustituye a <see cref="DevPlayerBuild"/>: es un exe aparte en <c>Builds/InventoryTest/</c>.
    ///
    /// Uso: Unity.exe -batchmode -quit -nographics -projectPath &lt;wt&gt;
    ///        -executeMethod BackroomsSurvival.EditorTools.BackroomsInventoryTestBuild.BuildWindows64 -logFile &lt;log&gt;
    /// </summary>
    public static class BackroomsInventoryTestBuild
    {
        public const string Output = "Builds/InventoryTest/InventoryTest.exe";

        public static void BuildWindows64()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(BackroomsInventoryTestSceneBuilder.ScenePath) == null)
            {
                Debug.LogError($"[InventoryTestBuild] falta {BackroomsInventoryTestSceneBuilder.ScenePath}: lanza Backrooms/UI/Build Inventory Test Scene");
                EditorApplication.Exit(1);
                return;
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { BackroomsInventoryTestSceneBuilder.ScenePath },
                locationPathName = Output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development | BuildOptions.AllowDebugging,
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary s = report.summary;
            Debug.Log($"[InventoryTestBuild] result={s.result} output={Output} errors={s.totalErrors} duration={s.totalTime}");
            EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
#endif
