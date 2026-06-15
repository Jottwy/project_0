#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// One-shot setup for the menu -> connect -> gameplay flow:
    ///   * Injects <see cref="NetworkMenuBootstrap"/> into the main-menu scene (the
    ///     "Multiplayer" button onClick is wired by hand to ShowConnectPanel()).
    ///   * Injects the <see cref="GameBootstrap"/> boot stack (NetworkInitializer,
    ///     GameBootGateBinder, RemotePlayerManager, renderers + JoinSessionUI fallback)
    ///     into the gameplay scene, so it spawns the player once IPC is connected.
    ///   * Verifies the menu + gameplay scenes are in Build Settings.
    ///
    /// Idempotent: safe to re-run. UnityEvent (button onClick) wiring is not automated.
    /// </summary>
    public static class NetworkFlowSetup
    {
        private const string GameplaySceneName = "STP_Showcase";
        private const string MenuSceneName = "STP_MainMenu";

        [MenuItem("Backrooms/Setup Network Flow")]
        public static void Setup()
        {
            string gameplayPath = FindScenePath(GameplaySceneName);
            string menuPath = FindScenePath(MenuSceneName);

            if (gameplayPath == null)
            {
                Debug.LogError($"[NetworkFlowSetup] Gameplay scene '{GameplaySceneName}' not found.");
                return;
            }

            EnsureComponentInScene<GameBootstrap>(gameplayPath, "__NetworkBoot");

            if (menuPath != null)
                EnsureComponentInScene<NetworkMenuBootstrap>(menuPath, "__NetworkMenu");
            else
                Debug.LogWarning($"[NetworkFlowSetup] Menu scene '{MenuSceneName}' not found; skipped menu injection.");

            EnsureInBuildSettings(MenuSceneName, GameplaySceneName);

            Debug.Log("[NetworkFlowSetup] Done. Next: in STP_MainMenu, re-wire the Multiplayer button's " +
                      "onClick (on the scene instance) -> NetworkMenuBootstrap.ShowConnectPanel().");
        }

        private static void EnsureComponentInScene<T>(string scenePath, string goName) where T : Component
        {
            // Don't disturb a scene the user already has open: reuse it if loaded,
            // otherwise open additively and close again when done.
            Scene scene = EditorSceneManager.GetSceneByPath(scenePath);
            bool wasAlreadyOpen = scene.IsValid() && scene.isLoaded;
            if (!wasAlreadyOpen)
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

            try
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.GetComponentInChildren<T>(true) != null)
                    {
                        Debug.Log($"[NetworkFlowSetup] {typeof(T).Name} already present in '{scene.name}'. Skipping.");
                        return;
                    }
                }

                var go = new GameObject(goName);
                SceneManager.MoveGameObjectToScene(go, scene);
                go.AddComponent<T>();
                Undo.RegisterCreatedObjectUndo(go, $"Create {goName}");

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[NetworkFlowSetup] Added '{goName}' ({typeof(T).Name}) to '{scene.name}'.");
            }
            finally
            {
                if (!wasAlreadyOpen && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void EnsureInBuildSettings(params string[] sceneNames)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            bool changed = false;

            foreach (var name in sceneNames)
            {
                string path = FindScenePath(name);
                if (path == null)
                {
                    Debug.LogWarning($"[NetworkFlowSetup] Cannot add '{name}' to Build Settings: scene not found.");
                    continue;
                }

                if (scenes.Exists(s => s.path == path))
                    continue;

                scenes.Add(new EditorBuildSettingsScene(path, true));
                changed = true;
                Debug.Log($"[NetworkFlowSetup] Added '{name}' to Build Settings.");
            }

            if (changed)
                EditorBuildSettings.scenes = scenes.ToArray();
            else
                Debug.Log("[NetworkFlowSetup] Build Settings already contain the required scenes.");
        }

        private static string FindScenePath(string sceneName)
        {
            foreach (var guid in AssetDatabase.FindAssets($"{sceneName} t:Scene"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == sceneName)
                    return path;
            }
            return null;
        }
    }
}
#endif
