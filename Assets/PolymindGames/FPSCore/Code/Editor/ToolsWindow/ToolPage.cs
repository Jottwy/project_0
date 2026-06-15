using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System.Linq;
using System;

namespace PolymindGames.Editor
{
    using Object = UnityEngine.Object;

    /// <summary> 
    /// Represents a page in the editor tool system.
    /// Defines the contract for displaying and interacting with editor tool pages.
    /// </summary>
    public interface IEditorToolPage : IComparable<IEditorToolPage>
    {
        /// <summary>
        /// Gets the order of the page for sorting purposes.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Gets the display name of the page.
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Gets the description of the page, typically shown in the UI.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Refreshes the page's state or data.
        /// </summary>
        void Refresh();

        /// <summary>
        /// Disposes this page.
        /// </summary>
        void Dispose();

        /// <summary>
        /// Returns true if the page is focused.
        /// </summary>
        bool HasFocus();
        
        /// <summary>
        /// Sets the focus to this page.
        /// </summary>
        void SetFocus();

        /// <summary>
        /// Draws the content of the page in the editor.
        /// </summary>
        void DrawContent();

        /// <summary>
        /// Retrieves any sub-pages associated with this page.
        /// </summary>
        /// <returns>An enumerable collection of sub-pages.</returns>
        IEnumerable<IEditorToolPage> GetSubPages();

        /// <summary>
        /// Determines whether the page is compatible with a given Unity object.
        /// </summary>
        /// <param name="unityObject">The Unity object to check compatibility with.</param>
        /// <returns>True if compatible; otherwise, false.</returns>
        bool IsCompatibleWithObject(Object unityObject);
    }

    /// <summary>
    /// Represents the root page in the editor tool hierarchy.
    /// A base for pages that act as entry points for other tool pages.
    /// </summary>
    public abstract class RootToolPage : ToolPage
    { }

    /// <summary>
    /// Base implementation of an editor tool page, providing default behavior and utilities.
    /// </summary>
    public abstract class ToolPage : IEditorToolPage
    {
        /// <inheritdoc />
        public abstract string DisplayName { get; }

        /// <inheritdoc />
        public virtual int Order => 0;

        /// <inheritdoc />
        public virtual string Description => string.Empty;

        /// <inheritdoc />
        public abstract void DrawContent();

        /// <inheritdoc />
        public abstract bool IsCompatibleWithObject(Object unityObject);

        /// <inheritdoc />
        public virtual void Refresh() { }
        
        /// <inheritdoc />
        public virtual void Dispose() { }

        /// <inheritdoc />
        public virtual bool HasFocus() => false;

        /// <inheritdoc />
        public virtual void SetFocus() { }

        /// <inheritdoc />
        public virtual IEnumerable<IEditorToolPage> GetSubPages() => Array.Empty<IEditorToolPage>();

        /// <summary>
        /// Compares this page to another page based on their order values.
        /// </summary>
        /// <param name="other">The other page to compare with.</param>
        /// <returns>A value indicating the relative order of the pages.</returns>
        public int CompareTo(IEditorToolPage other) => Order.CompareTo(other?.Order ?? 0);

        /// <summary>
        /// Draws links to the provided pages, optionally including their sub-pages.
        /// </summary>
        /// <param name="pages">The collection of pages to render links for.</param>
        /// <param name="includeSubPages">Whether to include sub-pages in the rendering.</param>
        protected void DrawPageLinks(IEnumerable<IEditorToolPage> pages, bool includeSubPages = true)
        {
            foreach (var page in pages)
            {
                GUILayout.Space(2f);

                if (!string.IsNullOrEmpty(page.Description))
                    EditorGUILayout.HelpBox(page.Description, MessageType.Info);

                if (GUILayout.Button(page.DisplayName, GUIStyles.Button))
                    ToolsWindow.SelectPage(page);

                if (includeSubPages)
                {
                    var subPages = page.GetSubPages();
                    if (subPages.Any())
                    {
                        DrawPageLinks(page.GetSubPages());
                        GUILayout.Space(8f);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Represents a tool page for inspecting a specific Unity object type.
    /// </summary>
    public sealed class ObjectInspectorToolPage : IEditorToolPage
    {
        private readonly InspectorEditorWrapper _inspector;
        private readonly Func<Object> _objectProvider;
        private readonly Type _targetObjectType;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectInspectorToolPage"/> class.
        /// </summary>
        /// <param name="displayName">The name to display for this tool page.</param>
        /// <param name="objectType">The type of Unity object this page is associated with.</param>
        /// <param name="order">The order of this page for sorting purposes.</param>
        /// <param name="objectProvider">A function that provides the target Unity object to inspect.</param>
        /// <param name="description">An optional description of this tool page.</param>
        public ObjectInspectorToolPage(string displayName, Type objectType, int order, Func<Object> objectProvider, string description = null)
        {
            DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            _targetObjectType = objectType ?? throw new ArgumentNullException(nameof(objectType));
            _objectProvider = objectProvider ?? throw new ArgumentNullException(nameof(objectProvider));
            _inspector = new InspectorEditorWrapper();

            Order = order;
            Description = description;
        }

        /// <inheritdoc />
        public int Order { get; }

        /// <inheritdoc />
        public string DisplayName { get; }

        /// <inheritdoc />
        public string Description { get; }

        /// <inheritdoc />
        public void Refresh() { }

        /// <inheritdoc />
        public void Dispose() { }

        /// <inheritdoc />
        public bool HasFocus() => false;

        /// <inheritdoc />
        public void SetFocus() { }

        /// <inheritdoc />
        public void DrawContent()
        {
            if (!_inspector.HasTarget)
                _inspector.SetTarget(_objectProvider());

            GUILayout.Label(DisplayName, GUIStyles.Title);
            _inspector.Draw(EditorStyles.helpBox);
        }

        /// <inheritdoc />
        public IEnumerable<IEditorToolPage> GetSubPages() => Array.Empty<IEditorToolPage>();

        /// <inheritdoc />
        public bool IsCompatibleWithObject(Object unityObject)
        {
            return unityObject != null && unityObject.GetType() == _targetObjectType;
        }

        /// <inheritdoc />
        public int CompareTo(IEditorToolPage other)
        {
            return Order.CompareTo(other?.Order ?? 0);
        }
    }
}