using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    [Serializable]
    public sealed class CurvesMotionData
    {
        [SerializeField]
        public SpringSettings PositionSpring = SpringSettings.Default;

        [SerializeField]
        public AnimCurves3D PositionCurves;

        [SerializeField, SpaceArea]
        public SpringSettings RotationSpring = SpringSettings.Default;

        [SerializeField]
        public AnimCurves3D RotationCurves;
    }
}