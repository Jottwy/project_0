using UnityEngine;
using System;

#if POLYMIND_GAMES_FPS_HDRP || POLYMIND_GAMES_FPS_URP
using UnityEngine.Rendering;
#else
using UnityEngine.Rendering.PostProcessing;
#endif

namespace PolymindGames.PostProcessing
{
    [Serializable]
    public abstract class VolumeParameterAnimation
    {
        [SerializeField]
        private bool _enabled = true;
        
        public bool Enabled => _enabled;

        public abstract void Animate(float t);
        public abstract void Dispose();
    }
    
    [Serializable]
    public sealed class VolumeParameterAnimation<T> : VolumeParameterAnimation
    {
        [SerializeField]
        private T _targetValue;

        [SerializeField]
        private AnimationCurve _animation;
        
#if POLYMIND_GAMES_FPS_HDRP || POLYMIND_GAMES_FPS_URP
        private VolumeParameter<T> _volumeParameter;
#else
        private ParameterOverride<T> _volumeParameter;
#endif
        
        private bool _originalOverrideState;
        private T _originalValue;

        public VolumeParameterAnimation(float curveStartValue, float curveEndValue)
        {
            _animation = AnimationCurve.EaseInOut(0f, curveStartValue, 1f, curveEndValue);
        }
        
#if POLYMIND_GAMES_FPS_HDRP || POLYMIND_GAMES_FPS_URP
        public VolumeParameterAnimation<T> SetParameter(VolumeParameter<T> parameter)
#else
        public VolumeParameterAnimation<T> SetParameter(ParameterOverride<T> parameter)
#endif
        {
            if (!Enabled)
                return null;
            
            _volumeParameter = parameter;
            _originalValue = parameter.value;
            _originalOverrideState = parameter.overrideState;
            _volumeParameter.overrideState = true;
            return this;
        }

        public override void Animate(float t)
        {
#if UNITY_EDITOR
            if (!Enabled)
            {
                _volumeParameter.value = _originalValue;
                return;
            }
#endif
            
            _volumeParameter.Interp(_originalValue, _targetValue, _animation.Evaluate(t));
        }

        public override void Dispose()
        {
            _volumeParameter.value = _originalValue;
            _volumeParameter.overrideState = _originalOverrideState;
            _volumeParameter = null;
        }
    }
}
