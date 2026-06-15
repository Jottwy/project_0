using System;
using UnityEngine;

#if POLYMIND_GAMES_FPS_HDRP
using VolumeComponent = UnityEngine.Rendering.HighDefinition.ColorAdjustments;
#elif POLYMIND_GAMES_FPS_URP
using VolumeComponent = UnityEngine.Rendering.Universal.ColorAdjustments;
#else
using VolumeComponent = UnityEngine.Rendering.PostProcessing.ColorGrading;
#endif

namespace PolymindGames.PostProcessing
{
    [Serializable]
    public sealed class ExposureAnimation : VolumeAnimation<VolumeComponent>
    {
        [SerializeField]
        private VolumeParameterAnimation<float> _postExposure = new(0f, 0f);
        
        protected override void AddAnimations(VolumeParameterAnimationCollection list, VolumeComponent component)
        {
            list.Add(_postExposure.SetParameter(component.postExposure));
        }
    }
}