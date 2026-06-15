using System.Collections.Generic;
using UnityEngine;
using System.Text;
using System;

namespace PolymindGames
{
    using Object = UnityEngine.Object;

    /// <summary>
    /// Extension methods for Unity types.
    /// </summary>
    public static class UnityExtensions
    {
        public static void PlayClip(this Animation animation, AnimationClip clip)
        {
            animation.Stop();
            animation.clip = clip;
            animation.Play();
        }

        /// <summary>
        /// Checks if a component belongs to a prefab.
        /// </summary>
        /// <param name="component">The component to check.</param>
        /// <returns>True if the component belongs to a prefab, otherwise false.</returns>
        public static bool IsPrefab(this Component component) => !component.gameObject.scene.IsValid();

        /// <summary>
        /// Checks if a game object is a prefab.
        /// </summary>
        /// <param name="gameObject">The game object to check.</param>
        /// <returns>True if the game object is a prefab, otherwise false.</returns>
        public static bool IsPrefab(this GameObject gameObject) => !gameObject.scene.IsValid();

        /// <summary>
        /// Sets the layer of the specified game object and all of its children recursively.
        /// </summary>
        /// <param name="gameObject">The game object to set the layers of.</param>
        /// <param name="layer">The layer to set.</param>
        public static void SetLayersInChildren(this GameObject gameObject, int layer)
        {
            gameObject.layer = layer;
            foreach (Transform child in gameObject.transform)
                child.gameObject.SetLayersInChildren(layer);
        }
        
        /// <summary>
        /// Checks if the game object's layer is included in the specified layer mask.
        /// </summary>
        /// <param name="gameObject">The game object to check.</param>
        /// <param name="layerMask">The layer mask to check against.</param>
        /// <returns>True if the game object's layer is included in the layer mask; otherwise, false.</returns>
        public static bool IsLayerInMask(this GameObject gameObject, LayerMask layerMask)
        {
            // Get the game object's layer
            int objectLayer = gameObject.layer;

            // Check if the layer is included in the layer mask
            return layerMask == (layerMask | (1 << objectLayer));
        }
        
        /// <summary>
        /// Finds a component of type <typeparamref name="T"/> in the children of the game object with the specified name.
        /// </summary>
        /// <typeparam name="T">The type of the component to find.</typeparam>
        /// <param name="parent">The parent game object.</param>
        /// <param name="childName">The name of the child game object.</param>
        /// <returns>The component of type <typeparamref name="T"/> if found; otherwise, null.</returns>
        public static T GetComponentInChildrenByName<T>(this Transform parent, string childName) where T : Component
        {
            // Find the child transform with the specified name
            Transform childTransform = parent.transform.Find(childName);

            // If the child transform is found, try to get the component of type T
            if (childTransform != null)
            {
                return childTransform.GetComponent<T>();
            }

            // If the child transform or component is not found, return null
            return null;
        }
        
        /// <summary>
        /// Calculates the transform path from a given root transform to a target transform using a provided StringBuilder.
        /// </summary>
        /// <param name="root">The root transform.</param>
        /// <param name="target">The target transform.</param>
        /// <param name="pathBuilder">The StringBuilder to use for constructing the path.</param>
        /// <returns>The calculated transform path.</returns>
        public static string CalculateTransformPath(this Transform root, Transform target, StringBuilder pathBuilder)
        {
            pathBuilder.Clear();

            if (target == root)
                return string.Empty;

            pathBuilder.Append(target.name);
            Transform parent = target.parent;

            while (parent != null && parent != root)
            {
                pathBuilder.Insert(0, parent.name + "/");
                parent = parent.parent;
            }

            return pathBuilder.ToString();
        }

        /// <summary>
        /// Gets a component of type T from the root of the specified game object's hierarchy.
        /// </summary>
        /// <typeparam name="T">The type of component to retrieve.</typeparam>
        /// <param name="gameObj">The game object whose root to search in.</param>
        /// <returns>A component of type T found in the root of the hierarchy, or null if none is found.</returns>
        public static T GetComponentInRoot<T>(this GameObject gameObj)
        {
            return gameObj.transform.root.GetComponentInChildren<T>();
        }

        /// <summary>
        /// Gets or adds a component of the specified type to the specified game object.
        /// </summary>
        /// <param name="gameObj">The game object to get or add the component to.</param>
        /// <param name="type">The type of component to get or add.</param>
        /// <returns>The component of the specified type attached to the game object, or a new component if none is found.</returns>
        public static Component GetOrAddComponent(this GameObject gameObj, Type type)
        {
            return gameObj.TryGetComponent(type, out var comp) ? comp : gameObj.AddComponent(type);
        }

        /// <summary>
        /// Gets a component of the specified base type from the specified game object, or adds or swaps it with the specified target type.
        /// </summary>
        /// <typeparam name="BaseType">The base type of component to retrieve or add.</typeparam>
        /// <param name="gameObj">The game object to get, add, or swap the component on.</param>
        /// <param name="targetType">The type of component to retrieve, add, or swap.</param>
        /// <returns>
        /// The component of the specified type attached to the game object if found, otherwise a new component is added or swapped.
        /// </returns>
        public static BaseType GetAddOrSwapComponent<BaseType>(this GameObject gameObj, Type targetType) where BaseType : Component
        {
            if (gameObj.TryGetComponent(out BaseType comp))
            {
                if (comp.GetType() != targetType)
                {
#if UNITY_EDITOR
                    if (Application.isPlaying)
                        Object.Destroy(comp);
                    else
                        Object.DestroyImmediate(comp);
#else
                    Object.Destroy(comp);
#endif
                }
                else
                {
                    return comp;
                }
            }

            return gameObj.AddComponent(targetType) as BaseType;
        }


