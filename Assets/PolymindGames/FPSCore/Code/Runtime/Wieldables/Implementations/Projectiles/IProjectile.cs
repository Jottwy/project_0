using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    public readonly struct LaunchContext
    {
        public readonly Vector3 Origin;
        public readonly Vector3 Velocity;
        public readonly Vector3 Torque;
        public readonly float Gravity;
        public readonly int LayerMask;
        
        public LaunchContext(in Vector3 origin, in Vector3 velocity, in Vector3 torque = default(Vector3), float gravity = 9.81f, int layerMask = LayerConstants.SolidObjectsMask)
        {
            Origin = origin;
            Velocity = velocity;
            Torque = torque;
            Gravity = Mathf.Abs(gravity);
            LayerMask = layerMask;
        }
    }

    public interface IProjectile : IMonoBehaviour
    {
        void Launch(
            ICharacter character,
            in LaunchContext context,
            IFirearmImpactEffect effect,
            UnityAction hitCallback = null);
    }
}