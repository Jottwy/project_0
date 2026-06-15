using System.Collections.Generic;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Defines an interface for objects that listen for incoming messages.
    /// </summary>
    public interface IMessageListener
    {
        /// <summary>
        /// Called when a message is received.
        /// </summary>
        /// <param name="character">The character associated with the message.</param>
        /// <param name="args">The message arguments.</param>
        void OnMessageReceived(ICharacter character, in MessageArgs args);
    }

    /// <summary>
    /// Manages the dispatching of messages to registered listeners.
    /// </summary>
    public sealed class MessageDispatcher
    {
        private static readonly MessageDispatcher _instance = new();
        private readonly List<IMessageListener> _listeners = new();

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init()
        {
            if (_instance != null)
                _instance._listeners.Clear();
        }
#endif

        public static MessageDispatcher Instance => _instance;

        /// <summary>
        /// Adds a message listener to the dispatcher.
        /// </summary>
        /// <param name="listener">The listener to add.</param>
        public void AddListener(IMessageListener listener) => _listeners.Add(listener);

        /// <summary>
        /// Removes a message listener from the dispatcher.
        /// </summary>
        /// <param name="listener">The listener to remove.</param>
        public void RemoveListener(IMessageListener listener) => _listeners.Remove(listener);

        /// <summary>
        /// Dispatches a message to all registered listeners.
        /// </summary>
        /// <param name="character">The character associated with the message.</param>
        /// <param name="type">The type of message.</param>
        /// <param name="message">The message text.</param>
        /// <param name="sprite">An optional sprite associated with the message.</param>
        public void Dispatch(ICharacter character, MsgType type, string message, Sprite sprite = null)
        {
            MessageArgs args = new MessageArgs(type, message, sprite);
            foreach (var listener in _listeners)
                listener.OnMessageReceived(character, args);
        }
        
        /// <summary>
        /// Dispatches a message to all registered listeners.
        /// </summary>
        /// <param name="character">The character associated with the message.</param>
        /// <param name="args">The message arguments.</param>
        public void Dispatch(ICharacter character, in MessageArgs args)
        {
            foreach (var listener in _listeners)
                listener.OnMessageReceived(character, args);
        }
    }

    /// <summary>
    /// Represents the arguments associated with a message.
    /// </summary>
    public readonly struct MessageArgs
    {
        /// <summary>
        /// Gets the type of the message.
        /// </summary>
        public readonly MsgType Type;

        /// <summary>
        /// Gets the message text.
        /// </summary>
        public readonly string Message;

        /// <summary>
        /// Gets the sprite associated with the message (optional).
        /// </summary>
        public readonly Sprite Sprite;

        /// <summary>
        /// Initializes a new instance of the <see cref="MessageArgs"/> struct.
        /// </summary>
        /// <param name="type">The type of the message.</param>
        /// <param name="message">The message text.</param>
        /// <param name="sprite">The sprite associated with the message (optional).</param>
        public MessageArgs(MsgType type, string message, Sprite sprite = null)
        {
            Type = type;
            Message = message;
            Sprite = sprite;
        }
    }

    /// <summary>
    /// Represents the type of a message.
    /// </summary>
    public enum MsgType : byte
    {
        /// <summary>
        /// Information message.
        /// </summary>
        Info = 0,

        /// <summary>
        /// Warning message.
        /// </summary>
        Warning = 1,

        /// <summary>
        /// Error message.
        /// </summary>
        Error = 2
    }
}
