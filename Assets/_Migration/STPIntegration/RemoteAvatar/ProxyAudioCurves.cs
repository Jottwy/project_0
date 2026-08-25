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

        /// <summary>
        /// ADR-094 Enmienda 10 — AIR ABSORPTION, as a low-pass cutoff over distance.
        ///
        /// Play-test (Joel, 2026-08-25): "la curva de sonido... ahora siento muy plano, no se
        /// siente distanciado". The rolloff above was already correct and was never the problem —
        /// the problem is that DISTANCE IS NOT VOLUME. A sound forty metres away has lost its
        /// treble on the way: air absorbs high frequencies far faster than low ones, which is why
        /// distant thunder rumbles and near thunder cracks. Attenuate only the level and the ear
        /// hears a near sound turned down, which is exactly the flatness being described.
        ///
        /// The cutoff reached at <paramref name="maxDistance"/> is derived FROM that distance
        /// rather than authored: a bank that only carries eleven metres has barely any air to
        /// lose treble to, while one that carries eighty has a great deal. So the same rule gives
        /// a whisper an almost unfiltered curve and the chant a very dark one, without either
        /// having to be tuned by hand.
        ///
        /// Interpolated in LOG space, for the same reason <c>FacelingDazeEffect</c> is: perceived
        /// "muffledness" is logarithmic, and a linear ramp between 700 and 22000 Hz spends nearly
        /// its whole length sounding unfiltered.
        /// </summary>
        public static AnimationCurve BuildAirAbsorptionCurve(float maxDistance, int samples = 24)
        {
            maxDistance = Mathf.Max(0.01f, maxDistance);

            // 22 m e-folding: roughly where a corridor's worth of air becomes audible as colour.
            // Floored at 700 Hz — past that it stops sounding distant and starts sounding broken.
            float atMax = Mathf.Clamp(20000f * Mathf.Exp(-maxDistance / 22f), 700f, 12000f);

            var curve = new AnimationCurve();
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                curve.AddKey(t, Mathf.Exp(Mathf.Lerp(Mathf.Log(22000f), Mathf.Log(atMax), t)));
            }

            for (int i = 0; i < curve.length; i++)
                curve.SmoothTangents(i, 0f);

            return curve;
        }

        /// <summary>
        /// ADR-094 Enmienda 10 — how WIDE the source reads, over distance. The second half of the
        /// distance cue: a voice at arm's length is a point you can turn to, and one down the
        /// corridor has bounced off enough surfaces to arrive from a general direction rather
        /// than a spot.
        ///
        /// Deliberately stops well short of the 360° Unity allows. The pack has to remain
        /// LOCATABLE — the whole encounter is about working out how many are behind you and
        /// where — so this blurs the far field without ever dissolving it.
        /// </summary>
        public static AnimationCurve BuildSpreadCurve(float maxSpreadDegrees = 45f)
        {
            var curve = new AnimationCurve();
            for (int i = 0; i <= 8; i++)
            {
                float t = i / 8f;
                // Held near zero up close, so nothing changes in the range where you are being
                // circled and need to point at them.
                curve.AddKey(t, Mathf.Pow(t, 1.7f) * (maxSpreadDegrees / 360f));
            }

            for (int i = 0; i < curve.length; i++)
                curve.SmoothTangents(i, 0f);

            return curve;
        }

        /// <summary>
        /// Applies the air-absorption cutoff and the spread widening to a source. The low-pass is
        /// added to the source's own GameObject if it is not there yet.
        ///
        /// Unity evaluates both curves over the SAME normalized distance axis as the rolloff, so
        /// this costs nothing per frame — no component polls, the engine reads the curve.
        /// </summary>
        public static void ApplyDistanceColour(AudioSource source, float maxDistance,
            float maxSpreadDegrees = 45f)
        {
            if (source == null)
                return;

            var lp = source.GetComponent<AudioLowPassFilter>();
            if (lp == null)
                lp = source.gameObject.AddComponent<AudioLowPassFilter>();

            lp.customCutoffCurve = BuildAirAbsorptionCurve(maxDistance);
            lp.enabled = true;

            source.SetCustomCurve(AudioSourceCurveType.Spread, BuildSpreadCurve(maxSpreadDegrees));
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
