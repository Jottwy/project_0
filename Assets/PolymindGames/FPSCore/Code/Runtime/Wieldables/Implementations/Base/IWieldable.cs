using System.Collections;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Represents the various states a wieldable item can be in.
    /// </summary>
    public enum WieldableStateType
    {
        [Tooltip("The item is not visible and not in use.")]
        Hidden = 0,

        [Tooltip("The item is in the process of being equipped.")]
        Equipping = 1,

        [Tooltip("The item is currently equipped and in use.")]
        Equipped = 2,

        [Tooltip("The item is in the process of being holstered.")]
        Holstering = 3
    }
    
    public interface IWieldable : IMonoBehaviour
    {
        ICharacter Character { get; }
        IWieldableMotion Motion { get; }
        IAnimatorController Animator { get; }
        ICharacterAudioPlayer Audio { get; }
        WieldableStateType State { get; }
        bool IsGeometryVisible { get; set; }

        /// <summary>
        /// Sets the character/wielder of this wieldable.
        /// </summary>
        /// <param name="character">Character to set</param>
        void SetCharacter(ICharacter character);

        /// <summary>
        /// Initiates the equipping process for this wieldable.
        /// </summary>
        IEnumerator Equip();

        /// <summary>
        /// Initiates the holstering process for this wieldable at a specified speed.
        /// </summary>
        /// <param name="holsterSpeed">Speed of holstering</param>
        IEnumerator Holster(float holsterSpeed);
        
        const float MinHolsterSpeed = 0.5f;
        const float MaxHolsterSpeed = 5f;
    }

    public sealed class NullWieldable : IWieldable
    {
        public ICharacter Character => null;
        public IWieldableMotion Motion => null;
        public IAnimatorController Animator => null;
        public ICharacterAudioPlayer Audio => null;
        public WieldableStateType State => WieldableStateType.Hidden;
        public GameObject gameObject => null;
        public Transform transform => null;
        public bool enabled { get => true; set { } }
        public bool IsGeometryVisible { get => false; set { } }
        
        public void SetCharacter(ICharacter character) { }
        public IEnumerator Equip() { yield break; }
        public IEnumerator Holster(float holsterSpeed) { yield break; }
        public Coroutine StartCoroutine(IEnumerator routine)
            => CoroutineUtility.StartGlobalCoroutine(routine);
    }
}