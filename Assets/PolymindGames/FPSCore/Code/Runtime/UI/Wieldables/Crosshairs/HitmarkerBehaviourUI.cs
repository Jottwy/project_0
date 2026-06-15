using UnityEngine;

namespace PolymindGames.UserInterface
{
    public abstract class HitmarkerBehaviourUI : MonoBehaviour
    {
        public abstract bool IsActive { get; }
        public abstract void StartAnimation(DamageResult damageResult);
        public abstract void UpdateAnimation(float accuracy);
    }
}