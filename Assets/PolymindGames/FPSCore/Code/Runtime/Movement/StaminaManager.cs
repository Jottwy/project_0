using System.Runtime.CompilerServices;
using UnityEngine.Events;
using UnityEngine;
using System;
using PolymindGames.SaveSystem;

namespace PolymindGames
{
    [RequireCharacterComponent(typeof(IMovementControllerCC))]
    public sealed class StaminaManager : CharacterBehaviour, IStaminaManagerCC, ISaveableComponent
    {
        [SerializeField]
#if UNITY_EDITOR
        [DynamicRange(nameof(Editor_GetMinStamina), nameof(Editor_GetMaxStamina))]
#endif
        [Tooltip("The starting health of this character (can't be higher than the max health).")]
        private float _stamina = 1f;

        [SerializeField, Range(1, 1000f)]
#if UNITY_EDITOR
        [OnValueChanged(nameof(Editor_OnMaxStaminaChanged))]
#endif
        [Tooltip("The starting max health of this character (can be modified at runtime).")]
        private float _maxStamina = 1f;

        [SerializeField, Range(0f, 5f), Title("Settings")]
        [Tooltip("How much time the stamina regeneration will be paused after it gets lowered.")]
        private float _regenerationPause = 1.35f;

        [SerializeField]
        [ReorderableList(ListStyle.Lined), LabelByChild("StateType")]
        private StaminaState[] _staminaStates = Array.Empty<StaminaState>();

        [SerializeField, Range(-1f, 25f), Title("Audio")]
        private float _breathingHeavyDuration = 6.5f;

        [SerializeField, IndentArea]
        [ShowIf(nameof(_breathingHeavyDuration), 0f, Comparison = UnityComparisonMethod.Greater)]
        private AudioData _breathingHeavyAudio = new(null);

        private static readonly StaminaState _defaultState = new(MovementStateType.Idle, 0f, 0f, 0.2f);

        private IMovementControllerCC _movement;
        private StaminaState _currentState;
        private bool _isMovementBlocked;
        private float _heavyBreathingTimer;
        private float _regenTimer;
        private bool _serverControlled;

        private const float HeavyBreathThreshold = 0.05f;
        private const float DisableMovementThreshold = 0.01f;
        private const float EnableMovementThreshold = 0.2f;

        public float Stamina
        {
            get => _stamina;
            set
            {
                // Ensure the new stamina value is within the valid range.
                float newStamina = Mathf.Clamp(value, 0, _maxStamina);

                // If the stamina value hasn't changed, no further action is needed.
                // ReSharper disable once CompareOfFloatsByEqualityOperator
                if (_stamina == newStamina)
                    return;

                // If the new stamina value is lower than the current stamina value,
                // set the regeneration timer to pause regeneration for a certain duration.
                if (newStamina < _stamina)
                    _regenTimer = Time.time + _regenerationPause;

                // Update the stamina value to the new calculated value.
                _stamina = newStamina;

                // Invoke the StaminaChanged event to notify subscribers about the change in stamina.
                StaminaChanged?.Invoke(_stamina);

                // Handle heavy breathing effect based on the current stamina level.
                HandleHeavyBreathing();

                // Handle movement blocking based on the current stamina level.
                HandleMovementBlocking();
            }
        }

        public float MaxStamina
        {
            get => _maxStamina;
            set
            {
                _maxStamina = Mathf.Max(value, 0f);
                Stamina = Mathf.Clamp(_stamina, 0f, _maxStamina);
            }
        }

        public event UnityAction<float> StaminaChanged;

        /// <summary>
        /// ADR-009: when true, the per-frame drain/regen in Update() is skipped — the
        /// server-authoritative value is driven externally via the Stamina setter
        /// (which still fires StaminaChanged and runs movement-blocking/audio).
        /// </summary>
        public void SetServerControlled(bool value) => _serverControlled = value;

        protected override void OnBehaviourStart(ICharacter character)
        {
            var health = character.HealthManager;
            health.Respawn += OnRespawn;
            health.Death += OnDeath;
            Stamina = MaxStamina;

            enabled = Character.HealthManager.IsAlive();
        }

