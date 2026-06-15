using System.Reflection;
using Toolbox.Editor;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    using UnityObject = UnityEngine.Object;

    [CustomEditor(typeof(CharacterBehaviour), true)]
    public class CharacterBehaviourEditor : ToolboxEditor
    {
        private readonly GUILayoutOption[] _pingOptions = { GUILayout.Height(20f), GUILayout.Width(45f) };
        private DependenciesData _dependenciesData;
        private ICharacter _parentCharacter;
        private string _dependenciesLabel;
        private bool _dependenciesEnabled;

        private const string DependenciesEnabledFoldoutKey = "CharacterBehaviour.DependenciesEnabled";

        public override void DrawCustomInspector()
        {
            base.DrawCustomInspector();

            if (_parentCharacter == null)
            {
                GUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.HelpBox("No Parent Character Found", MessageType.Error);
                GUILayout.EndVertical();
                return;
            }

            if (!_dependenciesData.IsValid)
                return;

            EditorGUILayout.Space();
            _dependenciesEnabled =
                EditorGUILayout.Foldout(_dependenciesEnabled, _dependenciesLabel, true, GUIStyles.Foldout);

            if (!_dependenciesEnabled)
                return;

            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Required", EditorStyles.miniBoldLabel);
            DrawComponentLabels(_parentCharacter, _dependenciesData.RequiredComponents, MessageType.Error);
            GUILayout.EndVertical();

            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Optional", EditorStyles.miniBoldLabel);
            DrawComponentLabels(_parentCharacter, _dependenciesData.OptionalComponents,
                MessageType.Warning);
            GUILayout.EndVertical();
        }

        protected virtual void OnEnable()
        {
            _dependenciesData = CreateBehaviourData(target.GetType());
            _parentCharacter = ((MonoBehaviour)target).gameObject.GetComponentInParent<ICharacter>();

            (int required, int optional) = GetFoundCount(_parentCharacter, _dependenciesData);
            _dependenciesLabel = GetDependenciesText(_dependenciesData, required, optional);

            bool defaultEnable = _dependenciesData != null && required != _dependenciesData.RequiredComponents.Length;
            _dependenciesEnabled = SessionState.GetBool(DependenciesEnabledFoldoutKey, defaultEnable);
        }

        private void OnDisable()
        {
            SessionState.SetBool(DependenciesEnabledFoldoutKey, _dependenciesEnabled);
        }

        private static (int required, int optional) GetFoundCount(ICharacter character, DependenciesData data)
        {
            if (character == null || !data.IsValid)
                return default((int required, int optional));

            int requiredFound = 0;
            foreach (var component in data.RequiredComponents)
                requiredFound += character.GetCC(component) != null ? 1 : 0;

            int optionalFound = 0;
            foreach (var component in data.OptionalComponents)
                optionalFound += character.GetCC(component) != null ? 1 : 0;

            return (requiredFound, optionalFound);
        }

        private static string GetDependenciesText(DependenciesData data, int requiredCount, int optionalCount)
        {
            if (!data.IsValid)
                return string.Empty;

            string requiredColor = requiredCount == data.RequiredComponents.Length ? "white" : "red";
            string optionalColor = optionalCount == data.OptionalComponents.Length ? "white" : "yellow";

            return $"<b>Dependencies</b>  " +
                   $"(<color={requiredColor}>{requiredCount}/{data.RequiredComponents.Length}</color>) " +
                   $"(<color={optionalColor}>{optionalCount}/{data.OptionalComponents.Length}</color>)";
        }

        private static DependenciesData CreateBehaviourData(Type behaviourType)
        {
            var requiredTypes = behaviourType.GetCustomAttribute<RequireCharacterComponentAttribute>()?.Types ??
                                Array.Empty<Type>();
            var optionalTypes = behaviourType.GetCustomAttribute<OptionalCharacterComponentAttribute>()?.Types ??
                                Array.Empty<Type>();

            return new DependenciesData(requiredTypes, optionalTypes);
        }

        private void DrawComponentLabels(ICharacter parentCharacter, Type[] componentTypes, MessageType errorType)
        {
            foreach (var type in componentTypes)
                DrawComponentLabel(type, parentCharacter.GetCC(type) as UnityObject, errorType);
        }

        private void DrawComponentLabel(Type type, UnityObject component, MessageType messageType)
        {
            GUILayout.BeginHorizontal();
            
            string componentName = ObjectNames.NicifyVariableName(type.Name[1..].Replace("CC", ""));
            GUILayout.Label(componentName, EditorStyles.miniLabel);

            using (new BackgroundColorScope(GUIStyles.YellowColor))
            {
                if (component is Component unityComponent &&
                    GUILayout.Button("Ping", GUIStyles.Button, _pingOptions))
                {
                    EditorGUIUtility.PingObject(unityComponent);
                }
            }

            GUILayout.EndHorizontal();

            if (component != null)
                return;
            
            string errorLabel = messageType == MessageType.Error
                ? "Not found in the parent character, this behaviour will not function."
                : "Not found in the parent character, this behaviour might not behave properly.";

            EditorGUILayout.HelpBox(errorLabel, messageType);
            EditorGUILayout.Space(3f);
        }

        #region Internal Types

        private sealed class DependenciesData
        {
            public readonly Type[] OptionalComponents;

            public readonly Type[] RequiredComponents;

            public DependenciesData(Type[] required, Type[] optional)
            {
                RequiredComponents = required;
                OptionalComponents = optional;
            }

            public bool IsValid => RequiredComponents.Length > 0 || OptionalComponents.Length > 0;
        }

        #endregion
    }
}