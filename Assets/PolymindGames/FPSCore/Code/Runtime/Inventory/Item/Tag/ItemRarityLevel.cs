using System.Linq;
using UnityEngine;

namespace PolymindGames.InventorySystem
{
    /// <summary>
    /// Represents an item rarity level, defining its name, rarity value, and associated color.
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Items/Rarity Level", fileName = "RarityLevel_")]
    public sealed class ItemRarityLevel : ScriptableObject
    {
        [SerializeField]
        [Tooltip("The name of the rarity level.")]
        private string _rarityName = DefaultRarityName;

        [SerializeField, Range(0f, 0.99f)]
        [Tooltip("The rarity value, where 0 represents common and 1 represents very rare or legendary.")]
        private float _rarityValue;

        [SerializeField]
        [Tooltip("The color associated with this rarity level.")]
        private Color _color = Color.grey;

        private static ItemRarityLevel _defaultRarity;

        private const string DefaultRarityName = "Common";
        private const string RarityLevelsPath = "RarityLevels";

        /// <summary>
        /// The name of the rarity level.
        /// </summary>
        public string Name => _rarityName;

        /// <summary>
        /// The rarity value, where 0 represents common and 1 represents very rare or legendary.
        /// </summary>
        public float RarityValue => _rarityValue;

        /// <summary>
        /// The color associated with this rarity level.
        /// </summary>
        public Color Color => _color;

        /// <summary>
        /// Retrieves the default rarity level, typically representing the most common rarity.
        /// </summary>
        public static ItemRarityLevel DefaultRarity
        {
            get
            {
                // If already loaded, return it
                if (_defaultRarity != null)
                    return _defaultRarity;

                // Load all rarity levels
                var levels = Resources.LoadAll<ItemRarityLevel>(RarityLevelsPath);

                // Find the rarity level with the lowest rarity value
                _defaultRarity = levels.OrderBy(level => level.RarityValue).FirstOrDefault();

                // If no default rarity is found, create a new instance
                return _defaultRarity ?? CreateInstance<ItemRarityLevel>();
            }
        }
    }
}