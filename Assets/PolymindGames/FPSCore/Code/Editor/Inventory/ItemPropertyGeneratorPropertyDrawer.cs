using JetBrains.Annotations;
using PolymindGames.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.InventorySystem.Editor
{
    [UsedImplicitly]
    [CustomPropertyDrawer(typeof(ItemPropertyGenerator))]
    public sealed class ItemPropertyGeneratorPropertyDrawer : PropertyDrawer
    {
        private readonly DataDefinitionReferenceDrawer _propertyReferenceDrawer =
            new(new DataReferenceAttribute()
            {
                DataType = typeof(ItemPropertyDefinition),
                NullElement = "Empty"
            });

        private DataDefinitionReferenceDrawer _itemReferenceDrawer;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return (_propertyReferenceDrawer.GetHeight() + EditorGUIUtility.standardVerticalSpacing) * 2;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            position.height = EditorGUIUtility.singleLineHeight;

            var prop = DoPropertySelectionPopup(position, property);

            if (prop != null)
                DoPropertyValueSelection(position, property, prop);
        }

        private ItemPropertyDefinition DoPropertySelectionPopup(Rect position, SerializedProperty property)
        {
            SerializedProperty itemProp = property.FindPropertyRelative("_itemPropertyId");
            Rect popupRect = new Rect(position.x, position.y, position.width * 0.8f, position.height);

            _propertyReferenceDrawer.OnGUI(itemProp, popupRect, GUIContent.none);
            return ItemPropertyDefinition.GetWithId(itemProp.intValue);
        }

        private void DoPropertyValueSelection(Rect position, SerializedProperty property, ItemPropertyDefinition prop)
        {
            ItemPropertyType propertyType = prop.Type;

            Rect typeRect = new(
                x: position.xMax - position.width * 0.2f + EditorGUIUtility.standardVerticalSpacing,
                y: position.y,
                width: position.width * 0.2f - EditorGUIUtility.standardVerticalSpacing,
                height: position.height);

            EditorGUI.LabelField(typeRect, $"Type: {ObjectNames.NicifyVariableName(propertyType.ToString())}",
                EditorStyles.miniLabel);

            position.y = position.yMax + EditorGUIUtility.standardVerticalSpacing;
            SerializedProperty valueProp = property.FindPropertyRelative("_valueRange");

            switch (propertyType)
            {
                case ItemPropertyType.Boolean:
                    DoBoolValueProperty(position, valueProp);
                    break;
                case ItemPropertyType.Float or ItemPropertyType.Integer or ItemPropertyType.Double:
                    SerializedProperty isRandomProp = property.FindPropertyRelative("_useRandomValue");
                    DoNumberValueProperty(position, isRandomProp, propertyType, valueProp);
                    break;
                case ItemPropertyType.Item:
                    DoItemValueProperty(position, valueProp);
                    break;
            }
        }

        private static void DoBoolValueProperty(Rect position, SerializedProperty valueProp)
        {
            bool boolean = !Mathf.Approximately(valueProp.vector2Value.x, 0f);

            EditorGUI.LabelField(position, "True/False");
            boolean = EditorGUI.Toggle(new Rect(position.x + 86f, position.y, 16f, position.height), boolean);

            valueProp.vector2Value = new Vector2(boolean ? 1f : 0f, 0f);
        }

        private static void DoNumberValueProperty(Rect position, SerializedProperty isRandomProp, ItemPropertyType propertyType,
            SerializedProperty valueProp)
        {
            Rect selectModeRect = new(position.x, position.y, position.width * 0.35f, position.height);

            int selectedMode = GUI.Toolbar(selectModeRect, isRandomProp.boolValue ? 1 : 0, new[]
            {
                "Fixed", "Random"
            });
            isRandomProp.boolValue = selectedMode == 1;

            Rect valueRect = new(selectModeRect.xMax + EditorGUIUtility.singleLineHeight, position.y,
                position.width - selectModeRect.width - EditorGUIUtility.singleLineHeight, position.height);

            if (selectedMode == 0)
            {
                if (propertyType is ItemPropertyType.Float or ItemPropertyType.Double)
                {
                    float value = EditorGUI.FloatField(valueRect, valueProp.vector2Value.x);
                    valueProp.vector2Value = new Vector2(value, 0f);
                }
                else
                {
                    float value = EditorGUI.IntField(valueRect, Mathf.RoundToInt(valueProp.vector2Value.x));
                    valueProp.vector2Value = new Vector2(Mathf.Clamp(value, -9999999, 9999999), 0);
                }
            }
            else
            {
                float[] randomRange =
                {
                    valueProp.vector2Value.x, valueProp.vector2Value.y
                };

                valueProp.vector2Value = propertyType != ItemPropertyType.Integer
                    ? EditorGUI.Vector2Field(valueRect, GUIContent.none, valueProp.vector2Value)
                    : EditorGUI.Vector2IntField(valueRect, GUIContent.none, new Vector2Int(
                        x: Mathf.RoundToInt(randomRange[0]),
                        y: Mathf.RoundToInt(randomRange[1])));
            }
        }

        private void DoItemValueProperty(Rect position, SerializedProperty valueProp)
        {
            _itemReferenceDrawer ??= new DataDefinitionReferenceDrawer(new DataReferenceAttribute
                {
                    DataType = typeof(ItemDefinition),
                    NullElement = "Empty"
                });

            EditorGUI.LabelField(position, "Target Item");

            Rect itemPopupRect = EditorGUI.IndentedRect(position);
            itemPopupRect = new Rect(itemPopupRect.x + 80f, itemPopupRect.y, itemPopupRect.width * 0.8f - 80f,
                itemPopupRect.height);

            _itemReferenceDrawer.OnGUI(valueProp, itemPopupRect, GUIContent.none);
        }
    }
}