using System;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-049: where each carried plank sits on a peer's proxy, one slot per unit in the stack.
    ///
    /// A separate asset rather than fields on the hook, for the same reason as
    /// <see cref="GripPoseSet"/>: the avatar prefab gets re-baked, and the builder seeds a calibration
    /// asset only when it is missing. Values edited here therefore survive a bake; values on the
    /// component would be at the mercy of one.
    ///
    /// It also cannot reuse the four offsets already on the <c>CarryableDefinition</c>. Those are
    /// captured in the space of the FIRST-PERSON carry socket, by a vendor editor button that only
    /// runs in Play against the viewmodel. Applied to a proxy's hand bone they put planks inside the
    /// torso.
    ///
    /// Edit it during Play: the hook re-applies every LateUpdate, so the stack calibrates live.
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/Carry Pose Set", fileName = "CarryPoseSet")]
    public sealed class CarryPoseSet : ScriptableObject
    {
        [Serializable]
        public sealed class Slot
        {
            [Tooltip("Position of this plank relative to the hand bone.")]
            public Vector3 localPosition;

            [Tooltip("Rotation of this plank relative to the hand bone, in degrees.")]
            public Vector3 localEuler;
        }

        [Tooltip("Scale applied to every carried model. The carryable prefab is authored at world " +
                 "size, which is the right size in the hands too — this is here for the case where " +
                 "it is not, not as a routine knob.")]
        public Vector3 modelLocalScale = Vector3.one;

        [Tooltip("One entry per unit in the stack, bottom first. A stack taller than this array " +
                 "reuses the last entry rather than dropping the extra planks.")]
        public Slot[] slots =
        {
            new Slot(),
            new Slot { localPosition = new Vector3(0f, 0.06f, 0f) },
            new Slot { localPosition = new Vector3(0f, 0.12f, 0f) },
            new Slot { localPosition = new Vector3(0f, 0.18f, 0f) },
        };

        /// <summary>
        /// The slot for stack index <paramref name="index"/>, clamped. Clamping rather than returning
        /// null keeps a mis-sized asset from making planks vanish: a fifth plank stacked on the
        /// fourth one's spot is visibly wrong and gets fixed; a fifth plank that renders nowhere
        /// looks like the network dropped it.
        /// </summary>
        public Slot Resolve(int index)
        {
            if (slots == null || slots.Length == 0)
                return null;

            return slots[Mathf.Clamp(index, 0, slots.Length - 1)];
        }
    }
}
