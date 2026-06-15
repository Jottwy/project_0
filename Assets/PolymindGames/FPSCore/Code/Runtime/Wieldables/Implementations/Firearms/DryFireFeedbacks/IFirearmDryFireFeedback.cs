namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Interface for providing feedback when the firearm is dry fired.
    /// </summary>
    public interface IFirearmDryFireFeedback : IFirearmComponent
    {
        /// <summary>
        /// Executes the feedback mechanism for a dry fire action,
        /// such as playing an animation or sound.
        /// </summary>
        void TriggerDryFireFeedback();
    }

    public sealed class DefaultFirearmDryFireFeedback : IFirearmDryFireFeedback
    {
        public static readonly DefaultFirearmDryFireFeedback Instance = new();

        public void TriggerDryFireFeedback() { }
        public void Attach() { }
        public void Detach() { }
    }
}