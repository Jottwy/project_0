using PolymindGames.ProceduralMotion;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Iron Sight Aim-Handler")]
    public class FirearmIronSightAimHandler : FirearmAimHandlerBehaviour
    {
        [SerializeField, Range(0f, 2f), Title("Field of View")]
        [Tooltip("Duration for adjusting the field of view when aiming.")]
        private float _fovSetDuration = 0.4f;

        [SerializeField, Range(0f, 2f)]
        [Tooltip("Field of view modification factor for the head when aiming.")]
        private float _headFOVMod = 0.75f;

        [SerializeField, Range(0f, 2f)]
        [Tooltip("Field of view modification factor for the hands when aiming.")]
        private float _handsFOVMod = 0.75f;

        [SerializeField, Range(0f, 1f), Title("Motion")]
        [Tooltip("Multiplier to reduce the intensity of the weapon's and camera's motion when aiming.")]
        private float _motionDampeningFactor = 0.25f;

        [SerializeField, NewLabel("Hands Offset (Wieldable)")]
        [Tooltip("Offset for adjusting the weapon's position during aiming.")]
        private OffsetMotionData _handsOffset;

        [SerializeField]
        [NewLabel("Head Profile (Camera)")]
        [Tooltip("List of camera motions applied when aiming.")]
        private MotionProfile _headMotionProfile;

        [SerializeField, Title("Audio")]
        [Tooltip("Audio configuration for the sound played when aiming starts.")]
        private AudioData _aimStartAudio = new(null);

        [SerializeField]
        [Tooltip("Audio configuration for the sound played when aiming stops.")]
        private AudioData _aimStopAudio = new(null);

        private WieldableFOV _wieldableFOV;
        private float _prevViewOffsetWeight;

        /// <summary>
        /// Begins aiming, applying motion effects, animations, and audio.
        /// </summary>
        /// <returns>True if aiming started successfully; otherwise, false.</returns>
        public override bool StartAiming()
        {
            if (!base.StartAiming())
                return false;

            ApplyAimOffset(true);
            ApplyAimCameraEffects(true);
            ApplyFieldOfView(true);

            // Animation
            Wieldable.Animator.SetBool(AnimationConstants.IsAiming, true);

            // Audio
            Wieldable.Audio.PlayClip(_aimStartAudio, BodyPoint.Hands);

            return true;
        }

        /// <summary>
        /// Stops aiming, reverting motion effects, animations, and audio.
        /// </summary>
        /// <returns>True if aiming stopped successfully; otherwise, false.</returns>
        public override bool StopAiming()
        {
            if (!base.StopAiming())
                return false;

            if (UnityUtility.IsQuitting)
                return true;
            
            ApplyAimOffset(false);
            ApplyFieldOfView(false);
            ApplyAimCameraEffects(false);

            // Animation
            Wieldable.Animator.SetBool(AnimationConstants.IsAiming, false);

            // Audio
            Wieldable.Audio.PlayClip(_aimStopAudio, BodyPoint.Hands);

            return true;
        }

        /// <summary>
        /// Applies or removes aiming-related weapon offset adjustments.
        /// </summary>
        /// <param name="enable">True to apply offset, false to reset.</param>
        private void ApplyAimOffset(bool enable)
        {
            var motion = Wieldable.Motion;
            var mixer = motion.HandsComponents.Mixer;
            var dataHandler = motion.HandsComponents.Data;

            mixer.WeightMultiplier = enable ? _motionDampeningFactor : 1f;
            dataHandler.SetDataOverride<OffsetMotionData>(enable ? _handsOffset : null);

            if (mixer.TryGetMotion(out ViewOffsetMotion viewOffset))
            {
                if (enable)
                {
                    _prevViewOffsetWeight = viewOffset.Multiplier;
                    viewOffset.Multiplier = 0f;
                }
                else
                {
                    viewOffset.Multiplier = _prevViewOffsetWeight;
                }
            }

            if (mixer.TryGetMotion<OffsetMotion>(out var offsetMotion))
                offsetMotion.IgnoreParentMultiplier = enable;
        }

        /// <summary>
        /// Applies or removes aiming-related camera effects.
        /// </summary>
        /// <param name="enable">True to apply motion changes, false to reset.</param>
        private void ApplyAimCameraEffects(bool enable)
        {
            var motion = Wieldable.Motion.HeadComponents;

            if (enable)
            {
                motion.Data.PushProfile(_headMotionProfile);
                motion.Mixer.WeightMultiplier = _motionDampeningFactor;
            }
            else
            {
                motion.Data.PopProfile(_headMotionProfile);
                motion.Mixer.WeightMultiplier = 1f;
            }
        }

        /// <summary>
        /// Adjusts the field of view (FOV) for aiming.
        /// </summary>
        /// <param name="enable">True to apply FOV changes, false to reset.</param>
        private void ApplyFieldOfView(bool enable)
        {
            if (_wieldableFOV == null)
                return;

            var fovParams = GetFieldOfViewParameters(enable);
            _wieldableFOV.SetViewModelFOV(fovParams.HandsMultiplier, fovParams.SetDuration, fovParams.HandsDelay);
            _wieldableFOV.SetCameraFOV(fovParams.HeadMultiplier, fovParams.SetDuration, fovParams.HeadDelay);
        }

        /// <summary>
        /// Returns the appropriate FOV settings based on aiming state.
        /// </summary>
        /// <param name="enable">True if aiming is enabled, false otherwise.</param>
        /// <returns>The appropriate FOV settings.</returns>
        protected virtual FieldOfViewParams GetFieldOfViewParameters(bool enable)
        {
            return enable
                ? new FieldOfViewParams(_fovSetDuration, 0f, _handsFOVMod, 0f, _headFOVMod)
                : new FieldOfViewParams(_fovSetDuration, 0f, 1f, 0f, 1f);
        }

        /// <summary>
        /// Initializes the wieldable field of view component.
        /// </summary>
        protected override void Awake()
        {
            base.Awake();
            _wieldableFOV = Wieldable.gameObject.GetComponentInFirstChildren<WieldableFOV>();
        }

        #region Internal Types

        protected readonly struct FieldOfViewParams
        {
            public readonly float SetDuration;
            public readonly float HandsDelay;
            public readonly float HandsMultiplier;
            public readonly float HeadDelay;
            public readonly float HeadMultiplier;

            public FieldOfViewParams(float setDuration, float handsDelay, float handsMultiplier, float headDelay,
                float headMultiplier)
            {
                SetDuration = setDuration;
                HandsDelay = handsDelay;
                HandsMultiplier = handsMultiplier;
                HeadDelay = headDelay;
                HeadMultiplier = headMultiplier;
            }
        }

        #endregion
    }
}