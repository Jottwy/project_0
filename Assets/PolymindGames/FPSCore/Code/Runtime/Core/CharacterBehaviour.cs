using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Inherit from this if you need access to the parent character and its components
    /// </summary>
    public abstract class CharacterBehaviour : MonoBehaviour
    {
        public ICharacter Character { get; private set; }
        
        protected virtual void Start()
        {
            var character = gameObject.GetComponentInParent<ICharacter>();
            if (character == null)
            {
                Debug.LogError("No character found in the root game object.", gameObject);
                return;
            }

            Character = character;
            OnBehaviourStart(character);

            if (enabled)
                OnBehaviourEnable(character);
        }

        protected virtual void OnDestroy()
        {
            if (Character != null)
                OnBehaviourDestroy(Character);
        }

        protected virtual void OnEnable()
        {
            if (Character != null)
                OnBehaviourEnable(Character);
        }

        protected virtual void OnDisable()
        {
            if (Character != null)
                OnBehaviourDisable(Character);
        }

        /// <summary>
        /// <para> - Similar to the Unity <b>Start</b> callback with the bonus of being synced with the parent character. </para>
        /// </summary>
        protected virtual void OnBehaviourStart(ICharacter character) { }
        
        /// <summary>
        /// <para> Gets called when this behaviour gets destroyed only if it has been initialized. <b>Destroyed</b>. </para>
        /// </summary>
        protected virtual void OnBehaviourDestroy(ICharacter character) { }
        
        /// <summary>
        /// <para> - Similar to the Unity <b>OnEnable</b> callback with the bonus of being synced with the parent character. </para>
        /// <para> - Gets called every time this behaviour get <b>Enabled</b> (only if the parent character has been found) </para>
        /// </summary>
        protected virtual void OnBehaviourEnable(ICharacter character) { } 

        /// <summary>
        /// <para> - Similar to the Unity <b>OnDisable</b> callback with the bonus of being synced with the parent character. </para>
        /// <para> - Gets called every time this behaviour get <b>Disabled</b> (only if the parent character has been found) </para>
        /// </summary>
        protected virtual void OnBehaviourDisable(ICharacter character) { }
    }
}