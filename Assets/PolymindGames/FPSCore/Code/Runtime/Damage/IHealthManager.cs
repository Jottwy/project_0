using System.Runtime.CompilerServices;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Interface for managing health-related functionality.
    /// </summary>
    public interface IHealthManager
    {
        /// <summary> Current health value. </summary>
        float Health { get; }
        
        /// <summary> Maximum health value. </summary>
        float MaxHealth { get; set; }

        /// <summary> Event triggered when damage is received. </summary>
        event DamageReceivedDelegate DamageReceived;
        
        /// <summary> Event triggered when health is restored. </summary>
        event HealthRestoredDelegate HealthRestored;

        /// <summary> Event triggered when the entity dies. </summary>
        event DeathDelegate Death;
        
        /// <summary> Event triggered when the entity respawns. </summary>
        event UnityAction Respawn;

        /// <summary>
        /// Restores health by the specified value.
        /// </summary>
        /// <param name="value">The amount of health to restore.</param>
        /// <returns>The actual amount of health restored.</returns>
        float RestoreHealth(float value);
        
        /// <summary>
        /// Inflicts damage to the entity.
        /// </summary>
        /// <param name="damage">The amount of damage to inflict.</param>
        /// <returns>The actual amount of damage inflicted.</returns>
        float ReceiveDamage(float damage);
        
        /// <summary>
        /// Inflicts damage to the entity with additional arguments.
        /// </summary>
        /// <param name="damage">The amount of damage to inflict.</param>
        /// <param name="args">Additional arguments related to the damage.</param>
        /// <returns>The actual amount of damage inflicted.</returns>
        float ReceiveDamage(float damage, in DamageArgs args);
    }

    /// <summary>
    /// Delegate for handling damage received events.
    /// </summary>
    /// <param name="damage">The amount of damage received.</param>
    /// <param name="args">Additional arguments related to the damage.</param>
    public delegate void DamageReceivedDelegate(float damage, in DamageArgs args);

    /// <summary>
    /// Delegate for handling death events.
    /// </summary>
    /// <param name="args">Additional arguments related to the death event.</param>
    public delegate void DeathDelegate(in DamageArgs args);

    /// <summary>
    /// Delegate for handling health restored events.
    /// </summary>
    /// <param name="value">The amount of health restored.</param>
    public delegate void HealthRestoredDelegate(float value);

    /// <summary>
    /// Static class containing extension methods for health management.
    /// </summary>
    public static class HealthExtensions
    {
        /// <summary> Threshold value for considering an entity as alive. </summary>
        public const float Threshold = 0.001f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsFullHealth(this IHealthManager health) => Mathf.Abs(health.MaxHealth - health.Health) < Threshold;
        
        /// <summary>
        /// Checks if the entity is alive based on its health value.
        /// </summary>
        /// <param name="health">The health manager interface.</param>
        /// <returns>True if the entity is alive, otherwise false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAlive(this IHealthManager health) => health.Health >= Threshold;
        
        /// <summary>
        /// Checks if the entity is dead based on its health value.
        /// </summary>
        /// <param name="health">The health manager interface.</param>
        /// <returns>True if the entity is dead, otherwise false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsDead(this IHealthManager health) => health.Health < Threshold;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ResetHealth(this IHealthManager health) => health.RestoreHealth(health.MaxHealth);
        
        /// <summary>
        /// Determines the <see cref="DamageResult"/> based on the health status and whether the damage is critical.
        /// </summary>
        /// <param name="healthManager">The health manager that tracks the entity's health status.</param>
        /// <param name="isCritical">Whether the damage is considered critical.</param>
        /// <returns>A <see cref="DamageResult"/> based on whether the entity is alive and if the damage is critical.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DamageResult GetDamageResultBasedOnStatus(this IHealthManager healthManager, bool isCritical)
        {
            return healthManager.IsAlive()
                ? isCritical ? DamageResult.Critical : DamageResult.Normal
                : DamageResult.Fatal;
        }
    }
    
    public sealed class NullHealthManager : IHealthManager
    {
        public float Health => 100f;

        public float MaxHealth
        {
            get => 100f;
            set { }
        }

        public event DamageReceivedDelegate DamageReceived { add { } remove { } }
        public event HealthRestoredDelegate HealthRestored { add { } remove { } }
        public event DeathDelegate Death { add { } remove { } }
        public event UnityAction Respawn { add { } remove { } }

        public float RestoreHealth(float value) => value;
        public float ReceiveDamage(float damage) => ReceiveDamage(damage, DamageArgs.Default);
        public float ReceiveDamage(float damage, in DamageArgs args) => damage;
    }
}