using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Abstract base class for handling shell ejection behavior in firearms.
    /// </summary>
    public abstract class FirearmShellEjectorBehaviour : FirearmComponentBehaviour, IFirearmShellEjector
    {
        [SerializeField, Range(0f, 10f)]
        private float _ejectDuration;

        [SerializeField, SpaceArea(0f, 5f)]
        private bool _ejectAnimation;

        private float _ejectionTimer;

        protected const string AddMenuPath = "Polymind Games/Wieldables/Firearms/Shell Ejectors/";

        /// <inheritdoc/>
        public float EjectionDuration => _ejectDuration;

        /// <inheritdoc/>
        public bool IsEjecting => _ejectionTimer > Time.time;

        /// <summary>
        /// Ejects the shell, triggering any associated animations.
        /// </summary>
        public virtual void Eject()
        {
            if (_ejectAnimation)
                Wieldable.Animator.SetTrigger(AnimationConstants.Eject);

            _ejectionTimer = Time.time + _ejectDuration;
        }

        public abstract void ResetShells();

        protected virtual void OnEnable()
        {
            if (Firearm != null)
                Firearm.ShellEjector = this;
        }
    }
}