namespace PolymindGames.UserInterface
{
    public sealed class PlayerUI : CharacterUI
    {
        public static PlayerUI Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null)
                Instance = this;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            
            if (Instance == this)
                Instance = null;
        }
    }
}