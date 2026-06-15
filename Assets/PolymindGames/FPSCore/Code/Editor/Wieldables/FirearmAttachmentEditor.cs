using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System.Linq;

namespace PolymindGames.WieldableSystem.Editor
{
    [CustomEditor(typeof(FirearmAttachment))]
    public sealed class FirearmAttachmentEditor : ToolboxEditor
    {
        private readonly GUILayoutOption[] _pingOptions = { GUILayout.Height(22), GUILayout.Width(44) };
        private FirearmComponentBehaviour[] _components;
        private GUIContent[] _contents;

        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            if (_components.Length > 0)
            {
                GUILayout.Label($"Components ({_components.Length})", EditorStyles.boldLabel);

                for (int i = 0; i < _components.Length; i++)
                    DrawAttachment(_components[i], _contents[i]);
            }
            else
            {
                GUILayout.BeginVertical(EditorStyles.helpBox);
                GUILayout.Label($"No firearm components found under this object", EditorStyles.miniLabel);
                GUILayout.EndVertical();
            }
        }

        private void DrawAttachment(FirearmComponentBehaviour component, GUIContent content)
        {
            GUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label(content);

            using (new BackgroundColorScope(GUIStyles.YellowColor))
            {
                if (GUILayout.Button("Ping", _pingOptions))
                    EditorGUIUtility.PingObject(component);
            }

            GUILayout.EndHorizontal();
        }

        private void OnEnable()
        {
            _components = ((Component)target)
                .GetComponentsInChildren<FirearmComponentBehaviour>(true);

            if (_components != null && _components.Length > 0)
            {
                _contents = _components.Select(behaviour =>
                    new GUIContent($"{ObjectNames.NicifyVariableName(behaviour.GetType().Name)}")).ToArray();
            }
        }
    }
}