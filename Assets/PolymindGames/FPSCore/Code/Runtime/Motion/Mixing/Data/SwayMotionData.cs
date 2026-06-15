using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    [Serializable]
    public sealed class SwayMotionData
    {
        [SerializeField, Range(0f, 100f)]
        public float MaxSwayLength = 10f;

        [SerializeField, SpaceArea]
        public SpringSettings PositionSpring = SpringSettings.Default;

        [SerializeField]
        public Vector3 PositionSway;

        [SerializeField, SpaceArea]
        public SpringSettings RotationSpring = SpringSettings.Default;

        [SerializeField]
        public Vector3 RotationSway;
    }
}