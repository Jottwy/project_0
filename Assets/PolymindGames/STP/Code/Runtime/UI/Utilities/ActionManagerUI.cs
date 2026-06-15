using PolymindGames.InputSystem;
using System.Collections;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine;
using TMPro;

namespace PolymindGames.UserInterface
{
    public readonly struct CustomActionArgs
    {
        public readonly float EndTime;
        public readonly string Description;
        public readonly bool CanCancel;
        public readonly UnityAction CompletedCallback;
        public readonly UnityAction CancelledCallback;
        private readonly float _startTime;

        public CustomActionArgs(string description, float duration, bool canCancel, UnityAction completedCallback, UnityAction cancelledCallback)
        {
            _startTime = Time.time;
            EndTime = Time.time + duration;
            Description = description;
            CanCancel = canCancel;
            CompletedCallback = completedCallback;
            CancelledCallback = cancelledCallback;
        }

        public readonly bool IsComplete() => Time.time >= EndTime;
        public readonly float GetProgress() => 1f - (EndTime - Time.time) / (EndTime - _startTime);
    }

    [DefaultExecutionOrder(ExecutionOrderConstants.MonoSingleton)]
    public sealed class ActionManagerUI : MonoBehaviour
    {
        [SerializeField]
        private InputContext _actionContext;

        [SerializeField]
        private CanvasGroup _canvasGroup;

        [SerializeField]
        private Image _fillImg;

        [SerializeField]
        private TextMeshProUGUI _loadTxt;

        private CustomActionArgs _customAction;

        public static ActionManagerUI Instance { get; private set; }

        public void StartAction(in CustomActionArgs customActionArgs)
        {
            InputManager.Instance.PushContext(_actionContext);
            InputManager.Instance.PushEscapeCallback(StopAction);

            _customAction = customActionArgs;
            _canvasGroup.blocksRaycasts = true;
            _canvasGroup.alpha = 1f;
            _loadTxt.text = _customAction.Description;

            StartCoroutine(HandleAction());
        }

        public bool CancelCurrentAction()
        {
            if (!_customAction.CanCancel)
                return false;

            StopAction();
            return true;
        }
        
        private void Awake()
        {
            _canvasGroup.alpha = 0f;
            
            // Ensure only one instance of CustomActionManager exists
            if (Instance == null)
                Instance = this;
        }
        
        private void OnDestroy()
        {
            // Clear singleton instance when destroyed
            if (Instance == this)
                Instance = null;
        }

        private void StopAction()
        {
            StopAllCoroutines();
            
            InputManager.Instance.PopEscapeCallback(StopAction);
            InputManager.Instance.PopContext(_actionContext);

            (_customAction.IsComplete() ? _customAction.CompletedCallback : _customAction.CancelledCallback)?.Invoke();

            _fillImg.fillAmount = 0f;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.alpha = 0f;
        }

        private IEnumerator HandleAction()
        {
            while (!_customAction.IsComplete())
            {
                _fillImg.fillAmount = _customAction.GetProgress();
                yield return null;
            }
            
            StopAction();
        }
    }
}