using PolymindGames.ProceduralMotion;
using UnityEngine.Animations;
using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    [RequireComponent(typeof(ParentConstraint))]
    public sealed class WieldableArmsHandler : MonoBehaviour, IWieldableArmsHandlerCC
    {
        [SerializeField, NotNull]
        private Animator _animator; 

        [SpaceArea]
        [SerializeField, ReorderableList]
        private ArmSet[] _armSets;

        private ParentConstraint _parentConstraint; 
        private IMotionMixer _mixer;
        private int _selectedArmsIndex;
        private bool _isVisible = true;
        private int _instanceId;

        public Animator Animator => _animator;

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                _isVisible = value;
                _armSets[_selectedArmsIndex].Enable(value);
            }
        }

        public void EnableArms()
        {
            gameObject.SetActive(true);
            
            var source = new ConstraintSource
            {
                weight = 1f,
                sourceTransform = _mixer.TargetTransform
            };

            _parentConstraint.constraintActive = true;
            if (_parentConstraint.sourceCount == 0)
            {
                _parentConstraint.AddSource(source);
            }
            else
            {
                _parentConstraint.SetSource(0, source);
            }
        }

        public void DisableArms()
        {
            _animator.Rebind();
            gameObject.SetActive(false);
            _parentConstraint.constraintActive = false;
        }

        public void ToggleNextArmSet()
        {
            var prevArms = _armSets[_selectedArmsIndex];
            var arms = _armSets.Select(ref _selectedArmsIndex, SelectionType.Sequence);

            prevArms.Enable(false);
            arms.Enable(_isVisible);
        }

        private void Awake()
        {
            if (_armSets.Length == 0)
            {
                Debug.LogError("No arm sets assigned.", gameObject);
                return;
            }

            _armSets[0].Enable(true);
            for (int i = 1; i < _armSets.Length; i++)
            {
                _armSets[i].Enable(false);
            }

            _parentConstraint = GetComponent<ParentConstraint>();
            _mixer = GetComponentInParent<IMotionMixer>();
            
            gameObject.SetActive(false);
        }

        #region Internal Types
        [Serializable]
        private struct ArmSet
        {
            public string Name;
            public SkinnedMeshRenderer LeftArm;
            public SkinnedMeshRenderer RightArm;

            public void Enable(bool enable)
            {
                LeftArm.gameObject.SetActive(enable);
                RightArm.gameObject.SetActive(enable);
            }
        }
        #endregion
    }
}