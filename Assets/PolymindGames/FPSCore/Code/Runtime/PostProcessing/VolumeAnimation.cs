using System.Collections.Generic;
using System;

#if POLYMIND_GAMES_FPS_HDRP || POLYMIND_GAMES_FPS_URP
using VolComponent = UnityEngine.Rendering.VolumeComponent;
using VolProfile = UnityEngine.Rendering.VolumeProfile;
#else
using VolComponent = UnityEngine.Rendering.PostProcessing.PostProcessEffectSettings;
using VolProfile = UnityEngine.Rendering.PostProcessing.PostProcessProfile;
#endif

namespace PolymindGames.PostProcessing
{
    [Serializable]
    public abstract class VolumeAnimation
    {
        public abstract void SetProfile(VolProfile profile);
        public abstract void Dispose(VolProfile profile);
        public abstract void Animate(float t);
    }
    
    /// <summary>
    /// Abstract class representing a volume animation for a specific volume component type
    /// </summary>
    [Serializable]
    public abstract class VolumeAnimation<T> : VolumeAnimation where T : VolComponent
    {
        #region Internal Types
        protected sealed class VolumeParameterAnimationCollection
        {
            private readonly List<VolumeParameterAnimation> _list;

            public VolumeParameterAnimationCollection()
            {
                _list = new List<VolumeParameterAnimation>(1);
            }

            public int Count => _list.Count;

            public VolumeParameterAnimation this[int index] => _list[index];

            public void Add(VolumeParameterAnimation parameter)
            {
                if (CanAdd(parameter))
                    _list.Add(parameter);
            }
        
            public void Clear() => _list.Clear();
            public bool Remove(VolumeParameterAnimation item) => _list.Remove(item);

            private bool CanAdd(VolumeParameterAnimation parameter) =>
                parameter != null && parameter.Enabled && !_list.Contains(parameter);
        }
        #endregion
        
        private VolumeParameterAnimationCollection _animatedParameters;
        private bool _wasComponentEnabled;
        
        /// <summary>
        /// Set the animation profile for the specified volume component
        /// </summary>
        public sealed override void SetProfile(VolProfile profile)
        {
            // Try to get the specified component from the volume profile
            if (!TryGetComponent(profile, out T component))
            {
                // If the component doesn't exist, add it to the volume profile
                component = AddNewComponent(profile);
                _wasComponentEnabled = false;
            }
            else
                _wasComponentEnabled = component.active;
            
            // Ensure the component is active
            component.active = true;

            // Initialize the animated parameters collection if necessary
            _animatedParameters ??= new VolumeParameterAnimationCollection();
            
            // Add animations for the component to the collection
            AddAnimations(_animatedParameters, component);
        }

        /// <summary>
        /// Dispose the animation for the specified volume component
        /// </summary>
        public sealed override void Dispose(VolProfile profile)
        {
            // Dispose each animated parameter
            for (int i = 0; i < _animatedParameters.Count; i++)
                _animatedParameters[i].Dispose();

            // Clear the animated parameters collection
            _animatedParameters.Clear();
            
            // Disable the component if it wasn't initially enabled
            if (!_wasComponentEnabled && TryGetComponent(profile, out T component))
                component.active = false;
        }

        /// <summary>
        /// Animate the volume component based on the current time 't'
        /// </summary>
        public sealed override void Animate(float t)
        {
            // Animate each parameter in the collection
            for (int i = 0; i < _animatedParameters.Count; i++)
                _animatedParameters[i].Animate(t);
        }

        protected abstract void AddAnimations(VolumeParameterAnimationCollection list, T component);

        private static bool TryGetComponent(VolProfile volumeProfile, out T component)
        {
#if POLYMIND_GAMES_FPS_HDRP || POLYMIND_GAMES_FPS_URP
            return volumeProfile.TryGet(out component);
#else
            return volumeProfile.TryGetSettings(out component);
#endif
        }
        
        private static T AddNewComponent(VolProfile volumeProfile)
        {
#if POLYMIND_GAMES_FPS_HDRP || POLYMIND_GAMES_FPS_URP
            return volumeProfile.Add<T>();
#else
            return volumeProfile.AddSettings<T>();
#endif
        }
    }
}