using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    public sealed class ColorTween : Tween<Color>
    {
        protected override Color InterpolateValue(in Color startValue, in Color endValue, float progress)
        {
            return new Color(r: Mathf.LerpUnclamped(startValue.r, endValue.r, progress),
                g: Mathf.LerpUnclamped(startValue.g, endValue.g, progress),
                b: Mathf.LerpUnclamped(startValue.b, endValue.b, progress),
                a: Mathf.LerpUnclamped(startValue.a, endValue.a, progress));
        }
    }
}