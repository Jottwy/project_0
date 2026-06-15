using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    [CustomEditor(typeof(LightEffect))]
    public sealed class LightEffectEditor : ToolboxEditor
    {
        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            EditorGUILayout.Space();

            if (!Application.isPlaying)
                GUI.enabled = false;

            if (GUILayout.Button("Play (fadeIn = true)"))
                ((LightEffect)target).Play();

            if (GUILayout.Button("Play (fadeIn = false)"))
                ((LightEffect)target).Play(false);

            if (!Application.isPlaying)
                GUI.enabled = true;
        }
    }
}