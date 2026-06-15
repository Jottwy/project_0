using PolymindGames.PostProcessing;
using PolymindGames.InputSystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    public sealed class PauseMenu : CharacterUIBehaviour
    {
        [SerializeField]
        private InputContext _context;

        [SerializeField]
        private SerializedScene _mainMenuScene;

        [SerializeField, SpaceArea]
        private UIPanel _panel;

        [SerializeField, Range(0f, 1f)]
        private float _timeScale = 0.1f;
        
        [SerializeField]
        private VolumeAnimationProfile _volumeAnimation;

        [SerializeField, SpaceArea]
        private UnityEvent _onPause;

        [SerializeField]
        private UnityEvent _onResume;

        private float _pauseTimer;

        public void TryPause()
        {
            if (Time.time > _pauseTimer && !InputManager.Instance.HasEscapeCallbacks)
            {
                _pauseTimer = Time.time + 0.3f;
                _panel.Show();
            }
        }

        public void QuitToMenu() => LevelManager.Instance.CloseCurrentGame(_mainMenuScene.SceneName);
        public void QuitToDesktop() => LevelManager.Instance.FadeInAndQuitGame();

        protected override void OnCharacterAttached(ICharacter character)
        {
            character.HealthManager.Death += OnDeath;
            _panel.PanelStateChanged += OnPanelStateChanged;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            character.HealthManager.Death -= OnDeath;
            _panel.PanelStateChanged -= OnPanelStateChanged;
            Resume();
        }

        private void OnDeath(in DamageArgs args)
        {
            _panel.Hide();
            foreach (var panel in gameObject.GetComponentsInFirstChildren<UIPanel>())
                panel.Hide();
        }

        private void OnPanelStateChanged(bool show)
        {
            if (show) Pause();
            else Resume();
        }

        private void Pause()
        {
            Time.timeScale = _timeScale;
            InputManager.Instance.PushContext(_context);
            PostProcessingManager.Instance.PlayAnimation(this, _volumeAnimation, 0f, true);
            
            _onPause.Invoke();
        }

        private void Resume()
        {
            Time.timeScale = 1f;
            InputManager.Instance.PopContext(_context);
            PostProcessingManager.Instance.CancelAnimation(this, _volumeAnimation);
            
            _onResume.Invoke();
        }
    }
}