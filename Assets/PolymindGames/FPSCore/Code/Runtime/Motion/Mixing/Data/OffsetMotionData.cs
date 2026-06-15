using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    [Serializable]
    public sealed class OffsetMotionData : MotionData
    {
        public SpringSettings PositionSpring = SpringSettings.Default;

        public SpringSettings RotationSpring = SpringSettings.Default;

        [SpaceArea]
        public SpringForce3D EnterForce;

        public SpringForce3D ExitForce;

        [SpaceArea]
        public Vector3 PositionOffset;

        public Vector3 RotationOffset;
    }
}