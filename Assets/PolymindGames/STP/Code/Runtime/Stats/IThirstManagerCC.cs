namespace PolymindGames
{
    /// <summary>
    /// Manages thirst levels for a character.
    /// </summary>
    public interface IThirstManagerCC : ICharacterComponent
    {
        /// <summary> Gets or sets the current thirst level. </summary>
        float Thirst { get; set; }
    
        /// <summary> Gets or sets the maximum thirst level. </summary>
        float MaxThirst { get; set; }
    }
}