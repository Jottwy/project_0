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

        // Backrooms defaults. ADR-066 los subió (viñeta 0.45, grano 0.25) y el playtest los
        // corrigió: a esos valores el mundo se leía como metraje encontrado, no como un sitio
        // real. Ahora el grano es ruido de sensor en las sombras, no textura de película, y la
        // viñeta cierra el encuadre sin cantar como efecto de lente. La oscuridad la ponen el
        // ambient por zona y postExposure, que es donde debe estar.
        private const float DefVig = 0.32f, DefGrain = 0.08f, DefBloom = 0.45f, DefGrade = 1f;

        /// <summary>
        /// Umbral de bloom, en la misma escala que <c>lampEmission</c> del difusor. Es la
        /// pareja de tuneo de ese campo: por debajo del umbral el panel no florece nada y se
        /// pierde el halo de fluorescente; muy por encima, la superficie ENTERA florece y el
        /// rectángulo se convierte en una mancha sin forma. Con NORMAL emitiendo a 1.25, este
        /// 1.15 deja florecer solo la parte alta de la variación del panel: hay halo y se sigue
        /// leyendo la geometría rectangular. Subió de 1.05 porque postExposure pasó de −0.35 a
        /// 0 y eso mete +0,35 EV en todo lo que llega al bloom.
        /// </summary>
        private const float BloomThreshold = 1.15f;

        /// <summary>
        /// Exposición estática del grado de color. ADR-066 la puso en −0.35 para oscurecer, y
        /// eso APLASTABA LOS MEDIOS, que es donde vive el ambiente de Backrooms: ACES ya
        /// comprime los altos por sí solo, así que restar exposición encima solo se come el
        /// rango medio. A 0 el canon de Level 0 (plano, casi sobreexpuesto) es alcanzable.
        /// Es el valor MENOS seguro de la reautoría — se deja aquí, con nombre, para poder
        /// moverlo sin buscarlo: entre −0.15 y +0.15 el mundo sigue leyéndose.
        /// </summary>
        private const float PostExposure = 0f;

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
            // Thin1: grano fino. ADR-066 lo puso en Medium1 porque ACES apaga el fino, pero
            // Medium1 es grano de PELÍCULA y se ve como tal — justo lo que rompía el realismo.
            // El fino a baja intensidad ensucia las sombras sin anunciarse.
            _grain.type.Override(FilmGrainLookup.Thin1);
            _grain.response.Override(0.8f);
            // Enmienda ADR-066: 0.85 -> 0.75 fue un error compuesto. Los paneles emitian a
            // 2.8-3.4 (bug de unidades, ya corregido en BackroomsLighting), o sea casi 4x por
            // encima del umbral, y el bloom los convertia en manchas sin contorno que se
            // comian el techo. Con la emision ya en ~1.3, un umbral por ENCIMA de 1 hace que
            // solo florezcan los bordes del difusor y no la superficie entera.
            _bloom.threshold.Override(BloomThreshold);

            // ADR-066 — ACES: sin él los fluorescentes queman a blanco plano y el mundo se
            // lee como sobreexpuesto justo donde debería dar miedo.
            _tonemapping.mode.Override(TonemappingMode.ACES);

            // ADR-066 — la exposición es ESTÁTICA, fuera del lerp de ApplyGrading: el slider
            // bp_colorgrading gobierna carácter (saturación, contraste, tinte), no cuánta luz
            // entra. Apagar el grading no puede devolver un mundo brillante.
            _grading.postExposure.Override(PostExposure);

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
