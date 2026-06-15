using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Basic Barrel-Effect")]
    public class FirearmBasicBarrelEffect : FirearmBarrelEffectBehaviour
    {
        [SerializeField]
        private AudioData _fireAudio = new(null);

        [SerializeField]
        private DelayedAudioData _fireTailAudio = new(null);

        [SerializeField, NotNull, SpaceArea]
        private LightEffect _light;

        [SerializeField, SpaceArea]
        private Transform _particlesRoot;

        [SerializeField, DisableIf(nameof(_particlesRoot), false)]
        private Vector3 _particlesAimOffset;

        [SerializeField, SpaceArea]
#if UNITY_EDITOR
        [EditorButton(nameof(FillReferences))]
#endif
        [ReorderableList(HasLabels = false, Foldable = true)]
        private ParticleSystem[] _particles = Array.Empty<ParticleSystem>();

        private float _fireEffectsTimer;
        private Vector3 _originalOffset;

        private const float PlayParticlesCooldown = 0.1f;

        public override void TriggerFireEffect()
        {
            Wieldable.Audio.PlayClip(_fireAudio, BodyPoint.Hands);
            _light.Play(false);

            if (_particlesRoot != null)
            {
                _particlesRoot.transform.localPosition
                    = Firearm.AimHandler.IsAiming ? _originalOffset + _particlesAimOffset : _originalOffset;
            }

            if (_fireEffectsTimer < Time.time)
            {
                foreach (var particle in _particles)
                    particle.Play(false);

                _fireEffectsTimer = Time.time + PlayParticlesCooldown;
            }
        }

        public override void TriggerFireStopEffect()
        {
            if (_fireTailAudio.IsPlayable)
                Wieldable.Audio.PlayClip(_fireTailAudio, BodyPoint.Hands);
        }

        protected override void Awake()
        {
            base.Awake();

            if (_particlesRoot != null)
                _originalOffset = _particlesRoot.localPosition;
        }

        #region Editor
#if UNITY_EDITOR
        [Conditional("UNITY_EDITOR")]
        private void FillReferences()
        {
            _light = GetComponentInChildren<LightEffect>();
            _particles = GetComponentsInChildren<ParticleSystem>();

            if (_particles != null && _particles.Length > 0 && _particlesRoot == null)
            {
                int maxChildCount = 0;
                foreach (var particle in _particles)
                {
                    var trs = particle.transform;

                    if (trs.childCount > maxChildCount)
                    {
                        _particlesRoot = trs;
                        maxChildCount = trs.childCount;
                    }
                }

                if (_particlesRoot == null)
                    _particlesRoot = _particles[0].transform.parent;
            }

            EditorUtility.SetDirty(this);
        }
#endif
        #endregion
    }
}