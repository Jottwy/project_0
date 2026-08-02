using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BackroomsSurvival.Editor
{
    /// <summary>
    /// Copies the Rust backend executable into the player build so a shipped game can launch it.
    /// The runtime resolves it at <c>&lt;buildFolder&gt;/Backend/backrooms_server.exe</c>
    /// (NetworkInitializer's second non-editor candidate), which is what this writes.
    ///
    /// NOT covered by an EditMode test, deliberately and for a structural reason:
    /// <c>Assets/Editor/</c> has no asmdef, so this lives in the predefined
    /// <c>Assembly-CSharp-Editor</c>, and <c>EditModeTests.asmdef</c> (overrideReferences, no
    /// autoReference) cannot reference a predefined assembly. Adding an asmdef here to make it
    /// testable would change the implicit references of every other editor script in the folder.
    /// The guarantee is enforced a stronger way instead: a missing backend FAILS THE BUILD, so the
    /// failure is impossible to ship past rather than merely asserted somewhere.
    /// </summary>
    public sealed class BackendBuildPostprocessor : IPostprocessBuildWithReport
    {
        private const string ExecutableName = "backrooms_server.exe";

        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.summary.outputPath))
            {
                throw new BuildFailedException(
                    "[BackendBuildPostprocessor] Build output path unavailable, so the backend " +
                    "could not be placed. The build would ship unable to start its server.");
            }

            string buildFolder = Path.GetDirectoryName(report.summary.outputPath);
            if (string.IsNullOrWhiteSpace(buildFolder))
            {
                throw new BuildFailedException(
                    "[BackendBuildPostprocessor] Build folder unavailable; backend not copied.");
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string source = ResolveSource(projectRoot, out string sourceLabel);

            if (source == null)
            {
                // FAIL THE BUILD, do not warn and continue. Shipping a player that cannot start
                // its backend is strictly worse than not producing one: the failure moves from
                // here, where it names the fix, to a runtime LogError in a packaged game.
                throw new BuildFailedException(
                    "[BackendBuildPostprocessor] No backend executable found. Looked in " +
                    $"Builds/Backend/ and backend/target/release/ under {projectRoot}. " +
                    "Build and deploy it first:\n" +
                    "  cargo +stable-x86_64-pc-windows-gnu build --release --manifest-path backend/Cargo.toml\n" +
                    "  Copy-Item backend/target/release/backrooms_server.exe Builds/Backend/ -Force");
            }

            WarnIfStale(projectRoot, source);

            string backendFolder = Path.Combine(buildFolder, "Backend");
            Directory.CreateDirectory(backendFolder);
            string destination = Path.Combine(backendFolder, ExecutableName);
            File.Copy(source, destination, true);

            // Verify rather than assume. A copy onto a path held by a running server can leave a
            // short or locked file, and that is exactly the "build looks fine, game will not
            // start" case this class exists to prevent.
            long sourceLength = new FileInfo(source).Length;
            long copiedLength = new FileInfo(destination).Length;
            if (copiedLength != sourceLength)
            {
                throw new BuildFailedException(
                    $"[BackendBuildPostprocessor] Backend copy is {copiedLength} B but the source " +
                    $"is {sourceLength} B. Destination={destination}");
            }

            Debug.Log(
                $"[BackendBuildPostprocessor] Backend copied from {sourceLabel} " +
                $"({sourceLength} B, {File.GetLastWriteTime(source):yyyy-MM-dd HH:mm:ss}) to {destination}");
        }

        /// <summary>
        /// Picks which executable to ship. <c>Builds/Backend/</c> wins over
        /// <c>backend/target/release/</c> on purpose, for two reasons that have both bitten this
        /// project: it is the location the EDITOR runs from (NetworkInitializer's first candidate),
        /// so preferring it means Play mode and the shipped build are the same binary instead of
        /// two that merely look alike; and <c>backend/target/</c> is shared with any concurrent
        /// tooling, so it can hold a link of half-finished work that was never validated.
        /// </summary>
        private static string ResolveSource(string projectRoot, out string label)
        {
            string deployed = Path.Combine(projectRoot, "Builds", "Backend", ExecutableName);
            if (File.Exists(deployed))
            {
                label = "Builds/Backend (deployed)";
                return deployed;
            }

            string built = Path.Combine(projectRoot, "backend", "target", "release", ExecutableName);
            if (File.Exists(built))
            {
                label = "backend/target/release (NOT deployed — Builds/Backend was empty)";
                return built;
            }

            label = null;
            return null;
        }

        /// <summary>
        /// Loud warning when the executable predates the newest backend source file. A warning and
        /// not a build failure: shipping an older backend on purpose is legitimate, and mtimes are
        /// noisy enough that failing on them would block real work. But a silently stale backend is
        /// this project's most repeated trap — a green Unity build carrying a server from before
        /// the fix it is supposed to contain.
        /// </summary>
        private static void WarnIfStale(string projectRoot, string source)
        {
            string sourceDir = Path.Combine(projectRoot, "backend", "src");
            if (!Directory.Exists(sourceDir)) return;

            DateTime exeTime = File.GetLastWriteTimeUtc(source);
            DateTime newest = DateTime.MinValue;
            string newestFile = null;
            foreach (string file in Directory.EnumerateFiles(sourceDir, "*.rs", SearchOption.AllDirectories))
            {
                DateTime t = File.GetLastWriteTimeUtc(file);
                if (t <= newest) continue;
                newest = t;
                newestFile = file;
            }

            if (newestFile != null && newest > exeTime)
            {
                Debug.LogWarning(
                    "[BackendBuildPostprocessor] The backend executable is OLDER than the Rust " +
                    $"sources. exe={exeTime:yyyy-MM-dd HH:mm:ss}Z, newest source={newest:yyyy-MM-dd HH:mm:ss}Z " +
                    $"({Path.GetFileName(newestFile)}). This build ships a server that does not " +
                    "contain the latest backend changes. Re-run cargo build --release and redeploy.");
            }
        }
    }
}
