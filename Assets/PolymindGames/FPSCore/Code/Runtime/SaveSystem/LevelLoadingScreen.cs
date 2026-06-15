using System.Collections;
using UnityEngine;

namespace PolymindGames.SaveSystem
{
    /// <summary>
    /// Interface for defining the behavior of a level loading screen, including handling loading start, progress, and completion.
    /// </summary>
    public interface ILevelLoadingScreen
    {
        /// <summary>
        /// Called at the start of a level load. Can be used to display loading UI.
        /// </summary>
        /// <param name="sceneName">The level definition being loaded.</param>
        void OnLevelLoadStart(string sceneName);

        /// <summary>
        /// Updates the loading progress. Can be used to update a progress bar or similar UI.
        /// </summary>
        /// <param name="progress">The progress of the level load, typically between 0 and 1.</param>
        void OnLoadProgressChanged(float progress);

        /// <summary>
        /// Called when the level load is complete. Can be used to hide loading UI.
        /// </summary>
        /// <returns>An enumerator for handling asynchronous operations.</returns>
        IEnumerator OnLevelLoadComplete();
    }

    /// <summary>
    /// Base class for managing the level loading screen, ensuring a single instance and handling loading start, progress, and completion.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    public abstract class LevelLoadingScreen : MonoSingleton<LevelLoadingScreen>, ILevelLoadingScreen
    {
        /// <inheritdoc/>
        public abstract void OnLevelLoadStart(string sceneName);

        /// <inheritdoc/>
        public abstract void OnLoadProgressChanged(float progress);

        /// <inheritdoc/>
        public abstract IEnumerator OnLevelLoadComplete();
    }
    
    /// <summary>
    /// Provides a default implementation of the ILevelLoadingScreen interface with no-op methods,
    /// serving as a placeholder when no custom loading screen behavior is needed.
    /// </summary>
    public sealed class DefaultLevelLoadingScreen : ILevelLoadingScreen
    {
        /// <inheritdoc/>
        public void OnLevelLoadStart(string sceneName) { }

        /// <inheritdoc/>
        public void OnLoadProgressChanged(float progress) { }

        /// <inheritdoc/>
        public IEnumerator OnLevelLoadComplete() { yield break; }
    }
}