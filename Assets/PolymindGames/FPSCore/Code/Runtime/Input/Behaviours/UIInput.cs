using UnityEngine.EventSystems;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/UI Input")]
    public sealed class UIInput : InputBehaviour
    {
        [SerializeField, NotNull, Title("Actions")]
        private BaseInputModule _inputModule;

        private void OnEnable()
        {
            _inputModule.enabled = true;
            UnityUtility.UnlockCursor();
        }

        private void OnDisable()
        {
            if (UnityUtility.IsQuitting)
                return;

            _inputModule.enabled = false;
            UnityUtility.LockCursor();
        }
    }
}
