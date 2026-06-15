#if POLYMIND_GAMES_FPS_HDRP
using UnityEngine.Rendering.HighDefinition;
#endif

using System.Runtime.CompilerServices;
using UnityEngine.Rendering;
using UnityEngine;
using System;

namespace PolymindGames.WorldManagement
{
    [DisallowMultipleComponent, ExecuteAlways]
    public sealed class DayNightCycleController : MonoBehaviour
    {
        #region Internal Types
        [Serializable]
        private struct LightSettings
        {
            [NotNull]
            public Light SunLight;

            [NotNull]
            public Light MoonLight;

            [Range(-360f, 360f)]
            public float LightsTilt;
            
            [SpaceArea]
            [ShowForRenderPipeline(ShowForRenderPipelineAttribute.Type.BuiltIn)]
            public LightCycleSettings SunLightCycle;
            
            [ShowForRenderPipeline(ShowForRenderPipelineAttribute.Type.BuiltIn)]
            public LightCycleSettings MoonLightCycle;
        }
        
        [Serializable]
        private struct AmbientSettings
        {
            public Gradient FogColor;
            public Gradient AmbientSkyLight;
            public Gradient AmbientEquatorLight;
            public Gradient AmbientGroundLight;
        }

        [Serializable]
        private struct ReflectionSettings
        {
            public ReflectionProbe ReflectionProbe;
        }
        
        [Serializable]
        private class LightCycleSettings
        {
            public Gradient Color;

            [Range(0f, 5f)]
            public float Intensity;

            public AnimationCurve IntensityCurve;
        }

        [Serializable]
        private struct VolumeSettings
        {
            [Range(0f, 1000f)]
            public float WindSpeed;
        
            public AnimationCurve StarsExposure;
        }
        #endregion

        [SerializeField]
        private bool _isActive = true;
        
        [SerializeField, SubGroup]
        private LightSettings _light = new()
        {
            LightsTilt = -35
        };
        
        [SerializeField, SubGroup]
        private ReflectionSettings _reflection;

        [SerializeField, SubGroup]
        [ShowForRenderPipeline(ShowForRenderPipelineAttribute.Type.BuiltIn)]
        private AmbientSettings _ambient;

        [SerializeField, SubGroup]
        [ShowForRenderPipeline(ShowForRenderPipelineAttribute.Type.Hdrp)]
        private VolumeSettings _volume = new()
        {
            WindSpeed = 1f
        };

#if POLYMIND_GAMES_FPS_HDRP
        private PhysicallyBasedSky _physicallyBasedSky; 
        private VisualEnvironment _environment;
#endif

        private bool _isInitialized;
        private ITimeManager _time;

        public void Toggle() => _isActive = !_isActive;

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    UpdateCycle(_time.DayTime);
                    OnHourChanged(_time.Hour, 0);
                }
            }
        }

        private void OnEnable()
        {
            _time = World.Instance.Time;
            if (_time == null)
            {
                Debug.LogWarning("No time manager found in the scene.");
                return;
            }

            _time.DayTimeChanged += UpdateCycle;
            _time.HourChanged += OnHourChanged;

            UpdateCycle(_time.DayTime);
            OnHourChanged(_time.Hour, 0);
        }

        private void OnDisable()
        {
            if (_time == null)
            {
                Debug.LogWarning("No time manager found in the scene.");
                return;
            }
            
            _time.DayTimeChanged -= UpdateCycle;
            _time.HourChanged -= OnHourChanged;
        }

        private void Awake() => Init();
        
#if UNITY_EDITOR
        private void Start() => Init();
