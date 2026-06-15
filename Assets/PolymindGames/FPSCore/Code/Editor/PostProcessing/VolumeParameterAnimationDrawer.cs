using JetBrains.Annotations;
using PolymindGames.Editor;
using Toolbox.Editor.Drawers;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.PostProcessing.Editor
{
    [UsedImplicitly]
    public sealed class VolumeParameterAnimationDrawer : ToolboxTargetTypeDrawer
    {
        public override void OnGui(SerializedProperty property, GUIContent label)
        {
            EditorGUILayout.LabelField(label, GUIStyles.BoldMiniGreyLabel);
            ToolboxEditorGui.DrawPropertyChildren(property);
        }

        public override Type GetTargetType() => typeof(VolumeParameterAnimation);
        public override bool UseForChildren() => true;
    }
}