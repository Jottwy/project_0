using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Advanced Reloadable-Magazine")]
    public class FirearmAdvancedReloadableMagazine : FirearmBasicReloadableMagazine
    {
        [SerializeField, Title("Magazine Ejection")]
        private Rigidbody _ejectedMagazinePrefab;

        [SerializeField, Range(0f, 20f), BeginIndent]
        [ShowIf(nameof(_ejectedMagazinePrefab), true)]
        private float _magazineEjectDelay = 0.5f;

        [SerializeField, NotNull]
        [ShowIf(nameof(_ejectedMagazinePrefab), true)]
        private Transform _magazineEjectRoot;

        [SerializeField, Range(0f, 10f)]
        [ShowIf(nameof(_ejectedMagazinePrefab), true)]
        private float _magazineEjectionForce = 1f;

        [Range(0f, 10f)]
        [SerializeField, EndIndent]
        [ShowIf(nameof(_ejectedMagazinePrefab), true)]
        private float _magazineEjectionTorque = 3f;

        [SerializeField, Title("Moving Parts")]
        private WieldableMovingPart _movingParts;

        private const float MagazineDestroyDelay = 20f;

        protected override void OnAmmoUsed(int ammo)
        {
            if (ammo == 0)
            {
                _movingParts.BeginMovement();
            }
            else
            {
                _movingParts.StopMovement();
            }
        }

        protected override void OnTacticalReload()
        {
            base.OnTacticalReload();
            _movingParts.StopMovement();
        }

        protected override void OnEmptyReload()
        {
            base.OnEmptyReload();

            if (_ejectedMagazinePrefab != null)
                CoroutineUtility.InvokeDelayed(this, EjectMagazine, _magazineEjectDelay);

            _movingParts.StopMovement();
        }

        // If this weapon's magazine is empty, update the moving parts
        private void LateUpdate() => _movingParts.UpdateMovement();

        private void EjectMagazine()
        {
            _magazineEjectRoot.GetPositionAndRotation(out var position, out var rotation);
            var magazine = Instantiate(_ejectedMagazinePrefab, position, rotation);

            Vector3 force = _magazineEjectRoot.TransformVector(Vector3.one * _magazineEjectionForce);
            Vector3 torque = Random.rotation.eulerAngles.normalized * _magazineEjectionTorque;

            magazine.position = position;
            magazine.AddForce(force, ForceMode.VelocityChange);
            magazine.AddTorque(torque, ForceMode.VelocityChange);
            
            Destroy(magazine.gameObject, MagazineDestroyDelay);
        }
    }
}