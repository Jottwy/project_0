using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using UnityEditor;
using Toolbox;

namespace PolymindGames.Editor
{
    /*
    [UsedImplicitly]
    public sealed class WizardsPage : RootToolPage
    {
        private IEnumerable<IEditorToolPage> _subPages;

        public override string DisplayName => "Wizards";
        public override int Order => 0;

        public override void DrawContent()
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
        }

        public override IEnumerable<IEditorToolPage> GetSubPages()
        {
            _subPages ??= CreateSubPages();
            return _subPages;
        }

        public override bool IsCompatibleWithObject(Object unityObject) => false;

        private static IEnumerable<IEditorToolPage> CreateSubPages()
        {
            var types = typeof(EditorWizardBase).GetAllChildClasses();
            var subPages = new ObjectInspectorToolPage[types.Count];

            for (var i = 0; i < types.Count; i++)
            {
                var wizardType = types[i];
                string cleanName = ObjectNames.NicifyVariableName(wizardType.Name.Replace("CreationWizard", ""));
                string pageName = "Create " + cleanName;
                subPages[i] = new ObjectInspectorToolPage(pageName, wizardType, 0, GetObject,
                    $"Quickly create and set up a new {cleanName}");

                Object GetObject() => ScriptableObject.CreateInstance(wizardType);
            }

            return subPages;
        }
    }*/
}