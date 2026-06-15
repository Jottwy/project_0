using PolymindGames.InventorySystem;
using System.Collections.Generic;

namespace PolymindGames
{
    public interface IWorkstation : IMonoBehaviour
    {
        string Name { get; }
        IReadOnlyList<IItemContainer> GetContainers();
        
        void BeginInspection();
        void EndInspection();
    }
}