using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// URP Volume post-process for the Backrooms look (reescrito desde PPv2 en la
    /// migración BIRP→URP). Builds a global Volume + a runtime profile
    /// (Vignette / FilmGrain / Bloom / ColorAdjustments) and exposes per-effect
    /// enable + intensity, persisted to PlayerPrefs under the same bp_* keys as the
    /// PPv2 era. The volume GameObject sits on the PostProcessing layer (11): the
    /// vendor player camera's volumeLayerMask only samples that layer. The camera
    /// side is handled by <see cref="PlayerCameraPostProcessEnabler"/>.
    /// Singleton; created by GridTestWorld.InitializeWorld.
    /// </summary>
    public sealed class BackroomsPostProcess : MonoBehaviour
    {
        public static BackroomsPostProcess Instance { get; private set; }

        // PlayerPrefs keys: intensity under the key, the on/off toggle under "<key>_on".
        private const string KVig = "bp_vignette", KGrain = "bp_grain",
                             KBloom = "bp_bloom", KGrade = "bp_colorgrading";

        // Backrooms defaults (ADR-066 subió viñeta y grano: el mundo tiene que cerrarse
        // sobre el jugador, no leerse como un pasillo de oficina bien iluminado).
        private const float DefVig = 0.45f, DefGrain = 0.25f, DefBloom = 0.6f, DefGrade = 1f;

        private VolumeProfile     _profile;
        private Volume            _volume;
        private Vignette          _vignette;
        private FilmGrain         _grain;
        private Bloom             _bloom;
        private ColorAdjustments  _grading;
        private Tonemapping       _tonemapping;
        private float             _gradeT = DefGrade;

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
            _profile     = ScriptableObject.CreateInstance<VolumeProfile>();
            _vignette    = _profile.Add<Vignette>();
            _grain       = _profile.Add<FilmGrain>();
            _bloom       = _profile.Add<Bloom>();
            _grading     = _profile.Add<ColorAdjustments>();
            _tonemapping = _profile.Add<Tonemapping>();

            // Static (non-tunable) parameters → Backrooms character.
            // PPv2 mapping: Grain.lumContrib→response, Grain.size≈1→Thin lookup;
            // Bloom.softKnee has no URP equivalent (scatter stays at default).
            _vignette.smoothness.Override(0.5f);
            _vignette.rounded.Override(true);
            _grain.type.Override(FilmGrainLookup.Medium1);
            _grain.response.Override(0.8f);
            _bloom.threshold.Override(0.75f);

            // ADR-066 — ACES: sin él los fluorescentes queman a blanco plano y el mundo se
            // lee como sobreexpuesto justo donde debería dar miedo.
            _tonemapping.mode.Override(TonemappingMode.ACES);

            // ADR-066 — la exposición es ESTÁTICA, fuera del lerp de ApplyGrading: el slider
            // bp_colorgrading gobierna carácter (saturación, contraste, tinte), no cuánta luz
            // entra. Apagar el grading no puede devolver un mundo brillante. Compensa además
            // el lift de ACES en los medios.
            _grading.postExposure.Override(-0.35f);

            var go = new GameObject("BackroomsPostProcessVolume");
            go.transform.SetParent(transform, false);
            int ppLayer = LayerMask.NameToLayer("PostProcessing");
            if (ppLayer >= 0) go.layer = ppLayer;
            _volume = go.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 100f;
            _volume.weight   = 1f;
            _volume.sharedProfile = _profile;
        }

        // ── Public API ──────────────────────────────────────────────────────────

        public void SetVignetteIntensity(float v)     { _vignette.intensity.Override(Mathf.Clamp01(v)); SaveFloat(KVig, _vignette.intensity.value); }
        public void SetGrainIntensity(float v)        { _grain.intensity.Override(Mathf.Clamp01(v));    SaveFloat(KGrain, _grain.intensity.value); }
        public void SetBloomIntensity(float v)        { _bloom.intensity.Override(Mathf.Max(0f, v));    SaveFloat(KBloom, _bloom.intensity.value); }
        public void SetColorGradingIntensity(float t) { _gradeT = Mathf.Clamp01(t); ApplyGrading(_gradeT); SaveFloat(KGrade, _gradeT); }

        // URP VolumeComponents toggle via .active (PPv2 used the enabled parameter).
        public void SetVignetteEnabled(bool on)     { _vignette.active = on; SaveBool(KVig, on); }
        public void SetGrainEnabled(bool on)        { _grain.active = on;    SaveBool(KGrain, on); }
        public void SetBloomEnabled(bool on)        { _bloom.active = on;    SaveBool(KBloom, on); }
        public void SetColorGradingEnabled(bool on) { _grading.active = on;  SaveBool(KGrade, on); }

        // Current values (for the UI to initialise its widgets).
        public float VignetteIntensity     => _vignette.intensity.value;
        public float GrainIntensity        => _grain.intensity.value;
        public float BloomIntensity        => _bloom.intensity.value;
        public float ColorGradingIntensity => _gradeT;
        public bool  VignetteEnabled       => _vignette.active;
        public bool  GrainEnabled          => _grain.active;
        public bool  BloomEnabled          => _bloom.active;
        public bool  ColorGradingEnabled   => _grading.active;

        private void ApplyGrading(float t)
        {
            _grading.saturation.Override(Mathf.Lerp(0f, -28f, t));
            _grading.contrast.Override(Mathf.Lerp(0f, 15f, t));
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
