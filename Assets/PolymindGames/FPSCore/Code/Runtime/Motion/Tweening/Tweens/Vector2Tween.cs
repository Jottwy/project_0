using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    public sealed class Vector2Tween : Tween<Vector2>
    {
        protected override Vector2 InterpolateValue(in Vector2 startValue, in Vector2 endValue, float progress)
        {
            return new Vector2(x: Mathf.LerpUnclamped(startValue.x, endValue.x, progress),
                y: Mathf.LerpUnclamped(startValue.y, endValue.y, progress));
        }
    }
}