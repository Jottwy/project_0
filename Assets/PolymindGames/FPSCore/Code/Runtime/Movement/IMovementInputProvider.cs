using UnityEngine;

namespace PolymindGames.MovementSystem
{
    /// <summary>
    /// Interface that provides input values for movement actions such as moving, running, crouching, and jumping.
    /// Implement this interface to provide movement input data from various input systems (e.g., keyboard, gamepad).
    /// </summary>
    public interface IMovementInputProvider
    {
        /// <summary>
        /// Gets the raw movement direction input (e.g., from a joystick or keyboard).
        /// </summary>
        Vector2 RawMovement { get; }

        /// <summary>
        /// Gets the processed movement direction in 3D space (e.g., accounting for transformations).
        /// </summary>
        Vector3 ProcessedMovement { get; }

        /// <summary>
        /// Gets a boolean indicating whether the player is running based on input.
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Gets a boolean indicating whether the player is crouching based on input.
        /// </summary>
        bool IsCrouching { get; }

        /// <summary>
        /// Gets a boolean indicating whether the player is jumping based on input.
        /// </summary>
        bool IsJumping { get; }

        /// <summary>
        /// Marks the crouch input as used to prevent further processing until it's reset.
        /// </summary>
        void MarkCrouchInputUsed();

        /// <summary>
        /// Marks the run input as used to prevent further processing until it's reset.
        /// </summary>
        void MarkRunInputUsed();

        /// <summary>
        /// Marks the jump input as used to prevent further processing until it's reset.
        /// </summary>
        void MarkJumpInputUsed();
    }
}