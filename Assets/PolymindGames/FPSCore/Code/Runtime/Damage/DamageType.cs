namespace PolymindGames
{
    /// <summary>
    /// Represents various types of damage that can be inflicted.
    /// </summary>
    public enum DamageType
    {
        /// <summary>Default or unknown damage type.</summary>
        Undefined = 0,

        /// <summary>Cutting or slicing damage.</summary>
        Slash = 1,

        /// <summary>Impact-based damage.</summary>
        Blunt = 2,

        /// <summary>Stabbing or puncturing damage.</summary>
        Pierce = 3,

        /// <summary>Bullet or projectile damage.</summary>
        Ballistic = 4,

        /// <summary>Explosion damage.</summary>
        Explosive = 5,

        /// <summary>Heat or flame-based damage.</summary>
        Fire = 6,

        /// <summary>Gravity or impact from a fall.</summary>
        Fall = 7,

        /// <summary>Electrical damage.</summary>
        Shock = 8,

        /// <summary>Toxin or chemical damage.</summary>
        Poison = 9,

        /// <summary>Cold or ice-based damage.</summary>
        Frost = 10,

        /// <summary>Corrosive or acidic damage.</summary>
        Acid = 11,

        /// <summary>Nuclear or radiation exposure damage.</summary>
        Radiation = 12,

        /// <summary>Mystical or magical damage.</summary>
        Magic = 14
    }
}