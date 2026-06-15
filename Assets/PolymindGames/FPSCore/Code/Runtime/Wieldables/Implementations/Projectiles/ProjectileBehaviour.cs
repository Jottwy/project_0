using PolymindGames.WieldableSystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    public abstract class ProjectileBehaviour : MonoBehaviour, IProjectile
    {
        public abstract void Launch(
            ICharacter character,
            in LaunchContext context,
            IFirearmImpactEffect effect,
            UnityAction hitCallback = null);
    }
}