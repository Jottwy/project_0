using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    public interface IMotorCC : ICharacterComponent
    {
        /// <summary> Indicates whether the character is currently grounded. </summary>
        bool IsGrounded { get; }

        /// <summary> The last time the grounded state changed. </summary>
        float LastGroundedChangeTime { get; }

        /// <summary> The gravitational force affecting the character. </summary>
        float Gravity { get; }

        /// <summary> The current velocity of the character. </summary>
        Vector3 Velocity { get; }

        /// <summary> The simulated velocity used for prediction and physics calculations. </summary>
        Vector3 SimulatedVelocity { get; }

        /// <summary> The normal of the surface the character is standing on. </summary>
        Vector3 GroundNormal { get; }

        /// <summary> The speed at which the character can turn. </summary>
        float TurnSpeed { get; }

        /// <summary> The angle of the ground surface the character is standing on. </summary>
        float GroundSurfaceAngle { get; }

        /// <summary> Flags indicating the type of collision the character is experiencing. </summary>
        CollisionFlags CollisionFlags { get; }

        /// <summary> The layer mask defining what the character can collide with. </summary>
        LayerMask CollisionMask { get; }

        /// <summary> The default height of the character collider. </summary>
        float DefaultHeight { get; }

        /// <summary> The maximum slope angle the character can traverse. </summary>
        float SlopeLimit { get; }

        /// <summary> The current height of the character collider. </summary>
        float Height { get; set; }

        /// <summary> The radius of the character collider. </summary>
        float Radius { get; }

        /// <summary> Triggered when the character is teleported. </summary>
        event UnityAction Teleported;

        /// <summary> Triggered when the grounded state of the character changes. </summary>
        event UnityAction<bool> GroundedChanged;

        /// <summary> Triggered when the character experiences a fall impact, providing the impact force. </summary>
        event UnityAction<float> FallImpact;

        /// <summary> Triggered when the height of the character changes. </summary>
        event UnityAction<float> HeightChanged;

        /// <summary>
        /// Determines whether the character's height can be set to the specified value.
        /// </summary>
        /// <param name="height">The desired height.</param>
        /// <returns>True if the height can be set, otherwise false.</returns>
        bool CanSetHeight(float height);

        /// <summary> Resets the character's velocity to zero. </summary>
        void ResetVelocity();

        /// <summary>
        /// Calculates a speed multiplier based on the slope the character is on.
        /// </summary>
        /// <returns>The slope speed multiplier.</returns>
        float GetSlopeSpeedMultiplier();

        /// <summary>
        /// Instantly moves the character to a specified position and rotation.
        /// </summary>
        /// <param name="position">The target position.</param>
        /// <param name="rotation">The target rotation.</param>
        void Teleport(Vector3 position, Quaternion rotation);

        /// <summary>
        /// Applies a force to the character.
        /// </summary>
        /// <param name="force">The force vector to apply.</param>
        /// <param name="mode">The force mode (e.g., impulse, acceleration).</param>
        /// <param name="snapToGround">If true, the character will stay snapped to the ground.</param>
        void AddForce(Vector3 force, ForceMode mode, bool snapToGround = false);

        /// <summary>
        /// Sets the motion input callback, which is used to provide movement input to the character motor.
        /// </summary>
        /// <param name="motionInput">The motion input callback method.</param>
        void SetMotionInput(MotionInputCallback motionInput);
    }

    /// <summary>
    /// A delegate that will be called when the character motor needs input.
    /// </summary>
    public delegate Vector3 MotionInputCallback(Vector3 velocity, out bool useGravity, out bool snapToGround);

    public static class CharacterMotorExtensions
    {
        public static bool Has(this CollisionFlags thisFlags, CollisionFlags flag)
        {
            return (thisFlags & flag) == flag;
        }

        public static bool Raycast(this IMotorCC motor, Ray ray, float distance)
        {
            return PhysicsUtility.RaycastOptimized(ray, distance, out _, motor.CollisionMask);
        }

        public static bool Raycast(this IMotorCC motor, Ray ray, float distance, out RaycastHit raycastHit)
        {
            return PhysicsUtility.RaycastOptimized(ray, distance, out raycastHit, motor.CollisionMask);
        }

        public static bool SphereCast(this IMotorCC motor, Ray ray, float distance, float radius)
        {
            return PhysicsUtility.SphereCastOptimized(ray, radius, distance, out _, motor.CollisionMask);
        }
    }
}