using System.Collections.Generic;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    public abstract class SelectableGroupBase : MonoBehaviour
    {
        public abstract SelectableButton Selected { get; }
        public abstract SelectableButton Highlighted { get; }
        public abstract IReadOnlyList<SelectableButton> RegisteredSelectables { get; }

        public abstract event UnityAction<SelectableButton> SelectedChanged;
        public abstract event UnityAction<SelectableButton> HighlightedChanged;

        internal abstract void RegisterSelectable(SelectableButton buttonSelectable);
        internal abstract void UnregisterSelectable(SelectableButton buttonSelectable);
        public abstract void SelectSelectable(SelectableButton buttonSelectable);
        public abstract void HighlightSelectable(SelectableButton buttonSelectable);
        public abstract SelectableButton GetDefaultSelectable();

        public void EnableAllSelectables()
        {
            var selectables = RegisteredSelectables;
            foreach (var selectable in selectables)
                EnableSelectable(selectable);
        }

        public void DisableAllSelectables()
        {
            var selectables = RegisteredSelectables;
            for (int i = 0; i < selectables.Count; i++)
                DisableSelectable(selectables[i]);
        }

        public void RefreshSelected()
        {
            if (Selected != null && EventSystem.current.gameObject != Selected.gameObject)
                EventSystem.current.SetSelectedGameObject(Selected.gameObject);
        }

        public void DeselectSelected()
        {
            if (Selected != null)
                Selected.Deselect();
        }
        
        public void SelectDefault()
        {
            GetDefaultSelectable().OnSelect(null);
        }

        public void SelectDefaultIfNoSelected()
        {
            if (Selected == null || !Selected.isActiveAndEnabled)
                GetDefaultSelectable().OnSelect(null);
        }

        public void EnableSelectable(SelectableButton buttonSelectable) => buttonSelectable.gameObject.SetActive(true);
        public void DisableSelectable(SelectableButton buttonSelectable) => buttonSelectable.gameObject.SetActive(false);
    }
}