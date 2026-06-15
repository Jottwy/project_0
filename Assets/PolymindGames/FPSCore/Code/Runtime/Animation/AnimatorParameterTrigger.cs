using System.Runtime.CompilerServices;
using UnityEngine;
using System;

namespace PolymindGames
{
    [Serializable]
    public sealed class AnimatorParameterTrigger : ISerializationCallbackReceiver
    {
        [SerializeField]
        public AnimatorControllerParameterType Type;

        [SerializeField]
        public string Name;

        [SerializeField]
        public float Value;

        [NonSerialized]
        public int Hash;

        public AnimatorParameterTrigger(AnimatorControllerParameterType type, string name, float value)
        {
            Type = type;
            Name = name;
            Value = value;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize() => CreateHash();
        void ISerializationCallbackReceiver.OnAfterDeserialize() => CreateHash();

        public void TriggerParameter(Animator animator)
        {
            switch (Type)
            {
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(Hash, Value);
                    break;
                case AnimatorControllerParameterType.Trigger:
                    animator.SetTrigger(Hash);
                    break;
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(Hash, Value > 0f);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(Hash, (int)Value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public void TriggerParameter(Animator animator, float valueMod)
        {
            float value = Value * valueMod;

            switch (Type)
            {
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(Hash, value);
                    break;
                case AnimatorControllerParameterType.Trigger:
                    animator.SetTrigger(Hash);
                    break;
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(Hash, value > 0f);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(Hash, (int)value);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateHash()
        {
            if (Hash == 0)
                Hash = Animator.StringToHash(Name);
        }
    }
}