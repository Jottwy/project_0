using PolymindGames.UserInterface;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames
{
    [RequireComponent(typeof(UIPanel))]
    public sealed class UIPanelEvents : MonoBehaviour
    {
        [SerializeField]
        private UnityEvent _showEvent;
        
        [SerializeField]
        private UnityEvent _hideEvent;

        private void Start()
        {
            var panel = GetComponent<UIPanel>();
            panel.PanelStateChanged += OnPanelStateChanged;
        }

        private void OnPanelStateChanged(bool enable)
        {
            if (enable)
                _showEvent.Invoke();
            else
                _hideEvent.Invoke();
        }

#if UNITY_EDITOR
        private void Reset()
        {
            gameObject.GetOrAddComponent<UIPanel>();
        }
#endif
    }
}
