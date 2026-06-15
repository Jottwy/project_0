using UnityEngine;

namespace PolymindGames
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/player/modules-and-behaviours/health#thirst-manager-module")]
    public sealed class ThirstManager : CharacterBehaviour, IThirstManagerCC
    {
        [SerializeField]
#if UNITY_EDITOR
        [DynamicRange(nameof(Editor_GetMinThirst), nameof(Editor_GetMaxThirst))]
#endif
        [Tooltip("The starting health of this character (can't be higher than the max health).")]
        private float _thirst = 100f;
        
        [SerializeField, Range(1, 1000f)]
#if UNITY_EDITOR
        [OnValueChanged(nameof(Editor_OnMaxThirstChanged))]
#endif
        [Tooltip("The starting max health of this character (can be modified at runtime).")]
        private float _maxThirst = 100f;

        [SerializeField, SpaceArea(3f)]
        private StatDepletionSettings _settings;

        private IHealthManager _health;

        public float Thirst
        {
             get => _thirst;
             set => _thirst = Mathf.Clamp(value, 0f, _maxThirst);
        }

        public float MaxThirst
        {
             get => _maxThirst;
             set 
             {
                _maxThirst = Mathf.Max(value, 0f);
                Thirst = Mathf.Clamp(Thirst, 0f, _maxThirst);
             }
        }
        
        protected override void OnBehaviourStart(ICharacter character) 
        {
             _health = character.HealthManager;
             _health.Respawn += OnRespawn;
        }

        private void OnRespawn() => _thirst = _maxThirst;
        
        private void Update()
        {
             if (_health.IsAlive())
                 _settings.UpdateStat(ref _thirst, _maxThirst, Time.deltaTime, _health);
        }
        
        #region Editor
#if UNITY_EDITOR
#pragma warning disable CS0628
        protected void Editor_OnMaxThirstChanged() => Thirst = _thirst;
        protected float Editor_GetMinThirst() => 0f;
        protected float Editor_GetMaxThirst() => _maxThirst;
#pragma warning restore CS0628
#endif
        #endregion
    }
}