using UnityEngine;

namespace PolymindGames.Editor
{
    public interface IDrawablePanel
    {
        void Draw(Rect rect = default(Rect));
    }
}