using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu(AddMenuPath + "Scoped Aim-Handler")]
    public class FirearmScopedAimHandler : FirearmIronSightAimHandler, IScopeHandler
    {
        [SerializeField, Range(0f, 10f), Title("Scope")]
        private float _scopeEnableDelay = 0.3f;

        [SerializeField, Range(-1, 24)]
        private int _scopeIndex = 0;

        // [SerializeField, Range(0, 8)]
        // private int _maxZoomLevel;

        private Coroutine _aimCoroutine;

        public int ZoomLevel { get => 0; set { } }
        public bool IsScopeEnabled => _aimCoroutine != null;
        public int ScopeIndex => _scopeIndex;
        public int MaxZoomLevel => 0;

        public event UnityAction<bool> ScopeEnabled;

        public override bool StartAiming()
        {
            if (!base.StartAiming())
                return false;

            _aimCoroutine = CoroutineUtility.InvokeDelayed(this, EnableScope, _scopeEnableDelay);
            return true;
        }

        public override bool StopAiming()
        {
            if (!base.StopAiming())
                return false;

            CoroutineUtility.StopCoroutine(this, ref _aimCoroutine);
            DisableScope();
            return true;
        }

        protected override FieldOfViewParams GetFieldOfViewParameters(bool enable)
        {
            return !enable ?
                new FieldOfViewParams(0f, 0f, 1f, 0f, 1f)
                : base.GetFieldOfViewParameters(true);
        }
        
        private void EnableScope()
        {
            Wieldable.IsGeometryVisible = false;
            ScopeEnabled?.Invoke(true);
        }
        
        private void DisableScope()
        {
            Wieldable.IsGeometryVisible = true;
            ScopeEnabled?.Invoke(false);
        }
    }
}