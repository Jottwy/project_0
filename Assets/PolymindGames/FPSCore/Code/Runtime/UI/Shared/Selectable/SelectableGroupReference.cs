using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [AddComponentMenu("Polymind Games/User Interface/Selectables/Selectable Group Reference")]
    public sealed class SelectableGroupReference : SelectableGroupBase
    {
        [SerializeField, NotNull]
        private SelectableGroup _referencedGroup;

        public SelectableGroup ReferencedGroup
        {
            get => _referencedGroup;
            set => _referencedGroup = value;
        }

        public override SelectableButton Selected => _referencedGroup.Selected;
        public override SelectableButton Highlighted => _referencedGroup.Highlighted;
        public override IReadOnlyList<SelectableButton> RegisteredSelectables => _referencedGroup.RegisteredSelectables;

        public override event UnityAction<SelectableButton> SelectedChanged
        {
            add => _referencedGroup.SelectedChanged += value;
            remove => _referencedGroup.SelectedChanged -= value;
        }

        public override event UnityAction<SelectableButton> HighlightedChanged
        {
            add => _referencedGroup.HighlightedChanged += value;
            remove => _referencedGroup.HighlightedChanged -= value; 
        }

        internal override void RegisterSelectable(SelectableButton buttonSelectable) => _referencedGroup.RegisterSelectable(buttonSelectable);
        internal override void UnregisterSelectable(SelectableButton buttonSelectable) => _referencedGroup.UnregisterSelectable(buttonSelectable);
        public override void SelectSelectable(SelectableButton buttonSelectable) => _referencedGroup.SelectSelectable(buttonSelectable);
        public override void HighlightSelectable(SelectableButton buttonSelectable) => _referencedGroup.HighlightSelectable(buttonSelectable);
        public override SelectableButton GetDefaultSelectable() => _referencedGroup.GetDefaultSelectable();

        private void Awake()
        {
            if (_referencedGroup == null)
                Debug.LogError($"The referenced group on ''{gameObject.name}'' is null, you need to assign it in the inspector.", gameObject);
        }
    }
}