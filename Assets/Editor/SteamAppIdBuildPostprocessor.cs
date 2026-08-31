using System;
using System.IO;
using BackroomsSurvival.Net;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BackroomsSurvival.Editor
{
    /// <summary>
    /// Deja el build en condiciones de hablar con Steam. Dos cosas, y las dos han fallado ya en
    /// silencio:
    ///
    /// 1. **`steam_appid.txt` junto al ejecutable.** Sin él, un player que NO se lanza desde el
    ///    cliente de Steam no tiene forma de saber qué juego es, y `SteamClient.Init` falla. Se
    ///    escribe con el mismo id que <see cref="SteamAppConfig"/> resolverá en runtime, así que
    ///    los dos no pueden discrepar (si discrepan, Steam rechaza el Init).
    ///
    /// 2. **El nativo `steam_api64.dll`.** El binding managed de Facepunch estaba en el proyecto
    ///    desde el principio y el nativo del SDK de Steamworks **no**, así que todos los builds
    ///    anteriores a esto salieron con Steam muerto: `SteamClient.Init(480) failed: steam_api64`
    ///    en el log, leído durante semanas como "Steam cerrado". Se verifica que Unity lo haya
    ///    copiado y, si no, **se falla el build** — igual que hace
    ///    <see cref="BackendBuildPostprocessor"/> con el backend, y por la misma razón: un fallo
    ///    aquí nombra el arreglo, y a runtime sólo deja un warning que nadie lee.
    ///
    /// Este fichero, como el del backend, vive en <c>Assets/Editor/</c> sin asmdef y por tanto en
    /// <c>Assembly-CSharp-Editor</c>, que <c>EditModeTests.asmdef</c> no puede referenciar. Lo
    /// probable de probar (la resolución del id) vive en <see cref="SteamAppConfig"/>, que sí es
    /// código de runtime y sí tiene tests.
    /// </summary>
    public sealed class SteamAppIdBuildPostprocessor : IPostprocessBuildWithReport
    {
        private const string NativeName = "steam_api64.dll";

        /// Después del backend (callbackOrder 0): si no hay servidor que copiar, el App ID da
        /// igual.
        public int callbackOrder => 1;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.summary.outputPath)) return;
            if (report.summary.platform != BuildTarget.StandaloneWindows64) return;

            string buildFolder = Path.GetDirectoryName(report.summary.outputPath);
            if (string.IsNullOrWhiteSpace(buildFolder))
            {
                throw new BuildFailedException(
                    "[SteamAppIdBuildPostprocessor] Build folder unavailable; " +
                    $"no se pudo escribir {SteamAppConfig.AppIdFileName} ni verificar {NativeName}.");
            }

            WriteAppIdFile(buildFolder, report.summary.options);
            VerifyNativePlugin(report.summary.outputPath, buildFolder);
        }

        /// <summary>
        /// El id que se escribe es el MISMO que <see cref="SteamAppConfig.DefaultAppId"/> resolverá
        /// en ese player: Spacewar en un development build, producción en release. Escribirlo no
        /// congela nada — el fichero es texto y se edita para cambiar de id sin recompilar, que es
        /// exactamente para lo que existe este escalón.
        /// </summary>
        private static void WriteAppIdFile(string buildFolder, BuildOptions options)
        {
            bool development = (options & BuildOptions.Development) != 0;
            uint appId = development ? SteamAppConfig.DevAppId : SteamAppConfig.ProductionAppId;

            // Un override de entorno en la MÁQUINA DE BUILD no debe viajar dentro del player: el
            // fichero describe el build, no la sesión que lo produjo.
            string path = Path.Combine(buildFolder, SteamAppConfig.AppIdFileName);

            try
            {
                // Sin BOM y sin salto final: Valve lee este fichero con un parser propio y el BOM
                // le ha dado problemas históricamente. `SteamAppConfig.TryParse` tolera ambos, el
                // cliente de Steam no necesariamente.
                File.WriteAllText(path, appId.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception e)
            {
                throw new BuildFailedException(
                    $"[SteamAppIdBuildPostprocessor] No se pudo escribir {path}: {e.Message}. " +
                    "Sin ese fichero, un player lanzado fuera de Steam no inicializa Steam.");
            }

            Debug.Log($"[SteamAppIdBuildPostprocessor] {SteamAppConfig.AppIdFileName} = " +
                      $"{SteamAppConfig.Describe(appId)} en {buildFolder} " +
                      $"(development={development}).");
        }

        /// <summary>
        /// Unity copia los plugins nativos a <c>&lt;exe&gt;_Data/Plugins/x86_64/</c>. Si el
        /// <c>.meta</c> del DLL no tiene marcado Standalone Win64, el build sale sin él y Steam
        /// muere a runtime con un `DllNotFoundException` que no menciona a Unity por ningún lado.
        /// </summary>
        private static void VerifyNativePlugin(string outputPath, string buildFolder)
        {
            string exeName = Path.GetFileNameWithoutExtension(outputPath);
            string dataFolder = Path.Combine(buildFolder, exeName + "_Data");
            string pluginPath = Path.Combine(dataFolder, "Plugins", "x86_64", NativeName);

            if (File.Exists(pluginPath))
            {
                Debug.Log($"[SteamAppIdBuildPostprocessor] {NativeName} presente en {pluginPath}.");
                return;
            }

            // Segundo sitio válido: junto al exe. Algunos flujos lo colocan ahí a mano y el
            // cargador nativo de Windows también lo encuentra.
            if (File.Exists(Path.Combine(buildFolder, NativeName)))
            {
                Debug.Log($"[SteamAppIdBuildPostprocessor] {NativeName} presente junto al ejecutable.");
                return;
            }

            throw new BuildFailedException(
                $"[SteamAppIdBuildPostprocessor] {NativeName} NO está en el build " +
                $"(buscado en {pluginPath} y en {buildFolder}). Este player saldría con Steam " +
                "muerto: sin lobby, sin invitaciones y sin navegador de servidores, y el único " +
                "aviso sería un DllNotFoundException en el log. " +
                "Arreglo: comprobar que Assets/Plugins/Facepunch.Steamworks/redistributable_bin/" +
                $"win64/{NativeName} existe y que su importador tiene marcado Standalone Win64 " +
                "(CPU x86_64).");
        }
    }
}
