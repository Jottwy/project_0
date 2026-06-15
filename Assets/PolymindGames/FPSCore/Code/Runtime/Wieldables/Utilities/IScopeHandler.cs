using UnityEngine.Events;

namespace PolymindGames.WieldableSystem
{
    public interface IScopeHandler
    {
        int ZoomLevel { get; set; }
        int MaxZoomLevel { get; }
        
        int ScopeIndex { get; }
        bool IsScopeEnabled { get; }
        
        event UnityAction<bool> ScopeEnabled;
    }
}
