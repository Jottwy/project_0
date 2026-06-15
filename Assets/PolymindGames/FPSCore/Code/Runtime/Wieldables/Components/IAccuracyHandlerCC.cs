using System.Collections;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Handles and manages character accuracy.
    /// </summary>
    public interface IAccuracyHandlerCC : ICharacterComponent
    {
        /// <summary> Gets the accuracy modifier. </summary>
        float GetAccuracyMod();
    
        /// <summary> Sets the base accuracy value. </summary>
        void SetBaseAccuracy(float accuracy);
    }

    public sealed class NullAccuracyHandler : IAccuracyHandlerCC
    {
        public GameObject gameObject => null;
        public Transform transform => null;
        public bool enabled { get => true; set { } }
        public Coroutine StartCoroutine(IEnumerator routine) => null;

        public float GetAccuracyMod() => 1f;
        public void SetBaseAccuracy(float accuracy) { }
    }
}