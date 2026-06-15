using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    public sealed class Vector3Tween : Tween<Vector3>
    {
        protected override Vector3 InterpolateValue(in Vector3 startValue, in Vector3 endValue, float progress)
        {
            return new Vector3(x: Mathf.LerpUnclamped(startValue.x, endValue.x, progress), 
                y: Mathf.LerpUnclamped(startValue.y, endValue.y, progress),
                z: Mathf.LerpUnclamped(startValue.z, endValue.z, progress));
        }
    }
}