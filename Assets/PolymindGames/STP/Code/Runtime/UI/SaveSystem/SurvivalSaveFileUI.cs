using PolymindGames.WorldManagement;
using PolymindGames.SaveSystem;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public sealed class SurvivalSaveFileUI : SaveFileUI
    {
        [SerializeField, NotNull, Line]
        private TextMeshProUGUI _saveGameTimeText;

        [SerializeField, NotNull]
        private TextMeshProUGUI _difficultyText;

        public override void AttachToMetadata(GameMetadata metadata)
        {
            base.AttachToMetadata(metadata);

            if (metadata != null)
            {
                _difficultyText.text = metadata.TryGetSavedValue<GameDifficulty>(SurvivalSceneSaveHandler.DifficultyKey, out var difficulty)
                    ? $"Difficulty:    {difficulty.ToString()}"
                    : string.Empty;

                _saveGameTimeText.text = metadata.TryGetSavedValue<GameTime>(SurvivalSceneSaveHandler.GameTimeKey, out var gameTime)
                    ? gameTime.FormatGameTimeWithPrefixes()
                    : string.Empty;
            }
            else
            {
                _difficultyText.text = string.Empty;
                _saveGameTimeText.text = string.Empty;
            }
        }
    }
}