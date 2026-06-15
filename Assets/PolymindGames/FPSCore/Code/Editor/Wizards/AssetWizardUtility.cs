using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    public static class AssetWizardUtility
    {
        public static bool FindMatchingAssetButton(SerializedProperty property, string matchingName, string ignoredString = "")
        {
            ToolboxEditorGui.DrawToolboxProperty(property);

            if (!ValidateMatchingAssetParams(property))
                return false;

            var disable = property.objectReferenceValue != null;
            using (new EditorGUI.DisabledScope(disable))
            {
                using (new BackgroundColorScope(GUIStyles.YellowColor))
                {
                    if (GUILayout.Button($"Find Matching {property.displayName}"))
                    {
                        FindMatchingAsset(property, matchingName, ignoredString);
                        return true;
                    }
                }
            }

            return false;
        }

        public static void FindMatchingAsset(SerializedProperty property, string matchingName, string ignoredString = "")
        {
            if (!ValidateMatchingAssetParams(property))
                return;

            property.GetFieldInfo(out var fieldType);

            var matchingAsset = IsPrefab(fieldType)
                ? AssetDatabaseUtility.FindClosestMatchingPrefab(fieldType, matchingName, ignoredString)
                : AssetDatabaseUtility.FindClosestMatchingObjectWithName(fieldType, matchingName, ignoredString);

            property.objectReferenceValue = matchingAsset;

            static bool IsPrefab(Type type)
            {
                return type == typeof(GameObject) || type.IsSubclassOf(typeof(Component));
            }
        }
        
        public static void ShowWizardButton(ref Rect rect, SerializedProperty property, Func<AssetCreationWizard> wizardFactory)
        {
            if (!ValidateWizardParams(property, wizardFactory))
                return;

            bool disable = property.objectReferenceValue != null;
            using (new EditorGUI.DisabledScope(disable))
            {
                using (new BackgroundColorScope(GUIStyles.GreenColor))
                {
                    if (GUILayout.Button($"Create {property.displayName}"))
                    {
                        ShowWizard(rect, property, wizardFactory);
                    }
                }
            }

            if (Event.current.type == EventType.Repaint)
                rect = GUILayoutUtility.GetLastRect();
        }

        public static void ShowWizard(Rect rect, SerializedProperty property, Func<AssetCreationWizard> wizardFactory)
        {
            if (!ValidateWizardParams(property, wizardFactory))
                return;

            var wizard = wizardFactory();

            if (wizard == null)
            {
                Debug.LogError("Wizard factory argument not valid.");
                return;
            }

            // wizard.ObjectCreatedCallback = AssignAsset;
            // PopupWindow.Show(rect, wizard);

            // void AssignAsset(Object unityObject)
            // {
            //     var fieldInfo = property.GetFieldInfo(out _);
            //     property.SetProperValue(fieldInfo, unityObject);
            // }
        }

        private static bool ValidateMatchingAssetParams(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                Debug.LogWarning($"The property ''{property.displayName}'' needs to be a Unity Object.", property.serializedObject.targetObject);
                return false;
            }

            return true;
        }

        private static bool ValidateWizardParams(SerializedProperty property, Func<AssetCreationWizard> createWizardFunc)
        {
            bool valid = false;

            if (property != null && createWizardFunc != null)
            {
                if (property.propertyType == SerializedPropertyType.ObjectReference)
                    valid = true;
            }

            if (!valid)
                Debug.LogError($"Please ensure none of the arguments are null and that the property is of type {nameof(SerializedPropertyType.ObjectReference)}");

            return valid;
        }
    }
}