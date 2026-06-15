using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Represents an attachment for a firearm, such as scopes, grips, or silencers.
    /// This class manages the attachment's name, icon, and attachment/detachment events.
    /// </summary>
    public sealed class FirearmAttachment : MonoBehaviour
    {
        [SerializeField, BeginHorizontal, EndHorizontal]
        [EditorButton(nameof(ResetName), "Reset Name")]
        private string _name;

        [SerializeField]
        private Sprite _icon;

        [SerializeField, Range(0.5f, 5f)]
        private float _iconSize = 1f;
        
        [SerializeField, SpaceArea]
        private UnityEvent _attachEvent;

        [SerializeField]
        private UnityEvent _detachEvent;
        
        private FirearmComponentBehaviour[] _components;
        private bool _isEnabled;

        /// <summary>
        /// Gets the name of the attachment.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Gets the icon associated with the attachment.
        /// </summary>
        public Sprite Icon => _icon;

        /// <summary>
        /// Gets the size of the icon associated with the attachment.
        /// </summary>
        public float IconSize => _iconSize;

        public bool CanAttach()
        {
            _components ??= GetComponentsInChildren<FirearmComponentBehaviour>(true);
            foreach (var component in _components)
            {
                if (!component.CanAttach())
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Attaches the firearm attachment, enabling its functionality and invoking the attach event.
        /// </summary>
        public void Attach()
        {
            if (_isEnabled)
                return;

            gameObject.SetActive(true);
            _components ??= GetComponentsInChildren<FirearmComponentBehaviour>(true);

            foreach (var component in _components)
                component.Attach();

            _attachEvent.Invoke();
            _isEnabled = true;
        }

        /// <summary>
        /// Detaches the firearm attachment, disabling its functionality and invoking the detach event.
        /// </summary>
        public void Detach()
        {
            if (!_isEnabled)
                return;

            gameObject.SetActive(false);
            _components ??= GetComponentsInChildren<FirearmComponentBehaviour>(true);
            foreach (var component in _components)
                component.Detach();

            _detachEvent.Invoke();
            _isEnabled = false;
        }

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            ResetName();
        }
#endif
        
        private void ResetName() => _name = gameObject.name
            .Replace("Mode", "")
            .Replace("Attachment", "")
            .Replace("_", "")
            .AddSpaceBeforeCapitalLetters();
        #endregion
    }
}