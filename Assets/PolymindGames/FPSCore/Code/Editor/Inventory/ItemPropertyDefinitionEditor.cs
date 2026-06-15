using System.Collections.Generic;
using PolymindGames.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.InventorySystem.Editor
{
    [CustomEditor(typeof(ItemPropertyDefinition))]
    public sealed class ItemPropertyDefinitionEditor : DataDefinitionEditor<ItemPropertyDefinition>
    {
        private static bool _showFoldout;
        
        private List<ItemDefinition> _itemReferences;
        private Vector2 _scrollView;

        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            EditorGUILayout.Space();

            _showFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_showFoldout, "References");

            if (_showFoldout)
                DrawReferences();

            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawReferences()
        {
            var property = (ItemPropertyDefinition)target;
            _itemReferences ??= ItemDefinition.GetAllItemsWithProperty(property);

            using var scroll = new GUILayout.ScrollViewScope(_scrollView);
            _scrollView = scroll.scrollPosition;

            using (new EditorGUI.DisabledScope(true))
            {
                if (_itemReferences.Count == 0)
                    GUILayout.Label("No references found..");

                foreach (var item in _itemReferences)
                    EditorGUILayout.ObjectField(item, typeof(ItemDefinition), false);
            }
        }
    }
}