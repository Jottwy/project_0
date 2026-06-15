using System.Collections;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Interface representing MonoBehaviour-like functionality.
    /// </summary>
    public interface IMonoBehaviour
    {
        /// <summary>
        /// Gets the game object this component is attached to.
        /// </summary>
        GameObject gameObject { get; }

        /// <summary>
        /// Gets the transform of the game object this component is attached to.
        /// </summary>
        Transform transform { get; }
        
        /// <summary>
        /// Enabled Behaviours are Updated, disabled Behaviours are not.
        /// </summary>
        bool enabled { get; set; }

        /// <summary>
        ///   <para>Starts a Coroutine.</para>
        /// </summary>
        Coroutine StartCoroutine(IEnumerator routine);
    }
}