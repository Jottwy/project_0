using UnityEngine;
using System.Text;
using System;

namespace PolymindGames.SaveSystem
{
    /// <summary>
    /// Represents serialized data of a component, including the game object path, component type, and serialized data.
    /// </summary>
    [Serializable]
    public struct SerializedComponentData
    {
        /// <summary>
        /// The path of the game object containing the component.
        /// </summary>
        public string GameObjectPath;

        /// <summary>
        /// The type of the component.
        /// </summary>
        public Type ComponentType;

        /// <summary>
        /// The serialized data of the component.
        /// </summary>
        public object Data;

        /// <summary>
        /// Constructs SerializedComponentData from given parameters.
        /// </summary>
        /// <param name="gameObjectPath">The path of the game object containing the component.</param>
        /// <param name="componentType">The type of the component.</param>
        /// <param name="data">The serialized data of the component.</param>
        public SerializedComponentData(string gameObjectPath, Type componentType, object data)
        {
            GameObjectPath = gameObjectPath;
            ComponentType = componentType;
            Data = data;
        }

        /// <summary>
        /// Extracts SerializedComponentData from a given root transform.
        /// </summary>
        /// <param name="root">The root transform to extract data from.</param>
        /// <param name="pathBuilder">The StringBuilder to use for constructing the path.</param>
        /// <returns>An array of SerializedComponentData extracted from the root transform.</returns>
        public static SerializedComponentData[] ExtractFromObject(Transform root, StringBuilder pathBuilder)
        {
            var savComponents = root.GetComponentsInChildren<ISaveableComponent>();

            if (savComponents.Length == 0)
                return null;

            var data = new SerializedComponentData[savComponents.Length];
            for (int i = 0; i < savComponents.Length; i++)
            {
                ISaveableComponent savComponent = savComponents[i];
                string compPath = root.CalculateTransformPath(savComponent.transform, pathBuilder);
                data[i] = new SerializedComponentData(compPath, savComponent.GetType(), savComponent.SaveMembers());
            }
            return data;
        }

        /// <summary>
        /// Applies SerializedComponentData to a given root transform.
        /// </summary>
        /// <param name="root">The root transform to apply data to.</param>
        /// <param name="data">The SerializedComponentData to apply.</param>
        public static void ApplyToObject(Transform root, ReadOnlySpan<SerializedComponentData> data)
        {
            if (data == null)
                return;

            string rootName = root.gameObject.name;
            foreach (var compData in data)
            {
                var compTrs = compData.GameObjectPath != rootName
                    ? root.Find(compData.GameObjectPath)
                    : root;

                if (compTrs == null)
                    continue;
                
                var component = (ISaveableComponent)compTrs.gameObject.GetOrAddComponent(compData.ComponentType);
                component.LoadMembers(compData.Data);
            }
        }
    }
}