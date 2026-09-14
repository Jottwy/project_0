using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InputSystem;
using PolymindGames.MovementSystem;
using PolymindGames.UserInterface;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.5 de MAPPING-PROTOTYPE — el recuerdo y la libreta en el JUEGO: con el jugador STP local, crea el
    /// <see cref="MapMemorySampler"/> y una <see cref="MapNotebook"/>, y monta la pestaña «Notas» en su libro de
    /// supervivencia. Todo en runtime: no edita escenas ni prefabs.
    /// </summary>
    /// <remarks>
    /// **Solo en el editor** (<see cref="EnabledInBuilds"/>): es un prototipo P0, local y sin guardado, y no debe acabar
    /// en una build. En la escena <c>MappingPlaytest</c> (con <see cref="MapNotebookView"/>) no hace nada.
    ///
    /// Se arranca con <c>RuntimeInitializeOnLoadMethod(AfterSceneLoad)</c>, que sí salta al dar Play sobre la escena
    /// abierta (<c>sceneLoaded</c> no). Busca al jugador cada 2 s: el de STP aparece con la sesión, no con la escena, y
    /// al reaparecer es otro objeto (con otro libro).
    /// </remarks>
    public sealed class MappingRuntime : MonoBehaviour
    {
        /// <summary>El prototipo no entra en builds.</summary>
        public const bool EnabledInBuilds = false;

        private const float LookupSeconds = 2f;
        // Los mismos que la libreta de prueba (MapNotebookView), validados por Joel el 2026-09-13.
        private const uint PenArgb = 0xF22A47A8u;
        private const float PenWidthPx = 3f;
        private const float PenCostPerMetre = 0.002f;
        private const uint PaperArgb = 0xFFF1EFE5u;

        private static MappingRuntime _instance;

        private MapMemorySampler _sampler;
        private MapNotebook _notebook;
        private CharacterControllerMotor _player;
        private MapNotebookBookTab _tab;
        private float _nextLookup;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Application.isEditor && !EnabledInBuilds) return;
            if (_instance != null) return;

            var go = new GameObject("MappingRuntime");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MappingRuntime>();
        }

        private const string FlipSoundPath = "Assets/PolymindGames/STP/Data/Audio/Wieldables/STP_Book_FlipPage.asset";

        private void Update()
        {
            HandleNotesKey();
            if (Time.unscaledTime < _nextLookup) return;
            _nextLookup = Time.unscaledTime + LookupSeconds;

            // La escena de playtest ya trae su libreta y su muestreador.
            if (FindAnyObjectByType<MapNotebookView>() != null) return;

            CharacterControllerMotor player = LocalPlayerLocator.Find<CharacterControllerMotor>();
            if (player == null) return;

            if (_sampler == null)
            {
                _sampler = gameObject.AddComponent<MapMemorySampler>();
                _notebook = new MapNotebook(new MapPen("Boli azul", PenArgb, PenWidthPx, PenCostPerMetre, 1f))
                {
                    Log = message => Debug.Log(message),
                };
            }

            if (player != _player)
            {
                _player = player;
                _sampler.target = player.transform;
                _tab = null;
                Debug.Log($"MAPBOOK player={player.name}");
            }

            if (_tab == null)
            {
                SurvivalBookUI book = player.GetComponentInChildren<SurvivalBookUI>(true);
                if (book != null) _tab = MapNotebookBookTab.Attach(book, _sampler, _notebook, PaperArgb, LoadFlipSound());
            }
        }

        /// <summary>
        /// Joel, 2026-09-14: la libreta también en <c>N</c>. Saca el MISMO libro (como <c>FPSSurvivalBookInput</c> con
        /// <c>B</c>) y lo abre en «Notas»; con el libro en la mano, lo guarda. Solo desde el contexto por defecto: con el
        /// inventario u otro menú abierto no hace nada. <c>N</c> es también <c>FireMode</c> del vendor: con un arma en la
        /// mano, pulsarla cambia el modo de disparo Y abre la libreta (deuda del prototipo, MAPPING-PROTOTYPE §5.8).
        /// </summary>
        private void HandleNotesKey()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.nKey.wasPressedThisFrame || _tab == null || _player == null) return;
            if (_tab.Wieldable == null) return;

            var character = _player.GetComponentInParent<ICharacter>();
            IWieldablesControllerCC controller = character != null ? character.GetCC<IWieldablesControllerCC>() : null;
            if (controller == null) return;

            if (ReferenceEquals(controller.ActiveWieldable, _tab.Wieldable))
            {
                controller.TryHolsterWieldable(_tab.Wieldable);
                Debug.Log("MAPBOOK key=N holster");
                return;
            }

            if (InputManager.Instance != null && !InputManager.Instance.IsDefaultContext()) return;
            _tab.OpenOnNotesNextTime();
            bool equipped = controller.TryEquipWieldable(_tab.Wieldable, 1.35f);
            Debug.Log($"MAPBOOK key=N equip={equipped}");
        }

        private static UnityEngine.Audio.AudioResource LoadFlipSound()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Audio.AudioResource>(FlipSoundPath);
#else
            return null;
#endif
        }
    }
}
