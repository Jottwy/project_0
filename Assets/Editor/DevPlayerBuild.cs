using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Build de DESARROLLO por línea de comandos.
    ///
    /// Existe porque el `-buildWindows64Player` que usa
    /// <c>tools/dev/RunMultiInstancePlaytest.ps1</c> produce siempre un player de
    /// RELEASE, y varios diagnósticos del proyecto están detrás de
    /// <c>DEVELOPMENT_BUILD</c> — entre ellos <see cref="UI.PoiDebugHud"/>, que es el
    /// único sitio donde se leen en pantalla el <c>layer</c> y el <c>zone_kind</c> del
    /// chunk en el que está el jugador. Sin esto, comprobar en juego algo que dependa
    /// de la capa obliga a abrir el Editor.
    ///
    /// Uso:
    ///   Unity.exe -batchmode -quit -nographics -projectPath &lt;proyecto&gt; ^
    ///     -executeMethod BackroomsSurvival.EditorTools.DevPlayerBuild.BuildWindows64 ^
    ///     -buildOutput &lt;ruta\al\.exe&gt; -logFile &lt;log&gt;
    ///
    /// `-buildOutput` es opcional; por defecto escribe en Builds\BackroomsSurvivalMMO.exe,
    /// la misma ruta que espera el arnés de playtest.
    ///
    /// NO sustituye al build de release: es un método aparte a propósito, para que nadie
    /// publique sin querer un player con los diagnósticos dentro.
    /// </summary>
    public static class DevPlayerBuild
    {
        private const string DefaultOutput = "Builds/BackroomsSurvivalMMO.exe";

        public static void BuildWindows64()
        {
            string output = ArgValue("-buildOutput") ?? DefaultOutput;

            // Las escenas HABILITADAS en Build Settings, en su orden — el mismo conjunto
            // que toma `-buildWindows64Player`, para que el player de desarrollo y el de
            // release no difieran en nada más que las opciones de build.
            string[] scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("[DevPlayerBuild] No hay escenas habilitadas en Build Settings; " +
                               "un player sin escenas arranca en negro.");
                EditorApplication.Exit(1);
                return;
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                // Development habilita DEVELOPMENT_BUILD (y con él PoiDebugHud);
                // AllowDebugging deja además enganchar un depurador si hace falta.
                options = BuildOptions.Development | BuildOptions.AllowDebugging,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            Debug.Log($"[DevPlayerBuild] result={summary.result} output={output} " +
                      $"size={summary.totalSize} errors={summary.totalErrors} " +
                      $"warnings={summary.totalWarnings} duration={summary.totalTime}");

            // Exit code explícito: en batchmode, `BuildPlayer` devolviendo Failed NO hace
            // que Unity salga con código distinto de 0 por sí solo, así que un llamante
            // que solo mire $LASTEXITCODE daría por bueno un build fallido.
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>Valor del argumento `name` en la línea de comandos, o null.</summary>
        private static string ArgValue(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
