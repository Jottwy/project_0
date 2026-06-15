using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    [Serializable]
    public sealed class RetractionMotionData : MotionData
    {
        [Range(0.1f, 5f)]
        public float RetractionDistance = 0.55f;

        [SpaceArea]
        public SpringSettings PositionSpring = SpringSettings.Default;

        public Vector3 PositionOffset;

        [SpaceArea]
        public SpringSettings RotationSpring = SpringSettings.Default;

        public Vector3 RotationOffset;
    }
}