namespace PolymindGames
{
    public interface ICharacterUI : IMonoBehaviour
    {
        ICharacter Character { get; }

        void AddBehaviour(ICharacterUIBehaviour behaviour);
        void RemoveBehaviour(ICharacterUIBehaviour behaviour);
    }
}