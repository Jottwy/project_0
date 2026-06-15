using PolymindGames.InventorySystem;
using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Represents a character in the game with various systems such as health, inventory, and audio.
    /// Allows access to different components like animator, health manager, and inventory.
    /// </summary>
    public partial interface ICharacter : IDamageSource
    {
        /// <summary>
        /// Gets or sets the name of the character.
        /// </summary>
        string Name { get; set; }

        /// <summary>
        /// Gets the animator controller responsible for animating the character.
        /// </summary>
        IAnimatorController Animator { get; }

        /// <summary>
        /// Gets the audio player responsible for playing character-related sound effects.
        /// </summary>
        ICharacterAudioPlayer Audio { get; }

        /// <summary>
        /// Gets the health manager that handles the character’s health-related logic.
        /// </summary>
        IHealthManager HealthManager { get; }

        /// <summary>
        /// Gets the inventory system that manages the character's items and equipment.
        /// </summary>
        IInventory Inventory { get; }

        /// <summary>
        /// Event triggered when the character is destroyed.
        /// </summary>
        event UnityAction<ICharacter> Destroyed;

        /// <summary>
        /// Retrieves the transform of a specific body point (e.g., head, hands, etc.).
        /// </summary>
        /// <param name="point">The body point whose transform is to be retrieved.</param>
        /// <returns>The transform of the specified body point.</returns>
        Transform GetTransformOfBodyPoint(BodyPoint point);

        /// <summary>
        /// Attempts to retrieve a character component of the specified type.
        /// </summary>
        /// <typeparam name="T">The type of the character component to retrieve.</typeparam>
        /// <param name="component">The component of type T, if found.</param>
        /// <returns>True if the component of type T exists; otherwise, false.</returns>
        bool TryGetCC<T>(out T component) where T : class, ICharacterComponent;

        /// <summary>
        /// Retrieves a character component of the specified type. Logs an error if no such component exists.
        /// </summary>
        /// <typeparam name="T">The type of the character component to retrieve.</typeparam>
        /// <returns>The component of type T, if found; otherwise, throws an exception.</returns>
        T GetCC<T>() where T : class, ICharacterComponent;

        /// <summary>
        /// Retrieves a character component of the specified type.
        /// </summary>
        /// <param name="type">The type of the character component to retrieve.</param>
        /// <returns>The component of the specified type, if found; otherwise, null.</returns>
        ICharacterComponent GetCC(Type type);
    }
}