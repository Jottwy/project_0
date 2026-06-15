using System;
using UnityEngine;

namespace PolymindGames.SaveSystem
{
    /// <summary>
    /// Represents serialized data of a transform, including position, rotation, and scale.
    /// </summary>
    [Serializable]
    public struct SerializedTransformData
    {
        /// <summary>
        /// Flags for specifying which parts of the transform to save.
        /// </summary>
        [Flags]
        public enum SaveFlags : byte
        {
            Position = 1,
            Rotation = 2,
            Scale = 4,
            All = Position | Rotation | Scale
        }

        /// <summary>
        /// The position of the transform.
        /// </summary>
        public Vector3 Position;

        /// <summary>
        /// The rotation of the transform.
        /// </summary>
        public Quaternion Rotation;

        /// <summary>
        /// The scale of the transform.
        /// </summary>
        public Vector3 Scale;

        /// <summary>
        /// Constructs SerializedTransformData from a given transform.
        /// </summary>
        /// <param name="transform">The transform to serialize.</param>
        public SerializedTransformData(Transform transform)
        {
            Position = transform.localPosition;
            Rotation = transform.localRotation;
            Scale = transform.localScale;
        }

        /// <summary>
        /// Converts the SerializedTransformData to a string representation.
        /// </summary>
        /// <returns>A string representing the SerializedTransformData.</returns>
        public override string ToString() => $"Position: {Position} | Rotation: {Rotation.eulerAngles} | Scale: {Scale}";

        /// <summary>
        /// Extracts SerializedTransformData from an array of transforms.
        /// </summary>
        /// <param name="transforms">The transforms to extract data from.</param>
        /// <returns>An array of SerializedTransformData extracted from the transforms.</returns>
        public static SerializedTransformData[] ExtractFromTransforms(ReadOnlySpan<Transform> transforms)
        {
            if (transforms.Length == 0)
                return null;

            var data = new SerializedTransformData[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
                data[i] = new SerializedTransformData(transforms[i]);
            return data;
        }

        /// <summary>
        /// Applies SerializedTransformData to an array of transforms.
        /// </summary>
        /// <param name="transforms">The transforms to apply data to.</param>
        /// <param name="data">The SerializedTransformData to apply.</param>
        public static void ApplyToTransforms(ReadOnlySpan<Transform> transforms, ReadOnlySpan<SerializedTransformData> data)
        {
            if (data == null)
                return;

            for (int i = 0; i < data.Length; i++)
            {
                var trsData = data[i];
                var trs = transforms[i];
                trs.SetLocalPositionAndRotation(trsData.Position, trsData.Rotation);
                trs.localScale = trsData.Scale;
            }
        }

        /// <summary>
        /// Applies SerializedTransformData with specified flags to an array of transforms.
        /// </summary>
        /// <param name="transforms">The transforms to apply data to.</param>
        /// <param name="data">The SerializedTransformData to apply.</param>
        /// <param name="flags">Flags specifying which parts of the transform to apply.</param>
        public static void ApplyToTransforms(ReadOnlySpan<Transform> transforms, ReadOnlySpan<SerializedTransformData> data, SaveFlags flags)
        {
            if (data == null)
                return;

            for (int i = 0; i < data.Length; i++)
                ApplyToTransform(transforms[i], in data[i], flags);
        }

        /// <summary>
        /// Applies SerializedTransformData with specified flags to a transform.
        /// </summary>
        /// <param name="trs">The transform to apply data to.</param>
        /// <param name="data">The SerializedTransformData to apply.</param>
        /// <param name="flags">Flags specifying which parts of the transform to apply.</param>
        public static void ApplyToTransform(Transform trs, in SerializedTransformData data, SaveFlags flags = SaveFlags.All)
        {
            if ((flags & SaveFlags.Position) == SaveFlags.Position)
                trs.localPosition = data.Position;

            if ((flags & SaveFlags.Rotation) == SaveFlags.Rotation)
                trs.localRotation = data.Rotation;

            if ((flags & SaveFlags.Scale) == SaveFlags.Scale)
                trs.localScale = data.Scale;
        }
    }

}