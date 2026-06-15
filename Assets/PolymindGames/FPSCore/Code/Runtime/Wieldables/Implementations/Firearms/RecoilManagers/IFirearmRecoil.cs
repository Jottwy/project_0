using PolymindGames.ProceduralMotion;
using UnityEngine.Serialization;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Defines the interface for firearm recoil implementations, providing methods for initialization and recoil application.
    /// </summary>
    public interface IFirearmRecoil
    {
        /// <summary>
        /// Initializes the recoil system with the provided motion mixer and character reference.
        /// </summary>
        void Initialize(IMotionMixer motionMixer, ICharacter character);

        /// <summary>
        /// Applies the recoil effect based on the given strength and progression through the recoil sequence.
        /// </summary>
        void ApplyRecoil(float recoilStrength, float recoilProgress, bool isAiming);
    }

    /// <summary>
    /// Handles recoil using spring forces for both position and rotation.
    /// </summary>
    [Serializable]
    public sealed class SpringForceRecoil : IFirearmRecoil
    {
        [FormerlySerializedAs("_recoilProgressStrength")]
        [SerializeField, Range(0f, 1f)]
        private float _recoilProgressMultiplier = 0f;

        [SerializeField, Range(0f, 1f)]
        private float _aimStrengthMultiplier = 1f;

        [SerializeField]
        private SpringType _springType = SpringType.Responsive;

        [SerializeField, ShowIf(nameof(_springType), SpringType.Custom)]
        private SpringSettings _springSettings = new(14f, 250f, 1f, 1.5f);

        [SerializeField, SpaceArea(3f)]
        [Tooltip("Defines the positional spring force applied for recoil.")]
        private RandomSpringForce3D _positionForce = RandomSpringForce3D.Default;

        [SerializeField]
        [Tooltip("Defines the rotational spring force applied for recoil.")]
        private RandomSpringForce3D _rotationForce = RandomSpringForce3D.Default;

        private AdditiveForceMotion _additiveForceMotion;

        /// <inheritdoc />
        public void Initialize(IMotionMixer motionMixer, ICharacter character)
        {
            _additiveForceMotion = motionMixer.GetMotion<AdditiveForceMotion>();
        }

        /// <inheritdoc />
        public void ApplyRecoil(float recoilStrength, float recoilProgress, bool isAiming)
        {
            recoilStrength *= Mathf.Lerp(1f, recoilProgress, _recoilProgressMultiplier);
            recoilStrength *= isAiming ? _aimStrengthMultiplier : 1f;

            if (_springType == SpringType.Custom)
                _additiveForceMotion.SetCustomSpringSettings(_springSettings, _springSettings);

            _additiveForceMotion.AddPositionForce(_positionForce, recoilStrength, _springType);
            _additiveForceMotion.AddRotationForce(_rotationForce, recoilStrength, _springType);
        }
    }

    /// <summary>
    /// Handles recoil using shake settings for both position and rotation.
    /// </summary>
    [Serializable]
    public sealed class SpringShakeRecoil : IFirearmRecoil
    {
        [SerializeField]
        [Tooltip("Defines the shake settings for positional recoil.")]
        private ShakeData _shake;

        private AdditiveShakeMotion _additiveShakeMotion;

        /// <inheritdoc />
        public void Initialize(IMotionMixer motionMixer, ICharacter character)
        {
            _additiveShakeMotion = motionMixer.GetMotion<AdditiveShakeMotion>();
        }

        /// <inheritdoc />
        public void ApplyRecoil(float recoilStrength, float recoilProgress, bool isAiming)
        {
            if (_shake.IsPlayable)
                _additiveShakeMotion.AddShake(_shake, recoilStrength);
        }
    }

    /// <summary>
    /// Applies a recoil pattern to the camera using customizable spring settings and a curve.
    /// </summary>
    [Serializable]
    public sealed class CameraPatternSpringRecoil : IFirearmRecoil
    {
        [SerializeField]
        [Tooltip("Settings for the recoil spring, determining its responsiveness.")]
        private SpringSettings _recoilSpringSettings = SpringSettings.Default;

        [SerializeField]
        [Tooltip("Settings for the recovery spring, controlling how the camera returns to its initial position.")]
        private SpringSettings _recoverySpringSettings = SpringSettings.Default;

        [SerializeField, Line]
        [Tooltip("Animation curve defining the progression of recoil over time.")]
        private AnimCurves2D _recoilPatternCurve = new();

        private CharacterRecoilMotion _characterRecoilMotion;

        /// <inheritdoc />
        public void Initialize(IMotionMixer motionMixer, ICharacter character)
        {
            if (!motionMixer.TryGetMotion(out _characterRecoilMotion))
            {
                _characterRecoilMotion = new CharacterRecoilMotion(character);
                motionMixer.AddMotion(_characterRecoilMotion);
            }

            _characterRecoilMotion.SetRecoilSprings(_recoilSpringSettings, _recoverySpringSettings);
        }

        /// <inheritdoc />
        public void ApplyRecoil(float recoilStrength, float recoilProgress, bool isAiming)
        {
            Vector2 recoilAmount = _recoilPatternCurve.Evaluate(recoilProgress);
            _characterRecoilMotion.AddRecoil(recoilAmount * recoilStrength);
        }
    }

    /// <summary>
    /// Applies a randomized force-based recoil to the camera using spring settings.
    /// </summary>
    [Serializable]
    public sealed class CameraForceSpringRecoil : IFirearmRecoil
    {
        [SerializeField]
        [Tooltip("Settings for the recoil spring, determining the camera's response to recoil.")]
        private SpringSettings _recoilSpringSettings = new(13, 250f, 1, 3f);

        [SerializeField]
        [Tooltip("Settings for the recovery spring, controlling how the camera stabilizes after recoil.")]
        private SpringSettings _recoverySpringSettings = new(11f, 55f, 1.1f, 1f);

        [SerializeField, Line]
        [Tooltip("Range for the X-axis recoil (vertical rotation).")]
        private Vector2 _xRecoilRange = new(-1.25f, -1.5f);

        [SerializeField]
        [Tooltip("Range for the Y-axis recoil (horizontal rotation).")]
        private Vector2 _yRecoilRange = new(-0.25f, 0.25f);

        private CharacterRecoilMotion _characterRecoilMotion;

        /// <inheritdoc />
        public void Initialize(IMotionMixer motionMixer, ICharacter character)
        {
            if (!motionMixer.TryGetMotion(out _characterRecoilMotion))
            {
                _characterRecoilMotion = new CharacterRecoilMotion(character);
                motionMixer.AddMotion(_characterRecoilMotion);
            }

            _characterRecoilMotion.SetRecoilSprings(_recoilSpringSettings, _recoverySpringSettings);
        }

        /// <inheritdoc />
        public void ApplyRecoil(float recoilStrength, float recoilProgress, bool isAiming)
        {
            Vector2 recoilAmount = new Vector2(
                _xRecoilRange.GetRandomFromRange() * recoilStrength,
                _yRecoilRange.GetRandomFromRange() * recoilStrength
            );
            _characterRecoilMotion.AddRecoil(recoilAmount);
        }
    }
}