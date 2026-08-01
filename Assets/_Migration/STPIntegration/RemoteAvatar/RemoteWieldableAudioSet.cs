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

        [Tooltip("Logarithmic is the physical curve and the right default. Linear reads flat and fake: " +
                 "it stays too loud mid-range and then cuts out.")]
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
