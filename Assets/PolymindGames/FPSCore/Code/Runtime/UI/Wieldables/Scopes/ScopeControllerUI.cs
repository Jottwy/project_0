using PolymindGames.WieldableSystem;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/user-interface/behaviours/ui_wieldables#scope")]
    public sealed class ScopeControllerUI : CharacterUIBehaviour
    {
        [SerializeField, Range(0f, 5f)]
        private float _showDuration = 0.2f;

        [SerializeField, Range(0f, 5f)]
        private float _hideDuration = 0.05f;

        [SerializeField, Title("Scopes")]
        [ReorderableList(ListStyle.Lined, HasLabels = false, HasHeader = false)]
        [Tooltip("All of the existing UI scopes.")]
        private ScopeUI[] _scopes;

        private IScopeHandler _scopeHandler;
        private ScopeUI _activeScope;
        private IFirearm _firearm;

        protected override void OnCharacterAttached(ICharacter character)
        {
            var wieldables = character.GetCC<IWieldablesControllerCC>();
            wieldables.EquippingStarted += OnEquipStart;
            wieldables.HolsteringStarted += OnHolsterStart;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            var wieldables = character.GetCC<IWieldablesControllerCC>();
            wieldables.EquippingStarted -= OnEquipStart;
            wieldables.HolsteringStarted -= OnHolsterStart;
        }
        
        protected override void Awake()
        {
            base.Awake();
            foreach (var scope in _scopes)
                scope.Hide(0f);
        }

        private void OnEquipStart(IWieldable wieldable)
        {
            if (wieldable is IFirearm firearm)
            {
                _firearm = firearm;
                _firearm.AddChangedListener(FirearmComponentType.AimHandler, OnSightChanged);
                OnSightChanged();
            }
        }
        
        private void OnHolsterStart(IWieldable wieldable)
        {
            if (_firearm != null)
            {
                _firearm.RemoveChangedListener(FirearmComponentType.AimHandler, OnSightChanged);
                _firearm = null;
                OnSightChanged();
            }
        }
        
        private void OnSightChanged()
        {
            UnsubscribeFromScopeHandlerEvents();

            if (_firearm != null && _firearm.AimHandler is IScopeHandler scopeHandler)
            {
                SubscribeToScopeHandlerEvents(scopeHandler);
                HandleScopeEnabled(scopeHandler.IsScopeEnabled);
            }
            else
                SetScope(-1);
        }

        private void SubscribeToScopeHandlerEvents(IScopeHandler handler)
        {
            _scopeHandler = handler;
            _scopeHandler.ScopeEnabled += HandleScopeEnabled;
        }

        private void UnsubscribeFromScopeHandlerEvents()
        {
            if (_scopeHandler != null)
            {
                _scopeHandler.ScopeEnabled -= HandleScopeEnabled;
                _scopeHandler = null;
            }
        }
        
        private void HandleScopeEnabled(bool isEnabled) => SetScope(isEnabled ? _scopeHandler.ScopeIndex : -1);

        private void SetScope(int scopeIndex)
        {
            if (_activeScope != null)
                _activeScope.Hide(_hideDuration);

            scopeIndex = Mathf.Clamp(scopeIndex, -1, _scopes.Length - 1);
            
            if (scopeIndex != -1)
            {
                _activeScope = _scopes[scopeIndex];
                _activeScope.Show(_showDuration);
            }
            else
                _activeScope = null;
        }
    }
}