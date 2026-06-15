namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Interface for managing recoil mechanics of a firearm,
    /// including recoil recovery and accuracy adjustments.
    /// </summary>
    public interface IFirearmRecoilManager : IFirearmComponent
    {
        /// <summary>
        /// Gets the rate at which recoil heat recovers.
        /// </summary>
        float RecoilRecoveryRate { get; }

        /// <summary>
        /// Gets the delay before recoil heat begins to recover.
        /// </summary>
        float RecoilRecoveryDelay { get; }

        /// <summary>
        /// Gets the kick to hipfire accuracy caused by recoil.
        /// </summary>
        float HipfireAccuracyKick { get; }

        /// <summary>
        /// Gets the rate at which hipfire accuracy recovers after recoil.
        /// </summary>
        float HipfireAccuracyRecoveryRate { get; }

        /// <summary>
        /// Gets the kick to aim accuracy caused by recoil.
        /// </summary>
        float AimAccuracyKick { get; }

        /// <summary>
        /// Gets the rate at which aim accuracy recovers after recoil.
        /// </summary>
        float AimAccuracyRecoveryRate { get; }

        /// <summary>
        /// Applies recoil effects based on whether the firearm is aimed
        /// and the current heat value of the firearm.
        /// </summary>
        /// <param name="accuracy">The current accuracy value affecting recoil.</param>
        /// <param name="recoilProgress">The current heat value affecting recoil.</param>
        /// <param name="isAiming"></param>
        void ApplyRecoil(float accuracy, float recoilProgress, bool isAiming);

        /// <summary>
        /// Sets the recoil multiplier for adjustments in recoil effects.
        /// </summary>
        /// <param name="multiplier">The multiplier to apply to recoil effects.</param>
        void SetRecoilMultiplier(float multiplier);
    }

    public sealed class DefaultFirearmRecoilManager : IFirearmRecoilManager
    {
        public static readonly DefaultFirearmRecoilManager Instance = new();

        public float RecoilRecoveryDelay => 0.1f;
        public float RecoilRecoveryRate => 0.3f;
        public float HipfireAccuracyKick => 0f;
        public float HipfireAccuracyRecoveryRate => 0.3f;
        public float AimAccuracyKick => 0f;
        public float AimAccuracyRecoveryRate => 0.3f;

        public void ApplyRecoil(float accuracy, float recoilProgress, bool isAiming) { }
        public void SetRecoilMultiplier(float multiplier) { }
        public void Attach() { }
        public void Detach() { }
    }
}