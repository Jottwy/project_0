namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Represents a component of a firearm that can be attached or detached.
    /// </summary>
    public interface IFirearmComponent
    {
        /// <summary>
        /// Attaches the component to the firearm, enabling its functionality.
        /// </summary>
        void Attach();

        /// <summary>
        /// Detaches the component from the firearm, disabling its functionality.
        /// </summary>
        void Detach();
    }
}