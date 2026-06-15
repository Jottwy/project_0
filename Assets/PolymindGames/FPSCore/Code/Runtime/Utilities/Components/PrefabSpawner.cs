using PolymindGames.ProceduralMotion;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PolymindGames.Demo
{
    public sealed class PrefabSpawner : MonoBehaviour
    {
        [SerializeField]
        private bool _spawnOnStart;

        [SerializeField, MinMaxSlider(0, 100)]
        private Vector2Int _spawnCountRange = new(5, 10);

        [SerializeField, Range(0f, 1000f)]
        private float _forceMultiplier = 1f;

        [SpaceArea]
        [SerializeField, ReorderableList(HasLabels = false)]
        private Rigidbody[] _prefabs;
        
        [SpaceArea]
#if UNITY_EDITOR
        [EditorButton(nameof(CalculateOffsets))]
#endif
        [SerializeField, ReorderableList(HasLabels = false)]
        private Vector3[] _spawnPoints;

        [Title("Effects")]
        [SerializeField, Range(1f, 100f)]
        private float _spawnShakeRadius = 30f;

        [SerializeField, IgnoreParent]
        [Tooltip("Settings for the camera shake effect triggered by the resource impact.")]
        private ShakeData _spawnShake;

        [SerializeField]
        private AudioData _spawnAudio;

        [SerializeField]
        private ParticleSystem _spawnFX;

        public void SpawnPrefabs()
        {
            if (_prefabs.Length == 0 || _spawnPoints.Length == 0)
            {
                Debug.LogWarning("No prefabs or spawn offsets assigned!");
                return;
            }

            var trs = transform;
            int spawnCount = _spawnCountRange.GetRandomFromRange();
            trs.GetPositionAndRotation(out var position, out var rotation);
            for (int i = 0; i < spawnCount; i++)
            {
                Rigidbody prefabToSpawn = _prefabs[Random.Range(0, _prefabs.Length)];
                Vector3 spawnPosition = trs.TransformPoint(_spawnPoints[i % spawnCount]);
                var instance = Instantiate(prefabToSpawn, spawnPosition, Random.rotation);
                instance.AddForce((spawnPosition - position).normalized * _forceMultiplier, ForceMode.VelocityChange);
            }

            if (_spawnFX != null)
                Instantiate(_spawnFX, position, rotation);

            AudioManager.Instance.PlayClip3D(_spawnAudio, position);
            ShakeZone.PlayOneShotAtPosition(_spawnShake, position, _spawnShakeRadius);
        }

        private void Start()
        {
            if (_spawnOnStart)
                SpawnPrefabs();
        }

        #region Editor
#if UNITY_EDITOR
        private void CalculateOffsets()
        {
            Collider col = GetComponent<Collider>();

            if (col == null)
            {
                col = GetComponentInChildren<Collider>();
                if (col == null)
                {
                    Debug.LogWarning("No collider found! Offsets cannot be calculated.");
                    return;
                }
            }

            Bounds bounds = col.bounds;
            List<Vector3> offsets = new List<Vector3>();

            for (int i = 0; i < _spawnCountRange.y; i++)
            {
                Vector3 randomPoint;
                int attempts = 10; // Limit retries to avoid infinite loops

                do
                {
                    randomPoint = new Vector3(
                        Random.Range(bounds.min.x, bounds.max.x),
                        Random.Range(bounds.min.y, bounds.max.y),
                        Random.Range(bounds.min.z, bounds.max.z)
                    );
                    attempts--;
                } while (attempts > 0 && !IsPointInsideCollider(randomPoint, col));

                offsets.Add(randomPoint - transform.position); // Convert to local offset
            }

            // Sort offsets by distance from the center
            _spawnPoints = offsets.OrderBy(v => v.sqrMagnitude).ToArray();

            Debug.Log($"Calculated {_spawnPoints.Length} valid spawn offsets.");
        }

        /// <summary>
        /// Checks if a point is inside the collider using Collider.ClosestPoint.
        /// Works in both Edit Mode and Play Mode.
        /// </summary>
        private bool IsPointInsideCollider(Vector3 point, Collider col)
        {
            Vector3 closestPoint = col.ClosestPoint(point);
            return Vector3.Distance(closestPoint, point) < 0.001f;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            var trs = transform;
            foreach (Vector3 offset in _spawnPoints)
            {
                Gizmos.DrawSphere(trs.TransformPoint(offset), 0.2f);
            }
        }
#endif
        #endregion
    }
}