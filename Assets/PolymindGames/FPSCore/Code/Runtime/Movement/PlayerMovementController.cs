using PolymindGames.InputSystem.Behaviours;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;
using System;
using PolymindGames.SaveSystem;

namespace PolymindGames.MovementSystem
{
    using Object = UnityEngine.Object;
    
    [RequireCharacterComponent(typeof(IMotorCC))]
    public sealed class PlayerMovementController : CharacterBehaviour, IMovementControllerCC, ISaveableComponent
    {
        [SerializeField, NotNull]
        [Tooltip("Input handler for FPS movement.")]
        private FPSMovementInput _inputHandler;

        [SerializeField, Range(0f, 10f), Title("Settings")]
        [Tooltip("Multiplier for movement speed.")]
        private float _speedMultiplier = 1f;

        [SerializeField, Range(1f, 100f)]
        private float _baseAcceleration = 8f;
    
        [SerializeField, Range(1f, 100f)]
        private float _baseDeceleration = 10f;

        [Title("Step Cycle"), SerializeField, Range(0.1f, 10f)]
        [Tooltip("Speed of transition between different step lengths.")]
        private float _stepLerpSpeed = 1.5f;

        [SerializeField, Range(0f, 10f)]
        [Tooltip("Step length when turning.")]
        private float _turnStepLength = 0.8f;

        [SerializeField, Range(0f, 10f)]
        [Tooltip("Maximum step velocity when turning.")]
        private float _maxTurnStepVelocity = 1.75f;

        [NewLabel("States"), Line]
        [SerializeReference, ReorderableList(elementLabel: "State")]
        [ReferencePicker(typeof(ICharacterMovementState), TypeGrouping.ByFlatName)]
        [Tooltip("List of default movement states.")]
        private ICharacterMovementState[] _defaultStates;
        
        private readonly UnityAction<MovementStateType>[] _stateEnterEvents = new UnityAction<MovementStateType>[MovementStateTypeUtility.TotalStateTypes];
        private readonly UnityAction<MovementStateType>[] _stateExitEvents = new UnityAction<MovementStateType>[MovementStateTypeUtility.TotalStateTypes];
        private readonly List<Object>[] _stateLockers = new List<Object>[MovementStateTypeUtility.TotalStateTypes];
        private readonly ICharacterMovementState[] _states = new ICharacterMovementState[MovementStateTypeUtility.TotalStateTypes];

        private ICharacterMovementState _activeState;
        private float _distMovedSinceLastCycleEnded;
        private float _currentStepLength = 1f;
        private IMotorCC _motor;
        
        public MovementStateType ActiveState { get; private set; }
        public MovementModifierGroup SpeedModifier { get; private set; }
        public MovementModifierGroup AccelerationModifier { get; private set; }
        public MovementModifierGroup DecelerationModifier { get; private set; }
        public float StepCycle { get; private set; }

        public event UnityAction StepCycleEnded;
        
        #region Initialization
        private void Awake()
        {
            SpeedModifier = new MovementModifierGroup(_speedMultiplier, SpeedModifier);
            AccelerationModifier = new MovementModifierGroup(_baseAcceleration, AccelerationModifier);
            DecelerationModifier = new MovementModifierGroup(_baseDeceleration, DecelerationModifier);
        }

        protected override void OnBehaviourStart(ICharacter character)
        {
            _motor = character.GetCC<IMotorCC>();

            foreach (var state in _defaultStates)
            {
                int stateIndex = (int)state.StateType;
                state.InitializeState(this, _inputHandler, _motor, character);
                _states[stateIndex] = state;
            }

            ActiveState = MovementStateType.Idle;
            EnterState(_states[(int)ActiveState]);
        }

        protected override void OnBehaviourEnable(ICharacter character) => _motor.SetMotionInput(GetMotionInput);
        protected override void OnBehaviourDisable(ICharacter character) => _motor.SetMotionInput(null);
        #endregion
        
        #region State Accessing
        public T GetStateOfType<T>() where T : ICharacterMovementState
        {
            foreach (var state in _states)
            {
                if (state is T matchedState)
                    return matchedState;
            }

            return default(T);
        }
        #endregion

        #region Update Loop
        private Vector3 GetMotionInput(Vector3 velocity, out bool useGravity, out bool snapToGround)
        {
            float deltaTime = Time.deltaTime;
            var activeState = _activeState;

            useGravity = activeState.ApplyGravity;
            snapToGround = activeState.SnapToGround;
            var newVelocity = activeState.UpdateVelocity(velocity, deltaTime);

            // Update the step cycle, mainly used for footsteps
            UpdateStepCycle(deltaTime);

            activeState.UpdateLogic();

            return newVelocity;
        }
        #endregion

