namespace PolymindGames
{
    /// <summary>
    /// Interface for character components that handle field of view (FOV) adjustments.
    /// </summary>
    public interface IFOVHandlerCC : ICharacterComponent
    {
        /// <summary>
        /// Gets the current camera field of view.
        /// </summary>
        float CameraFOV { get; }

        /// <summary>
        /// Gets the current field of view for the view model.
        /// </summary>
        float ViewModelFOV { get; }

        /// <summary>
        /// Gets the current size of the view model.
        /// </summary>
        float ViewModelSize { get; }

        /// <summary>
        /// Sets the size of the view model.
        /// </summary>
        /// <param name="size">The size to set for the view model.</param>
        void SetViewModelSize(float size);

        /// <summary>
        /// Sets the camera field of view with optional animation parameters.
        /// </summary>
        /// <param name="fovMod">The modification to apply to the field of view.</param>
        /// <param name="duration">The duration of the animation in seconds (default is 0.3 seconds).</param>
        /// <param name="delay">The delay before starting the animation in seconds (default is 0 seconds).</param>
        void SetCameraFOV(float fovMod, float duration = 0.3f, float delay = 0f);

        /// <summary>
        /// Sets the view model field of view with optional animation parameters.
        /// </summary>
        /// <param name="fov">The field of view to set for the view model.</param>
        /// <param name="duration">The duration of the animation in seconds (default is 0.3 seconds).</param>
        /// <param name="delay">The delay before starting the animation in seconds (default is 0 seconds).</param>
        void SetViewModelFOV(float fov, float duration = 0.3f, float delay = 0f);
    }

}