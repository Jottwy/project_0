namespace PolymindGames
{
    /// <summary>
    /// Provides constants representing layer indices for commonly used layers in Unity.
    /// </summary>
    public static partial class LayerConstants
    {
        // Layer indices
        public const int Default = 0;
        public const int TransparentFX = 1;
        public const int IgnoreRaycast = 2;
        public const int Water = 4;
        public const int UI = 5;
        public const int Debris = 6;
        public const int Effect = 7;
        public const int TriggerZone = 8;
        public const int Interactable = 9;
        public const int ViewModel = 10;
        public const int PostProcessing = 11;
        public const int Hitbox = 12;
        public const int Character = 13;
        public const int StaticObject = 14;
        public const int DynamicObject = 15;
        public const int Building = 16;
        public const int InteractableNoCollision = 17;
        
        // Layer masks
        public const int CharacterMask = (1 << Character);
        public const int BuildingMask = (1 << Building);
        public const int DamageableMask = (1 << Hitbox) | (1 << Interactable) | (1 << DynamicObject);
        public const int InteractableMask = (1 << Interactable) | (1 << InteractableNoCollision) | (1 << Building);
        public const int SimpleSolidObjectsMask = (1 << Default) | (1 << StaticObject) | (1 << DynamicObject);
        public const int SolidObjectsMask = SimpleSolidObjectsMask | DamageableMask | BuildingMask;
    }

    /// <summary>
    /// Provides constants representing commonly used tags in Unity.
    /// </summary>
    public static partial class TagConstants
    {
        // Tag names
        public const string MainCamera = "MainCamera";
        public const string Player = "Player";
        public const string GameController = "GameController";
    }

    /// <summary>
    /// Provides constants representing execution order values for script execution in Unity.
    /// </summary>
    public static partial class ExecutionOrderConstants
    {
        // Execution order values
        public const int Manager = -100000;
        public const int MonoSingleton = -10000;
        public const int BeforeDefault3 = -1000;
        public const int BeforeDefault2 = -100;
        public const int BeforeDefault1 = -10;
        public const int AfterDefault1 = 10;
        public const int AfterDefault2 = 100;
    }
}