        /// <summary>
        /// Gets or adds a component of type T to the specified game object.
        /// </summary>
        /// <typeparam name="T">The type of component to get or add.</typeparam>
        /// <param name="gameObj">The game object to get or add the component to.</param>
        /// <returns>The component of type T attached to the game object, or a new component if none is found.</returns>
        public static T GetOrAddComponent<T>(this GameObject gameObj) where T : Component
        {
            return gameObj.TryGetComponent(out T comp) ? comp : gameObj.AddComponent<T>();
        }

        /// <summary>
        /// Gets a component of type <typeparamref name="BaseType"/> from the specified game object,
        /// or adds a component of type <typeparamref name="TypeToAdd"/> if none is found.
        /// </summary>
        /// <typeparam name="BaseType">The base type of component to get or add.</typeparam>
        /// <typeparam name="TypeToAdd">The derived type of component to add if the base type is not found.</typeparam>
        /// <param name="gameObj">The game object to get or add the component to.</param>
        /// <returns>The component of type <typeparamref name="BaseType"/> attached to the game object, or a new component of type <typeparamref name="TypeToAdd"/> if none is found.</returns>
        public static BaseType GetOrAddDerivedComponent<BaseType, TypeToAdd>(this GameObject gameObj)
            where BaseType : Component
            where TypeToAdd : BaseType
        {
            return gameObj.TryGetComponent(out BaseType comp) ? comp : gameObj.AddComponent<TypeToAdd>();
        }

        /// <summary>
        /// Gets a component of the specified base type from the specified game object,
        /// or adds a component of the specified derived type if none is found or if the existing component is of a different type.
        /// </summary>
        /// <typeparam name="BaseType">The base type of component to retrieve or add.</typeparam>
        /// <param name="gameObj">The game object to get or add the component to.</param>
        /// <param name="derivedType">The type of component to add if the base type is not found or is of a different type.</param>
        /// <returns>
        /// The component of the specified base type attached to the game object if found, otherwise a new component of the specified derived type.
        /// </returns>
        public static BaseType GetOrAddDerivedComponent<BaseType>(this GameObject gameObj, Type derivedType) where BaseType : Component
        {
            if (!typeof(BaseType).IsAssignableFrom(derivedType))
            {
                Debug.LogError($"The type {derivedType} is not a subclass of {typeof(BaseType)}.");
                return null;
            }

            if (gameObj.TryGetComponent(out BaseType comp))
            {
                if (comp.GetType() != derivedType)
                {
#if UNITY_EDITOR
                    if (Application.isPlaying)
                        Object.Destroy(comp);
                    else
                        Object.DestroyImmediate(comp);
#else
                    Object.Destroy(comp);
#endif
                    comp = gameObj.AddComponent(derivedType) as BaseType;
                }
            }
            else
            {
                comp = gameObj.AddComponent(derivedType) as BaseType;
            }

            return comp;
        }

        /// <summary>
        /// Checks if the specified game object has a component of type T attached.
        /// </summary>
        /// <typeparam name="T">The type of component to check for.</typeparam>
        /// <param name="gameObject">The game object to check.</param>
        /// <returns>True if the game object has a component of type T attached, otherwise false.</returns>
        public static bool HasComponent<T>(this GameObject gameObject)
        {
            return gameObject.TryGetComponent<T>(out _);
        }

        /// <summary>
        /// Gets the first component of type T found in the immediate children of the specified game object.
        /// </summary>
        /// <typeparam name="T">The type of component to retrieve.</typeparam>
        /// <param name="gameObj">The game object to search in.</param>
        /// <returns>The first component of type T found in the immediate children, or null if none is found.</returns>
        public static T GetComponentInFirstChildren<T>(this GameObject gameObj) where T : class
        {
            var transform = gameObj.transform;
            int childCount = transform.childCount;
            for (int i = 0; i < childCount; i++)
            {
                if (transform.GetChild(i).TryGetComponent<T>(out var comp))
                    return comp;
            }

            return null;
        }

        /// <summary>
        /// Gets components of type T from the immediate children of the specified game object.
        /// </summary>
        /// <typeparam name="T">The type of components to retrieve.</typeparam>
        /// <param name="gameObj">The game object to search in.</param>
        /// <param name="multipleOnSameObject">Indicates whether to allow multiple components of type T on the same object.</param>
        /// <param name="capacity"></param>
        /// <returns>A list of components of type T found in the immediate children.</returns>
        public static List<T> GetComponentsInFirstChildren<T>(this GameObject gameObj, bool multipleOnSameObject = false, int capacity = 4)
        {
            var list = new List<T>(capacity);
            GetComponentsInFirstChildren(gameObj.transform, list, multipleOnSameObject);
            return list;
        }

        /// <summary>
        /// Gets components of type T from the immediate children of the specified transform.
        /// </summary>
        /// <typeparam name="T">The type of components to retrieve.</typeparam>
        /// <param name="transform">The transform whose children to search in.</param>
        /// <param name="list">The list to which the found components will be added.</param>
        /// <param name="multipleOnSameObject">Indicates whether to allow multiple components of type T on the same object.</param>
        public static void GetComponentsInFirstChildren<T>(this Transform transform, List<T> list, bool multipleOnSameObject = false)
        {
            list.Clear();
            int childCount = transform.childCount;
            if (multipleOnSameObject)
            {
                for (int i = 0; i < childCount; i++)
                    list.AddRange(transform.GetChild(i).GetComponents<T>());
            }
            else
            {
                for (int i = 0; i < childCount; i++)
                {
                    if (transform.GetChild(i).TryGetComponent(out T component))
                        list.Add(component);
                }
            }
        }
    }

}