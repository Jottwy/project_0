using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.InventorySystem.Editor
{
    [CustomEditor(typeof(ItemDefinition))]
    public sealed class ItemDefinitionEditor : DataDefinitionEditor<ItemDefinition>
    {
        private Rect _pickupBtnRect;

        protected override Action<SerializedProperty> GetDrawingAction() => DrawProperty;

        private void DrawProperty(SerializedProperty property)
        {
            switch (property.name)
            {
                case "_icon":
                    DrawIconProperty(property);
                    break;
                case "_pickup":
                    DrawPickupProperty(property);
                    break;
                case "_actions":
                    DrawActionsProperty(property);
                    break;
                default:
                    ToolboxEditorGui.DrawToolboxProperty(property);
                    break;
            }
        }

        private void DrawIconProperty(SerializedProperty property)
        {
            GUILayout.BeginHorizontal();
            AssetWizardUtility.FindMatchingAssetButton(property, Definition.Name, "Item_");
            GUILayout.EndHorizontal();
        }

        private void DrawPickupProperty(SerializedProperty property)
        {
            GUILayout.BeginHorizontal();
            AssetWizardUtility.FindMatchingAssetButton(property, Definition.Name, "Pickup_");
            // AssetWizardUtility.ShowWizardButton(ref _pickupBtnRect, property, () => CreateInstance<ItemPickupCreationWizard>());
            GUILayout.EndHorizontal();
        }

        private void DrawActionsProperty(SerializedProperty property)
        {
            ToolboxEditorGui.DrawToolboxProperty(property);

            if (!Definition.HasParentGroup)
                return;

            GUILayout.BeginVertical(EditorStyles.helpBox);

            using (new EditorGUI.DisabledScope(true))
            {
                GUILayout.Label("Actions (Inherited)");
                GUILayout.Space(3f);

                GUILayout.BeginHorizontal();

                foreach (var action in Definition.ParentGroup.BaseActions)
                    EditorGUILayout.ObjectField(action, typeof(ItemAction), false);

                GUILayout.EndVertical();
            }

            GUILayout.EndVertical();

            EditorGUILayout.Space();
        }
    }
}