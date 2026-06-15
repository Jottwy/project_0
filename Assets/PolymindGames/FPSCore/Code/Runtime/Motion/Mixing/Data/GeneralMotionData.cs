using UnityEngine;
using System;

namespace PolymindGames.ProceduralMotion
{
    [Serializable]
    public sealed class GeneralMotionData : MotionData
    {
        [SerializeField, BeginIndent]
        public SwayMotionData Look;

        [SerializeField]
        public SwayMotionData Strafe;

        [SerializeField]
        public CurvesMotionData Jump;

        [SerializeField]
        public CurvesMotionData Land;

        [SerializeField, EndIndent]
        public SingleValueMotionData Fall;
    }
}