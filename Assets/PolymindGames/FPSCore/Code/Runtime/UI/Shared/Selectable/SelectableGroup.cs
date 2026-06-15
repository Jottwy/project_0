using System.Collections.Generic;
using UnityEngine.Events;
using System.Linq;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Polymind Games/User Interface/Selectables/Selectable Group")]
    public class SelectableGroup : SelectableGroupBase
    {
        private enum SelectableRegisterMode
        {
            None = 0,
            Disable = 2
        }

        [SerializeField]
        private SelectableRegisterMode _selectableRegisterMode = SelectableRegisterMode.None;

        [SerializeField]
        private bool _selectDefaultOnStart;
        
        [SerializeField, ChildObjectOnly]
        private SelectableButton _defaultButtonSelectable;

        private readonly List<SelectableButton> _selectables = new();
        private SelectableButton _highlighted;
        private SelectableButton _selected;

        public override IReadOnlyList<SelectableButton> RegisteredSelectables => _selectables;
        public override SelectableButton Selected => _selected;
        public override SelectableButton Highlighted => _highlighted;

        public override event UnityAction<SelectableButton> SelectedChanged;
        public override event UnityAction<SelectableButton> HighlightedChanged;

        internal override void RegisterSelectable(SelectableButton buttonSelectable)
        {
            if (buttonSelectable == null)
                return;

            _selectables.Add(buttonSelectable);
            if (_selectableRegisterMode == SelectableRegisterMode.Disable)
                DisableSelectable(buttonSelectable);
        }

        internal override void UnregisterSelectable(SelectableButton buttonSelectable)
        {
            if (buttonSelectable == null)
                return;

            _selectables.Remove(buttonSelectable);
        }

        public override void SelectSelectable(SelectableButton buttonSelectable)
        {
            if (buttonSelectable == _selected)
                return;

            var prevSelectable = _selected;
            _selected = buttonSelectable;

            if (prevSelectable != null)
                prevSelectable.Deselect();

            if (buttonSelectable != null)
                buttonSelectable.Select();

            OnSelectedChanged(buttonSelectable);
            SelectedChanged?.Invoke(buttonSelectable);
        }

        public override void HighlightSelectable(SelectableButton buttonSelectable)
        {
            _highlighted = buttonSelectable;
            HighlightedChanged?.Invoke(buttonSelectable);
        }

        public override SelectableButton GetDefaultSelectable()
        {
            return _defaultButtonSelectable != null ? _defaultButtonSelectable : _selectables[0];
        }

        protected virtual void OnSelectedChanged(SelectableButton buttonSelectable) { }

        private void Start()
        {
            if (_selectDefaultOnStart)
                SelectSelectable(GetDefaultSelectable());
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_defaultButtonSelectable == null)
                _defaultButtonSelectable = GetComponentsInChildren<SelectableButton>().FirstOrDefault();
        }
#endif
    }
}