using PolymindGames.WorldManagement;
using PolymindGames.Editor;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.World.Editor
{
    [CustomEditor(typeof(TimeManager))]
    public sealed class TimeManagerEditor : ToolboxEditor
    {
        private TimeManager Time => (TimeManager)target;

        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            GUILayout.BeginVertical(EditorStyles.helpBox);
            {
                GUILayout.Label("Info", EditorStyles.boldLabel);
                GUILayout.Label($"Hour: {Time.Hour}", GUIStyles.BoldMiniGreyLabel);
                GUILayout.Label($"Minute: {Time.Minute}", GUIStyles.BoldMiniGreyLabel);
                GUILayout.Label($"Second: {Time.Second}", GUIStyles.BoldMiniGreyLabel);
                EditorGUILayout.Space();
                GUILayout.Label($"Total Hours: {Time.TotalHours}", GUIStyles.BoldMiniGreyLabel);
                GUILayout.Label($"Total Minutes: {Time.TotalMinutes}", GUIStyles.BoldMiniGreyLabel);
                EditorGUILayout.Space();
                GUILayout.Label($"Real seconds per day: {Time.GetDayDurationInRealSeconds()}",
                    GUIStyles.BoldMiniGreyLabel);
                GUILayout.Label($"Real minutes per day: {Time.GetDayDurationInRealMinutes()}",
                    GUIStyles.BoldMiniGreyLabel);

                EditorGUILayout.Space();

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Prev Day"))
                    Time.Day--;

                if (GUILayout.Button("Next Day"))
                    Time.Day++;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }
    }
}