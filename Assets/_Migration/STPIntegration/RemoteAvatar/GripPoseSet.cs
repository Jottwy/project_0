using System;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-023 (Slice 2): per-category grip configuration for the 3P proxy's held item, read by
    /// <see cref="ProxyHeldItemHook"/>. The held wieldable's FPSCore category (de-prefixed
    /// <c>ParentGroup.Name</c>: "Melee"/"Firearms"/"Tools") selects one <see cref="CategoryGrip"/>,
    /// which positions the pickup mesh under <c>Hand.R</c> and curls the fingers into a static grip.
    ///
    /// Lives as a ScriptableObject (not serialized on the hook) so edits PERSIST during Play — the
    /// calibration loop tweaks this asset live and the hook re-applies every LateUpdate.
    ///
    /// Finger pose is PARAMETRIC in v1 (curl scalars around a shared bend axis); raw per-bone capture
    /// is a future upgrade for a single bucket if play-test demands it. Grip is RIGHT-HAND ONLY —
    /// two-handed items (rifle/bow/spear) leave Hand.L free (accepted incremental-fidelity limit).
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/Grip Pose Set", fileName = "GripPoseSet")]
    public sealed class GripPoseSet : ScriptableObject
    {
        [Serializable]
        public sealed class CategoryGrip
        {
            [Tooltip("De-prefixed FPSCore category name to match (ItemDefinition.ParentGroup.Name): " +
                     "\"Melee\", \"Firearms\", \"Tools\". Leave EMPTY for the fallback entry.")]
            public string categoryName = "";

            [Header("Model placement under Hand.R")]
            public Vector3 modelLocalPosition = Vector3.zero;
            public Vector3 modelLocalEuler = Vector3.zero;
            public Vector3 modelLocalScale = Vector3.one;

            [Header("Finger curl (degrees per segment)")]
            [Tooltip("Curl applied to the Index/Middle/Ring/Pinky segments around the shared bend axis.")]
            public float fingerCurl;
            [Tooltip("Curl applied to the Thumb segments around the shared bend axis.")]
            public float thumbCurl;
        }

        [Tooltip("Local bend axis of the finger bones (calibrate the sign per rig). Generic rig: the " +
                 "finger chain rotates around this axis when curling.")]
        public Vector3 fingerBendAxis = Vector3.forward;

        [Tooltip("Flip the curl direction if fingers bend backwards. Rig-dependent; calibrate once.")]
        public bool invertBend;

        [Tooltip("One entry per category, plus one fallback entry with an empty categoryName.")]
        public CategoryGrip[] grips = Array.Empty<CategoryGrip>();

        /// <summary>
        /// The grip for the given (de-prefixed) category name, or the fallback entry (empty name)
        /// if none matches, or null if the set is empty.
        /// </summary>
        public CategoryGrip Resolve(string categoryName)
        {
            CategoryGrip fallback = null;
            foreach (var g in grips)
            {
                if (g == null)
                    continue;
                if (string.IsNullOrEmpty(g.categoryName))
                    fallback ??= g;
                else if (g.categoryName == categoryName)
                    return g;
            }
            return fallback;
        }
    }
}
