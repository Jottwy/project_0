using UnityEngine;

namespace PolymindGames
{
    [RequireCharacterComponent(typeof(IMotorCC))]
    public sealed class PlayerRigidbodyDeathHandler : PlayerDeathHandler
    {
        [SerializeField, NotNull, Title("Settings")]
        private Rigidbody _deathRigidbody;

        [SerializeField, NotNull]
        private Transform _eyesTransform;

        private Transform _originalEyesParent;
        private Vector3 _originalPosition;

        protected override void OnBehaviourStart(ICharacter character)
        {
            _originalEyesParent = _eyesTransform.parent;
            _originalPosition = _deathRigidbody.transform.localPosition;
            _deathRigidbody.gameObject.SetActive(false);
        }

        protected override void OnPlayerDeath(in DamageArgs args)
        {
            // Call the base class method
            base.OnPlayerDeath(args);

            // Disable the Character Controller.
            if (Character.TryGetCC(out IMotorCC motor))
                motor.enabled = false;

            // Enable the body collider and Rigidbody, and set them to non-trigger and non-kinematic
            _deathRigidbody.isKinematic = false;
            _deathRigidbody.gameObject.SetActive(true);
            
            // Apply impulse force to the body Rigidbody
            Vector3 velocity = Character.GetCC<IMotorCC>().Velocity;
            _deathRigidbody.AddForce(Vector3.ClampMagnitude(velocity * 0.5f, 1f), ForceMode.Impulse);
            _deathRigidbody.AddRelativeTorque(new Vector3(Random.value - 0.5f, Random.value - 0.5f, Random.value - 0.5f) * 35, ForceMode.Impulse);
            _eyesTransform.SetParent(_deathRigidbody.transform, true);
        }

        protected override void OnPlayerRespawn()
        {
            // Call the base class method
            base.OnPlayerRespawn();

            // Reset the position and rotation of the body collider
            _eyesTransform.SetParent(_originalEyesParent, false);
            _eyesTransform.localPosition = Vector3.zero;
            _eyesTransform.localScale = Vector3.one;
            
            // Disable the body collider and Rigidbody, and set them to trigger and kinematic
            _deathRigidbody.isKinematic = true;
            _deathRigidbody.gameObject.SetActive(false);
            _deathRigidbody.transform.SetLocalPositionAndRotation(_originalPosition, Quaternion.identity);
            
            // Enable the Character Controller.
            if (Character.TryGetCC(out IMotorCC motor))
                motor.enabled = true;
        }
    }
}