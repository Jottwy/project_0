using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    [Serializable]
    public sealed class SingleValueMotionData
    {
        [SerializeField]
        public SpringSettings PositionSpring = SpringSettings.Default;

        [SerializeField]
        public SpringSettings RotationSpring = SpringSettings.Default;

        [SerializeField, Range(-100f, 100f), SpaceArea]
        public float PositionValue;

        [SerializeField, Range(-100f, 100f)]
        public float RotationValue;
    }
}