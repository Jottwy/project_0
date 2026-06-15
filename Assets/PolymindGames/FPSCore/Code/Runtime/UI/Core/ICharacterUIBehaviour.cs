namespace PolymindGames
{
    public interface ICharacterUIBehaviour : IMonoBehaviour
    {
        void OnCharacterChanged(ICharacter prevCharacter, ICharacter newCharacter);
    }
}