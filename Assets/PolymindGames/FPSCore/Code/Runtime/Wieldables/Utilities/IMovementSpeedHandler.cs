using PolymindGames.MovementSystem;

namespace PolymindGames.WieldableSystem
{
    public interface IMovementSpeedHandler
    {
        MovementModifierGroup SpeedModifier { get; }
    }
}