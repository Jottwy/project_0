using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    /// <summary>
    /// Visualizes the trajectory of a projectile using a line renderer and handles collision detection.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault2)]
    public sealed class ProjectilePathVisualizer : MonoBehaviour
    {
        [SerializeField, NotNull]
        private LineRenderer _lineRenderer;

        [SerializeField, NotNull]
        private Transform _hitRenderer;

        [SerializeField, Title("Settings")]
        private bool _calculateCollision;

        [SerializeField, Range(0f, 10f)]
        private float _predictedSeconds = 1f;

        [SerializeField, Range(3, 100)]
        private int _lineSegments = 12;

        [SerializeField, Range(0f, 10f)]
        private float _hitMinSize = 0.1f;

        [SerializeField, Range(0f, 10f)]
        private float _hitMaxSize = 0.2f;

        [SerializeField, Range(0f, 1f)]
        private float _hitPositionOffset = 0.1f;

        private Transform _cachedTransform;
        private Vector3[] _positions;
        private Vector3 _lastNormal;
        private float _gravity;
        private float _speed;
        private int _layerMask;

        private static readonly Vector3 _hiddenPosition = new(0f, -1000f, 0f);

        private const float NormalDiffThreshold = 0.5f;
        private const float NormalInterpSpeed = 7.5f;

        /// <summary>
        /// Gets a value indicating whether the projectile path visualizer is currently enabled.
        /// </summary>
        public bool IsEnabled => enabled;

        /// <summary>
        /// Enables the projectile path visualizer and sets the initial position of the hit renderer.
        /// </summary>
        public void Enable()
        {
            _lineRenderer.enabled = true;
            _hitRenderer.position = _hiddenPosition;
            enabled = true;
        }

        /// <summary>
        /// Disables the projectile path visualizer and hides the hit renderer.
        /// </summary>
        public void Disable()
        {
            _lineRenderer.enabled = false;
            _hitRenderer.position = _hiddenPosition;
            enabled = false;
        }

        /// <summary>
        /// Updates the context for the projectile path, including speed, gravity, and layer mask.
        /// </summary>
        /// <param name="speed">The speed of the projectile.</param>
        /// <param name="gravity">The gravity affecting the projectile.</param>
        /// <param name="layerMask">The layer mask for collision detection.</param>
        public void UpdateContext(float speed, float gravity = 9.81f, int layerMask = LayerConstants.SolidObjectsMask)
        {
            _speed = speed;
            _gravity = gravity;
            _layerMask = layerMask;
        }

        private void Awake()
        {
            _cachedTransform = transform;
            _positions = new Vector3[_lineSegments];
            Disable();
        }

        private void LateUpdate()
        {
            Vector3 origin = _cachedTransform.position;
            Vector3 velocity = _cachedTransform.forward * _speed;

            if (_calculateCollision)
            {
                CalculatePathWithCollision(origin, velocity);
            }
            else
            {
                CalculatePath(origin, velocity);
            }

            _lineRenderer.SetPositions(_positions);
        }

        /// <summary>
        /// Calculates the projectile path and visualizes it with collision detection if enabled.
        /// </summary>
        /// <param name="origin">The origin of the projectile.</param>
        /// <param name="velocity">The velocity of the projectile.</param>
        /// <param name="lengthMultiplier"></param>
        private void CalculatePath(Vector3 origin, Vector3 velocity, float lengthMultiplier = 1f)
        {
            for (int i = 0; i < _lineSegments; i++)
            {
                float t = (i / (float)_lineSegments) * _predictedSeconds * lengthMultiplier;
                _positions[i] = CalculateParabolicPoint(origin, velocity, t);
            }
        }

        /// <summary>
        /// Calculates the projectile path and handles collision visualization if a collision is detected.
        /// </summary>
        /// <param name="origin">The origin of the projectile.</param>
        /// <param name="velocity">The velocity of the projectile.</param>
        private void CalculatePathWithCollision(Vector3 origin, Vector3 velocity)
        {
            float collisionTime = CheckForCollision(origin, velocity, out var hit);
            if (collisionTime > 0f)
            {
                float lengthMultiplier = collisionTime / _predictedSeconds;
                CalculatePath(origin, velocity, lengthMultiplier);

                Vector3 calculatedNormal = hit.normal;

                // Check the difference between the current normal and the last normal
                // Interpolate if the difference is within the threshold
                _lastNormal = Vector3.Distance(_lastNormal, calculatedNormal) <= NormalDiffThreshold
                    ? Vector3.Lerp(_lastNormal, calculatedNormal, Time.deltaTime * NormalInterpSpeed)
                    : calculatedNormal; // Directly update the normal if the difference is too large

                _hitRenderer.transform.SetPositionAndRotation(hit.point + _lastNormal * _hitPositionOffset, Quaternion.LookRotation(_lastNormal));

                float hitSize = Mathf.Lerp(_hitMinSize, _hitMaxSize, lengthMultiplier);
                _hitRenderer.localScale = new Vector3(hitSize, hitSize, hitSize);
            }
            else
            {
                _hitRenderer.transform.position = _hiddenPosition;
                CalculatePath(origin, velocity);
            }
        }

        /// <summary>
        /// Checks for collision along the projectile path and returns the time of collision if detected.
        /// </summary>
        /// <param name="origin">The origin of the projectile.</param>
        /// <param name="velocity">The velocity of the projectile.</param>
        /// <param name="hit">The collision information if a collision is detected.</param>
        /// <returns>The time of collision if detected; otherwise, -1f.</returns>
        private float CheckForCollision(Vector3 origin, Vector3 velocity, out RaycastHit hit)
        {
            for (int i = 0; i < _lineSegments; i++)
            {
                float t = (i / (float)_lineSegments) * _predictedSeconds;
                Vector3 currentPoint = CalculateParabolicPoint(origin, velocity, t);

                if (i > 0)
                {
                    Vector3 previousPoint = _positions[i - 1];
                    if (CheckCollision(previousPoint, currentPoint, out hit))
                    {
                        // Interpolate to find the exact collision time
                        float tPrevious = ((i - 1) / (float)_lineSegments) * _predictedSeconds;
                        float tCollision = InterpolateCollisionTime(previousPoint, currentPoint, tPrevious, t);
                        return tCollision;
                    }
                }

                _positions[i] = currentPoint;
            }

            hit = default(RaycastHit);
            return -1f;
        }

        /// <summary>
        /// Interpolates to find the exact collision time between two points along the projectile path.
        /// </summary>
        /// <param name="start">The start point of the segment.</param>
        /// <param name="end">The end point of the segment.</param>
        /// <param name="tStart">The time at the start point.</param>
        /// <param name="tEnd">The time at the end point.</param>
        /// <returns>The interpolated collision time.</returns>
        private float InterpolateCollisionTime(Vector3 start, Vector3 end, float tStart, float tEnd)
        {
            Ray ray = new Ray(start, (end - start).normalized);
            float distance = Vector3.Distance(start, end);
            if (PhysicsUtility.RaycastOptimized(ray, distance, out RaycastHit hit, _layerMask))
            {
                float distanceToCollision = Vector3.Distance(start, hit.point);
                return Mathf.Lerp(tStart, tEnd, distanceToCollision / distance);
            }
            return tEnd;
        }

        /// <summary>
        /// Calculates a point on the projectile's parabolic path based on the given time.
        /// </summary>
        /// <param name="origin">The origin of the projectile.</param>
        /// <param name="velocity">The velocity of the projectile.</param>
        /// <param name="time">The time at which to calculate the point.</param>
        /// <returns>The calculated point on the parabolic path.</returns>
        private Vector3 CalculateParabolicPoint(Vector3 origin, Vector3 velocity, float time)
        {
            Vector3 point = origin + velocity * time;
            Vector3 gravityEffect = Vector3.down * (_gravity * time * time);
            return point + gravityEffect;
        }

        /// <summary>
        /// Checks for a collision between two points and outputs collision information if detected.
        /// </summary>
        /// <param name="startPoint">The start point of the segment.</param>
        /// <param name="endPoint">The end point of the segment.</param>
        /// <param name="hit">The collision information if a collision is detected.</param>
        /// <returns>True if a collision is detected; otherwise, false.</returns>
        private bool CheckCollision(Vector3 startPoint, Vector3 endPoint, out RaycastHit hit)
        {
            Vector3 direction = endPoint - startPoint;
            float distance = direction.magnitude;
            Ray ray = new Ray(startPoint, direction);
            return PhysicsUtility.RaycastOptimized(ray, distance, out hit, _layerMask);
        }
    }
}