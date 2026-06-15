using UnityEngine;
using System;

namespace PolymindGames
{
    [Serializable]
    public abstract class AnimatorTranslatorData : ISerializationCallbackReceiver, IComparable<AnimatorTranslatorData>
    {
        [NonSerialized]
        public int Hash;

        [DisableInPlayMode]
        public AnimatorControllerParameterType ParamType = AnimatorControllerParameterType.Trigger;

        [AnimatorParameter(nameof(ParamType))]
        public string ParamName;

        [DisableInPlayMode]
        [HideIf(nameof(ParamType), AnimatorControllerParameterType.Trigger)]
        public int TargetValue;

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (Hash == 0)
                Hash = Animator.StringToHash(ParamName);

            if (ParamType == AnimatorControllerParameterType.Trigger)
                TargetValue = 0;
        }

        public int CompareTo(AnimatorTranslatorData other) => ParamType.CompareTo(other.ParamType);
    }

    public abstract class AnimatorEffectTranslator<AnimationData> : MonoBehaviour, IAnimatorController where AnimationData : AnimatorTranslatorData
    {
        [LabelByChild(nameof(AnimatorTranslatorData.ParamName))]
        [SerializeField, ReorderableList(elementLabel: "Animation")]
        private AnimationData[] _animations;

        private int _integerEndIndex;
        private int _boolEndIndex;
        private int _triggerStartIndex;
        private bool _isAnimating;

        public bool IsAnimating
        {
            get => _isAnimating;
            set => _isAnimating = value;
        }

        public bool IsVisible
        {
            get => true;
            set { }
        }

        public void SetFloat(int id, float value)
        {
            int intValue = Mathf.FloorToInt(value);
            SetInteger(id, intValue);
        }

        public void SetInteger(int id, int value)
        {
            if (_isAnimating && _integerEndIndex != -1)
                Play(id, _animations.AsSpan(0, _integerEndIndex + 1), value);
        }

        public void SetBool(int id, bool value)
        {
            if (_isAnimating && _boolEndIndex != -1)
                Play(id, _animations.AsSpan(_integerEndIndex + 1, _boolEndIndex - _integerEndIndex), value ? 1 : 0);
        }

        public void SetTrigger(int id)
        {
            if (_isAnimating && _triggerStartIndex != -1)
                Play(id, _animations.AsSpan(_triggerStartIndex), 0);
        }

        public void ResetTrigger(int id) { }

        protected abstract void PlayAnimation(AnimationData animationData);

        private void Play(int id, Span<AnimationData> animations, int value)
        {
            foreach (var animData in animations)
            {
                if (animData.Hash == id && animData.TargetValue == value)
                {
                    PlayAnimation(animData);
                    return;
                }
            }
        }

        protected virtual void Awake()
        {
            _isAnimating = true;
            CalculateIndexes();
        }

        private void CalculateIndexes()
        {
            _integerEndIndex = Array.FindLastIndex(_animations, data => data.ParamType is AnimatorControllerParameterType.Float or AnimatorControllerParameterType.Int);
            _boolEndIndex = Array.FindLastIndex(_animations, data => data.ParamType is AnimatorControllerParameterType.Bool);
            _triggerStartIndex = Array.FindIndex(_animations, data => data.ParamType is AnimatorControllerParameterType.Trigger);
        }

        #region Editor
#if UNITY_EDITOR
        protected virtual void OnValidate()
        {
            if (_animations != null)
            {
                Array.Sort(_animations);

                foreach (var anim in _animations)
                {
                    if ((int)anim.ParamType > (int)AnimatorControllerParameterType.Bool)
                        return;
                    else if (anim.ParamType == AnimatorControllerParameterType.Bool)
                        anim.TargetValue = (int)Mathf.Clamp01(anim.TargetValue);
                }
            }
            else if (Application.isPlaying)
            {
                CalculateIndexes();
            }
        }
#endif
        #endregion
    }
}