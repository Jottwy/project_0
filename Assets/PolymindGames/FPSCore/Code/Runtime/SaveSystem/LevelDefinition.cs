using UnityEngine.Serialization;
using UnityEngine;

namespace PolymindGames.SaveSystem
{
    /// <summary>
    /// Represents the metadata of a scene, including description, thumbnail, and associated scene.
    /// </summary>
    [CreateAssetMenu(menuName = "Polymind Games/Level Definition", fileName = "New Level Definition")]
    public sealed class LevelDefinition : ScriptableObject
    {
        [FormerlySerializedAs("_levelScene")]
        [SerializeField]
        [Tooltip("The scene associated with this level.")]
        private SerializedScene _scene;

        [FormerlySerializedAs("_levelName")] 
        [SerializeField]
        private string _name;
        
        [FormerlySerializedAs("_levelDescription")]
        [SerializeField, Multiline]
        [Tooltip("A brief description of the level shown when the level is loaded.")]
        private string _description;

        [FormerlySerializedAs("_levelThumbnail")]
        [SerializeField, SpritePreview]
        [Tooltip("The thumbnail image representing this level in menus.")]
        private Sprite _thumbnail;

        [FormerlySerializedAs("_levelLoadingImage")]
        [SerializeField, SpritePreview]
        [Tooltip("The loading screen image shown while the level is being loaded.")]
        private Sprite _loadingImage;

        /// <summary>
        /// Gets the scene associated with this level.
        /// </summary>
        public string LevelScene => _scene.SceneName;
        
        /// <summary>
        /// Gets the scene associated with this level.
        /// </summary>
        public int LevelSceneIndex => _scene.BuildIndex;

        /// <summary>
        /// Gets the loading screen image for the level.
        /// </summary>
        public Sprite LoadingImage => _loadingImage;

        public string LevelName => _name;

        /// <summary>
        /// Gets the description of the level.
        /// </summary>
        public string LevelDescription => _description;

        /// <summary>
        /// Gets the thumbnail image representing this level.
        /// </summary>
        public Sprite LevelIcon => _thumbnail;
    }
}