using PolymindGames.SaveSystem;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class SaveLoadUI : MonoBehaviour
    {
        [SerializeField]
        private Mode _defaultMode = Mode.None;

        [SerializeField, NotNull]
        private TextMeshProUGUI _header;

        [SerializeField, NotNull]
        private RectTransform _saveFilesRoot;

        [SerializeField, Range(1, SaveLoadManager.MaxSaveFiles)]
        private int _saveFilesCount = 4;

        [SerializeField, NotNull]
        private SaveFileUI _saveFileTemplate;

        private SaveFileUI[] _saveFileDisplays;
        private bool _isRefreshed = false;
        private Mode _currentMode = Mode.None;

        private const string SaveGame = "Save Game";
        private const string LoadGame = "Load Game";

        /// <summary>
        /// Clears all save slots and refreshes UI.
        /// </summary>
        public void DeleteSaveFiles()
        {
            for (int i = 0; i < SaveLoadManager.MaxSaveFiles; i++)
                SaveLoadManager.DeleteSaveFile(i);

            foreach (SaveFileUI saveFileUI in _saveFileDisplays)
                saveFileUI.AttachToMetadata(null);
        }

        public void EnableSaveMode() => SetMode(Mode.Save);
        public void EnableLoadMode() => SetMode(Mode.Load);

        public void RefreshSaveSlots()
        {
            if (_isRefreshed)
                return;

            // Load save information and update the UI and clear the remaining displays.
            var metadata = SaveLoadManager.LoadMetadatas(_saveFileDisplays.Length);

            foreach (var display in _saveFileDisplays)
                display.AttachToMetadata(null);

            foreach (var saveFileMetadata in metadata)
                _saveFileDisplays[saveFileMetadata.SaveIndex].AttachToMetadata(saveFileMetadata);

            _isRefreshed = true;
        }

        private void SetMode(Mode mode)
        {
            if (_currentMode == mode)
                return;

            _currentMode = mode;
            _header.text = mode switch
            {
                Mode.None => string.Empty,
                Mode.Load => LoadGame,
                Mode.Save => SaveGame,
                _ => string.Empty
            };
        }

        private void Awake()
        {
            _saveFileDisplays = new SaveFileUI[_saveFilesCount];
            for (int i = 0; i < _saveFilesCount; i++)
            {
                var saveFileDisplay = Instantiate(_saveFileTemplate, _saveFilesRoot);
                saveFileDisplay.SaveFileIndex = i;
                saveFileDisplay.ButtonSelectable.Clicked += OnSaveFileClicked;
                _saveFileDisplays[i] = saveFileDisplay;
            }
            
            SetMode(_defaultMode);
        }

        private void OnSaveFileClicked(SelectableButton selectable)
        {
            var saveFileDisplay = selectable.GetComponent<SaveFileUI>();
            switch (_currentMode)
            {
                case Mode.Save: SaveGameToIndex(saveFileDisplay); break;
                case Mode.Load: LoadGameFromSaveFile(saveFileDisplay); break;
                default: throw new ArgumentOutOfRangeException(nameof(_currentMode), _currentMode, "Invalid mode");
            }
        }

        private static void SaveGameToIndex(SaveFileUI saveFileDisplay)
        {
            if (LevelManager.Instance.SaveCurrentGame(saveFileDisplay.SaveFileIndex, out var metadata))
                saveFileDisplay.AttachToMetadata(metadata);
        }

        private static void LoadGameFromSaveFile(SaveFileUI saveFileDisplay)
        {
            if (saveFileDisplay.Metadata != null)
                LevelManager.Instance.LoadGame(saveFileDisplay.SaveFileIndex);
        }

        #region Internal Types

        private enum Mode
        {
            None,
            Save,
            Load,
        }

        #endregion
    }
}