        protected override void OnBehaviourDestroy(ICharacter character)
        {
            var health = character.HealthManager;
            health.Respawn -= OnRespawn;
            health.Death -= OnDeath;
        }

        protected override void OnBehaviourEnable(ICharacter character)
        {
            _movement ??= character.GetCC<IMovementControllerCC>();
            _movement.AddStateTransitionListener(MovementStateType.None, OnStateChanged);
            _currentState = GetStateOfType(_movement.ActiveState);

        }

        protected override void OnBehaviourDisable(ICharacter character)
        {
            _movement?.RemoveStateTransitionListener(MovementStateType.None, OnStateChanged);
        }

        private void OnDeath(in DamageArgs args) => enabled = false;

        private void OnRespawn()
        {
            Stamina = MaxStamina;
            enabled = true;
        }

        private void OnStateChanged(MovementStateType stateType)
        {
            Stamina += _currentState.ExitChange;
            _currentState = GetStateOfType(stateType);
            Stamina += _currentState.EnterChange;
        }

        private void Update()
        {
            // ADR-009: server owns stamina → the StatInterpolator drives the value
            // via the Stamina setter; skip the local per-frame drain/regen.
            if (_serverControlled)
                return;

            if (_currentState.ChangeRatePerSec < 0f) // Decrease stamina.
                Stamina += _currentState.ChangeRatePerSec * Time.deltaTime;
            else if (Time.time > _regenTimer) // Regenerate stamina.
                Stamina += _currentState.ChangeRatePerSec * Time.deltaTime;
        }

        private void HandleHeavyBreathing()
        {
            if (_stamina < HeavyBreathThreshold && _breathingHeavyDuration > 0f && _heavyBreathingTimer < Time.time)
            {
                Character.Audio.StartLoop(_breathingHeavyAudio, BodyPoint.Head, _breathingHeavyDuration);

                // Set the timer for when to stop the heavy breathing audio loop.
                _heavyBreathingTimer = Time.time + _breathingHeavyDuration;
            }
        }

        private void HandleMovementBlocking()
        {
            switch (_isMovementBlocked)
            {
                case false when _stamina < DisableMovementThreshold:
                    _movement.AddStateBlocker(this, MovementStateType.Jump);
                    _movement.AddStateBlocker(this, MovementStateType.Run);
                    _isMovementBlocked = true;
                    break;

                case true when _stamina > EnableMovementThreshold:
                    _movement.RemoveStateBlocker(this, MovementStateType.Jump);
                    _movement.RemoveStateBlocker(this, MovementStateType.Run);
                    _isMovementBlocked = false;
                    break;
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private StaminaState GetStateOfType(MovementStateType stateType)
        {
            foreach (var state in _staminaStates)
            {
                if (state.StateType == stateType)
                    return state;
            }

            return _defaultState;
        }

        #region Internal Types
        [Serializable]
        private sealed class StaminaState
        {
            public MovementStateType StateType;

            [SpaceArea(3f)]
            [Range(-1f, 1f)]
            public float EnterChange;

            [Range(-1f, 1f)]
            public float ExitChange;

            [Range(-1f, 1f)]
            public float ChangeRatePerSec;

            public StaminaState(MovementStateType stateType, float enterChange, float exitChange, float changeRatePerSec)
            {
                StateType = stateType;
                EnterChange = enterChange;
                ExitChange = exitChange;
                ChangeRatePerSec = changeRatePerSec;
            }
        }
        #endregion

        #region Save & Load
        [Serializable]
        private sealed class SaveData
        {
            public float Stamina;
            public float MaxStamina;
        }

        void ISaveableComponent.LoadMembers(object data)
        {
            var saveData = (SaveData)data;
            _stamina = saveData.Stamina;
            _maxStamina = saveData.MaxStamina;
        }

        object ISaveableComponent.SaveMembers() => new SaveData
        {
            Stamina = _stamina,
            MaxStamina = _maxStamina
        };
        #endregion

        #region Editor
#if UNITY_EDITOR
#pragma warning disable CS0628
        protected void Editor_OnMaxStaminaChanged() => Stamina = _stamina;
        protected float Editor_GetMinStamina() => 0f;
        protected float Editor_GetMaxStamina() => _maxStamina;
#pragma warning restore CS0628
#endif
        #endregion
    }
}