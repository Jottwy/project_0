using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    public sealed class NetworkWorldObject : MonoBehaviour
    {
        public uint id;
        public string kind = "item";
        public bool active = true;
    }
}
