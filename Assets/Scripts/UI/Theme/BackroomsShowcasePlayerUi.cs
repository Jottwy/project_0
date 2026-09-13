using PolymindGames.UserInterface;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// El inventario nuevo también en <c>STP_Showcase</c> (Joel, 2026-09-13, opción A: solo la interfaz). La escena es
    /// del vendor —un reimport la pisa— así que NO se edita: al cargarla, se instancia <c>BR_UI_Player</c> antes de que
    /// el <c>GameMode</c> cree la suya. <c>GameMode.Start</c> hace <c>PlayerUI.Instance ?? SpawnPlayerUI()</c> al menos
    /// un frame después, y <c>sceneLoaded</c> llega antes de cualquier <c>Start</c> de la escena.
    ///
    /// El jugador sigue siendo <c>STP_Player</c> con sus 6 contenedores: sin mochilas, carga ni cuerpo por zonas
    /// (ADR-147 enm. 1 y ADR-149 R0 siguen solo en <c>BR_InventoryTest</c>). Lo que la variante enseña de esos
    /// prototipos y el jugador no tiene lo esconde <see cref="BackroomsHideUnboundContainers"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/UI/Showcase Player UI", fileName = "BackroomsShowcasePlayerUi")]
    public sealed class BackroomsShowcasePlayerUi : ScriptableObject
    {
        public const string ResourcePath = "UI/BackroomsShowcasePlayerUi";
        public const string SceneName = "STP_Showcase";

        [SerializeField]
        private PlayerUI _playerUI;

        public PlayerUI PlayerUIPrefab => _playerUI;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>
        /// El proyecto entra en Play con «Reload Scene» desactivado (EditorSettings, Enter Play Mode Options = 2): la escena
        /// que ya estaba abierta NO dispara <c>sceneLoaded</c>, y el enganche no llegaba nunca (medido en Play por Joel,
        /// 2026-09-13). Aquí se miran las escenas ya cargadas; si <c>sceneLoaded</c> sí llegó, <c>PlayerUI.Instance</c> ya existe
        /// y no se duplica.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallInLoadedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) OnSceneLoaded(scene, LoadSceneMode.Additive);
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ShouldInstall(scene.name, PlayerUI.Instance != null)) return;

            var hook = Resources.Load<BackroomsShowcasePlayerUi>(ResourcePath);
            if (hook == null || hook._playerUI == null)
            {
                Debug.LogWarning($"[ShowcaseUI] falta Resources/{ResourcePath} o su PlayerUI: {SceneName} sigue con el inventario del vendor.");
                return;
            }

            var ui = Instantiate(hook._playerUI);
            SceneManager.MoveGameObjectToScene(ui.gameObject, scene);
            ui.gameObject.AddComponent<BackroomsHideUnboundContainers>();
            Debug.Log($"[ShowcaseUI] {SceneName}: interfaz {hook._playerUI.name} instanciada antes que la del GameMode.");
        }

        /// <summary>Solo en la escena jugable y solo si nadie ha puesto ya una PlayerUI. Pura, con test.</summary>
        public static bool ShouldInstall(string sceneName, bool playerUIExists) => sceneName == SceneName && !playerUIExists;
    }
}
