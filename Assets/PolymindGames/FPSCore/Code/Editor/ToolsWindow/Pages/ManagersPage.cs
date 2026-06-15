using System.Collections.Generic;
using JetBrains.Annotations;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using System.Linq;
using Toolbox;

namespace PolymindGames.Editor
{
    [UsedImplicitly]
    public sealed class ManagersPage : RootToolPage
    {
        private IEnumerable<IEditorToolPage> _subPages;

        public override string DisplayName => "Managers";
        public override int Order => 2;

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
            var types = typeof(Manager).GetAllChildClasses();
            types.RemoveAll(type => type.GetCustomAttribute(typeof(CreateAssetMenuAttribute)) == null);
            var subPages = new ObjectInspectorToolPage[types.Count];

            for (var i = 0; i < types.Count; i++)
            {
                var type = types[i];
                string pageName = ObjectNames.NicifyVariableName(type.Name);
                subPages[i] = new ObjectInspectorToolPage(pageName, type, 0, GetObject);

                Object GetObject() => Resources.LoadAll(Manager.ManagersPath, type).FirstOrDefault();
            }

            return subPages;
        }
    }
}