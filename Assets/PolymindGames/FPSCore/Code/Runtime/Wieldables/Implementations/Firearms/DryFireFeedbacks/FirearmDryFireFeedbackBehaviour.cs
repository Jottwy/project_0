namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Abstract base class for providing feedback when a firearm is dry-fired (triggered without ammunition).
    /// </summary>
    public abstract class FirearmDryFireFeedbackBehaviour : FirearmComponentBehaviour, IFirearmDryFireFeedback
    {
        protected const string AddMenuPath = "Polymind Games/Wieldables/Firearms/Dry Fire Feedbacks/";

        /// <inheritdoc/>
        public abstract void TriggerDryFireFeedback();

        protected virtual void OnEnable()
        {
            if (Firearm != null)
                Firearm.DryFireFeedback = this;
        }
    }
}