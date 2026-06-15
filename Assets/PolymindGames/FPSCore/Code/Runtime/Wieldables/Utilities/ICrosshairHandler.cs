using UnityEngine.Events;

namespace PolymindGames.WieldableSystem
{
    public interface ICrosshairHandler
    {
        int CrosshairIndex { get; set; }
        float CrosshairCharge { get; set; }
        float Accuracy { get; }

        event UnityAction<int> CrosshairChanged;

        void ResetCrosshair();
        bool IsCrosshairActive();
    }
}