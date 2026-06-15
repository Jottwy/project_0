using System;
using UnityEngine;

namespace PolymindGames.SaveSystem
{
    /// <summary>
    /// Represents serialized data of a Rigidbody, including position, rotation, velocity, and angular velocity.
    /// </summary>
    [Serializable]
    public struct SerializedRigidbodyData
    {
        /// <summary>
        /// The position of the Rigidbody.
        /// </summary>
        public Vector3 Position;

        /// <summary>
        /// The rotation of the Rigidbody.
        /// </summary>
        public Quaternion Rotation;

        /// <summary>
        /// The velocity of the Rigidbody.
        /// </summary>
        public Vector3 Velocity;

        /// <summary>
        /// The angular velocity of the Rigidbody.
        /// </summary>
        public Vector3 AngularVelocity;

        /// <summary>
        /// Constructs SerializedRigidbodyData from given parameters.
        /// </summary>
        /// <param name="position">The position of the Rigidbody.</param>
        /// <param name="rotation">The rotation of the Rigidbody.</param>
        /// <param name="velocity">The velocity of the Rigidbody.</param>
        /// <param name="angularVelocity">The angular velocity of the Rigidbody.</param>
        public SerializedRigidbodyData(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            AngularVelocity = angularVelocity;
        }

        /// <summary>
        /// Constructs SerializedRigidbodyData from a Rigidbody instance.
        /// </summary>
        /// <param name="rigidbody">The Rigidbody to extract data from.</param>
        public SerializedRigidbodyData(Rigidbody rigidbody)
        {
            Position = rigidbody.position;
            Rotation = rigidbody.rotation;
            Velocity = rigidbody.linearVelocity;
            AngularVelocity = rigidbody.angularVelocity;
        }
        
        /// <summary>
        /// Extracts SerializedRigidbodyData from an array of Rigidbodies.
        /// </summary>
        /// <param name="rigidbodies">The Rigidbodies to extract data from.</param>
        /// <returns>An array of SerializedRigidbodyData extracted from the Rigidbodies.</returns>
        public static SerializedRigidbodyData[] ExtractFromRigidbodies(ReadOnlySpan<Rigidbody> rigidbodies)
        {
            if (rigidbodies.Length == 0)
                return null;

            var data = new SerializedRigidbodyData[rigidbodies.Length];
            for (int i = 0; i < rigidbodies.Length; i++)
                data[i] = new SerializedRigidbodyData(rigidbodies[i]);

            return data;
        }

        /// <summary>
        /// Applies SerializedRigidbodyData to an array of Rigidbodies.
        /// </summary>
        /// <param name="rigidbodies">The Rigidbodies to apply data to.</param>
        /// <param name="data">The SerializedRigidbodyData to apply.</param>
        public static void ApplyToRigidbodies(ReadOnlySpan<Rigidbody> rigidbodies, ReadOnlySpan<SerializedRigidbodyData> data)
        {
            if (data == null)
                return;

            for (int i = 0; i < data.Length; i++)
                ApplyToRigidbody(rigidbodies[i], in data[i]);
        }

        /// <summary>
        /// Applies SerializedRigidbodyData to a Rigidbody.
        /// </summary>
        /// <param name="rigidbody">The Rigidbody to apply data to.</param>
        /// <param name="data">The SerializedRigidbodyData to apply.</param>
        public static void ApplyToRigidbody(Rigidbody rigidbody, in SerializedRigidbodyData data)
        {
            rigidbody.position = data.Position;
            rigidbody.rotation = data.Rotation;
            rigidbody.linearVelocity = data.Velocity;
            rigidbody.angularVelocity = data.AngularVelocity;
        }
    }
}