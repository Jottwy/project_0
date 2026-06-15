using UnityEngine.Events;

namespace PolymindGames
{
    /// <summary>
    /// Manages the stamina of a character.
    /// </summary>
    public interface IStaminaManagerCC : ICharacterComponent
    {
        /// <summary> Gets or sets the current stamina value. </summary>
        float Stamina { get; set; }
    
        /// <summary> Gets or sets the maximum stamina value. </summary>
        float MaxStamina { get; set; }

        /// <summary> Event triggered when the stamina value changes. </summary>
        event UnityAction<float> StaminaChanged;
    }
}