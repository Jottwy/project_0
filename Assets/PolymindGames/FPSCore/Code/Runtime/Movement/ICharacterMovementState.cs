using UnityEngine;

namespace PolymindGames.MovementSystem
{
    /// <summary>
    /// Interface for defining character movement states.
    /// </summary>
    public interface ICharacterMovementState
    {
        /// <summary> The type of movement state. </summary>
        MovementStateType StateType { get; }
        
        /// <summary> Length of the step cycle. </summary>
        float StepCycleLength { get; }
        
        /// <summary> Determines if gravity should be applied in this state. </summary>
        bool ApplyGravity { get; }
        
        /// <summary> Determines if the character should snap to the ground in this state. </summary>
        bool SnapToGround { get; }
        
        /// <summary> Indicates whether the state is enabled. </summary>
        bool Enabled { get; set; }

        /// <summary>
        /// Initializes/enables this state.
        /// </summary>
        /// <param name="movement">The movement controller.</param>
        /// <param name="input">The movement input provider.</param>
        /// <param name="motor">The motor controller.</param>
        /// <param name="character">The character associated with this state.</param>
        void InitializeState(IMovementControllerCC movement, IMovementInputProvider input, IMotorCC motor, ICharacter character);

        /// <summary>
        /// Checks if this state can be transitioned to.
        /// </summary>
        /// <returns>True if the state can be transitioned to, otherwise false.</returns>
        bool IsValid();

        /// <summary>
        /// Called when entering this state.
        /// </summary>
        /// <param name="prevStateType">The type of the previous movement state.</param>
        void OnEnter(MovementStateType prevStateType);

        /// <summary>
        /// Updates the logic of this state, including handling transitions to other states.
        /// </summary>
        void UpdateLogic();

        /// <summary>
        /// Passes the current velocity and returns a new one that will be used to move the parent character.
        /// </summary>
        /// <param name="currentVelocity">The current velocity of the character.</param>
        /// <param name="deltaTime">The time since the last update.</param>
        /// <returns>The new velocity to be used for movement.</returns>
        Vector3 UpdateVelocity(Vector3 currentVelocity, float deltaTime);

        /// <summary>
        /// Called when exiting this state.
        /// </summary>
        void OnExit();
    }
}