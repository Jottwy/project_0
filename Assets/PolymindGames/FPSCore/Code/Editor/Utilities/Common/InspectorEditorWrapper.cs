using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.Editor
{
    using Object = UnityEngine.Object;
    using Editor = UnityEditor.Editor;

    public sealed class InspectorEditorWrapper : IDisposable
    {
        private Vector2 _scrollPosition;
        private Editor _cachedEditor;
        private bool _isDisposed;
        private bool _hasTarget;

        /// <summary>
        /// Initializes a new instance of the <see cref="InspectorEditorWrapper"/> class.
        /// </summary>
        public InspectorEditorWrapper(params Object[] targetObjects)
        {
            SetTargets(targetObjects);
        }
        
        ~InspectorEditorWrapper()
        {
            Dispose();
        }

        public Object[] Targets => _hasTarget && _cachedEditor != null ? _cachedEditor.targets : Array.Empty<Object>();
        public Object Target => _hasTarget && _cachedEditor != null ? _cachedEditor.target : null;
        public bool HasTarget => _hasTarget && _cachedEditor != null && _cachedEditor.targets.Length > 0;

        /// <summary>
        /// Sets target object to display in the inspector.
        /// </summary>
        public void SetTarget(Object targetObject)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(InspectorEditorWrapper));
            
            if (_cachedEditor != null && _cachedEditor.target == targetObject)
                return;

            _hasTarget = targetObject != null;
            Editor.CreateCachedEditor(targetObject, null, ref _cachedEditor);
        }

        /// <summary>
        /// Sets multiple target objects to display in the inspector.
        /// </summary>
        /// <param name="targetObjects">An array of Unity objects to set as targets.</param>
        public void SetTargets(params Object[] targetObjects)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(InspectorEditorWrapper));

            if (_cachedEditor != null && _cachedEditor.targets == targetObjects)
                return;

            _hasTarget = targetObjects != null && targetObjects.Length > 0;
            Editor.CreateCachedEditor(targetObjects, null, ref _cachedEditor);
        }
        
        /// <summary>
        /// Draws the target object's inspector in a scrollable view.
        /// </summary>
        /// <param name="style">Optional GUI style for the scroll view.</param>
        /// <param name="options">Layout options for the scroll view.</param>
        public void Draw(GUIStyle style = null, params GUILayoutOption[] options)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(InspectorEditorWrapper));

            style ??= GUIStyle.none;

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, style, options);
            if (_hasTarget && _cachedEditor != null)
            {
                using (new EditorGUI.DisabledScope(!_cachedEditor.target))
                {
                    _cachedEditor.OnInspectorGUI();
                }
            }
            GUILayout.EndScrollView();
        }

        /// <summary>
        /// Releases resources and destroys the cached editor.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            if (_cachedEditor != null)
            {
                Object.DestroyImmediate(_cachedEditor);
                _cachedEditor = null;
            }

            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }

    public sealed class InspectorPanelDrawer : IDrawablePanel
    {
        private readonly InspectorEditorWrapper _inspectorWrapper;
        private readonly string _displayName;

        /// <summary>
        /// Initializes a new instance of the <see cref="InspectorPanelDrawer"/> class.
        /// </summary>
        /// <param name="targetObject">The Unity object to display in the inspector panel.</param>
        public InspectorPanelDrawer(Object targetObject)
        {
            if (targetObject == null)
                throw new ArgumentNullException(nameof(targetObject), "Target object cannot be null.");

            _inspectorWrapper = new InspectorEditorWrapper(targetObject);
            _displayName = ObjectNames.NicifyVariableName(targetObject.GetType().Name);
        }
        
        public void Draw(Rect rect)
        {
            GUILayout.Label(_displayName, GUIStyles.Title);
            _inspectorWrapper.Draw(EditorStyles.helpBox);
        }
    }
}