using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// You can extend this by creating another partial class with the same name.
    /// </summary>
    public static partial class AnimationConstants
    {
        // General Params.
        public static readonly int Equip = Animator.StringToHash("Equip");
        public static readonly int Holster = Animator.StringToHash("Holster");
        public static readonly int EquipSpeed = Animator.StringToHash("Equip Speed");
        public static readonly int HolsterSpeed = Animator.StringToHash("Holster Speed");
        public static readonly int IsVisible = Animator.StringToHash("Is Visible");
        public static readonly int Use = Animator.StringToHash("Use");
        
        // Firearm Params.
        public static readonly int Shoot = Animator.StringToHash("Shoot");
        public static readonly int ShootSpeed = Animator.StringToHash("Shoot Speed");
        public static readonly int IsAiming = Animator.StringToHash("Is Aiming");
        public static readonly int IsReloading = Animator.StringToHash("Is Reloading");
        public static readonly int ReloadSpeed = Animator.StringToHash("Reload Speed");
        public static readonly int IsEmpty = Animator.StringToHash("Is Empty");
        public static readonly int IsCharging = Animator.StringToHash("Is Charging");
        public static readonly int FullCharge = Animator.StringToHash("Full Charge");
        public static readonly int Eject = Animator.StringToHash("Eject");
        public static readonly int ChangeMode = Animator.StringToHash("Change Mode");

        // Melee Params.
        public static readonly int Attack = Animator.StringToHash("Attack");
        public static readonly int AttackHit = Animator.StringToHash("Attack Hit");
        public static readonly int AttackIndex = Animator.StringToHash("Attack Index");
        public static readonly int AttackSpeed = Animator.StringToHash("Attack Speed");
        public static readonly int IsThrown = Animator.StringToHash("Is Thrown");
    }
}
