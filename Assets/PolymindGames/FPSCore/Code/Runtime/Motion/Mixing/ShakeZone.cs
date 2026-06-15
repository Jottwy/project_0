using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    [RequireComponent(typeof(SphereCollider))]
    public sealed class ShakeZone : MonoBehaviour
    {
        // private enum PlayType
        // {
        //     OneShot,
        //     Continuous
        // }
        //
        // [SerializeField]
        // private bool _playOnAwake = false;
        //
        // [SerializeField, ShowIf(nameof(_playOnAwake), true)]
        // private PlayType _playType;

        // [SerializeField, IgnoreParent]
        // private ShakeData _shakeData;
        //
        // private readonly List<IShakeHandler> _shakeHandlers = new();
        // private Poolable _parentPoolable;
        // private SphereCollider _collider;
        // private float _zoneRadius;
        // private bool _isPlaying;

        // public static void PlayAtPosition(ShakeData shakeData, Vector3 position, float radius)
        // {
        //     var shakeZone = GetShakeZone(position, shakeData.Duration);
        //     shakeZone._shakeData = shakeData;
        //     shakeZone.Play(radius);
        // }

        // public void Play(float radius)
        // {
        //     _collider.radius = radius;
        //     _zoneRadius = radius;
        //     Play();
        // }
        //
        // public void Play()
        // {
        //     if (_isPlaying)
        //         return;
        //
        //     // _isPlaying = true;
        //     PlayOneShot();
        // }

        // public void Stop()
        // {
        //     // if (!_isPlaying)
        //     //     return;
        // }
        
        public static void PlayOneShotAtPosition(ShakeData shakeData, Vector3 position, float radius, float multiplier = 1f)
        {
            int count = PhysicsUtility.OverlapSphereOptimized(position, radius, out var colliders, LayerConstants.CharacterMask);

            for (int i = 0; i < count; i++)
            {
                var col = colliders[i];
                if (col.TryGetComponent(out IFPSCharacter character))
                {
                    PlayShake(character.HeadComponents.Shake, shakeData, position, radius, multiplier);
                    PlayShake(character.HandsComponents.Shake, shakeData, position, radius, multiplier);
                }
                else if (col.TryGetComponent(out IShakeHandler shakeHandler))
                    PlayShake(shakeHandler, shakeData, position, radius, multiplier);
            }
        }

        private static void PlayShake(IShakeHandler handler, ShakeData shakeData, Vector3 position, float radius, float multiplier = 1f)
        {
            float distToImpact = (handler.transform.position - position).magnitude;
            if (radius - distToImpact > 0f)
            {
                float distanceFactor = Easer.Apply(EaseType.ExpoOut, 1f - Mathf.Clamp01(distToImpact / radius));
                handler.AddShake(shakeData, distanceFactor * multiplier);
            }
        }

        // private void OnTriggerEnter(Collider other)
        // {
        //     if (other.TryGetComponent(out IFPSCharacter character))
        //     {
        //         Debug.Log("ENTER");
        //
        //         _shakeHandlers.Add(character.HandsComponents.Shake);
        //         _shakeHandlers.Add(character.HeadComponents.Shake);
        //     }
        //     else if (other.TryGetComponent(out IShakeHandler shakeHandler))
        //         _shakeHandlers.Add(shakeHandler);
        // }
        //
        // private void OnTriggerExit(Collider other)
        // {
        //     if (other.TryGetComponent(out IFPSCharacter character))
        //     {
        //         _shakeHandlers.Remove(character.HandsComponents.Shake);
        //         _shakeHandlers.Remove(character.HeadComponents.Shake);
        //     }
        //     else if (other.TryGetComponent(out IShakeHandler shakeHandler))
        //         _shakeHandlers.Remove(shakeHandler);
        // }
        //
        // private void Awake()
        // {
        //     _collider = GetComponent<SphereCollider>();
        //     _zoneRadius = _collider.radius;
        // }

        // #region Pooling
        // private static ShakeZone _zoneTemplate;
        // private const string ZonePoolCategory = "Shake Zone";
        //
        // private static ShakeZone GetShakeZone(Vector3 position, float duration)
        // {
        //     if (_zoneTemplate == null)
        //         _zoneTemplate = CreateTemplate();
        //
        //     var instance = ScenePool.Instance.CreateOrGetPool(_zoneTemplate, 4, 12, ZonePoolCategory, 10f).GetInstance();
        //     instance.DefaultReleaseDelay = duration;
        //
        //     var shakeZone = (ShakeZone)instance.CachedComponent;
        //     shakeZone.transform.position = position;
        //     return shakeZone;
        // }
        //
        // private static ShakeZone CreateTemplate()
        // {
        //     var gameObj = new GameObject("ShakeZone");
        //     gameObj.AddComponent<SphereCollider>();
        //     gameObj.AddComponent<Poolable>();
        //     return gameObj.AddComponent<ShakeZone>();
        // }
        // #endregion

//         #region Editor
// #if UNITY_EDITOR
//         private void Reset()
//         {
//             _collider ??= GetComponent<SphereCollider>();
//             _collider.isTrigger = true;
//         }
// #endif
//         #endregion
    }
}