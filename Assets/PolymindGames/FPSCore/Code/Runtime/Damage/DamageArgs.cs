using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Represents the arguments for a damage event, including damage type, source, and impact details.
    /// </summary>
    public readonly struct DamageArgs
    {
        /// <summary>Source of the damage, typically the entity or object responsible.</summary>
        public readonly IDamageSource Source;

        /// <summary>World position where the damage occurred.</summary>
        public readonly Vector3 HitPoint;

        /// <summary>Force applied at the hit point as a result of the damage.</summary>
        public readonly Vector3 HitForce;

        /// <summary>Type of damage inflicted.</summary>
        public readonly DamageType DamageType;

        /// <summary>Default damage arguments with uninitialized values.</summary>
        public static readonly DamageArgs Default = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="DamageArgs"/> struct with optional parameters.
        /// </summary>
        /// <param name="damageType">The type of damage inflicted.</param>
        /// <param name="source">The source of the damage.</param>
        /// <param name="hitPoint">The world position where the damage occurred.</param>
        /// <param name="hitForce">The force applied at the hit point.</param>
        public DamageArgs(DamageType damageType = DamageType.Undefined, IDamageSource source = null, Vector3 hitPoint = default(Vector3), Vector3 hitForce = default(Vector3))
        {
            DamageType = damageType;
            HitPoint = hitPoint;
            HitForce = hitForce;
            Source = source;
        }
    }
}