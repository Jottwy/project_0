using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames
{
    public abstract class DataDefinition<T> : DataDefinition where T : DataDefinition<T>
    {
        [SerializeField, Disable]
        [Tooltip("Unique id (auto generated).")]
        private int _id = -1;

        [NonSerialized]
        private string _cachedName;

        private static Dictionary<int, T> _definitionsById;
        private static T[] _definitions;

        public sealed override int Id => _id;

        public override string Name 
        {
            get => _cachedName ??= name.RemovePrefix();
            set
            {
                if (name != value) //_cachedName.AsSpan() != value.AsSpan().RemovePrefix())
                {
                    name = value;
                    _cachedName = value.RemovePrefix();
                }
            }
        }

        public static bool Initialized => _definitions != null;
        public static T[] Definitions => _definitions ??= LoadDefinitionsFromResources();
        private static Dictionary<int, T> DefinitionsById => _definitionsById ??= InitializeDefinitionsById();

        #region Access Methods
        /// <summary>
        /// Tries to return a definition with the given id.
        /// </summary>
        public static bool TryGetWithId(int id, out T def) => DefinitionsById.TryGetValue(id, out def);

        /// <summary>
        /// Returns a definition with the given id.
        /// </summary>
        public static T GetWithId(int id) => DefinitionsById.GetValueOrDefault(id);

        /// <summary>
        /// <para>Tries to return a definition with the given name.</para>
        /// Note: For much better performance use the "GetWithId" methods instead.
        /// </summary>
        public static bool TryGetWithName(string defName, out T def)
        {
            if (string.IsNullOrEmpty(defName))
            {
                def = null;
                return false;
            }

            int nameHash = defName.GetHashCode();
            int definitionsCount = Definitions.Length;

            for (int i = 0; i < definitionsCount; i++)
            {
                if (_definitions[i].Name.GetHashCode() == nameHash)
                {
                    def = _definitions[i];
                    return true;
                }
            }

            def = null;
            return false;
        }

        /// <summary>
        /// <para>Returns a definition with the given name.</para>
        /// Note: For much better performance use the "GetWithId" methods instead.
        /// </summary>
        public static T GetWithName(string defName)
        {
            foreach (T definition in Definitions)
            {
                if (definition.Name == defName)
                    return definition;
            }

            return null;
        }

        /// <summary>
        /// <para>Returns a definition that matches the provided filter criteria.</para>
        /// </summary>
        /// <param name="filter">A function to filter definitions based on custom criteria.</param>
        /// <returns>The first definition that matches the filter criteria, or null if none match.</returns>
        public static T Find(Func<T, bool> filter)
        {
            foreach (T definition in Definitions)
            {
                if (filter(definition))
                    return definition;
            }

            return null;
        }

        /// <summary>
        /// <para>Returns a list of all definitions that match the provided filter criteria.</para>
        /// </summary>
        /// <param name="filter">A function to filter definitions based on custom criteria.</param>
        /// <returns>A list of definitions that match the filter criteria.</returns>
        public static List<T> FindAll(Func<T, bool> filter)
        {
            var list = new List<T>();

            foreach (T definition in Definitions)
            {
                if (filter(definition))
                    list.Add(definition);
            }
            
            return list;
        }
        #endregion

        #region Definition Loading
        private static T[] LoadDefinitionsFromResources()
        {
            string path = $"Definitions/{typeof(T).Name.Replace("Definition", "")}";
            var definitions = Resources.LoadAll<T>(path);
            
            Array.Sort(definitions, (a, b) => string.Compare(a.FullName, b.FullName, StringComparison.InvariantCultureIgnoreCase));

            if (definitions.Length > 0)
                return definitions;

            string errorStr = $"Failed to load definitions of type '{typeof(T).Name}': No Resources folder found at the specified path '{path}'. Please ensure the path is correct and the folder exists.";

            if (Application.isPlaying)
            {
                Debug.LogError(errorStr);
            }
            else
            {
                Debug.LogWarning(errorStr);
            }

            return Array.Empty<T>();
        }

        private static Dictionary<int, T> InitializeDefinitionsById()
        {
            var definitionsById = new Dictionary<int, T>(Definitions.Length);

            foreach (var def in _definitions)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    if (def.Id == -1 || definitionsById.ContainsKey(def.Id))
                        def.AssignID();
                }
#endif

                try
                {
                    definitionsById.Add(def.Id, def);
                }
                catch
                {
                    Debug.LogError($"Multiple '{nameof(T)}' of the same id are found. Restarting the editor should fix this problem.");
                }
            }

            return definitionsById;
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        public static void AddDefinition_EditorOnly(T definition)
        {
            UnityEditor.ArrayUtility.Add(ref _definitions, definition);
            _definitionsById?.Add(definition.Id, definition);
        }

        public static void RemoveDefinition_EditorOnly(T definition)
        {
            UnityEditor.ArrayUtility.Remove(ref _definitions, definition);
            _definitionsById?.Remove(definition.Id);
        }

        public static void ReloadDefinitions_EditorOnly()
        {
            _definitions = LoadDefinitionsFromResources();
            _definitionsById = null;
        }

        public override void Validate_EditorOnly(in ValidationContext validationContext)
        {
            Name = name;
            if (validationContext.Trigger is ValidationTrigger.Created or ValidationTrigger.Duplicated)
                AssignID();
        }

        private void OnValidate()
        {
            var evt = Event.current;
            if (evt is { type: EventType.Used })
            {
                if (evt.commandName is "Duplicate" or "Paste")
                    Validate_EditorOnly(new ValidationContext(false, ValidationTrigger.Duplicated));
            }
            else
            {
                Validate_EditorOnly(new ValidationContext(false, ValidationTrigger.Refresh));
            }
        }

        private void Reset()
        {
            Validate_EditorOnly(new ValidationContext(false, ValidationTrigger.Created));
        }

        /// <summary>
        /// Generates and assigns a unique id to this definition.
        /// </summary>
        private void AssignID()
        {
            const int MaxAssignmentTries = 10;

            int assignmentTries = 0;
            while (assignmentTries < MaxAssignmentTries)
            {
                int assignedId = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
                assignmentTries++;

                // If no other definition uses this id assign it.
                if (!Find(def => def.Id == assignedId))
                {
                    _id = assignedId;
                    UnityEditor.EditorUtility.SetDirty(this);
                    return;
                }
            }

            Debug.LogError($"Couldn't generate an unique id for definition: {Name}");
        }
#endif
        #endregion
    }

}