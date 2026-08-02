using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Debug = UnityEngine.Debug;

namespace BackroomsSurvival.Editor
{
    /// <summary>
    /// Compiles the Rust backend and deploys it to <c>Builds/Backend/</c> BEFORE a player build,
    /// so <see cref="BackendBuildPostprocessor"/> then carries a current server into the build.
    ///
    /// Why here and not left to the developer: the two manual steps
    /// (<c>cargo build --release</c> and <c>tools/dev/CopyReleaseBackendToBuild.ps1</c>) are exactly
    /// the ones this project has repeatedly forgotten — STATE.md records a session where the shipped
    /// binary silently predated the fix it was supposed to contain. A build that compiles its own
    /// backend cannot ship a stale one.
    ///
    /// Skipped gracefully when cargo is absent (a designer without the Rust toolchain still gets a
    /// build, from whatever binary is already deployed). A cargo COMPILE ERROR, by contrast, fails
    /// the build: that is a broken backend, not a missing toolchain.
    /// </summary>
    public sealed class BackendBuildPreprocessor : IPreprocessBuildWithReport
    {
        private const string ExecutableName = "backrooms_server.exe";
        private const string SkipPrefKey = "Backrooms.SkipBackendCompileOnBuild";
        private const string SkipMenu = "Backrooms/Build/Skip backend compile on player build";

        /// <summary>rustup needs the toolchain named EXPLICITLY when cargo is invoked from the repo
        /// root: it resolves the toolchain from the CWD, so <c>backend/rust-toolchain.toml</c> (which
        /// pins GNU) is ignored and the global MSVC default is used instead — which dies with
        /// "linker `link.exe` not found" on this machine, where no MSVC C++ tools are installed.
        /// <c>--manifest-path</c> alone does NOT fix it.</summary>
        private const string CargoArgs =
            "+stable-x86_64-pc-windows-gnu build --release --manifest-path backend/Cargo.toml";

        public int callbackOrder => 0;

        [MenuItem(SkipMenu)]
        private static void ToggleSkip() => EditorPrefs.SetBool(SkipPrefKey, !EditorPrefs.GetBool(SkipPrefKey, false));

        [MenuItem(SkipMenu, true)]
        private static bool ToggleSkipValidate()
        {
            Menu.SetChecked(SkipMenu, EditorPrefs.GetBool(SkipPrefKey, false));
            return true;
        }

        [MenuItem("Backrooms/Build/Compile and deploy backend now")]
        private static void CompileAndDeployNow()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
            if (CompileAndDeploy(projectRoot, out string error))
            {
                Debug.Log("[BackendBuildPreprocessor] Backend compiled and deployed to Builds/Backend/");
            }
            else
            {
                Debug.LogError($"[BackendBuildPreprocessor] {error}");
            }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (EditorPrefs.GetBool(SkipPrefKey, false))
            {
                Debug.LogWarning(
                    "[BackendBuildPreprocessor] Backend compile SKIPPED (toggled off in " +
                    $"'{SkipMenu}'). The build ships whatever is already in Builds/Backend/.");
                return;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
            if (!CompileAndDeploy(projectRoot, out string error))
            {
                throw new BuildFailedException($"[BackendBuildPreprocessor] {error}");
            }
        }

        /// <summary>
        /// Runs cargo and deploys on success. Returns false ONLY for conditions that should stop a
        /// build — a compile failure or a failed deploy. A missing cargo returns true after warning,
        /// because "no Rust toolchain on this machine" must not make the project unbuildable.
        /// </summary>
        private static bool CompileAndDeploy(string projectRoot, out string error)
        {
            error = null;

            var psi = new ProcessStartInfo
            {
                FileName = "cargo",
                Arguments = CargoArgs,
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            string stderr;
            int exitCode;
            try
            {
                using Process process = Process.Start(psi);
                if (process == null)
                {
                    Debug.LogWarning(
                        "[BackendBuildPreprocessor] cargo could not be started; shipping the " +
                        "backend already in Builds/Backend/.");
                    return true;
                }
                // Read BEFORE waiting: cargo writes its progress to stderr, and a full pipe buffer
                // with the parent blocked in WaitForExit is a deadlock, not a slow build.
                string stdout = process.StandardOutput.ReadToEnd();
                stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                exitCode = process.ExitCode;
                if (!string.IsNullOrWhiteSpace(stdout)) Debug.Log($"[cargo] {stdout.Trim()}");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // cargo is not on PATH. Not an error: build with what is deployed.
                Debug.LogWarning(
                    "[BackendBuildPreprocessor] cargo not found on PATH; skipping the backend " +
                    "compile and shipping the binary already in Builds/Backend/.");
                return true;
            }
            catch (Exception e)
            {
                error = $"Unexpected failure launching cargo: {e.Message}";
                return false;
            }

            if (exitCode != 0)
            {
                error = "The Rust backend FAILED TO COMPILE, so this build would ship a stale or " +
                        $"missing server. cargo exit={exitCode}\n{Tail(stderr, 2000)}";
                return false;
            }

            // cargo prints its "Compiling/Finished" lines on stderr even when it succeeds, so this
            // is progress output and not a fault — logged at info level on purpose.
            if (!string.IsNullOrWhiteSpace(stderr)) Debug.Log($"[cargo] {Tail(stderr, 800)}");

            return Deploy(projectRoot, out error);
        }

        /// <summary>Copies the freshly built executable to <c>Builds/Backend/</c>, the canonical
        /// location the editor ALSO launches from — so Play mode and the shipped build run the same
        /// binary rather than two that merely look alike.</summary>
        private static bool Deploy(string projectRoot, out string error)
        {
            error = null;
            string built = Path.Combine(projectRoot, "backend", "target", "release", ExecutableName);
            if (!File.Exists(built))
            {
                error = $"cargo reported success but {built} does not exist.";
                return false;
            }

            string destDir = Path.Combine(projectRoot, "Builds", "Backend");
            Directory.CreateDirectory(destDir);
            string dest = Path.Combine(destDir, ExecutableName);
            try
            {
                File.Copy(built, dest, true);
            }
            catch (IOException e)
            {
                // Almost always a live server still holding the file. Say so, because the raw
                // "process cannot access the file" message sends people looking at permissions.
                error = $"Could not write {dest}: {e.Message}. A backrooms_server may still be " +
                        "running — check Get-Process backrooms_server and stop it.";
                return false;
            }

            if (new FileInfo(dest).Length != new FileInfo(built).Length)
            {
                error = $"Deployed backend is a different size than the one just built ({dest}).";
                return false;
            }
            return true;
        }

        private static string Tail(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text[^max..];
    }
}
