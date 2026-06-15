namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Interface for managing the ejection of spent shells
    /// after a shot is fired from the firearm.
    /// </summary>
    public interface IFirearmShellEjector : IFirearmComponent
    {
        /// <summary>
        /// Gets the duration of the shell ejection process.
        /// </summary>
        float EjectionDuration { get; }

        /// <summary>
        /// Indicates whether a shell is currently being ejected.
        /// </summary>
        bool IsEjecting { get; }

        /// <summary>
        /// Initiates the shell ejection process.
        /// </summary>
        void Eject();

        void ResetShells();
    }

    public sealed class DefaultFirearmShellEjector : IFirearmShellEjector
    {
        public static readonly DefaultFirearmShellEjector Instance = new();

        public float EjectionDuration => 0f;
        public bool IsEjecting => false;

        public void Eject() { }
        public void ResetShells() { }

        public void Attach() { }
        public void Detach() { }
    }
}