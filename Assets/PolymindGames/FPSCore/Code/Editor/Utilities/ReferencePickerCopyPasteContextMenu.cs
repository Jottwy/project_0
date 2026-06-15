using SerializationUtility = PolymindGames.OdinSerializer.SerializationUtility;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    [InitializeOnLoad]
    public static class ReferencePickerCopyPasteContextMenu
    {
        private static object[] _copiedReferences;
        private static object _copiedReference;

#if UNITY_2021_2_OR_NEWER
        static ReferencePickerCopyPasteContextMenu()
        {
            EditorApplication.contextualPropertyMenu += ShowCopyPasteContextMenu;
        }

        private static void ShowCopyPasteContextMenu(GenericMenu menu, SerializedProperty property)
        {
            if (IsNormalManagedReference(property))
            {
                var copyProperty = property.Copy();
                menu.AddItem(new GUIContent("Copy Serialize Reference"), false,
                    _ =>
                    {
                        _copiedReference = property.managedReferenceValue;
                    }, null);

                var pasteContent = new GUIContent("Paste Serialize Reference");
                if (_copiedReference != null)
                {
                    menu.AddItem(pasteContent, false,
                        _ => PasteSerializeReference(copyProperty), null);
                }
                else
                {
                    menu.AddDisabledItem(pasteContent, false);
                }

                return;
            }

            if (IsManagedReferenceArray(property))
            {
                var copyProperty = property.Copy();
                menu.AddItem(new GUIContent("Copy Serialize Reference(s)"), false,
                    _ =>
                    {
                        _copiedReferences = GetReferencesCopy(property);
                    }, null);

                var pasteContent = new GUIContent("Paste Serialize Reference(s)");
                if (_copiedReferences != null)
                {
                    menu.AddItem(pasteContent, false,
                        _ => PasteSerializeReferences(copyProperty), null);
                }
                else
                {
                    menu.AddDisabledItem(pasteContent, false);
                }
            }
        }

        private static void PasteSerializeReference(SerializedProperty to)
        {
            to.serializedObject.Update();
            var deepCopy = SerializationUtility.CreateCopy(_copiedReference);
            to.managedReferenceValue = deepCopy;
            to.serializedObject.ApplyModifiedProperties();
        }

        private static void PasteSerializeReferences(SerializedProperty to)
        {
            to.serializedObject.Update();
            to.ClearArray();
            for (int i = 0; i < _copiedReferences.Length; i++)
            {
                var deepCopy = SerializationUtility.CreateCopy(_copiedReferences[i]);

                to.InsertArrayElementAtIndex(i);
                to.GetArrayElementAtIndex(i).managedReferenceValue = deepCopy;
            }
            to.serializedObject.ApplyModifiedProperties();
        }

        private static object[] GetReferencesCopy(SerializedProperty property)
        {
            var deepCopies = new object[property.arraySize];
            for (int i = 0; i < property.arraySize; i++)
                deepCopies[i] = SerializationUtility.CreateCopy(property.GetArrayElementAtIndex(i).managedReferenceValue);

            return deepCopies;
        }

        private static bool IsNormalManagedReference(SerializedProperty property)
        {
            return property.propertyType == SerializedPropertyType.ManagedReference;
        }

        private static bool IsManagedReferenceArray(SerializedProperty property)
        {
            return property.isArray && property.arrayElementType == "managedReference<>";
        }
#endif
    }
}