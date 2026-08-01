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
        }

        [Tooltip("Fallback for any firearm without its own entry. Dragging ONE clip here already makes " +
                 "every weapon audible, so the feature is not silent until all of them are authored.")]
        public AudioClip defaultFireClip;

        [Range(0f, 1f)] public float defaultVolume = 1f;

        [Tooltip("Per-weapon overrides, matched by held item id.")]
        public Entry[] entries = Array.Empty<Entry>();

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
