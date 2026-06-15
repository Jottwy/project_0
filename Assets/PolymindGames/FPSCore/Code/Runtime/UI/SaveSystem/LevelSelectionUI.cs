using PolymindGames.SaveSystem;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Manages the UI for selecting and loading game levels.
    /// </summary>
    public sealed class LevelSelectionUI : MonoBehaviour
    {
        [SerializeField, NotNull]
        [Tooltip("The template used for creating level slots in the UI.")]
        private LevelDefinitionUI _levelTemplate;

        [SerializeField, NotNull]
        [Tooltip("The parent transform where level slots will be instantiated.")]
        private SelectableGroupBase _levelSlotsGroup;

        [SpaceArea]
        [SerializeField, ReorderableList(HasLabels = false)]
        [Tooltip("The list of game levels to display in the UI.")]
        private LevelDefinition[] _gameLevels;
        
        /// <summary>
        /// Loads the scene associated with the currently selected level.
        /// </summary>
        public void LoadSelectedLevel()
        {
            if (_levelSlotsGroup.Selected.TryGetComponent<LevelDefinitionUI>(out var selectedLevel))
            {
                LevelManager.Instance.CreateGame(selectedLevel.Data.LevelScene);
            }
            else
            {
                Debug.LogWarning("No level selected. Unable to load a level.");
            }
        }

        /// <summary>
        /// Initializes the level selection UI by creating slots for each level.
        /// </summary>
        private void Start()
        {
            var spawnTransform = _levelSlotsGroup.transform;
            for (int i = 0; i < _gameLevels.Length; i++)
            {
                var levelSlot = Instantiate(_levelTemplate, spawnTransform);
                levelSlot.SetData(_gameLevels[i]);
            }
        }
    }
}