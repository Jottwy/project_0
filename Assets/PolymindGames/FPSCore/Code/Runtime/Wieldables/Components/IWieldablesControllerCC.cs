using PolymindGames.WieldableSystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    public interface IWieldablesControllerCC : ICharacterComponent
    {
        /// <summary>
        /// The active or equipped wieldable.
        /// </summary>
        IWieldable ActiveWieldable { get; }
        
        /// <summary>
        /// The active state of this controller.
        /// </summary>
        WieldableControllerState State { get; }
        
        /// <summary>
        /// The parent transform of every registered wieldable.
        /// </summary>
        Transform WieldablesRoot { get; }

        /// <summary>
        /// Raised when the holstering process of the previous wieldable has started.
        /// </summary>
        event WieldableEquipDelegate HolsteringStarted;
        
        /// <summary>
        /// Raised when the holstering process of the previous wieldable has stopped.
        /// </summary>
        event WieldableEquipDelegate HolsteringStopped;
        
        /// <summary>
        /// Raised when the equipping process of the new wieldable has started.
        /// </summary>
        event WieldableEquipDelegate EquippingStarted;
        
        /// <summary>
        /// Raised when the holstering process of the new wieldable has stopped.
        /// </summary>
        event WieldableEquipDelegate EquippingStopped;
        
        /// <summary>
        /// Pushes a wieldable to the equip stack with the intention to equip it after the active one holsters.
        /// </summary>
        /// <param name="wieldable">Wieldable to equip, it has to be registered in the available pool.</param>
        /// <param name="holsterSpeed">How fast should the active wieldable be holstered</param>
        /// <param name="equipCallback">Method to invoke when the pushed wieldable starts the equipping process</param>
        /// <returns> True if successfully pushed.</returns>
        bool TryEquipWieldable(IWieldable wieldable, float holsterSpeed = 1f, UnityAction equipCallback = null);

        /// <summary>
        /// Pops a wieldable from the equip stack. If the popped wieldable is the active one the controller will holster the active one and equip the latest available wieldable in the stack.
        /// </summary>
        /// <param name="wieldable">Wieldable to pop, it has to be registered in the available pool.</param>
        /// <param name="holsterSpeed"></param>
        /// <returns> True if successfully popped.</returns>
        bool TryHolsterWieldable(IWieldable wieldable, float holsterSpeed = 1f);
        
        /// <summary>
        /// Clears the equip stack and holsters the active wieldable.
        /// </summary>
        void HolsterAll();

        /// <summary>
        /// Registers a wieldable to the available pool.
        /// </summary>
        /// <param name="wieldable">Wieldable to register.</param>
        /// <param name="disable">Should the registered wieldable be disabled by default?</param>
        /// <returns>The registered wieldable (if the passed wieldable was a prefab, this will return an instance)</returns>
        IWieldable RegisterWieldable(IWieldable wieldable, bool disable = true);
        
        /// <summary>
        /// Attempts to unregister a wieldable from the available pool.
        /// </summary>
        /// <param name="wieldable">Wieldable to unregister.</param>
        /// <param name="destroy">Should the wieldable be destroyed after unregistering it?</param>
        bool UnregisterWieldable(IWieldable wieldable, bool destroy = false);
    }
    
    public enum WieldableControllerState
    {
        None = 0,
        Equipping = 1,
        Holstering = 2
    }

    public delegate void WieldableEquipDelegate(IWieldable wieldable);
}