using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    public sealed class FloatTween : Tween<float>
    {
        protected override float InterpolateValue(in float startValue, in float endValue, float progress)
        {
            return Mathf.LerpUnclamped(startValue, endValue, progress);
        }
    }
}