using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    public enum BobMode
    {
        StepCycleBased,
        TimeBased
    }
    
    [Serializable]
    public sealed class BobMotionData : MotionData
    {
        public BobMode BobType = BobMode.StepCycleBased;

        [Range(0.01f, 10f)]
        [ShowIf(nameof(BobType), BobMode.TimeBased)]
        public float BobSpeed = 1f;

        [SpaceArea]
        public SpringSettings PositionSpring = SpringSettings.Default;
        public SpringForce3D PositionStepForce = SpringForce3D.Default;
        public Vector3 PositionAmplitude = Vector3.zero;

        [SpaceArea]
        public SpringSettings RotationSpring = SpringSettings.Default;
        public SpringForce3D RotationStepForce = SpringForce3D.Default;
        public Vector3 RotationAmplitude = Vector3.zero;
    }
}