using System.Linq;
using System;

namespace PolymindGames
{
    public enum BodyPoint
    {
        Head = 0,
        Torso = 1,
        Feet = 2,
        Hands = 3,
        Legs = 4
    }

    public static class BodyPointUtility
    {
        public static readonly BodyPoint[] BodyPoints = Enum.GetValues(typeof(BodyPoint)).Cast<BodyPoint>().ToArray();
        public static readonly int TotalBodyPoints = BodyPoints.Length;
    }
}
