using PolymindGames.WorldManagement;
using UnityEngine;

namespace PolymindGames.BuildingSystem
{
    [RequireComponent(typeof(IInteractable))]
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/interaction/interactable/demo-interactables")]
    public sealed class SleepingBag : MonoBehaviour, ISleepingSpot
    {
        [SerializeField]
        [Help("Position offset for the player sleeping handler. (Where the camera will be positioned).", UnityMessageType.None)]
        private Vector3 _sleepPositionOffset = new(0.6f, 0.6f, 0);

        [SerializeField]
        [Help("Rotation offset for the player sleeping handler. (In which direction will the camera be pointed).", UnityMessageType.None)]
        private Vector3 _sleepRotationOffset = new(45, 0, 0);

        public Vector3 SleepPosition => transform.position + transform.TransformVector(_sleepPositionOffset);
        public Vector3 SleepOrientation => (Quaternion.LookRotation(transform.up, transform.right) * Quaternion.Euler(_sleepRotationOffset)).eulerAngles;

        private void Start() => GetComponent<IInteractable>().Interacted += TrySleep;

        private void TrySleep(IInteractable interactable, ICharacter character)
        {
            if (character.TryGetCC(out ISleepControllerCC sleepHandler))
                sleepHandler.TrySleep(this);
        }

        private void Reset() => gameObject.AddComponent<Interactable>();

        #region Editor
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            var prevColor = Gizmos.color;
            Gizmos.color = Color.green;

            Gizmos.DrawSphere(SleepPosition, 0.1f);

            Gizmos.color = prevColor;
        }
#endif
        #endregion
    }
}