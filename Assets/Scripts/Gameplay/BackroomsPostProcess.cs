using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Fase 5D — Built-in PPv2 post-process for the Backrooms look. Builds a global
    /// PostProcessVolume + a runtime profile (Vignette / Grain / Bloom / ColorGrading) and
    /// exposes per-effect enable + intensity, persisted to PlayerPrefs (bp_*). The camera
    /// side (the PostProcessLayer) is handled by <see cref="PlayerCameraPostProcessEnabler"/>.
    /// Singleton; created by GridTestWorld.InitializeWorld.
    /// </summary>
    public sealed class BackroomsPostProcess : MonoBehaviour
    {
        public static BackroomsPostProcess Instance { get; private set; }

        // PlayerPrefs keys: intensity under the key, the on/off toggle under "<key>_on".
        private const string KVig = "bp_vignette", KGrain = "bp_grain",
                             KBloom = "bp_bloom", KGrade = "bp_colorgrading";

        // Backrooms defaults.
        private const float DefVig = 0.38f, DefGrain = 0.18f, DefBloom = 0.5f, DefGrade = 1f;

        private PostProcessProfile _profile;
        private PostProcessVolume  _volume;
        private Vignette     _vignette;
        private Grain        _grain;
        private Bloom        _bloom;
        private ColorGrading _grading;
        private float        _gradeT = DefGrade;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            BuildVolume();
            LoadFromPrefs();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_volume != null) Destroy(_volume.gameObject);
            if (_profile != null) Destroy(_profile);
        }

        private void BuildVolume()
        {
            _profile  = ScriptableObject.CreateInstance<PostProcessProfile>();
            _vignette = _profile.AddSettings<Vignette>();
            _grain    = _profile.AddSettings<Grain>();
            _bloom    = _profile.AddSettings<Bloom>();
            _grading  = _profile.AddSettings<ColorGrading>();

            // Static (non-tunable) parameters → Backrooms character.
            _vignette.smoothness.Override(0.4f);
            _vignette.rounded.Override(true);
            _grain.lumContrib.Override(0.8f); // "response"
            _grain.colored.Override(false);
            _grain.size.Override(1.0f);
            _bloom.threshold.Override(0.85f);
            _bloom.softKnee.Override(0.5f);
            _grading.gradingMode.Override(GradingMode.LowDefinitionRange);

            var go = new GameObject("BackroomsPostProcessVolume");
            go.transform.SetParent(transform, false);
            _volume = go.AddComponent<PostProcessVolume>();
            _volume.isGlobal = true;
            _volume.priority = 100f;
            _volume.weight   = 1f;
            _volume.sharedProfile = _profile;
        }

        // ── Public API ──────────────────────────────────────────────────────────

        public void SetVignetteIntensity(float v)    { _vignette.intensity.Override(Mathf.Clamp01(v)); SaveFloat(KVig, _vignette.intensity.value); }
        public void SetGrainIntensity(float v)        { _grain.intensity.Override(Mathf.Clamp01(v));    SaveFloat(KGrain, _grain.intensity.value); }
        public void SetBloomIntensity(float v)        { _bloom.intensity.Override(Mathf.Max(0f, v));    SaveFloat(KBloom, _bloom.intensity.value); }
        public void SetColorGradingIntensity(float t) { _gradeT = Mathf.Clamp01(t); ApplyGrading(_gradeT); SaveFloat(KGrade, _gradeT); }

        public void SetVignetteEnabled(bool on)     { _vignette.enabled.Override(on); SaveBool(KVig, on); }
        public void SetGrainEnabled(bool on)        { _grain.enabled.Override(on);    SaveBool(KGrain, on); }
        public void SetBloomEnabled(bool on)        { _bloom.enabled.Override(on);    SaveBool(KBloom, on); }
        public void SetColorGradingEnabled(bool on) { _grading.enabled.Override(on);  SaveBool(KGrade, on); }

        // Current values (for the UI to initialise its widgets).
        public float VignetteIntensity     => _vignette.intensity.value;
        public float GrainIntensity        => _grain.intensity.value;
        public float BloomIntensity        => _bloom.intensity.value;
        public float ColorGradingIntensity => _gradeT;
        public bool  VignetteEnabled       => _vignette.enabled.value;
        public bool  GrainEnabled          => _grain.enabled.value;
        public bool  BloomEnabled          => _bloom.enabled.value;
        public bool  ColorGradingEnabled   => _grading.enabled.value;

        private void ApplyGrading(float t)
        {
            _grading.saturation.Override(Mathf.Lerp(0f, -18f, t));
            _grading.contrast.Override(Mathf.Lerp(0f, 12f, t));
            _grading.colorFilter.Override(Color.Lerp(Color.white, new Color(1f, 0.97f, 0.88f), t));
        }

        // ── Persistence ─────────────────────────────────────────────────────────

        private void LoadFromPrefs()
        {
            SetVignetteEnabled(LoadBool(KVig));
            SetVignetteIntensity(PlayerPrefs.GetFloat(KVig, DefVig));
            SetGrainEnabled(LoadBool(KGrain));
            SetGrainIntensity(PlayerPrefs.GetFloat(KGrain, DefGrain));
            SetBloomEnabled(LoadBool(KBloom));
            SetBloomIntensity(PlayerPrefs.GetFloat(KBloom, DefBloom));
            SetColorGradingEnabled(LoadBool(KGrade));
            SetColorGradingIntensity(PlayerPrefs.GetFloat(KGrade, DefGrade));
        }

        private static void SaveFloat(string key, float v) { PlayerPrefs.SetFloat(key, v); }
        private static void SaveBool(string key, bool on)   { PlayerPrefs.SetInt(key + "_on", on ? 1 : 0); }
        private static bool LoadBool(string key)            => PlayerPrefs.GetInt(key + "_on", 1) != 0;
    }
}
