using Toolbox.Editor.Drawers;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System.Linq;
using System;

namespace PolymindGames.Editor
{
    [CustomPropertyDrawer(typeof(DataIdReference<>))]
    [CustomPropertyDrawer(typeof(DataReferenceAttribute))]
    public sealed class DataReferencePropertyDrawer : PropertyDrawerBase
    {
        private DataDefinitionReferenceDrawer _referenceDrawer;
        private string _propertyPath;

        protected override float GetPropertyHeightSafe(SerializedProperty property, GUIContent label)
        {
            HandlePropertyChanges(property);
            return _referenceDrawer.GetHeight();
        }

        protected override void OnGUISafe(Rect position, SerializedProperty property, GUIContent label)
        {
            HandlePropertyChanges(property);
            var referenceProperty = property.FindPropertyRelative("_value") ?? property;
            _referenceDrawer.OnGUI(referenceProperty, position, label);
        }
 
        private void HandlePropertyChanges(SerializedProperty property)
        {
            if (_referenceDrawer != null && property.propertyPath == _propertyPath)
                return;

            _propertyPath = property.propertyPath;
            var fieldType = fieldInfo.FieldType;
            
            Type dataType = fieldType.IsGenericType || fieldType.IsArray && fieldType.GetElementType().IsGenericType
                ? (fieldType.IsArray ? fieldType.GetElementType().GenericTypeArguments.FirstOrDefault() : fieldType.GenericTypeArguments.FirstOrDefault())
                : typeof(DataDefinition).IsAssignableFrom(fieldType) ? fieldType : null;
            
            var attr = PropertyUtility.GetAttribute<DataReferenceAttribute>(property);
            attr ??= new DataReferenceAttribute();
            
            if (dataType != null)
                attr.DataType = dataType;

            _referenceDrawer = new DataDefinitionReferenceDrawer(attr);
        }
    }
}