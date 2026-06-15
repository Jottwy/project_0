using UnityEngine;

namespace PolymindGames
{
    public class CharacterAnimator : MonoBehaviour, IAnimatorController
    {
        [SerializeField, NotNull]
        private Animator _animator;

        public bool IsAnimating
        {
            get => _animator.speed != 0f;
            set => _animator.speed = value ? 1f : 0f;
        }

        public bool IsVisible
        {
            get => true;
            set { }
        }

        public void SetFloat(int id, float value) => _animator.SetFloat(id, value);
        public void SetBool(int id, bool value) => _animator.SetBool(id, value);
        public void SetInteger(int id, int value) => _animator.SetInteger(id, value);
        public void SetTrigger(int id) => _animator.SetTrigger(id);
        public void ResetTrigger(int id) => _animator.ResetTrigger(id);

        private void Reset()
        {
            _animator = GetComponent<Animator>();
        }
    }
}
