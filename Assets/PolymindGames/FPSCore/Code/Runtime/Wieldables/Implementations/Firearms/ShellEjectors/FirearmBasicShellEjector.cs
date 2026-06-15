using PolymindGames.PoolingSystem;
using System.Collections;
using UnityEngine;
using UnityEngine.Serialization;
using Random = UnityEngine.Random;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Basic Shell-Ejector")]
    public class FirearmBasicShellEjector : FirearmShellEjectorBehaviour
    {
        [FormerlySerializedAs("_casingPrefab")]
        [Title("References")]
        [SerializeField, NotNull, PrefabObjectOnly]
        private Rigidbody _shellPrefab;

        [SerializeField, NotNull]
        private Transform _ejectionPoint;

        [Title("Ejection")]
        [SerializeField, Range(0f, 10f)]
        private float _spawnDelay;

        [SerializeField, Range(0, 100f)]
        private float _spawnSpeed = 2f;

        [SerializeField, Range(0, 100f)]
        private float _spawnSpin = 10f;

        [SerializeField, Range(0.01f, 10f)]
        private float _spawnScale = 1f;

        [SerializeField]
        private Vector3 _spawnRotation;

        [SerializeField]
        private AudioSequence _ejectAudio;

        private const float InheritedSpeed = 0.85f;
        private const float SpeedRandomness = 0.5f;
        private const float ResetScaleDuration = 0.3f;
        private const float ResetScaleDelay = 0.3f;

        public override void Eject()
        {
            base.Eject();

            Wieldable.Audio.PlayClips(_ejectAudio, BodyPoint.Hands);

            if (_spawnDelay > 0.01f || !Wieldable.IsGeometryVisible || Mathf.Abs(_spawnScale - 1f) > 0.01f)
            {
                StartCoroutine(EjectShellDelayed());
            }
            else
            {
                EjectShell();
            }
        }

        public override void ResetShells() { }

        private IEnumerator EjectShellDelayed()
        {
            yield return new WaitForTime(_spawnDelay);

            var shell = EjectShell();
            if (Mathf.Abs(_spawnScale - 1f) > 0.01f || Wieldable.IsGeometryVisible)
                yield return InterpolateShellScale(shell);
        }

        private Transform EjectShell()
        {
            Quaternion rotation = Quaternion.Euler(Quaternion.LookRotation(_ejectionPoint.forward) * _spawnRotation);
            Quaternion randomRotation = Random.rotation;
            Vector3 position = _ejectionPoint.position;

            var shell = PoolManager.Instance.Get(_shellPrefab, position, Quaternion.Lerp(rotation, randomRotation, 0.1f));

            Vector3 velocityJitter = new(Random.Range(-SpeedRandomness, SpeedRandomness),
                Random.Range(-SpeedRandomness, SpeedRandomness),
                Random.Range(-SpeedRandomness, SpeedRandomness));

            var inheritedVelocity = Wieldable.Character.GetCC<IMotorCC>().Velocity * InheritedSpeed;
            var randomizedVelocity = _ejectionPoint.TransformVector(Vector3.forward * _spawnSpeed + velocityJitter);

            float spinDirection = Random.Range(0, 2) == 0 ? 1f : -1f;
            float spinAmount = Random.Range(0.5f, 1f) * _spawnSpin;

            shell.position = position;
            shell.linearVelocity = inheritedVelocity + randomizedVelocity;
            shell.angularVelocity = spinAmount * spinDirection * Vector3.one;

            return shell.transform;
        }

        private IEnumerator InterpolateShellScale(Transform shell)
        {
            shell.localScale = !Wieldable.IsGeometryVisible ? Vector3.zero : Vector3.one * _spawnScale;    
            yield return new WaitForTime(ResetScaleDelay);
            shell.localScale = Vector3.one;

            // float t = 0f;
            // while (t < 1f)
            // {
            //     shell.localScale = Vector3.Lerp(startScale, Vector3.one, t);
            //     t += Time.deltaTime * (1 / ResetScaleDuration);
            //
            //     yield return null;
            // }
        }

        protected override void Awake()
        {
            base.Awake();

            if (_shellPrefab == null)
            {
                Debug.LogError($"Prefab on {gameObject.name} can't be null.");
                return;
            }

            _shellPrefab.maxAngularVelocity = 10000f;
            if (!PoolManager.Instance.HasPool(_shellPrefab))
                PoolManager.Instance.RegisterPool(_shellPrefab, new SceneObjectPool<Rigidbody>(_shellPrefab, gameObject.scene, PoolCategory.Shells, 2, 16));
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            _spawnDelay = Mathf.Clamp(_spawnDelay, 0f, EjectionDuration);
        }
#endif
    }
}