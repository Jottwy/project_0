using UnityEngine;

namespace PolymindGames
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/player/modules-and-behaviours/health#hunger-manager-module")]
    public sealed class HungerManager : CharacterBehaviour, IHungerManagerCC
    {
        [SerializeField]
#if UNITY_EDITOR
        [DynamicRange(nameof(Editor_GetMinHunger), nameof(Editor_GetMaxHunger))]
#endif
        [Tooltip("The starting health of this character (can't be higher than the max health).")]
        private float _hunger = 100f;

        [SerializeField, Range(1, 1000f)]
#if UNITY_EDITOR
        [OnValueChanged(nameof(Editor_OnMaxHungerChanged))]
#endif
        [Tooltip("The starting max health of this character (can be modified at runtime).")]
        private float _maxHunger = 100f;

        [SerializeField, SpaceArea(3f)]
        private StatDepletionSettings _settings;

        private IHealthManager _health;

        public float Hunger
        {
            get => _hunger;
            set => _hunger = Mathf.Clamp(value, 0f, _maxHunger);
        }

        public float MaxHunger
        {
            get => _maxHunger;
            set
            {
                _maxHunger = Mathf.Max(value, 0f);
                _hunger = Mathf.Clamp(_hunger, 0f, _maxHunger);
            }
        }

        protected override void OnBehaviourStart(ICharacter character)
        {
            _health = character.HealthManager;
            _health.Respawn += OnRespawn;
        }

        private void OnRespawn() => _hunger = _maxHunger;

        private void Update()
        {
            if (_health.IsAlive())
                _settings.UpdateStat(ref _hunger, _maxHunger, Time.deltaTime, _health);
        }

        #region Editor
#if UNITY_EDITOR
        private void Editor_OnMaxHungerChanged() => Hunger = _hunger;
        private float Editor_GetMinHunger() => 0f;
        private float Editor_GetMaxHunger() => _maxHunger;
#endif
        #endregion
    }
}