using System;
using UnityEngine;

#if POLYMIND_GAMES_FPS_HDRP
using VolumeComponent = UnityEngine.Rendering.HighDefinition.ChromaticAberration;
#elif POLYMIND_GAMES_FPS_URP
using VolumeComponent = UnityEngine.Rendering.Universal.ChromaticAberration;
#else
using VolumeComponent = UnityEngine.Rendering.PostProcessing.ChromaticAberration;
#endif

namespace PolymindGames.PostProcessing
{
    [Serializable]
    public sealed class ChromaticAnimation : VolumeAnimation<VolumeComponent>
    {
        [SerializeField]
        private VolumeParameterAnimation<float> _intensity = new(0f, 0f);
        
        protected override void AddAnimations(VolumeParameterAnimationCollection list, VolumeComponent component)
        {
            list.Add(_intensity.SetParameter(component.intensity));
        }
    }
}