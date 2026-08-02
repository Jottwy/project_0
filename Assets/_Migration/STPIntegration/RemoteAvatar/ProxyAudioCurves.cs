using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-042 play-test fix (2026-08-02): shared rolloff curve for the proxy audio hooks.
    ///
    /// THE UNITY TRAP THIS EXISTS TO WORK AROUND. With <see cref="AudioRolloffMode.Logarithmic"/>,
    /// <c>maxDistance</c> is NOT a cutoff — it is where the attenuation STOPS. The volume falls as
    /// <c>minDistance / distance</c> up to <c>maxDistance</c> and then stays pinned at that value for
    /// ever. With min 1.5 and max 22 that floor is ~6.8 %, so a footstep never actually went silent: it
    /// just got quiet and then followed the peer across the entire level at a constant level. Same trap
    /// on the gunshot. Lowering <c>maxDistance</c> does not fix it — it RAISES the floor, because the
    /// floor is exactly <c>min/max</c>. That is why "make it quieter" kept not working.
    ///
    /// The fix is a CUSTOM curve that genuinely reaches zero. It keeps the physical <c>1/d</c> shape
    /// (so the near and mid field sound unchanged) and then windows the last stretch down to a true 0
    /// at <c>maxDistance</c> with a smoothstep — a hard clip there would click on anything still
    /// ringing, and a linear ramp over the whole range would gut the mid field.
    ///
    /// Unity evaluates a custom rolloff over a NORMALIZED axis: time 0 is the listener and time 1 is
    /// <c>maxDistance</c>. <c>minDistance</c> is ignored by the engine in this mode, so it is baked
    /// into the curve here instead.
    /// </summary>
    internal static class ProxyAudioCurves
    {
        /// <summary>
        /// Builds a <c>1/d</c> rolloff that is exactly 0 at <paramref name="maxDistance"/>.
        /// </summary>
        /// <param name="minDistance">Full-volume radius, in metres.</param>
        /// <param name="maxDistance">Distance at which the sound is SILENT (not merely quiet).</param>
        /// <param name="fadeStart">Normalized distance where the fade-to-zero window begins. Earlier =
        /// a softer, more gradual disappearance; later = truer physical falloff but a more abrupt end.</param>
        /// <param name="samples">Curve resolution. 32 is smooth well below what the ear resolves.</param>
        public static AnimationCurve BuildHardCutoffRolloff(float minDistance, float maxDistance,
            float fadeStart = 0.55f, int samples = 32)
        {
            maxDistance = Mathf.Max(0.01f, maxDistance);
            minDistance = Mathf.Clamp(minDistance, 0.01f, maxDistance);
            fadeStart = Mathf.Clamp(fadeStart, 0.05f, 0.95f);

            var curve = new AnimationCurve();
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;      // normalized distance, 1 == maxDistance
                float d = t * maxDistance;

                float v = d <= minDistance ? 1f : minDistance / d;

                if (t > fadeStart)
                {
                    float w = (t - fadeStart) / (1f - fadeStart);
                    v *= Mathf.SmoothStep(1f, 0f, w);
                }

                curve.AddKey(t, i == samples ? 0f : v); // pin the last key to an exact zero
            }

            for (int i = 0; i < curve.length; i++)
                curve.SmoothTangents(i, 0f);

            return curve;
        }

        /// <summary>Applies the curve to a source and switches it into Custom rolloff mode.</summary>
        public static void ApplyHardCutoff(AudioSource source, float minDistance, float maxDistance,
            float fadeStart = 0.55f)
        {
            source.minDistance = minDistance; // ignored by the engine in Custom mode; kept for the inspector
            source.maxDistance = maxDistance;
            source.rolloffMode = AudioRolloffMode.Custom;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff,
                BuildHardCutoffRolloff(minDistance, maxDistance, fadeStart));
        }
    }
}
