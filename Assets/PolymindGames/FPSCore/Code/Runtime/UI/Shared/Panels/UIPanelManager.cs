using PolymindGames.InputSystem;
using System.Collections.Generic;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Manages the visibility and stacking of UI panels across different layers.
    /// This class handles the logic for showing and hiding panels while managing their stacking order based on layers.
    /// </summary>
    public sealed class UIPanelManager
    {
        private const int UndefinedLayer = -1;
        private const int MaxLayerCount = 8;

        private static readonly UIPanelManager _instance = new();
        private readonly List<UIPanel>[] _panelsByLayer = new List<UIPanel>[MaxLayerCount];

        #region Initialization
#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void OnRuntimeInitialize()
        {
            // Clears all panel lists when the subsystem is reloaded in the editor.
            if (_instance != null)
            {
                foreach (var panelList in _instance._panelsByLayer)
                    panelList?.Clear();
            }
        }
#endif
        #endregion

        /// <summary>
        /// Shows the specified panel, making it visible and managing its layer if needed.
        /// If the panel is stackable, it is pushed onto the stack. Otherwise, it is directly shown.
        /// </summary>
        /// <param name="panel">The panel to show.</param>
        public static void ShowPanel(UIPanel panel)
        {
            if (panel == null)
            {
                Debug.LogError("Cannot show a null panel.");
                return;
            }

            if (ShouldStackPanel(panel))
               _instance.AddPanelToStack(panel);
            else
                panel.ChangeVisibility(true);
        }

        /// <summary>
        /// Hides the specified panel, making it invisible and managing its layer if needed.
        /// If the panel is stackable, it is removed from the stack. Otherwise, it is directly hidden.
        /// </summary>
        /// <param name="panel">The panel to hide.</param>
        public static void HidePanel(UIPanel panel)
        {
            if (panel == null)
            {
                Debug.LogError("Cannot hide a null panel.");
                return;
            }

            if (ShouldStackPanel(panel))
               _instance.RemovePanelFromStack(panel);
            else
                panel.ChangeVisibility(false);
        }

        /// <summary>
        /// Determines if a panel should be managed in a stack based on its layer and escape behavior.
        /// Panels on layers other than UNDEFINED_LAYER and those that cannot escape are not stackable.
        /// </summary>
        /// <param name="panel">The panel to check.</param>
        /// <returns>True if the panel should be stackable; otherwise, false.</returns>
        private static bool ShouldStackPanel(UIPanel panel)
        {
            int layer = panel.PanelLayer;
            return layer < MaxLayerCount && (layer != UndefinedLayer || panel.CanEscape);
        }

        /// <summary>
        /// Adds a panel to the stack, making it visible and ensuring it is on top of other panels in its layer.
        /// Hides the previous top panel if necessary and manages escape callbacks.
        /// </summary>
        /// <param name="panel">The panel to add to the stack.</param>
        private void AddPanelToStack(UIPanel panel)
        {
            var panelList = GetOrCreatePanelListForLayer(panel.PanelLayer);

            UIPanel topPanel = panelList.Count > 0 ? panelList[panelList.Count - 1] : null;
            if (topPanel == panel)
                return;

            if (panel.PanelLayer != UndefinedLayer && topPanel != null)
                topPanel.ChangeVisibility(false);

            if (panel.CanEscape)
                InputManager.Instance.PushEscapeCallback(panel.Hide);

            // Set the new panel as the top panel.
            panelList.Remove(panel);
            panelList.Add(panel);

            panel.ChangeVisibility(true);
        }

        /// <summary>
        /// Removes a panel from the stack, making it invisible and updating the visibility of the new top panel.
        /// Manages escape callbacks appropriately.
        /// </summary>
        /// <param name="panel">The panel to remove from the stack.</param>
        private void RemovePanelFromStack(UIPanel panel)
        {
            var panelList = GetOrCreatePanelListForLayer(panel.PanelLayer);
            int index = panelList.IndexOf(panel);

            if (index != -1)
            {
                panelList.RemoveAt(index);

                if (panel.CanEscape)
                    InputManager.Instance.PopEscapeCallback(panel.Hide);

                if (panel.PanelLayer != UndefinedLayer)
                {
                    UIPanel newTopPanel = panelList.Count > 0 ? panelList[panelList.Count - 1] : null;
                    newTopPanel?.ChangeVisibility(true);
                }
            }

            panel.ChangeVisibility(false);
        }

        /// <summary>
        /// Retrieves or creates the list of panels for a specified layer.
        /// Ensures that a list is initialized if it does not already exist for the given layer.
        /// </summary>
        /// <param name="layer">The layer index for which to retrieve or create the panel list.</param>
        /// <returns>The list of panels for the specified layer.</returns>
        private List<UIPanel> GetOrCreatePanelListForLayer(int layer)
        {
            layer = Mathf.Clamp(layer, 0, MaxLayerCount - 1);

            var panelList = _panelsByLayer[layer];
            if (panelList == null)
            {
                panelList = new List<UIPanel>();
                _panelsByLayer[layer] = panelList;
            }

            return panelList;
        }
    }
}