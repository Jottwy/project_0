using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    public sealed class QuaternionTween : Tween<Quaternion>
    {
        protected override Quaternion InterpolateValue(in Quaternion startValue, in Quaternion endValue, float progress)
        {
            return Quaternion.LerpUnclamped(startValue, endValue, progress);
            ;
        }
    }
}