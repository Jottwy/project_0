using System.Collections;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Provides extension methods for characters to handle physics interactions such as throwing and dropping objects.
    /// </summary>
    public static class CharacterExtensions
    {
        private const float InterpolationDisableDelay = 0.75f;

        public static bool IsLocalPlayer(this ICharacter character)
        {
            return ReferenceEquals(character, GameMode.Instance.LocalPlayer);
        }

        /// <summary>
        /// Sets the character's position and rotation, with an optional velocity reset.
        /// </summary>
        public static void SetPositionAndRotation(this ICharacter character, Vector3 position, Quaternion rotation, bool resetVelocity = true)
        {
            if (character.TryGetCC(out IMotorCC motor))
            {
                motor.Teleport(position, rotation);

                if (resetVelocity)
                    motor.ResetVelocity();
            }
            else
            {
                character.transform.SetPositionAndRotation(position, rotation);
            }
        }

        /// <summary>
        /// Applies a throwing force and torque to a rigidbody, considering the character's inherited velocity.
        /// </summary>
        public static void ThrowObject(this ICharacter character, Rigidbody rigidbody, Vector3 throwForce, float throwTorque)
        {
            Vector3 inheritedVelocity = CalculateInheritedVelocity(character);

            Vector3 totalForce = throwForce + inheritedVelocity;
            Vector3 torqueVector = Random.rotation.eulerAngles.normalized * throwTorque;

            rigidbody.AddForce(totalForce, ForceMode.VelocityChange);
            rigidbody.AddTorque(torqueVector, ForceMode.VelocityChange);
            
            StartInterpolationRoutine(character, rigidbody);
        }

        /// <summary>
        /// Calculates the inherited velocity of a character, accounting for vertical motion.
        /// </summary>
        private static Vector3 CalculateInheritedVelocity(ICharacter character)
        {
            Vector3 velocity = character.TryGetCC(out IMotorCC motor) ? motor.Velocity : Vector3.zero;
            velocity.y = Mathf.Abs(velocity.y);
            return velocity;
        }

        private static void StartInterpolationRoutine(ICharacter character, Rigidbody rigidbody)
        {
            if (rigidbody.interpolation == RigidbodyInterpolation.None)
            {
                rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
                character.StartCoroutine(ResetInterpolationAfterDelay(rigidbody, InterpolationDisableDelay));
            }
        }

        /// <summary>
        /// Coroutine to reset rigidbody interpolation after a delay.
        /// </summary>
        private static IEnumerator ResetInterpolationAfterDelay(Rigidbody rigidbody, float delay)
        {
            yield return new WaitForTime(delay);
            if (rigidbody != null)
                rigidbody.interpolation = RigidbodyInterpolation.None;
        }
    }
}