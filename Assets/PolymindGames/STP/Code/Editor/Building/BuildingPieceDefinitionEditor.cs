using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.BuildingSystem.Editor
{
    [CustomEditor(typeof(BuildingPieceDefinition))]
    public sealed class BuildingPieceDefinitionEditor : DataDefinitionEditor<BuildingPieceDefinition>
    {
        private Rect _buildableBtnRect;

        protected override Action<SerializedProperty> GetDrawingAction() => DrawProperty;

        private void DrawProperty(SerializedProperty property)
        {
            switch (property.name)
            {
                case "_icon":
                    DrawIconProperty(property);
                    break;
                case "_prefab":
                    DrawBuildingPiece(property);
                    break;
                default:
                    ToolboxEditorGui.DrawToolboxProperty(property);
                    break;
            }
        }

        private void DrawIconProperty(SerializedProperty property)
        {
            GUILayout.BeginHorizontal();
            AssetWizardUtility.FindMatchingAssetButton(property, Definition.Name, "BuildingPiece_");
            GUILayout.EndHorizontal();
        }

        private void DrawBuildingPiece(SerializedProperty property)
        {
            GUILayout.BeginHorizontal();
            AssetWizardUtility.FindMatchingAssetButton(property, Definition.Name, "BuildingPiece_");
            // AssetWizardUtility.ShowWizardButton(ref _buildableBtnRect, property, () => new BuildingPieceCreationWizard(Definition));
            GUILayout.EndHorizontal();
        }
    }
}