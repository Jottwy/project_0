using PolymindGames.InventorySystem;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine;
using System.Linq;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Main character class used by every entity in the game.
    /// It mainly acts as a hub for accessing components.
    /// </summary>
    [SelectionBase]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(ExecutionOrderConstants.BeforeDefault2)]
    public abstract partial class Character : MonoBehaviour, ICharacter
    {
        private Dictionary<Type, ICharacterComponent> _components;
        
        private static readonly List<ICharacterComponent> _cachedComponents;
        private static readonly Dictionary<Type, Type> _characterComponentToInterfacePairs;

        static Character()
        {
            Type baseType = typeof(ICharacterComponent);
            var ccImplementations =
                baseType.Assembly.GetTypes().Where(type => !type.IsInterface && baseType.IsAssignableFrom(type) && type != baseType).ToArray();

            int capacity = ccImplementations.Length;
           _characterComponentToInterfacePairs = new Dictionary<Type, Type>(capacity);
           _cachedComponents = new List<ICharacterComponent>(capacity);

            foreach (var ccImplementation in ccImplementations)
            {
                var ccInterfaces = ccImplementation.GetInterfaces();
                foreach (var ccInterface in ccInterfaces)
                {
                    if (ccInterface != baseType && baseType.IsAssignableFrom(ccInterface))
                    {
                       _characterComponentToInterfacePairs.Add(ccImplementation, ccInterface);
                        break;
                    }
                }
            }
        }

        /// <inheritdoc/>>
        public virtual string Name
        {
            get => gameObject.name;
            set => gameObject.name = value;
        }

        /// <inheritdoc/>>
        public IAnimatorController Animator { get; private set; }
        
        /// <inheritdoc/>>
        public ICharacterAudioPlayer Audio { get; private set; }
        
        /// <inheritdoc/>>
        public IHealthManager HealthManager { get; private set; }
        
        /// <inheritdoc/>>
        public IInventory Inventory { get; private set; }
        
        /// <inheritdoc/>>
        string IDamageSource.SourceName => Name;

        /// <inheritdoc/>>
        public event UnityAction<ICharacter> Destroyed;

        /// <inheritdoc/>>
        public abstract Transform GetTransformOfBodyPoint(BodyPoint point);

        /// <inheritdoc/>>
        public bool TryGetCC<T>(out T component) where T : class, ICharacterComponent
        {
#if UNITY_EDITOR
            _components ??= GetCharacterComponentsInChildren(gameObject);
#endif
            if (_components.TryGetValue(typeof(T), out var cc))
            {
                component = (T)cc;
                return true;
            }

            component = null;
            return false;
        }

        /// <inheritdoc/>>
        public T GetCC<T>() where T : class, ICharacterComponent
        {
#if UNITY_EDITOR
            _components ??= GetCharacterComponentsInChildren(gameObject);
#endif
            if (_components.TryGetValue(typeof(T), out ICharacterComponent component))
                return (T)component;

            return null;
        }

        /// <inheritdoc/>>
        public ICharacterComponent GetCC(Type type)
        {
#if UNITY_EDITOR
            _components ??= GetCharacterComponentsInChildren(gameObject);
#endif
            return _components.GetValueOrDefault(type);
        }

        protected virtual void Awake()
        {
            _components = GetCharacterComponentsInChildren(gameObject);

            Audio = GetComponentInChildren<ICharacterAudioPlayer>(true)
                          ?? new DefaultCharacterAudioPlayer();
            
            HealthManager = GetComponentInChildren<IHealthManager>(true);
            Inventory = GetComponentInChildren<IInventory>(true);

            Animator = GetAnimator();
            
            DamageTracker.RegisterSource(this);
        }

        private IAnimatorController GetAnimator()
        {
            var animators = gameObject.GetComponentsInChildren<IAnimatorController>(false);
            return animators.Length switch
            {
                0 => NullAnimator.Instance,
                1 => animators[0],
                _ => new MultiAnimator(animators)
            };
        }

        protected virtual void OnDestroy()
        {
            Destroyed?.Invoke(this);
            DamageTracker.UnregisterSource(this);
        }

        private static Dictionary<Type, ICharacterComponent> GetCharacterComponentsInChildren(GameObject root)
        {
            // Find & Setup all the components
            root.GetComponentsInChildren(false,_cachedComponents);

            var components = new Dictionary<Type, ICharacterComponent>(_cachedComponents.Count);
            foreach (var component in _cachedComponents)
            {
                var interfaceType = _characterComponentToInterfacePairs[component.GetType()];
#if DEBUG
                if (!components.TryAdd(interfaceType, component))
                {
                    Debug.LogError($"2 character components of the same type ({component.GetType()}) found under {root.name}.", root);
                }
#else
                components.Add(interfaceType, component);
#endif
            }

            return components;
        }

        #region Editor
#if UNITY_EDITOR
        protected virtual void Reset()
        {
            gameObject.layer = LayerConstants.Character;
        }
#endif
        #endregion
    }
}