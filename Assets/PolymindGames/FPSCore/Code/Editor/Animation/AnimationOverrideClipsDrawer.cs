using UnityEditorInternal;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    [CustomPropertyDrawer(typeof(AnimationOverrideClips))]
    public sealed class AnimationOverrideClipsDrawer : PropertyDrawer
    {
        private SerializedObject _serializedObject;
        private ReorderableList _recordClipList;
        private SerializedProperty _controller;
        private SerializedProperty _parameters;
        private SerializedProperty _clips;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            position.height = EditorGUIUtility.singleLineHeight;

            EditorGUI.BeginChangeCheck();
            EditorGUI.PropertyField(position, _controller);

            RuntimeAnimatorController controller = _controller.objectReferenceValue as RuntimeAnimatorController;

            if (EditorGUI.EndChangeCheck())
            {
                if (_controller.objectReferenceValue == null)
                    _clips.arraySize = 0;
                else
                    GetClipsFromController(controller);
            }

            if (controller != null && controller.animationClips.Length != _clips.arraySize)
                GetClipsFromController(controller);

            position.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            _recordClipList.DoList(EditorGUI.IndentedRect(position));

            if (!Application.isPlaying)
            {
                position.y += _recordClipList.GetHeight();
                EditorGUI.PropertyField(position, _parameters);
            }
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (_serializedObject != property.serializedObject)
            {
                Initialize(property);
                _serializedObject = property.serializedObject;
            }

            float clipListHeight = _recordClipList.GetHeight() + EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            float paramHeight = 0f;

            if (!Application.isPlaying)
            {
                int paramsChildCount = _parameters.Copy().CountInProperty();
                paramHeight = (paramsChildCount - 1) * 16 +
                              (paramsChildCount <= 2 && _parameters.isExpanded ? 42f : 8f);
            }

            return clipListHeight + paramHeight;
        }

        private void DrawClipElement(Rect rect, int index, bool selected, bool focused)
        {
            AnimationClip originalClip = (AnimationClip)_clips.GetArrayElementAtIndex(index).FindPropertyRelative("Original").objectReferenceValue;
            AnimationClip overrideClip = (AnimationClip)_clips.GetArrayElementAtIndex(index).FindPropertyRelative("Override").objectReferenceValue;

            rect.xMax /= 2.0f;
            GUI.Label(rect, originalClip.name, EditorStyles.label);
            rect.xMin = rect.xMax;
            rect.xMax *= 2.0f;

            EditorGUI.BeginChangeCheck();
            overrideClip = EditorGUI.ObjectField(rect, "", overrideClip, typeof(AnimationClip), false) as AnimationClip;

            if (EditorGUI.EndChangeCheck())
                _clips.GetArrayElementAtIndex(index).FindPropertyRelative("Override").objectReferenceValue = overrideClip;
        }

        private void SelectClip(ReorderableList list)
        {
            if (0 <= list.index && list.index < _clips.arraySize)
                EditorGUIUtility.PingObject(_clips.GetArrayElementAtIndex(list.index).FindPropertyRelative("Original").objectReferenceValue);
        }

        private static void DrawClipHeader(Rect rect)
        {
            rect.xMax /= 2.0f;
            GUI.Label(rect, "Original", EditorStyles.label);

            rect.xMin = rect.xMax + 14;
            rect.xMax *= 2.0f;
            GUI.Label(rect, "Override", EditorStyles.label);
        }

        private void GetClipsFromController(RuntimeAnimatorController controller)
        {
            var clips = controller.animationClips;

            _clips.arraySize = clips.Length;

            int i = 0;

            foreach (SerializedProperty clipPair in _clips)
            {
                clipPair.FindPropertyRelative("Original").objectReferenceValue = clips[i];
                i++;
            }
        }

        private void Initialize(SerializedProperty property)
        {
            _controller = property.FindPropertyRelative("_controller");
            _clips = property.FindPropertyRelative("_clips");
            _parameters = property.FindPropertyRelative("_defaultParameters");

            _recordClipList = new ReorderableList(property.serializedObject, _clips);
            _recordClipList.draggable = false;
            _recordClipList.displayAdd = _recordClipList.displayRemove = false;
            _recordClipList.drawElementCallback = DrawClipElement;
            _recordClipList.drawHeaderCallback = DrawClipHeader;
            _recordClipList.drawNoneElementCallback = DrawNoneElementCallback;
            _recordClipList.elementHeight = EditorGUIUtility.singleLineHeight;
            _recordClipList.onSelectCallback = SelectClip;
            _recordClipList.footerHeight = 0f;

            void DrawNoneElementCallback(Rect rect)
            {
                GUI.Label(rect, _controller.objectReferenceValue == null
                    ? "Assign an animator controller"
                    : "The animator controller has no clips");
            }
        }
    }
}