using System.Collections.Generic;
using UnityEditor.PackageManager;
using UnityEditor.Build;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    /// <summary>
    /// Utility class for modifying project dependencies and settings.
    /// </summary>
    public static class ProjectModificationUtility
    {
        /// <summary>
        /// Modifies project dependencies by adding and removing specified dependencies.
        /// </summary>
        /// <param name="dependenciesToAdd">Array of dependencies to add.</param>
        /// <param name="dependenciesToRemove">Array of dependencies to remove.</param>
        public static void ModifyDependencies(string[] dependenciesToAdd, string[] dependenciesToRemove)
        {
            var addAndRemoveRequest = Client.AddAndRemove(dependenciesToAdd, dependenciesToRemove);

            // Wait for the add and remove request to complete
            while (!addAndRemoveRequest.IsCompleted)
            { }

            if (addAndRemoveRequest.IsCompleted)
            {
                // Check if the request was successful
                if (addAndRemoveRequest.Status != StatusCode.Success)
                    Debug.LogError("Failed to add or remove dependencies. Error: " + addAndRemoveRequest.Error.message);
            }
            else
            {
                Debug.LogError("Add and remove request timed out.");
            }
        }
        
        /// <summary>
        /// Adds or removes a scripting define symbol for the selected or specified build target group.
        /// </summary>
        /// <param name="symbol">The define symbol to add or remove.</param>
        /// <param name="add">True to add the symbol, false to remove it.</param>
        /// <param name="targetGroup">Optional override of the target group. Defaults to currently selected build target group.</param>
        public static void ModifyDefineSymbol(string symbol, bool add, BuildTargetGroup? targetGroup = null)
        {
            BuildTargetGroup group = targetGroup ?? EditorUserBuildSettings.selectedBuildTargetGroup;

            string currentDefines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(group));
            var defineSet = new HashSet<string>(currentDefines.Split(';'));

            if (add)
            {
                if (defineSet.Add(symbol))
                {
                    ApplyDefines(group, defineSet);
                    Debug.Log($"Added scripting define: {symbol}");
                }
            }
            else
            {
                if (defineSet.Remove(symbol))
                {
                    ApplyDefines(group, defineSet);
                    Debug.Log($"Removed scripting define: {symbol}");
                }
            }
        }

        private static void ApplyDefines(BuildTargetGroup group, HashSet<string> defines)
        {
            string newDefineString = string.Join(";", defines);
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(group), newDefineString);
        }
    }
}