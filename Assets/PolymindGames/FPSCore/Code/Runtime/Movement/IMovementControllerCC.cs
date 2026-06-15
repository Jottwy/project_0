using PolymindGames.MovementSystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Handles and controls motion states used in moving a character motor.
    /// </summary>
    public interface IMovementControllerCC : ICharacterComponent
    {
        /// <summary>
        /// Gets the currently active movement state.
        /// </summary>
        MovementStateType ActiveState { get; }

        /// <summary>
        /// Gets the speed modifier group.
        /// </summary>
        MovementModifierGroup SpeedModifier { get; }

        /// <summary>
        /// Gets the acceleration modifier group.
        /// </summary>
        MovementModifierGroup AccelerationModifier { get; }

        /// <summary>
        /// Gets the deceleration modifier group.
        /// </summary>
        MovementModifierGroup DecelerationModifier { get; }

        /// <summary>
        /// Gets the step cycle value.
        /// </summary>
        float StepCycle { get; }

        /// <summary>
        /// Event triggered when the step cycle ends.
        /// </summary>
        event UnityAction StepCycleEnded;

        /// <summary>
        /// Gets a state of the given type.
        /// </summary>
        /// <typeparam name="T">The type of state to get.</typeparam>
        /// <returns>The state of the given type, if found; otherwise, null.</returns>
        T GetStateOfType<T>() where T : ICharacterMovementState;

        /// <summary>
        /// Tries to transition to a state of the given type.
        /// </summary>
        bool TrySetState(MovementStateType stateType);

        /// <summary>
        /// Tries to transition to the given state.
        /// </summary>
        bool TrySetState(ICharacterMovementState newState);

        /// <summary>
        /// Adds a state blocker for the given state type.
        /// </summary>
        void AddStateBlocker(Object blocker, MovementStateType stateType);

        /// <summary>
        /// Removes a state blocker for the given state type.
        /// </summary>
        void RemoveStateBlocker(Object blocker, MovementStateType stateType);

        /// <summary>
        /// Adds a listener for state transitions of the given type.
        /// </summary>
        void AddStateTransitionListener(MovementStateType stateType, UnityAction<MovementStateType> callback, MovementStateTransitionType transition = MovementStateTransitionType.Enter);

        /// <summary>
        /// Removes a listener for state transitions of the given type.
        /// </summary>
        void RemoveStateTransitionListener(MovementStateType stateType, UnityAction<MovementStateType> callback, MovementStateTransitionType transition = MovementStateTransitionType.Enter);
    }
}