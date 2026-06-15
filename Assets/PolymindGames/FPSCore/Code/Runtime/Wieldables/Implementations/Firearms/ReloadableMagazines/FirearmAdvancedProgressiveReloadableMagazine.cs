using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Advanced Progressive Reloadable-Magazine")]
    public class FirearmAdvancedProgressiveReloadableMagazine : FirearmProgressiveReloadableMagazine
    {
        [SerializeField, Title("Magazine Ejection")]
        private Rigidbody _ejectedMagazinePrefab;

        [SerializeField, Range(0f, 20f), BeginIndent]
        [ShowIf(nameof(_ejectedMagazinePrefab), true)]
        private float _magazineEjectDelay = 0.5f;

        [SerializeField, NotNull]
        [ShowIf(nameof(_ejectedMagazinePrefab), true)]
        private Transform _magazineEjectRoot;

        [SerializeField]
        [ShowIf(nameof(_ejectedMagazinePrefab), true)]
        private Vector3 _magazineEjectionForce;

        [SerializeField, EndIndent]
        [ShowIf(nameof(_ejectedMagazinePrefab), true)]
        private Vector3 _magazineEjectionTorque;

        [SerializeField, Title("Moving Parts")]
        private WieldableMovingPart _movingParts;

        private const float MagazineDestroyDelay = 30f;

        protected override void OnAmmoUsed(int ammo)
        {
            if (ammo == 0)
                _movingParts.BeginMovement();
            else
                _movingParts.StopMovement();
        }

        protected override void OnEmptyReloadStart(IFirearmAmmoProvider ammoProvider)
        {
            base.OnEmptyReloadStart(ammoProvider);

            if (_ejectedMagazinePrefab != null)
                CoroutineUtility.InvokeDelayed(this, EjectMagazine, _magazineEjectDelay);

            _movingParts.StopMovement();
        }

        protected override void OnTacticalReloadStart(IFirearmAmmoProvider ammoProvider)
        {
            base.OnTacticalReloadStart(ammoProvider);
            _movingParts.StopMovement();
        }

        // If this weapon's magazine is empty, update the moving parts
        private void LateUpdate() => _movingParts.UpdateMovement();

        private void EjectMagazine()
        {
            var magazine = Instantiate(_ejectedMagazinePrefab, _magazineEjectRoot.position, _magazineEjectRoot.rotation);

            Vector3 force = Wieldable.Character.transform.TransformVector(_magazineEjectionForce);
            Vector3 torque = Wieldable.Character.transform.TransformVector(_magazineEjectionTorque);

            magazine.linearVelocity = force;
            magazine.angularVelocity = torque;

            CoroutineUtility.InvokeDelayedGlobal(DestroyMagazine, _magazineEjectDelay);

            return;

            void DestroyMagazine()
            {
                if (magazine != null)
                    Destroy(magazine.gameObject, MagazineDestroyDelay);
            }
        }
    }
}