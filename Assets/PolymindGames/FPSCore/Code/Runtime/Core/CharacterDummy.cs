using PolymindGames.InventorySystem;
using UnityEngine.Events;
using UnityEngine;
using System;

namespace PolymindGames
{
    public sealed class CharacterDummy : MonoBehaviour, ICharacter
    {
        private IHealthManager _healthManager;
        private ICharacterAudioPlayer _audioPlayer;
        private IInventory _inventory;

        public string SourceName => Name;
        
        public string Name
        {
            get => gameObject.name;
            set => gameObject.name = value;
        }

        public IHealthManager HealthManager => _healthManager;
        public IAnimatorController Animator => NullAnimator.Instance;
        public ICharacterAudioPlayer Audio => _audioPlayer;
        public IInventory Inventory => _inventory;

        public event UnityAction<ICharacter> Destroyed { add { } remove { } }

        public Transform GetTransformOfBodyPoint(BodyPoint point)
        {
            return transform;
        }

        public bool TryGetCC<T>(out T component) where T : class, ICharacterComponent
        {
            component = default(T);
            return false;
        }

        public T GetCC<T>() where T : class, ICharacterComponent
        {
            return default(T);
        }

        public ICharacterComponent GetCC(Type type) => null;

        private void Awake()
        {
            _healthManager = GetComponentInChildren<IHealthManager>() ?? new NullHealthManager();
            _audioPlayer = GetComponentInChildren<ICharacterAudioPlayer>() ?? new DefaultCharacterAudioPlayer();
            _inventory = GetComponentInChildren<IInventory>() ?? new DefaultInventory(gameObject);
        }
    }
}
