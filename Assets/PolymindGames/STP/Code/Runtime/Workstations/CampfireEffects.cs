using System.Collections;
using UnityEngine;

namespace PolymindGames
{
    [RequireComponent(typeof(CookingStation))]
    public sealed class CampfireEffects : MonoBehaviour
    {
        [SerializeField, NotNull, Title("Material")]
        private GameObject _wood;

        [SerializeField, NotNull]
        private Material _woodMaterial;

        [SerializeField, NotNull, Title("Audio")]
        private AudioEffect _audioEffect;

        [SerializeField, Range(0f, 1f)]
        private float _minFireVolume = 0.5f;

        [SerializeField, NotNull, Title("Light")]
        private LightEffect _lightEffect;

        [SerializeField, Range(0f, 1f)]
        private float _minLightIntensity = 0.5f;

        [SpaceArea, Title("Particles")]
        [SerializeField, ReorderableList(ListStyle.Lined, HasLabels = false, HasHeader = false)]
        private ParticleSystem[] _particleEffects;

        private static readonly int _burnedAmountShaderId = Shader.PropertyToID("_Burned_Amount");
        private Material _burnedWoodMaterial;
        private CookingStation _station;

        private void Awake()
        {
            _station = GetComponent<CookingStation>();
            _station.CookingStarted += OnCookingStarted;
        }

        private void OnDestroy()
        {
            _station.CookingStarted -= OnCookingStarted;
        }

        private void CreateBurnedWoodMaterial()
        {
            _burnedWoodMaterial = new Material(_woodMaterial);
            _burnedWoodMaterial.SetFloat(_burnedAmountShaderId, 0f);

            var renderers = _wood.GetComponentsInChildren<Renderer>(true);
            foreach (var rend in renderers)
                rend.sharedMaterial = _burnedWoodMaterial;

            _wood.SetActive(false);
        }

        private void OnCookingStarted()
        {
            if (_burnedWoodMaterial == null)
                CreateBurnedWoodMaterial();
            
            StopAllCoroutines();
            
            if (_station.IsCookingActive)
                StartCoroutine(UpdateEffectsRoutine());
        }

        private IEnumerator UpdateEffectsRoutine()
        {
            StartEffects();
            UpdateEffects(0f);

            while (_station.IsCookingActive)
            {
                UpdateEffects(_station.CookingProgress);
                yield return null;
            }

            UpdateEffects(1f);
            StopEffects();
        }

        private void StartEffects()
        {
            foreach (var effect in _particleEffects)
                effect.Play(false);

            _wood.SetActive(true);
            _lightEffect.Play();
            _audioEffect.Play();
        }

        private void UpdateEffects(float strength)
        {
            _audioEffect.VolumeMultiplier = Mathf.Lerp(_minFireVolume, Mathf.Max(strength, _minFireVolume), strength);
            _lightEffect.Multiplier = Mathf.Lerp(_minLightIntensity, Mathf.Max(strength, _minLightIntensity), strength);
            _burnedWoodMaterial.SetFloat(_burnedAmountShaderId, strength);
        }

        private void StopEffects()
        {
            foreach (var effect in _particleEffects)
                effect.Stop(false);

            _lightEffect.Stop();
            _audioEffect.Stop();
        }
    }
}