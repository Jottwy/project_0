using System.Collections.Generic;
using PolymindGames.SaveSystem;
using UnityEngine.Device;
using UnityEngine.Events;

namespace PolymindGames
{
    public abstract class Player : HumanCharacter, ISaveableComponent
    {
        public static IReadOnlyList<Player> AllPlayers => _allPlayers;
        public static event UnityAction<Player> PlayerCreated;
        
        private static readonly List<Player> _allPlayers = new();

        protected override void Awake()
        {
            base.Awake();
            PlayerCreated?.Invoke(this);
            _allPlayers.Add(this);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            _allPlayers.Remove(this);
        }

        #region Save & Load
        public void LoadMembers(object data)
        {
            if (data is string str)
                Name = str;
        }

        public object SaveMembers()
        {
            return Name;
        }
        #endregion
        
        #region Editor
#if UNITY_EDITOR
        protected virtual void OnValidate()
        {
            if (!Application.isPlaying)
                Name = null;
        }

        protected override void Reset()
        {
            base.Reset();
            gameObject.tag = TagConstants.Player;
        }
#endif
        #endregion
    }
}