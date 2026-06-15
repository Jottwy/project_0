using PolymindGames.SaveSystem;
using UnityEngine.InputSystem;
using System.Collections;
using UnityEngine.UI;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Manages the level loading screen, including tips cycling and visual/audio transitions.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    public sealed class LevelLoadingScreenWithTips : LevelLoadingScreen
    {
        [SerializeField, NotNull, Title("Panels")]
        [Tooltip("The panel that contains the tips and loading bar UI.")]
        private UIPanel _loadingPanel;

        [SerializeField, NotNull]
        [Tooltip("The panel displayed after the level is loaded, prompting the player to press any button to continue.")]
        private UIPanel _postLoadPanel;

        [SerializeField, NotNull, Title("Info")]
        private Image _levelBackgroundImage;

        [SerializeField, NotNull]
        private TextMeshProUGUI _levelNameText;

        [SerializeField, NotNull]
        private TextMeshProUGUI _levelDescriptionText;
        
        [SerializeField]
        [Tooltip("Displays the current loading progress visually.")]
        private ProgressBarUI _progressBar;

        [SerializeField, Title("Tips")]
        [Tooltip("The text component for displaying the tip title.")]
        private TextMeshProUGUI _tipTitleText;

        [SerializeField]
        [Tooltip("The text component for displaying the tip description.")]
        private TextMeshProUGUI _tipDescriptionText;

        [SerializeField, Range(1f, 100f)]
        [Tooltip("The interval (in seconds) between changing tips.")]
        private float _tipCycleIntervalSeconds = 3f;

        [SerializeField, SpaceArea]
        [ReorderableList(HasLabels = false), IgnoreParent]
        [Tooltip("A collection of tips displayed during the loading process.")]
        private GameTip[] _gameTips;

        private LevelDefinition _level;
        private Coroutine _tipCyclingCoroutine;

        /// <inheritdoc/>
        public override void OnLevelLoadStart(string sceneName)
        {
            // TODO: Implement
            // _level = LevelDefinition.Find(definition => definition.LevelScene == sceneName);
            
            if (_level != null)
            {
                _levelNameText.text = _level.LevelName;
                _levelDescriptionText.text = _level.LevelDescription;
                _levelBackgroundImage.sprite = _level.LoadingImage;
                _loadingPanel.Show();
            }
            
            if (_gameTips.Length > 0)
                _tipCyclingCoroutine = StartCoroutine(CycleTipsRoutine());
        }

        /// <inheritdoc/>
        public override void OnLoadProgressChanged(float progress)
        {
            _progressBar.SetFillAmount(progress);
        }

        /// <inheritdoc/>
        public override IEnumerator OnLevelLoadComplete()
        {
            CoroutineUtility.StopCoroutine(this, ref _tipCyclingCoroutine);

            if (_level != null)
            {
                _loadingPanel.Hide();
                _postLoadPanel.Show();

                if (_level != null)
                    yield return new WaitUntil(IsAnyButtonPressed);
            }
        }

        /// <summary>
        /// Handles cycling through the tips displayed on the loading screen.
        /// </summary>
        private IEnumerator CycleTipsRoutine()
        {
            int currentTipIndex = UnityEngine.Random.Range(0, _gameTips.Length);
            UpdateDisplayedTip(currentTipIndex);

            while (true)
            {
                yield return new WaitForSeconds(_tipCycleIntervalSeconds);
                currentTipIndex = (currentTipIndex + 1) % _gameTips.Length;
                UpdateDisplayedTip(currentTipIndex);
            }
        }

        /// <summary>
        /// Updates the displayed tip based on the given index.
        /// </summary>
        /// <param name="index">The index of the tip to display.</param>
        private void UpdateDisplayedTip(int index)
        {
            var tip = _gameTips[index];
            _tipTitleText.text = tip.Title;
            _tipDescriptionText.text = tip.Description;
        }

        /// <summary>
        /// Determines if any button is currently pressed.
        /// </summary>
        /// <returns>True if any button is pressed; otherwise, false.</returns>
        private static bool IsAnyButtonPressed()
        {
            return Keyboard.current?.anyKey.isPressed == true;
        }

        #region Internal Types
        /// <summary>
        /// Represents a game tip with a title and description.
        /// </summary>
        [Serializable]
        private struct GameTip
        {
            [Tooltip("The title of the tip.")]
            public string Title;

            [Multiline]
            [Tooltip("A detailed description of the tip.")]
            public string Description;
        }
        #endregion
    }
}