using UnityEngine;

namespace PolymindGames.Demo
{
    public sealed class Billboard : MonoBehaviour
    {
        [SerializeField]
        private bool _syncXRotation;

        private void LateUpdate()
        {
            if (UnityUtility.CachedMainCamera == null)
                return;

            Quaternion rot = Quaternion.LookRotation(transform.position - UnityUtility.CachedMainCamera.transform.position);

            if (!_syncXRotation)
                rot = Quaternion.Euler(0f, rot.eulerAngles.y, 0f);

            transform.rotation = rot;
        }
    }
}