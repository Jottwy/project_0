using UnityEngine;

namespace PolymindGames.BuildingSystem
{
    /// <summary>
    /// Definition for a build material used in construction.
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Building/Build Material Definition", fileName = "BuildMaterial_")]
    public sealed class BuildMaterialDefinition : DataDefinition<BuildMaterialDefinition>
    {
        [SerializeField, SpritePreview]
        [Tooltip("The icon representing the build material")]
        private Sprite _icon;

        [SerializeField]
        [Tooltip("The audio data for using the build material")]
        private AudioData _useAudio = new(null);

        /// <summary>
        /// Gets the icon representing the build material.
        /// </summary>
        public override Sprite Icon => _icon;

        /// <summary>
        /// Gets the audio data for using the build material.
        /// </summary>
        public AudioData UseAudio => _useAudio;
    }

}