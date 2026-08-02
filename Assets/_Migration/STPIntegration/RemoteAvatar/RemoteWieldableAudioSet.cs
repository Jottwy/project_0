using System;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-042: per-item fire sounds for remote proxies, authored by hand.
    ///
    /// Why this is an authored asset and not resolved automatically. The clip a firearm plays lives
    /// as a private <c>AudioData _fireAudio</c> on <c>FirearmBasicBarrelEffect</c>, a component of the
    /// FIRST-PERSON wieldable prefab, and it is played through <c>Wieldable.Audio</c> — the wielder's
    /// own audio player. Neither is reachable from someone else's proxy. Two automatic routes were
    /// considered and rejected in the ADR: <c>IWieldableInventoryCC.GetWieldableWithId</c> only knows
    /// the LOCAL player's registered wieldables, so an observer who does not own that rifle gets null;
    /// and reading the vendor's private field by reflection would couple us to code we do not control.
    ///
    /// Same shape and same rules as <see cref="GripPoseSet"/> (ADR-023 Slice 2): created once by the
    /// prefab builder, never reseeded, so play-test calibration is never overwritten by a re-bake.
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/Remote Wieldable Audio Set", fileName = "RemoteWieldableAudioSet")]
    public sealed class RemoteWieldableAudioSet : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            [Tooltip("ItemDefinition id of the wieldable, matching the networked held_item (ADR-023).")]
            public int itemId;

            [Tooltip("Clip played on the proxy when this peer fires.")]
            public AudioClip fireClip;

            [Tooltip("Volume multiplier for this weapon.")]
            [Range(0f, 1f)] public float volume = 1f;

            [Tooltip("Optional tail/boom for this weapon. Leave empty to use the default tail.")]
            public AudioClip tailClip;
        }

        [Tooltip("Fallback for any firearm without its own entry. Dragging ONE clip here already makes " +
                 "every weapon audible, so the feature is not silent until all of them are authored.")]
        public AudioClip defaultFireClip;

        [Range(0f, 1f)] public float defaultVolume = 1f;

        [Tooltip("Per-weapon overrides, matched by held item id.")]
        public Entry[] entries = Array.Empty<Entry>();

        [Header("Distance curve — the CRACK (near, directional)")]
        [Tooltip("Full-volume radius, in metres. THIS is the range knob that matters: with logarithmic " +
                 "rolloff the volume falls as minDistance/distance, so 15 m still leaves ~15% at 100 m " +
                 "and ~5% at 300 m. AudioManager's pooled sources use 2 m — tuned for footsteps and " +
                 "impacts, which is why an untouched gunshot dies by 40 m.")]
        [Min(0.1f)] public float minDistance = 15f;

        [Tooltip("Cutoff, in metres. 500 m on purpose: it matches the rifle loudness ADR-041 sends to " +
                 "the phantom, so what the AI hears and what players hear agree.")]
        [Min(1f)] public float maxDistance = 500f;

        [Tooltip("Use a custom rolloff curve that genuinely reaches zero at Max Distance. Unity's " +
                 "Logarithmic mode does NOT silence there — it stops attenuating and holds " +
                 "minDistance/maxDistance for ever, so a shot stayed faintly audible at any range. " +
                 "Turn off only to compare against the old behaviour.")]
        public bool hardCutoff = true;

        [Tooltip("Fallback rolloff when Hard Cutoff is off.")]
        public AudioRolloffMode rolloff = AudioRolloffMode.Logarithmic;

        [Tooltip("Stereo spread in degrees. A little widening keeps a distant shot from feeling like a " +
                 "pinpoint dot; 0 is fully directional.")]
        [Range(0f, 180f)] public float spread = 15f;

        [Header("Distance curve — the TAIL (far, enveloping)")]
        [Tooltip("Optional boom/echo played just after the crack, on its OWN source with a much larger " +
                 "full-volume radius. This is what creates the depth: up close you mostly hear the crack, " +
                 "far away the tail is what survives. Leave empty and only the crack plays.")]
        public AudioClip defaultTailClip;

        [Range(0f, 1f)] public float tailVolume = 0.8f;

        [Tooltip("Delay before the tail, in seconds. Real distance delay would need speed-of-sound " +
                 "modelling; a small fixed offset is what sells it.")]
        [Range(0f, 0.5f)] public float tailDelay = 0.08f;

        [Tooltip("Full-volume radius for the tail. Deliberately much larger than the crack's — that " +
                 "difference IS the effect.")]
        [Min(0.1f)] public float tailMinDistance = 60f;

        [Header("Realism — travel time and air absorption")]
        [Tooltip("Metres per second. Sound arrives LATE: at 200 m a shot is heard 0.58 s after it was " +
                 "fired. This is the single strongest 'that was far away' cue there is — volume alone " +
                 "never reads as distance, it reads as a quiet gun nearby. 343 m/s is air at 20 °C. " +
                 "Set 0 to disable.")]
        [Min(0f)] public float speedOfSound = 343f;

        [Tooltip("Below this distance the travel delay is skipped, so nearby shots stay perfectly tight " +
                 "to the muzzle flash instead of feeling laggy.")]
        [Min(0f)] public float minDelayDistance = 15f;

        [Tooltip("Low-pass cutoff (Hz) for a shot right next to you: fully open, all the crack.")]
        [Range(500f, 22000f)] public float nearCutoff = 22000f;

        [Tooltip("Low-pass cutoff (Hz) at maximum distance. Air eats the high frequencies, which is why " +
                 "distant gunfire is a dull BOOM and not a quiet CRACK. This is the other half of the " +
                 "distance illusion — lower it for a muddier, further-away feel.")]
        [Range(200f, 22000f)] public float farCutoff = 650f;

        [Tooltip("Distance at which the cutoff reaches farCutoff.")]
        [Min(1f)] public float absorptionFullDistance = 220f;

        [Tooltip("Stereo spread at maximum distance. A far shot is diffuse and hard to pin down; a near " +
                 "one is a point. Interpolated with distance from the near 'Spread' above.")]
        [Range(0f, 180f)] public float farSpread = 70f;

        /// <summary>
        /// Air absorption as a low-pass cutoff for a given distance. Interpolated on sqrt(t) rather
        /// than t: real absorption bites hardest over the first stretch, and a linear ramp keeps a shot
        /// sounding bright well past the point where it should already be muffled — which is exactly
        /// the "it's far away but sounds close" complaint.
        /// </summary>
        public float CutoffForDistance(float distance)
        {
            float t = Mathf.Clamp01(distance / Mathf.Max(1f, absorptionFullDistance));
            return Mathf.Lerp(nearCutoff, farCutoff, Mathf.Sqrt(t));
        }

        /// <summary>Stereo spread for a given distance (near → far).</summary>
        public float SpreadForDistance(float distance)
        {
            float t = Mathf.Clamp01(distance / Mathf.Max(1f, absorptionFullDistance));
            return Mathf.Lerp(spread, farSpread, t);
        }

        /// <summary>Seconds the sound takes to travel; 0 inside <see cref="minDelayDistance"/>.</summary>
        public float TravelTime(float distance)
        {
            if (speedOfSound <= 0f || distance <= minDelayDistance)
                return 0f;
            return distance / speedOfSound;
        }

        [Header("Echo — corridor slap-back")]
        [Tooltip("Extra reflections replayed after the shot. This is the 'it echoed down the hall' cue. " +
                 "Discrete taps rather than a reverb filter on purpose: the Backrooms are long hard " +
                 "corridors, which produce distinct slap-back rather than a smooth room tail — and taps " +
                 "cost nothing, while a live AudioReverbFilter per proxy would run every frame whether " +
                 "anyone is shooting or not. 0 disables the echo.")]
        [Range(0, 4)] public int echoTaps = 2;

        [Tooltip("Distance at which echo reaches full strength. Below it the taps fade in, so a shot " +
                 "next to your ear stays dry and immediate.")]
        [Min(1f)] public float echoFullDistance = 60f;

        [Tooltip("Delay of the first reflection. ~0.12 s is a wall about 20 m away.")]
        [Range(0.02f, 1f)] public float echoFirstDelay = 0.13f;

        [Tooltip("Extra delay added per additional tap.")]
        [Range(0.02f, 1f)] public float echoSpacing = 0.17f;

        [Tooltip("Volume of the first reflection relative to the shot. Each further tap multiplies by " +
                 "this again, so reflections die away instead of machine-gunning.")]
        [Range(0.05f, 0.9f)] public float echoFalloff = 0.45f;

        [Tooltip("How much duller each reflection is than the one before. Every bounce off a wall eats " +
                 "high frequencies, so a late reflection should be noticeably darker.")]
        [Range(0.2f, 1f)] public float echoCutoffScale = 0.55f;

        [Header("Variation")]
        [Tooltip("Per-shot random pitch spread. Without it a burst is the identical sample stamped N " +
                 "times, which the ear hears instantly as a loop rather than as gunfire.")]
        [Range(0f, 0.2f)] public float pitchVariation = 0.05f;

        /// <summary>Echo strength for a distance: 0 point-blank (dry), 1 at
        /// <see cref="echoFullDistance"/> and beyond.</summary>
        public float EchoStrength(float distance)
            => Mathf.Clamp01(distance / Mathf.Max(1f, echoFullDistance));

        /// <summary>Tail clip for a held item id, falling back to the default. False when there is
        /// nothing to play — the crack alone is a valid configuration.</summary>
        public bool TryResolveTail(int itemId, out AudioClip clip)
        {
            if (entries != null)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    if (e != null && e.itemId == itemId && e.tailClip != null)
                    {
                        clip = e.tailClip;
                        return true;
                    }
                }
            }

            clip = defaultTailClip;
            return clip != null;
        }

        /// <summary>Clip + volume for a held item id; falls back to the default clip. Returns false
        /// when there is nothing to play at all (no entry AND no default) — the caller stays silent
        /// rather than guessing.</summary>
        public bool TryResolve(int itemId, out AudioClip clip, out float volume)
        {
            if (entries != null)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    if (e != null && e.itemId == itemId && e.fireClip != null)
                    {
                        clip = e.fireClip;
                        volume = e.volume;
                        return true;
                    }
                }
            }

            clip = defaultFireClip;
            volume = defaultVolume;
            return clip != null;
        }
    }
}
