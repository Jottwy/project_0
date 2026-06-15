using System;
using UnityEngine;

namespace PolymindGames
{
    [Serializable]
    public sealed class StatDepletionSettings
    {
        private enum DepletionType
        {
            IncreaseValue,
            DecreaseValue
        }

        private enum ValueThresholdType
        {
            BiggerThan,
            SmallerThan,
            None
        }

        [SerializeField]
        private DepletionType _depletionType = DepletionType.DecreaseValue;

        [SerializeField, Range(0f, 100f)]
        private float _depletionSpeed = 0.05f;

        [SerializeField, SpaceArea]
        [Help("After the stat value crosses this threshold the character will start taking damage.", UnityMessageType.None)]
        private ValueThresholdType _damageThresholdType = ValueThresholdType.SmallerThan;

        [SerializeField, Range(0f, 1000f)]
        private float _damageValueThreshold = 5f;

        [SerializeField, Range(0f, 100f)]
        private float _damage = 3f;

        [SerializeField, SpaceArea]
        [Help("After the stat value crosses this threshold the character's health will start to restore", UnityMessageType.None)]
        private ValueThresholdType _healThresholdType = ValueThresholdType.BiggerThan;

        [SerializeField, Range(0f, 1000f)]
        private float _healValueThreshold = 95f;

        [SerializeField, Range(0f, 100f)]
        private float _healthRestore = 3f;

        public void UpdateStat(ref float statValue, float maxStatValue, float deltaTime, IHealthManager health) 
        {
            float depletion = _depletionType == DepletionType.IncreaseValue ? _depletionSpeed * deltaTime : -(_depletionSpeed * deltaTime);

            statValue = Mathf.Clamp(statValue + depletion, 0, maxStatValue);

            // Apply damage
            switch (_damageThresholdType)
            {
                case ValueThresholdType.BiggerThan:
                    if (statValue > _damageValueThreshold)
                        health.ReceiveDamage(_damage * deltaTime);
                    break;
                case ValueThresholdType.SmallerThan:
                    if (statValue < _damageValueThreshold)
                        health.ReceiveDamage(_damage * deltaTime);
                    break;
                case ValueThresholdType.None:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            // Restore health
            switch (_healThresholdType)
            {
                case ValueThresholdType.BiggerThan:
                    if (statValue > _healValueThreshold)
                        health.RestoreHealth(_healthRestore * deltaTime);
                    break;
                case ValueThresholdType.SmallerThan:
                    if (statValue < _healValueThreshold)
                        health.RestoreHealth(_healthRestore * deltaTime);
                    break;
                case ValueThresholdType.None:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}