using PolymindGames.ProceduralMotion;
using UnityEngine;
using UnityEngine.Serialization;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Charge Trigger")]
    public class FirearmChargeTrigger : FirearmTriggerBehaviour
    {
        [SerializeField, Range(0f, 10f)]
        [Tooltip("The minimum time that can pass between consecutive shots.")]
        private float _pressCooldown;

        [SerializeField, Range(0f, 0.95f)]
        [Tooltip("Minimum charge needed to shoot")]
        private float _minChargeTime;

        [SerializeField, Range(0f, 10f)]
        private float _maxChargeTime = 1f;

        [SerializeField, Range(0f, 2f), Title("Field of View")]
        private float _fovSetDuration = 0.4f;

        [SerializeField, Range(0f, 2f)]
        private float _worldFOVMod = 0.75f;

        [SerializeField, Range(0f, 2f)]
        private float _overlayFOVMod = 0.75f;

        [SerializeField, Range(0f, 1f), Title("Motion")]
        [Tooltip("Multiplier to reduce the intensity of the weapon's and camera's motion when aiming.")]
        private float _motionDampeningFactor = 0.5f;

        [FormerlySerializedAs("_motionOffset")]
        [SerializeField, NewLabel("Hands Offset (Wieldable)")]
        [Tooltip("Offset for adjusting the weapon's position during aiming.")]
        private OffsetMotionData _handsOffset;

        [SerializeField]
        [NewLabel("Head Profile (Camera)")]
        [Tooltip("List of camera motions applied when aiming.")]
        private MotionProfile _headMotionProfile;

        [SerializeField, Title("Audio")]
        private AudioData _chargeStartAudio = new(null);

        [SerializeField]
        private AudioData _chargeCancelAudio = new(null);

        [SerializeField]
        private AudioData _chargeMaxAudio = new(null);

        [SerializeField, Title("Misc")]
        private ProjectilePathVisualizer _projectilePathVisualizer;

        private ICrosshairHandler _crosshairHandler;
        private float _prevViewOffsetWeight;
        private WieldableFOV _fovHandler;
        private float _triggerChargeStartTime;
        private bool _triggerChargeStarted;
        private float _canHoldTimer;
        private bool _chargeMaxed;
        private float _chargeAudioTimer;

        public override void HoldTrigger()
        {
            if (Time.time < _canHoldTimer || Firearm.ReloadableMagazine.IsMagazineEmpty())
                return;

            IsTriggerHeld = true;

            if (!_triggerChargeStarted && GetChargeLevel() > _minChargeTime)
                StartCharge();

            if (!_chargeMaxed && GetChargeLevel() > _maxChargeTime - 0.01f)
                HandleChargeMaxed();
        }

        public override void ReleaseTrigger()
        {
            if (!IsTriggerHeld)
                return;

            if (Firearm is IUseInputHandler useHandler && useHandler.UseBlocker.IsBlocked || GetChargeLevel() < _minChargeTime)
                CancelCharge();
            else
                FireChargedShot();

            ResetCharge();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            ResetCharge();
        }

        private void OnDisable()
        {
            _crosshairHandler.CrosshairCharge = 0f;
        }

        protected override void Awake()
        {
            base.Awake();
            _crosshairHandler = Wieldable as ICrosshairHandler;
            _fovHandler = Wieldable.gameObject.GetComponentInFirstChildren<WieldableFOV>();
        }

        private void Update()
        {
            TriggerCharge = GetChargeLevel();

            if (TriggerCharge > _minChargeTime && _projectilePathVisualizer != null)
            {
                if (!_projectilePathVisualizer.IsEnabled)
                    _projectilePathVisualizer.Enable();

                var context = Firearm.FireSystem.GetLaunchContext();
                _projectilePathVisualizer.UpdateContext(context.Velocity.magnitude, context.Gravity, context.LayerMask);
            }

            _crosshairHandler.CrosshairCharge = TriggerCharge;
        }

        private void ResetCharge()
        {
            _triggerChargeStarted = false;
            _chargeMaxed = false;
            _canHoldTimer = Time.time + _pressCooldown;
            IsTriggerHeld = false;
        }

        /// <summary>
        /// Handles the start of the charge process, applying FOV changes, animations, and audio.
        /// </summary>
        private void StartCharge()
        {
            _fovHandler.SetViewModelFOV(_overlayFOVMod, _fovSetDuration * 1.1f);
            _fovHandler.SetCameraFOV(_worldFOVMod, _fovSetDuration);

            Wieldable.Animator.SetBool(AnimationConstants.IsCharging, true);

            PlayChargeAudio(_chargeStartAudio, 0.35f);

            _triggerChargeStarted = true;
            _triggerChargeStartTime = Time.time;

            ApplyChargeOffset(true);
            ApplyChargeCameraEffects(true);
        }

        /// <summary>
        /// Handles stopping the charge, resetting FOV and animations.
        /// </summary>
        private void StopCharge()
        {
            _fovHandler.SetViewModelFOV(1f, _fovSetDuration * 0.9f);
            _fovHandler.SetCameraFOV(1f, _fovSetDuration * 0.9f);

            Wieldable.Animator.SetBool(AnimationConstants.IsCharging, false);

            ApplyChargeOffset(false);
            ApplyChargeCameraEffects(false);

            if (_projectilePathVisualizer != null)
                _projectilePathVisualizer.Disable();
        }

        /// <summary>
        /// Handles firing after a full charge, resetting charge state and triggering the shoot animation.
        /// </summary>
        private void FireChargedShot()
        {
            StopCharge();

            var animator = Wieldable.Animator;
            animator.SetBool(AnimationConstants.IsEmpty, false);
            animator.SetTrigger(AnimationConstants.Shoot);

            RaiseShootEvent();
        }

        /// <summary>
        /// Handles reaching maximum charge, playing the max charge audio and triggering the animation.
        /// </summary>
        private void HandleChargeMaxed()
        {
            if (_chargeMaxAudio.IsPlayable)
                Wieldable.Audio.PlayClip(_chargeMaxAudio, BodyPoint.Hands);

            Wieldable.Animator.SetTrigger(AnimationConstants.FullCharge);
            _chargeMaxed = true;
        }

        /// <summary>
        /// Cancels the charge, resetting effects and playing cancellation audio.
        /// </summary>
        private void CancelCharge()
        {
            StopCharge();
            PlayChargeAudio(_chargeCancelAudio, 0.35f);
        }

        /// <summary>
        /// Returns the current charge level as a normalized value between 0.05 and 1.
        /// </summary>
        private float GetChargeLevel()
        {
            if (!IsTriggerHeld)
                return 0f;

            return Mathf.Clamp((Time.time - _triggerChargeStartTime) / _maxChargeTime, 0.05f, 1f);
        }

        /// <summary>
        /// Adjusts weapon offset motion when charging.
        /// </summary>
        /// <param name="enable">True to apply offset, false to reset.</param>
        private void ApplyChargeOffset(bool enable)
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

            mixer.GetMotion<OffsetMotion>().IgnoreParentMultiplier = enable;
        }

        /// <summary>
        /// Adjusts head motion when charging.
        /// </summary>
        /// <param name="enable">True to apply motion changes, false to reset.</param>
        private void ApplyChargeCameraEffects(bool enable)
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
        /// Plays a charge-related audio clip with a cooldown to prevent spamming.
        /// </summary>
        /// <param name="audioClip">The audio clip to play.</param>
        /// <param name="cooldown">The cooldown duration before another sound can be played.</param>
        private void PlayChargeAudio(in AudioData audioClip, float cooldown)
        {
            if (_chargeAudioTimer < Time.time)
            {
                Wieldable.Audio.PlayClip(audioClip, BodyPoint.Hands);
                _chargeAudioTimer = Time.time + cooldown;
            }
        }
    }
}