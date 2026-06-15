using System.Collections.Generic;
using UnityEngine;
using System;

namespace PolymindGames
{
    using UnityObject = UnityEngine.Object;
    using Random = UnityEngine.Random;

    /// <summary>
    /// Provides extension methods for collections.
    /// </summary>
    public static class CollectionExtensions
    {
        /// <summary>
        /// Checks if the specified array contains any of the elements from another array.
        /// </summary>
        public static bool Contains<T>(this IReadOnlyList<T> list, T element)
            => IndexOf(list, element) != -1;

        /// <summary>
        /// Finds the index of the first occurrence of a specified value in the list.
        /// </summary>
        public static int IndexOf<T>(this IReadOnlyList<T> list, T item)
        {
            if (list is T[] array)
                return Array.IndexOf(array, item);

            if (list is List<T> tList)
                return tList.IndexOf(item);

            for (int i = 0; i < list.Count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(list[i], item))
                    return i;
            }

            return -1;
        }
        
        /// <summary>
        /// Finds the index of the first item in the list that satisfies the specified predicate.
        /// </summary>
        public static int FindIndex<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (predicate(list[i]))
                    return i;
            }

            return -1;
        }
        
        /// <summary>
        /// Selects an item from the list based on the specified selection method.
        /// </summary>
        /// <typeparam name="T">The type of items in the list.</typeparam>
        /// <param name="list">The list to select from.</param>
        /// <param name="lastIndex">The index of the last selected item.</param>
        /// <param name="selectionMethod">The method to use for selection.</param>
        /// <returns>The selected item.</returns>
        public static T Select<T>(this IReadOnlyList<T> list, ref int lastIndex, SelectionType selectionMethod = SelectionType.RandomExcludeLast)
        {
            return selectionMethod switch
            {
                SelectionType.Random => SelectRandom(list),
                SelectionType.RandomExcludeLast => SelectRandomExcludeLast(list, ref lastIndex),
                SelectionType.Sequence => SelectSequence(list, ref lastIndex),
                _ => default(T)
            };
        }

        /// <summary>
        /// Searches for an element in the array that matches the predicate, prioritizing the specified range first.
        /// </summary>
        /// <typeparam name="T">The type of elements in the array.</typeparam>
        /// <param name="array">The array to search.</param>
        /// <param name="predicate">The predicate to match.</param>
        /// <param name="startIndex">The starting index of the range to prioritize.</param>
        /// <param name="endIndex">The ending index of the range to prioritize.</param>
        /// <returns>The first matching element if found, or the default value of T if not found.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the array or predicate is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the range is invalid.</exception>
        public static T FindWithPriorityRange<T>(this T[] array, Func<T, bool> predicate, int startIndex, int endIndex)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));
            if (startIndex < 0 || startIndex >= array.Length)
                throw new ArgumentOutOfRangeException(nameof(startIndex));
            if (endIndex < startIndex || endIndex >= array.Length)
                throw new ArgumentOutOfRangeException(nameof(endIndex));

            // Search within the range first
            for (int i = startIndex; i <= endIndex; i++)
            {
                if (predicate(array[i]))
                    return array[i];
            }

            // Search the rest of the array
            for (int i = 0; i < array.Length; i++)
            {
                // Skip the prioritized range
                if (i >= startIndex && i <= endIndex)
                    continue;

                if (predicate(array[i]))
                    return array[i];
            }

            return default(T);
        }

        public static T SelectRandomFiltered<T>(this T[] array, Func<T, bool> filter) where T : class
        {
            const int MaxStackAlloc = 64;

            // Use stackalloc to store valid indexes if the array size is small enough
            Span<int> validIndexes = array.Length <= MaxStackAlloc
                ? stackalloc int[array.Length]
                : new int[array.Length];

            int validCount = 0;

            // Collect the indexes of all valid elements
            for (int i = 0; i < array.Length; i++)
            {
                if (filter(array[i]))
                {
                    validIndexes[validCount++] = i;
                }
            }

            // Check if there are any valid elements
            if (validCount == 0)
            {
                return default(T);
            }

            // Select a random index from the valid indexes
            int randomIndex = Random.Range(0, validCount);
            return array[validIndexes[randomIndex]];
        }

        /// <summary>
        /// Selects a random item from the list.
        /// </summary>
        /// <typeparam name="T">The type of items in the list.</typeparam>
        /// <param name="list">The list to select from.</param>
        /// <returns>The selected item, or the default value of type T if the list is empty.</returns>
        public static T SelectRandom<T>(this IReadOnlyList<T> list)
        {
            if (list == null || list.Count == 0)
                return default(T);

            return list[Random.Range(0, list.Count)];
        }

        /// <summary>
        /// Selects the next item in the list sequentially, looping back to the beginning if necessary.
        /// </summary>
        /// <typeparam name="T">The type of items in the list.</typeparam>
        /// <param name="list">The list to select from.</param>
        /// <param name="lastIndex">The index of the last selected item.</param>
        /// <returns>The selected item, or the default value of type T if the list is empty.</returns>
        public static T SelectSequence<T>(this IReadOnlyList<T> list, ref int lastIndex)
        {
            if (list == null || list.Count == 0)
                return default(T);

            lastIndex = (int)Mathf.Repeat(lastIndex + 1, list.Count);
            return list[lastIndex];
        }

        /// <summary>
        /// Selects a random item from the list, excluding the last selected item.
        /// </summary>
        /// <typeparam name="T">The type of items in the list.</typeparam>
        /// <param name="list">The list to select from.</param>
        /// <param name="lastIndex">The index of the last selected item.</param>
        /// <returns>The selected item, or the default value of type T if the list is empty or contains only one item.</returns>
        public static T SelectRandomExcludeLast<T>(this IReadOnlyList<T> list, ref int lastIndex)
        {
            if (list == null || list.Count == 0)
                return default(T);

            if (list.Count == 1)
                return list[0];

            int newIndex;
            do
            {
                newIndex = Random.Range(0, list.Count);
            } while (newIndex == lastIndex);

            lastIndex = newIndex;
            return list[newIndex];
        }

        public static void RemoveDuplicates<T>(ref T[] array) where T : UnityObject
        {
            if (array == null)
                return;

            int index = 0;
            while (index < array.Length)
            {
                int itemCount = 0;
                int indexOfDuplicate = -1;
                for (int i = 0; i < array.Length; i++)
                {
                    if (array[index] == array[i])
                    {
                        itemCount++;
                        indexOfDuplicate = i;
                    }
                }

                if (array[indexOfDuplicate] != null && itemCount == 2)
                {
                    array[indexOfDuplicate] = null;
                    index++;
                    continue;
                }

                if (itemCount > 1)
                {
                    RemoveAtIndex(ref array, indexOfDuplicate);
                    continue;
                }

                index++;
            }
        }

        private static void RemoveAtIndex<T>(ref T[] array, int index) where T : UnityObject
        {
            var newArray = new T[array.Length - 1];

            for (int i = 0; i < index; i++)
                newArray[i] = array[i];

            for (int j = index; j < newArray.Length; j++)
                newArray[j] = array[j + 1];

            array = newArray;
        }
    }

    public enum SelectionType
    {
        /// <summary>The item will be selected randomly.</summary>
        Random,

        /// <summary>The item will be selected randomly, but will exclude the last selected.</summary>
        RandomExcludeLast,

        /// <summary>The items will be selected in sequence.</summary>
        Sequence
    }
}