using Toolbox.Editor.Drawers;
using JetBrains.Annotations;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.SaveSystem.Editor
{
    using Object = UnityEngine.Object;

    [UsedImplicitly]
    public sealed class SerializedGuidDrawer : ToolboxTargetTypeDrawer
    {
        public override void OnGui(SerializedProperty property, GUIContent label)
        {
            Object targetObj = property.serializedObject.targetObject;

            using (new EditorGUI.DisabledScope(true))
            {
                property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, label, true);

                if (property.isExpanded)
                {
                    GUILayout.BeginHorizontal();
                    DrawGuidGui(property, targetObj);
                    GUILayout.EndHorizontal();
                }
            }
        }

        public override Type GetTargetType() => typeof(SerializedGuid);
        public override bool UseForChildren() => true;

        private static void DrawGuidGui(SerializedProperty property, Object targetObj)
        {
            var attribute = PropertyUtility.GetAttribute<SerializedGuidDetailsAttribute>(property);
            bool isDisabled = (attribute == null || attribute.DisableForPrefabs) &&
                              UnityUtility.IsAssetOnDisk((Component)targetObj);

            if (isDisabled)
                EditorGUILayout.TextField("Guid", "Not available for prefabs");
            else
            {
                Guid guid = (SerializedGuid)property.boxedValue;
                EditorGUILayout.TextField("Guid", guid.ToString());

                if (attribute == null || attribute.HasNewGuidButton)
                {
                    GUI.enabled = true;
                    if (GUILayout.Button("New Guid"))
                    {
                        if (targetObj is GuidComponent guidComponent)
                            GuidManager.Add(guidComponent);

                        property.boxedValue = new SerializedGuid(Guid.NewGuid());
                        EditorUtility.SetDirty(targetObj);
                    }
                }
            }
        }
    }
}