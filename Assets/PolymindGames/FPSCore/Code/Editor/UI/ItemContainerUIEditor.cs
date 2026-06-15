using PolymindGames.UserInterface;
using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.UISystem.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(ItemContainerUI), true)]
    public sealed class ItemContainerUIEditor : ToolboxEditor
    {
        private SerializedProperty _slotsParent;
        private SerializedProperty _slotTemplate;
        private int _slotCount;

        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            serializedObject.Update();

            EditorGUILayout.Space();

            if (!Application.isPlaying)
            {
                ToolboxEditorGui.DrawLine();
                GUILayout.BeginHorizontal();
                
                using (new BackgroundColorScope(GUIStyles.GreenColor))
                {
                    if (!serializedObject.isEditingMultipleObjects && GUILayout.Button("Spawn Default Slots"))
                    {
                        ((ItemContainerUI)target).GenerateSlots_EditorOnly(_slotCount);
                    }
                }

                _slotCount = EditorGUILayout.IntField(_slotCount);
                _slotCount = Mathf.Clamp(_slotCount, 0, 100);

                GUILayout.EndHorizontal();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void OnEnable()
        {
            _slotCount = ((Component)target).gameObject.GetComponentsInFirstChildren<ItemSlotUIBase>().Count;
        }
    }
}