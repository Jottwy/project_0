using BackroomsSurvival.Net;
using BackroomsSurvival.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace BackroomsSurvival.Gameplay
{
    public sealed class WorldInteractor : MonoBehaviour
    {
        [Min(0.5f)] public float interactDistance = 5f;

        private long _nextRequestId = 1;

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.eKey.wasPressedThisFrame)
                return;

            if (ShouldIgnoreInput())
                return;

            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            var cam = Camera.main;
            if (cam == null)
                return;

            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (!Physics.Raycast(ray, out var hit, interactDistance, ~0, QueryTriggerInteraction.Collide))
                return;

            var target = hit.collider.GetComponentInParent<NetworkWorldObject>();
            if (target == null || !target.active)
                return;

            long requestId = MakeRequestId();
            int selfId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
            Debug.Log($"MPTRACE step=AI event=unity_interact_attempt self_id={selfId} target_id={target.id} kind={target.kind}");

            ipc.SendWorldInteractRequest(requestId, target.id, target.kind, "pickup", cam.transform.position);

            Debug.Log($"MPTRACE step=AJ event=unity_interact_sent self_id={selfId} target_id={target.id} request_id={requestId}");
        }

        private long MakeRequestId()
        {
            int selfId = NetworkInitializer.Instance != null ? NetworkInitializer.Instance.LastSelectedNetId : 0;
            long local = _nextRequestId++;
            return ((long)Mathf.Max(1, selfId) * 1000000000L) + local;
        }

        private static bool ShouldIgnoreInput()
        {
            if (JoinSessionUI.IsAnyMenuVisible)
                return true;

            if (Cursor.lockState != CursorLockMode.Locked)
                return true;

            if (JoinSessionUI.IsUserEditingInput())
                return true;

            return EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null;
        }
    }
}
