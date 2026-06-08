using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BackroomsSurvival.Editor
{
    public sealed class BackendBuildPostprocessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.summary.outputPath))
            {
                Debug.LogWarning("[BackendBuildPostprocessor] Build output path unavailable; backend was not copied");
                return;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string source = Path.Combine(projectRoot, "backend", "target", "release", "backrooms_server.exe");

            if (!File.Exists(source))
            {
                Debug.LogWarning(
                    "[BackendBuildPostprocessor] Backend release executable not found. " +
                    "Run cargo build --release before building Unity.");
                Debug.LogWarning($"[BackendBuildPostprocessor] Missing source={source}");
                return;
            }

            string buildFolder = Path.GetDirectoryName(report.summary.outputPath);
            if (string.IsNullOrWhiteSpace(buildFolder))
            {
                Debug.LogWarning("[BackendBuildPostprocessor] Build folder unavailable; backend was not copied");
                return;
            }

            string backendFolder = Path.Combine(buildFolder, "Backend");
            Directory.CreateDirectory(backendFolder);

            string destination = Path.Combine(backendFolder, "backrooms_server.exe");
            File.Copy(source, destination, true);

            Debug.Log($"[BackendBuildPostprocessor] Copied backend executable to {destination}");
        }
    }
}
