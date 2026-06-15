using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    [Serializable]
    public sealed class NoiseMotionData : MotionData
    {
        [Range(0f, 5f)]
        public float NoiseSpeed = 1f;

        [Range(0f, 1f)]
        public float NoiseJitter;

        [SpaceArea]
        public Vector3 PositionAmplitude = Vector3.zero;
        
        public Vector3 RotationAmplitude = Vector3.zero;
    }
}