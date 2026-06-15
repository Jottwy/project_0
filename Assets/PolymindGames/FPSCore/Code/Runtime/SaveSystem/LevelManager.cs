using PolymindGames.PostProcessing;
using UnityEngine.SceneManagement;
using PolymindGames.UserInterface;
using PolymindGames.InputSystem;
using PolymindGames.SaveSystem;
using System.Threading.Tasks;
using System.Collections;
using UnityEngine;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Manages level loading, saving, and scene transitions within the game.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    [CreateAssetMenu(menuName = CreateMenuPath + "Level Manager", fileName = nameof(LevelManager))]
    public sealed partial class LevelManager : Manager<LevelManager>
    {
        /// <summary>
        /// Mode for saving game thumbnails during game saves.
        /// </summary>
        private enum ThumbnailSaveMode
        {
            /// <summary>Use a predefined scene thumbnail.</summary>
            SceneThumbnail = 0,

            /// <summary>Take a screenshot of the game screen.</summary>
            GameScreenshot = 1
        }

        [SerializeField]
        [Tooltip("Mode for saving game thumbnails.")]
        private ThumbnailSaveMode _thumbnailSaveMode;

        [SerializeField, PrefabObjectOnly]
        private FadeScreen _fadeScreenPrefab;

        private Coroutine _currentSceneLoadCoroutine;
        private FadeScreen _fadeScreenInstance;
        private SceneLoader _sceneLoader;
        private Guid _currentGameID;

        #region Initialization
        private sealed class SceneLoader : MonoBehaviour
        { }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
#else
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        private static void Init() => LoadOrCreateInstance();

        protected override void OnInitialized()
        {
            _currentSceneLoadCoroutine = null;


            var rootObject = CreateChildTransformForManager("LevelManagerRuntimeObject").gameObject;
            
            // TODO: Implement auto saving
            // rootObject.AddComponent<AutoSaveHandler>();
            
            _sceneLoader = rootObject.AddComponent<SceneLoader>();
            
#if UNITY_EDITOR
            CoroutineUtility.InvokeNextFrame(_sceneLoader, TryCreateGame);
#endif

            if (_fadeScreenPrefab != null)
            {
                _fadeScreenInstance = Instantiate(_fadeScreenPrefab, rootObject.transform);
                _sceneLoader.StartCoroutine(_fadeScreenInstance.FadeOut(0.5f));
            }

            void TryCreateGame()
            {
                _currentGameID = SceneSaveHandler.TryGetHandler(SceneManager.GetActiveScene(), out var handler)
                    ? Guid.NewGuid()
                    : Guid.Empty;
            }
        }
        #endregion

        /// <summary>
        /// Gets the current game ID, returns an empty guid if invalid.
        /// </summary>
        public Guid CurrentGameID => _currentGameID;

        /// <summary>
        /// Creates a new game and loads the specified scene.
        /// </summary>
        /// <param name="sceneName">The name of the scene to load for the new game.</param>
        /// <returns>True if the scene was loaded and the game was created successfully, otherwise false.</returns>
        public bool CreateGame(string sceneName)
        {
            if (LoadScene(sceneName))
            {
                _currentGameID = Guid.NewGuid();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Attempts to load a game save by index.
        /// </summary>
        /// <param name="saveFileIndex">The index of the save file to load.</param>
        /// <returns>True if the save file was loaded successfully, otherwise false.</returns>
        public bool LoadGame(int saveFileIndex)
        {
            SaveLoadManager.ThrowIfSaveIndexOutOfRange(saveFileIndex);
            
            if (IsLoadingOrSaving())
                return false;

            _currentSceneLoadCoroutine = _sceneLoader.StartCoroutine(LoadGameRoutine(saveFileIndex));
            return true;
        }
        
        /// <summary>
        /// Attempts to save the current game to the specified save file index.
        /// </summary>
        /// <param name="saveFileIndex">The index of the save file to save to.</param>
        /// <returns>True if the game was saved successfully, otherwise false.</returns>
        public bool SaveCurrentGame(int saveFileIndex) => SaveCurrentGame(saveFileIndex, out _);

        /// <summary>
        /// Attempts to save the current game to the specified save file index.
        /// </summary>
        /// <param name="saveFileIndex">The index of the save file to save to.</param>
        /// <param name="metadata">The generated game metadata.</param>
        /// <returns>True if the game was saved successfully, otherwise false.</returns>
        public bool SaveCurrentGame(int saveFileIndex, out GameMetadata metadata)
        {
            SaveLoadManager.ThrowIfSaveIndexOutOfRange(saveFileIndex);
            
            if (IsLoadingOrSaving() || !IsCurrentGameValid())
            {
                metadata = null;
                return false;
            }
            
            var saveData = GenerateGameSaveData(saveFileIndex);
            metadata = saveData.Metadata;
            _sceneLoader.StartCoroutine(SaveGameRoutine(saveData, saveFileIndex));
            return true;
        }

        /// <summary>
        /// Closes the current game and loads the specified scene.
        /// </summary>
        /// <param name="sceneName">The name of the scene to load after closing the current game.</param>
        /// <returns>True if the current game was closed and the scene was loaded successfully, otherwise false.</returns>
        public bool CloseCurrentGame(string sceneName)
        {
            ThrowIfSceneDoesNotExist(sceneName);
            
            if (IsLoadingOrSaving())
                return false;
            
            _currentGameID = Guid.Empty;
            _currentSceneLoadCoroutine = _sceneLoader.StartCoroutine(LoadSceneRoutine(sceneName));
            return true;
        }

        /// <param name="sceneName">The name of the scene to load.</param>
        /// <returns>True if the scene was loaded successfully, otherwise false.</returns>
        public bool LoadScene(string sceneName)
        {
            ThrowIfSceneDoesNotExist(sceneName);
            
            if (IsLoadingOrSaving())
                return false;

            _currentSceneLoadCoroutine = _sceneLoader.StartCoroutine(LoadSceneRoutine(sceneName));
            return true;
        }
        
        public void FadeInAndQuitGame()
        {
            if (IsLoadingOrSaving())
                return;
            
            InputManager.Instance.PushContext(InputContext.NullContext);
            _currentSceneLoadCoroutine = _sceneLoader.StartCoroutine(CoroutineUtility.InvokeAfter(_fadeScreenInstance.FadeIn(1.5f), QuitGame));
        }
        
        public void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.ExitPlaymode();
#else
            Application.Quit();
#endif
        }
        
        public bool IsLoadingOrSaving() => _currentSceneLoadCoroutine != null;
        public bool IsCurrentGameValid() => _currentGameID != Guid.Empty;

        /// <summary>
        /// Coroutine to load a specific scene with a loading screen and fade transitions.
        /// </summary>
        /// <param name="sceneName">The name of the scene to load.</param>
        private IEnumerator LoadSceneRoutine(string sceneName)
        {
            InputManager.Instance.PushContext(InputContext.NullContext);

            yield return LoadSceneWithLoadingScreen(sceneName);

            InputManager.Instance.PopContext(InputContext.NullContext);
            _currentSceneLoadCoroutine = null;
        }

        /// <summary>
        /// Coroutine to load a saved game.
        /// </summary>
        /// <param name="saveIndex">The index of the save file to load.</param>
        private IEnumerator LoadGameRoutine(int saveIndex)
        {
            var saveData = SaveLoadManager.LoadSaveDataFromFile(saveIndex);
            if (saveData == null)
                yield break;

            _currentGameID = saveData.Metadata.SaveId;
            var activeSceneSaveData = saveData.SceneData[0];

            yield return LoadSceneRoutine(activeSceneSaveData.SceneName);

            var activeScene = SceneManager.GetActiveScene();
            var sceneSaveHandler = SceneSaveHandler.GetHandler(activeScene);
            sceneSaveHandler.ApplySceneSaveData(activeSceneSaveData);

            _currentSceneLoadCoroutine = null;
        }

        /// <summary>
        /// Coroutine to save the current game.
        /// </summary>
        /// <param name="saveData"></param>
        /// <param name="saveIndex">The index of the save file to write to.</param>
        private IEnumerator SaveGameRoutine(GameSaveData saveData, int saveIndex)
        {
            yield return new WaitForEndOfFrame();

            var saveTask = Task.Run(() => SaveLoadManager.SaveDataToFile(saveData, saveIndex));
            yield return new WaitUntil(() => saveTask.IsCompleted);

            _currentSceneLoadCoroutine = null;
        }

        // TODO: Implement Loading Screen
        /// <summary>
        /// Loads a target scene with a loading screen, ensuring the loading screen stays visible until the loading is complete.
        /// </summary>
        /// <param name="sceneName">The name of the target scene.</param>
        private IEnumerator LoadSceneWithLoadingScreen(string sceneName)
        {
            yield return FadeIn();
            
            PostProcessingManager.Instance.CancelAllAnimations();

            AsyncOperation loadTargetScene = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            while (!loadTargetScene.isDone)
                yield return null;

            FadeOut();
        }

        private IEnumerator FadeIn()
        {
            if (_fadeScreenInstance != null)
                yield return _fadeScreenInstance.FadeIn();
        }

        private void FadeOut()
        {
            if (_fadeScreenInstance != null)
                _sceneLoader.StartCoroutine(_fadeScreenInstance.FadeOut());
        }

        /// <summary>
        /// Generates game save data for the current state.
        /// </summary>
        /// <returns>A new <see cref="GameSaveData"/> instance.</returns>
        private GameSaveData GenerateGameSaveData(int saveFileIndex)
        {
            var activeScene = SceneManager.GetActiveScene();
            var sceneSaveHandler = SceneSaveHandler.GetHandler(activeScene);
            var sceneSaveData = sceneSaveHandler.GenerateSceneSaveData();
            var thumbnail = GetThumbnail(sceneSaveHandler);
            var gameMetadata = new GameMetadata(_currentGameID, saveFileIndex, sceneSaveHandler.Scene.name,
                sceneSaveHandler.LevelDefinition.LevelName, DateTime.Now, thumbnail, sceneSaveHandler.CollectSceneInfoData());

            return new GameSaveData(gameMetadata, sceneSaveData);
        }

        private static bool SceneExists(string sceneName)
        {
            int sceneIndex = SceneUtility.GetBuildIndexByScenePath(sceneName);
            return sceneIndex != -1;
        }

        private static void ThrowIfSceneDoesNotExist(string sceneName)
        {
            if (!SceneExists(sceneName))
                throw new ArgumentException($"Scene '{sceneName}' does not exist.");
        }

        private Texture2D GetThumbnail(SceneSaveHandler sceneSaveHandler)
        {
            return _thumbnailSaveMode switch
            {
                ThumbnailSaveMode.SceneThumbnail => sceneSaveHandler.LevelDefinition != null
                    ? sceneSaveHandler.LevelDefinition.LevelIcon.texture
                    : TakeScreenshot(),
                ThumbnailSaveMode.GameScreenshot => TakeScreenshot(),
                _ => null
            };
        }

        private static Texture2D TakeScreenshot()
        {
            var texture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            var rect = new Rect(0, 0, Screen.width, Screen.height);
            texture.ReadPixels(rect, 0, 0);
            texture.Apply();
            return texture;
        }

        // TODO: Implement streaming
        #region Streaming
        // public bool LoadSceneStreaming(string sceneName)
        // {
        //     if (!DoesSceneExist(sceneName))
        //         throw new ArgumentException($"Scene '{sceneName}' does not exist.");
        //
        //     if (!IsCurrentGameValid())
        //         throw new InvalidOperationException("Current game is not valid, it needs to be loaded or created first.");
        //     
        //     if (ScenesUtility.IsSceneLoaded(sceneName))
        //         throw new ArgumentException($"Scene '{sceneName}' is already loaded.");
        //     
        //     SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive)
        //         .completed += OnLoaded;
        //
        //     return true;
        //
        //     void OnLoaded(AsyncOperation operation)
        //     {
        //         Debug.Log($"Loaded Scene {sceneName}");
        //     }
        // }
        //
        // public bool UnloadSceneStreaming(string sceneName, UnloadSceneOptions unloadOptions)
        // {
        //     if (!DoesSceneExist(sceneName))
        //         throw new ArgumentException($"Scene '{sceneName}' does not exist.");
        //
        //     if (!ScenesUtility.IsSceneLoaded(sceneName))
        //         throw new ArgumentException($"Scene '{sceneName}' is not loaded.");
        //     
        //     SceneManager.UnloadSceneAsync(sceneName, unloadOptions)
        //         .completed += OnUnloaded;
        //
        //     return true;
        //     
        //     void OnUnloaded(AsyncOperation operation)
        //     {
        //         Debug.Log($"Unloaded Scene {sceneName}");
        //     }
        // }
        #endregion
    }
}