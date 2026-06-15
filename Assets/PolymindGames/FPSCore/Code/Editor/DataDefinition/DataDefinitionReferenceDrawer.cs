using System.Reflection;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    public sealed class DataDefinitionReferenceDrawer
    {
        private readonly DataReferenceAttribute _attribute;
        private readonly DataDefinition[] _definitions;
        private readonly GUIContent[] _contents;

        #region Initialization
        public DataDefinitionReferenceDrawer(DataReferenceAttribute attribute)
        {
            _attribute = attribute;
            _definitions = GetDefinitions(attribute.DataType);
            _contents = CreateDefinitionGUIContents(_definitions, attribute.NullElement, attribute.HasIcon);
        }

        private static DataDefinition[] GetDefinitions(Type type)
        {
            try
            {
                if (type == null)
                {
                    Debug.Log("The provided type is null.");
                    return null;
                }

                // Ensure the provided type is a subclass of DataDefinition
                if (!typeof(DataDefinition).IsAssignableFrom(type))
                {
                    Debug.Log($"The provided type is not a subclass of DataDefinition. {type}");
                    return null;
                }

                // Use reflection to find the static Definitions property
                PropertyInfo definitionsProperty = type.GetProperty(
                    "Definitions",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy
                );

                // Ensure the property exists and is readable
                if (definitionsProperty != null && definitionsProperty.CanRead)
                {
                    // Invoke the getter to retrieve the definitions
                    return (DataDefinition[])definitionsProperty.GetValue(null);
                }
            }
            catch (Exception ex)
            {
                // Log or handle exceptions as needed
                Console.WriteLine($"Error accessing Definitions: {ex.Message}");
            }

            return null;
        }

        private static GUIContent[] CreateDefinitionGUIContents(DataDefinition[] definitions, string nullElement, bool hasIcon)
        {
            var contents = new GUIContent[definitions.Length + 1];

            contents[0] = new GUIContent(nullElement);
            for (int i = 1; i < definitions.Length + 1; i++)
            {
                var def = definitions[i - 1];
                contents[i] = new GUIContent(def.FullName, hasIcon ? def.Icon?.texture : null, def.Description);
            }

            return contents;
        }
        #endregion

        public float GetHeight()
        {
            float extraHeight = _attribute.HasAssetReference ? EditorGUIUtility.singleLineHeight + 3f : 0f;
            return EditorGUIUtility.singleLineHeight + extraHeight;
        }

        public void OnGUI(SerializedProperty property, Rect position, GUIContent label)
        {
            // Determine the selected index based on property type
            int selectedIndex = property.propertyType switch
            {
                SerializedPropertyType.Integer => GetIndexOfId(property.intValue),
                SerializedPropertyType.String => GetIndexOfName(property.stringValue),
                SerializedPropertyType.Vector2 => GetIndexOfId(Mathf.RoundToInt(property.vector2Value.x)),
                SerializedPropertyType.ObjectReference => GetIndexOfDef(property.objectReferenceValue as DataDefinition),
                _ => 0
            };

            // Display popup with the determined label and selected index
            Popup(property, position, _attribute.HasLabel ? label : GUIContent.none, selectedIndex);

            // Draw asset reference, if applicable
            if (_attribute.HasAssetReference)
            {
                DrawAssetReference(position, selectedIndex);
            }
        }

        private void Popup(SerializedProperty property, Rect position, GUIContent label, int selectedIndex)
        {
            position = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            int newSelected = EditorGUI.Popup(position, label, selectedIndex, _contents);
            
            if (newSelected != selectedIndex)
            {
                property.serializedObject.Update();
                UpdatePropertyBasedOnSelection(property, newSelected);
                property.serializedObject.ApplyModifiedProperties();
            }
        }

        private void UpdatePropertyBasedOnSelection(SerializedProperty property, int selectedIndex)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    property.intValue = GetIdAtIndex(selectedIndex);
                    break;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = GetDefAtIndex(selectedIndex);
                    break;
                case SerializedPropertyType.Vector2:
                    property.vector2Value = new Vector2(GetIdAtIndex(selectedIndex), 0f);
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = selectedIndex < _contents.Length ? _contents[selectedIndex].text : string.Empty;
                    break;
                default:
                    Debug.LogWarning($"Unsupported property type: {property.propertyType}");
                    break;
            }
        }

        private void DrawAssetReference(Rect position, int selectedIndex)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                float height = EditorGUIUtility.singleLineHeight;
                position = new Rect(position.x + height / 2, position.y + height, position.width - height, height);
                EditorGUI.ObjectField(position, "Asset", GetDefAtIndex(selectedIndex), _attribute.DataType, false);
            }
        }

        private int GetIndexOfDef(DataDefinition dataDefinition)
            => Array.IndexOf(_definitions, dataDefinition) + 1;
        
        private int GetIndexOfId(int id)
            => Array.FindIndex(_definitions, def => def.Id == id) + 1;
        
        private int GetIndexOfName(string name)
            => Array.FindIndex(_definitions, def => def.Name == name) + 1;

        private DataDefinition GetDefAtIndex(int index)
            => IndexIsValid(index) ? _definitions[index - 1] : null;

        private int GetIdAtIndex(int index)
            => IndexIsValid(index) ? _definitions[index - 1].Id : 0;

        private bool IndexIsValid(int index)
            => index > 0 && index <= _definitions.Length;
    }
}