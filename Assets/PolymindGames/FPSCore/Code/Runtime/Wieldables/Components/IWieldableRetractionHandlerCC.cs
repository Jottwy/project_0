
namespace PolymindGames
{
    /// <summary>
    /// Handles the retraction of wieldable objects.
    /// </summary>
    public interface IWieldableRetractionHandlerCC : ICharacterComponent
    {
        /// <summary> Gets the distance to the closest object. </summary>
        float ClosestObjectDistance { get; }
    }
}