namespace PolymindGames.WieldableSystem
{
    public interface ICarryableAction
    {
        bool CanDoAction(ICharacter character);
        bool TryDoAction(ICharacter character);
    }
}