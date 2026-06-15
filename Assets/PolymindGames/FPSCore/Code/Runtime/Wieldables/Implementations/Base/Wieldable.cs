using PolymindGames.MovementSystem;
using System.Collections;
using System.Diagnostics;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    using Debug = UnityEngine.Debug;

    /// <summary>
    /// Represents a wieldable item that can be equipped, holstered, and interacted with in the game.
    /// Implements interfaces for handling crosshair display and movement speed adjustments.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public abstract class Wieldable : MonoBehaviour, IWieldable, ICrosshairHandler, IMovementSpeedHandler
    {
        [OnValueChanged(nameof(Editor_CrosshairChanged))]
        [SerializeField, Range(-1, 100), NewLabel("Default Crosshair")]
        [Tooltip("Index of the default crosshair for this wieldable. Use -1 or lower for no crosshair.")]
        private int _baseCrosshair;

        [SerializeField, Range(0f, 5f), Title("Equipping")]
        [Tooltip("Duration of the equipping animation.")]
        private float _equipDuration = 0.5f;

        [SerializeField]
        [Tooltip("Audio sequence played during equipping.")]
        private AudioSequence _equipAudio;

        [SerializeField, Range(0f, 5f), Title("Holstering")]
        [Tooltip("Duration of the holstering animation.")]
        private float _holsterDuration = 0.5f;

        [SerializeField]
        [Tooltip("Audio sequence played during holstering.")]
        private AudioSequence _holsterAudio;

        private WieldableStateType _state;
        private bool _isGeometryVisible = true;
        private Renderer[] _renderers;

        public ICharacter Character { get; private set; }
        public IAnimatorController Animator { get; private set; }
        public IWieldableMotion Motion { get; private set; }
        public ICharacterAudioPlayer Audio { get; private set; }

        public bool IsGeometryVisible
        {
            get => _isGeometryVisible;
            set
            {
                if (_isGeometryVisible == value)
                    return;

                _renderers ??= GetComponentsInChildren<Renderer>();
                if (_renderers.Length == 0)
                {
                    Debug.LogError("This wieldable has no renderers.");
                    return;
                }

                foreach (var rend in _renderers)
                    rend.enabled = value;

                _isGeometryVisible = value;
            }
        }

        public WieldableStateType State
        {
            get => _state;
            private set
            {
                _state = value;
                OnStateChanged(value);
            }
        }

        void IWieldable.SetCharacter(ICharacter character)
        {
            if (Character == null || character == Character)
            {
                Character = character;
                Audio ??= Character.Audio;
                OnCharacterChanged(Character);
            }
            else
            {
                Debug.LogError("The parent character has been changed, this is not supported in the current version.", gameObject);
            }
        }

        IEnumerator IWieldable.Equip()
        {
            if (State is WieldableStateType.Equipping or WieldableStateType.Equipped)
                yield break;

            gameObject.SetActive(true);

            State = WieldableStateType.Equipping;
            Animator.ResetTrigger(AnimationConstants.Holster);
            Animator.SetTrigger(AnimationConstants.Equip);
            
            Audio ??= GetComponent<ICharacterAudioPlayer>();
            Audio.PlayClips(_equipAudio, BodyPoint.Hands);

            for (float timer = Time.time + _equipDuration; timer > Time.time;)
                yield return null;

            if (Character.TryGetCC(out IMovementControllerCC movement))
                movement.SpeedModifier.AddModifier(SpeedModifier.EvaluateValue);

            State = WieldableStateType.Equipped;
        }

        IEnumerator IWieldable.Holster(float holsterSpeed)
        {
            if (State is WieldableStateType.Holstering or WieldableStateType.Hidden)
                yield break;

            State = WieldableStateType.Holstering;

            bool instantHolster = holsterSpeed >= IWieldable.MaxHolsterSpeed;

            if (!instantHolster)
            {
                Audio ??= GetComponent<ICharacterAudioPlayer>();
                Audio.PlayClips(_holsterAudio, BodyPoint.Hands, holsterSpeed);
                
                Animator.SetTrigger(AnimationConstants.Holster);
                Animator.SetFloat(AnimationConstants.HolsterSpeed, holsterSpeed);

                for (float timer = Time.time + _holsterDuration / holsterSpeed; timer > Time.time;)
                    yield return null;
            }

            if (Character.TryGetCC(out IMovementControllerCC movement))
                movement.SpeedModifier.RemoveModifier(SpeedModifier.EvaluateValue);

            gameObject.SetActive(false);
            State = WieldableStateType.Hidden;
        }

        protected virtual void OnStateChanged(WieldableStateType state) { }
        protected virtual void OnCharacterChanged(ICharacter character) { }

        private void Awake()
        {
            _currentCrosshairIndex = _baseCrosshair;
            State = WieldableStateType.Hidden;
            Animator = GetAnimator();
            Motion = GetMotion();
            gameObject.SetActive(false);
        }

        private IAnimatorController GetAnimator()
        {
            var animators = gameObject.GetComponentsInChildren<IAnimatorController>(false);
            return animators.Length switch
            {
                0 => NullAnimator.Instance,
                1 => animators[0],
                _ => new MultiAnimator(animators)
            };
        }

        private IWieldableMotion GetMotion()
        {
            var motion = gameObject.GetComponentInFirstChildren<IWieldableMotion>();

            if (motion == null)
                Debug.LogError("No motion handler found under this wieldable!", gameObject);

            return motion;
        }

        #region Movement Speed
        public MovementModifierGroup SpeedModifier { get; } = new();
        #endregion

        #region Accuracy
        public float CrosshairCharge { get; set; } = 0f;
        public float Accuracy { get; protected set; }

        private int _currentCrosshairIndex;

        public int CrosshairIndex
        {
            get => _currentCrosshairIndex;
            set
            {
                if (_currentCrosshairIndex == value)
                    return;

                _currentCrosshairIndex = value;
                CrosshairChanged?.Invoke(value);
            }
        }

        public event UnityAction<int> CrosshairChanged;
        public void ResetCrosshair() => CrosshairIndex = _baseCrosshair;
        public virtual bool IsCrosshairActive() => true;

        [Conditional("UNITY_EDITOR")]
        protected void Editor_CrosshairChanged()
        {
            if (Application.isPlaying)
                CrosshairIndex = _baseCrosshair;
        }
        #endregion

        #region Debug
#if UNITY_EDITOR
        private static bool? _isDebugMode;

        public static bool IsDebugMode
        {
            get
            {
                _isDebugMode ??= UnityEditor.EditorPrefs.GetBool("WieldableDebug", false);
                return _isDebugMode.Value;
            }
        }

        public static void EnableDebugMode(bool enable)
        {
            _isDebugMode = enable;
            UnityEditor.EditorPrefs.SetBool("WieldableDebug", enable);
        }

        private void OnGUI()
        {
            if (IsDebugMode)
                DrawDebugGUI();
        }

        protected virtual void DrawDebugGUI() { }
#endif
        #endregion
    }
}