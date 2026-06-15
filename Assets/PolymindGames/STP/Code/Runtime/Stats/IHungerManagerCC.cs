namespace PolymindGames
{
    /// <summary>
    /// Manages hunger levels for a character.
    /// </summary>
    public interface IHungerManagerCC : ICharacterComponent
    {
        /// <summary> Gets or sets the current hunger level. </summary>
        float Hunger { get; set; }
    
        /// <summary> Gets or sets the maximum hunger level. </summary>
        float MaxHunger { get; set; }
    }
}