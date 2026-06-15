using UnityEngine.Events;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Represents a firearm with various components that manage aiming, firing, and feedback mechanisms.
    /// </summary>
    public interface IFirearm : IMonoBehaviour
    {
        /// <summary>
        /// Gets or sets the aim handler for the firearm.
        /// </summary>
        IFirearmAimHandler AimHandler { get; set; }

        /// <summary>
        /// Gets or sets the trigger mechanism for the firearm.
        /// </summary>
        IFirearmTrigger Trigger { get; set; }

        /// <summary>
        /// Gets or sets the firing system that manages projectile launching.
        /// </summary>
        IFirearmFireSystem FireSystem { get; set; }

        /// <summary>
        /// Gets or sets the ammo provider that supplies ammunition to the firearm.
        /// </summary>
        IFirearmAmmoProvider AmmoProvider { get; set; }

        /// <summary>
        /// Gets or sets the reloadable magazine that manages ammunition loading and unloading.
        /// </summary>
        IFirearmReloadableMagazine ReloadableMagazine { get; set; }

        /// <summary>
        /// Gets or sets the recoil manager that handles recoil mechanics.
        /// </summary>
        IFirearmRecoilManager RecoilManager { get; set; }

        /// <summary>
        /// Gets or sets the impact effect manager for visual and audio effects on impact.
        /// </summary>
        IFirearmImpactEffect ImpactEffect { get; set; }

        /// <summary>
        /// Gets or sets the shell ejector that manages shell ejection.
        /// </summary>
        IFirearmShellEjector ShellEjector { get; set; }

        /// <summary>
        /// Gets or sets the dry fire feedback mechanism for when the firearm is dry fired.
        /// </summary>
        IFirearmDryFireFeedback DryFireFeedback { get; set; }

        /// <summary>
        /// Gets or sets the barrel effects manager for visual and audio effects at the barrel.
        /// </summary>
        IFirearmBarrelEffect BarrelEffect { get; set; }

        /// <summary>
        /// Adds a listener for changes in a specified firearm component type.
        /// </summary>
        /// <param name="type">The type of firearm component to listen for changes.</param>
        /// <param name="callback">The callback to invoke when the component changes.</param>
        void AddChangedListener(FirearmComponentType type, UnityAction callback);

        /// <summary>
        /// Removes a listener for changes in a specified firearm component type.
        /// </summary>
        /// <param name="type">The type of firearm component to stop listening for changes.</param>
        /// <param name="callback">The callback to remove.</param>
        void RemoveChangedListener(FirearmComponentType type, UnityAction callback);
    }

    /// <summary>
    /// Enumeration of different component types that can be part of a firearm.
    /// </summary>
    public enum FirearmComponentType
    {
        /// <summary>
        /// The component responsible for aiming mechanics.
        /// </summary>
        AimHandler,

        /// <summary>
        /// The component that handles trigger input and firing logic.
        /// </summary>
        Trigger,

        /// <summary>
        /// The component that manages the actual firing of projectiles.
        /// </summary>
        FireSystem,

        /// <summary>
        /// The component that provides ammunition to the firearm.
        /// </summary>
        AmmoProvider,

        /// <summary>
        /// The component that manages reloading and magazine operations.
        /// </summary>
        ReloadableMagazine,

        /// <summary>
        /// The component that handles recoil effects during firing.
        /// </summary>
        RecoilManager,

        /// <summary>
        /// The component that manages effects upon projectile impact.
        /// </summary>
        ImpactEffect,

        /// <summary>
        /// The component that provides feedback for dry firing.
        /// </summary>
        DryFireFeedback,

        /// <summary>
        /// The component that manages shell ejection.
        /// </summary>
        ShellEjector,

        /// <summary>
        /// The component that handles effects related to the barrel.
        /// </summary>
        BarrelEffect
    }
}