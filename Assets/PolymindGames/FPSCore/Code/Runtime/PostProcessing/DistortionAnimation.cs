using UnityEngine;
using System;

#if POLYMIND_GAMES_FPS_HDRP
using VolumeComponent = UnityEngine.Rendering.HighDefinition.LensDistortion;
#elif POLYMIND_GAMES_FPS_URP
using VolumeComponent = UnityEngine.Rendering.Universal.LensDistortion;
#else
using VolumeComponent = UnityEngine.Rendering.PostProcessing.LensDistortion;
#endif

namespace PolymindGames.PostProcessing
{
    [Serializable]
    public sealed class DistortionAnimation : VolumeAnimation<VolumeComponent>
    {
        [SerializeField]
        private VolumeParameterAnimation<float> _intensity = new(0f, 0f);
            
        protected override void AddAnimations(VolumeParameterAnimationCollection list, VolumeComponent component)
        {
            list.Add(_intensity.SetParameter(component.intensity));
        }
    }
}
