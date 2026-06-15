using UnityEngine;
using System;

namespace PolymindGames.InputSystem
{
    [CreateAssetMenu(menuName = "Polymind Games/Input/Input Context", fileName = "InputContext_")]
    public sealed class InputContext : ScriptableObject
    {
        [SerializeField, ReorderableList]
        [field: ClassImplements(typeof(IInputBehaviour), AllowAbstract = false, TypeGrouping = TypeGrouping.ByAddComponentMenu)]
        private SerializedType[] _allowedInputs = Array.Empty<SerializedType>();

        public SerializedType[] AllowedInputs => _allowedInputs;
        
        private static InputContext _nullContext;

        public static InputContext NullContext
        {
            get
            {
                if (_nullContext != null)
                    return _nullContext;
                
                _nullContext = CreateInstance<InputContext>();
                return _nullContext;
            }
        }
    }
}
