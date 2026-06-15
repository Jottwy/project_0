using UnityEngine;
using UnityEngine.Serialization;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(SelectableButton))]
    public abstract class DataSlotUI<T> : MonoBehaviour where T : Object
    {
        [FormerlySerializedAs("_definition")]
        [SerializeField]
        [Tooltip("The data definition used to populate this UI.")]
        private T _data;

        [SerializeField, HideInInspector]
        private SelectableButton _selectable;

        /// <summary>
        /// Gets the associated data object.
        /// </summary>
        public T Data => _data;

        /// <summary>
        /// Gets the selectable button component.
        /// </summary>
        public SelectableButton Selectable
        {
            get
            {
                if (_selectable == null)
                    _selectable = GetComponent<SelectableButton>();

                return _selectable;
            }
        }
        
        /// <summary>
        /// Refreshes the UI.
        /// </summary>
        public void Refresh() => UpdateUI(_data);

        /// <summary>
        /// Updates the definition and refreshes the UI accordingly.
        /// </summary>
        public void SetData(T data)
        {
            if (data == Data)
                return;

            _data = data;
            UpdateUI(data);
        }

        /// <summary>
        /// Set the definition to null and refreshes the UI accordingly.
        /// </summary>
        public void ClearData() => SetData(null);

        /// <summary>
        /// Updates the UI based on the current data object.
        /// This method must be implemented by derived classes.
        /// </summary>
        /// <param name="definition">The data object to use for updating the UI.</param>
        protected abstract void UpdateUI(T definition);

        /// <summary>
        /// Initializes the UI with the assigned data object on start.
        /// </summary>
        private void Start()
        {
            if (_data != null)
                UpdateUI(_data);
        }

        #region Editor
#if UNITY_EDITOR
        /// <summary>
        /// Ensures that required components are assigned during validation in the Unity Editor.
        /// </summary>
        protected virtual void OnValidate()
        {
            if (_selectable == null)
                _selectable = GetComponent<SelectableButton>();
        }
#endif
        #endregion
    }
}