using System.Collections.Generic;
using JetBrains.Annotations;
using Toolbox.Editor;
using System.Linq;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    [UsedImplicitly]
    public sealed class DefinitionsPage : RootToolPage
    {
        private IEnumerable<IEditorToolPage> _subPages;

        public override string DisplayName => "Definitions";
        public override int Order => 1;

        public override void DrawContent()
        {
            GUILayout.BeginVertical();
            {
                GUILayout.BeginVertical();
                {
                    GUILayout.Label(DisplayName, GUIStyles.Title);
                    GUILayout.BeginVertical(EditorStyles.helpBox);
                    DrawPageLinks(GetSubPages());
                    GUILayout.FlexibleSpace();
                    GUILayout.EndVertical();
                }
                GUILayout.EndVertical();

                GUILayout.BeginVertical();
                {
                    GUILayout.Label("Shortcuts", GUIStyles.Title);
                    GUILayout.BeginVertical(EditorStyles.helpBox);
                    
                    DrawDataDefinitionShortcuts();
                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Refresh Definitions", GUIStyles.Button))
                    {
                        DataDefinitionAssetUtility.RefreshAllDefinitions();
                    }

                    GUILayout.EndVertical();
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndVertical();
        }

        public override IEnumerable<IEditorToolPage> GetSubPages()
        {
            _subPages ??= CreateSubPages();
            return _subPages;
        }

        public override bool IsCompatibleWithObject(UnityEngine.Object unityObject) => false;

        private static IEnumerable<IEditorToolPage> CreateSubPages()
        {
            var definitionTypes = typeof(DataDefinitionPage).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && !type.IsNested && type.IsSubclassOf(typeof(DataDefinitionPage)))
                .ToArray();

            var subPages = new IEditorToolPage[definitionTypes.Length];
            for (int i = 0; i < subPages.Length; i++)
                subPages[i] = (IEditorToolPage)Activator.CreateInstance(definitionTypes[i]);

            return subPages;
        }

        private static void DrawDataDefinitionShortcuts()
        {
            // Display the shortcuts
            var shortcutStyle = new GUIStyle(GUIStyles.BoldMiniGreyLabel)
            {
                fontSize = 13
            };

            DrawShortcut("'F5'", "Refresh database", shortcutStyle);
            DrawShortcut("'Ctrl + N'", "Create a new definition", shortcutStyle);
            DrawShortcut("'Ctrl + D'", "Duplicate selected definitions", shortcutStyle);
            DrawShortcut("'Ctrl + C'", "Copy selected definitions", shortcutStyle);
            DrawShortcut("'Ctrl + V'", "Paste copied definitions", shortcutStyle);
            DrawShortcut("'Del'", "Delete selected definitions", shortcutStyle);
            ToolboxEditorGui.DrawLine();
            DrawShortcut("'Shift' + 'Right Arrow'", "Set focus to selected page", shortcutStyle);
            DrawShortcut("'Shift' + 'Left Arrow'", "Return focus to tools", shortcutStyle);
        }

        private static void DrawShortcut(string shortcut, string label, GUIStyle labelStyle)
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(shortcut, labelStyle);
            GUILayout.Label(label);
            GUILayout.EndVertical();
        }
    }
}