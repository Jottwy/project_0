using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Base class for components that can be attached to a firearm.
    /// This class manages the attachment state and enables or disables the component accordingly.
    /// </summary>
    public abstract class FirearmComponentBehaviour : MonoBehaviour
    {
        [SerializeField, HideInInspector]
        [Tooltip("Indicates whether this part is currently attached to a firearm.")]
        private bool _isAttached;

        private AttachmentMode _attachmentMode = AttachmentMode.None;

        /// <summary>
        /// Gets a value indicating whether this component is currently attached to a firearm.
        /// </summary>
        public bool IsAttached => _isAttached;

        /// <summary>
        /// Gets the wieldable object associated with this component.
        /// </summary>
        protected IWieldable Wieldable { get; private set; }

        /// <summary>
        /// Gets the firearm associated with this component, if any.
        /// </summary>
        protected IFirearm Firearm { get; private set; }

        /// <summary>
        /// Attaches this component to the firearm, enabling it if applicable.
        /// </summary>
        public void Attach()
        {
            if (_isAttached)
                return;

            if (_attachmentMode == AttachmentMode.None)
                Initialize();

            switch (_attachmentMode)
            {
                case AttachmentMode.Component:
                    _isAttached = true;
                    enabled = true;
                    break;
                case AttachmentMode.GameObject:
                    _isAttached = true;
                    gameObject.SetActive(true);
                    enabled = true;
                    break;
                case AttachmentMode.None:
                    return;
                default: throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Detaches this component from the firearm, disabling it if applicable.
        /// </summary>
        public void Detach()
        {
            if (!_isAttached)
                return;

            if (_attachmentMode == AttachmentMode.None)
                Initialize();

            switch (_attachmentMode)
            {
                case AttachmentMode.Component:
                    _isAttached = false;
                    enabled = false;
                    break;
                case AttachmentMode.GameObject:
                    _isAttached = false;
                    gameObject.SetActive(false);
                    enabled = true;
                    break;
                case AttachmentMode.None:
                    return;
                default: throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Determines whether the firearm attachment is valid for use.  
        /// Can be overridden to implement specific conditions,  
        /// such as checking inventory requirements or compatibility.  
        /// </summary>
        /// <returns>True if the attachment is valid; otherwise, false.</returns>
        public virtual bool CanAttach()
        {
            if (_attachmentMode == AttachmentMode.None)
                Initialize();

            return true;
        }

        /// <summary>
        /// Initializes the component and determines its attachment mode.
        /// </summary>
        protected virtual void Awake()
        {
            if (_attachmentMode == AttachmentMode.None)
                Initialize();

            if (!_isAttached)
                Detach();
        }

        /// <summary>
        /// Initializes the wieldable and determines the enable mode for this component.
        /// </summary>
        private void Initialize()
        {
            Wieldable = GetComponentInParent<IWieldable>();

            if (Wieldable == null)
                _attachmentMode = AttachmentMode.None;
            else
            {
                _attachmentMode = Wieldable.gameObject == gameObject
                    ? AttachmentMode.Component
                    : AttachmentMode.GameObject;

                if (Wieldable is IFirearm firearm)
                    Firearm = firearm;
            }
        }

        #region Internal Types
        private enum AttachmentMode
        {
            None = 0,
            Component = 1,
            GameObject = 2
        }
        #endregion
    }
}