using PolymindGames.Options;
using System.Collections;
using UnityEngine;

namespace PolymindGames.SaveSystem
{
    /// <summary>
    /// Manages the automatic saving of game levels based on user preferences.
    /// </summary>
    public sealed class AutoSaveHandler : MonoBehaviour
    {
        private const int AutoSaveIndex = 0;
        private Coroutine _autoSaveRoutine;
        private float _autosaveTimer;
            
        private void Start()
        {
            CoroutineUtility.InvokeNextFrame(this, () =>
            {
                GameplayOptions.Instance.AutosaveEnabled.Changed += OnAutosaveSettingChanged;
                OnAutosaveSettingChanged(GameplayOptions.Instance.AutosaveEnabled);
            });

            Application.quitting += () => 
                GameplayOptions.Instance.AutosaveEnabled.Changed -= OnAutosaveSettingChanged;
        }
            
        /// <summary>
        /// Handles changes to the autosave setting and starts or stops the auto-save routine accordingly.
        /// </summary>
        /// <param name="value">True if autosave is enabled, false otherwise.</param>
        private void OnAutosaveSettingChanged(bool value)
        {
            if (value) 
                CoroutineUtility.StartOrReplaceCoroutine(this, AutoSaveRoutine(), ref _autoSaveRoutine);
            else 
                CoroutineUtility.StopCoroutine(this, ref _autoSaveRoutine);
        }

        /// <summary>
        /// Continuously saves the current game state at specified intervals.
        /// </summary>
        /// <returns>An IEnumerator that allows the saving to be paused or resumed via coroutines.</returns>
        private IEnumerator AutoSaveRoutine()
        {
            _autosaveTimer = Time.time + GameplayOptions.Instance.AutosaveInterval;
            while (true)
            {
                if (Time.time < _autosaveTimer)
                    yield return null;

                _autosaveTimer = Time.time + GameplayOptions.Instance.AutosaveInterval;
                LevelManager.Instance.SaveCurrentGame(AutoSaveIndex);
                yield return null;
            }
        }
    }
}