        #region Step Cycle
        private void UpdateStepCycle(float deltaTime)
        {
            if (!_motor.IsGrounded)
                return;

            // Advance the step cycle based on the current velocity.
            _distMovedSinceLastCycleEnded += _motor.Velocity.GetHorizontal().magnitude * deltaTime;
            float targetStepLength = Mathf.Max(_activeState.StepCycleLength, 1f);
            _currentStepLength = Mathf.MoveTowards(_currentStepLength, targetStepLength, deltaTime * _stepLerpSpeed);

            // Advance the step cycle based on the character turn.
            _distMovedSinceLastCycleEnded += Mathf.Clamp(_motor.TurnSpeed, 0f, _maxTurnStepVelocity) * deltaTime * _turnStepLength;

            // If the step cycle is complete, reset it, and send a notification.
            if (_distMovedSinceLastCycleEnded > _currentStepLength)
            {
                _distMovedSinceLastCycleEnded -= _currentStepLength;
                StepCycleEnded?.Invoke();
            }

            StepCycle = _distMovedSinceLastCycleEnded / _currentStepLength;
        }
        #endregion
        
        #region State Events
        public void AddStateTransitionListener(MovementStateType stateType, UnityAction<MovementStateType> callback, MovementStateTransitionType transition)
        {
            int stateIndex = (int)stateType;
            switch (transition)
            {
                case MovementStateTransitionType.Enter:
                    _stateEnterEvents[stateIndex] += callback;
                    break;
                case MovementStateTransitionType.Exit:
                    _stateExitEvents[stateIndex] += callback;
                    break;
                case MovementStateTransitionType.Both:
                    _stateEnterEvents[stateIndex] += callback;
                    _stateExitEvents[stateIndex] += callback;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(transition), transition, null);
            }
        }

        public void RemoveStateTransitionListener(MovementStateType stateType, UnityAction<MovementStateType> callback, MovementStateTransitionType transition)
        {
            int stateIndex = (int)stateType;
            switch (transition)
            {
                case MovementStateTransitionType.Enter:
                    _stateEnterEvents[stateIndex] -= callback;
                    break;
                case MovementStateTransitionType.Exit:
                    _stateExitEvents[stateIndex] -= callback;
                    break;
                case MovementStateTransitionType.Both:
                    _stateEnterEvents[stateIndex] -= callback;
                    _stateExitEvents[stateIndex] -= callback;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(transition), transition, null);
            }
        }
        #endregion

        #region State Changing
        public bool TrySetState(ICharacterMovementState newState)
        {
            if (newState == null || !_states.Contains(newState))
                return false;
            
            if (_activeState != newState && newState.Enabled && newState.IsValid())
            {
                EnterState(newState);
                return true;
            }

            return false;
        }

        public bool TrySetState(MovementStateType stateType)
            => TrySetState(_states[(int)stateType]);

        private void EnterState(ICharacterMovementState newState)
        {
            var stateType = newState.StateType;

            // Handles state previous state exit.
            if (_activeState != null)
            {
                _activeState.OnExit();
                _stateExitEvents[0]?.Invoke(ActiveState);
                _stateExitEvents[(int)ActiveState]?.Invoke(ActiveState);
            }

            // Handles next state enter.
            _activeState = newState;
            newState.OnEnter(ActiveState);
            ActiveState = stateType;

            _stateEnterEvents[0]?.Invoke(stateType);
            _stateEnterEvents[(int)stateType]?.Invoke(stateType);
        }
        #endregion

        #region State Blocking
        public void AddStateBlocker(Object blocker, MovementStateType stateType)
        {
            int stateIndex = (int)stateType;

            // Creates a new locker list for the given state type
            if (_stateLockers[stateIndex] == null)
            {
                var list = new List<Object>
                {
                    blocker
                };
                _stateLockers[stateIndex] = list;

                _states[stateIndex].Enabled = false;
            }

            // Gets existing locker list for the given state type if available
            else
            {
                _stateLockers[stateIndex].Add(blocker);
                _states[stateIndex].Enabled = false;
            }
        }

        public void RemoveStateBlocker(Object blocker, MovementStateType stateType)
        {
            int stateIndex = (int)stateType;

            // Gets existing locker list for the given state type if available
            var list = _stateLockers[stateIndex];
            if (list != null && list.Remove(blocker))
            {
                if (list.Count == 0)
                    _states[stateIndex].Enabled = true;
            }
        }
        #endregion

        #region Save & Load
        // ADR-009: STP save disabled — Rust is the single source of truth.
        void ISaveableComponent.LoadMembers(object data) { }
        object ISaveableComponent.SaveMembers() => null;
        #endregion
        
        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Character != null)
            {
                OnBehaviourStart(Character);
            }
        }
#endif
        #endregion
    }
}