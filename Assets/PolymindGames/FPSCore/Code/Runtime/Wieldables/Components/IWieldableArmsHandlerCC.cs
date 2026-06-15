using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    public interface IWieldableArmsHandlerCC : ICharacterComponent
    {
        Animator Animator { get; }
        bool IsVisible { get; set; }
        void EnableArms();
        void DisableArms();
        void ToggleNextArmSet();
    }
}