#endif

        private void Init()
        {
            if (_isInitialized)
                return;
            
            RenderSettings.sun = _light.SunLight;
            if (_reflection.ReflectionProbe != null)
                _reflection.ReflectionProbe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            
            HandleVolumeComponents();
            HandleReflectionSettings();
            UpdateReflection();
            _isInitialized = true;
        }

        private void OnHourChanged(int total, int change)
        {
            if (!_isActive)
                return;
            
            UpdateReflection();
        }

        private void UpdateCycle(float dayTime)
        {
            if (!_isActive)
                return;
            
            UpdateSun(dayTime);
            UpdateMoon(dayTime);
            UpdateVolume(dayTime);
            UpdateAmbient(dayTime);
        }
        
        private void UpdateReflection()
        {
            if (_reflection.ReflectionProbe != null)
                _reflection.ReflectionProbe.RenderProbe();
        }

        private void UpdateSun(float dayTime)
        {
            bool isSunlightVisible = IsSunlightVisible(dayTime);
            _light.SunLight.enabled = isSunlightVisible;

#if POLYMIND_GAMES_FPS_HDRP
            if (!isSunlightVisible)
                return;
#endif
            
            float sunAngle = Mathf.Lerp(-90, 270, dayTime);

#if !POLYMIND_GAMES_FPS_HDRP
            var cycle = _light.SunLightCycle;
            _light.SunLight.intensity = cycle.IntensityCurve.Evaluate(dayTime) * cycle.Intensity;
            _light.SunLight.color = cycle.Color.Evaluate(dayTime);
#endif
            
            _light.SunLight.transform.rotation = Quaternion.Euler(sunAngle, _light.LightsTilt, 0f);
        }
        
        private void UpdateMoon(float dayTime)
        {
            bool isMoonLightVisible = IsMoonlightVisible(dayTime);
            _light.MoonLight.enabled = isMoonLightVisible;
            _light.MoonLight.shadows = IsSunlightVisible(dayTime) ? LightShadows.None : LightShadows.Soft;
            
            if (!isMoonLightVisible)
                return;

            float sunAngle = Mathf.Lerp(-90, 270, dayTime);
            float moonAngle = sunAngle - 175f;

#if !POLYMIND_GAMES_FPS_HDRP
            var cycle = _light.MoonLightCycle;
            _light.MoonLight.intensity = cycle.IntensityCurve.Evaluate(dayTime) * cycle.Intensity;
            _light.MoonLight.color = cycle.Color.Evaluate(dayTime);
#endif

            _light.MoonLight.transform.rotation = Quaternion.Euler(moonAngle, _light.LightsTilt, 0);
        }

        private void UpdateAmbient(float dayTime)
        {
#if !POLYMIND_GAMES_FPS_HDRP
            RenderSettings.fogColor = _ambient.FogColor.Evaluate(dayTime);
            RenderSettings.ambientSkyColor = _ambient.AmbientSkyLight.Evaluate(dayTime);
            RenderSettings.ambientEquatorColor = _ambient.AmbientEquatorLight.Evaluate(dayTime);
            RenderSettings.ambientGroundColor = _ambient.AmbientGroundLight.Evaluate(dayTime);
#endif
        }

        private void UpdateVolume(float dayTime)
        {
#if POLYMIND_GAMES_FPS_HDRP && !UNITY_POST_PROCESSING_STACK_V2
            float exposure = _volume.StarsExposure.Evaluate(dayTime);
            _physicallyBasedSky.spaceEmissionMultiplier.value = exposure;
            _environment.windSpeed.Override(_volume.WindSpeed * _time.DayTimeIncrementPerSecond);
#endif
        }

        private void HandleReflectionSettings()
        {
            var reflectionProbe = _reflection.ReflectionProbe;
            if (reflectionProbe == null)
                return;
            
            reflectionProbe.timeSlicingMode = Application.isPlaying 
                ? ReflectionProbeTimeSlicingMode.IndividualFaces
                : ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
            
            reflectionProbe.RenderProbe();
        }

        private void HandleVolumeComponents()
        {
#if POLYMIND_GAMES_FPS_HDRP && !UNITY_POST_PROCESSING_STACK_V2
            var volume = GetComponentInChildren<Volume>();

            if (volume == null)
            {
                Debug.LogError("This component requires a volume on any of its child objects.", gameObject);
                return;
            }

            var profile = Application.isPlaying ? volume.profile : volume.sharedProfile;
            profile.TryGet(out _physicallyBasedSky);
            profile.TryGet(out _environment);
#endif
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsSunlightVisible(float dayTime) => dayTime < 0.765f && dayTime > 0.215f;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsMoonlightVisible(float dayTime) => dayTime > 0.75f || dayTime < 0.25f;

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_time != null && _isInitialized)
                UpdateCycle(_time.DayTime);
        }
#endif
        #endregion
    }
}